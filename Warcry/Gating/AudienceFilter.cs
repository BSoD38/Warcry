using System.Collections.Generic;
using System.Linq;
using Warcry.Game;

namespace Warcry.Gating;

/// <summary>
/// What a caster is to you, and whether that is enough to be heard.
/// </summary>
/// <param name="Membership">
/// EVERY bucket the caster belongs to — a friend in your party is <c>Party | Friend</c>.
/// This is what a profile's target is matched against, so a profile aimed at your party
/// fires for a party member whatever else they also are.
/// </param>
/// <param name="Primary">
/// The narrowest bucket that both applies and is switched on, used for the cooldown tier
/// and for what the Events tab shows. When nothing is switched on it is the narrowest that
/// applies, so a refused row can still say who they were.
/// </param>
/// <param name="Refusal">Why this caster is not heard, or empty. Always a literal.</param>
public readonly record struct AudienceVerdict(
    AudienceBucket Membership,
    AudienceBucket Primary,
    string Refusal)
{
    public bool Admitted => this.Refusal.Length == 0;

    /// <summary>
    /// A cast that never reached the filter — not a player, or not an action.
    /// </summary>
    /// <remarks>
    /// Distinct from a refusal so the Events tab can leave the audience column blank for
    /// rows the filter was never asked about, rather than inventing a reason for them.
    /// </remarks>
    public static readonly AudienceVerdict Unclassified =
        new(AudienceBucket.None, AudienceBucket.None, "not a player action");
}

/// <summary>
/// Decides whose actions are heard. This is the caster seam docs/PLAN.md 5.3 describes,
/// and the only place allowed to answer "should this person play a line".
/// </summary>
/// <remarks>
/// <para>Every input it reads was populated by the server on the spawned object and copied
/// into a <see cref="CasterFacts"/> — by the detour on the cast path, by the crowd scan
/// once a second — so classification is a handful of integer compares with no object-table
/// lookup and no allocation, and both callers get the same answer.</para>
/// <para>The named and blocked lists are held as precomputed 64-bit hash sets, rebuilt only
/// when the lists are edited. Every mutation goes through this class for that reason —
/// editing <c>Configuration.NamedPeople</c> directly would leave the sets stale.</para>
/// </remarks>
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

    /// <summary>True when anything but your own actions can be heard.</summary>
    public bool HearsAnyoneElse => (this.config.Audience & ~AudienceBucket.Self) != 0;

    /// <summary>Recomputes the name hash sets. Call after any edit to either list.</summary>
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

    /// <summary>Is this name on the named list, on any world?</summary>
    public bool IsNamed(string name) => Holds(this.namedAnyWorld, this.namedOnWorld, name);

    /// <summary>Is this name on the blocked list, on any world?</summary>
    public bool IsBlocked(string name) => Holds(this.blockedAnyWorld, this.blockedOnWorld, name);

    /// <summary>
    /// Classifies one cast. Never throws and never allocates.
    /// </summary>
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

    /// <summary>Seconds this tier must wait between lines from the same person.</summary>
    public float CooldownFor(AudienceBucket primary) => primary switch
    {
        AudienceBucket.Self => this.config.SelfCooldownSeconds,
        AudienceBucket.Named => this.config.NamedCooldownSeconds,
        AudienceBucket.Party or AudienceBucket.Alliance or AudienceBucket.Friend
            => this.config.PartyCooldownSeconds,
        _ => this.config.OtherCooldownSeconds,
    };

    /// <summary>Volume trim for this tier. Only your own lines play untrimmed.</summary>
    public float GainFor(AudienceBucket primary)
        => primary == AudienceBucket.Self ? 1f : this.config.OtherPlayerGain;

    /// <summary>
    /// Every bucket this caster belongs to.
    /// </summary>
    /// <remarks>
    /// A set rather than a first-match winner. Classifying a friend who is also in your
    /// party as Party alone would make "play lines for friends" silently skip them, which
    /// is the single most confusing thing this filter could do.
    /// </remarks>
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

    /// <summary>Does the user listen to anyone in this membership set?</summary>
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

    /// <summary>The narrowest bucket in a set — the one that describes fewest people.</summary>
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

    /// <summary>On the list under any world, from the hashes <see cref="Rebuild"/> precomputed.</summary>
    private static bool Holds(HashSet<ulong> anyWorld, HashSet<(ulong Name, uint World)> onWorld, string name)
    {
        var hash = PlayerId.Of(name);
        return hash != 0 && (anyWorld.Contains(hash) || onWorld.Any(e => e.Name == hash));
    }

    private static bool Same(NamedPlayer a, NamedPlayer b)
        => PlayerId.Of(a.Name) == PlayerId.Of(b.Name) && a.World == b.World;
}
