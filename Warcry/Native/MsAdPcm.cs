using System;
using System.Buffers.Binary;

namespace Warcry.Native;

/// <summary>
/// Microsoft ADPCM encoder — 4 bits per sample, fixed-coefficient, no dependencies.
/// </summary>
/// <remarks>
/// <para><b>Why this and not PCM.</b> A survey of the game's own SCDs (2026-08-17) found
/// 267 MS-ADPCM entries across 18 of 26 files, 92 HCA entries, and <em>zero</em> PCM. The
/// engine accepted our container and refused to decode a <c>Format = 0x01</c> entry, which
/// is consistent with that path being dead code. MS-ADPCM is the format the engine
/// demonstrably plays that we can also write.</para>
/// <para><b>Why not Vorbis or HCA.</b> HCA is proprietary and unencodable here. Vorbis would
/// mean a new dependency for a codec the game uses less than ADPCM. MS-ADPCM is a
/// documented, fixed-coefficient scheme that fits in one file.</para>
/// <para>Mono only, deliberately: the game's own battle voice is mono 44.1 kHz, and so is
/// everything <c>ManagedVoiceSink</c> produces, so there is no resampling or downmixing on
/// the way in.</para>
/// </remarks>
public static class MsAdPcm
{
    /// <summary>Standard MS-ADPCM step-size adaptation table, indexed by the 4-bit code.</summary>
    private static readonly int[] Adaptation =
    [
        230, 230, 230, 230, 307, 409, 512, 614,
        768, 614, 512, 409, 307, 230, 230, 230,
    ];

    /// <summary>The seven standard predictor coefficient pairs, scaled by 256.</summary>
    private static readonly int[] CoefficientA = [256, 512, 0, 192, 240, 460, 392];

    private static readonly int[] CoefficientB = [0, -256, 0, 64, 0, -208, -232];

    /// <summary>Bytes of per-block header for a mono stream: predictor, delta, two samples.</summary>
    private const int MonoBlockHeader = 7;

    /// <summary>The smallest step size the format allows.</summary>
    private const int MinimumDelta = 16;

    public const int CoefficientCount = 7;

    /// <summary>Block size in bytes. 256 is the common choice for mono at 44.1 kHz.</summary>
    public const int DefaultBlockAlign = 256;

    /// <summary>How many decoded samples one block carries.</summary>
    /// <remarks>
    /// Two samples travel in the block header uncompressed; the remaining bytes hold two
    /// 4-bit codes each.
    /// </remarks>
    public static int SamplesPerBlock(int blockAlign)
        => ((blockAlign - MonoBlockHeader) * 2) + 2;

