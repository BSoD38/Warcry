using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Warcry.Native;

/// <summary>
/// Parses a real game <c>.scd</c> and rewrites a clone of it to carry our audio, keeping
/// the original file as a structural template.
/// </summary>
/// <remarks>
/// <para><b>Why template rather than author from scratch.</b> The audio-entry header is
/// eight plain u32 fields and is fully understood (see docs/native-spike.md). The
/// sound-entry and layout structures are NOT — hand-authoring them blind is how you get
/// a silent load failure with no diagnostic. Copying a real file's container and entry
/// bytes verbatim, and substituting only the audio, keeps every unknown field at a value
/// the engine already accepts.</para>
/// <para>The template is read from the user's own game install at runtime via
/// <c>IDataManager.GetFile</c>, so no game data is ever shipped with the plugin and the
/// structures always match their client version.</para>
/// </remarks>
public static class ScdWriter
{
    /// <summary>
    /// SscfWaveFormat.MsAdPcm — what we encode. A survey of the game's own SCDs found
    /// MS-ADPCM and HCA (which we cannot encode) and not one PCM entry.
    /// </summary>
    public const uint FormatMsAdPcm = 0x0C;

    private const int HeaderSize = 0x30;

    // Offsets within the 0x30 table block.
    private const int OffTable3Count = 0x30;
    private const int OffSoundCount = 0x32;
    private const int OffAudioCount = 0x34;
    private const int OffSoundTable = 0x38;
    private const int OffAudioTable = 0x3C;
    private const int OffTable3 = 0x40;

    /// <summary>
    /// One encoded audio entry: the eight header fields plus the codec header the format
    /// needs.
    /// </summary>
    /// <remarks>
    /// Every format the game actually uses needs a codec header, so the payload carries
    /// its own.
    /// </remarks>
    public readonly record struct AudioPayload(
        uint Format,
        uint SampleRate,
        uint Channels,
        byte[] SubInfo,
        byte[] Data)
    {
        /// <summary>Mono MS-ADPCM — the format the game demonstrably plays and we can write.</summary>
        public static AudioPayload MsAdPcmMono(
            ReadOnlySpan<short> pcm, uint sampleRate, int blockAlign = MsAdPcm.DefaultBlockAlign)
            => new(
                FormatMsAdPcm,
                sampleRate,
                1,
                MsAdPcm.BuildCodecHeader(1, (int)sampleRate, blockAlign),
                MsAdPcm.EncodeMono(pcm, blockAlign));

        /// <summary>Total bytes this entry occupies, header included.</summary>
        public int TotalLength => 32 + this.SubInfo.Length + this.Data.Length;

        public string Describe(uint sampleRate)
            => $"format 0x{this.Format:X2}, {this.Data.Length} bytes of audio, " +
               $"{this.SubInfo.Length}-byte codec header (SubInfoSize 0x{this.SubInfo.Length:X})";
    }

    /// <summary>Parsed shape of a game SCD, enough to lift one entry out of it.</summary>
    public sealed class Template
    {
        public required byte[] Bytes { get; init; }

        public required uint[] SoundOffsets { get; init; }

        public required uint[] AudioOffsets { get; init; }

        public required uint[] Table3Offsets { get; init; }

        /// <summary>Offset of the audio-entry offset table itself (the u32 array at 0x3C points here).</summary>
        /// <remarks>
        /// Needed by <see cref="PointAudioAtOneEntry"/>: group records reference audio by
        /// <em>index</em>, and this table is what turns an index into a file offset. Rewrite
        /// it and every index resolves wherever you like.
        /// </remarks>
        public required int AudioTableOffset { get; init; }

        /// <summary>Offset of the sound-entry offset table.</summary>
        public required int SoundTableOffset { get; init; }

        /// <summary>Byte range of sound entry 0, to be copied verbatim.</summary>
        public required int SoundEntry0Offset { get; init; }

        public required int SoundEntry0Length { get; init; }

        public required int Table3Entry0Offset { get; init; }

        public required int Table3Entry0Length { get; init; }
    }

