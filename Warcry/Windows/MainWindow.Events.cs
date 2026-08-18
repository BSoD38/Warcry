using Dalamud.Bindings.ImGui;

namespace Warcry.Windows;

/// <summary>The Events tab.</summary>
public sealed partial class MainWindow
{
    private bool onlyMe;
    private bool hideDropped = true;


    /// <summary>
    /// Every action the hook saw, with what happened to it — the "why did/didn't that
    /// play" ledger, and the quickest route to mapping or muting an action you just used.
    /// </summary>
    private void DrawEvents()
    {
        var diag = this.plugin.Diag;

        ImGui.TextUnformatted($"{diag.TotalSeen} events seen, showing last {diag.Count}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
        {
            diag.Clear();
        }

        ImGui.SameLine();
        ImGui.Checkbox("Only me", ref this.onlyMe);
        ImGui.SameLine();
        ImGui.Checkbox("Hide non-PC / non-Action", ref this.hideDropped);

        ImGui.Separator();

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
        ImGui.TableSetupColumn("Caster", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("ActionId", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("Action");
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 92);
        // Cast time is THE discriminator for the snapshot-vs-visual-completion offset:
        // only rows with a non-zero cast can fire "early". Instants must never be delayed.
        ImGui.TableSetupColumn("Cast / +left", ImGuiTableColumnFlags.WidthFixed, 108);
        ImGui.TableSetupColumn("Voice", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Drop", ImGuiTableColumnFlags.WidthFixed, 76);
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
                ImGui.TextUnformatted("YOU");
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
                ImGui.SetTooltip($"Assign a clip to action #{ev.ActionId} on the Mappings tab.");
            }

            ImGui.PopID();
            ImGui.SameLine();
            ImGui.TextUnformatted(ev.ActionId.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(this.ActionName(ev.ActionId));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(this.CategoryOf(ev.ActionId));

            ImGui.TableNextColumn();
            var cast = this.CastSecondsOf(ev.ActionId);
            if (ev.WasCasting)
            {
                // The number that matters: seconds of cast bar left at snapshot.
                // This is the real, per-event, latency-correct offset.
                ImGui.TextUnformatted($"{cast:0.0}s  +{ev.CastRemaining:0.00}");
            }
            else if (cast > 0f)
            {
                ImGui.TextUnformatted($"{cast:0.0}s  (bar gone)");
            }
            else
            {
                ImGui.TextDisabled("-");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ev.Caster.IsValid
                ? $"r{ev.Caster.Race} s{ev.Caster.Sex} v{ev.Caster.VoiceId} (#{ev.Caster.VoiceSlot})"
                : "-");

            ImGui.TableNextColumn();
            if (row.Drop == DropStage.None)
            {
                ImGui.TextUnformatted("ok");
            }
            else
            {
                ImGui.TextDisabled(row.Drop.ToString());
            }

            // Mute, from the row where you just heard the thing you did not want.
            // This is the only way MutedActionIds can be populated at all — before it
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
            }
            else if (ImGui.SmallButton($"mute##m{i}"))
            {
                toggleMute = ev.ActionId;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"Never play anything for action {ev.ActionId} ({this.ActionName(ev.ActionId)}),\n" +
                    "whatever is mapped to it. Reversible here or on the Settings tab.");
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
}
