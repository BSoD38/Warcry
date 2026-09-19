using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Warcry.Game;

namespace Warcry.Gating;

// Once-a-second answer to "how many people around me can I hear, and what are they
// playing". Two consumers, one scan: Throttle reads CooldownScale so a crowd stretches
// other people's cooldowns (docs/PLAN.md 5.7 stage 3), and Native.PackBuilder reads Jobs so
// the clips those people could trigger are warm before they cast one.
// The job set is the load-bearing half. Warmed resource handles cannot be evicted until the
// game exits, so the warm set is the jobs of the people actually around you and actually
// audible — never every job in the game, which would be an unbounded permanent cost.
// Framework thread only, and skipped entirely while the audience is self-only.
public sealed unsafe class CrowdWatch
{
    // docs/PLAN.md 5.7 budgets this scan at 1 Hz and under 120 microseconds.
    private const double IntervalSeconds = 1.0;

    private readonly Configuration config;
    private readonly IObjectTable objects;
    private readonly AudienceFilter audience;

    private readonly HashSet<uint> jobs = [];
    private readonly HashSet<uint> scratch = [];

    private double nextTick;
    private uint lastLocalJob = uint.MaxValue;

    public CrowdWatch(Configuration config, IObjectTable objects, AudienceFilter audience)
    {
        this.config = config;
        this.objects = objects;
        this.audience = audience;
    }

    // Excludes yourself. 0 while self-only.
    public int NearbyCount { get; private set; }

    // Jobs that can currently trigger a line: yours, plus every audible player's.
    public IReadOnlySet<uint> Jobs => this.jobs;

    // Bumped whenever Jobs changes, so consumers can skip work.
    public int Revision { get; private set; }

    // Multiplier on every non-Self cooldown. 1 until the crowd passes the soft limit.
    public float CooldownScale { get; private set; } = 1f;

    public void Update(double elapsed, uint localJobId)
    {
        // Your own job switch does not wait for the tick: the pack builder warms off this
        // set, and holding a switch for up to a second would let the first action on a new
        // job find its clip cold — an audible drop in native-only mode.
        var switched = localJobId != this.lastLocalJob;

        if (elapsed < this.nextTick && !switched)
        {
            return;
        }

        this.lastLocalJob = localJobId;
        this.nextTick = elapsed + IntervalSeconds;

        this.scratch.Clear();
        if (localJobId != 0)
        {
            this.scratch.Add(localJobId);
        }

        this.NearbyCount = 0;

        // Self-only is the default and stays free: no scan, no crowd scaling, and a job set
        // of exactly one.
        if (this.audience.HearsAnyoneElse)
        {
            this.Scan();
        }

        this.CooldownScale = this.ScaleFor(this.NearbyCount);
        this.Commit();
    }

    // Drops everything learned about the previous zone.
    public void Reset()
    {
        this.jobs.Clear();
        this.scratch.Clear();
        this.NearbyCount = 0;
        this.CooldownScale = 1f;
        this.nextTick = 0;
        this.lastLocalJob = uint.MaxValue;
        this.Revision++;
    }

    private void Scan()
    {
        var localEntityId = Plugin.PlayerState.EntityId;
        var limit = this.config.MaxDistanceYalms;

        foreach (var obj in this.objects.PlayerObjects)
        {
            if (obj.EntityId == localEntityId)
            {
                continue;
            }

            var chara = (Character*)obj.Address;
            if (chara == null)
            {
                continue;
            }

            // The same facts the detour reads, from the same fields, so a player counted
            // here is exactly a player who would be admitted if they cast right now.
            var facts = new CasterFacts(
                NameHash: PlayerId.Of(chara->GameObject.Name),
                HomeWorld: chara->HomeWorld,
                Distance: chara->CurrentDistance,
                IsPartyMember: chara->IsPartyMember,
                IsAllianceMember: chara->IsAllianceMember,
                IsFriend: chara->IsFriend,
                IsSelf: false);

            if (limit > 0 && facts.Distance > limit)
            {
                continue;
            }

            if (!this.audience.Admits(this.audience.MembershipOf(in facts)))
            {
                continue;
            }

            this.NearbyCount++;

            var job = (uint)chara->ClassJob;
            if (job != 0)
            {
                this.scratch.Add(job);
            }
        }
    }

    // Linear above the soft limit, capped. A 48-player alliance raid turns a 6 s stranger
    // cooldown into roughly 24 s while your own lines and your party's stay responsive.
    private float ScaleFor(int nearby)
    {
        if (!this.config.ScaleWithCrowd)
        {
            return 1f;
        }

        var limit = Math.Max(1, this.config.SoftCrowdLimit);
        if (nearby <= limit)
        {
            return 1f;
        }

        var scale = 1f + ((nearby - limit) / (float)limit);
        return Math.Min(scale, this.config.MaxCrowdScale);
    }

    // Publishes only when the set differs, so the pack builder is not asked to re-evaluate
    // reachability every second for an unchanged crowd.
    private void Commit()
    {
        if (this.scratch.SetEquals(this.jobs))
        {
            return;
        }

        this.jobs.Clear();
        foreach (var job in this.scratch)
        {
            this.jobs.Add(job);
        }

        this.Revision++;
    }
}
