using System;
using Dalamud.Plugin.Services;

namespace Warcry.Audio;

/// <summary>
/// Prefers the native sink and falls back to the managed one, silently and per line.
/// </summary>
/// <remarks>
/// <para>The native path is real but young: it depends on Penumbra being loaded, on a
/// battle-voice container being readable, and on a per-clip warm-up that costs the first
/// play of each clip. None of those are worth dropping a voiceline over, and none are worth
/// a popup. Every one of them simply produces the managed sink instead.</para>
/// <para><b>Fallback is not failure.</b> The common case — a clip's first ever play, which
/// the native sink spends on loading the resource — is expected and permanent-per-clip, not
/// a fault. Only outright errors count toward demotion.</para>
/// <para>Demotion is sticky for the session. A native path that has thrown three times is
/// not going to start working because the fourth line arrived, and retrying it forever
/// costs a failed encode on every cast.</para>
/// </remarks>
public sealed class CompositeVoiceSink : IVoiceSink
{
    /// <summary>Errors tolerated before the native path is abandoned for the session.</summary>
    private const int StrikeLimit = 3;

    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly NativeVoiceSink native;
    private readonly ManagedVoiceSink managed;

    private int strikes;
    private bool demoted;

    public CompositeVoiceSink(
        IPluginLog log, Configuration config, NativeVoiceSink native, ManagedVoiceSink managed)
    {
        this.log = log;
        this.config = config;
        this.native = native;
        this.managed = managed;
    }

    /// <summary>Whether the native sink is currently the one being tried first.</summary>
    public bool NativeActive => this.config.PreferNativeSink && !this.demoted && this.native.Available;

    /// <summary>How many lines the native sink has actually played this session.</summary>
    public long NativePlays { get; private set; }

    /// <summary>How many fell through to the managed sink.</summary>
    public long ManagedPlays { get; private set; }

    /// <summary>Why the native sink last declined, so a fallback is explainable.</summary>
    public string NativeRefusal => this.native.LastRefusal;

    public string Name => this.NativeActive ? "Native + managed" : "Managed";

    public string Status
    {
        get
        {
            if (!this.config.PreferNativeSink)
            {
                return $"{this.managed.Status}  (native available but not enabled)";
            }

            if (this.demoted)
            {
                return $"{this.managed.Status}  (native demoted after {StrikeLimit} errors)";
            }

            return this.native.Available
                ? $"{this.native.Status}  — falls back to: {this.managed.Status}"
                : $"{this.managed.Status}  (native unavailable: {this.native.Status})";
        }
    }

    public bool Available => this.managed.Available || this.NativeActive;

    public int ActiveVoices => this.native.ActiveVoices + this.managed.ActiveVoices;

    public bool TryPlay(in VoiceRequest request)
    {
        if (this.NativeActive)
        {
            try
            {
                if (this.native.TryPlay(in request))
                {
                    this.NativePlays++;
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Only a thrown exception is a strike. A plain refusal is the sink saying
                // "not this line" — a cold clip, the concurrency cap, a muted gain — and
                // those are normal.
                this.strikes++;
                this.log.Error(ex, "Native sink threw ({Strikes}/{Limit})", this.strikes, StrikeLimit);

                if (this.strikes >= StrikeLimit)
                {
                    this.demoted = true;
                    this.log.Warning(
                        "Native sink demoted for this session after {Limit} errors. Managed audio continues.",
                        StrikeLimit);
                }
            }
        }

        if (!this.managed.TryPlay(in request))
        {
            return false;
        }

        this.ManagedPlays++;
        return true;
    }

    public void Update()
    {
        this.native.Update();
        this.managed.Update();
    }

    public void Dispose()
    {
        // Native first: it drops the Penumbra redirects, which must not outlive the plugin.
        this.native.Dispose();
        this.managed.Dispose();
    }
}
