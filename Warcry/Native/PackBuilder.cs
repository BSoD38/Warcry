using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using Warcry.Audio;
using Warcry.Clips;
using Warcry.Game;
using Warcry.Profiles;

namespace Warcry.Native;

/// <summary>
/// Owns the sound-pack lifecycle: everything mapped is compiled and registered ahead of
/// time, and the clips reachable from the job being played are kept warm.
/// </summary>
/// <remarks>
/// <para>This exists because the native path is only honest when it can always serve. The
/// forge used to improvise — encode on first request, warm on registration — which made
/// the first plays of every clip fall back to NAudio and made "native mode" mean "native,
/// eventually, sometimes". The builder moves all of that cost to Apply time: by the time a
/// cast needs a clip, its container is on disk, its redirect is registered, and — if the
/// current job can reach it — its resource is warm.</para>
/// <para><b>Compile everything, warm per job.</b> Encoding is disk and a few milliseconds,
/// so all of it happens for every mapping. Warming is different: a warmed resource handle
/// is client memory that cannot be evicted until the game exits, so only the clips the
/// current job can actually trigger are warmed, plus job-agnostic ones. Switching jobs
/// warms the new set; already-warm clips stay warm — resident memory is bounded by the
/// jobs actually played this session, which is the best achievable.</para>
/// <para>With remote casters admitted, "reachable" stops meaning one job. The active set is
/// yours plus the jobs of the audible players around you, from <c>Gating.CrowdWatch</c> —
/// still a bounded, earned set rather than "every job in the game". A stranger who walks up
/// is warm within a second or two of arriving rather than on their first cast, and while the
/// audience is self-only the set is exactly the one job it always was.</para>
/// <para>The variant plan must enumerate exactly the keys <c>Plugin.OnCast</c> will ask
/// for. Both build keys from the same primitives (<see cref="Plugin.VariantKey"/>,
/// <see cref="CachedClip.QuantiseRate"/>), so a drift between them is a compile error or
/// a shared bug, never two opinions.</para>
/// </remarks>
public sealed class PackBuilder
{
    /// <summary>Warm-ups issued per frame, so a job switch does not burst the sound pool.</summary>
    private const int WarmPerFrame = 4;

    /// <summary>How long profile edits are allowed to settle before an automatic re-apply.</summary>
    private const double ApplyDebounceSeconds = 2.0;

    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ProfileStore profiles;
    private readonly ClipLibrary clips;
    private readonly ScdForge forge;
    private readonly JobIndex jobs;
    private readonly NativeVoiceSink native;

    /// <summary>The plan, keyed by variant key. Main thread only.</summary>
    private readonly Dictionary<string, PlannedVariant> plan = [];

    private readonly List<string> errors = [];
    private readonly Queue<ForgedClip> warmQueue = new();

    /// <summary>Jobs that can currently trigger a mapping — yours plus the audience's.</summary>
    private IReadOnlySet<uint> activeJobs = new HashSet<uint>();

    private string fingerprint = string.Empty;
    private long fingerprintChangedAt;
    private int lastJobsRevision = int.MinValue;

    public PackBuilder(
        IPluginLog log,
        Configuration config,
        ProfileStore profiles,
        ClipLibrary clips,
        ScdForge forge,
        JobIndex jobs,
        NativeVoiceSink native)
    {
        this.log = log;
        this.config = config;
        this.profiles = profiles;
        this.clips = clips;
        this.forge = forge;
        this.jobs = jobs;
        this.native = native;

        // The sink pumps registrations; the builder decides which of them warm now.
        this.native.WarmGate = this.ShouldWarmNow;
    }

    /// <summary>One base rendering the pipeline can request, and who can reach it.</summary>
    public sealed class PlannedVariant
    {
        public required string VariantKey { get; init; }

        public required string ClipHash { get; init; }

        public required string Label { get; init; }

        /// <summary>Empty means job-agnostic: category, cast-time or wildcard rules.</summary>
        public required List<uint> ActionIds { get; init; }

        /// <summary>Why this variant cannot compile, or empty.</summary>
        public string Error { get; set; } = string.Empty;
    }

    /// <summary>Your own job, as of the last <see cref="Update"/>. 0 off-world.</summary>
    public uint CurrentJobId { get; private set; }

    /// <summary>Every job a mapping could currently be triggered from. Never empty in game.</summary>
    public IReadOnlySet<uint> ActiveJobs => this.activeJobs;

    public int PlannedCount => this.plan.Count;

