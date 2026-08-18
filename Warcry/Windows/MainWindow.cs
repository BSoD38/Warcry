using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using GameAction = Lumina.Excel.Sheets.Action;

namespace Warcry.Windows;

/// <summary>
/// The main window: mapping editor, clip library, sound-pack state, settings, and the
/// Events/Status diagnostics that explain what the plugin heard and why it did or did
/// not play.
/// </summary>
/// <remarks>
/// This file holds the shell — the tab bar, the shared sheet-lookup helpers, and the
/// state more than one tab touches. Each tab is drawn by its own partial-class file
/// (<c>MainWindow.Events.cs</c> and friends), which also owns that tab's private state.
/// </remarks>
public sealed partial class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // Name lookups allocate, so memoise. Cleared never — the sheet is immutable.
    private readonly Dictionary<uint, string> actionNames = new();
    private readonly Dictionary<uint, string> categoryNames = new();
    private readonly Dictionary<uint, float> castTimes = new();

    /// <summary>Pumped by PostDraw here; opened by the Clips tab.</summary>
    private readonly FileDialogManager fileDialog = new();

    /// <summary>Set by the Events tab's "map" button to switch to the Mappings tab.</summary>
    private bool jumpToMappings;

    public MainWindow(Plugin plugin) : base("Warcry###WarcryMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() => this.fileDialog.Reset();

    /// <summary>
    /// The file dialog must be pumped every frame, outside this window's ImGui scope.
    /// ⚠ PostDraw is skipped on frames drawn in the error style, and runs before
    /// ImGui.PopID() for namespaced windows — see docs/PLAN.md 5.8.
    /// </summary>
    public override void PostDraw() => this.fileDialog.Draw();

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##warcrytabs"))
        {
            if (ImGui.BeginTabItem("Events"))
            {
                this.DrawEvents();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                this.DrawSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Status"))
            {
                this.DrawStatus();
                ImGui.EndTabItem();
            }

            var mappingFlags = this.jumpToMappings ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            this.jumpToMappings = false;
            if (ImGui.BeginTabItem("Mappings", mappingFlags))
            {
                this.DrawMappings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Clips"))
            {
                this.DrawClips();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Sound pack"))
            {
                this.DrawSoundPack();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // ---------------------------------------------------------------- helpers

    private string ActionName(uint id)
    {
        if (id == 0)
        {
            return "-";
        }

        if (this.actionNames.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var sheet = Plugin.Data.GetExcelSheet<GameAction>();
        var name = sheet.TryGetRow(id, out var row) ? row.Name.ExtractText() : string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "(no row)";
        }

        this.actionNames[id] = name;
        return name;
    }

    /// <summary>
    /// Action.Cast100ms in seconds, 0 for instants. This is the gate for the
    /// snapshot-offset feature: ActionEffectHandler.Receive fires at snapshot, which
    /// precedes the client-side cast bar completing by roughly the slidecast window.
    /// Instants have no such gap and must never be delayed.
    /// </summary>
    private float CastSecondsOf(uint actionId)
    {
        if (this.castTimes.TryGetValue(actionId, out var cached))
        {
            return cached;
        }

        var sheet = Plugin.Data.GetExcelSheet<GameAction>();
        var seconds = sheet.TryGetRow(actionId, out var row) ? row.Cast100ms / 10f : 0f;
        this.castTimes[actionId] = seconds;
        return seconds;
    }

    private string CategoryOf(uint actionId)
    {
        if (this.categoryNames.TryGetValue(actionId, out var cached))
        {
            return cached;
        }

        var sheet = Plugin.Data.GetExcelSheet<GameAction>();
        var name = "-";
        if (sheet.TryGetRow(actionId, out var row))
        {
            var cat = row.ActionCategory.ValueNullable;
            name = cat.HasValue ? $"{row.ActionCategory.RowId} {cat.Value.Name.ExtractText()}" : row.ActionCategory.RowId.ToString();
        }

        this.categoryNames[actionId] = name;
        return name;
    }

    private static string RaceName(byte race, byte sex)
    {
        var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Race>();
        if (!sheet.TryGetRow(race, out var row))
        {
            return string.Empty;
        }

        return sex == 0 ? row.Masculine.ExtractText() : row.Feminine.ExtractText();
    }

    private static string TribeName(byte tribe, byte sex)
    {
        var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Tribe>();
        if (!sheet.TryGetRow(tribe, out var row))
        {
            return string.Empty;
        }

        return sex == 0 ? row.Masculine.ExtractText() : row.Feminine.ExtractText();
    }

}
