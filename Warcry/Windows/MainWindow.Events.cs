using Dalamud.Bindings.ImGui;

namespace Warcry.Windows;

/// <summary>The Events tab.</summary>
public sealed partial class MainWindow
{
    private bool onlyMe;
    private bool hideDropped = true;

    /// <summary>
    /// Every action the hook saw, with what happened to it: the "why did that not play"
    /// ledger, and the quickest route to mapping or muting an action you just used.
    /// </summary>
    /// <remarks>
    /// The Result column used to print the raw <c>DropStage</c> name, so a user who wanted
    /// to know why nothing happened got <c>SinkRefused</c> or <c>TooFarOut</c>. Each cell
    /// now carries a short answer and a full sentence on hover.
    /// </remarks>
    private void DrawEvents()
    {
        var diag = this.plugin.Diag;

        ImGui.TextUnformatted($"{diag.TotalSeen} action(s) seen, showing the last {diag.Count}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
        {
            diag.Clear();
        }

        ImGui.SameLine();
        ImGui.Checkbox("Only me", ref this.onlyMe);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Hide actions used by anyone else. Warcry only voices you, so this is usually what you want.");
        }

        ImGui.SameLine();
        ImGui.Checkbox("Hide non-actions", ref this.hideDropped);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Hide everything that was never a candidate anyway: items, mounts, status\n" +
                "ticks, and anything cast by something that is not a player.");
        }

        ImGui.Separator();

        if (diag.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Nothing yet. Use an action and it will appear here.");
            ImGui.TextDisabled("This list fills up even when playback is off, which makes it the place to");
            ImGui.TextDisabled("check whether Warcry is seeing your actions at all.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg
                                    | ImGuiTableFlags.Borders
                                    | ImGuiTableFlags.ScrollY
                                    | ImGuiTableFlags.Resizable
                                    | ImGuiTableFlags.SizingStretchProp;

        var muted = this.plugin.Config.MutedActionIds;
        uint? toggleMute = null;

        if (!ImGui.BeginTable("##events", 9, flags, ImGui.GetContentRegionAvail()))
        {
            return;
        }

        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("Used by", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("Action");
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 92);
        // Cast time is THE discriminator for the snapshot-vs-visual-completion offset:
        // only rows with a non-zero cast can fire "early". Instants must never be delayed.
        ImGui.TableSetupColumn("Cast bar", ImGuiTableColumnFlags.WidthFixed, 108);
        ImGui.TableSetupColumn("Voice type", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthFixed, 104);
        ImGui.TableSetupColumn("Mute", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        for (var i = 0; i < diag.Count; i++)
        {
            var row = diag.At(i);
            var ev = row.Event;

            if (this.onlyMe && !ev.IsLocalPlayer)
            {
                continue;
            }

            if (this.hideDropped && row.Drop is DropStage.NotPc or DropStage.NotAction)
            {
                continue;
            }

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.When.ToString("HH:mm:ss"));

            ImGui.TableNextColumn();
            if (ev.IsLocalPlayer)
            {
                ImGui.TextUnformatted("You");
            }
            else
            {
                ImGui.TextUnformatted(string.IsNullOrEmpty(row.CasterName) ? $"0x{ev.CasterEntityId:X8}" : row.CasterName);
            }

            ImGui.TableNextColumn();
            ImGui.PushID((int)ev.GlobalSequence ^ (int)ev.ActionId);
            if (ImGui.SmallButton("map"))
            {
                // Straight from an observed event: no guessing which of several rows
                // sharing a name is the one that actually fires.
                this.selectedActionId = ev.ActionId;
                this.jumpToMappings = true;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"Pick a clip for {this.ActionName(ev.ActionId)} on the Actions tab.");
            }

            ImGui.PopID();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(this.ActionName(ev.ActionId));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"Action id {ev.ActionId}.\n\n" +
                    "The game has several rows sharing most action names, so the id is the\n" +
                    "only reliable way to say which button this was. The map button beside\n" +
                    "this row fills in the right one for you.");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(this.CategoryOf(ev.ActionId));

            ImGui.TableNextColumn();
            var cast = this.CastSecondsOf(ev.ActionId);
            if (ev.WasCasting)
            {
                // The number that matters: seconds of cast bar left at snapshot.
                // This is the real, per-event, latency-correct offset.
                ImGui.TextUnformatted($"{cast:0.0}s  +{ev.CastRemaining:0.00}");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        $"A {cast:0.0}s cast, with {ev.CastRemaining:0.00}s still to run when the game\n" +
                        "committed the action. That gap is how long the line was held back for,\n" +
                        "so it finishes with the cast bar instead of ahead of it.");
                }
            }
            else if (cast > 0f)
            {
                ImGui.TextUnformatted($"{cast:0.0}s  (no bar)");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "Normally a cast, but no cast bar was running for it, so it played\n" +
                        "immediately. Instant casts and ability procs look like this.");
                }
            }
            else
            {
                ImGui.TextDisabled("instant");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ev.Caster.IsValid
                ? $"r{ev.Caster.Race} s{ev.Caster.Sex} v{ev.Caster.VoiceId} (#{ev.Caster.VoiceSlot})"
                : "-");

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "The character's race, sex and voice, which is what lets a mapping apply\n" +
                    "to one voice type rather than to everybody. Your own values are listed\n" +
                    "under Details on the Status tab.");
            }

            ImGui.TableNextColumn();
            if (row.Drop == DropStage.None)
            {
                ImGui.TextUnformatted("played");
            }
            else
            {
                ImGui.TextDisabled(DropShort(row.Drop));
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(row.Drop == DropStage.None
                    ? "Warcry accepted this one and handed it to the audio output."
                    : DropReason(row.Drop));
            }

            // Mute, from the row where you just heard the thing you did not want.
            // This is the only way MutedActionIds can be populated at all: before it
            // existed the config field and the Throttle check were both unreachable.
            ImGui.TableNextColumn();
            if (ev.ActionId == 0)
            {
                ImGui.TextDisabled("-");
            }
            else if (muted.Contains(ev.ActionId))
            {
                if (ImGui.SmallButton($"unmute##m{i}"))
                {
                    toggleMute = ev.ActionId;
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Let {this.ActionName(ev.ActionId)} play again.");
                }
            }
            else
            {
                if (ImGui.SmallButton($"mute##m{i}"))
                {
                    toggleMute = ev.ActionId;
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        $"Never play anything for {this.ActionName(ev.ActionId)}, whatever is\n" +
                        "mapped to it. You can undo this here or on the Settings tab.");
                }
            }
        }

        ImGui.EndTable();

        if (toggleMute is { } id)
        {
            if (!muted.Remove(id))
            {
                muted.Add(id);
            }

            this.plugin.Config.Save();
        }
    }

    /// <summary>
    /// A two-or-three word answer for the Result column. The full sentence is on hover,
    /// from <see cref="DropReason"/>.
    /// </summary>
    private static string DropShort(DropStage stage) => stage switch
    {
        DropStage.PlaybackOff => "playback off",
        DropStage.Gate => "kept quiet",
        DropStage.Throttle => "too soon",
        DropStage.NoClip => "no clip",
        DropStage.SinkRefused => "not played",
        DropStage.TooFarOut => "cast too long",
        DropStage.Audience => "not you",
        DropStage.NotPc => "not a player",
        DropStage.NotAction => "not an action",
        _ => stage.ToString(),
    };
}
