using System;
using System.Buffers.Binary;

namespace Warcry.Native;

// Parses a real game .scd and rewrites a clone of it to carry our audio, keeping the
// original as a structural template. The audio-entry header is eight plain u32 fields and
// is fully understood (docs/native-spike.md); the sound-entry and layout structures are
// not, and hand-authoring them blind gives a silent load failure with no diagnostic.
// Copying the container verbatim and substituting only the audio keeps every unknown field
// at a value the engine already accepts.
// The template comes from the user's own game install at runtime via
// IDataManager.GetFile, so no game data ships with the plugin and the structures always
// match the client version.
public static class ScdWriter
{
    // SscfWaveFormat.MsAdPcm. See MsAdPcm for why this format.
    public const uint FormatMsAdPcm = 0x0C;

    private const int HeaderSize = 0x30;

    // Offsets within the 0x30 table block.
    private const int OffTable3Count = 0x30;
    private const int OffSoundCount = 0x32;
    private const int OffAudioCount = 0x34;
    private const int OffAudioTable = 0x3C;
    private const int OffTable3 = 0x40;

    // One encoded audio entry: the eight header fields plus the codec header. Every format
    // the game uses needs one, so the payload carries its own.
    public readonly record struct AudioPayload(
        uint Format,
        uint SampleRate,
        uint Channels,
        byte[] SubInfo,
        byte[] Data)
    {
        public static AudioPayload MsAdPcmMono(
            ReadOnlySpan<short> pcm, uint sampleRate, int blockAlign = MsAdPcm.DefaultBlockAlign)
            => new(
                FormatMsAdPcm,
                sampleRate,
                1,
                MsAdPcm.BuildCodecHeader(1, (int)sampleRate, blockAlign),
                MsAdPcm.EncodeMono(pcm, blockAlign));

        // Header included.
        public int TotalLength => 32 + this.SubInfo.Length + this.Data.Length;
    }

    // Parsed shape of a game SCD, enough to lift one entry out of it.
    public sealed class Template
    {
        public required byte[] Bytes { get; init; }

        public required uint[] AudioOffsets { get; init; }

        public required uint[] Table3Offsets { get; init; }

        // Offset of the audio-entry offset table itself (the u32 array at 0x3C points here).
        // Group records reference audio by index, and this table turns an index into a file
        // offset, so rewriting it makes every index resolve wherever you like.
        public required int AudioTableOffset { get; init; }
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

        var audioTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[OffAudioTable..]);
        var table3 = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[OffTable3..]);

        if (soundCount < 1 || audioCount < 1)
        {
            error = $"template has no usable entries (sounds={soundCount}, audio={audioCount})";
            return false;
        }

        // Every offset below comes straight from the file, and this is reachable from the
        // cast path, so a truncated or hostile file must come back as false rather than an
        // ArgumentOutOfRangeException.
        if (!TryReadTable(span, audioTable, audioCount, out var audio))
        {
            error = "the audio-entry offset table lies outside the file";
            return false;
        }

        uint[] t3 = [];
        if (table3Count > 0 && !TryReadTable(span, table3, table3Count, out t3))
        {
            error = "the group-header table lies outside the file";
            return false;
        }

        foreach (var off in audio)
        {
            if (off >= (uint)scd.Length)
            {
                error = $"audio entry offset 0x{off:X} is past the end of the file";
                return false;
            }
        }

        template = new Template
        {
            Bytes = scd,
            AudioOffsets = audio,
            Table3Offsets = t3,
            AudioTableOffset = audioTable,
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

    // The field at 0x10 is NOT the file length but the bytes from 0x70 — where the table
    // block begins — to the end.
    private static void PatchSizeField(byte[] bytes)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10), (uint)(bytes.Length - 0x70));

    // Makes playback deterministic by pointing EVERY audio index at one appended entry. The
    // file's weighted-random table is left intact and simply has nothing left to choose
    // between, so soundNumber 0 cannot miss and there is no group arithmetic. Scoping would
    // only matter when shadowing a real Vo_Battle path, where the damage and death banks
    // must survive; this only ever writes our own synthetic path.
    // The payload is appended past the end of the original rather than overwriting an entry,
    // so every byte the untouched indices depend on survives and the payload has no length
    // limit.
    public static byte[] PointAudioAtOneEntry(Template template, AudioPayload payload)
    {
        var original = template.Bytes;
        var entryOffset = Align(original.Length, 16);

        var bytes = new byte[entryOffset + payload.TotalLength];
        Array.Copy(original, bytes, original.Length);

        var span = bytes.AsSpan();
        for (var i = 0; i < template.AudioOffsets.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                span[(template.AudioTableOffset + (i * 4))..], (uint)entryOffset);
        }

        WriteAudioEntry(bytes, entryOffset, payload);
        PatchSizeField(bytes);

        return bytes;
    }

    // The 32-byte entry header, its codec header, then the audio.
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
