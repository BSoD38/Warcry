using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Warcry.Native;

/// <summary>
/// Builds a minimal single-entry <c>.scd</c> containing raw PCM, using one of the game's
/// own SCD files as a structural template.
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
/// <para><b>Unproven:</b> that the engine plays <c>Format = 0x01</c> (PCM) at all.
/// VFXEditor only ever emits Vorbis / MS-ADPCM / HCA. If PCM is rejected, MS-ADPCM is
/// the fallback and is still writable in managed code. That is the whole question the
/// spike exists to answer.</para>
/// </remarks>
public static class ScdWriter
{
    /// <summary>SscfWaveFormat.Pcm. The bet this spike is making.</summary>
    public const uint FormatPcm = 0x01;

    /// <summary>SscfWaveFormat.MsAdPcm — the fallback if PCM is rejected.</summary>
    public const uint FormatMsAdPcm = 0x0C;

    /// <summary>HCA. What the game's own battle voices use; we cannot encode it.</summary>
    public const uint FormatHca = 0x1A;

    /// <summary>
    /// The "no audio here" marker. A real battle-voice file is full of these.
    /// </summary>
    /// <remarks>
    /// <c>Vo_Battle_PC_ros_Ma_fr.scd</c> has 22 audio entries of which 6 — indices 6, 7, 8,
    /// 15, 16, 17 — are 32-byte stubs with <c>dataLength = 0</c> and this format tag. They
    /// are padding between banks and no group record references any of them, so they are
    /// not a source of silence; they are simply gaps in the numbering.
    /// </remarks>
    public const uint FormatEmpty = 0xFFFFFFFF;

    private const int HeaderSize = 0x30;
    private const int TableBlockOffset = 0x30;

    // Offsets within the 0x30 table block.
    private const int OffTable3Count = 0x30;
    private const int OffSoundCount = 0x32;
    private const int OffAudioCount = 0x34;
    private const int OffSoundTable = 0x38;
    private const int OffAudioTable = 0x3C;
    private const int OffTable3 = 0x40;
    private const int OffTable4 = 0x48;

    /// <summary>
    /// One encoded audio entry: the eight header fields plus the codec header the format
    /// needs.
    /// </summary>
    /// <remarks>
    /// Raw PCM needs no codec header, so the writer originally hardcoded
    /// <c>SubInfoSize = 0</c>. Every format the game actually uses does need one — the
    /// survey found MS-ADPCM and HCA and no PCM at all — so the payload carries its own.
    /// </remarks>
    public readonly record struct AudioPayload(
        uint Format,
        uint SampleRate,
        uint Channels,
        byte[] SubInfo,
        byte[] Data)
    {
        /// <summary>Raw 16-bit samples, no codec header.</summary>
        /// <remarks>
        /// Kept for the record. The engine loaded a file containing one of these and refused
        /// to decode it, and a survey of the game's own SCDs found no PCM entry anywhere, so
        /// <see cref="FormatPcm"/> looks like dead code in the engine.
        /// </remarks>
        public static AudioPayload Pcm16(ReadOnlySpan<short> pcm, uint sampleRate)
        {
            var data = new byte[pcm.Length * 2];
            for (var i = 0; i < pcm.Length; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), pcm[i]);
            }

