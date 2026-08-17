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

    public bool TryPlay(in VoiceRequest request)
    {
        if (string.IsNullOrEmpty(request.VariantKey))
        {
            // Nothing stable to content-address, so it can neither be cached nor served.
            return false;
        }

        if (this.voiceEndsAt.Count >= this.config.MaxConcurrent)
        {
            return false;
        }

        // The engine's own bus volume is applied downstream; only our own trim goes here.
        var gain = Math.Clamp(request.Gain * this.config.MasterGain, 0f, 2f);
        if (gain <= 0.0001f)
        {
            return false;
        }

        if (!this.forge.TryForge(request.VariantKey, request.CreateSource, out var clip) || clip is null)
        {
            return false;
        }

        var manager = SoundManager.Instance();
        if (manager == null)
        {
            return false;
        }

        try
        {
            var result = manager->PlaySound(
                clip.GamePath,
                clip.Warm ? gain : 0f,
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
                this.log.Warning("NativeVoiceSink: no pool slot for {Path}", clip.GamePath);
                return false;
            }

            if (!clip.Warm)
            {
                // This play was the resource load. It made no sound, so refuse and let the
                // caller fall back for this one line; every later play of this clip is real.
                clip.Warm = true;
                return false;
            }

            this.voiceEndsAt.Add(
                Stopwatch.GetTimestamp() + (long)((clip.Seconds + TailSeconds) * Stopwatch.Frequency));
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "NativeVoiceSink: PlaySound threw for {Path}", clip.GamePath);
            return false;
        }
    }

    public void Update()
    {
        // Registers anything the background encoder finished. Must be the game thread:
        // Penumbra IPC is not safe to call from a worker.
        this.forge.Pump();

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
