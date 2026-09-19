using System;
using System.Linq;

namespace Warcry.Game;

// Who a caster is to you, decided by Gating.AudienceFilter. Flags rather than a plain enum
// because both sides need sets: the user's "play lines for..." choice and a profile's
// target. A single classified cast always carries exactly one bit.
[Flags]
public enum AudienceBucket : byte
{
    None = 0,

    Self = 1,

    // A player you listed by name.
    Named = 2,

    // RelationFlags bit 0.
    Party = 4,

    // RelationFlags bit 1 — alliance but not party.
    Alliance = 8,

    // RelationFlags bit 2, server-populated.
    Friend = 16,

    Other = 32,

    Anyone = Self | Named | Party | Alliance | Friend | Other,
}

public static class Audience
{
    // First match wins, narrowest first: a party member who is also a friend classifies as
    // Party, and someone you named by hand outranks both.
    public static readonly AudienceBucket[] ClassifyOrder =
    [
        AudienceBucket.Self,
        AudienceBucket.Named,
        AudienceBucket.Party,
        AudienceBucket.Alliance,
        AudienceBucket.Friend,
        AudienceBucket.Other,
    ];

    // How few people a bucket describes; drives profile specificity. Self and Named tie —
    // both name one person — and the tie falls through to voice specificity then Priority.
    public static int Tier(AudienceBucket bucket) => bucket switch
    {
        AudienceBucket.Self or AudienceBucket.Named => 3,
        AudienceBucket.Party or AudienceBucket.Alliance or AudienceBucket.Friend => 2,
        AudienceBucket.Other => 1,
        _ => 0,
    };

    // A set is as narrow as its widest member: "my party or anyone else nearby" fires for
    // the same strangers as "anyone else nearby". The minimum is what stops adding a tier
    // to a profile making it look more specific.
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
