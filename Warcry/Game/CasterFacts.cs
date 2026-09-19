namespace Warcry.Game;

// The audience-relevant facts about a player, read straight off their game object — no
// lookup, no lock, no allocation. Exists so the two places that classify a player (the
// ActionEffect detour reading a Character*, and the 1 Hz crowd scan reading the object
// table) hand the identical shape to Gating.AudienceFilter and cannot drift.
// Distance is Character.CurrentDistance in yalms, saturating at 255, so a distance gate
// costs no square root.
public readonly record struct CasterFacts(
    ulong NameHash,
    ushort HomeWorld,
    byte Distance,
    bool IsPartyMember,
    bool IsAllianceMember,
    bool IsFriend,
    bool IsSelf)
{
    public static readonly CasterFacts Unknown = new(0, 0, byte.MaxValue, false, false, false, false);
}
