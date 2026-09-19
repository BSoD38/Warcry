using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Warcry.Audio;

// Distinct from DropStage on purpose: the scheduler sits below the diagnostics layer and
// must not depend on it. The caller maps these onto a stage.
public enum ScheduleRefusal : byte
{
    // Dispatched, or queued for dispatch.
    None = 0,

    // Dispatched immediately and every sink refused it.
    SinkRefused = 1,

    TooFarOut = 2,
}

// Holds a voice back until the cast bar has actually finished. ActionEffectHandler.Receive
// fires at snapshot, one slidecast window before the client-side cast bar completes, and
// that window is latency-dependent rather than a game constant — so the delay comes from
// CastEvent.CastRemaining per event, never from a tuned constant.
// A deliberate offset, not a throttle backlog: admission has already happened by the time
// anything is scheduled, and the throttle's "drop, don't queue" rule still governs it.
// Dispatch is on the first frame at or after the target — err late, never early. The ~16 ms
// of frame quantisation at 60 fps is unavoidable.
public sealed class PlaybackScheduler
{
    // A backstop nothing should reach: CastEvent.CastRemaining already caps itself at one
    // slidecast window, so a delay of seconds means the offset came from a cast bar that is
    // not this action's. TooFarOut counts it rather than returning silently.
    private const float MaxDelaySeconds = 5f;

    private readonly struct Pending
    {
        public readonly VoiceRequest Request;
        public readonly long DueTicks;

        public Pending(in VoiceRequest request, long dueTicks)
        {
            this.Request = request;
            this.DueTicks = dueTicks;
        }
    }

    private readonly List<Pending> pending = [];
    private readonly CompositeVoiceSink sink;
    private readonly Action? onRefused;

    // onRefused fires when a delayed dispatch is refused by the sink. Immediate refusals
    // reach the caller through Schedule's return value; delayed ones happen frames later
    // with nobody watching.
    public PlaybackScheduler(CompositeVoiceSink sink, Action? onRefused = null)
    {
        this.sink = sink;
        this.onRefused = onRefused;
    }

    public int PendingCount => this.pending.Count;

    public long Dispatched { get; private set; }

    public long Refused { get; private set; }

    // Its own counter so the Status tab's totals reconcile: every event is dispatched,
    // refused, cancelled, pending or here.
    public long TooFarOut { get; private set; }

    public long Cancelled { get; private set; }

    // A delay of zero dispatches immediately rather than waiting a frame — instants must
    // never be delayed. refusal lets the caller attribute the drop: a delay past the
    // ceiling is scheduler policy, not a sink refusal.
    public bool Schedule(in VoiceRequest request, float delaySeconds, out ScheduleRefusal refusal)
    {
        if (delaySeconds <= 0f)
        {
            if (this.sink.TryPlay(in request))
            {
                this.Dispatched++;
                refusal = ScheduleRefusal.None;
                return true;
            }

            this.Refused++;
            refusal = ScheduleRefusal.SinkRefused;
            return false;
        }

        if (delaySeconds > MaxDelaySeconds)
        {
            this.TooFarOut++;
            refusal = ScheduleRefusal.TooFarOut;
            return false;
        }

        var due = Stopwatch.GetTimestamp() + (long)(delaySeconds * Stopwatch.Frequency);
        this.pending.Add(new Pending(in request, due));
        refusal = ScheduleRefusal.None;
        return true;
    }

    // Game main thread, every frame.
    public void Update()
    {
        if (this.pending.Count == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();

        for (var i = this.pending.Count - 1; i >= 0; i--)
        {
            var item = this.pending[i];
            if (item.DueTicks > now)
            {
                continue;
            }

            this.pending.RemoveAt(i);

            if (this.sink.TryPlay(in item.Request))
            {
                this.Dispatched++;
            }
            else
            {
                this.Refused++;
                this.onRefused?.Invoke();
            }
        }
    }

    // The caster can die, despawn or zone during the gap, and a line arriving after a
    // loading screen is worse than none.
    public void CancelAll()
    {
        this.Cancelled += this.pending.Count;
        this.pending.Clear();
    }
}
