using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Warcry.Native;

// One weighted-random choice inside a sound group. The 8-byte on-disk record is four u16 —
// cue index, audio index, cumulative weight, local index — of which only these two are read
// back. AudioIndex indexes the audio-entry table; the engine rolls against the group's
// final CumulativeWeight.
public readonly record struct ScdGroupRecord(ushort AudioIndex, ushort CumulativeWeight);

// A sound group — what PlaySound's soundNumber selects.
public sealed class ScdGroup
{
    public required int Id { get; init; }

    public required int Offset { get; init; }

    public required List<ScdGroupRecord> Records { get; init; }
}

// How an SCD chooses what to play.
//
// The count at 0x30 is the number of sound groups. The table at 0x40 points at that many
// fixed-size group headers (128 bytes each), and the variable-length group bodies follow
// contiguously after the last header, in the same order. Each body is a 32-byte header —
// first byte is the record count, the u32 at +0x0C is the group id — followed by that many
// 8-byte records of four u16: cue index, audio index, cumulative weight, local index.
//
// So soundNumber selects the group, not the waveform, and the group picks a record by
// weighted random: one soundNumber yields several different grunts, and a caller cannot
// override that. See ScdWriter.PointAudioAtOneEntry for the way around it.
public static class ScdInspector
{
    private const int GroupHeaderSize = 0x80;
    private const int GroupBodyHeaderSize = 0x20;
    private const int RecordSize = 8;

    // A real group holds a handful of choices; a bigger count means we are misreading.
    private const int MaxPlausibleRecords = 64;

    // Returns an empty list rather than throwing on anything unexpected: this is a
    // diagnostic, and a file that does not fit the model is itself the finding.
    private static List<ScdGroup> ParseGroups(ScdWriter.Template template, out string error)
    {
        var groups = new List<ScdGroup>();
        error = string.Empty;

        if (template.Table3Offsets.Length == 0)
        {
            error = "no group headers (the count at 0x30 is zero)";
            return groups;
        }

        var span = template.Bytes.AsSpan();
        var headerStride = template.Table3Offsets.Length > 1
            ? (int)(template.Table3Offsets[1] - template.Table3Offsets[0])
            : GroupHeaderSize;

        // Bodies begin immediately after the last fixed-size header. Holds for battle-voice
        // files and is NOT general — see the validation below.
        var cursor = (int)template.Table3Offsets[^1] + headerStride;

        for (var i = 0; i < template.Table3Offsets.Length; i++)
        {
            if (cursor + GroupBodyHeaderSize > template.Bytes.Length)
            {
                error = $"group {i} body starts past the end of the file (0x{cursor:X})";
                return Reject(groups, ref error);
            }

            int count = span[cursor];
            var id = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(cursor + 0x0C)..]);

            // The layout above holds for battle-voice files but not for every SCD; on other
            // files it yields impossible ids, record counts and weights. Refuse rather than
            // report nonsense — a wrong index set here would also make PointAudioAtOneEntry
            // overwrite the wrong bank.
            if (id != i)
            {
                error = $"group {i} reports id {id} — the body layout does not hold for this file";
                return Reject(groups, ref error);
            }

            if (count > MaxPlausibleRecords)
            {
                error = $"group {i} claims {count} records, past the plausible limit of {MaxPlausibleRecords}";
                return Reject(groups, ref error);
            }

            var recordsAt = cursor + GroupBodyHeaderSize;
            if (recordsAt + (count * RecordSize) > template.Bytes.Length)
            {
                error = $"group {i} claims {count} records, which runs past the end of the file";
                return Reject(groups, ref error);
            }

            var records = new List<ScdGroupRecord>(count);
            var previousWeight = 0;

            for (var r = 0; r < count; r++)
            {
                var at = recordsAt + (r * RecordSize);
                var record = new ScdGroupRecord(
                    BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 2)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 4)..]));

                if (record.AudioIndex >= template.AudioOffsets.Length)
                {
                    error = $"group {i} record {r} points at audio {record.AudioIndex}, " +
                            $"but the file has {template.AudioOffsets.Length} entries";
                    return Reject(groups, ref error);
                }

                if (record.CumulativeWeight < previousWeight)
                {
                    error = $"group {i} record {r} has a cumulative weight of " +
                            $"{record.CumulativeWeight} after {previousWeight} — not a running total";
                    return Reject(groups, ref error);
                }

                previousWeight = record.CumulativeWeight;
                records.Add(record);
            }

            groups.Add(new ScdGroup { Id = id, Offset = cursor, Records = records });
            cursor = recordsAt + (count * RecordSize);
        }

        return groups;
    }

    // Discards a partial parse. Half a wrong answer is worse than none.
    private static List<ScdGroup> Reject(List<ScdGroup> groups, ref string error)
    {
        groups.Clear();
        error += ". Group parsing abandoned — no groups are reported for this file.";
        return groups;
    }

    // ⚠ Inferred: offset in a group body of the float governing how often the group fires.
    // Every group in a battle-voice file holds 0.4335 here while the group headers'
    // volume-looking floats sit at 1.0, so it is not volume, and ~43% matches the game not
    // grunting on every action. Setting it to 1 in a container of our own is safe either
    // way: playback becomes certain, or the clip is louder and the volume slider
    // compensates.
    private const int PlayChanceOffset = 0x08;

    // A battle-voice file is authored to be intermittent, which is what makes a character
    // grunt on some swings and not others; a clone inherits that and presents as "native
    // works, but only fires occasionally". Two sources of loss are removed: the per-group
    // chance above, and the cumulative weights, which in the template sum to 30 rather than
    // 100. Rescaling the running total to end at 100 is correct whether the engine rolls
    // against the group's own total or a fixed denominator, and changes no offsets.
    public static int ForceDeterministicPlayback(byte[] scd, out string note)
    {
        if (!ScdWriter.TryParse(scd, out var template, out var parseError) || template is null)
        {
            note = $"not rewritten — {parseError}";
            return 0;
        }

        var groups = ParseGroups(template, out var groupError);
        if (groups.Count == 0)
        {
            note = $"not rewritten — no groups could be read ({groupError})";
            return 0;
        }

        var span = scd.AsSpan();
        var rewritten = 0;

        foreach (var group in groups)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span[(group.Offset + PlayChanceOffset)..], 1f);

            var recordsAt = group.Offset + GroupBodyHeaderSize;
            for (var i = 0; i < group.Records.Count; i++)
            {
                var cumulative = (ushort)Math.Round(100.0 * (i + 1) / group.Records.Count);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    span[(recordsAt + (i * RecordSize) + 4)..], cumulative);
            }

            rewritten++;
        }

        note = $"{rewritten} group(s) forced to always play, weights rescaled to total 100";
        return rewritten;
    }
}
