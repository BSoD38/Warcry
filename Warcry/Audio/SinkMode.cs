namespace Warcry.Audio;

// Persisted as integers in the config — append members, never renumber.
public enum SinkMode : byte
{
    // Native first, managed per-line fallback.
    Auto = 0,

    // The engine or nothing: a native refusal is a counted drop, never an NAudio substitute.
    NativeOnly = 1,

    ManagedOnly = 2,

    // No gameplay playback. Auditioning from the editor still works.
    Off = 3,
}
