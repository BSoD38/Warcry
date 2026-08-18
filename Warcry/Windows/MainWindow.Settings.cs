using System.Linq;
using Dalamud.Bindings.ImGui;
using Warcry.Audio;

namespace Warcry.Windows;

/// <summary>The Settings tab.</summary>
public sealed partial class MainWindow
{
    private void DrawSettings()
    {
        var cfg = this.plugin.Config;
        var dirty = false;

        // The one question this tab exists to answer, answered before anything else on it.
        var silence = this.plugin.ExplainSilence();
        if (silence.Length > 0)
        {
            ImGui.TextUnformatted("Why you are not hearing anything:");
            ImGui.TextWrapped($"  {silence}");
            ImGui.Separator();
        }

        // ---- the master switch, and the audio settings that used to hide on the
        // diagnostics tab where nobody would look for them ----
        var enabled = cfg.Enabled;
        if (ImGui.Checkbox("Warcry enabled", ref enabled))
        {
            cfg.Enabled = enabled;
            dirty = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Off stops everything: no detection work, no clips, no scheduling.");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Volume");

        var gain = cfg.MasterGain;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Plugin volume", ref gain, 0f, 2f, "%.2f"))
        {
            cfg.MasterGain = gain;
        }

        // Save on release, not per frame. SavePluginConfig is synchronous and writes
        // through IReliableFileStorage, so saving while a slider is dragged writes the
        // whole config on every frame of the drag.
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            dirty = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "A trim on top of the game's own sliders, which are always applied first.\n" +
                "1.00 means 'exactly as loud as the game would play it'.");
        }

        cfg.UseVoiceSliderNotSe = Toggle("Follow the Voice slider", cfg.UseVoiceSliderNotSe, ref dirty,
            "On: plugin audio scales with the game's Voice volume.\n" +
            "Off: it scales with Sound Effects instead.\n\n" +
            "Voice is the better match for voicelines — it is the slider a user reaches for\n" +
            "when they want dialogue quieter without deadening combat.");

        cfg.WaitForCastToFinish = Toggle("Wait for the cast bar to finish", cfg.WaitForCastToFinish, ref dirty,
            "The action packet arrives about a slidecast window before the bar visually\n" +
            "completes, so without this a cast line fires early. Instants self-gate: no cast\n" +
            "bar means no delay.");

        cfg.FallBackToTestTone = Toggle("Test tone for unmapped actions", cfg.FallBackToTestTone, ref dirty,
            "Plays the synthesised tone when an action has no clip. Audible proof the action\n" +
            "was detected while you are setting mappings up, and noise once you are done.");

        this.DrawNativeSinkSetting(cfg, ref dirty);

        cfg.PlayTestToneOnActions = Toggle("Play clips on my actions", cfg.PlayTestToneOnActions, ref dirty,
            "Off keeps detection and the Events tab running but plays nothing — which is what\n" +
            "you want while diagnosing, since it removes the plugin as a source of sound\n" +
            "without turning off the machinery you are trying to watch.\n\n" +
            "'Warcry enabled' above is the harder switch: that one stops detection too.");

        ImGui.Separator();
        ImGui.TextUnformatted("When to stay quiet");

        // Evaluate live. Gates.Reason is only written when a cast is processed, so reading
        // the cached value showed the reason from the last action you used rather than the
        // state you are in now — which reads as a bug when you walk into a cutscene and the
        // line still says "not suppressed".
        this.plugin.Gates.IsSuppressed();

        var suppressed = this.plugin.Gates.Reason;
        if (suppressed.Length > 0)
        {
            ImGui.TextUnformatted($"  Currently suppressed: {suppressed}");
        }
        else
        {
            ImGui.TextDisabled("  Not currently suppressed.");
        }

        // NOTE: a property cannot be passed by ref, hence the return-the-value shape.
        cfg.DisableInCutscenes = Toggle("Cutscenes", cfg.DisableInCutscenes, ref dirty,
            "Covers both cutscene condition flags. Battle cries over dialogue is the\nworst thing this plugin can do, so this is on by default.");
        cfg.DisableInQuestEvents = Toggle("Quest events", cfg.DisableInQuestEvents, ref dirty,
            "Scripted quest interactions.");
        cfg.DisableInPvP = Toggle("PvP", cfg.DisableInPvP, ref dirty,
            "PvP actions are a different kit and the pace is much higher.");
        cfg.DisableInGpose = Toggle("Group pose", cfg.DisableInGpose, ref dirty, null);

        ImGui.TextDisabled("Loading screens are always suppressed and cannot be enabled.");

        // Territory mute, using wherever you are right now.
        var here = Plugin.ClientState.TerritoryType;
        var muted = cfg.BlockedTerritories.Contains(here);
        if (ImGui.Button(muted ? $"Unmute this zone ({here})" : $"Mute this zone ({here})"))
        {
            if (muted)
            {
                cfg.BlockedTerritories.Remove(here);
            }
            else
            {
                cfg.BlockedTerritories.Add(here);
            }

            dirty = true;
        }

        if (cfg.BlockedTerritories.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{cfg.BlockedTerritories.Count} zone(s) muted");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
            {
                cfg.BlockedTerritories.Clear();
                dirty = true;
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("How often");

        var cooldown = cfg.SelfCooldownSeconds;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Cooldown", ref cooldown, 0f, 15f, cooldown <= 0f ? "off" : "%.1f s"))
        {
            cfg.SelfCooldownSeconds = cooldown;
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            dirty = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Minimum gap between voicelines. The single most important setting for\n" +
                "whether this stays fun past the first hour.\n\n" +
                "A GCD rotation fires roughly every 2.5s, so 2s lets most casts through\n" +
                "while still collapsing oGCD bursts.");
        }

        cfg.SkipAutoAttacks = Toggle("Skip auto-attacks", cfg.SkipAutoAttacks, ref dirty,
            "Auto-attacks fire constantly and are never worth a line.");
        cfg.CastsOnly = Toggle("Only actions with a cast bar", cfg.CastsOnly, ref dirty,
            "Drops every instant, including most weaponskills and all oGCDs.\nVery quiet — mostly useful for casters.");

        var concurrent = cfg.MaxConcurrent;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("Max at once", ref concurrent, 1, 6))
        {
            cfg.MaxConcurrent = concurrent;
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            dirty = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Not a taste setting. The game's own sound pool is 256 entries shared\n" +
                "with the entire client and its Voice bus has only 5 tracks, so this is\n" +
                "capped low on purpose.");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Muted actions");

        if (cfg.MutedActionIds.Count == 0)
        {
            ImGui.TextDisabled("  None. Use the Mute button on a row in the Events tab.");
        }
        else
        {
            ImGui.TextDisabled($"  {cfg.MutedActionIds.Count} action(s) never play, whatever is mapped to them.");

            // Individually removable. Previously the only control was "Unmute all", so one
            // mis-click meant redoing the whole list.
            uint? unmute = null;
            foreach (var id in cfg.MutedActionIds.OrderBy(x => x))
            {
                if (ImGui.SmallButton($"unmute##{id}"))
                {
                    unmute = id;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted($"{id}  {this.ActionName(id)}");
            }

            if (unmute is { } removed)
            {
                cfg.MutedActionIds.Remove(removed);
                dirty = true;
            }

            if (ImGui.SmallButton("Unmute all"))
            {
                cfg.MutedActionIds.Clear();
                dirty = true;
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Drops by stage (this session)");
        foreach (var stage in new[]
                 {
                     DropStage.PlaybackOff, DropStage.Gate, DropStage.Throttle,
                     DropStage.NoClip, DropStage.SinkRefused, DropStage.NotPc, DropStage.NotAction,
                 })
        {
            var n = this.plugin.Diag.DropCount(stage);
            if (n > 0)
            {
                ImGui.TextDisabled($"  {stage,-10} {n}");
            }
        }

        if (dirty)
        {
            cfg.Save();
        }

        static bool Toggle(string label, bool current, ref bool dirty, string? tooltip)
        {
            var value = current;
            if (ImGui.Checkbox(label, ref value))
            {
                dirty = true;
            }

            if (tooltip is not null && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tooltip);
            }

            return value;
        }
    }

    private static readonly (SinkMode Mode, string Label, string Blurb)[] SinkModes =
    [
        (SinkMode.NativeOnly, "Game engine only",
         "Every line goes through the game's own sound engine, or it does not play at all.\n" +
         "A line the engine cannot serve is a counted drop with a reason on the Events tab —\n" +
         "never a quiet NAudio substitute. Requires Penumbra and a compiled sound pack."),
        (SinkMode.Auto, "Game engine, NAudio fallback",
         "Native first; anything the engine cannot serve this instant (a cold clip, Penumbra\n" +
         "missing) is played by NAudio instead. Forgiving, but what you hear is not always\n" +
         "the engine."),
        (SinkMode.ManagedOnly, "NAudio only",
         "The plugin mixes and plays everything itself. No Penumbra needed, no .scd\n" +
         "encoding — and none of the engine's positioning, bus routing or volume rules."),
        (SinkMode.Off, "Off",
         "Nothing plays on actions. Auditioning from the editor still works."),
    ];

    /// <summary>
    /// The sink-mode choice, with the honest caveats attached to it rather than buried
    /// in a document.
    /// </summary>
    private void DrawNativeSinkSetting(Configuration cfg, ref bool dirty)
    {
        var penumbra = this.plugin.Penumbra.PenumbraAvailable;

        var currentLabel = "?";
        foreach (var (mode, label, _) in SinkModes)
        {
            if (mode == cfg.Sink)
            {
                currentLabel = label;
            }
        }

        ImGui.SetNextItemWidth(260f);
        if (ImGui.BeginCombo("Sound output", currentLabel))
        {
            foreach (var (mode, label, blurb) in SinkModes)
            {
                var needsPenumbra = mode is SinkMode.NativeOnly or SinkMode.Auto;
                var disabled = needsPenumbra && !penumbra;

                if (disabled)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.Selectable(label, mode == cfg.Sink))
                {
                    cfg.Sink = mode;
                    dirty = true;
                }

                if (disabled)
                {
                    ImGui.EndDisabled();
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(disabled ? $"{blurb}\n\n(Requires Penumbra, which is not loaded.)" : blurb);
                }
            }

            ImGui.EndCombo();
        }

        // The consequence line: one sentence saying what the current choice means, in the
        // same words the Status tab will use when it happens.
        ImGui.TextDisabled(cfg.Sink switch
        {
            SinkMode.NativeOnly => "  Engine or silence. A refused line is a visible drop, never NAudio.",
            SinkMode.Auto => "  Engine when it can, NAudio when it cannot. Fallbacks are quiet by design.",
            SinkMode.ManagedOnly => "  NAudio for everything. The engine and Penumbra are not involved.",
            _ => "  Nothing plays on actions.",
        });

        if (!penumbra && cfg.Sink is SinkMode.NativeOnly or SinkMode.Auto)
        {
            ImGui.TextUnformatted("  ⚠ Penumbra is not loaded, so nothing can reach the engine right now.");
        }

        if (cfg.Sink is not (SinkMode.NativeOnly or SinkMode.Auto))
        {
            return;
        }

        var composite = this.plugin.Composite;
        var forge = this.plugin.Forge;

        ImGui.TextDisabled($"  {composite.NativePlays} line(s) via the engine, " +
                           $"{composite.ManagedPlays} via NAudio, " +
                           $"{forge.Count} clip(s) encoded, {forge.WarmedCount} warmed.");

        if (composite.NativeRefusal.Length > 0)
        {
            ImGui.TextDisabled($"  Last native refusal: {composite.NativeRefusal}");
        }

        if (composite.Demoted)
        {
            ImGui.TextUnformatted(cfg.Sink == SinkMode.NativeOnly
                ? "  ⚠ The native sink was demoted after repeated errors. Sink mode is Game engine only, so NOTHING IS PLAYING. See the log."
                : "  ⚠ The native sink was demoted after repeated errors; NAudio is serving everything. See the log.");
        }
    }
}
