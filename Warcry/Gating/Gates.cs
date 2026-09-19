using Dalamud.Game.ClientState.Conditions;

namespace Warcry.Gating;

// "Should anything play at all right now", before the audience filter and any clip work.
public sealed class Gates
{
    private readonly Configuration config;

    public Gates(Configuration config) => this.config = config;

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

        // Never configurable: a line arriving after a zone transition belongs to a fight
        // that is already over.
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
