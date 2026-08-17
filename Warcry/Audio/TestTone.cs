using System;
using NAudio.Wave;

namespace Warcry.Audio;

/// <summary>
/// A synthesised burst used to prove the audio chain end to end without needing an
/// asset. M4 swaps this for decoded PCM from the clip library; nothing else changes.
/// </summary>
/// <remarks>
/// Deliberately enveloped: a raw sine gated on and off clicks audibly, and that same
/// click is what a 5 ms ramp fixes on real clips. Getting it right here means the
/// ramp behaviour is already proven when real audio arrives.
/// </remarks>
public sealed class TestTone : ISampleProvider
{
    public const int SampleRate = 44100;

    private readonly int totalSamples;
    private readonly int attackSamples;
    private readonly float startFrequency;
    private readonly float endFrequency;

    private int position;
    private double phase;

    public TestTone(float seconds = 0.35f, float startHz = 620f, float endHz = 380f)
    {
        this.totalSamples = (int)(SampleRate * seconds);
        this.attackSamples = (int)(SampleRate * 0.005f); // 5 ms — the anti-zipper ramp
        this.startFrequency = startHz;
        this.endFrequency = endHz;
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        var remaining = this.totalSamples - this.position;
        if (remaining <= 0)
        {
            return 0; // MixingSampleProvider removes inputs that return 0.
        }

        var n = Math.Min(count, remaining);

        for (var i = 0; i < n; i++)
        {
            var t = (float)this.position / this.totalSamples;

            // Falling pitch reads as a "shout" rather than a system beep.
            var frequency = this.startFrequency + ((this.endFrequency - this.startFrequency) * t);
            this.phase += 2.0 * Math.PI * frequency / SampleRate;
            if (this.phase > Math.PI * 2)
            {
                this.phase -= Math.PI * 2;
            }

            // Linear attack, exponential decay.
            var attack = this.position < this.attackSamples
                ? (float)this.position / this.attackSamples
                : 1f;
            var decay = MathF.Exp(-3.5f * t);

            buffer[offset + i] = (float)Math.Sin(this.phase) * attack * decay * 0.5f;
            this.position++;
        }

        return n;
    }
}