    /// <summary>Variants whose container exists and whose redirect is registered.</summary>
    public int CompiledCount
    {
        get
        {
            var compiled = 0;
            foreach (var entry in this.plan.Keys)
            {
                if (this.forge.TryGetForged(entry, out _))
                {
                    compiled++;
                }
            }

            return compiled;
        }
    }

    /// <summary>Planned variants both reachable from an active job and warmed.</summary>
    public int WarmForActiveJobs
    {
        get
        {
            var warm = 0;
            foreach (var (key, entry) in this.plan)
            {
                if (this.IsReachable(entry) &&
                    this.forge.TryGetForged(key, out var clip) && clip is { WarmedAt: not 0 })
                {
                    warm++;
                }
            }

            return warm;
        }
    }

    /// <summary>Planned variants reachable from an active job, warm or not.</summary>
    public int ReachableForActiveJobs
    {
        get
        {
            var reachable = 0;
            foreach (var entry in this.plan.Values)
            {
                if (this.IsReachable(entry))
                {
                    reachable++;
                }
            }

            return reachable;
        }
    }

    public IReadOnlyList<string> Errors => this.errors;

    public IReadOnlyDictionary<string, PlannedVariant> Plan => this.plan;

    public long LastApplyAt { get; private set; }

    public string Status { get; private set; } = "not applied yet";

    /// <summary>
    /// Rebuilds the plan and starts every missing encode. Idempotent: content addressing
    /// makes an unchanged mapping a dictionary lookup, not a re-encode.
    /// </summary>
    public void Apply()
    {
        this.BuildPlan();

        var started = 0;
        var ready = 0;
        var missing = 0;

        foreach (var (key, entry) in this.plan)
        {
            if (entry.Error.Length > 0)
            {
                missing++;
                continue;
            }

            if (this.forge.TryGetForged(key, out _))
            {
                ready++;
                continue;
            }

            // The test tone synthesises itself and has no library entry; everything else
            // needs a decoded clip. Keyed off the variant, not off an empty hash, so a
            // malformed mapping cannot slip past into a null dereference downstream.
            var synthetic = entry.VariantKey == Plugin.TestToneKey;
            if (!this.clips.TryGet(entry.ClipHash, out var cached) && !synthetic)
            {
                entry.Error = "clip not loaded (still decoding, or missing from the library)";
                missing++;
                continue;
            }

            var factory = this.FactoryFor(entry, cached);
            if (this.forge.TryForge(key, factory, out _))
            {
                ready++;
            }
            else if (this.forge.IsInFlight(key))
            {
                started++;
            }
            else if (this.forge.TryGetFailure(key, out var why))
            {
                // Terminal: an earlier encode gave up on it. Naming the reason here is
                // what stops it reading as "not compiled — press Apply" forever.
                entry.Error = why;
                missing++;
            }
            else
            {
                // TryForge refused without queueing — the cap, or the forge is not ready.
                entry.Error = this.forge.Status;
                missing++;
            }
        }

        this.LastApplyAt = Stopwatch.GetTimestamp();
        this.Status = missing > 0
            ? $"{ready} ready, {started} being prepared, {missing} blocked. See the list below"
            : started > 0
                ? $"{ready} ready, {started} encoding"
                : $"{ready} ready";

        this.log.Information(
            "PackBuilder: applied — {Planned} planned, {Ready} ready, {Started} encoding, {Missing} blocked",
            this.plan.Count,
            ready,
            started,
            missing);
    }

    /// <summary>
    /// Per frame, on the game main thread, before the sink pumps. Detects mapping edits
    /// (debounced re-apply) and changes to the reachable job set (warm what is newly
    /// reachable).
    /// </summary>
    /// <param name="localJobId">Your own job, for labels. 0 off-world.</param>
    /// <param name="jobs">Every job that could trigger a mapping right now.</param>
    /// <param name="jobsRevision">
    /// Bumped by the crowd scan only when <paramref name="jobs"/> actually changed, so an
    /// unchanged crowd costs one integer compare per frame rather than a set comparison.
    /// </param>
    public void Update(uint localJobId, IReadOnlySet<uint> jobs, int jobsRevision)
    {
        this.CurrentJobId = localJobId;
        this.activeJobs = jobs;

        // Automatic apply only when a native mode can use the result. The button on the
        // Sound pack tab works in any mode, for preparing a pack before switching.
        if (this.config.Sink is SinkMode.NativeOnly or SinkMode.Auto)
        {
            var current = this.Fingerprint();
            if (current != this.fingerprint)
            {
                if (this.fingerprintChangedAt == 0)
                {
                    this.fingerprintChangedAt = Stopwatch.GetTimestamp();
                }
                else if (Stopwatch.GetTimestamp() - this.fingerprintChangedAt
                         > (long)(ApplyDebounceSeconds * Stopwatch.Frequency))
                {
                    this.fingerprint = current;
                    this.fingerprintChangedAt = 0;
                    this.Apply();
                }
            }
            else
            {
                this.fingerprintChangedAt = 0;
            }
        }

        if (jobsRevision != this.lastJobsRevision)
        {
            this.lastJobsRevision = jobsRevision;
            this.QueueWarmables();
        }

        // Paced: each warm-up is a real (muted) PlaySound and briefly holds a pool slot.
        var budget = WarmPerFrame;
        while (budget-- > 0 && this.warmQueue.TryDequeue(out var clip))
        {
            if (clip.WarmedAt == 0)
            {
                this.native.Warm(clip);
            }
        }
    }

