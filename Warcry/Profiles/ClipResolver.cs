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

        var key = new ActionKey(actionId, string.Empty, 0, 0, 0f);
        var sheet = this.data.GetExcelSheet<GameAction>();
        if (sheet.TryGetRow(actionId, out var row))
        {
            // English deliberately: the name is an identity here, not a label. Memoised,
            // so the sheet lookup happens once per action id, never per cast.
            var english = this.data.GetExcelSheet<GameAction>(Dalamud.Game.ClientLanguage.English);
            var name = english.TryGetRow(actionId, out var enRow)
                ? enRow.Name.ExtractText()
                : row.Name.ExtractText();

            key = new ActionKey(
                actionId,
                name,
                (ushort)row.ActionCategory.RowId,
                row.ClassJob.RowId,
                row.Cast100ms / 10f);
        }

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
    /// Base pitch plus a fresh uniform roll in the random spread, as a playback rate.
    /// </summary>
    private float RollRate(VoiceRule rule)
    {
        var semitones = rule.PitchSemitones;

        if (rule.PitchRandomSemitones > 0.001f)
        {
            // xorshift64* -> [-1, 1]
            this.rng ^= this.rng >> 12;
            this.rng ^= this.rng << 25;
            this.rng ^= this.rng >> 27;
            var unit = ((this.rng * 0x2545F4914F6CDD1DUL) >> 11) / (float)(1UL << 53);
            semitones += ((unit * 2f) - 1f) * rule.PitchRandomSemitones;
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
            return rule.Clips[0];
        }

        // xorshift64* — no Random.Shared contention on the game thread.
        this.rng ^= this.rng >> 12;
        this.rng ^= this.rng << 25;
        this.rng ^= this.rng >> 27;
        var roll = (int)((this.rng * 0x2545F4914F6CDD1DUL) % (ulong)total);

        foreach (var c in rule.Clips)
        {
            if (c.Hash == previous)
            {
                continue;
            }

            roll -= Math.Max(1, c.Weight);
            if (roll < 0)
            {
                this.lastClip[(caster, rule.Id)] = c.Hash;
                return c;
            }
        }

        return rule.Clips[0];
    }

    public void ClearCaches()
    {
        this.actionCache.Clear();
        this.lastClip.Clear();
    }
}
