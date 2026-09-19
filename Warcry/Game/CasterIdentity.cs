namespace Warcry.Game;

// Everything a profile can be matched against. Kept separate from CasterKey — which is
// stable per character and therefore usable as a cache key — because Audience is derived
// per cast against live party and friend state.
public readonly record struct CasterIdentity(
    CasterKey Voice,
    AudienceBucket Audience,
    ulong NameHash,
    ushort HomeWorld);
