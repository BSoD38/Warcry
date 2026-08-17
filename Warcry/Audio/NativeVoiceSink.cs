using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using Warcry.Native;

namespace Warcry.Audio;

/// <summary>
/// Plays through the game's own sound engine: a clip we encode, in a container we assemble,
/// handed to <c>SoundManager::PlaySound</c>.
/// </summary>
/// <remarks>
/// <para><b>What this buys over the managed sink.</b> The engine does the mixing, so the
/// audio lands on the game's own bus, follows the game's output device, obeys its volume
/// rules, and is positioned by the same code that positions every other sound in the world.
/// None of that is emulated.</para>
/// <para><b>Gain is deliberately not scaled by the game's sliders here.</b> The managed sink
/// reads <c>SoundMaster</c>/<c>SoundVoice</c> and multiplies them in by hand because it has
/// to. This sink must not: the engine applies its own bus volume downstream, and applying
/// them twice squares the curve and makes everything vanish at low settings.</para>
/// <para><b>Warm-up is real and visible.</b> Resource loading is asynchronous, so the first
/// request for a newly forged path returns before the bytes are in memory. That first play
/// is used as the warm-up and reported as a refusal, which lets a composite fall back to the
/// managed sink for exactly one line rather than dropping it.</para>
/// <para>Verified end to end in <c>docs/native-spike.md</c>. Not yet verified: behaviour
/// against the Master and Voice sliders, positional attenuation, and sustained load —
/// PLAN.md §6 (b)–(e). That is why this is opt-in.</para>
/// </remarks>
public sealed unsafe class NativeVoiceSink : IVoiceSink
{
    /// <summary>
    /// Assumed tail beyond a clip's own length before its voice slot is considered free.
    /// </summary>
    /// <remarks>
    /// Active voices are tracked by expected end time rather than by retaining a
    /// <c>SoundData*</c>. The engine recycles pool slots, and a retained pointer reads as a
    /// perfectly healthy sound that belongs to something else — that mistake produced
    /// several false "it played" results during the spike. Time is not exact, but it cannot
    /// lie about whose sound it is.
    /// </remarks>
    private const double TailSeconds = 0.25;

    /// <summary>
    /// How long a warm-up is given to land before the path is trusted for a real play.
    /// </summary>
    /// <remarks>
    /// A local file behind a Penumbra redirect loads in well under this. The window only
    /// matters when a clip is forged and used almost immediately; the throttle cooldown is
    /// an order of magnitude longer in normal play.
    /// </remarks>
    private const double WarmGraceSeconds = 0.25;

    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ScdForge forge;
    private readonly List<long> voiceEndsAt = [];

    public NativeVoiceSink(IPluginLog log, Configuration config, ScdForge forge)
    {
        this.log = log;
        this.config = config;
        this.forge = forge;
    }

    public string Name => "Native";

    public string Status => this.Available
        ? $"Native (game engine) — {this.forge.Status}"
        : $"Native unavailable — {this.forge.Status}";

    public bool Available
    {
        get
        {
            if (!this.forge.Initialise())
            {
                return false;
            }

            return SoundManager.Instance() != null;
        }
    }

    public int ActiveVoices => this.voiceEndsAt.Count;

    /// <summary>
    /// Why the last request was not played natively, or empty if it was.
    /// </summary>
    /// <remarks>
    /// Every path out of <see cref="TryPlay"/> sets this. A silent boolean was enough to
    /// hide a three-play warm-up ramp behind what looked like "native does not work".
    /// </remarks>
    public string LastRefusal { get; private set; } = string.Empty;

