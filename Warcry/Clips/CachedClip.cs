using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Warcry.Clips;

public enum PitchMode : byte
{
    // Tape speed: pitch and duration move together. Cheap, and usually the better choice
    // for a voice — a larger or smaller person really does shift formants along with pitch.
    Varispeed = 0,

    // Phase vocoder: duration preserved. Costs CPU, and the phasiness and transient
    // smearing are structural to the algorithm. Raise fftSize to trade CPU for quality.
    PreserveDuration = 1,
}

// Decoded mono 44.1 kHz float PCM, shared and immutable.
public sealed class CachedClip
{
    public CachedClip(ClipInfo info, float[] samples)
    {
        this.Info = info;
        this.Samples = samples;
    }

    public ClipInfo Info { get; }

    // Never mutated after construction — many providers read it concurrently.
    public float[] Samples { get; }

    // One reader per playback, never reused. rate is varispeed: 2 = an octave up and half
    // as long, 0.5 = an octave down and twice as long.
    public ISampleProvider CreateProvider(float rate = 1f, PitchMode mode = PitchMode.Varispeed, int fftSize = 2048)
    {
        if (mode == PitchMode.Varispeed || Math.Abs(rate - 1f) < 0.001f)
        {
            return new Reader(this.Samples, rate);
        }

        // osamp 8 is Bernsee's recommended overlap for speech-like material; lower values
        // are noticeably grainier.
        return new SmbPitchShiftingSampleProvider(
            new Reader(this.Samples, 1f), fftSize, 8, rate);
    }

    public static float SemitonesToRate(float semitones) => MathF.Pow(2f, semitones / 12f);

    // For pitch baked into an encoded variant: a continuous random rate would mint a new
    // variant per roll and make the set unbounded, while half-semitone steps cap a ±12 st
    // spread at 49 renderings and sit below the just-noticeable difference here.
    public static float QuantiseRate(float rate, float stepSemitones = 0.5f)
    {
        if (rate <= 0f || stepSemitones <= 0f)
        {
            return 1f;
        }

        var semitones = 12f * MathF.Log2(rate);
        var snapped = MathF.Round(semitones / stepSemitones) * stepSemitones;
        return Math.Abs(snapped) < 0.001f ? 1f : SemitonesToRate(snapped);
    }

    private sealed class Reader : ISampleProvider
    {
        private readonly float[] samples;
        private readonly double rate;
        private double position;

        public Reader(float[] samples, float rate)
        {
            this.samples = samples;
            this.rate = Math.Clamp(rate, 0.25f, 4f);
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(ClipLibrary.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            if (Math.Abs(this.rate - 1.0) < 1e-6)
            {
                var whole = (int)this.position;
                var left = this.samples.Length - whole;
                if (left <= 0)
                {
                    return 0; // MixingSampleProvider drops inputs that return 0.
                }

                var take = Math.Min(count, left);
                Array.Copy(this.samples, whole, buffer, offset, take);
                this.position += take;
                return take;
            }

            var written = 0;
            var last = this.samples.Length - 1;

            while (written < count)
            {
                var i = (int)this.position;
                if (i >= last)
                {
                    break;
                }

                // Catmull-Rom rather than linear: linear interpolation is a poor
                // reconstruction filter and is the dominant source of the harshness when
                // pitching up. Neighbours are clamped at the buffer edges.
                var t = (float)(this.position - i);
                var p0 = this.samples[i > 0 ? i - 1 : 0];
                var p1 = this.samples[i];
                var p2 = this.samples[i + 1];
                var p3 = this.samples[i + 2 <= last ? i + 2 : last];

                buffer[offset + written] = 0.5f * (
                    (2f * p1) +
                    ((-p0 + p2) * t) +
                    (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t * t) +
                    ((-p0 + (3f * p1) - (3f * p2) + p3) * t * t * t));

                this.position += this.rate;
                written++;
            }

            return written;
        }
    }
}
