using System;
using System.Numerics;
using NAudio.Wave;

namespace Warcry.Audio;

/// <summary>One thing to play, at a place, on a channel.</summary>
public readonly struct VoiceRequest
{
    /// <summary>
    /// Builds a fresh mono 44100 Hz reader. Called at most once per sink that actually
    /// plays the request, and not at all when a sink can serve it from cache.
    /// </summary>
    /// <remarks>
    /// A factory rather than an instance, for two reasons. The native sink has to
    /// <em>drain</em> a provider to encode it, so handing the same instance to a fallback
    /// would give it an exhausted reader and silence. And a request held back for a cast
    /// bar no longer builds a provider it might never use.
    /// </remarks>
    public readonly Func<ISampleProvider> CreateSource;

    /// <summary>
    /// Stable identity of the exact audio <see cref="CreateSource"/> will produce, or empty
    /// when there is none.
    /// </summary>
    /// <remarks>
    /// Must cover every input that changes a sample — clip, rate, pitch mode, FFT size —
    /// because the native sink content-addresses its encoded files by this key and will
    /// serve a second request the first one's bytes. An empty key is honest: it means
    /// "uncacheable", and the native sink declines rather than guessing.
    /// </remarks>
    public readonly string VariantKey;

    public readonly Vector3 Position;

    /// <summary>The game's own Player/Party/Other classification, from Character+0x2369.</summary>
    public readonly byte SoundCategory;

    /// <summary>Clip and profile trim, before any game-config scaling.</summary>
    public readonly float Gain;

    public readonly uint CasterEntityId;

    public VoiceRequest(
        Func<ISampleProvider> createSource,
        string variantKey,
        Vector3 position,
        byte soundCategory,
        float gain,
        uint casterEntityId)
    {
        this.CreateSource = createSource;
        this.VariantKey = variantKey;
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
