using System.Collections.Generic;
using System.Linq;
using Warcry.Game;

namespace Warcry.Gating;

// What a caster is to you, and whether that is enough to be heard.
// Membership is EVERY bucket the caster belongs to — a friend in your party is
// Party | Friend — and is what a profile's target is matched against.
// Primary is the narrowest bucket that both applies and is switched on, used for the
// cooldown tier and the Events tab. When nothing is switched on it is the narrowest that
// applies, so a refused row can still say who they were.
// Refusal is always a literal, empty when the caster is heard.
public readonly record struct AudienceVerdict(
    AudienceBucket Membership,
    AudienceBucket Primary,
    string Refusal)
{
    public bool Admitted => this.Refusal.Length == 0;

    // A cast that never reached the filter — not a player, or not an action. Distinct from
    // a refusal so the Events tab can leave the audience column blank rather than inventing
    // a reason.
    public static readonly AudienceVerdict Unclassified =
        new(AudienceBucket.None, AudienceBucket.None, "not a player action");
}

// Decides whose actions are heard: the caster seam of docs/PLAN.md 5.3, and the only place
// allowed to answer "should this person play a line".
// Every input arrives in a CasterFacts, so classification is a handful of integer compares
// with no object-table lookup and no allocation, and the detour and the crowd scan get the
// same answer.
// The named and blocked lists are precomputed 64-bit hash sets rebuilt only on edit, so
// every mutation must go through this class — editing Configuration.NamedPeople directly
// leaves the sets stale.
public sealed class AudienceFilter
{
    private readonly Configuration config;

    // Split by whether the entry pinned a world, so the common "any world" case is a
    // single ulong lookup and the pinned case never has to scan.
    private readonly HashSet<ulong> namedAnyWorld = [];
    private readonly HashSet<(ulong Name, uint World)> namedOnWorld = [];
    private readonly HashSet<ulong> blockedAnyWorld = [];
    private readonly HashSet<(ulong Name, uint World)> blockedOnWorld = [];

    public AudienceFilter(Configuration config)
    {
        this.config = config;
        this.Rebuild();
    }

    // True when anything but your own actions can be heard.
    public bool HearsAnyoneElse => (this.config.Audience & ~AudienceBucket.Self) != 0;

    // Call after any edit to either list.
    public void Rebuild()
    {
        this.namedAnyWorld.Clear();
        this.namedOnWorld.Clear();
        this.blockedAnyWorld.Clear();
        this.blockedOnWorld.Clear();

        Fill(this.config.NamedPeople, this.namedAnyWorld, this.namedOnWorld);
        Fill(this.config.BlockedPeople, this.blockedAnyWorld, this.blockedOnWorld);

        static void Fill(
            List<NamedPlayer> source,
            HashSet<ulong> anyWorld,
            HashSet<(ulong, uint)> onWorld)
        {
            foreach (var entry in source)
            {
                var hash = PlayerId.Of(entry.Name);
                if (hash == 0)
                {
                    continue;
                }

                if (entry.World == 0)
                {
                    anyWorld.Add(hash);
                }
                else
                {
                    onWorld.Add((hash, entry.World));
                }
            }
        }
    }

    public void AddNamed(NamedPlayer player) => this.Add(this.config.NamedPeople, player);

    public void AddBlocked(NamedPlayer player) => this.Add(this.config.BlockedPeople, player);

    public void RemoveNamed(NamedPlayer player) => this.Remove(this.config.NamedPeople, player);

    public void RemoveBlocked(NamedPlayer player) => this.Remove(this.config.BlockedPeople, player);

    public bool IsNamed(string name) => Holds(this.namedAnyWorld, this.namedOnWorld, name);

    public bool IsBlocked(string name) => Holds(this.blockedAnyWorld, this.blockedOnWorld, name);

