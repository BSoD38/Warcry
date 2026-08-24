using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Warcry.Audio;

/// <summary>Why <see cref="PlaybackScheduler.Schedule"/> did not take a request.</summary>
/// <remarks>
/// Distinct from <c>DropStage</c> on purpose: the scheduler lives below the diagnostics
/// layer and must not depend on it. The caller maps these onto a stage.
/// </remarks>
public enum ScheduleRefusal : byte
{
    /// <summary>Dispatched, or queued for dispatch.</summary>
    None = 0,

    /// <summary>Dispatched immediately and every sink refused it.</summary>
    SinkRefused = 1,

    /// <summary>The cast bar had further to run than a line will be held for.</summary>
    TooFarOut = 2,
}

/// <summary>
/// Holds a voice back until the cast bar has actually finished.
/// </summary>
/// <remarks>
/// <para>ActionEffectHandler.Receive fires at snapshot, which precedes the client-side
/// cast bar completing by one slidecast window — measured at 0.40-0.46 s in game, and
/// latency-dependent rather than a game constant. The delay is therefore taken from
/// <c>CastEvent.CastRemaining</c> per event, not from a user-tuned constant.</para>
/// <para>This is a deliberate offset, NOT a throttle backlog. Admission (audience,
/// cooldown, concurrency) has already happened by the time something is scheduled;
/// the throttle's "drop, don't queue" rule still governs admission.</para>
/// <para>Dispatch is on the first frame at or after the target — <b>err late, never
/// early</b>, since earliness was the original complaint. ~16 ms of frame quantisation
/// is unavoidable at 60 fps.</para>
/// </remarks>
public sealed class PlaybackScheduler
{
    /// <summary>Anything further out than this is a bug, not a cast. Refuse it.</summary>
    /// <remarks>
    /// A backstop, and one nothing should reach: <c>CastEvent.CastRemaining</c> already
    /// caps itself at one slidecast window, so a delay of seconds means an offset was
    /// computed from a cast bar that is not this action's. Reaching it is a signal, which
    /// is why <see cref="TooFarOut"/> counts it rather than returning silently.
    /// </remarks>
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
    private readonly IVoiceSink sink;
    private readonly Action? onRefused;

    /// <param name="onRefused">
    /// Called when a <em>delayed</em> dispatch is refused by the sink. Immediate refusals
    /// are visible to the caller through <see cref="Schedule"/>'s return value; delayed
    /// ones happen frames later with nobody watching, and used to vanish — the Events row
    /// read "ok" for a line that never sounded.
    /// </param>
    public PlaybackScheduler(IVoiceSink sink, Action? onRefused = null)
    {
        this.sink = sink;
        this.onRefused = onRefused;
    }

    public int PendingCount => this.pending.Count;

    /// <summary>Requests actually accepted by a sink.</summary>
    public long Dispatched { get; private set; }

    /// <summary>Requests every sink refused at dispatch time.</summary>
    public long Refused { get; private set; }

    /// <summary>Requests whose cast bar ran past <see cref="MaxDelaySeconds"/>.</summary>
    /// <remarks>
    /// Its own counter so the Status tab's totals reconcile. This path used to return
    /// without counting anything, which left an event that was neither dispatched, nor
    /// refused, nor cancelled, nor pending — exactly the invisible drop the rest of the
    /// pipeline was rebuilt to eliminate.
    /// </remarks>
    public long TooFarOut { get; private set; }

    public long Cancelled { get; private set; }

    /// <summary>
    /// Play now, or after <paramref name="delaySeconds"/>. A delay of zero dispatches
    /// immediately rather than waiting a frame — instants must never be delayed.
    /// </summary>
    /// <param name="refusal">
    /// Why the request was not taken, or <see cref="ScheduleRefusal.None"/>. The caller
    /// needs this to attribute the drop: a delay past the ceiling is a scheduler policy
    /// decision, and reporting it as a sink refusal sent debugging to the wrong tab.
    /// </param>
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

    /// <summary>Called every frame on the game main thread.</summary>
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

    /// <summary>
    /// Drop everything pending. The caster can die, despawn or zone during the gap,
    /// and a voiceline arriving after a loading screen is worse than none.
    /// </summary>
    public void CancelAll()
    {
        this.Cancelled += this.pending.Count;
        this.pending.Clear();
    }
}
