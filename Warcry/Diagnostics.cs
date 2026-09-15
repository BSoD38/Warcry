using System;
using Warcry.Detection;
using Warcry.Game;

namespace Warcry;

/// <summary>Which pipeline stage discarded an event.</summary>
public enum DropStage : byte
{
    None = 0,
    NotPc = 1,
    NotAction = 2,
    Audience = 3,
    Throttle = 4,
    NoClip = 5,

    /// <summary>
    /// The sink refused the request — at the cap, muted, unavailable, or (in NativeOnly
    /// mode) any native refusal. The reason is on <c>CompositeVoiceSink.LastRefusal</c>.
    /// </summary>
    SinkRefused = 6,
    Gate = 7,

    /// <summary>
    /// Playback is switched off, so the event was detected and deliberately not played.
    /// </summary>
    /// <remarks>
    /// Its own stage because it is the single most common reason for "I hear nothing" and
    /// used to be indistinguishable from a bug: the early return recorded no drop at all,
    /// so the Events tab said "ok" for a line that never sounded.
    /// </remarks>
    PlaybackOff = 8,

    /// <summary>
    /// The cast bar had further to run than the scheduler will hold a line for, so the
    /// line was refused rather than fired into a fight that had moved on.
    /// </summary>
    /// <remarks>
    /// Its own stage rather than <see cref="SinkRefused"/>: no sink was ever asked. Folding
    /// the two together pointed anyone debugging a long cast at the audio path instead of
    /// at the delay ceiling.
    /// </remarks>
    TooFarOut = 9,

    /// <summary>
    /// Other people's lines were arriving faster than the global rate cap allows, so this
    /// one was dropped rather than queued.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Throttle"/> because the fix is different: a cooldown drop
    /// means one person is casting too often, a rate drop means the crowd as a whole is.
    /// Never applies to your own actions — you bypass the bucket.
    /// </remarks>
    RateLimited = 10,
}

public readonly struct DiagRow
{
    public readonly DateTime When;
    public readonly CastEvent Event;
    public readonly string CasterName;
    public readonly DropStage Drop;

    /// <summary>
    /// The narrowest audience tier this caster fell into, or <see cref="AudienceBucket.None"/>
    /// for anything that never reached the filter.
    /// </summary>
    public readonly AudienceBucket Audience;

    /// <summary>Why the audience filter refused them, or empty. Always a literal.</summary>
    public readonly string AudienceRefusal;

    public DiagRow(
        DateTime when,
        in CastEvent ev,
        string casterName,
        DropStage drop,
        AudienceBucket audience = AudienceBucket.None,
        string audienceRefusal = "")
    {
        this.When = when;
        this.Event = ev;
        this.CasterName = casterName;
        this.Drop = drop;
        this.Audience = audience;
        this.AudienceRefusal = audienceRefusal;
    }
}

/// <summary>
/// Fixed-capacity ring of recent pipeline decisions, backing the Events tab.
/// </summary>
/// <remarks>
/// Deliberately lock-free: both the writer (the ActionEffect detour) and the reader
/// (ImGui Draw) run on the game main thread — Lane A in docs/PLAN.md 4. If a producer
/// ever moves off that thread this needs revisiting.
/// </remarks>
public sealed class Diagnostics
{
    public const int Capacity = 200;

    private readonly DiagRow[] rows = new DiagRow[Capacity];
    private int next;

    public int Count { get; private set; }

    public long TotalSeen { get; private set; }

    private readonly long[] dropCounts = new long[Enum.GetValues<DropStage>().Length];

    public void Record(in DiagRow row)
    {
        this.rows[this.next] = row;
        this.next = (this.next + 1) % Capacity;
        if (this.Count < Capacity)
        {
            this.Count++;
        }

        this.TotalSeen++;
        this.dropCounts[(int)row.Drop]++;
    }

    /// <summary>Newest first.</summary>
    public DiagRow At(int index)
    {
        var start = (this.next - 1 + Capacity) % Capacity;
        return this.rows[(start - index + Capacity * 2) % Capacity];
    }

    public long DropCount(DropStage stage) => this.dropCounts[(int)stage];

    /// <summary>Counts a discard that happened after the event was already recorded.</summary>
    public void Drop(DropStage stage) => this.dropCounts[(int)stage]++;

    public void Clear()
    {
        Array.Clear(this.rows);
        Array.Clear(this.dropCounts);
        this.next = 0;
        this.Count = 0;
        this.TotalSeen = 0;
    }
}