    /// <summary>
    /// The sink's warm gate: whether a just-registered clip should warm immediately.
    /// </summary>
    /// <remarks>
    /// Unplanned variants — auditions, the test tone, anything from before the builder —
    /// warm unconditionally, which is the pre-builder behaviour. Planned ones warm only
    /// when an active job can reach them; the rest wait for the job set to change.
    /// </remarks>
    public bool ShouldWarmNow(ForgedClip clip)
    {
        if (!this.PlanEntryFor(clip, out var entry))
        {
            return true;
        }

        return this.IsReachable(entry);
    }

    private bool PlanEntryFor(ForgedClip clip, out PlannedVariant entry)
        => this.plan.TryGetValue(clip.VariantKey, out entry!);

    /// <summary>
    /// Job-agnostic variants are always reachable. Job-specific ones need at least one
    /// active job with one of the rule's actions in its kit — with no active job (off-world,
    /// nobody audible nearby) nothing job-specific warms, because there is nobody to cast it.
    /// </summary>
    private bool IsReachable(PlannedVariant entry)
    {
        if (entry.ActionIds.Count == 0)
        {
            return true;
        }

        foreach (var jobId in this.activeJobs)
        {
            if (jobId == 0)
            {
                continue;
            }

            foreach (var actionId in entry.ActionIds)
            {
                if (this.jobs.ActionBelongsToJob(actionId, jobId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void QueueWarmables()
    {
        this.warmQueue.Clear();

        foreach (var clip in this.forge.UnwarmedClips())
        {
            if (!this.PlanEntryFor(clip, out var entry) || this.IsReachable(entry))
            {
                this.warmQueue.Enqueue(clip);
            }
        }

        if (this.warmQueue.Count > 0)
        {
            this.log.Information(
                "PackBuilder: warming {Count} clip(s) for {Jobs} active job(s)",
                this.warmQueue.Count,
                this.activeJobs.Count);
        }
    }

    /// <summary>
    /// Walks every enabled profile, rule and clip and lists the exact variant keys the
    /// cast pipeline can request. Mirrors the key logic in <c>Plugin.OnCast</c>.
    /// </summary>
    private void BuildPlan()
    {
        this.plan.Clear();
        this.errors.Clear();

        foreach (var profile in this.profiles.Profiles)
        {
            if (!profile.Enabled)
            {
                continue;
            }

            foreach (var rule in profile.Rules)
            {
                if (!rule.Enabled || rule.Clips.Count == 0)
                {
                    continue;
                }

                foreach (var clipRef in rule.Clips)
                {
                    foreach (var rate in BakedRatesFor(rule))
                    {
                        var key = Plugin.VariantKey(clipRef.Hash, rate, rule.PitchMode, rule.PitchFftSize);
                        if (this.plan.TryGetValue(key, out var already))
                        {
                            // Two rules can reach the identical rendering — the same clip at
                            // the same pitch, reached once by action id and once by category.
                            // Skipping outright kept only the FIRST rule's action set, so the
                            // variant looked unreachable from any job the second rule covers
                            // and never warmed: one dropped line per job switch, from the very
                            // component that exists to prevent that.
                            Widen(already, rule.When.ActionIds);
                            continue;
                        }

                        var entry = new PlannedVariant
                        {
                            VariantKey = key,
                            ClipHash = clipRef.Hash,
                            Label = rule.Label.Length > 0 ? rule.Label : key,
                            ActionIds = [.. rule.When.ActionIds],
                        };

                        if (!this.clips.TryGet(clipRef.Hash, out _))
                        {
                            entry.Error = "clip not loaded (still decoding, or missing from the library)";
                        }

                        this.plan[key] = entry;
                    }
                }
            }
        }

        // The unmapped-action fallback is a variant like any other; pre-warm it too.
        if (this.config.FallBackToTestTone)
        {
            this.plan[Plugin.TestToneKey] = new PlannedVariant
            {
                VariantKey = Plugin.TestToneKey,
                ClipHash = string.Empty,
                Label = "test tone (unmapped-action fallback)",
                ActionIds = [],
            };
        }

        foreach (var entry in this.plan.Values)
        {
            if (entry.Error.Length > 0)
            {
                this.errors.Add($"{entry.Label}: {entry.Error}");
            }
        }

        if (this.plan.Count > ScdForge.MaxVariants)
        {
            this.errors.Add(
                $"{this.plan.Count} variants planned but the forge holds at most {ScdForge.MaxVariants} — " +
                "reduce mappings or pitch spreads, or the overflow will never compile");
        }

        // Reachability is a union over every rule that can request the rendering. An empty
        // action set means job-agnostic and therefore always reachable, so it has to win
        // over any specific list rather than being merged into one.
        static void Widen(PlannedVariant entry, List<uint> actionIds)
        {
            if (entry.ActionIds.Count == 0)
            {
                return;
            }

            if (actionIds.Count == 0)
            {
                entry.ActionIds.Clear();
                return;
            }

            foreach (var id in actionIds)
            {
                if (!entry.ActionIds.Contains(id))
                {
                    entry.ActionIds.Add(id);
                }
            }
        }
    }

    /// <summary>
    /// The baked playback rates a rule can request — the plan-side mirror of the pitch
    /// logic in <c>Plugin.OnCast</c> and <c>ClipResolver.RollRate</c>.
    /// </summary>
    private static IEnumerable<float> BakedRatesFor(VoiceRule rule)
    {
        if (rule.PitchMode == PitchMode.Varispeed)
        {
            // The whole roll rides on the engine's speed argument; one base variant.
            yield return 1f;
            yield break;
        }

        var spread = rule.PitchRandomSemitones;
        if (spread <= 0.001f)
        {
            yield return CachedClip.QuantiseRate(CachedClip.SemitonesToRate(rule.PitchSemitones));
            yield break;
        }

        // Every half-semitone step the quantised roll can land on. The edges are rounded
        // outward, which can add one never-rolled variant per side — two spare encodes
        // beat one cast-time cache miss.
        const float step = 0.5f;
        var lo = (int)MathF.Floor((rule.PitchSemitones - spread) / step);
        var hi = (int)MathF.Ceiling((rule.PitchSemitones + spread) / step);

        for (var k = lo; k <= hi; k++)
        {
            yield return CachedClip.QuantiseRate(CachedClip.SemitonesToRate(k * step));
        }
    }

    private Func<NAudio.Wave.ISampleProvider> FactoryFor(PlannedVariant entry, CachedClip? cached)
    {
        if (entry.VariantKey == Plugin.TestToneKey)
        {
            return static () => new TestTone();
        }

        ArgumentNullException.ThrowIfNull(cached);

        // Reconstruct the baked parameters from the plan rather than the key string.
        // The key is derived from these same values, so they cannot disagree.
        var (rate, mode, fft) = KeyParameters(entry.VariantKey);
        return () => cached.CreateProvider(rate, mode, fft);
    }

    /// <summary>Reads (rate, mode, fft) back out of a variant key.</summary>
    /// <remarks>
    /// The key format is owned by <see cref="Plugin.VariantKey"/>:
    /// <c>{hash}:{rate:0.0000}:{(byte)mode}:{fft}</c>. Parsed with the invariant culture,
    /// exactly as it was written.
    /// </remarks>
    private static (float Rate, PitchMode Mode, int Fft) KeyParameters(string variantKey)
    {
        var parts = variantKey.Split(':');
        if (parts.Length != 4)
        {
            return (1f, PitchMode.Varispeed, 2048);
        }

        var rate = float.TryParse(
            parts[1],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var r)
            ? r
            : 1f;
        var mode = byte.TryParse(parts[2], out var m) ? (PitchMode)m : PitchMode.Varispeed;
        var fft = int.TryParse(parts[3], out var f) ? f : 2048;
        return (rate, mode, fft);
    }

    private string Fingerprint()
        => $"{this.profiles.Revision}:{this.clips.Count}:{this.config.FallBackToTestTone}";
}
