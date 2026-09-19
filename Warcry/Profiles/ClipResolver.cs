using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Warcry.Game;
using GameAction = Lumina.Excel.Sheets.Action;

namespace Warcry.Profiles;

// Turns "who cast what" into "which clip", with a fallback chain and weighted variants.
// Fallback is per (caster, action), not per caster: a profile that matches the caster but
// has no rule for this action falls through to the NEXT profile rather than stopping, so a
// profile covering only Limit Breaks can coexist with a generic one covering everything
// else.
// The chain — voiceId, voiceSlot, tribe, race, sex, wildcard, silence — is not hand-coded;
// it falls out of ProfileStore's specificity sort.
public sealed class ClipResolver
{
    private readonly ProfileStore profiles;
    private readonly IDataManager data;

    private readonly Dictionary<uint, ActionKey> actionCache = [];

    // Last clip played per (player, rule), so a variant never repeats back to back. Keyed
    // on the player rather than on CasterKey: two strangers who share a race and a voice
    // are still two people, and one shared no-repeat slot would make each of them sound
    // MORE repetitive than either alone.
    private readonly Dictionary<(ulong Player, string RuleId), string> lastClip = [];

    // One entry per player per rule they have triggered, so this grows with the crowd.
    // Forgetting is cheap: the only cost is that one clip may repeat once.
    private const int MaxRememberedPicks = 4096;

    private ulong rng = 0x243F6A8885A308D3;

    public ClipResolver(ProfileStore profiles, IDataManager data)
    {
        this.profiles = profiles;
        this.data = data;
    }

    // Memoised.
    public ActionKey GetActionKey(uint actionId)
    {
        if (this.actionCache.TryGetValue(actionId, out var cached))
        {
            return cached;
        }

        var sheet = this.data.GetExcelSheet<GameAction>();
        if (!sheet.TryGetRow(actionId, out var row))
        {
            // NOT memoised: a miss can be transient, and caching the empty placeholder
            // would poison this action id for the session — no category/job/cast rule would
            // match it, the auto-attack skip would misread category 0, and casts-only would
            // drop it.
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

    // Null means silence, which is a valid answer. trace collects an explanation; leave it
    // null on the hot path.
    public ResolvedClip? Resolve(in CasterIdentity who, in ActionKey action, List<string>? trace = null)
    {
        foreach (var profile in this.profiles.Sorted)
        {
            if (!profile.Enabled)
            {
                trace?.Add($"skip '{profile.Name}': disabled");
                continue;
            }

            if (!profile.Match.Accepts(in who))
            {
                // Which half missed is most of the trace's value: a profile aimed at your
                // party skipping a stranger works as asked, one skipping your party member
                // is a setup mistake. Free when trace is null — the null-conditional call
                // does not evaluate its argument.
                trace?.Add(profile.Match.Accepts(who.Voice)
                    ? $"skip '{profile.Name}': not aimed at {Audience.Describe(who.Audience)}"
                    : $"skip '{profile.Name}': character does not match");
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

                var clip = this.PickWeighted(rule, who.NameHash);
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

    // Advances the shared xorshift64* state. One generator, two scalings below, so a change
    // cannot be made at one call site and missed at the other.
    // xorshift64* rather than Random.Shared: no contention on the game thread, no
    // allocation, deterministic given the seed.
    private ulong NextRaw()
    {
        this.rng ^= this.rng >> 12;
        this.rng ^= this.rng << 25;
        this.rng ^= this.rng >> 27;
        return this.rng * 0x2545F4914F6CDD1DUL;
    }

    // Uniform in [0, 1).
    private float NextUnit() => (this.NextRaw() >> 11) / (float)(1UL << 53);

    // Uniform integer in [0, exclusiveMax).
    private int NextBelow(int exclusiveMax) => (int)(this.NextRaw() % (ulong)exclusiveMax);

    // Base pitch plus a fresh uniform roll in the random spread, as a playback rate.
    private float RollRate(VoiceRule rule)
    {
        var semitones = rule.PitchSemitones;

        if (rule.PitchRandomSemitones > 0.001f)
        {
            semitones += ((this.NextUnit() * 2f) - 1f) * rule.PitchRandomSemitones;
        }

        return Math.Abs(semitones) < 0.001f ? 1f : Warcry.Clips.CachedClip.SemitonesToRate(semitones);
    }

    // Cumulative-weight pick that avoids repeating the previous clip when the rule has two
    // or more.
    private ClipRef? PickWeighted(VoiceRule rule, ulong player)
    {
        if (rule.Clips.Count == 0)
        {
            return null;
        }

        if (rule.Clips.Count == 1)
        {
            // Recorded even though there is nothing to avoid yet: the moment a second clip
            // is added to the rule, "don't repeat what just played" must already know what
            // just played.
            this.Remember(player, rule.Id, rule.Clips[0].Hash);
            return rule.Clips[0];
        }

        this.lastClip.TryGetValue((player, rule.Id), out var previous);

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
            this.Remember(player, rule.Id, rule.Clips[0].Hash);
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
                this.Remember(player, rule.Id, c.Hash);
                return c;
            }
        }

        // Unreachable while the roll is bounded by the summed weights, but if it is ever
        // reached the fallback still has to count as the previous pick.
        this.Remember(player, rule.Id, rule.Clips[0].Hash);
        return rule.Clips[0];
    }

    // Records what just played for this (player, rule), within a fixed bound.
    private void Remember(ulong player, string ruleId, string hash)
    {
        var key = (player, ruleId);

        // Dropping the whole table beats tracking an eviction order on the cast path.
        // Overwriting an existing key is free, so this only trips on a genuinely new pair.
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