    /// <summary>
    /// Encodes mono 16-bit PCM. The final block is padded with its own last sample rather
    /// than with silence, so no click is introduced at the end.
    /// </summary>
    public static byte[] EncodeMono(ReadOnlySpan<short> pcm, int blockAlign = DefaultBlockAlign)
    {
        if (blockAlign <= MonoBlockHeader)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockAlign), blockAlign, $"must exceed the {MonoBlockHeader}-byte block header");
        }

        if (pcm.Length < 2)
        {
            return [];
        }

        var perBlock = SamplesPerBlock(blockAlign);
        var blocks = (pcm.Length + perBlock - 1) / perBlock;
        var output = new byte[blocks * blockAlign];

        var block = new short[perBlock];

        for (var b = 0; b < blocks; b++)
        {
            var start = b * perBlock;
            var available = Math.Min(perBlock, pcm.Length - start);

            pcm.Slice(start, available).CopyTo(block);
            for (var i = available; i < perBlock; i++)
            {
                block[i] = block[available - 1];
            }

            EncodeBlock(block, output.AsSpan(b * blockAlign, blockAlign));
        }

        return output;
    }

    /// <summary>
    /// Encodes one block, choosing whichever of the seven predictors reproduces it best.
    /// </summary>
    /// <remarks>
    /// Trying all seven costs seven passes over 500 samples and measurably improves quality
    /// on speech, where the fixed coefficients otherwise fit poorly.
    /// </remarks>
    private static void EncodeBlock(ReadOnlySpan<short> block, Span<byte> destination)
    {
        var bestPredictor = 0;
        var bestError = long.MaxValue;

        for (var predictor = 0; predictor < CoefficientCount; predictor++)
        {
            var error = EncodeWithPredictor(block, predictor, Span<byte>.Empty);
            if (error < bestError)
            {
                bestError = error;
                bestPredictor = predictor;
            }
        }

        EncodeWithPredictor(block, bestPredictor, destination);
    }

    /// <summary>
    /// Encodes with a fixed predictor, writing to <paramref name="destination"/> when it is
    /// non-empty and otherwise only accumulating the error, so predictor selection and the
    /// real encode share one implementation.
    /// </summary>
    private static long EncodeWithPredictor(ReadOnlySpan<short> block, int predictor, Span<byte> destination)
    {
        var coefA = CoefficientA[predictor];
        var coefB = CoefficientB[predictor];

        var delta = InitialDelta(block);
        var sample2 = block[0];
        var sample1 = block[1];

        var write = !destination.IsEmpty;
        if (write)
        {
            destination[0] = (byte)predictor;
            BinaryPrimitives.WriteInt16LittleEndian(destination[1..], (short)delta);
            BinaryPrimitives.WriteInt16LittleEndian(destination[3..], sample1);
            BinaryPrimitives.WriteInt16LittleEndian(destination[5..], sample2);
            destination[MonoBlockHeader..].Clear();
        }

        long totalError = 0;

        for (var i = 2; i < block.Length; i++)
        {
            var predicted = ((sample1 * coefA) + (sample2 * coefB)) / 256;
            var error = block[i] - predicted;

            // Four-bit two's complement: the quantised error must land in [-8, 7].
            var code = error / delta;
            if (error % delta != 0 && error < 0)
            {
                code--; // floor rather than truncate toward zero, as the decoder assumes
            }

            code = Math.Clamp(code, -8, 7);

            var reconstructed = Math.Clamp(predicted + (code * delta), short.MinValue, short.MaxValue);
            totalError += Math.Abs(block[i] - reconstructed);

            if (write)
            {
                var at = MonoBlockHeader + ((i - 2) / 2);
                var nibble = (byte)(code & 0x0F);
                destination[at] |= (i - 2) % 2 == 0 ? (byte)(nibble << 4) : nibble;
            }

            sample2 = sample1;
            sample1 = (short)reconstructed;

            delta = Adaptation[code & 0x0F] * delta / 256;
            if (delta < MinimumDelta)
            {
                delta = MinimumDelta;
            }
        }

        return totalError;
    }

    /// <summary>
    /// A starting step size derived from the block's own dynamics.
    /// </summary>
    /// <remarks>
    /// A fixed initial delta of 16 makes the adaptation climb for a dozen samples at the
    /// start of every block, which on loud material is audible as a click every 500 samples.
    /// The quantised error has to fit in [-8, 7], so a step of roughly one seventh of the
    /// typical sample-to-sample change is the right neighbourhood.
    /// </remarks>
    private static int InitialDelta(ReadOnlySpan<short> block)
    {
        long total = 0;
        for (var i = 1; i < block.Length; i++)
        {
            total += Math.Abs(block[i] - block[i - 1]);
        }

        var mean = block.Length > 1 ? total / (block.Length - 1) : 0;
        return (int)Math.Clamp(mean / 4, MinimumDelta, short.MaxValue);
    }

    /// <summary>
    /// The <c>WAVEFORMATEX</c>-shaped codec header an MS-ADPCM audio entry carries in its
    /// SubInfo block: 18 bytes of format plus 32 bytes of ADPCM extra, 50 total.
    /// </summary>
    /// <remarks>
    /// ⚠ Authored from the documented Microsoft layout rather than copied from a game file.
    /// The prediction to check is that real MS-ADPCM entries report <c>SubInfoSize = 0x32</c>
    /// (50). If they do not, dump one and copy it instead — templating from a real file has
    /// been the only reliable technique with this format.
    /// </remarks>
    public static byte[] BuildCodecHeader(int channels, int sampleRate, int blockAlign)
    {
        var samplesPerBlock = SamplesPerBlock(blockAlign);
        var averageBytesPerSecond = (int)((long)sampleRate * blockAlign / samplesPerBlock);

        var header = new byte[18 + 32];
        var span = header.AsSpan();

        BinaryPrimitives.WriteUInt16LittleEndian(span, 0x0002);            // WAVE_FORMAT_ADPCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)averageBytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], 4);           // bits per sample
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], 32);          // cbSize
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], (ushort)samplesPerBlock);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], CoefficientCount);

        for (var i = 0; i < CoefficientCount; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(22 + (i * 4))..], (short)CoefficientA[i]);
            BinaryPrimitives.WriteInt16LittleEndian(span[(24 + (i * 4))..], (short)CoefficientB[i]);
        }

        return header;
    }

    /// <summary>Decodes mono MS-ADPCM. Used to verify the encoder against itself.</summary>
    public static short[] DecodeMono(ReadOnlySpan<byte> adpcm, int blockAlign = DefaultBlockAlign)
    {
        var perBlock = SamplesPerBlock(blockAlign);
        var blocks = adpcm.Length / blockAlign;
        var output = new short[blocks * perBlock];
        var written = 0;

        for (var b = 0; b < blocks; b++)
        {
            var block = adpcm.Slice(b * blockAlign, blockAlign);

            var predictor = Math.Clamp((int)block[0], 0, CoefficientCount - 1);
            int delta = BinaryPrimitives.ReadInt16LittleEndian(block[1..]);
            var sample1 = BinaryPrimitives.ReadInt16LittleEndian(block[3..]);
            var sample2 = BinaryPrimitives.ReadInt16LittleEndian(block[5..]);

            var coefA = CoefficientA[predictor];
            var coefB = CoefficientB[predictor];

            output[written++] = sample2;
            output[written++] = sample1;

            for (var i = 2; i < perBlock; i++)
            {
                var at = MonoBlockHeader + ((i - 2) / 2);
                var packed = block[at];
                var nibble = (i - 2) % 2 == 0 ? packed >> 4 : packed & 0x0F;

                // Sign-extend the 4-bit code.
                var code = nibble > 7 ? nibble - 16 : nibble;

                var predicted = ((sample1 * coefA) + (sample2 * coefB)) / 256;
                var value = Math.Clamp(predicted + (code * delta), short.MinValue, short.MaxValue);

                sample2 = sample1;
                sample1 = (short)value;
                output[written++] = sample1;

                delta = Adaptation[nibble] * delta / 256;
                if (delta < MinimumDelta)
                {
                    delta = MinimumDelta;
                }
            }
        }

        return output;
    }
}
