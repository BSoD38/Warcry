using System.Collections.Generic;
using System.Diagnostics;
using Warcry.Detection;
using Warcry.Profiles;

namespace Warcry.Gating;

/// <summary>
/// Admission control: how often a voiceline is allowed through.
/// </summary>
/// <remarks>
/// <para>v1 implements the plan's stages 0 (content filter), 2 (per-caster cooldown) and
/// 5 (concurrency, which lives in the sink). Crowd scaling and the global token bucket
/// arrive with remote players — for a single caster the per-caster cooldown already is
/// the global one.</para>
/// <para>The cooldown is stamped only when a clip actually gets scheduled, not when the
/// event is admitted, so an unmapped action does not consume the window.</para>
/// </remarks>
public sealed class Throttle
{
    /// <summary>ActionCategory 1 — source-confirmed as auto-attack.</summary>
    private const ushort AutoAttackCategory = 1;

    private const int MaxTrackedCasters = 256;

    private readonly Configuration config;
    private readonly Dictionary<uint, long> lastPlayTicks = [];

    public Throttle(Configuration config) => this.config = config;

    public bool Admit(in CastEvent ev, in ActionKey action, out DropStage stage)
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

        // ---- stage 2: per-caster cooldown ----
        var cooldown = this.config.SelfCooldownSeconds;
        if (cooldown > 0f && this.lastPlayTicks.TryGetValue(ev.CasterEntityId, out var last))
        {
            var elapsed = (Stopwatch.GetTimestamp() - last) / (double)Stopwatch.Frequency;
            if (elapsed < cooldown)
            {
                stage = DropStage.Throttle;
                return false;
            }
        }

        stage = DropStage.None;
        return true;
    }

    /// <summary>Starts the cooldown. Call only once a clip has actually been scheduled.</summary>
    public void Mark(uint casterEntityId)
    {
        if (this.lastPlayTicks.Count > MaxTrackedCasters)
        {
            this.Reap();
        }

        this.lastPlayTicks[casterEntityId] = Stopwatch.GetTimestamp();
    }

    /// <summary>Drop entries older than a minute so the dictionary cannot grow unbounded.</summary>
    /// <remarks>
    /// The age sweep alone is not a bound. With more than <see cref="MaxTrackedCasters"/>
    /// casters all active inside the window it frees nothing, and then runs in full on every
    /// single <see cref="Mark"/> — an allocating scan per cast, growing as the crowd grows.
    /// So when the sweep comes up empty, evict the oldest entry outright: it is the one whose
    /// cooldown has least left to run, and dropping it costs at most one repeated line.
    /// </remarks>
    private void Reap()
    {
        var cutoff = Stopwatch.GetTimestamp() - (Stopwatch.Frequency * 60);
        var stale = new List<uint>();
        var oldestId = 0u;
        var oldestTicks = long.MaxValue;

        foreach (var (id, ticks) in this.lastPlayTicks)
        {
            if (ticks < cutoff)
            {
                stale.Add(id);
            }
            else if (ticks < oldestTicks)
            {
                oldestTicks = ticks;
                oldestId = id;
            }
        }

        if (stale.Count == 0)
        {
            if (oldestTicks != long.MaxValue)
            {
                this.lastPlayTicks.Remove(oldestId);
            }

            return;
        }

        foreach (var id in stale)
        {
            this.lastPlayTicks.Remove(id);
        }
    }

    public void Clear() => this.lastPlayTicks.Clear();
}
