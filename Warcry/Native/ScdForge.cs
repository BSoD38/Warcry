using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin.Services;
using NAudio.Wave;
using Warcry.Clips;

namespace Warcry.Native;

/// <summary>One clip, encoded and reachable by the game's resource system.</summary>
public sealed class ForgedClip
{
    public required string VariantKey { get; init; }

    /// <summary>The game-relative path the engine must be asked for.</summary>
    public required string GamePath { get; init; }

    public required string LocalPath { get; init; }

    public required float Seconds { get; init; }

    /// <summary>Container size on disk — and in client memory once warmed.</summary>
    public required int Bytes { get; init; }

    /// <summary>
    /// When the engine was first asked for this path, or 0 if it has not been.
    /// </summary>
    /// <remarks>
    /// Resource loading is asynchronous: the first request for a path returns before the
    /// bytes are in memory and produces nothing audible. The warm-up is issued the moment
    /// the clip is registered rather than being charged to a real play, so by the time a
    /// line actually needs it — at least a throttle cooldown later — it is loaded.
    /// </remarks>
    public long WarmedAt { get; set; }
}

/// <summary>
/// Turns a decoded clip into an <c>.scd</c> the game's own engine will play, and keeps the
/// Penumbra redirects that make it reachable.
/// </summary>
/// <remarks>
/// <para>The route, proven end to end in <c>docs/native-spike.md</c>: clone a real
/// battle-voice container from the user's own install, encode the clip as mono MS-ADPCM
/// (the only format the engine both plays and we can write — a survey of the game's own
/// SCDs found 267 MS-ADPCM entries, 92 HCA and no PCM at all), append it past the end of
/// the container and point every audio index at it, then serve it from a content-addressed
/// synthetic path.</para>
/// <para><b>Nothing is shipped.</b> The container template is read from the player's own
/// game files at runtime, which also means it always matches their client version.</para>
/// </remarks>
public sealed class ScdForge
{
    /// <summary>
    /// Ceiling on distinct encoded variants held at once.
    /// </summary>
    /// <remarks>
    /// A resource handle is cached by path for the life of the game process and cannot be
    /// evicted from our side, so every variant forged is memory the client holds until it
    /// exits. A profile with a few dozen mappings sits far under this; the cap exists so a
    /// runaway cannot quietly consume the session.
    /// </remarks>
    public const int MaxVariants = 192;

    /// <summary>Refuse to encode anything longer than this. A voiceline is not a song.</summary>
    private const float MaxSeconds = 30f;

    private static readonly string[] TemplateCandidates =
    [
        "sound/voice/Vo_Battle/Vo_Battle_PC_ros_Ma_fr.scd",
        "sound/voice/Vo_Battle/Vo_Battle_PC_mid_Ma_fr.scd",
        "sound/voice/Vo_Battle/Vo_Battle_PC_mid_Ma_en.scd",
        "sound/voice/Vo_Battle/Vo_Battle_PC_mid_Ma_ja.scd",
        "sound/voice/Vo_Battle/Vo_Battle_PC_mid_Ma_de.scd",
    ];

    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly PenumbraBridge penumbra;
    private readonly string cacheDir;

    /// <summary>
    /// Guards <see cref="byVariant"/>, <see cref="inFlight"/> and <see cref="failed"/>
    /// across threads.
    /// </summary>
    private readonly object gate = new();

    private readonly Dictionary<string, ForgedClip> byVariant = [];
    private readonly HashSet<string> inFlight = [];

    /// <summary>
    /// Variants that cannot be encoded, and why. Guarded by <see cref="gate"/>.
    /// </summary>
    /// <remarks>
    /// A terminal failure has to be remembered. Without this a clip that decodes to nothing
    /// — or is too long to encode — left no trace in either <see cref="byVariant"/> or
    /// <see cref="inFlight"/>, so every later cast spawned another encode, drained the whole
    /// provider again, and logged the same warning forever. Cleared by <see cref="Clear"/>,
    /// so re-importing the clip is a real retry.
    /// </remarks>
    private readonly Dictionary<string, string> failed = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Encoded> completed = new();

    /// <summary>Main-thread only: Penumbra IPC and the redirect set are not shared.</summary>
    private readonly Dictionary<string, string> redirects = [];

