using System;
using System.Collections.Generic;
using System.Diagnostics;
using Warcry.Detection;
using Warcry.Game;
using Warcry.Profiles;

namespace Warcry.Gating;

// Admission control, docs/PLAN.md 5.7. Crowd scaling and the token bucket never apply to
// your own lines — the mechanism exists to quieten the strangers around you.
// Cooldowns and tokens are stamped in Mark, once a clip is actually scheduled, so an
// unmapped action consumes nobody's window.
public sealed class Throttle
{
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

    // Deliberately does not refill: a readout must not advance the state the cast path is
    // metering itself against.
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

        // Peeked, not spent: an action with nothing mapped to it must not burn the burst a
        // mapped one was about to use.
        if (this.SpendsTokens(primary) && this.Peek() < 1d)
        {
            stage = DropStage.RateLimited;
            return false;
        }

        stage = DropStage.None;
        return true;
    }

    public float CooldownFor(AudienceBucket primary)
    {
        var cooldown = this.audience.CooldownFor(primary);
        return primary == AudienceBucket.Self ? cooldown : cooldown * this.crowd.CooldownScale;
    }

    // Call only once a clip is actually scheduled.
    public void Mark(uint casterEntityId, AudienceBucket primary)
    {
        // Drop the whole table at the ceiling, as ClipResolver.Remember does: maintaining
        // an eviction order on the cast path costs more than the worst case, which is one
        // early line per caster, once.
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

    // Refills by elapsed time and returns the balance, without spending. Lazy rather than
    // ticked per frame: a long quiet stretch costs one add instead of thousands.
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