            return new AudioPayload(FormatPcm, sampleRate, 1, [], data);
        }

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
        /// Needed by <see cref="PointAllAudioAtOneEntry"/>: group records reference audio by
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

        var sounds = ReadTable(span, soundTable, soundCount);
        var audio = ReadTable(span, audioTable, audioCount);
        var t3 = table3Count > 0 ? ReadTable(span, table3, table3Count) : [];

        // Entry sizes are inferred from the gap to the next entry. Sound entries are
        // variable-length in the real files, so this must not be assumed constant.
        var soundLen = soundCount > 1
            ? (int)(sounds[1] - sounds[0])
            : (int)(audio[0] - sounds[0]);

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

    private static uint[] ReadTable(ReadOnlySpan<byte> span, int offset, int count)
    {
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + (i * 4))..]);
        }

        return result;
    }

    /// <summary>
    /// Emits a one-sound, one-audio SCD carrying <paramref name="pcm"/> as raw
    /// 16-bit mono PCM, reusing the template's container and sound-entry bytes.
    /// </summary>
    public static byte[] BuildPcm(Template template, ReadOnlySpan<short> pcm, uint sampleRate, uint format = FormatPcm)
    {
        var audioBytes = pcm.Length * 2;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // ---- fixed header ----
        w.Write("SEDBSSCF"u8);
        w.Write(3u);                 // version
        w.Write((byte)0);
        w.Write((byte)4);            // matches the real files
        w.Write((ushort)HeaderSize);
        w.Write(0u);                 // file size, patched at the end
        Pad(w, TableBlockOffset);

        // ---- table block (0x30 .. 0x70) ----
        var soundTableOffset = 0x70;
        var audioTableOffset = soundTableOffset + 8;   // 1 entry + terminator slot
        var table3TableOffset = audioTableOffset + 8;
        var dataStart = table3TableOffset + 8;

        w.Write((ushort)(template.Table3Offsets.Length > 0 ? 1 : 0)); // 0x30 table3 count
        w.Write((ushort)1);                                          // 0x32 sound count
        w.Write((ushort)1);                                          // 0x34 audio count
        w.Write((ushort)0);                                          // 0x36 unknown
        w.Write((uint)soundTableOffset);                             // 0x38
        w.Write((uint)audioTableOffset);                             // 0x3C
        w.Write((uint)(template.Table3Offsets.Length > 0 ? table3TableOffset : 0)); // 0x40
        w.Write(0u);                                                 // 0x44
        w.Write(0u);                                                 // 0x48 table4
        Pad(w, soundTableOffset);

        // Offsets are back-patched once the entries are laid out.
        var soundEntryOffset = Align(dataStart, 16);
        var soundEntryLength = template.SoundEntry0Length;

        var table3EntryOffset = Align(soundEntryOffset + soundEntryLength, 16);
        var table3EntryLength = template.Table3Offsets.Length > 0 ? template.Table3Entry0Length : 0;

        var audioEntryOffset = Align(table3EntryOffset + table3EntryLength, 16);

        w.Write((uint)soundEntryOffset);
        w.Write(0u);
        Pad(w, audioTableOffset);
        w.Write((uint)audioEntryOffset);
        w.Write(0u);
        Pad(w, table3TableOffset);
        if (template.Table3Offsets.Length > 0)
        {
            w.Write((uint)table3EntryOffset);
        }

        w.Write(0u);

        // ---- entries copied verbatim from the template ----
        Pad(w, soundEntryOffset);
        w.Write(template.Bytes, template.SoundEntry0Offset, soundEntryLength);

        if (table3EntryLength > 0)
        {
            Pad(w, table3EntryOffset);
            var available = Math.Min(table3EntryLength, template.Bytes.Length - template.Table3Entry0Offset);
            w.Write(template.Bytes, template.Table3Entry0Offset, available);
        }

        // ---- our audio entry ----
        Pad(w, audioEntryOffset);
        w.Write((uint)audioBytes);   // 0x00 DataLength
        w.Write(1u);                 // 0x04 NumChannels (mono)
        w.Write(sampleRate);         // 0x08 SampleRate
        w.Write(format);             // 0x0C Format
        w.Write(0u);                 // 0x10 LoopStart
        w.Write(0u);                 // 0x14 LoopEnd
        w.Write(0u);                 // 0x18 SubInfoSize — raw PCM needs no codec header
        w.Write(0u);                 // 0x1C Flags

        foreach (var sample in pcm)
        {
            w.Write(sample);
        }

        w.Flush();

        var bytes = ms.ToArray();
        PatchSizeField(bytes);
        return bytes;
    }

    /// <summary>
    /// The field at 0x10 is NOT the file length. In the real template it reads 0x19CC0
    /// (105664) for a 105776-byte file — short by exactly 0x70, the offset where the
    /// table block begins. So it is "bytes from 0x70 to the end".
    /// </summary>
    private static void PatchSizeField(byte[] bytes)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10), (uint)(bytes.Length - 0x70));

    /// <summary>
    /// Overwrites audio entry 0 <em>in place</em>, leaving every other byte of a real game
    /// SCD untouched.
    /// </summary>
    /// <remarks>
    /// This is the sharpest test available. The container, offset tables, sound entries and
    /// layout entries are all byte-identical to a file the engine demonstrably accepts, so
    /// the only variable left is our audio-entry header and payload. It isolates
    /// "our container rebuild is wrong" from "our audio entry is wrong" — two different
    /// bugs that the from-scratch build conflates.
    /// </remarks>
    /// <summary>
    /// Overwrites <em>every</em> audio entry in a real SCD with the same payload.
    /// </summary>
    /// <remarks>
    /// A battle-voice SCD is a random pool — the engine picks one of its ~22 entries per
    /// play, which is why in-game grunts vary. Filling them all makes the outcome
    /// deterministic without needing to understand the layout/randomisation tables.
    /// Entries too small to hold a header are given a zero-length payload, so a pick
    /// landing on one is silent rather than corrupt.
    /// </remarks>
    public static byte[] SwapAllAudioInPlace(
        Template template,
        ReadOnlySpan<short> pcm,
        uint sampleRate,
        uint format,
        out string note)
    {
        var bytes = (byte[])template.Bytes.Clone();
        var filled = 0;
        var stubbed = 0;
        var smallest = int.MaxValue;

        for (var i = 0; i < template.AudioOffsets.Length; i++)
        {
            var start = (int)template.AudioOffsets[i];
            var end = i + 1 < template.AudioOffsets.Length
                ? (int)template.AudioOffsets[i + 1]
                : bytes.Length;

            var slot = end - start;
            if (slot < 32 || start + 32 > bytes.Length)
            {
                stubbed++;
                continue;
            }

            var maxData = Math.Min(slot - 32, bytes.Length - start - 32);
            var samples = Math.Min(pcm.Length, maxData / 2);

            WriteAudioHeader(bytes, start, samples * 2, sampleRate, format);

            var span = bytes.AsSpan();
            for (var s = 0; s < samples; s++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(span[(start + 32 + (s * 2))..], pcm[s]);
            }

            var tail = start + 32 + (samples * 2);
            if (tail < end)
            {
                span[tail..end].Clear();
            }

            if (samples > 0)
            {
                filled++;
                smallest = Math.Min(smallest, samples);
            }
            else
            {
                stubbed++;
            }
        }

        note = $"filled {filled} of {template.AudioOffsets.Length} audio entries " +
               $"({stubbed} too small to hold audio); shortest holds " +
               $"{(smallest == int.MaxValue ? 0 : smallest) * 1000.0 / sampleRate:0} ms";

        return bytes;
    }

    /// <summary>
    /// Forces the template down to a single sound and single audio entry, then gives that
    /// entry everything from its own offset to the end of the file.
    /// </summary>
    /// <remarks>
    /// <para>The battle-voice SCD is a random pool of ~22 grunts, which is why playback has
    /// been unpredictable. Setting the counts at 0x32/0x34 to 1 leaves exactly one
    /// candidate — ours — so selection becomes deterministic without having to understand
    /// the layout/randomisation tables at all.</para>
    /// <para>Audio entries are the last thing in the file, so once entries 1..n are
    /// unreferenced their space is free. That lifts the ~60 ms ceiling to well over a
    /// second, which finally makes the payload unmistakable against a voice grunt.</para>
    /// </remarks>
    public static byte[] ForceSingleAudioEntry(
        Template template,
        ReadOnlySpan<short> pcm,
        uint sampleRate,
        uint format,
        out string note)
    {
        var bytes = (byte[])template.Bytes.Clone();
        var span = bytes.AsSpan();

        // One sound, one audio entry.
        BinaryPrimitives.WriteUInt16LittleEndian(span[OffSoundCount..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[OffAudioCount..], 1);

        var start = (int)template.AudioOffsets[0];
        var available = bytes.Length - start - 32;
        var samples = Math.Min(pcm.Length, available / 2);

        WriteAudioHeader(bytes, start, samples * 2, sampleRate, format);

        for (var i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(start + 32 + (i * 2))..], pcm[i]);
        }

        var tail = start + 32 + (samples * 2);
        if (tail < bytes.Length)
        {
            span[tail..].Clear();
        }

        PatchSizeField(bytes);

        note = $"counts forced to 1 sound / 1 audio; entry 0 now holds {samples} samples " +
               $"({samples * 1000.0 / sampleRate:0} ms) using the freed space to end of file";

        return bytes;
    }

    /// <summary>
    /// Makes playback deterministic by pointing <em>every</em> audio index at one entry,
    /// then giving that entry the whole rest of the file.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this beats every other approach in here.</b> A battle-voice SCD does not
    /// pick a waveform at random by accident — it contains an explicit weighted-random table.
    /// Five group blocks each hold a list of 8-byte records
    /// <c>(cueIndex, audioIndex, cumulativeWeight, localIndex)</c>, and <c>PlaySound</c>'s
    /// <c>soundNumber</c> selects the <em>group</em>. Group 3 of the Hrothgar file holds eight
    /// records, which is why a single <c>soundNumber</c> still produces eight different
    /// grunts.</para>
    /// <para>You could rewrite those records. You do not need to: they reference audio by
    /// index, and the table this method rewrites is what resolves an index to an offset.
    /// Point all of them at one entry and every roll of the dice — in every group, at every
    /// weight — lands on our audio. The randomisation is left completely intact and simply
    /// has nothing left to choose between.</para>
    /// <para>It also disposes of the empty stub entries (<see cref="FormatEmpty"/>) for free,
    /// since their indices now resolve to real audio too.</para>
    /// <para>Audio entries are the last thing in the file, so the payload is not bounded by
    /// the original slot — everything from the first entry's offset onwards is ours.</para>
    /// </remarks>
    public static byte[] PointAllAudioAtOneEntry(
        Template template,
        ReadOnlySpan<short> pcm,
        uint sampleRate,
        uint format,
        out string note)
        => PointAudioAtOneEntry(template, null, pcm, sampleRate, format, out note);

    /// <summary>
    /// As above, but retargets only the audio indices in <paramref name="audioIndices"/>.
    /// </summary>
    /// <param name="audioIndices">Indices to redirect, or null for all of them.</param>
    /// <remarks>
    /// <para><b>Why you almost always want a subset.</b> A battle-voice file is not one pool
    /// of interchangeable grunts. Confirmed in game 2026-08-17, and matching the parsed
    /// group table exactly: <c>soundNumber</c> 1 is damage-taken, 2 is death, 0 and 3 are
    /// attack. Retargeting everything would make the player's voiceline fire every time they
    /// took a hit or died.</para>
    /// <para>Restricting the rewrite to the indices one group can reach leaves the other
    /// banks completely intact — the character still grunts normally when hurt and killed,
    /// while the action voice becomes ours.</para>
    /// <para>The clip is <em>appended</em> past the end of the original file rather than
    /// overwriting entry 0, so every byte the untouched indices depend on survives, and the
    /// payload has no length limit either way.</para>
    /// </remarks>
    public static byte[] PointAudioAtOneEntry(
        Template template,
        IReadOnlySet<int>? audioIndices,
        ReadOnlySpan<short> pcm,
        uint sampleRate,
        uint format,
        out string note)
        => PointAudioAtOneEntry(
            template,
            audioIndices,
            format == FormatMsAdPcm
                ? AudioPayload.MsAdPcmMono(pcm, sampleRate)
                : AudioPayload.Pcm16(pcm, sampleRate),
            out note);

    /// <summary>As above, taking an already-encoded payload with its own codec header.</summary>
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

    /// <summary>
    /// Retargets audio indices at another entry <em>that already exists in the file</em>,
    /// adding no audio of our own.
    /// </summary>
    /// <remarks>
    /// <para><b>The control that separates the container edit from the codec.</b> Every
    /// other authored mode changes two things at once: it rewrites the offset table
    /// <em>and</em> introduces bytes we encoded. If the result is silent, those are
    /// indistinguishable.</para>
    /// <para>This changes only the offset table. The audio it points at is the game's own
    /// HCA, which the engine demonstrably plays. So: hearing the wrong bank means the
    /// surgery and the redirect both work and the sole remaining suspect is our payload;
    /// hearing the normal bank, or nothing, means the file never reached the engine and the
    /// payload was never the problem.</para>
    /// </remarks>
    public static byte[] PointAudioAtExistingEntry(
        Template template,
        IReadOnlySet<int> audioIndices,
        int targetIndex,
        out string note)
    {
        var bytes = (byte[])template.Bytes.Clone();
        var span = bytes.AsSpan();
        var target = template.AudioOffsets[targetIndex];

        var retargeted = 0;
        foreach (var index in audioIndices)
        {
            if (index < 0 || index >= template.AudioOffsets.Length)
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                span[(template.AudioTableOffset + (index * 4))..], target);
            retargeted++;
        }

        note = $"{retargeted} audio indices retargeted at existing entry {targetIndex} " +
               $"(0x{target:X}); file length unchanged, not one byte of audio is ours";

        return bytes;
    }

    /// <summary>
    /// Appends a byte-for-byte copy of an existing audio entry past the end of the file and
    /// retargets indices at the copy.
    /// </summary>
    /// <remarks>
    /// <para><b>The middle rung.</b> Three things change between an untouched file and one
    /// carrying our clip: the offset table is rewritten, an entry is appended past the
    /// original end of file, and that entry holds audio we encoded. Testing all three at
    /// once is what has made every silent result unreadable.</para>
    /// <para><see cref="PointAudioAtExistingEntry"/> isolates the first. This isolates the
    /// second: the appended entry is the game's own HCA, copied verbatim, so if it plays
    /// then appending past EOF is sound and only the codec is left. If it does not, the
    /// engine will not follow an offset past the original file length and no encoder would
    /// ever have helped.</para>
    /// </remarks>
    public static byte[] AppendCopyOfEntry(
        Template template,
        IReadOnlySet<int> audioIndices,
        int sourceIndex,
        out string note)
    {
        var original = template.Bytes;
        var source = (int)template.AudioOffsets[sourceIndex];
        var span = original.AsSpan();

        var dataLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[source..]);
        var subInfoSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(source + 0x18)..]);
        var entryLength = 32 + subInfoSize + dataLength;

        if (source + entryLength > original.Length)
        {
            throw new InvalidOperationException(
                $"audio entry {sourceIndex} claims {entryLength} bytes but runs past the end of the file");
        }

        var appendAt = Align(original.Length, 16);
        var bytes = new byte[appendAt + entryLength];
        Array.Copy(original, bytes, original.Length);
        Array.Copy(original, source, bytes, appendAt, entryLength);

        var outSpan = bytes.AsSpan();
        var retargeted = 0;
        foreach (var index in audioIndices)
        {
            if (index < 0 || index >= template.AudioOffsets.Length)
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                outSpan[(template.AudioTableOffset + (index * 4))..], (uint)appendAt);
            retargeted++;
        }

        PatchSizeField(bytes);

        note = $"copied audio entry {sourceIndex} ({entryLength} bytes: 32 header + {subInfoSize} codec " +
               $"+ {dataLength} data) to 0x{appendAt:X}, past the original end of file; " +
               $"{retargeted} indices retargeted at the copy. The audio is the game's own.";

        return bytes;
    }

    private static void WriteAudioHeader(byte[] bytes, int start, int dataBytes, uint sampleRate, uint format)
    {
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x00)..], (uint)dataBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x04)..], 1u);          // mono
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x08)..], sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x0C)..], format);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x10)..], 0u);          // LoopStart
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x14)..], 0u);          // LoopEnd
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x18)..], 0u);          // SubInfoSize
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x1C)..], 0u);          // Flags
    }

    /// <param name="note">Human-readable description of what fitted.</param>
    public static byte[] SwapFirstAudioInPlace(
        Template template,
        ReadOnlySpan<short> pcm,
        uint sampleRate,
        uint format,
        out string note)
    {
        var bytes = (byte[])template.Bytes.Clone();

        var start = (int)template.AudioOffsets[0];
        var end = template.AudioOffsets.Length > 1
            ? (int)template.AudioOffsets[1]
            : bytes.Length;

        var slot = end - start;
        var maxDataBytes = slot - 32; // 32-byte header, SubInfoSize 0 so no codec header

        if (maxDataBytes <= 0)
        {
            note = $"audio entry 0 slot is only {slot} bytes; cannot fit a header";
            return bytes;
        }

        var samples = Math.Min(pcm.Length, maxDataBytes / 2);
        var dataBytes = samples * 2;

        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x00)..], (uint)dataBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x04)..], 1u);          // mono
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x08)..], sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x0C)..], format);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x10)..], 0u);          // LoopStart
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x14)..], 0u);          // LoopEnd
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x18)..], 0u);          // SubInfoSize
        BinaryPrimitives.WriteUInt32LittleEndian(span[(start + 0x1C)..], 0u);          // Flags

        for (var i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(start + 32 + (i * 2))..], pcm[i]);
        }

        // Zero whatever is left of the old HCA payload so no stale data is decoded.
        span[(start + 32 + dataBytes)..end].Clear();

        note = $"swapped audio entry 0 in place: slot {slot} bytes, wrote {samples} samples " +
               $"({samples * 1000.0 / sampleRate:0} ms), file length unchanged";

        return bytes;
    }

    private static int Align(int value, int alignment)
        => (value + alignment - 1) / alignment * alignment;

    private static void Pad(BinaryWriter w, int target)
    {
        while (w.BaseStream.Position < target)
        {
            w.Write((byte)0);
        }
    }

    /// <summary>Converts float samples in [-1, 1] to 16-bit PCM.</summary>
    public static short[] ToPcm16(ReadOnlySpan<float> samples)
    {
        var result = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var v = Math.Clamp(samples[i], -1f, 1f);
            result[i] = (short)(v * short.MaxValue);
        }

        return result;
    }
}
