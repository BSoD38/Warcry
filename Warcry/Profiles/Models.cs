using System;
using System.Collections.Generic;
using Warcry.Game;

namespace Warcry.Profiles;

// What a rule is matched against: one action, resolved from the sheet. EnglishName is a
// language-independent identity — matching on the localized name would make every mapping
// client-specific and unshareable.
public readonly record struct ActionKey(
    uint ActionId, string EnglishName, ushort Category, uint ClassJob, float CastSeconds);

public sealed class ClipRef
{
    public string Hash { get; set; } = string.Empty;

    // Relative likelihood among the rule's clips.
    public int Weight { get; set; } = 1;

    public float Gain { get; set; } = 1f;
}

// Conditions on the action. All present fields must match; empty means "any".
public sealed class RuleWhen
{
    // The action family this rule covers: the id you assigned plus every id that currently
    // upgrades into it, captured via GetAdjustedActionId at assign time. A single id would
    // break under level sync, where you use the older form of a skill.
    public List<uint> ActionIds { get; set; } = [];

    // Catches duplicate sheet rows and variant/PvP copies that share a name but carry a
    // different id. English, so a mapping keeps working on a differently-localized client.
    public List<string> ActionNames { get; set; } = [];

    // ActionCategory rows. 1 = auto-attack, 2 = spell.
    public List<ushort> Categories { get; set; } = [];

    public List<uint> Jobs { get; set; } = [];

    public float? MinCastSeconds { get; set; }

    public float? MaxCastSeconds { get; set; }

    // Higher wins: naming an action beats a category, which beats a wildcard.
    public int Specificity =>
        (this.ActionIds.Count > 0 || this.ActionNames.Count > 0 ? 8 : 0) +
        (this.Jobs.Count > 0 ? 4 : 0) +
        (this.Categories.Count > 0 ? 2 : 0) +
        (this.MinCastSeconds.HasValue || this.MaxCastSeconds.HasValue ? 1 : 0);

    public bool TargetsAction => this.ActionIds.Count > 0 || this.ActionNames.Count > 0;

    public bool Accepts(in ActionKey action)
    {
        if (this.TargetsAction && !this.MatchesAction(in action))
        {
            return false;
        }

        if (this.Categories.Count > 0 && !this.Categories.Contains(action.Category))
        {
            return false;
        }

        if (this.Jobs.Count > 0 && !this.Jobs.Contains(action.ClassJob))
        {
            return false;
        }

        if (this.MinCastSeconds.HasValue && action.CastSeconds < this.MinCastSeconds.Value)
        {
            return false;
        }

        if (this.MaxCastSeconds.HasValue && action.CastSeconds > this.MaxCastSeconds.Value)
        {
            return false;
        }

        return true;
    }

