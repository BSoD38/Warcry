namespace Warcry.Audio;

// Persisted as integers in the config — append members, never renumber.
// Every member leaves damage-taken and death grunts alone; that split lives in
// GruntSuppressor, so no mode can widen past it.
public enum GruntMode : byte
{
    // The hook is not even enabled.
    Off = 0,

    // Silence the attack grunt of a caster whose Warcry line is about to play, for
    // GruntWindowSeconds after the cast.
    WhenVoiced = 1,

    // Every player attack grunt, mapped or not. Needs no caster attribution.
    Always = 2,
}
