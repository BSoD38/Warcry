using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Warcry.Game;
using GameAction = Lumina.Excel.Sheets.Action;

namespace Warcry.Windows;

// The main window: mapping editor, clip library, sound-pack state, settings, and the
// Events/Status diagnostics.
// This file holds the shell — the tab bar, the shared sheet-lookup helpers, and the state
// more than one tab touches. Each tab is drawn by its own partial-class file
// (MainWindow.Events.cs and friends), which owns that tab's private state.
public sealed partial class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // Name lookups allocate, so memoise. Cleared never — the sheet is immutable.
    private readonly Dictionary<uint, string> actionNames = new();
    private readonly Dictionary<uint, string> categoryNames = new();
    private readonly Dictionary<uint, float> castTimes = new();
    private readonly Dictionary<uint, string> zoneNames = new();

    // Pumped by PostDraw here; opened by the Clips tab.
    private readonly FileDialogManager fileDialog = new();

    // Set by the Events tab's "map" button to switch to the Mappings tab.
    private bool jumpToMappings;

    public MainWindow(Plugin plugin) : base("Warcry###WarcryMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // Scrolling belongs to the per-tab child regions (see TabBody), so the window
        // itself must never take a scroll of its own and drag the tab bar off the top.
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public void Dispose() => this.fileDialog.Reset();

    // The file dialog must be pumped every frame, outside this window's ImGui scope.
    // ⚠ PostDraw is skipped on frames drawn in the error style, and runs before
    // ImGui.PopID() for namespaced windows — see docs/PLAN.md 5.8.
    public override void PostDraw() => this.fileDialog.Draw();

    // Tabs in the order the job is done: import clips, decide which action plays them,
    // decide whose actions count, tune how they behave, then — only if something is wrong —
    // look at what the plugin heard and how it is.
    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##warcrytabs"))
        {
            return;
        }

        TabBody("Clips", this.DrawClips);

        // The Events tab's "map" button asks for the Actions tab; the request lasts one frame.
        var mappingFlags = this.jumpToMappings ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        this.jumpToMappings = false;
        TabBody("Actions", this.DrawMappings, mappingFlags);

        TabBody("People", this.DrawPeople);
        TabBody("Settings", this.DrawSettings);
        TabBody("Events", this.DrawEvents);
        TabBody("Status", this.DrawStatus);

        ImGui.EndTabBar();
    }

    // The body goes in a child region filling the rest of the window, and that child is what
    // pins the tab bar: drawn straight into the window, a long body grows the window's own
    // scroll region and carries the bar off the top. Bodies size themselves against
    // GetContentRegionAvail(), which measures the child.
    private static void TabBody(string label, Action draw, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
    {
        if (!ImGui.BeginTabItem(label, flags))
        {
            return;
        }

        if (ImGui.BeginChild($"##body{label}", Vector2.Zero, false))
        {
            draw();
        }

        ImGui.EndChild();
        ImGui.EndTabItem();
    }

    // ---------------------------------------------------------------- widgets

    // A hover tooltip on the item just submitted. Pass AllowWhenDisabled for a greyed-out
    // control, where the tooltip is usually the only place the reason lives.
    // The argument is built whether or not the item is hovered, so this is for literal text
    // and for controls drawn once a frame. Inside a row loop, or where the text costs a
    // sheet lookup, keep the explicit if (ImGui.IsItemHovered()).
    private static void Tip(string text, ImGuiHoveredFlags flags = ImGuiHoveredFlags.None)
    {
        if (ImGui.IsItemHovered(flags))
        {
            ImGui.SetTooltip(text);
        }
    }

    // The value is written back every frame so the sound changes as you drag, but dirty is
    // set only on release: SavePluginConfig is synchronous and writes through
    // IReliableFileStorage, so saving mid-drag would rewrite the whole config per frame.
    private static float Slider(
        string label, float value, float min, float max, string format, ref bool dirty, string? tooltip = null)
    {
        ImGui.SetNextItemWidth(220f);
        ImGui.SliderFloat(label, ref value, min, max, format);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            dirty = true;
        }

        if (tooltip is not null)
        {
            Tip(tooltip);
        }

        return value;
    }

    // As Slider.
    private static int SliderInt(
        string label, int value, int min, int max, string format, ref bool dirty, string? tooltip = null)
    {
        ImGui.SetNextItemWidth(220f);
        ImGui.SliderInt(label, ref value, min, max, format);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            dirty = true;
        }

        if (tooltip is not null)
        {
            Tip(tooltip);
        }

        return value;
    }

    // Type a player's name or take your current target; returns the player to add, or null.
    // Typing matches on any world, since a name is all we are given. The target button is
    // the only route that captures the home world, so it is the one that leaves a same-name
    // player on another world unaffected. Callers wrap this in their own ImGui ID scope.
    private static NamedPlayer? DrawPlayerEntry(string addLabel, ref string buffer)
    {
        ImGui.SetNextItemWidth(200f);
        var submitted = ImGui.InputTextWithHint(
            "##name", "Character name", ref buffer, PlayerId.MaxNameBytes,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if ((ImGui.Button(addLabel) || submitted) && buffer.Trim().Length > 0)
        {
            var typed = new NamedPlayer { Name = buffer.Trim() };
            buffer = string.Empty;
            return typed;
        }

        // Disabled rather than hidden, so the route is discoverable before you have
        // targeted anybody.
        var target = Plugin.Targets.Target as IPlayerCharacter;
        ImGui.SameLine();

        if (target is null)
        {
            ImGui.BeginDisabled();
        }

        var take = ImGui.Button(target is null ? "Add my target" : $"Add {target.Name.TextValue}");

        if (target is null)
        {
            ImGui.EndDisabled();
        }

        Tip(
            target is null
                ? "Target a player in game and this fills itself in, home world included."
                : "Adds them exactly, home world included, so a same-name player on another\nworld is not affected.",
            ImGuiHoveredFlags.AllowWhenDisabled);

        return take && target is not null
            ? new NamedPlayer
            {
                Name = target.Name.TextValue,
                World = target.HomeWorld.RowId,
                WorldName = target.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty,
            }
            : null;
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

    // Action.Cast100ms in seconds, 0 for instants — the gate for the snapshot offset, since
    // instants have no gap between snapshot and the cast bar completing and must never be
    // delayed.
    // Its own memo, NOT ClipResolver.GetActionKey, however duplicated that looks: the
    // resolver's cache is Lane A, written by the detour on the game main thread
    // (docs/PLAN.md 4). Reading it from Draw means two threads mutating one Dictionary,
    // which corrupts its buckets and takes the client down with no managed exception.
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

    // The zone's name, for a TerritoryType id. "Mute this zone (1185)" tells a player
    // nothing they can act on; the id stays in the diagnostics report, where it is useful.
    private string ZoneName(uint territory)
    {
        if (territory == 0)
        {
            return "this zone";
        }

        if (this.zoneNames.TryGetValue(territory, out var cached))
        {
            return cached;
        }

        var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        var name = string.Empty;
        if (sheet.TryGetRow(territory, out var row))
        {
            name = row.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"zone {territory}";
        }

        this.zoneNames[territory] = name;
        return name;
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
