namespace Warcry.Game;

/// <summary>
/// Everything a profile can be matched against: what the caster sounds like, who they are,
/// and what they are to you.
/// </summary>
/// <param name="Voice">Race, tribe, sex and voice — the appearance half of the match.</param>
/// <param name="Audience">
/// The single bucket this cast was classified into. <see cref="AudienceBucket.None"/> only
/// for a caster who was never admitted, which never reaches a profile.
/// </param>
/// <param name="NameHash">
/// <see cref="PlayerId"/> of the caster's name, 0 when unknown. Only a profile with an
/// explicit name list reads it.
/// </param>
/// <param name="HomeWorld">Home-world row id, 0 when unknown.</param>
/// <remarks>
/// Separate from <see cref="CasterKey"/> rather than folded into it: CasterKey is the
/// appearance the game hands us on the spawned object and is stable for a character,
/// whereas the audience bucket is a classification we derive per cast against live party
/// and friend state. Keeping them apart is what lets CasterKey stay a cache key.
/// </remarks>
public readonly record struct CasterIdentity(
    CasterKey Voice,
    AudienceBucket Audience,
    ulong NameHash,
    ushort HomeWorld);
