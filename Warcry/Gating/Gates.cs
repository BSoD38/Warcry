using Dalamud.Game.ClientState.Conditions;

namespace Warcry.Gating;

/// <summary>
/// Global "should anything play at all right now" checks, evaluated before the audience
/// filter and before any clip work.
/// </summary>
/// <remarks>
/// This is the layer whose absence was the plugin's worst behaviour: without it, battle
/// cries fire during cutscenes. Loading-screen suppression is unconditional — a voiceline
/// arriving after a zone transition belongs to a fight that is already over.
/// </remarks>
public sealed class Gates
{
    private readonly Configuration config;

    public Gates(Configuration config) => this.config = config;

    /// <summary>Human-readable reason playback is currently suppressed, or empty.</summary>
    public string Reason { get; private set; } = string.Empty;

    public bool IsSuppressed()
    {
        var condition = Plugin.Condition;

        // Two distinct flags cover cutscenes; gate on both.
        if (this.config.DisableInCutscenes &&
            (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene]))
        {
            this.Reason = "cutscene";
            return true;
        }

        // Never configurable: mid-transition audio is always wrong.
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            this.Reason = "zoning";
            return true;
        }

        if (this.config.DisableInPvP && Plugin.ClientState.IsPvP)
        {
            this.Reason = "PvP";
            return true;
        }

        if (this.config.DisableInGpose && Plugin.ClientState.IsGPosing)
        {
            this.Reason = "group pose";
            return true;
        }

        if (this.config.DisableInQuestEvents &&
            (condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent]))
        {
            this.Reason = "quest event";
            return true;
        }

        if (this.config.BlockedTerritories.Contains(Plugin.ClientState.TerritoryType))
        {
            this.Reason = $"territory {Plugin.ClientState.TerritoryType} is muted";
            return true;
        }

        this.Reason = string.Empty;
        return false;
    }
}
