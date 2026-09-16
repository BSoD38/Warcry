using System;
using System.Numerics;
using NAudio.Wave;

namespace Warcry.Audio;

/// <summary>One thing to play, at a place, on a channel.</summary>
public readonly struct VoiceRequest
{
    /// <summary>
    /// Builds a fresh mono 44100 Hz reader, with the given extra playback rate baked on
    /// top of whatever pitch the variant already carries. Pass 1 for the audio exactly as
    /// <see cref="VariantKey"/> identifies it.
    /// </summary>
    /// <remarks>
    /// <para>A factory rather than an instance, for two reasons. An encoder has to
    /// <em>drain</em> a provider, so handing the same instance to a second consumer would
    /// give it an exhausted reader and silence. And a request held back for a cast bar no
    /// longer builds a provider it might never use.</para>
    /// <para>The rate parameter exists because the two consumers want different audio from
    /// the same request: the managed sink plays the finished line and calls with
    /// <see cref="Speed"/>; the forge encodes the <em>base</em> variant and calls with 1,
    /// leaving <see cref="Speed"/> for the engine's own speed argument.</para>
    /// </remarks>
    public readonly Func<float, ISampleProvider> CreateSource;

    /// <summary>
    /// Stable identity of the exact audio <c>CreateSource(1f)</c> will produce, or empty
    /// when there is none.
    /// </summary>
    /// <remarks>
    /// Must cover every input that changes a sample of the base rendering — clip, baked
    /// rate, pitch mode, FFT size — because the native sink content-addresses its encoded
    /// files by this key and will serve a second request the first one's bytes. An empty
    /// key is honest: it means "uncacheable", and the native sink declines rather than
    /// guessing.
    /// </remarks>
    public readonly string VariantKey;

    /// <summary>
    /// Playback rate the sink must apply on top of the encoded audio. 1 when the pitch is
    /// fully baked into the variant.
    /// </summary>
    /// <remarks>
    /// This is what lets a random per-cast pitch coexist with content-addressed encoding:
    /// the variant stays one stable base rendering, and the roll rides in here — as the
    /// engine's <c>speed</c> argument natively, or baked at play time by the managed sink.
    /// </remarks>
    public readonly float Speed;

    public readonly Vector3 Position;

    /// <summary>The game's own Player/Party/Other classification, from Character+0x2369.</summary>
    public readonly byte SoundCategory;

    /// <summary>Clip and profile trim, before any game-config scaling.</summary>
    public readonly float Gain;

    public readonly uint CasterEntityId;

    /// <summary>
    /// Is this your own line? Only the concurrency reservation reads it.
    /// </summary>
    /// <remarks>
    /// Carried explicitly rather than inferred from <see cref="CasterEntityId"/> so the
    /// audio layer never has to ask the game who the local player is — and so an audition,
    /// which has no caster at all, can still claim the reservation.
    /// </remarks>
    public readonly bool IsSelf;

    public VoiceRequest(
        Func<float, ISampleProvider> createSource,
        string variantKey,
        float speed,
        Vector3 position,
        byte soundCategory,
        float gain,
        uint casterEntityId,
        bool isSelf)
    {
        this.CreateSource = createSource;
        this.VariantKey = variantKey;
        this.Speed = speed;
        this.Position = position;
        this.SoundCategory = soundCategory;
        this.Gain = gain;
        this.CasterEntityId = casterEntityId;
        this.IsSelf = isSelf;
    }
}

// NOTE: the sinks deliberately share no interface. NativeVoiceSink and ManagedVoiceSink
// are only ever reached through CompositeVoiceSink, which holds them concretely and is
// itself the only sink anything else talks to — so an abstraction here would have had one
// consumer and three declarations. Each still exposes Status/Available/ActiveVoices/
// TryPlay/Update/StopAll, and StopAll is load-bearing rather than politeness: for the
// native sink it is how a retained slot in the game's 256-entry sound pool gets handed
// back, and a slot leaked past unload is gone until the client restarts.
