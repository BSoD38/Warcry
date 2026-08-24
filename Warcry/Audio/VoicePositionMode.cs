namespace Warcry.Audio;

/// <summary>Where a gameplay line sounds from, and whether it tracks its caster.</summary>
/// <remarks>
/// <para>Persisted in the user's configuration as an integer — append new members, never
/// renumber these.</para>
/// <para>Only <see cref="Follow"/> retains an engine handle. <see cref="Fixed"/> and
/// <see cref="Listener"/> hand the sound to the engine and forget it, which is exactly the
/// call verified in game on 2026-08-18 — so the two fallbacks are not new code paths.</para>
/// </remarks>
public enum VoicePositionMode : byte
{
    /// <summary>
    /// Repositioned every frame from the caster's live position, so a displacement skill
    /// cannot outrun its own line.
    /// </summary>
    /// <remarks>
    /// Costs <c>autoRelease: false</c>, which makes the engine's pool slot ours until we
    /// release it. That pool is 256 entries shared with the entire client, so every exit
    /// path in <see cref="NativeVoiceSink"/> releases. See docs/PLAN.md 9.
    /// </remarks>
    Follow = 0,

    /// <summary>
    /// Sounds from where the line was uttered and stays there. The engine owns the slot.
    /// </summary>
    Fixed = 1,

    /// <summary>
    /// Non-positional: no falloff, no panning, wherever the camera is. Immune to
    /// displacement by construction, at the cost of not being in the world.
    /// </summary>
    Listener = 2,
}
