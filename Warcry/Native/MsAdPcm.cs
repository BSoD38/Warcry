using System;
using System.Buffers.Binary;

namespace Warcry.Native;

// Microsoft ADPCM encoder — 4 bits per sample, fixed-coefficient, no dependencies.
// It is the only format the engine both plays and we can write: the game's SCDs carry
// MS-ADPCM and HCA and no PCM at all, the engine refuses to decode a Format = 0x01 entry,
// HCA is proprietary, and Vorbis would mean a new dependency.
// Mono only: the game's battle voice is mono 44.1 kHz and so is everything
// ManagedVoiceSink produces, so nothing is resampled or downmixed on the way in.
public static class MsAdPcm
{
    // Standard MS-ADPCM step-size adaptation table, indexed by the 4-bit code.
    private static readonly int[] Adaptation =
    [
        230, 230, 230, 230, 307, 409, 512, 614,
        768, 614, 512, 409, 307, 230, 230, 230,
    ];

    // The seven standard predictor coefficient pairs, scaled by 256.
    private static readonly int[] CoefficientA = [256, 512, 0, 192, 240, 460, 392];

    private static readonly int[] CoefficientB = [0, -256, 0, 64, 0, -208, -232];

    // Mono block header: predictor, delta, two samples.
    private const int MonoBlockHeader = 7;

    // The smallest step size the format allows.
    private const int MinimumDelta = 16;

    public const int CoefficientCount = 7;

    // Bytes. 256 is the common choice for mono at 44.1 kHz.
    public const int DefaultBlockAlign = 256;

    // Two samples travel in the block header uncompressed; the remaining bytes hold two
    // 4-bit codes each.
    public static int SamplesPerBlock(int blockAlign)
        => ((blockAlign - MonoBlockHeader) * 2) + 2;

    // The final block is padded with its own last sample rather than with silence, so no
    // click is introduced at the end.
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

    // Tries all seven predictors and keeps the best. Seven passes over 500 samples, and
    // worth it on speech, where the fixed coefficients otherwise fit poorly.
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

    // Writes to destination when it is non-empty and otherwise only accumulates the error,
    // so predictor selection and the real encode share one implementation.
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

    // A starting step size derived from the block's own dynamics. A fixed initial delta of
    // 16 makes the adaptation climb for a dozen samples at the start of every block, which
    // on loud material is audible as a click every 500 samples. The quantised error has to
    // fit in [-8, 7], so roughly one seventh of the typical sample-to-sample change is the
    // right neighbourhood.
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

    // The WAVEFORMATEX-shaped codec header an MS-ADPCM audio entry carries in its SubInfo
    // block: 18 bytes of format plus 32 bytes of ADPCM extra, 50 total. Authored from the
    // documented Microsoft layout rather than copied from a game file; the engine accepts
    // this 50-byte SubInfoSize = 0x32 shape (docs/native-spike.md). Re-check after a game
    // patch, like everything else on the native path.
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
}
