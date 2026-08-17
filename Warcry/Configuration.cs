using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace Warcry;

/// <summary>
/// Plugin settings only. Profiles and clip metadata deliberately live in separate
/// System.Text.Json files under GetPluginConfigDirectory() — SavePluginConfig writes
/// through IReliableFileStorage (duplicates every byte into a SQLite backup, hard-fails
/// above 64MB, and is synchronous) and serialises with TypeNameHandling.Objects.
/// See docs/PLAN.md 5.8.
/// </summary>
/// <remarks>
/// The namespace of this type is baked into every user's config file by
/// TypeNameHandling.Objects. Moving or renaming it orphans their settings.
/// It is fixed as <c>Warcry.Configuration</c>.
/// </remarks>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>Schema version. Bump and add an ordered migration step when fields change.</summary>
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    // ---- audio (M3) ----

    /// <summary>Master trim on top of the game's own sliders.</summary>
    public float MasterGain { get; set; } = 1.0f;

    /// <summary>Scale by the game's Voice slider rather than Sound Effects.</summary>
    public bool UseVoiceSliderNotSe { get; set; } = true;

    /// <summary>
    /// Hard cap on simultaneous plugin voices. Not a taste setting: the game's SoundData
    /// pool is 256 entries shared with the entire client and the Voice bus has 5 tracks.
    /// </summary>
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>M3 scaffolding: play a sound on your own actions.</summary>
    public bool PlayTestToneOnActions { get; set; } = true;

    /// <summary>
    /// Play the synthesised tone when an action has no clip mapped. Useful while setting
    /// mappings up — audible proof the action was detected — and noise once you are done.
    /// </summary>
    public bool FallBackToTestTone { get; set; } = true;

    /// <summary>
    /// Delay playback until the cast bar has actually finished, using the per-event
    /// measured remainder. Instants self-gate (no cast bar, so zero delay).
    /// </summary>
    public bool WaitForCastToFinish { get; set; } = true;

    // ---- gates (M6) ----

    public bool DisableInCutscenes { get; set; } = true;

    public bool DisableInPvP { get; set; } = true;

    public bool DisableInGpose { get; set; } = true;

    public bool DisableInQuestEvents { get; set; } = true;

    /// <summary>TerritoryType ids where nothing plays. Useful for hub cities.</summary>
    public HashSet<uint> BlockedTerritories { get; set; } = [];

    // ---- throttle (M6) ----

    /// <summary>Seconds before the same caster can trigger another line. 0 disables.</summary>
    public float SelfCooldownSeconds { get; set; } = 2.0f;

    /// <summary>Auto-attacks fire constantly and are never worth a voiceline.</summary>
    public bool SkipAutoAttacks { get; set; } = true;

    /// <summary>Only voice actions that have a cast bar.</summary>
    public bool CastsOnly { get; set; }

    /// <summary>Actions that never play, whatever is mapped to them.</summary>
    public HashSet<uint> MutedActionIds { get; set; } = [];

    /// <summary>
    /// Action ids actually observed firing on this character. The Action sheet contains
    /// duplicates, unused rows and NPC copies, so "which of the five Riposte rows is the
    /// real one" is not answerable from the sheet — but it is answerable from what the
    /// hook has seen. Persisted so the list survives a restart.
    /// </summary>
    public List<uint> ObservedActionIds { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
