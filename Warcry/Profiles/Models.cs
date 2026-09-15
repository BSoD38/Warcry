using System;
using System.Collections.Generic;
using Warcry.Game;

namespace Warcry.Profiles;

/// <summary>What a rule is matched against: one action, resolved from the sheet.</summary>
/// <param name="EnglishName">
/// Canonical, language-independent identity. Matching on the localized name would make
/// every mapping client-specific and unshareable.
/// </param>
public readonly record struct ActionKey(
    uint ActionId, string EnglishName, ushort Category, uint ClassJob, float CastSeconds);

/// <summary>One clip choice within a rule.</summary>
public sealed class ClipRef
{
    public string Hash { get; set; } = string.Empty;

    /// <summary>Relative likelihood among the rule's clips.</summary>
    public int Weight { get; set; } = 1;

    public float Gain { get; set; } = 1f;
}

/// <summary>Conditions on the action. All present fields must match; empty means "any".</summary>
public sealed class RuleWhen
{
    /// <summary>
    /// The action family this rule covers — the id you assigned plus every id that
    /// currently upgrades into it, captured via <c>GetAdjustedActionId</c> at assign
    /// time. Mapping a single id would break under level sync, where you use the older
    /// form of a skill.
    /// </summary>
    public List<uint> ActionIds { get; set; } = [];

    /// <summary>
    /// English action names. Catches duplicate sheet rows and variant/PvP copies that
    /// share a name but carry a different id. Stored in English so a mapping does not
    /// stop working on a differently-localized client.
    /// </summary>
    public List<string> ActionNames { get; set; } = [];

    /// <summary>ActionCategory rows. 1 = auto-attack, 2 = spell (both source-confirmed).</summary>
    public List<ushort> Categories { get; set; } = [];

    public List<uint> Jobs { get; set; } = [];

    public float? MinCastSeconds { get; set; }

    public float? MaxCastSeconds { get; set; }

    /// <summary>Higher wins. Naming a specific action beats a category, which beats a wildcard.</summary>
    public int Specificity =>
        (this.ActionIds.Count > 0 || this.ActionNames.Count > 0 ? 8 : 0) +
        (this.Jobs.Count > 0 ? 4 : 0) +
        (this.Categories.Count > 0 ? 2 : 0) +
        (this.MinCastSeconds.HasValue || this.MaxCastSeconds.HasValue ? 1 : 0);

    /// <summary>Does this rule target a specific action at all?</summary>
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

    /// <summary>
    /// Either half is enough: the id family covers upgrade chains under level sync, the
    /// English name covers duplicate rows and variant copies.
    /// </summary>
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

    /// <summary>Shown in the UI. Usually the action's name.</summary>
    public string Label { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public RuleWhen When { get; set; } = new();

    public List<ClipRef> Clips { get; set; } = [];

    /// <summary>
    /// Base pitch offset in semitones. Varispeed — the clip also gets longer as it goes
    /// down and shorter as it goes up, like tape speed. 12 = one octave.
    /// </summary>
    public float PitchSemitones { get; set; }

    /// <summary>
    /// Per-play random spread around <see cref="PitchSemitones"/>, in semitones.
    /// 0 disables it. Even a small amount stops repeated casts sounding identical.
    /// </summary>
    public float PitchRandomSemitones { get; set; }

    /// <summary>Varispeed (duration follows pitch) or phase vocoder (duration held).</summary>
    public Warcry.Clips.PitchMode PitchMode { get; set; } = Warcry.Clips.PitchMode.Varispeed;

    /// <summary>
    /// Phase-vocoder window, PreserveDuration only. Larger is smoother on sustained
    /// vowels but smears transients further; smaller keeps the attack of a shout crisp
    /// at the cost of a grainier tail. Powers of two only.
    /// </summary>
    public int PitchFftSize { get; set; } = 2048;
}

/// <summary>Which characters this profile applies to. Every field optional.</summary>
/// <remarks>
/// Two independent halves. <see cref="Audience"/> and <see cref="Names"/> say WHO the
/// caster is to you; the appearance fields say what they look and sound like. A profile
/// can constrain either, both, or neither.
/// </remarks>
public sealed class ProfileMatch
{
    /// <summary>
    /// Which audience tiers this profile covers.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="AudienceBucket.Anyone"/> — no constraint — which is what
    /// makes every profile written before targets existed carry on behaving identically.
    /// Matched against the caster's full membership set, so a profile aimed at your party
    /// fires for a party member who also happens to be a friend.
    /// </remarks>
    public AudienceBucket Audience { get; set; } = AudienceBucket.Anyone;