    // Either half is enough: the id family covers upgrade chains under level sync, the
    // English name covers duplicate rows and variant copies.
    private bool MatchesAction(in ActionKey action)
    {
        if (this.ActionIds.Contains(action.ActionId))
        {
            return true;
        }

        foreach (var name in this.ActionNames)
        {
            if (string.Equals(name, action.EnglishName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class VoiceRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    // Shown in the UI. Usually the action's name.
    public string Label { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public RuleWhen When { get; set; } = new();

    public List<ClipRef> Clips { get; set; } = [];

    // Semitones, varispeed: the clip also gets longer going down and shorter going up, like
    // tape speed. 12 = one octave.
    public float PitchSemitones { get; set; }

    // Per-play random spread around PitchSemitones, in semitones. 0 disables it.
    public float PitchRandomSemitones { get; set; }

    public Warcry.Clips.PitchMode PitchMode { get; set; } = Warcry.Clips.PitchMode.Varispeed;

    // PreserveDuration only. Larger is smoother on sustained vowels but smears transients
    // further; smaller keeps the attack of a shout crisp at the cost of a grainier tail.
    // Powers of two only.
    public int PitchFftSize { get; set; } = 2048;
}

// Which characters this profile applies to, every field optional. Two independent halves:
// Audience and Names say WHO the caster is to you, the appearance fields say what they look
// and sound like. A profile can constrain either, both, or neither.
public sealed class ProfileMatch
{
    // Anyone means no constraint. Matched against the caster's full membership set, so a
    // profile aimed at your party fires for a party member who is also a friend.
    public AudienceBucket Audience { get; set; } = AudienceBucket.Anyone;

    // Specific players this profile is for; empty means anyone in Audience. Naming someone
    // here decides which clips they get, NOT whether they are heard — that is
    // Configuration.NamedPeople, and a profile naming someone not admitted there is silent.
    public List<NamedPlayer> Names { get; set; } = [];

    public List<byte> Sex { get; set; } = [];

    public List<byte> Race { get; set; } = [];

    public List<byte> Tribe { get; set; } = [];

    public List<byte> VoiceSlot { get; set; } = [];

    // Raw Character.Vfx.VoiceId. Never use alone — ARR races share ids (33/35/37/39 appear
    // for both Hyur-Midlander-M and Elezen-M). Pair with race+sex.
    public List<ushort> VoiceId { get; set; } = [];

    // Drives the fallback chain: profiles sort by this descending, so a voice-specific
    // profile is consulted before a race one, which is consulted before a wildcard. There is
    // no special-case code for the chain.
    // Who outranks what-they-sound-like, and the bands do not overlap: appearance tops out
    // at 18, well under the 32 a tier is worth, so adding a race can never promote a profile
    // past a narrower target.
    public int Specificity =>
        (this.Names.Count > 0 ? 128 : 0) +
        (Warcry.Game.Audience.TierOf(this.Audience) * 32) +
        (this.Sex.Count > 0 ? 1 : 0) +
        (this.Race.Count > 0 ? 2 : 0) +
        (this.Tribe.Count > 0 ? 3 : 0) +
        (this.VoiceSlot.Count > 0 ? 4 : 0) +
        (this.VoiceId.Count > 0 ? 8 : 0);

    // Does this profile constrain who the caster is, as opposed to how they look?
    public bool TargetsAudience
        => this.Names.Count > 0 || (this.Audience & AudienceBucket.Anyone) != AudienceBucket.Anyone;

    // Precomputed so matching costs no allocation on the cast path. Rebuilt by ProfileStore
    // on load and on every save, the only two moments Names can have changed. Kept off the
    // serialised surface: a stale copy in a shared profiles.json would be worse than none.
    private (ulong Name, uint World)[] nameKeys = [];

    // Call after any edit to Names.
    public void RebuildNameCache()
    {
        if (this.Names.Count == 0)
        {
            this.nameKeys = [];
            return;
        }

        var keys = new List<(ulong, uint)>(this.Names.Count);
        foreach (var entry in this.Names)
        {
            var hash = PlayerId.Of(entry?.Name);
            if (hash != 0)
            {
                keys.Add((hash, entry!.World));
            }
        }

        this.nameKeys = [.. keys];
    }

    // The full match: who they are to you, whether you named them, and what they sound like.
    public bool Accepts(in CasterIdentity who)
    {
        // Two sets of tiers; they only have to overlap. Anyone & anything is never empty, so
        // an untargeted profile cannot fail here.
        if ((this.Audience & who.Audience) == AudienceBucket.None)
        {
            return false;
        }

        if (this.nameKeys.Length > 0 && !this.NamesInclude(who.NameHash, who.HomeWorld))
        {
            return false;
        }

        return this.Accepts(who.Voice);
    }

    private bool NamesInclude(ulong nameHash, ushort homeWorld)
    {
        if (nameHash == 0)
        {
            return false;
        }

        foreach (var (name, world) in this.nameKeys)
        {
            if (name == nameHash && (world == 0 || world == homeWorld))
            {
                return true;
            }
        }

        return false;
    }

    public bool Accepts(in CasterKey key)
    {
        if (this.Sex.Count > 0 && !this.Sex.Contains(key.Sex))
        {
            return false;
        }

        if (this.Race.Count > 0 && !this.Race.Contains(key.Race))
        {
            return false;
        }

        if (this.Tribe.Count > 0 && !this.Tribe.Contains(key.Tribe))
        {
            return false;
        }

        if (this.VoiceSlot.Count > 0 && !this.VoiceSlot.Contains(key.VoiceSlot))
        {
            return false;
        }

        if (this.VoiceId.Count > 0 && !this.VoiceId.Contains(key.VoiceId))
        {
            return false;
        }

        return true;
    }

    public string Describe(Func<byte, byte, string> raceName, Func<byte, byte, string> tribeName)
    {
        var parts = new List<string>();

        // The target reads first: it is the coarser statement, and the one that decides
        // whether the profile can fire at all.
        if (this.Names.Count > 0)
        {
            parts.Add(this.Names.Count == 1
                ? this.Names[0].Describe()
                : $"{this.Names.Count} named players");
        }
        else if (this.TargetsAudience)
        {
            parts.Add(Warcry.Game.Audience.Describe(this.Audience));
        }

        if (this.Sex.Count > 0)
        {
            parts.Add(string.Join("/", this.Sex.ConvertAll(s => s == 0 ? "Male" : "Female")));
        }

        if (this.Race.Count > 0)
        {
            var sex = this.Sex.Count > 0 ? this.Sex[0] : (byte)0;
            parts.Add(string.Join("/", this.Race.ConvertAll(r => raceName(r, sex))));
        }

        if (this.Tribe.Count > 0)
        {
            var sex = this.Sex.Count > 0 ? this.Sex[0] : (byte)0;
            parts.Add(string.Join("/", this.Tribe.ConvertAll(t => tribeName(t, sex))));
        }

        if (this.VoiceSlot.Count > 0)
        {
            parts.Add("Voice " + string.Join("/", this.VoiceSlot));
        }

        if (this.VoiceId.Count > 0)
        {
            parts.Add("id " + string.Join("/", this.VoiceId));
        }

        return parts.Count == 0 ? "any character" : string.Join(" · ", parts);
    }
}

public sealed class VoiceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "New profile";

    public bool Enabled { get; set; } = true;

    // Tie-break only. Specificity always dominates.
    public int Priority { get; set; } = 100;

    public float Gain { get; set; } = 1f;

    public ProfileMatch Match { get; set; } = new();

    public List<VoiceRule> Rules { get; set; } = [];
}

// On-disk shape of profiles.json.
public sealed class ProfileDocument
{
    public int Schema { get; set; } = 1;

    public List<VoiceProfile> Profiles { get; set; } = [];
}

// What the resolver picked, from where, and how to play it.
public readonly record struct ResolvedClip(VoiceProfile Profile, VoiceRule Rule, ClipRef Clip, float Rate)
{
    public float CombinedGain => this.Profile.Gain * this.Clip.Gain;
}
