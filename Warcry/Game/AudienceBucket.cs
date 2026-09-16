using System;
using System.Linq;

namespace Warcry.Game;

/// <summary>
/// Who a caster is to you. One bucket per cast, decided by <c>Gating.AudienceFilter</c>.
/// </summary>
/// <remarks>
/// <para>Flags rather than a plain enum because both sides of the feature need SETS: the
/// user's "play lines for..." choice is a set, and a profile's target is a set. A single
/// classified cast always carries exactly one bit.</para>
/// <para>Ordered by narrowness, not by importance — <see cref="Tier"/> is what encodes
/// "how few people does this describe", and the resolver sorts on that.</para>
/// </remarks>
[Flags]
public enum AudienceBucket : byte
{
    None = 0,

    /// <summary>You. Compared on the 32-bit entity id, never on SourceSequence.</summary>
    Self = 1,

    /// <summary>A player you listed by name.</summary>
    Named = 2,

    /// <summary>In your party (RelationFlags bit 0).</summary>
    Party = 4,

    /// <summary>In your alliance but not your party (RelationFlags bit 1).</summary>
    Alliance = 8,

    /// <summary>On your friend list (RelationFlags bit 2), server-populated.</summary>
    Friend = 16,

    /// <summary>Any other player character.</summary>
    Other = 32,

    /// <summary>Every bucket. A profile targeting this puts no constraint on the caster.</summary>
    Anyone = Self | Named | Party | Alliance | Friend | Other,
}

/// <summary>Labels and narrowness ranking for <see cref="AudienceBucket"/>.</summary>
public static class Audience
{
    /// <summary>
    /// Classification order — first match wins. Narrowest first, so a party member who is
    /// also a friend is classified Party, and someone you named by hand outranks both.
    /// </summary>
    public static readonly AudienceBucket[] ClassifyOrder =
    [
        AudienceBucket.Self,
        AudienceBucket.Named,
        AudienceBucket.Party,
        AudienceBucket.Alliance,
        AudienceBucket.Friend,
        AudienceBucket.Other,
    ];

    /// <summary>
    /// How few people a bucket describes. Drives profile specificity, so a profile aimed
    /// at one named person beats one aimed at your party, which beats one aimed at
    /// everybody.
    /// </summary>
    /// <remarks>
    /// <see cref="AudienceBucket.Self"/> and <see cref="AudienceBucket.Named"/> tie: both
    /// name exactly one person. The tie falls through to voice specificity and then to
    /// <c>Priority</c>, which is the same way two equally specific voice matches resolve.
    /// </remarks>
    public static int Tier(AudienceBucket bucket) => bucket switch
    {
        AudienceBucket.Self or AudienceBucket.Named => 3,
        AudienceBucket.Party or AudienceBucket.Alliance or AudienceBucket.Friend => 2,
        AudienceBucket.Other => 1,
        _ => 0,
    };

    /// <summary>
    /// The narrowness of a SET of buckets, which is that of its widest member.
    /// </summary>
    /// <remarks>
    /// A profile aimed at "my party or anyone else nearby" is exactly as unspecific as one
    /// aimed at "anyone else nearby" — it will fire for the same strangers. Taking the
    /// minimum is what stops adding a tier to a profile making it look MORE specific.
    /// </remarks>
    public static int TierOf(AudienceBucket set)
    {
        if (set == AudienceBucket.None)
        {
            return 0;
        }

        var narrowest = int.MaxValue;
        foreach (var bucket in ClassifyOrder)
        {
            if ((set & bucket) != 0)
            {
                narrowest = Math.Min(narrowest, Tier(bucket));
            }
        }

        return narrowest == int.MaxValue ? 0 : narrowest;
    }

    public static string Label(AudienceBucket bucket) => bucket switch
    {
        AudienceBucket.Self => "Me",
        AudienceBucket.Named => "People I named",
        AudienceBucket.Party => "My party",
        AudienceBucket.Alliance => "My alliance",
        AudienceBucket.Friend => "Friends",
        AudienceBucket.Other => "Everyone else",
        AudienceBucket.Anyone => "Everyone",
        AudienceBucket.None => "Nobody",
        _ => bucket.ToString(),
    };

    /// <summary>A set as a short phrase, for a profile header or an event row.</summary>
    public static string Describe(AudienceBucket set)
    {
        if (set == AudienceBucket.None)
        {
            return "nobody";
        }

        if ((set & AudienceBucket.Anyone) == AudienceBucket.Anyone)
        {
            return "everyone";
        }

        // Only the leading label keeps its capital; the rest read as a continuing list.
        var labels = ClassifyOrder.Where(b => (set & b) != 0).Select(Label);
        return string.Join(", ", labels.Select((l, i) => i == 0 ? l : l.ToLowerInvariant()));
    }
}
