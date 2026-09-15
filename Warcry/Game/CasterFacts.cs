namespace Warcry.Game;

/// <summary>
/// The audience-relevant facts about a player, read straight off their game object.
/// </summary>
/// <param name="NameHash"><see cref="PlayerId"/> of their name, 0 if it could not be read.</param>
/// <param name="HomeWorld">Home-world row id, 0 if unknown. Pairs with the name hash.</param>
/// <param name="Distance">
/// Yalms from you, from <c>Character.CurrentDistance</c>. The game maintains it as a byte,
/// so a distance gate costs no square root. Saturates at 255.
/// </param>
/// <param name="IsSelf">You. Compared on the 32-bit entity id by whoever built this.</param>
/// <remarks>
/// <para>Exists so the two places that classify a player — the cast path, reading a
/// <c>Character*</c> inside the ActionEffect detour, and the once-a-second crowd scan,
/// reading the object table — can hand the identical shape to
/// <c>Gating.AudienceFilter</c>. Two sources, one policy; without this the two would
/// drift, and a player could be audible while not counting toward the crowd that is
/// supposed to be quietening them.</para>
/// <para>Every field is a server-populated value already present on the spawned object.
/// Nothing here needs a lookup, a lock or an allocation.</para>
/// </remarks>
public readonly record struct CasterFacts(
    ulong NameHash,
    ushort HomeWorld,
    byte Distance,
    bool IsPartyMember,
    bool IsAllianceMember,
    bool IsFriend,
    bool IsSelf)
{
    /// <summary>A caster we could not read anything useful about.</summary>
    public static readonly CasterFacts Unknown = new(0, 0, byte.MaxValue, false, false, false, false);
}