    public bool TryPlay(in VoiceRequest request)
    {
        if (string.IsNullOrEmpty(request.VariantKey))
        {
            // Nothing stable to content-address, so it can neither be cached nor served.
            this.LastRefusal = "the request has no variant key, so it cannot be encoded";
            return false;
        }

        if (this.voiceEndsAt.Count >= this.config.MaxConcurrent)
        {
            this.LastRefusal = $"at the concurrency cap ({this.config.MaxConcurrent})";
            return false;
        }

        // The engine's own bus volume is applied downstream; only our own trim goes here.
        var gain = Math.Clamp(request.Gain * this.config.MasterGain, 0f, 2f);
        if (gain <= 0.0001f)
        {
            this.LastRefusal = "gain is zero";
            return false;
        }

        if (!this.forge.TryForge(request.VariantKey, request.CreateSource, out var clip) || clip is null)
        {
            this.LastRefusal = "clip is still encoding (first use of a clip always falls back)";
            return false;
        }

        var manager = SoundManager.Instance();
        if (manager == null)
        {
            this.LastRefusal = "SoundManager is not available";
            return false;
        }

        // The warm-up is issued at registration, not charged to a play. If one has not
        // happened yet, or has not had time to land, let the managed sink take this line.
        if (clip.WarmedAt == 0)
        {
            this.LastRefusal = "clip encoded but not yet warmed — the next frame will warm it";
            return false;
        }

        if (Stopwatch.GetTimestamp() - clip.WarmedAt < (long)(WarmGraceSeconds * Stopwatch.Frequency))
        {
            this.LastRefusal = "clip warmed a moment ago; giving the resource time to load";
            return false;
        }

        try
        {
            var result = manager->PlaySound(
                clip.GamePath,
                gain,
                0u,
                request.Position.X, request.Position.Y, request.Position.Z,
                1.0f,                       // pitch is baked into the encoded samples
                0,
                0u,                         // every audio index resolves to our clip
                true,                       // engine owns the slot; never retain the pointer
                CategoryOf(request.SoundCategory),
                false,
                -1,
                false,
                false,
                true,                       // world coordinates, confirmed by observation
                false);

            if (result == null)
            {
                this.LastRefusal = "the engine had no free sound slot";
                this.log.Warning("NativeVoiceSink: no pool slot for {Path}", clip.GamePath);
                return false;
            }

            this.voiceEndsAt.Add(
                Stopwatch.GetTimestamp() + (long)((clip.Seconds + TailSeconds) * Stopwatch.Frequency));
            this.LastRefusal = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            this.LastRefusal = $"PlaySound threw: {ex.Message}";
            this.log.Error(ex, "NativeVoiceSink: PlaySound threw for {Path}", clip.GamePath);
            return false;
        }
    }

    public void Update()
    {
        // Registers anything the background encoder finished. Must be the game thread:
        // Penumbra IPC is not safe to call from a worker.
        foreach (var clip in this.forge.Pump())
        {
            this.Warm(clip);
        }

        if (this.voiceEndsAt.Count == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        for (var i = this.voiceEndsAt.Count - 1; i >= 0; i--)
        {
            if (this.voiceEndsAt[i] <= now)
            {
                this.voiceEndsAt.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Asks the engine for a freshly registered path at zero volume, purely to make it load.
    /// </summary>
    /// <remarks>
    /// <para>This exists because the first request for any path returns before the resource
    /// is in memory. Doing it here, the instant the redirect is registered, means the cost
    /// lands on an idle frame instead of on a voiceline.</para>
    /// <para>The earlier design charged the warm-up to the first real play and refused that
    /// line — which, combined with the encode also costing a play, meant the <b>third</b>
    /// use of a given clip was the first one you actually heard through the engine. With
    /// several mapped actions and a cooldown between them, that ramp is long enough to look
    /// exactly like the native path not working at all.</para>
    /// </remarks>
    private void Warm(ForgedClip clip)
    {
        var manager = SoundManager.Instance();
        if (manager == null)
        {
            return;
        }

        try
        {
            manager->PlaySound(
                clip.GamePath,
                0f,
                0u,
                0f, 0f, 0f,
                1.0f,
                0,
                0u,
                true,
                SoundVolumeCategory.Player,
                false,
                -1,
                false,
                false,
                false,      // non-positional: this is a load, not a sound
                false);

            clip.WarmedAt = Stopwatch.GetTimestamp();
            this.log.Information("NativeVoiceSink: warmed {Path}", clip.GamePath);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "NativeVoiceSink: warming {Path} failed", clip.GamePath);
        }
    }

    /// <summary>
    /// The game's own Player/Party/Other classification, straight through.
    /// </summary>
    /// <remarks>
    /// Read from <c>Character+0x2369</c>, which is the same byte the engine uses for its own
    /// sounds — so a plugin line and a native one are categorised identically rather than
    /// merely similarly.
    /// </remarks>
    private static SoundVolumeCategory CategoryOf(byte soundCategory) => soundCategory switch
    {
        0 => SoundVolumeCategory.Player,
        1 => SoundVolumeCategory.Party,
        _ => SoundVolumeCategory.Other,
    };

    public void Dispose()
    {
        this.voiceEndsAt.Clear();

        // Sounds already handed to the engine are the engine's problem — they are short and
        // auto-released. What must go is the redirect set, so an unloaded plugin does not
        // leave the resource system pointing at files in a cache directory.
        try
        {
            this.forge.ShutDown();
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "NativeVoiceSink: clearing redirects during teardown failed");
        }
    }
}
