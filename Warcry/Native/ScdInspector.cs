using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Warcry.Native;

/// <summary>One weighted-random choice inside a sound group.</summary>
/// <param name="CueIndex">Index into the sound-entry table.</param>
/// <param name="AudioIndex">Index into the audio-entry table — what actually gets played.</param>
/// <param name="CumulativeWeight">Running total; the engine rolls against the group's final value.</param>
/// <param name="LocalIndex">Position within the group, 0-based.</param>
public readonly record struct ScdGroupRecord(
    ushort CueIndex, ushort AudioIndex, ushort CumulativeWeight, ushort LocalIndex);

/// <summary>A sound group — what <c>PlaySound</c>'s <c>soundNumber</c> selects.</summary>
public sealed class ScdGroup
{
    public required int Id { get; init; }

    public required int Offset { get; init; }

    public required List<ScdGroupRecord> Records { get; init; }

    /// <summary>Total weight, i.e. the last record's cumulative value.</summary>
    public int TotalWeight => this.Records.Count == 0 ? 0 : this.Records[^1].CumulativeWeight;
}

/// <summary>
/// Explains how an SCD chooses what to play.
/// </summary>
/// <remarks>
/// <para><b>The structure, derived from <c>Vo_Battle_PC_ros_Ma_fr.scd</c> (2026-08-17).</b>
/// The count at 0x30 is the number of <em>sound groups</em>. The table at 0x40 points at
/// that many fixed-size group headers (128 bytes each), and the variable-length group
/// bodies follow contiguously after the last header, in the same order.</para>
/// <para>Each body is a 32-byte header — first byte is the record count, the u32 at +0x0C
/// is the group id — followed by that many 8-byte records of four <c>u16</c>:
/// cue index, audio index, cumulative weight, local index.</para>
/// <para><b>The consequence.</b> <c>soundNumber</c> selects the group, not the waveform.
/// The group then picks a record by weighted random. That is why one <c>soundNumber</c>
/// still yields several different grunts, and it is not something a caller can override.
/// See <see cref="ScdWriter.PointAudioAtOneEntry"/> for the way around it.</para>
/// </remarks>
public static class ScdInspector
{
    private const int GroupHeaderSize = 0x80;
    private const int GroupBodyHeaderSize = 0x20;
    private const int RecordSize = 8;

    /// <summary>A real group holds a handful of choices; 251 means we are misreading.</summary>
    private const int MaxPlausibleRecords = 64;

    /// <summary>
    /// Parses the group bodies. Returns an empty list rather than throwing on anything
    /// unexpected — this is a diagnostic, and a file that does not fit the model is itself
    /// the finding.
    /// </summary>
    public static List<ScdGroup> ParseGroups(ScdWriter.Template template, out string error)
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

        // Bodies begin immediately after the last fixed-size header. Inferred from
        // Vo_Battle_PC_ros_Ma_fr.scd and NOT general — see the validation below.
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

            // ---- validation ----
            // The layout above is a guess that happens to hold for battle-voice files. On a
            // 30-group monster SCD it produced ids like -16777216, 251-record groups and
            // negative weights — plausible-looking output that was pure garbage. Refuse
            // rather than report nonsense: a wrong index set here would also make
            // PointAudioAtOneEntry overwrite the wrong bank.
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
                    BinaryPrimitives.ReadUInt16LittleEndian(span[at..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 2)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 4)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 6)..]));

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

    /// <summary>Discards a partial parse. Half a wrong answer is worse than none.</summary>
    private static List<ScdGroup> Reject(List<ScdGroup> groups, ref string error)
    {
        groups.Clear();
        error += ". Group parsing abandoned — no groups are reported for this file.";
        return groups;
    }

    /// <summary>
    /// Offset in a group body of the float that governs how often the group fires.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Inferred, not confirmed.</b> Every group in a battle-voice file carries the same
    /// value here — 0.4335 — and the 128-byte group header blocks hold their volume-looking
    /// floats at 1.0, so this is not volume. A ~43% chance also matches the observed fact
    /// that the game does not grunt on every action.
    /// <para>Setting it to 1 in a container of our own is safe whatever it turns out to be:
    /// if the reading is right, playback becomes certain; if it is actually a volume, our
    /// clips get louder and the plugin volume slider compensates.</para>
    /// </remarks>
    private const int PlayChanceOffset = 0x08;

    /// <summary>
    /// Rewrites a cloned container so it plays every time it is asked to.
    /// </summary>
    /// <remarks>
    /// <para>A battle-voice file is authored to be intermittent — that is what makes a
    /// character grunt on some swings and not others. Cloning one for our own use inherits
    /// that, which presents as "the native path works, but only fires occasionally".</para>
    /// <para>Two independent sources of loss are removed: the per-group chance above, and
    /// the cumulative weights, which in the template sum to 30 rather than 100. Rescaling
    /// the running total to end at 100 is correct whether the engine rolls against the
    /// group's own total or against a fixed denominator, and it changes no offsets — only
    /// the <c>u16</c> already in each record.</para>
    /// </remarks>
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
