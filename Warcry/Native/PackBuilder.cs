using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using Warcry.Audio;
using Warcry.Clips;
using Warcry.Game;
using Warcry.Profiles;

namespace Warcry.Native;

// Owns the sound-pack lifecycle: everything mapped is compiled and registered ahead of time,
// and the clips reachable from the job being played are kept warm. All of that cost lands at
// Apply time, so by the time a cast needs a clip its container is on disk, its redirect is
// registered, and — if an active job can reach it — its resource is warm. Encoding on first
// request instead would make every clip's first play fall back to NAudio.
//
// Compile everything, warm per job. Encoding is disk and a few milliseconds, so it happens
// for every mapping. Warming is different: a warmed resource handle is client memory that
// cannot be evicted until the game exits, so only the clips an active job can trigger are
// warmed, plus job-agnostic ones. Already-warm clips stay warm, so resident memory is
// bounded by the jobs actually played this session.
//
// The active set is yours plus the jobs of the audible players around you, from
// Gating.CrowdWatch — a bounded, earned set, never "every job in the game". A stranger who
// walks up is warm within a second or two of arriving, and while the audience is self-only
// the set is exactly one job.
//
// The plan must enumerate exactly the keys Plugin.OnCast will ask for. Both build keys from
// the same primitives (Plugin.VariantKey, CachedClip.QuantiseRate), so a drift between them
// is a compile error or a shared bug, never two opinions.
public sealed class PackBuilder
{
    // Per frame, so a job switch does not burst the sound pool.
    private const int WarmPerFrame = 4;

    // How long profile edits settle before an automatic re-apply.
    private const double ApplyDebounceSeconds = 2.0;

    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ProfileStore profiles;
    private readonly ClipLibrary clips;
    private readonly ScdForge forge;
    private readonly JobIndex jobs;
    private readonly NativeVoiceSink native;

    // Keyed by variant key. Main thread only.
    private readonly Dictionary<string, PlannedVariant> plan = [];

    private readonly List<string> errors = [];
    private readonly Queue<ForgedClip> warmQueue = new();

    // Yours plus the audience's.
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

    // One base rendering the pipeline can request, and who can reach it.
    public sealed class PlannedVariant
    {
        public required string VariantKey { get; init; }

        public required string ClipHash { get; init; }

        public required string Label { get; init; }

        // Empty means job-agnostic: category, cast-time or wildcard rules.
        public required List<uint> ActionIds { get; init; }

        // Why this variant cannot compile, or empty.
        public string Error { get; set; } = string.Empty;
    }

    // Your own job, as of the last Update. 0 off-world.
    public uint CurrentJobId { get; private set; }

    // Never empty in game.
    public IReadOnlySet<uint> ActiveJobs => this.activeJobs;

    public int PlannedCount => this.plan.Count;

    // Variants whose container exists and whose redirect is registered.
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

    // Planned variants both reachable from an active job and warmed.
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

    // Planned variants reachable from an active job, warm or not.
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

    // Rebuilds the plan and starts every missing encode. Idempotent: content addressing
    // makes an unchanged mapping a dictionary lookup, not a re-encode.
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
                // Terminal: an earlier encode gave up on it. Naming the reason here stops it
                // reading as "not compiled — press Apply" forever.
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

    // Per frame, on the game main thread, before the sink pumps: detects mapping edits
    // (debounced re-apply) and changes to the reachable job set (warm what is newly
    // reachable). jobsRevision is bumped by the crowd scan only when jobs actually changed,
    // so an unchanged crowd costs one integer compare rather than a set comparison.
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

    // The sink's warm gate: whether a just-registered clip should warm immediately.
    // Unplanned variants — auditions, the test tone — warm unconditionally; planned ones
    // warm only when an active job can reach them, and otherwise wait for the job set to
    // change.
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

    // Job-agnostic variants are always reachable. Job-specific ones need at least one active
    // job with one of the rule's actions in its kit, so with no active job — off-world, or
    // nobody audible nearby — nothing job-specific warms, because nobody can cast it.
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

    // Walks every enabled profile, rule and clip and lists the exact variant keys the cast
    // pipeline can request. Mirrors the key logic in Plugin.OnCast.
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
                            // the same pitch, once by action id and once by category — so the
                            // action sets must be merged. Keeping only the first rule's set
                            // leaves the variant looking unreachable from any job the second
                            // rule covers, and it never warms.
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

    // The plan-side mirror of the pitch logic in Plugin.OnCast and ClipResolver.RollRate.
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

        // Every half-semitone step the quantised roll can land on. The edges round outward,
        // which can add one never-rolled variant per side: two spare encodes beat one
        // cast-time cache miss.
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

    // Reads (rate, mode, fft) back out of a variant key. The format is owned by
    // Plugin.VariantKey: {hash}:{rate:0.0000}:{(byte)mode}:{fft}, invariant culture, exactly
    // as it was written.
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
