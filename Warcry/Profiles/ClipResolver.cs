using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Warcry.Game;
using GameAction = Lumina.Excel.Sheets.Action;

namespace Warcry.Profiles;

/// <summary>
/// Turns "who cast what" into "which clip", with a fallback chain and weighted variants.
/// </summary>
/// <remarks>
/// <para>The key design point: fallback is per <b>(caster, action)</b>, not per caster.
/// A profile that matches the caster but has no rule for this action falls through to
/// the NEXT profile rather than stopping. So a hyper-specific profile covering only
/// Limit Breaks can coexist with a generic one covering everything else.</para>
/// <para>The chain — voiceId, voiceSlot, tribe, race, sex, wildcard, silence — is not
/// hand-coded. It is an emergent property of ProfileStore's specificity sort.</para>
/// </remarks>
public sealed class ClipResolver
{
    private readonly ProfileStore profiles;
    private readonly IDataManager data;

    private readonly Dictionary<uint, ActionKey> actionCache = [];

    /// <summary>Last clip played per (caster, rule), so a variant never repeats back to back.</summary>
    private readonly Dictionary<(CasterKey Caster, string RuleId), string> lastClip = [];

    /// <summary>
    /// Ceiling on remembered (caster, rule) pairs.
    /// </summary>
    /// <remarks>
    /// One entry per caster per rule they have triggered. Bounded at one caster in v1, but
    /// unbounded the moment remote players arrive — a raid night's worth of strangers would
    /// accumulate for the session with nothing but an explicit
    /// <see cref="ClearCaches"/> to reclaim it. Forgetting is cheap: the only cost is that
    /// one clip may repeat once.
    /// </remarks>
    private const int MaxRememberedPicks = 4096;

    private ulong rng = 0x243F6A8885A308D3;

    public ClipResolver(ProfileStore profiles, IDataManager data)
    {
        this.profiles = profiles;
        this.data = data;
    }

    /// <summary>Resolves the action id to its sheet attributes, memoised.</summary>
    public ActionKey GetActionKey(uint actionId)
    {
        if (this.actionCache.TryGetValue(actionId, out var cached))
        {
            return cached;
        }

        var sheet = this.data.GetExcelSheet<GameAction>();
        if (!sheet.TryGetRow(actionId, out var row))
        {
            // NOT memoised. A miss can be transient — a lookup before the data manager is
            // warm, say — and caching the empty placeholder would poison this action id
            // for the whole session: no category/job/cast rule would ever match it, the
            // auto-attack skip would misread category 0, and casts-only would drop it.
            return new ActionKey(actionId, string.Empty, 0, 0, 0f);
        }

        // English deliberately: the name is an identity here, not a label. Memoised,
        // so the sheet lookup happens once per action id, never per cast.
        var english = this.data.GetExcelSheet<GameAction>(Dalamud.Game.ClientLanguage.English);
        var name = english.TryGetRow(actionId, out var enRow)
            ? enRow.Name.ExtractText()
            : row.Name.ExtractText();

        var key = new ActionKey(
            actionId,
            name,
            (ushort)row.ActionCategory.RowId,
            row.ClassJob.RowId,
            row.Cast100ms / 10f);

        this.actionCache[actionId] = key;
        return key;
    }

    /// <summary>
    /// Null means silence, which is a valid answer. Pass a list to collect an
    /// explanation — leave it null on the hot path.
    /// </summary>
    public ResolvedClip? Resolve(in CasterKey caster, in ActionKey action, List<string>? trace = null)
    {
        foreach (var profile in this.profiles.Sorted)
        {
            if (!profile.Enabled)
            {
                trace?.Add($"skip '{profile.Name}': disabled");
                continue;
            }

            if (!profile.Match.Accepts(in caster))
            {
                trace?.Add($"skip '{profile.Name}': character does not match");
                continue;
            }

            foreach (var rule in profile.Rules)
            {
                if (!rule.Enabled)
                {
                    continue;
                }

                if (!rule.When.Accepts(in action))
                {
                    continue;
                }

                var clip = this.PickWeighted(rule, in caster);
                if (clip is null)
                {
                    trace?.Add($"'{profile.Name}' / rule '{rule.Label}': matched but has no usable clip");
                    continue;
                }

                var rate = this.RollRate(rule);
                trace?.Add(
                    $"MATCH '{profile.Name}' / rule '{rule.Label}' (specificity {rule.When.Specificity})" +
                    (Math.Abs(rate - 1f) > 0.001f ? $", pitch {rate:0.00}x" : string.Empty));
                return new ResolvedClip(profile, rule, clip, rate);
            }

            // Matched the caster but had nothing for this action — keep going.
            trace?.Add($"'{profile.Name}': matches you, but no rule covers this action");
        }

        trace?.Add("no profile produced a clip — silence");
        return null;
    }

