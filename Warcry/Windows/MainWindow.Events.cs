using Dalamud.Bindings.ImGui;
using Warcry.Game;

namespace Warcry.Windows;

// The Events tab.
public sealed partial class MainWindow
{
    private bool onlyMe;
    private bool hideDropped = true;

    // Every action the hook saw, with what happened to it: the "why did that not play"
    // ledger, and the quickest route to mapping or muting an action you just used.
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
        Tip(
            "Hide actions used by anyone else.\n\n" +
            "Leave it off while setting up who you hear: other people's rows are where\n" +
            "the People tab's decisions show up, and hovering a name says what they are\n" +
            "to you.");

        ImGui.SameLine();
        ImGui.Checkbox("Hide non-actions", ref this.hideDropped);
        Tip(
            "Hide everything that was never a candidate anyway: items, mounts, status\n" +
            "ticks, and anything cast by something that is not a player.");

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
        // Cast time is the discriminator for the snapshot-vs-visual-completion offset: only
        // rows with a non-zero cast can fire early. Instants must never be delayed.
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

            // What they are to you, and why that was or was not enough — otherwise "not
            // listening" gives no clue which switch on the People tab would fix it.
            // Guarded rather than Tip(): this is inside the row loop, and Tip's argument is
            // built whether or not the row is hovered.
            if (row.Audience != AudienceBucket.None && ImGui.IsItemHovered())
            {
                var tier = Audience.Label(row.Audience);
                ImGui.SetTooltip(row.AudienceRefusal.Length > 0
                    ? $"{tier} — not heard: {row.AudienceRefusal}.\nThe People tab is where that is decided."
                    : $"{tier}, and you are listening to them."
                      + (ev.Facts.Distance == byte.MaxValue ? string.Empty : $"\n{ev.Facts.Distance} yalms away."));
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
                // Seconds of cast bar left at snapshot: the per-event, latency-correct
                // offset.
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
                Tip(
                    "Normally a cast, but no cast bar was running for it, so it played\n" +
                    "immediately. Instant casts and ability procs look like this.");
            }
            else
            {
                ImGui.TextDisabled("instant");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ev.Caster.IsValid
                ? $"r{ev.Caster.Race} s{ev.Caster.Sex} v{ev.Caster.VoiceId} (#{ev.Caster.VoiceSlot})"
                : "-");

            Tip(
                "The character's race, sex and voice, which is what lets a mapping apply\n" +
                "to one voice type rather than to everybody. Your own values are listed\n" +
                "under Details on the Status tab.");

            ImGui.TableNextColumn();
            if (row.Drop == DropStage.None)
            {
                ImGui.TextUnformatted("played");
            }
            else
            {
                ImGui.TextDisabled(DropShort(row.Drop));
            }

            Tip(row.Drop == DropStage.None
                ? "Warcry accepted this one and handed it to the audio output."
                : DropReason(row.Drop));

            // The only route that populates MutedActionIds: mute from the row where you
            // just heard the thing you did not want.
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

    // A two-or-three word answer for the Result column. The full sentence is on hover, from
    // DropReason.
    private static string DropShort(DropStage stage) => stage switch
    {
        DropStage.PlaybackOff => "playback off",
        DropStage.Gate => "kept quiet",
        DropStage.Throttle => "too soon",
        DropStage.NoClip => "no clip",
        DropStage.SinkRefused => "not played",
        DropStage.TooFarOut => "cast too long",
        DropStage.RateLimited => "too many at once",
        DropStage.Audience => "not listening",
        DropStage.NotPc => "not a player",
        DropStage.NotAction => "not an action",
        _ => stage.ToString(),
    };
}
