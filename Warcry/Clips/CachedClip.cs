using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Warcry.Clips;

/// <summary>How a pitch offset is applied.</summary>
public enum PitchMode : byte
{
    /// <summary>
    /// Tape speed: pitch and duration move together. Cheap, no artifacts beyond
    /// interpolation error, and usually the better choice for a voice — a physically
    /// larger or smaller person really does shift formants along with pitch.
    /// </summary>
    Varispeed = 0,

    /// <summary>
    /// Phase vocoder: pitch changes, duration is preserved. Costs CPU and introduces
    /// phasiness and transient smearing that no amount of filtering removes — those are
    /// structural to the algorithm. Raise <c>fftSize</c> to trade CPU for quality.
    /// </summary>
    PreserveDuration = 1,
}

/// <summary>Decoded mono 44.1 kHz float PCM, shared and immutable.</summary>
public sealed class CachedClip
{
    public CachedClip(ClipInfo info, float[] samples)
    {
        this.Info = info;
        this.Samples = samples;
    }

    public ClipInfo Info { get; }

    /// <summary>Never mutated after construction — many providers read it concurrently.</summary>
    public float[] Samples { get; }

    /// <summary>
    /// A fresh reader over the shared buffer. One per playback, never reused.
    /// </summary>
    /// <param name="rate">
    /// Playback rate. 1 = unchanged, 2 = an octave up and half as long, 0.5 = an octave
    /// down and twice as long. This is varispeed — pitch and duration move together,
    /// like tape speed — rather than a formant-preserving shift.
    /// </param>
    public ISampleProvider CreateProvider(float rate = 1f, PitchMode mode = PitchMode.Varispeed, int fftSize = 2048)
    {
        if (mode == PitchMode.Varispeed || Math.Abs(rate - 1f) < 0.001f)
        {
            return new Reader(this.Samples, rate);
        }

        // Read at natural speed, then shift pitch without touching duration.
        // osamp 8 is Bernsee's recommended overlap for speech-like material; lower
        // values are noticeably grainier.
        return new SmbPitchShiftingSampleProvider(
            new Reader(this.Samples, 1f), fftSize, 8, rate);
    }

    /// <summary>Semitones to a playback rate. 12 semitones = one octave = 2x.</summary>
    public static float SemitonesToRate(float semitones) => MathF.Pow(2f, semitones / 12f);

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
            // Fast path: no resampling at all when the rate is effectively 1.
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

                // Catmull-Rom rather than linear. Linear interpolation is a poor
                // reconstruction filter and is the dominant source of the harshness you
                // hear when pitching up; cubic costs three extra multiplies and removes
                // most of it. Neighbours are clamped at the buffer edges.
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