    /// <summary>
    /// Specific players this profile is for. Empty means anyone in <see cref="Audience"/>.
    /// </summary>
    /// <remarks>
    /// Naming someone here decides which clips they get, NOT whether they are heard —
    /// that is <c>Configuration.NamedPeople</c>, and a profile naming someone who is not
    /// admitted there is silent. The People tab points that out rather than leaving it to
    /// be discovered.
    /// </remarks>
    public List<NamedPlayer> Names { get; set; } = [];

    public List<byte> Sex { get; set; } = [];

    public List<byte> Race { get; set; } = [];

    public List<byte> Tribe { get; set; } = [];

    public List<byte> VoiceSlot { get; set; } = [];

    /// <summary>
    /// Raw Character.Vfx.VoiceId. Never use alone — ARR races share ids with each other
    /// (33/35/37/39 appear for both Hyur-Midlander-M and Elezen-M). Pair with race+sex.
    /// </summary>
    public List<ushort> VoiceId { get; set; } = [];

    /// <summary>
    /// Drives the fallback chain. Profiles sort by this descending, so a voice-specific
    /// profile is consulted before a race one, which is consulted before a wildcard.
    /// The chain is an emergent property of this sort — there is no special-case code.
    /// </summary>
    /// <remarks>
    /// <para>Who outranks what-they-sound-like: a profile naming one player beats one aimed
    /// at a tier, which beats one aimed at everybody, and appearance only decides between
    /// profiles aimed at the same audience. The multipliers keep those bands from
    /// overlapping — appearance tops out at 18, well under the 32 a tier is worth — so
    /// adding a race to a profile can never promote it past a narrower target.</para>
    /// <para>See <see cref="Warcry.Game.Audience.TierOf"/> for why a tier's rank is that of
    /// its WIDEST member.</para>
    /// </remarks>
    public int Specificity =>
        (this.Names.Count > 0 ? 128 : 0) +
        (Warcry.Game.Audience.TierOf(this.Audience) * 32) +
        (this.Sex.Count > 0 ? 1 : 0) +
        (this.Race.Count > 0 ? 2 : 0) +
        (this.Tribe.Count > 0 ? 3 : 0) +
        (this.VoiceSlot.Count > 0 ? 4 : 0) +
        (this.VoiceId.Count > 0 ? 8 : 0);

    /// <summary>Does this profile constrain who the caster is, as opposed to how they look?</summary>
    public bool TargetsAudience
        => this.Names.Count > 0 || (this.Audience & AudienceBucket.Anyone) != AudienceBucket.Anyone;

    /// <summary>
    /// Precomputed name hashes, so matching costs no allocation on the cast path.
    /// </summary>
    /// <remarks>
    /// Rebuilt by <c>ProfileStore</c> on load and on every save, which are the only two
    /// moments <see cref="Names"/> can have changed. Kept off the serialised surface: this
    /// is derived data, and a stale copy in a shared profiles.json would be worse than none.
    /// </remarks>
    private (ulong Name, uint World)[] nameKeys = [];

    /// <summary>Recomputes <see cref="nameKeys"/>. Call after any edit to <see cref="Names"/>.</summary>
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

    /// <summary>
    /// The full match: who they are to you, whether you named them, and what they sound like.
    /// </summary>
    public bool Accepts(in CasterIdentity who)
    {
        // A profile is for a set of tiers; the caster belongs to a set of tiers. They only
        // have to overlap. Anyone & anything is always non-empty, so an untargeted profile
        // never fails here.
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

        // The target reads first because it is the coarser statement, and because it is
        // the one that decides whether the profile can ever fire at all.
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

    /// <summary>Tie-break only. Specificity always dominates.</summary>
    public int Priority { get; set; } = 100;

    public float Gain { get; set; } = 1f;

    public ProfileMatch Match { get; set; } = new();

    public List<VoiceRule> Rules { get; set; } = [];
}

/// <summary>On-disk shape of profiles.json.</summary>
public sealed class ProfileDocument
{
    public int Schema { get; set; } = 1;

    public List<VoiceProfile> Profiles { get; set; } = [];
}

/// <summary>What the resolver picked, from where, and how to play it.</summary>
public readonly record struct ResolvedClip(VoiceProfile Profile, VoiceRule Rule, ClipRef Clip, float Rate)
{
    public float CombinedGain => this.Profile.Gain * this.Clip.Gain;
}
