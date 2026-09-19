namespace Warcry.Audio;

// Persisted as integers in the config — append members, never renumber.
public enum VoicePositionMode : byte
{
    // Repositioned every frame from the caster's live position, so a displacement skill
    // cannot outrun its line. Costs autoRelease: false, so the engine's pool slot is ours
    // until released — 256 entries shared with the whole client. See docs/PLAN.md 9.
    Follow = 0,

    // Sounds from where it was uttered and stays there. The engine owns the slot.
    Fixed = 1,

    // Non-positional: no falloff, no panning, wherever the camera is.
    Listener = 2,
}
