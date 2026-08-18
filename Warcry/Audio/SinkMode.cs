namespace Warcry.Audio;

/// <summary>Which sink gameplay lines are routed to.</summary>
/// <remarks>
/// The values are persisted in the user's configuration as integers — append new members,
/// never renumber these.
/// </remarks>
public enum SinkMode : byte
{
    /// <summary>
    /// Native first, managed per-line fallback. The forgiving mode: a cold clip or a
    /// missing Penumbra costs a line its native routing, never the line itself.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// The game's sound engine or nothing. A native refusal is a visible drop with a
    /// reason, not a quiet NAudio substitute — what you hear is always the engine.
    /// </summary>
    NativeOnly = 1,

    /// <summary>NAudio only. No Penumbra needed; the fallback of last resort.</summary>
    ManagedOnly = 2,

    /// <summary>No gameplay playback at all. Auditioning from the editor still works.</summary>
    Off = 3,
}
