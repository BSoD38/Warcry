using System;
using System.Numerics;
using NAudio.Wave;

namespace Warcry.Audio;

// One thing to play, at a place, on a channel.
public readonly struct VoiceRequest
{
    // A factory, not an instance: an encoder drains a provider, so a second consumer
    // handed the same one gets silence. The rate argument is baked on top of the variant's
    // own pitch — the managed sink passes Speed, the forge passes 1 and encodes the base.
    public readonly Func<float, ISampleProvider> CreateSource;

    // Identity of the audio CreateSource(1f) produces; empty means uncacheable and the
    // native sink declines. Must cover every input that changes a sample of the base
    // rendering (clip, baked rate, pitch mode, FFT size) — the native sink
    // content-addresses its encoded files by this.
    public readonly string VariantKey;

    // Applied on top of the encoded audio; 1 when the pitch is baked into the variant.
    // This is what lets a random per-cast pitch coexist with content-addressed encoding.
    public readonly float Speed;

    public readonly Vector3 Position;

    // The game's own Player/Party/Other classification, from Character+0x2369.
    public readonly byte SoundCategory;

    // Clip and profile trim, before any game-config scaling.
    public readonly float Gain;

    public readonly uint CasterEntityId;

    // Only the concurrency reservation reads it. Carried rather than inferred from
    // CasterEntityId so the audio layer never asks the game who the local player is, and
    // so an audition — which has no caster — can still claim the reservation.
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

// The sinks deliberately share no interface: NativeVoiceSink and ManagedVoiceSink are only
// ever reached through CompositeVoiceSink, so an abstraction would have one consumer and
// three declarations. StopAll is load-bearing — for the native sink it is how a retained
// slot in the game's 256-entry sound pool gets handed back, and a slot leaked past unload
// is gone until the client restarts.
