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
/// <para><b>Every index, not a scoped group.</b> Scoping matters when shadowing a real
/// <c>Vo_Battle</c> path, where the damage and death banks must survive. This is our own
/// path that nothing else reads, so pointing all of them at one entry means
/// <c>soundNumber 0</c> is guaranteed to land on our audio with no group arithmetic.</para>
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

    /// <summary>Guards <see cref="byVariant"/> and <see cref="inFlight"/> across threads.</summary>
    private readonly object gate = new();

    private readonly Dictionary<string, ForgedClip> byVariant = [];
    private readonly HashSet<string> inFlight = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Encoded> completed = new();

    /// <summary>Main-thread only: Penumbra IPC and the redirect set are not shared.</summary>
    private readonly Dictionary<string, string> redirects = [];

    /// <summary>Encodes drained this pump, held until registration succeeds.</summary>
    private readonly List<Encoded> pendingRegistration = [];

    private ScdWriter.Template? template;

    /// <summary>Set on unload so in-flight encodes stop touching Dalamud services and disk.</summary>
    private volatile bool shutDown;

    /// <summary>An encode that finished off-thread, waiting to be registered.</summary>
    private sealed record Encoded(string VariantKey, string GamePath, string LocalPath, float Seconds);

    public ScdForge(IDataManager data, IPluginLog log, PenumbraBridge penumbra, string configDirectory)
    {
        this.data = data;
        this.log = log;
        this.penumbra = penumbra;
        this.cacheDir = Path.Combine(configDirectory, ".cache", "scd");

        try
        {
            Directory.CreateDirectory(this.cacheDir);
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

    /// <summary>Clips the engine has been asked for at least once, so are loadable now.</summary>
    public int WarmedCount
    {
        get
        {
            lock (this.gate)
            {
                var warmed = 0;
                foreach (var clip in this.byVariant.Values)
                {
                    if (clip.WarmedAt != 0)
                    {
                        warmed++;
                    }
                }

                return warmed;
            }
        }
    }

    /// <summary>Which game file the container is cloned from, for the Status tab.</summary>
    public string TemplatePath { get; private set; } = string.Empty;

    /// <summary>
    /// Loads the container template. Cheap to call repeatedly; only the first does work.
    /// </summary>
    public bool Initialise()
    {
        if (this.template is not null)
        {
            return this.Refresh();
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
        this.Ready = false;
        this.Status = "no battle-voice container could be read from the game files";
        return false;
    }

    /// <summary>Re-evaluates readiness. Penumbra can be unloaded at any time.</summary>
    private bool Refresh()
    {
        if (this.template is null)
        {
            this.Ready = false;
            this.Status = "no container template";
            return false;
        }

        if (!this.penumbra.PenumbraAvailable)
        {
            this.Ready = false;
            this.Status = "Penumbra is not installed or not loaded";
            return false;
        }

        int ready, pending;
        lock (this.gate)
        {
            ready = this.byVariant.Count;
            pending = this.inFlight.Count;
        }

        this.Ready = true;
        this.Status = pending > 0
            ? $"ready — {ready} clip(s) encoded, {pending} in progress"
            : $"ready — {ready} clip(s) encoded from {this.TemplatePath}";
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

            if (this.inFlight.Contains(variantKey))
            {
                return false;
            }

            if (this.byVariant.Count + this.inFlight.Count >= MaxVariants)
            {
                this.Status = $"variant cap reached ({MaxVariants}) — reload the plugin to clear it";
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

                var pcm = Drain(createSource(), out var seconds);
                if (pcm.Length == 0)
                {
                    this.log.Warning("ScdForge: {Key} produced no samples", variantKey);
                    return;
                }

                var payload = ScdWriter.AudioPayload.MsAdPcmMono(pcm, ClipLibrary.SampleRate);

                // Every index, so soundNumber 0 cannot miss. This is our own path.
                var scd = ScdWriter.PointAudioAtOneEntry(snapshot, null, payload, out _);

                var name = ContentHash(scd);
                var localPath = Path.Combine(this.cacheDir, $"{name}.scd");

                if (this.shutDown)
                {
                    return;
                }

                File.WriteAllBytes(localPath, scd);

                this.completed.Enqueue(
                    new Encoded(variantKey, $"sound/vfx/warcry/clip/{name}.scd", localPath, seconds));

                this.log.Information(
                    "ScdForge: encoded {Key} ({Bytes} bytes, {Seconds:0.00}s)", variantKey, scd.Length, seconds);
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
        var code = this.penumbra.Redirect(new Dictionary<string, string>(this.redirects));

        if (code is null)
        {
            // Registration never happened, so the engine cannot reach these files. Roll
            // them back rather than cache them: a cached entry the game cannot load would
            // be silently unplayable for the rest of the session.
            foreach (var item in this.pendingRegistration)
            {
                this.redirects.Remove(item.GamePath);
            }

            this.log.Error(
                "ScdForge: could not register {Count} redirect(s) ({Error}); they will be retried",
                this.pendingRegistration.Count,
                this.penumbra.LastError);

            this.pendingRegistration.Clear();
            this.Refresh();
            return [];
        }

        if (code.Value != 0)
        {
            this.log.Warning(
                "ScdForge: Penumbra returned {Code} registering {Count} redirect(s)",
                code.Value,
                this.redirects.Count);
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
    private static short[] Drain(ISampleProvider source, out float seconds)
    {
        var rate = source.WaveFormat.SampleRate;
        var limit = (int)(rate * MaxSeconds);
        var buffer = new float[4096];
        var collected = new List<short>(rate);

        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0 && collected.Count < limit)
        {
            for (var i = 0; i < read; i++)
            {
                var v = Math.Clamp(buffer[i], -1f, 1f);
                collected.Add((short)(v * short.MaxValue));
            }
        }

        seconds = collected.Count / (float)rate;
        return [.. collected];
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
        }

        this.redirects.Clear();
        this.pendingRegistration.Clear();
        this.completed.Clear();
        this.penumbra.Clear();
        this.Refresh();
    }
}
