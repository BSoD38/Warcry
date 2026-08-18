using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Warcry.Audio;

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

    public long Cancelled { get; private set; }

    /// <summary>
    /// Play now, or after <paramref name="delaySeconds"/>. A delay of zero dispatches
    /// immediately rather than waiting a frame — instants must never be delayed.
    /// </summary>
    public bool Schedule(in VoiceRequest request, float delaySeconds)
    {
        if (delaySeconds <= 0f)
        {
            if (this.sink.TryPlay(in request))
            {
                this.Dispatched++;
                return true;
            }

            this.Refused++;
            return false;
        }

        if (delaySeconds > MaxDelaySeconds)
        {
            return false;
        }

        var due = Stopwatch.GetTimestamp() + (long)(delaySeconds * Stopwatch.Frequency);
        this.pending.Add(new Pending(in request, due));
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