    /// <summary>
    /// Advances the shared xorshift64* state. One generator, two scalings below — the
    /// advance-and-multiply lives here so a future change cannot be made in one call
    /// site and missed in the other.
    /// </summary>
    /// <remarks>xorshift64* rather than <c>Random.Shared</c>: no contention on the game
    /// thread, no allocation, and deterministic given the seed.</remarks>
    private ulong NextRaw()
    {
        this.rng ^= this.rng >> 12;
        this.rng ^= this.rng << 25;
        this.rng ^= this.rng >> 27;
        return this.rng * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>Uniform in [0, 1).</summary>
    private float NextUnit() => (this.NextRaw() >> 11) / (float)(1UL << 53);

    /// <summary>Uniform integer in [0, exclusiveMax).</summary>
    private int NextBelow(int exclusiveMax) => (int)(this.NextRaw() % (ulong)exclusiveMax);

    /// <summary>
    /// Base pitch plus a fresh uniform roll in the random spread, as a playback rate.
    /// </summary>
    private float RollRate(VoiceRule rule)
    {
        var semitones = rule.PitchSemitones;

        if (rule.PitchRandomSemitones > 0.001f)
        {
            semitones += ((this.NextUnit() * 2f) - 1f) * rule.PitchRandomSemitones;
        }

        return Math.Abs(semitones) < 0.001f ? 1f : Warcry.Clips.CachedClip.SemitonesToRate(semitones);
    }

    /// <summary>
    /// Cumulative-weight pick that avoids repeating the previous clip when the rule has
    /// two or more. This is the single highest-value anti-annoyance measure in the plugin.
    /// </summary>
    private ClipRef? PickWeighted(VoiceRule rule, in CasterKey caster)
    {
        if (rule.Clips.Count == 0)
        {
            return null;
        }

        if (rule.Clips.Count == 1)
        {
            // Recorded even though there is nothing to avoid yet: the moment a second
            // clip is added to the rule, "don't repeat what just played" must already
            // know what just played.
            this.Remember(in caster, rule.Id, rule.Clips[0].Hash);
            return rule.Clips[0];
        }

        this.lastClip.TryGetValue((caster, rule.Id), out var previous);

        var total = 0;
        foreach (var c in rule.Clips)
        {
            if (c.Hash != previous)
            {
                total += Math.Max(1, c.Weight);
            }
        }

        if (total <= 0)
        {
            // Every clip in the rule shares the previous pick's hash (duplicated entries).
            this.Remember(in caster, rule.Id, rule.Clips[0].Hash);
            return rule.Clips[0];
        }

        var roll = this.NextBelow(total);

        foreach (var c in rule.Clips)
        {
            if (c.Hash == previous)
            {
                continue;
            }

            roll -= Math.Max(1, c.Weight);
            if (roll < 0)
            {
                this.Remember(in caster, rule.Id, c.Hash);
                return c;
            }
        }

        // Unreachable while the roll is bounded by the summed weights, but if it is ever
        // reached the fallback still has to count as the previous pick.
        this.Remember(in caster, rule.Id, rule.Clips[0].Hash);
        return rule.Clips[0];
    }

    /// <summary>Records what just played for this (caster, rule), within a fixed bound.</summary>
    private void Remember(in CasterKey caster, string ruleId, string hash)
    {
        var key = (caster, ruleId);

        // Dropping the whole table is the right trade against tracking an eviction order
        // on the cast path. Overwriting an existing key is always free, so this only ever
        // trips when a genuinely new pair arrives at the ceiling.
        if (this.lastClip.Count >= MaxRememberedPicks && !this.lastClip.ContainsKey(key))
        {
            this.lastClip.Clear();
        }

        this.lastClip[key] = hash;
    }

    public void ClearCaches()
    {
        this.actionCache.Clear();
        this.lastClip.Clear();
    }
}
