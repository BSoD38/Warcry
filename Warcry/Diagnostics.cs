using System;
using Warcry.Detection;
using Warcry.Game;

namespace Warcry;

// Which pipeline stage discarded an event.
public enum DropStage : byte
{
    None = 0,
    NotPc = 1,
    NotAction = 2,
    Audience = 3,
    Throttle = 4,
    NoClip = 5,

    // At the cap, muted, unavailable, or any native refusal in NativeOnly mode. The reason
    // is on CompositeVoiceSink.LastRefusal.
    SinkRefused = 6,
    Gate = 7,

    // Playback switched off. Its own stage so the Events tab does not report "ok" for a
    // line that never sounded.
    PlaybackOff = 8,

    // The cast bar had further to run than the scheduler will hold a line for. Not
    // SinkRefused: no sink is asked.
    TooFarOut = 9,

    // The global rate cap. Separate from Throttle: a cooldown drop means one person is
    // casting too often, a rate drop means the crowd is. Never applies to your own actions.
    RateLimited = 10,
}

public readonly struct DiagRow
{
    public readonly DateTime When;
    public readonly CastEvent Event;
    public readonly string CasterName;
    public readonly DropStage Drop;

    // None for anything that never reached the filter.
    public readonly AudienceBucket Audience;

    // Always a literal.
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

// Ring of recent pipeline decisions, backing the Events tab. Lock-free because both the
// writer (the ActionEffect detour) and the reader (ImGui Draw) run on the game main thread
// — Lane A in docs/PLAN.md 4. Revisit if a producer ever moves off it.
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

    // Newest first.
    public DiagRow At(int index)
    {
        var start = (this.next - 1 + Capacity) % Capacity;
        return this.rows[(start - index + Capacity * 2) % Capacity];
    }

    public long DropCount(DropStage stage) => this.dropCounts[(int)stage];

    // For a discard that happened after the event was already recorded.
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
