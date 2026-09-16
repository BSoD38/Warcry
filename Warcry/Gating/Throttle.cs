using System;
using System.Collections.Generic;
using System.Diagnostics;
using Warcry.Detection;
using Warcry.Game;
using Warcry.Profiles;

namespace Warcry.Gating;

/// <summary>
/// Admission control: how often a voiceline is allowed through.
/// </summary>
/// <remarks>
/// <para>The plan's five stages (docs/PLAN.md 5.7): 0 content filter, 2 per-caster
/// cooldown, 3 crowd scaling, 4 global token bucket, 5 concurrency — which lives in the
/// sink. Stage 1 (dedupe) is still unnecessary while <c>ActionEffectHandler.Receive</c> is
/// the only trigger source.</para>
/// <para>Stages 3 and 4 exist because of remote casters and only ever apply to them. Your
/// own lines are scaled by no crowd and spend no tokens: the point of the whole mechanism
/// is that a busy zone quietens the strangers around you, and it would be self-defeating
/// if it silenced you at the same time.</para>
/// <para>Cooldowns and tokens are stamped only when a clip actually gets scheduled, not
/// when the event is admitted, so an unmapped action does not consume anyone's window.</para>
/// </remarks>
public sealed class Throttle
{
    /// <summary>ActionCategory 1 — source-confirmed as auto-attack.</summary>
    private const ushort AutoAttackCategory = 1;

    private const int MaxTrackedCasters = 256;

    private readonly Configuration config;
    private readonly AudienceFilter audience;
    private readonly CrowdWatch crowd;
    private readonly Dictionary<uint, long> lastPlayTicks = [];

    private double tokens;
    private long tokensStampedAt;

    public Throttle(Configuration config, AudienceFilter audience, CrowdWatch crowd)
    {
        this.config = config;
        this.audience = audience;
        this.crowd = crowd;
        this.tokens = config.RateBurst;
        this.tokensStampedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Whole lines of burst currently available, for the UI. Deliberately does not refill:
    /// a readout must not advance the state the cast path is metering itself against.
    /// </summary>
    public int TokensAvailable
    {
        get
        {
            var elapsed = (Stopwatch.GetTimestamp() - this.tokensStampedAt) / (double)Stopwatch.Frequency;
            var refill = Math.Max(0.1f, this.config.RateRefillSeconds);
            return (int)Math.Min(this.config.RateBurst, this.tokens + (elapsed / refill));
        }
    }

    public bool Admit(in CastEvent ev, in ActionKey action, AudienceBucket primary, out DropStage stage)
    {
        // ---- stage 0: content filters, free ----
        if (this.config.SkipAutoAttacks && action.Category == AutoAttackCategory)
        {
            stage = DropStage.Throttle;
            return false;
        }

        if (this.config.MutedActionIds.Contains(action.ActionId))
        {
            stage = DropStage.Throttle;
            return false;
        }

        if (this.config.CastsOnly && action.CastSeconds <= 0f)
        {
            stage = DropStage.Throttle;
            return false;
        }

        // ---- stages 2 and 3: per-caster cooldown, stretched by the crowd ----
        var cooldown = this.CooldownFor(primary);
        if (cooldown > 0f && this.lastPlayTicks.TryGetValue(ev.CasterEntityId, out var last))
        {
            var elapsed = (Stopwatch.GetTimestamp() - last) / (double)Stopwatch.Frequency;
            if (elapsed < cooldown)
            {
                stage = DropStage.Throttle;
                return false;
            }
        }

        // ---- stage 4: global token bucket, everyone but you ----
        // Peeked, not spent. Spending happens in Mark, so an action with nothing mapped to
        // it cannot burn the burst that a mapped one was about to use.
        if (this.SpendsTokens(primary) && this.Peek() < 1d)
        {
            stage = DropStage.RateLimited;
            return false;
        }

        stage = DropStage.None;
        return true;
    }

    /// <summary>The cooldown this caster is actually held to, crowd scaling included.</summary>
    public float CooldownFor(AudienceBucket primary)
    {
        var cooldown = this.audience.CooldownFor(primary);

        // Never you. See the type remarks.
        return primary == AudienceBucket.Self ? cooldown : cooldown * this.crowd.CooldownScale;
    }

    /// <summary>Starts the cooldown and spends a token. Call only once a clip is scheduled.</summary>
    public void Mark(uint casterEntityId, AudienceBucket primary)
    {
        // Dropping the whole table at the ceiling, exactly as ClipResolver.Remember bounds
        // its own per-player map: an eviction order maintained on the cast path costs more
        // than the worst case here, which is one early line per caster, once. Overwriting
        // an existing caster is always free, so this only trips on a genuinely new one.
        if (this.lastPlayTicks.Count >= MaxTrackedCasters &&
            !this.lastPlayTicks.ContainsKey(casterEntityId))
        {
            this.lastPlayTicks.Clear();
        }

        this.lastPlayTicks[casterEntityId] = Stopwatch.GetTimestamp();

        if (this.SpendsTokens(primary))
        {
            this.tokens = Math.Max(0d, this.Peek() - 1d);
        }
    }

    private bool SpendsTokens(AudienceBucket primary)
        => this.config.LimitTotalRate && primary != AudienceBucket.Self;

    /// <summary>
    /// Refills by elapsed time and returns the balance, without spending.
    /// </summary>
    /// <remarks>
    /// Refill is lazy rather than ticked on the framework update: the bucket only matters
    /// at the moment something asks for it, so there is nothing to do per frame, and a
    /// long quiet stretch costs exactly one subtraction rather than thousands of adds.
    /// </remarks>
    private double Peek()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - this.tokensStampedAt) / (double)Stopwatch.Frequency;
        this.tokensStampedAt = now;

        var refill = Math.Max(0.1f, this.config.RateRefillSeconds);
        this.tokens = Math.Min(this.config.RateBurst, this.tokens + (elapsed / refill));
        return this.tokens;
    }

    public void Clear()
    {
        this.lastPlayTicks.Clear();
        this.tokens = this.config.RateBurst;
        this.tokensStampedAt = Stopwatch.GetTimestamp();
    }
}