    // Never throws and never allocates.
    public AudienceVerdict Classify(in CasterFacts facts)
    {
        if (facts.IsSelf)
        {
            return (this.config.Audience & AudienceBucket.Self) != 0
                ? new AudienceVerdict(AudienceBucket.Self, AudienceBucket.Self, string.Empty)
                : new AudienceVerdict(AudienceBucket.Self, AudienceBucket.Self, "your own lines are switched off");
        }

        var membership = this.MembershipOf(in facts);

        // The block list wins over every tier but your own, including a name that is also
        // on the named list. Blocking is the stronger of the two statements.
        if (this.Blocks(in facts))
        {
            return new AudienceVerdict(membership, Narrowest(membership), "they are on your blocked list");
        }

        // Distance before the tier check: a party member across the zone is still nobody
        // you can hear, and refusing here spends no voice and no sound-pool slot.
        var limit = this.config.MaxDistanceYalms;
        if (limit > 0 && facts.Distance > limit)
        {
            return new AudienceVerdict(membership, Narrowest(membership), "they are too far away");
        }

        var enabled = membership & this.config.Audience;
        if (enabled == AudienceBucket.None)
        {
            return new AudienceVerdict(membership, Narrowest(membership), "you are not listening to them");
        }

        return new AudienceVerdict(membership, Narrowest(enabled), string.Empty);
    }

    // Seconds this tier must wait between lines from the same person.
    public float CooldownFor(AudienceBucket primary) => primary switch
    {
        AudienceBucket.Self => this.config.SelfCooldownSeconds,
        AudienceBucket.Named => this.config.NamedCooldownSeconds,
        AudienceBucket.Party or AudienceBucket.Alliance or AudienceBucket.Friend
            => this.config.PartyCooldownSeconds,
        _ => this.config.OtherCooldownSeconds,
    };

    // Only your own lines play untrimmed.
    public float GainFor(AudienceBucket primary)
        => primary == AudienceBucket.Self ? 1f : this.config.OtherPlayerGain;

    // A set, not a first match: classifying a friend who is also in your party as Party
    // alone would make "play lines for friends" silently skip them.
    public AudienceBucket MembershipOf(in CasterFacts facts)
    {
        if (facts.IsSelf)
        {
            return AudienceBucket.Self;
        }

        var membership = AudienceBucket.Other;

        if (this.Names(in facts))
        {
            membership |= AudienceBucket.Named;
        }

        if (facts.IsPartyMember)
        {
            membership |= AudienceBucket.Party;
        }

        if (facts.IsAllianceMember)
        {
            membership |= AudienceBucket.Alliance;
        }

        if (facts.IsFriend)
        {
            membership |= AudienceBucket.Friend;
        }

        return membership;
    }

    // Does the user listen to anyone in this membership set?
    public bool Admits(AudienceBucket membership)
        => (membership & this.config.Audience) != AudienceBucket.None;

    private bool Names(in CasterFacts facts)
        => facts.NameHash != 0
           && (this.namedAnyWorld.Contains(facts.NameHash)
               || this.namedOnWorld.Contains((facts.NameHash, facts.HomeWorld)));

    private bool Blocks(in CasterFacts facts)
        => facts.NameHash != 0
           && (this.blockedAnyWorld.Contains(facts.NameHash)
               || this.blockedOnWorld.Contains((facts.NameHash, facts.HomeWorld)));

    // The bucket in the set that describes fewest people.
    private static AudienceBucket Narrowest(AudienceBucket set)
    {
        foreach (var bucket in Audience.ClassifyOrder)
        {
            if ((set & bucket) != 0)
            {
                return bucket;
            }
        }

        return AudienceBucket.None;
    }

    private void Add(List<NamedPlayer> list, NamedPlayer player)
    {
        if (string.IsNullOrWhiteSpace(player.Name))
        {
            return;
        }

        player.Name = player.Name.Trim();

        foreach (var existing in list)
        {
            if (Same(existing, player))
            {
                return;
            }
        }

        list.Add(player);
        this.Rebuild();
        this.config.Save();
    }

    private void Remove(List<NamedPlayer> list, NamedPlayer player)
    {
        if (list.RemoveAll(p => Same(p, player)) > 0)
        {
            this.Rebuild();
            this.config.Save();
        }
    }

    // On the list under any world, from the hashes Rebuild precomputed.
    private static bool Holds(HashSet<ulong> anyWorld, HashSet<(ulong Name, uint World)> onWorld, string name)
    {
        var hash = PlayerId.Of(name);
        return hash != 0 && (anyWorld.Contains(hash) || onWorld.Any(e => e.Name == hash));
    }

    private static bool Same(NamedPlayer a, NamedPlayer b)
        => PlayerId.Of(a.Name) == PlayerId.Of(b.Name) && a.World == b.World;
}