    public static bool TryParse(byte[] scd, out Template? template, out string error)
    {
        template = null;

        if (scd.Length < 0x80)
        {
            error = "file too small to be an SCD";
            return false;
        }

        var span = scd.AsSpan();

        if (span[0] != 'S' || span[1] != 'E' || span[2] != 'D' || span[3] != 'B' ||
            span[4] != 'S' || span[5] != 'S' || span[6] != 'C' || span[7] != 'F')
        {
            error = "missing SEDBSSCF magic";
            return false;
        }

        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(span[0x0E..]);
        if (headerSize != HeaderSize)
        {
            error = $"unexpected header size 0x{headerSize:X} (expected 0x{HeaderSize:X})";
            return false;
        }

        var table3Count = BinaryPrimitives.ReadUInt16LittleEndian(span[OffTable3Count..]);
        var soundCount = BinaryPrimitives.ReadUInt16LittleEndian(span[OffSoundCount..]);
        var audioCount = BinaryPrimitives.ReadUInt16LittleEndian(span[OffAudioCount..]);

        var soundTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[OffSoundTable..]);
        var audioTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[OffAudioTable..]);
        var table3 = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[OffTable3..]);

        if (soundCount < 1 || audioCount < 1)
        {
            error = $"template has no usable entries (sounds={soundCount}, audio={audioCount})";
            return false;
        }

        // Every offset below comes straight from the file. This function is reachable from
        // the cast path (forge initialisation), so a truncated or hostile file must come
        // back as `false`, never as an ArgumentOutOfRangeException.
        if (!TryReadTable(span, soundTable, soundCount, out var sounds) ||
            !TryReadTable(span, audioTable, audioCount, out var audio))
        {
            error = "an entry-offset table lies outside the file";
            return false;
        }

        uint[] t3 = [];
        if (table3Count > 0 && !TryReadTable(span, table3, table3Count, out t3))
        {
            error = "the group-header table lies outside the file";
            return false;
        }

        foreach (var off in sounds)
        {
            if (off >= (uint)scd.Length)
            {
                error = $"sound entry offset 0x{off:X} is past the end of the file";
                return false;
            }
        }

        foreach (var off in audio)
        {
            if (off >= (uint)scd.Length)
            {
                error = $"audio entry offset 0x{off:X} is past the end of the file";
                return false;
            }
        }

        // Entry sizes are inferred from the gap to the next entry. Sound entries are
        // variable-length in the real files, so this must not be assumed constant.
        // Long arithmetic: these are file-supplied u32s, and a wrapped subtraction would
        // read as a huge positive length.
        var soundLen = soundCount > 1
            ? (int)((long)sounds[1] - sounds[0])
            : (int)((long)audio[0] - sounds[0]);

        var t3Len = 0;
        var t3Off = 0;
        if (t3.Length > 0)
        {
            t3Off = (int)t3[0];
            t3Len = t3.Length > 1 ? (int)(t3[1] - t3[0]) : 0x80;
        }

        if (soundLen <= 0 || sounds[0] + soundLen > scd.Length)
        {
            error = $"could not determine sound entry size (got {soundLen})";
            return false;
        }

        template = new Template
        {
            Bytes = scd,
            SoundOffsets = sounds,
            AudioOffsets = audio,
            Table3Offsets = t3,
            AudioTableOffset = audioTable,
            SoundTableOffset = soundTable,
            SoundEntry0Offset = (int)sounds[0],
            SoundEntry0Length = soundLen,
            Table3Entry0Offset = t3Off,
            Table3Entry0Length = t3Len,
        };

        error = string.Empty;
        return true;
    }

    private static bool TryReadTable(ReadOnlySpan<byte> span, int offset, int count, out uint[] result)
    {
        result = [];

        if (offset < 0 || count < 0 || (long)offset + ((long)count * 4) > span.Length)
        {
            return false;
        }

        var table = new uint[count];
        for (var i = 0; i < count; i++)
        {
            table[i] = BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + (i * 4))..]);
        }

        result = table;
        return true;
    }

    /// <summary>
    /// The field at 0x10 is NOT the file length. In the real template it reads 0x19CC0
    /// (105664) for a 105776-byte file — short by exactly 0x70, the offset where the
    /// table block begins. So it is "bytes from 0x70 to the end".
    /// </summary>
    private static void PatchSizeField(byte[] bytes)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10), (uint)(bytes.Length - 0x70));

    /// <summary>
    /// Makes playback deterministic by pointing audio indices at one appended entry.
    /// </summary>
    /// <param name="audioIndices">Indices to redirect, or null for all of them.</param>
    /// <remarks>
    /// <para>A battle-voice SCD does not pick a waveform at random by accident — it contains
    /// an explicit weighted-random table: group records reference audio by <em>index</em>,
    /// and the table this method rewrites is what resolves an index to an offset. Point the
    /// indices at one entry and every roll of the dice lands on our audio; the randomisation
    /// is left completely intact and simply has nothing left to choose between.</para>
    /// <para>The payload is <em>appended</em> past the end of the original file rather than
    /// overwriting an existing entry, so every byte the untouched indices depend on
    /// survives, and the payload has no length limit.</para>
    /// </remarks>
    public static byte[] PointAudioAtOneEntry(
        Template template,
        IReadOnlySet<int>? audioIndices,
        AudioPayload payload,
        out string note)
    {
        var original = template.Bytes;
        var entryOffset = Align(original.Length, 16);

        var bytes = new byte[entryOffset + payload.TotalLength];
        Array.Copy(original, bytes, original.Length);

        var span = bytes.AsSpan();
        var retargeted = 0;
        for (var i = 0; i < template.AudioOffsets.Length; i++)
        {
            if (audioIndices is not null && !audioIndices.Contains(i))
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                span[(template.AudioTableOffset + (i * 4))..], (uint)entryOffset);
            retargeted++;
        }

        WriteAudioEntry(bytes, entryOffset, payload);
        PatchSizeField(bytes);

        var scope = audioIndices is null
            ? $"all {template.AudioOffsets.Length}"
            : $"{retargeted} of {template.AudioOffsets.Length}";

        note = $"{scope} audio indices now resolve to a new entry appended at 0x{entryOffset:X}; " +
               $"{payload.Describe(payload.SampleRate)}; file {bytes.Length} bytes — " +
               "every original byte preserved";

        return bytes;
    }

    /// <summary>Writes the 32-byte entry header, its codec header, then the audio.</summary>
    private static void WriteAudioEntry(byte[] bytes, int start, AudioPayload payload)
    {
        var span = bytes.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x00)..], (uint)payload.Data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x04)..], payload.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x08)..], payload.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x0C)..], payload.Format);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x10)..], 0u); // LoopStart
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x14)..], 0u); // LoopEnd
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x18)..], (uint)payload.SubInfo.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x1C)..], 0u); // Flags

        payload.SubInfo.CopyTo(span[(start + 32)..]);
        payload.Data.CopyTo(span[(start + 32 + payload.SubInfo.Length)..]);
    }

    private static int Align(int value, int alignment)
        => (value + alignment - 1) / alignment * alignment;
}
