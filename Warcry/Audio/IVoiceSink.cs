using System;
using System.Numerics;
using NAudio.Wave;

namespace Warcry.Audio;

/// <summary>One thing to play, at a place, on a channel.</summary>
public readonly struct VoiceRequest
{
    /// <summary>Mono, 44100 Hz. Freshly constructed per play — never shared between plays.</summary>
    public readonly ISampleProvider Source;

    public readonly Vector3 Position;

    /// <summary>The game's own Player/Party/Other classification, from Character+0x2369.</summary>
    public readonly byte SoundCategory;

    /// <summary>Clip and profile trim, before any game-config scaling.</summary>
    public readonly float Gain;

    public readonly uint CasterEntityId;

    public VoiceRequest(ISampleProvider source, Vector3 position, byte soundCategory, float gain, uint casterEntityId)
    {
        this.Source = source;
        this.Position = position;
        this.SoundCategory = soundCategory;
        this.Gain = gain;
        this.CasterEntityId = casterEntityId;
    }
}

/// <summary>
/// The seam that lets the native-audio question stay unanswered without blocking the plugin.
/// </summary>
/// <remarks>
/// Implementations: <see cref="ManagedVoiceSink"/> (NAudio, ships by default),
/// a future NativeVoiceSink (SoundManager.PlaySound via Penumbra-redirected .scd, spike-gated),
/// and a null sink. See docs/PLAN.md 5.6.
/// </remarks>
public interface IVoiceSink : IDisposable
{
    /// <summary>Short name for the Status tab, e.g. "Managed (NAudio / WaveOut)".</summary>
    string Name { get; }

    /// <summary>Plain-English reason this sink is the active one. Shown to the user.</summary>
    string Status { get; }

    bool Available { get; }

    /// <summary>Number of voices currently sounding.</summary>
    int ActiveVoices { get; }

    /// <summary>Called on the game main thread. Returns false if the request was refused.</summary>
    bool TryPlay(in VoiceRequest request);

    /// <summary>Called every frame on the game main thread.</summary>
    void Update();
}
