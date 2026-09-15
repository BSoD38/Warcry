namespace Warcry.Audio;

/// <summary>How much of the game's own battle grunt to silence.</summary>
/// <remarks>
/// <para>Persisted in the user's configuration as an integer — append new members, never
/// renumber these.</para>
/// <para>One setting rather than two independent switches because the two suppressing
/// members answer the same question at different widths: "silence the grunt Warcry is
/// speaking over" and "silence the grunt" are alternatives, not a feature and its modifier.
/// Only <see cref="WhenVoiced"/> needs a caster attributed to it, and so only it consults
/// <c>GruntWindowSeconds</c>.</para>
/// <para>Every member leaves damage-taken and death grunts alone — that split is in
/// <see cref="GruntSuppressor"/>, not here, so no mode can widen past it.</para>
/// </remarks>
public enum GruntMode : byte
{
    /// <summary>The game keeps its voice. The hook is not even enabled.</summary>
    Off = 0,

    /// <summary>
    /// Silence the attack grunt of a caster whose Warcry line is about to play, for
    /// <c>GruntWindowSeconds</c> after the cast.
    /// </summary>
    WhenVoiced = 1,

    /// <summary>
    /// Silence every player attack grunt, whether or not Warcry has anything to say.
    /// </summary>
    /// <remarks>
    /// A statement about the game's audio rather than about Warcry: it needs nothing
    /// mapped, no clips loaded and no caster attribution.
    /// </remarks>
    Always = 2,
}
