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
    private readonly Dictionary<uint, string> zoneNames = new();

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

        // Scrolling belongs to the per-tab child regions (see TabBody), so the window
        // itself must never take a scroll of its own and drag the tab bar off the top.
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public void Dispose() => this.fileDialog.Reset();

    /// <summary>
    /// The file dialog must be pumped every frame, outside this window's ImGui scope.
    /// ⚠ PostDraw is skipped on frames drawn in the error style, and runs before
    /// ImGui.PopID() for namespaced windows — see docs/PLAN.md 5.8.
    /// </summary>
    public override void PostDraw() => this.fileDialog.Draw();

    /// <summary>
    /// Tabs in the order the job is actually done: import clips, decide which action plays
    /// them, decide whose actions count, tune how they behave, then — only if something is
    /// wrong — look at what the plugin heard and how it is.
    /// </summary>
    /// <remarks>
    /// The old order opened on Events, i.e. on a diagnostic log, before the user had
    /// imported anything. Setup happens once and troubleshooting is occasional, but a first
    /// run has to lead somewhere useful, and "Clips" is where every path starts.
    /// </remarks>
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

    /// <summary>
    /// Draws one tab with its body inside a child region that fills whatever is left of the
    /// window.
    /// </summary>
    /// <remarks>
    /// The child is what pins the tab bar: drawn straight into the window, a long body grows
    /// the window's own scroll region and carries the bar off the top with it. Bodies size
    /// themselves against <c>GetContentRegionAvail()</c>, which now measures the child, so
    /// they need no changes — and the window itself is left with nothing to scroll.
    /// </remarks>
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

    /// <summary>A hover tooltip on the item just submitted.</summary>
    /// <param name="flags">
    /// <c>AllowWhenDisabled</c> for a control that is greyed out, where the tooltip is
    /// usually the only place the reason lives.
    /// </param>
    /// <remarks>
    /// The argument is built whether or not the item is hovered, so this is for literal
    /// text and for controls drawn once a frame. Inside a row loop — or where the text
    /// costs a sheet lookup — keep the explicit <c>if (ImGui.IsItemHovered())</c>.
    /// </remarks>
    private static void Tip(string text, ImGuiHoveredFlags flags = ImGuiHoveredFlags.None)
    {
        if (ImGui.IsItemHovered(flags))
        {
            ImGui.SetTooltip(text);
        }
    }

    /// <summary>
    /// A slider that reports its edit as finished only on release.
    /// </summary>
    /// <remarks>
    /// The value is written back every frame so the sound changes as you drag, but
    /// <paramref name="dirty"/> is set only on release: <c>SavePluginConfig</c> is
    /// synchronous and writes through IReliableFileStorage, so saving mid-drag would
    /// rewrite the whole config on every frame of it.
    /// </remarks>
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

    /// <inheritdoc cref="Slider"/>
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

    /// <summary>
    /// The two ways to name a player that both the People tab and a mapping set's target
    /// need: type it, or take your current target. Returns the player to add, or null.
    /// </summary>
    /// <remarks>
    /// Typing matches on any world — a name is all we are given. The target button is the
    /// only route that captures the home world, so it is the one that leaves a same-name
    /// player on another world unaffected. Callers wrap this in their own ImGui ID scope.
    /// </remarks>
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

    /// <summary>
    /// Action.Cast100ms in seconds, 0 for instants. This is the gate for the
    /// snapshot-offset feature: ActionEffectHandler.Receive fires at snapshot, which
    /// precedes the client-side cast bar completing by roughly the slidecast window.
    /// Instants have no such gap and must never be delayed.
    /// </summary>
    /// <remarks>
    /// Its own memo, NOT <c>ClipResolver.GetActionKey</c>, however duplicated that looks.
    /// The resolver's cache is Lane A: the ActionEffect detour writes it on the game main
    /// thread (docs/PLAN.md 4). Reading it from Draw means two threads mutating one
    /// <c>Dictionary</c>, which corrupts its buckets and takes the client down with no
    /// managed exception — the Events tab calls this once per visible row per frame, so it
    /// is the worst possible place to cross that line.
    /// </remarks>
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

    /// <summary>
    /// The zone's name, for a TerritoryType id.
    /// </summary>
    /// <remarks>
    /// "Mute this zone (1185)" tells a player nothing they can act on. The id stays
    /// available in the diagnostics report, where it is the useful form.
    /// </remarks>
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
