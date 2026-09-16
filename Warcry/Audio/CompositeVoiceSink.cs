using System;
using Dalamud.Plugin.Services;
using Warcry.Game;

namespace Warcry.Audio;

/// <summary>
/// Routes each gameplay line to a sink according to the configured <see cref="SinkMode"/>.
/// </summary>
/// <remarks>
/// <para>The modes differ in what a native refusal means. In <see cref="SinkMode.Auto"/> it
/// is routine — a cold clip, a missing Penumbra — and the line falls through to NAudio. In
/// <see cref="SinkMode.NativeOnly"/> it is a visible drop: the refusal reason is kept on
/// <see cref="LastRefusal"/> and counted, and nothing substitutes. That is the mode's whole
/// contract — what you hear is always the game's engine, and silence always names itself.</para>
/// <para>The concurrency cap is enforced here, over the <em>sum</em> of both sinks' voices.
/// Each leaf also guards its own count for callers that use it directly, but only this sum
/// respects what the cap is for: the game's 256-entry SoundData pool and 5-track Voice bus
/// are global, so "max at once" must mean at once anywhere, not per sink.</para>
/// <para>Demotion is sticky for the session. A native path that has thrown three times is
/// not going to start working because the fourth line arrived, and retrying it forever
/// costs a failed encode on every cast. In NativeOnly, demotion means silence plus a
/// banner, never a quiet swap to NAudio.</para>
/// </remarks>
public sealed class CompositeVoiceSink : IDisposable
{
    /// <summary>Errors tolerated before the native path is abandoned for the session.</summary>
    private const int StrikeLimit = 3;

    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly NativeVoiceSink native;
    private readonly ManagedVoiceSink managed;

    private int strikes;

    public CompositeVoiceSink(
        IPluginLog log, Configuration config, NativeVoiceSink native, ManagedVoiceSink managed)
    {
        this.log = log;
        this.config = config;
        this.native = native;
        this.managed = managed;
    }

    /// <summary>The leaf auditioning must use, so a preview never depends on a compile.</summary>
    public ManagedVoiceSink Managed => this.managed;

    /// <summary>The native leaf, for the Sound pack tab's warm/refusal readouts.</summary>
    public NativeVoiceSink Native => this.native;

    /// <summary>Whether the native sink is currently the one lines are routed to first.</summary>
    public bool NativeActive =>
        this.config.Sink is SinkMode.Auto or SinkMode.NativeOnly
        && !this.Demoted
        && this.native.Available;

    /// <summary>Sticky for the session once the native sink has struck out.</summary>
    public bool Demoted { get; private set; }

    /// <summary>How many lines the native sink has actually played this session.</summary>
    public long NativePlays { get; private set; }

    /// <summary>How many were played by the managed sink.</summary>
    public long ManagedPlays { get; private set; }

    /// <summary>Why the last refused line did not play, or empty if the last line played.</summary>
    public string LastRefusal { get; private set; } = string.Empty;

    /// <summary>Why the native sink last declined, for the Status tab.</summary>
    public string NativeRefusal => this.native.LastRefusal;

    public string Status
    {
        get
        {
            switch (this.config.Sink)
            {
                case SinkMode.Off:
                    return "gameplay playback is off (sink mode Off)";

                case SinkMode.ManagedOnly:
                    return this.managed.Status;

                case SinkMode.NativeOnly:
                    if (this.Demoted)
                    {
                        return $"SILENT. The game engine path was given up on after {StrikeLimit} errors, and this mode does not fall back";
                    }

                    return this.native.Available
                        ? this.native.Status
                        : $"SILENT. Game engine unavailable: {this.native.Status}";

                default:
                    if (this.Demoted)
                    {
                        return $"{this.managed.Status}  (native demoted after {StrikeLimit} errors)";
                    }

                    return this.native.Available
                        ? $"{this.native.Status}, falling back to: {this.managed.Status}"
                        : $"{this.managed.Status}  (native unavailable: {this.native.Status})";
            }
        }
    }

    public bool Available => this.config.Sink switch
    {
        SinkMode.Off => false,
        SinkMode.ManagedOnly => this.managed.Available,
        SinkMode.NativeOnly => this.NativeActive,
        _ => this.managed.Available || this.NativeActive,
    };

    public int ActiveVoices => this.native.ActiveVoices + this.managed.ActiveVoices;

    public bool TryPlay(in VoiceRequest request)
    {
        var mode = this.config.Sink;

        if (mode == SinkMode.Off)
        {
            this.LastRefusal = "sink mode is Off";
            return false;
        }

        // One cap over both sinks. The leaves also check their own counts, but the pool
        // this protects is shared with the whole game, so the sum is what matters.
        //
        // The last voice is reserved for you (docs/PLAN.md 5.7 stage 5). Without it, a
        // crowd fills every slot and the one line anyone actually cares about — their own —
        // is the one refused. Only reserved while your own lines are switched on, and never
        // when the cap is 1, which would leave nobody else able to play at all.
        var reserve = this.config.MaxConcurrent > 1
                      && (this.config.Audience & AudienceBucket.Self) != 0
                      && !request.IsSelf;

        var cap = reserve ? this.config.MaxConcurrent - 1 : this.config.MaxConcurrent;

        if (this.ActiveVoices >= cap)
        {
            this.LastRefusal = reserve
                ? $"at the concurrency cap for other people ({cap} of {this.config.MaxConcurrent}; " +
                  "the last voice is kept free for your own lines)"
                : $"at the concurrency cap ({this.config.MaxConcurrent} at once, both sinks combined)";
            return false;
        }

        if (mode is SinkMode.Auto or SinkMode.NativeOnly && !this.Demoted && this.native.Available)
        {
            try
            {
                if (this.native.TryPlay(in request))
                {
                    this.NativePlays++;
                    this.LastRefusal = string.Empty;
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Only a thrown exception is a strike. A plain refusal is the sink saying
                // "not this line" — a cold clip, a zero gain — and those are normal.
                this.strikes++;
                this.log.Error(ex, "Native sink threw ({Strikes}/{Limit})", this.strikes, StrikeLimit);

                if (this.strikes >= StrikeLimit)
                {
                    this.Demoted = true;
                    this.log.Warning(
                        this.config.Sink == SinkMode.NativeOnly
                            ? "Native sink demoted for this session after {Limit} errors. Sink mode is NativeOnly, so playback is now silent."
                            : "Native sink demoted for this session after {Limit} errors. Managed audio continues.",
                        StrikeLimit);
                }
            }
        }

        if (mode == SinkMode.NativeOnly)
        {
            // Never NAudio in this mode. The refusal reason is the deliverable here:
            // silence that cannot explain itself is the failure mode this mode replaces.
            this.LastRefusal = this.Demoted
                ? $"native sink demoted after {StrikeLimit} errors (NativeOnly does not fall back)"
                : this.native.Available
                    ? $"native refused: {this.native.LastRefusal}"
                    : $"native unavailable: {this.native.Status}";
            return false;
        }

        if (this.managed.TryPlay(in request))
        {
            this.ManagedPlays++;
            this.LastRefusal = string.Empty;
            return true;
        }

        this.LastRefusal = $"managed sink refused: {this.managed.LastRefusal}";
        return false;
    }

    public void Update()
    {
        this.native.Update();
        this.managed.Update();
    }

    public void StopAll()
    {
        this.native.StopAll();
        this.managed.StopAll();
    }

    public void Dispose()
    {
        // Native first: it drops the Penumbra redirects, which must not outlive the plugin.
        this.native.Dispose();
        this.managed.Dispose();
    }
}