    /// <summary>Encodes drained this pump, held until registration succeeds.</summary>
    private readonly List<Encoded> pendingRegistration = [];

    private ScdWriter.Template? template;

    /// <summary>
    /// Latched when no template candidate could be loaded, so the failure is answered from
    /// memory instead of re-reading five sqpack files on every call — Initialise is reached
    /// once per cast and once per frame from the Settings tab.
    /// </summary>
    private bool templateLoadFailed;

    /// <summary>Set on unload so in-flight encodes stop touching Dalamud services and disk.</summary>
    private volatile bool shutDown;

    private int lastStatusReady = -1;
    private int lastStatusPending = -1;

    /// <summary>An encode that finished off-thread, waiting to be registered.</summary>
    private sealed record Encoded(string VariantKey, string GamePath, string LocalPath, float Seconds, int Bytes);

    public ScdForge(IDataManager data, IPluginLog log, PenumbraBridge penumbra, string configDirectory)
    {
        this.data = data;
        this.log = log;
        this.penumbra = penumbra;
        this.cacheDir = Path.Combine(configDirectory, ".cache", "scd");

        try
        {
            Directory.CreateDirectory(this.cacheDir);

            // Orphaned temp files from writes interrupted by a crash or unload.
            foreach (var stale in Directory.EnumerateFiles(this.cacheDir, "*.tmp"))
            {
                try
                {
                    File.Delete(stale);
                }
                catch (Exception ex)
                {
                    this.log.Warning(ex, "ScdForge: could not remove the stale temp file {File}", stale);
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "ScdForge: could not create the cache directory");
        }
    }

    /// <summary>Plain-English reason the forge is or is not usable.</summary>
    public string Status { get; private set; } = "not initialised";

    public bool Ready { get; private set; }

    public int Count
    {
        get
        {
            lock (this.gate)
            {
                return this.byVariant.Count;
            }
        }
    }

    /// <summary>Which game file the container is cloned from, for the Status tab.</summary>
    public string TemplatePath { get; private set; } = string.Empty;

    /// <summary>Disk bytes across every registered container, and the warmed subset.</summary>
    /// <remarks>Warmed bytes approximate resident client memory: a warmed resource handle
    /// holds the whole container and cannot be evicted until the game exits.</remarks>
    public (long Total, long Warmed) ByteTotals()
    {
        lock (this.gate)
        {
            long total = 0, warmed = 0;
            foreach (var clip in this.byVariant.Values)
            {
                total += clip.Bytes;
                if (clip.WarmedAt != 0)
                {
                    warmed += clip.Bytes;
                }
            }

            return (total, warmed);
        }
    }

    /// <summary>A cache read with no side effects — never starts an encode.</summary>
    public bool TryGetForged(string variantKey, out ForgedClip? forged)
    {
        lock (this.gate)
        {
            return this.byVariant.TryGetValue(variantKey, out forged);
        }
    }

    /// <summary>Whether a variant is currently encoding in the background.</summary>
    public bool IsInFlight(string variantKey)
    {
        lock (this.gate)
        {
            return this.inFlight.Contains(variantKey);
        }
    }

    /// <summary>
    /// Why a variant will never encode, for a caller that needs to say so out loud.
    /// </summary>
    /// <remarks>A terminal answer: retrying it is what <see cref="Clear"/> is for.</remarks>
    public bool TryGetFailure(string variantKey, out string reason)
    {
        lock (this.gate)
        {
            return this.failed.TryGetValue(variantKey, out reason!);
        }
    }

    /// <summary>Records a terminal encode failure and logs it once.</summary>
    private void MarkFailed(string variantKey, string reason)
    {
        lock (this.gate)
        {
            if (!this.failed.TryAdd(variantKey, reason))
            {
                return;
            }
        }

        this.log.Warning("ScdForge: {Key} will not encode — {Reason}", variantKey, reason);
    }

    /// <summary>Registered clips the engine has never been asked for. Snapshot.</summary>
    public List<ForgedClip> UnwarmedClips()
    {
        lock (this.gate)
        {
            var cold = new List<ForgedClip>();
            foreach (var clip in this.byVariant.Values)
            {
                if (clip.WarmedAt == 0)
                {
                    cold.Add(clip);
                }
            }

            return cold;
        }
    }

    /// <summary>
    /// Loads the container template. Cheap to call repeatedly; only the first does work.
    /// </summary>
    public bool Initialise()
    {
        if (this.template is not null)
        {
            return this.Refresh();
        }

        if (this.templateLoadFailed)
        {
            return false;
        }

        foreach (var candidate in TemplateCandidates)
        {
            byte[] bytes;
            try
            {
                var file = this.data.GetFile(candidate);
                if (file?.Data is not { Length: > 0 })
                {
                    continue;
                }

                bytes = file.Data;
            }
            catch
            {
                // A candidate that cannot be read is simply not usable as a template;
                // the next one may still work. Exhausting them all latches a visible
                // failure Status below, so this swallow is not silent.
                continue;
            }

            if (!ScdWriter.TryParse(bytes, out var parsed, out var error) || parsed is null)
            {
                this.log.Warning("ScdForge: {Path} did not parse: {Error}", candidate, error);
                continue;
            }

            this.template = parsed;
            this.TemplatePath = candidate;
            this.log.Information(
                "ScdForge: container template {Path} ({Bytes} bytes, {Audio} audio entries)",
                candidate,
                bytes.Length,
                parsed.AudioOffsets.Length);
            return this.Refresh();
        }

        this.template = null;
        this.templateLoadFailed = true;
        this.Ready = false;
        this.Status = "no battle-voice container could be read from the game files. Reload the plugin to retry";
        return false;
    }

    /// <summary>Re-evaluates readiness. Penumbra can be unloaded at any time.</summary>
    private bool Refresh()
    {
        if (this.template is null)
        {
            this.Ready = false;
            this.Status = "no container template";
            this.lastStatusReady = -1; // a failure message overwrote the ready line
            return false;
        }

        if (!this.penumbra.PenumbraAvailable)
        {
            this.Ready = false;
            this.Status = "Penumbra is not installed or not loaded";
            this.lastStatusReady = -1; // a failure message overwrote the ready line
            return false;
        }

        int ready, pending;
        lock (this.gate)
        {
            ready = this.byVariant.Count;
            pending = this.inFlight.Count;
        }

        this.Ready = true;

        // Refresh is reached once per cast via NativeVoiceSink.Available; only rebuild
        // the status string when the numbers actually moved.
        if (ready != this.lastStatusReady || pending != this.lastStatusPending)
        {
            this.lastStatusReady = ready;
            this.lastStatusPending = pending;
            this.Status = pending > 0
                ? $"{ready} clip(s) ready, {pending} still being prepared"
                : $"{ready} clip(s) ready, built from {this.TemplatePath}";
        }

        return true;
    }

    /// <summary>
    /// Returns the forged clip for a variant, encoding it on first request.
    /// </summary>
    /// <param name="variantKey">
    /// Stable identity of the exact audio <paramref name="source"/> will produce — clip
    /// hash plus every parameter that changes a sample. Two requests with the same key must
    /// be byte-identical, because the second will be served the first's file.
    /// </param>
    /// <param name="createSource">
    /// Invoked only on a cache miss, then drained to completion. Pitch has already been
    /// applied by the provider chain, so whatever mode the mapping uses is baked in here
    /// and the engine plays at speed 1.
    /// </param>
    /// <returns>
    /// True with a ready clip, or false having started the encode in the background.
    /// </returns>
    /// <remarks>
    /// <b>Never encodes on the calling thread.</b> Measured on this machine, encoding a
    /// five-second clip takes about 10 ms — over half a frame at 60 fps, on the game's main
    /// thread, in combat. The first play of any clip was always going to fall back to the
    /// managed sink for the warm-up anyway, so nothing is lost by making it fall back while
    /// the encode happens off-thread instead of stalling the frame first.
    /// </remarks>
    public bool TryForge(string variantKey, Func<ISampleProvider> createSource, out ForgedClip? forged)
    {
        forged = null;

        if (string.IsNullOrEmpty(variantKey) || !this.Initialise() || this.template is null)
        {
            return false;
        }

        lock (this.gate)
        {
            if (this.byVariant.TryGetValue(variantKey, out var existing))
            {
                forged = existing;
                return true;
            }

            if (this.inFlight.Contains(variantKey) || this.failed.ContainsKey(variantKey))
            {
                return false;
            }

            if (this.byVariant.Count + this.inFlight.Count >= MaxVariants)
            {
                this.Status = $"at the limit of {MaxVariants} prepared clips. Reload the plugin to clear it";
                return false;
            }

            this.inFlight.Add(variantKey);
        }

        this.BeginEncode(variantKey, createSource);
        return false;
    }

    /// <summary>Encodes and writes off the game thread; registration happens in <see cref="Pump"/>.</summary>
    private void BeginEncode(string variantKey, Func<ISampleProvider> createSource)
    {
        // Captured before the task starts. `template` is written once during Initialise on
        // the main thread and only read afterwards; the clip's sample buffer is documented
        // as immutable after construction. Neither is mutated here.
        var snapshot = this.template!;

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // The plugin can be unloaded while this is queued. Nothing below should
                // touch Dalamud services or write files after that.
                if (this.shutDown)
                {
                    return;
                }

                var pcm = Drain(createSource(), out var seconds, out var tooLong);

                if (tooLong)
                {
                    // Refuse, as MaxSeconds has always claimed to. Truncating instead
                    // silently shortened the user's audio and then reported the clipped
                    // length back as fact.
                    this.MarkFailed(
                        variantKey,
                        $"the clip is longer than {MaxSeconds:0}s, so trim it and import it again");
                    return;
                }

                if (pcm.Length == 0)
                {
                    this.MarkFailed(variantKey, "the clip decoded to no samples");
                    return;
                }

                var payload = ScdWriter.AudioPayload.MsAdPcmMono(pcm, ClipLibrary.SampleRate);
                var scd = ScdWriter.PointAudioAtOneEntry(snapshot, payload);

                // A battle-voice container is authored to be intermittent — that is what
                // makes a character grunt on some swings and not others. Cloning one
                // inherits it, which presents as the native path firing only occasionally.
                ScdInspector.ForceDeterministicPlayback(scd, out var certainty);

                var name = ContentHash(scd);
                var localPath = Path.Combine(this.cacheDir, $"{name}.scd");

                if (this.shutDown)
                {
                    return;
                }

                WriteContentAddressed(localPath, scd);

                this.completed.Enqueue(
                    new Encoded(variantKey, $"sound/vfx/warcry/clip/{name}.scd", localPath, seconds, scd.Length));

                this.log.Information(
                    "ScdForge: encoded {Key} ({Bytes} bytes, {Seconds:0.00}s); {Certainty}",
                    variantKey,
                    scd.Length,
                    seconds,
                    certainty);
            }
            catch (Exception ex) when (!this.shutDown)
            {
                this.log.Error(ex, "ScdForge: encoding {Key} failed", variantKey);
            }
            catch
            {
                // Unloading. There is nowhere safe left to report this.
            }
            finally
            {
                lock (this.gate)
                {
                    this.inFlight.Remove(variantKey);
                }
            }
        });
    }

    /// <summary>
    /// Registers anything that finished encoding. Main thread only — Penumbra IPC is not
    /// safe to call from a worker.
    /// </summary>
    /// <returns>
    /// Clips registered by this call, which the caller must warm. Empty most frames.
    /// </returns>
    public IReadOnlyList<ForgedClip> Pump()
    {
        if (this.completed.IsEmpty)
        {
            return [];
        }

        var added = 0;
        while (this.completed.TryDequeue(out var item))
        {
            this.redirects[item.GamePath] = item.LocalPath;
            this.pendingRegistration.Add(item);
            added++;
        }

        if (added == 0)
        {
            return [];
        }

        // AddTemporaryModAll replaces the whole set for our tag, so the entire dictionary
        // goes every time rather than just the new entries.
        var code = this.penumbra.Redirect(PenumbraBridge.ClipsTag, new Dictionary<string, string>(this.redirects));

        if (code is null || code.Value != 0)
        {
            // Registration did not take — the IPC call failed outright, or Penumbra
            // answered with an error code. Either way the redirect may not be in force,
            // and a cached entry the game cannot load would be silently unplayable for
            // the rest of the session. Roll back and retry on a later pump.
            foreach (var item in this.pendingRegistration)
            {
                this.redirects.Remove(item.GamePath);
            }

            this.log.Error(
                "ScdForge: could not register {Count} redirect(s) ({Error}); they will be retried",
                this.pendingRegistration.Count,
                code is null ? this.penumbra.LastError : $"Penumbra returned {code.Value}");

            // Dropped, not re-queued: the variant is now neither cached nor in flight, so
            // the next request for it re-encodes and re-registers — a retry that is paced
            // by the throttle instead of hammering a failing IPC once per frame.
            this.pendingRegistration.Clear();
            this.Refresh();
            return [];
        }

        var registered = new List<ForgedClip>(this.pendingRegistration.Count);

        lock (this.gate)
        {
            foreach (var item in this.pendingRegistration)
            {
                var clip = new ForgedClip
                {
                    VariantKey = item.VariantKey,
                    GamePath = item.GamePath,
                    LocalPath = item.LocalPath,
                    Seconds = item.Seconds,
                    Bytes = item.Bytes,
                };

                this.byVariant[item.VariantKey] = clip;
                registered.Add(clip);
            }
        }

        this.pendingRegistration.Clear();
        this.Refresh();
        return registered;
    }

    /// <summary>
    /// Reads a provider to exhaustion into 16-bit mono samples.
    /// </summary>
    /// <param name="tooLong">
    /// Set when the source ran past <see cref="MaxSeconds"/>. The caller refuses on this
    /// rather than encoding what was collected: a truncated clip is indistinguishable from
    /// a correct one once it is on disk.
    /// </param>
    private static short[] Drain(ISampleProvider source, out float seconds, out bool tooLong)
    {
        var rate = source.WaveFormat.SampleRate;
        var limit = (int)(rate * MaxSeconds);
        var buffer = new float[4096];
        var collected = new List<short>(rate);

        tooLong = false;

        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var v = Math.Clamp(buffer[i], -1f, 1f);
                collected.Add((short)(v * short.MaxValue));
            }

            // Tested after appending, not in the loop condition: checking first discarded
            // the block that had already been read out of the provider.
            if (collected.Count > limit)
            {
                tooLong = true;
                break;
            }
        }

        seconds = collected.Count / (float)rate;
        return [.. collected];
    }

    /// <summary>
    /// Writes a content-addressed cache file: skip when it already exists, and land it
    /// with a temp-then-move so the final name only ever holds complete bytes.
    /// </summary>
    /// <remarks>
    /// The path is derived from the bytes, so "already exists with the right length"
    /// means "already holds exactly this content" — overwriting would only risk an
    /// IOException against a reader (a concurrent encode of a byte-identical variant,
    /// or the game engine itself once the redirect has loaded). Both used to happen: a
    /// plain WriteAllBytes here failed with "being used by another process" the moment
    /// the pack builder ran two identical-content encodes at once.
    /// </remarks>
    private static void WriteContentAddressed(string localPath, byte[] scd)
    {
        var existing = new FileInfo(localPath);
        if (existing.Exists && existing.Length == scd.Length)
        {
            return;
        }

        // Unique temp per attempt: two concurrent encodes of the same content must not
        // collide on the temp name either.
        var tmp = $"{localPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tmp, scd);

        try
        {
            // overwrite:true only for the wrong-length case — a truncated leftover from
            // a crash mid-write under the old scheme.
            File.Move(tmp, localPath, overwrite: existing.Exists);
        }
        catch (IOException) when (File.Exists(localPath) && new FileInfo(localPath).Length == scd.Length)
        {
            // A concurrent encode of a byte-identical variant won the move. Same
            // content is already in place, so this attempt has nothing left to do.
            TryDelete(tmp);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // A stray .tmp is swept on the next construction.
            }
        }
    }

    private static string ContentHash(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 8).ToLowerInvariant();

    /// <summary>Stops accepting work and drops every redirect. Call once, on unload.</summary>
    public void ShutDown()
    {
        this.shutDown = true;
        this.Clear();
    }

    /// <summary>Drops every redirect. Files on disk are left for the next session.</summary>
    public void Clear()
    {
        lock (this.gate)
        {
            this.byVariant.Clear();

            // Terminal failures go too: clearing the forge is the retry.
            this.failed.Clear();
        }

        this.redirects.Clear();
        this.pendingRegistration.Clear();
        this.completed.Clear();
        this.penumbra.Clear(PenumbraBridge.ClipsTag);
        this.Refresh();
    }
}
