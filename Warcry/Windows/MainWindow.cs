using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Warcry.Clips;
using Warcry.Native;
using Warcry.Profiles;
using Warcry.Detection;
using GameAction = Lumina.Excel.Sheets.Action;
using CSChar = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Warcry.Windows;

/// <summary>
/// M1 diagnostics window. Its job is to settle, empirically and in game, the questions
/// docs/PLAN.md marks as runtime-only — above all whether Header.ActionId or Header.SpellId
/// is the correct mapping key, which must be decided before the editor writes any user data.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // Name lookups allocate, so memoise. Cleared never — the sheet is immutable.
    private readonly Dictionary<uint, string> actionNames = new();
    private readonly Dictionary<uint, string> categoryNames = new();
    private readonly Dictionary<uint, float> castTimes = new();

    private bool onlyMe;
    private bool hideDropped = true;
    private bool onlyMismatch;
    private string soundFilter = "vo_";

    /// <summary>Path substituted into a replayed capture; blank replays it verbatim.</summary>
    private string soundReplayPath = string.Empty;

    private bool soundReplayOverrideNumber;
    private int soundReplayNumber;
    private readonly FileDialogManager fileDialog = new();

    private string actionSearch = string.Empty;
    private readonly List<(uint Id, string Name, string Job, bool Seen)> actionMatches = [];
    private uint selectedActionId;
    private string selectedClipHash = string.Empty;
    private uint jobFilter;
    private bool followCurrentJob = true;
    private float bulkPitch;
    private float bulkRandom;
    private bool includeUnassignable;
    private bool onlyObserved;
    private bool matchesTruncated;
    private bool jumpToMappings;
    private readonly Dictionary<uint, bool> actionJobMatch = [];
    private uint cachedCategoryJob = uint.MaxValue;
    private System.Reflection.PropertyInfo? categoryProperty;

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

            if (ImGui.BeginTabItem("Game sounds"))
            {
                this.DrawGameSounds();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Native spike"))
            {
                this.DrawSpike();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Sheets"))
            {
                this.DrawSheets();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // ---------------------------------------------------------------- Events

    /// <summary>
    /// The ActionId-vs-SpellId verdict machine. Both fields are shown with their own
    /// Action-sheet lookup side by side: whichever column consistently names the action
    /// you actually pressed is the mapping key.
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
        ImGui.SameLine();
        ImGui.Checkbox("Only ActionId != SpellId", ref this.onlyMismatch);

        // The ActionId-vs-SpellId verdict, computed rather than eyeballed.
        var agree = 0;
        var differ = 0;
        var castsMeasured = 0;
        var castSum = 0f;
        for (var i = 0; i < diag.Count; i++)
        {
            var r = diag.At(i);
            if (r.Drop != DropStage.None)
            {
                continue;
            }

            if (r.Event.ActionId == r.Event.SpellId)
            {
                agree++;
            }
            else
            {
                differ++;
            }

            if (r.Event.WasCasting && r.Event.CastRemaining > 0f)
            {
                castsMeasured++;
                castSum += r.Event.CastRemaining;
            }
        }

        var total = agree + differ;
        if (total > 0)
        {
            ImGui.TextUnformatted($"ActionId == SpellId on {agree}/{total} events.");
            ImGui.SameLine();
            if (differ == 0)
            {
                ImGui.TextDisabled("No divergence yet — the choice of key is still untested.");
            }
            else
            {
                ImGui.TextDisabled($"{differ} differ — tick the box and read those rows.");
            }
        }

        if (castsMeasured > 0)
        {
            ImGui.TextUnformatted($"Measured cast offset: {castSum / castsMeasured:0.000}s mean over {castsMeasured} casts.");
            ImGui.SameLine();
            ImGui.TextDisabled("This is what M3 schedules playback against.");
        }

        ImGui.Separator();

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg
                                    | ImGuiTableFlags.Borders
                                    | ImGuiTableFlags.ScrollY
                                    | ImGuiTableFlags.Resizable
                                    | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##events", 10, flags, ImGui.GetContentRegionAvail()))
        {
            return;
        }

        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("Caster", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("ActionId", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("ActionId -> name");
        ImGui.TableSetupColumn("SpellId", ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn("SpellId -> name");
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 92);
        // Cast time is THE discriminator for the snapshot-vs-visual-completion offset:
        // only rows with a non-zero cast can fire "early". Instants must never be delayed.
        ImGui.TableSetupColumn("Cast / +left", ImGuiTableColumnFlags.WidthFixed, 108);
        ImGui.TableSetupColumn("Voice", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Drop", ImGuiTableColumnFlags.WidthFixed, 76);
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

            if (this.onlyMismatch && ev.ActionId == ev.SpellId)
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
            ImGui.TextUnformatted(ev.SpellId.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(this.ActionName(ev.SpellId));

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
        }

        ImGui.EndTable();
    }

    // --------------------------------------------------------------- Settings

    private void DrawSettings()
    {
        var cfg = this.plugin.Config;
        var dirty = false;

        ImGui.TextUnformatted("When to stay quiet");

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

        if (cfg.MutedActionIds.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"{cfg.MutedActionIds.Count} action(s) muted");
            ImGui.SameLine();
            if (ImGui.SmallButton("Unmute all"))
            {
                cfg.MutedActionIds.Clear();
                dirty = true;
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Drops by stage (this session)");
        foreach (var stage in new[] { DropStage.Gate, DropStage.Throttle, DropStage.NoClip, DropStage.NotPc, DropStage.NotAction })
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

    // ---------------------------------------------------------------- Status

    private void DrawStatus()
    {
        var w = this.plugin.Watcher;

        ImGui.TextUnformatted("Detection");
        if (w.Installed)
        {
            ImGui.TextUnformatted($"  Hook          installed at 0x{w.HookAddress:X}");
        }
        else
        {
            ImGui.TextUnformatted("  Hook          NOT INSTALLED - signature did not resolve.");
            ImGui.TextDisabled("                Expected after a game patch; wait for a FFXIVClientStructs update.");
        }

        if (w.Tripped)
        {
            ImGui.TextUnformatted("  State         TRIPPED - too many faults, inert until reload.");
        }

        ImGui.Separator();
        this.DrawAudio();
        ImGui.Separator();
        this.DrawLocalPlayer();
        ImGui.Separator();
        DrawSoundConfig();
        ImGui.Separator();

        if (ImGui.Button("Copy diagnostics"))
        {
            ImGui.SetClipboardText(this.BuildReport());
        }
    }

    private void DrawAudio()
    {
        var sink = this.plugin.Sink;
        var sched = this.plugin.Scheduler;
        var vol = this.plugin.Volume;

        ImGui.TextUnformatted("Audio");
        ImGui.TextUnformatted($"  Sink          {sink.Status}");
        ImGui.TextUnformatted($"  Voices        {sink.ActiveVoices} / {this.plugin.Config.MaxConcurrent}");
        ImGui.TextUnformatted($"  Scheduler     {sched.PendingCount} pending, {sched.Dispatched} dispatched, {sched.Cancelled} cancelled");

        // The whole point of reading the game's config: this number should track your
        // in-game sliders live. If it stays 1.00 while you move Master, it isn't wired.
        var gameGain = vol.GainFor(0, this.plugin.Config.UseVoiceSliderNotSe);
        var bus = this.plugin.Config.UseVoiceSliderNotSe ? "Voice" : "SoundEffects";
        ImGui.TextUnformatted($"  Game gain     {gameGain:0.000}   (Master {vol.Master} x {bus} {(this.plugin.Config.UseVoiceSliderNotSe ? vol.Voice : vol.Se)} x Player {vol.Player}, /100 each)");

        if (gameGain <= 0f)
        {
            ImGui.TextDisabled("                muted by the game's own settings — nothing will play");
        }

        if (ImGui.Button("Play test tone"))
        {
            this.plugin.PlayTestTone();
        }

        ImGui.SameLine();
        var play = this.plugin.Config.PlayTestToneOnActions;
        if (ImGui.Checkbox("Tone on my actions", ref play))
        {
            this.plugin.Config.PlayTestToneOnActions = play;
            this.plugin.Config.Save();
        }

        ImGui.SameLine();
        var wait = this.plugin.Config.WaitForCastToFinish;
        if (ImGui.Checkbox("Wait for cast to finish", ref wait))
        {
            this.plugin.Config.WaitForCastToFinish = wait;
            this.plugin.Config.Save();
        }

        var gain = this.plugin.Config.MasterGain;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Plugin volume", ref gain, 0f, 2f, "%.2f"))
        {
            this.plugin.Config.MasterGain = gain;
            this.plugin.Config.Save();
        }
    }

    private void DrawLocalPlayer()
    {
        ImGui.TextUnformatted("Local player");

        var lp = Plugin.Objects.LocalPlayer;
        if (lp is null)
        {
            ImGui.TextDisabled("  not logged in");
            return;
        }

        ImGui.TextUnformatted($"  Name          {lp.Name}");
        ImGui.TextUnformatted($"  EntityId      0x{Plugin.PlayerState.EntityId:X8}");

        unsafe
        {
            var c = (CSChar*)lp.Address;
            if (c == null)
            {
                return;
            }

            ref var cd = ref c->DrawData.CustomizeData;
            var voiceId = (ushort)(c->Vfx.VoiceId & 0xFF);
            var slot = this.plugin.VoiceSlots.SlotOf(cd.Race, cd.Sex, voiceId);

            ImGui.TextUnformatted($"  Race          {cd.Race}  {RaceName(cd.Race, cd.Sex)}");
            ImGui.TextUnformatted($"  Tribe         {cd.Tribe}  {TribeName(cd.Tribe, cd.Sex)}");
            ImGui.TextUnformatted($"  Sex           {cd.Sex}  ({(cd.Sex == 0 ? "Male" : "Female")})");
            ImGui.TextUnformatted($"  VoiceId       {voiceId}");
            ImGui.SameLine();

            if (slot > 0)
            {
                ImGui.TextUnformatted($"-> Voice {slot} in the character creator");
            }
            else
            {
                ImGui.TextDisabled("-> NOT FOUND in this race+gender's CharaMakeType row");
            }

            ImGui.TextUnformatted($"  SoundCat      {c->SoundVolumeCategory} (0 Player, 1 Party, 2 Other)");
        }
    }

    private static void DrawSoundConfig()
    {
        ImGui.TextUnformatted("Game sound config");

        foreach (var key in new[] { "SoundMaster", "SoundSe", "SoundVoice", "SoundPlayer", "SoundParty", "SoundOther", "SoundMicpos" })
        {
            if (Plugin.GameConfig.System.TryGetUInt(key, out var value))
            {
                ImGui.TextUnformatted($"  {key,-12}  {value}");
            }
            else
            {
                ImGui.TextDisabled($"  {key,-12}  NOT FOUND");
            }
        }

        foreach (var key in new[] { "IsSndMaster", "IsSndSe", "IsSndVoice" })
        {
            if (Plugin.GameConfig.System.TryGetBool(key, out var value))
            {
                ImGui.TextUnformatted($"  {key,-12}  {value}  (true = muted)");
            }
            else
            {
                ImGui.TextDisabled($"  {key,-12}  NOT FOUND");
            }
        }
    }

    // ---------------------------------------------------------------- Sheets

    // --------------------------------------------------------------------- Mappings

    private void DrawMappings()
    {
        var lib = this.plugin.Clips;
        var store = this.plugin.Profiles;

        if (lib.Count == 0)
        {
            ImGui.TextDisabled("Import a clip first, on the Clips tab.");
            return;
        }

        // ---- job filter ----
        var currentJob = CurrentJobId();
        if (this.followCurrentJob && currentJob != 0 && currentJob != this.jobFilter)
        {
            this.jobFilter = currentJob;
            this.RebuildActionMatches();
        }

        ImGui.SetNextItemWidth(170);
        if (ImGui.BeginCombo("##jobfilter", this.jobFilter == 0 ? "All jobs" : JobLabel(this.jobFilter)))
        {
            if (ImGui.Selectable("All jobs"))
            {
                this.jobFilter = 0;
                this.followCurrentJob = false;
                this.RebuildActionMatches();
            }

            foreach (var job in Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>())
            {
                var abbr = job.Abbreviation.ExtractText();
                if (job.RowId == 0 || string.IsNullOrWhiteSpace(abbr))
                {
                    continue;
                }

                if (ImGui.Selectable($"{abbr} — {job.Name.ExtractText()}"))
                {
                    this.jobFilter = job.RowId;
                    this.followCurrentJob = false;
                    this.RebuildActionMatches();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        var follow = this.followCurrentJob;
        if (ImGui.Checkbox("Follow my job", ref follow))
        {
            this.followCurrentJob = follow;
            if (follow && currentJob != 0)
            {
                this.jobFilter = currentJob;
            }

            this.RebuildActionMatches();
        }

        ImGui.Separator();

        // ---- add a mapping ----
        ImGui.TextUnformatted("Assign a clip to an action");

        ImGui.SetNextItemWidth(300);
        if (ImGui.BeginCombo(
                "##actionpick",
                this.selectedActionId == 0
                    ? "(pick an action)"
                    : $"{this.ActionName(this.selectedActionId)}  #{this.selectedActionId}",
                ImGuiComboFlags.HeightLarge))
        {
            // Opening the popup should put the caret straight in the search box so you
            // can just start typing, and refresh the list in case the job changed while
            // it was closed.
            if (ImGui.IsWindowAppearing())
            {
                this.RebuildActionMatches();
                ImGui.SetKeyboardFocusHere();
            }

            var search = this.actionSearch;
            ImGui.SetNextItemWidth(-1);
            var hint = this.jobFilter == 0 ? "Search all actions e.g. Fire" : "Type to filter, or just scroll";
            if (ImGui.InputTextWithHint("##actionsearch", hint, ref search, 64))
            {
                this.actionSearch = search;
                this.RebuildActionMatches();
            }

            var onlySeen = this.onlyObserved;
            if (ImGui.Checkbox($"Only actions I've used ({this.plugin.ObservedActions.Count})", ref onlySeen))
            {
                this.onlyObserved = onlySeen;
                this.RebuildActionMatches();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "The Action sheet contains duplicates, old versions and NPC copies,\n" +
                    "so several rows can share a name and only one ever fires.\n" +
                    "This lists only ids actually observed from your character.\n\n" +
                    "Press the skill once and it appears here.");
            }

            // Conditional actions (Enchanted Riposte and friends) are not flagged
            // IsPlayerAction, so on an unfiltered search they need opting in.
            if (this.jobFilter == 0 && !this.onlyObserved)
            {
                ImGui.SameLine();
                var incl = this.includeUnassignable;
                if (ImGui.Checkbox("Include conditional", ref incl))
                {
                    this.includeUnassignable = incl;
                    this.RebuildActionMatches();
                }
            }

            ImGui.Separator();

            if (this.actionMatches.Count == 0)
            {
                if (this.jobFilter == 0 && this.actionSearch.Length < 2)
                {
                    ImGui.TextDisabled("Type at least 2 characters, or pick a job above.");
                }
                else if (this.jobFilter == 0 && !this.includeUnassignable)
                {
                    ImGui.TextDisabled("No match. Try ticking 'include conditional' above.");
                }
                else
                {
                    ImGui.TextDisabled("No match.");
                }
            }

            foreach (var (id, name, job, seen) in this.actionMatches)
            {
                var marker = seen ? "* " : "  ";
                if (ImGui.Selectable($"{marker}{name}  #{id}{(string.IsNullOrEmpty(job) ? string.Empty : $"  [{job}]")}"))
                {
                    this.selectedActionId = id;
                }

                if (ImGui.IsItemHovered())
                {
                    var prefix = seen ? "You have used this one — it is the id that actually fires.\n\n" : string.Empty;
                    ImGui.SetTooltip(prefix + this.ActionDebugInfo(id));
                }
            }

            if (this.matchesTruncated)
            {
                ImGui.Separator();
                ImGui.TextDisabled("List truncated — narrow the search.");
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        var clipLabel = this.selectedClipHash.Length > 0 && lib.TryGet(this.selectedClipHash, out var sel)
            ? sel.Info.DisplayName
            : "(pick a clip)";

        if (ImGui.BeginCombo("##clippick", clipLabel))
        {
            foreach (var clip in lib.Clips.ToArray())
            {
                if (ImGui.Selectable($"{clip.Info.DisplayName}  {clip.Info.DurationMs / 1000.0:0.00}s"))
                {
                    this.selectedClipHash = clip.Info.Hash;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        var canAdd = this.selectedActionId != 0 && this.selectedClipHash.Length > 0;
        if (!canAdd)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Assign"))
        {
            var profile = store.GetOrCreateDefault();
            store.MapClipToAction(
                profile,
                this.selectedActionId,
                this.ActionName(this.selectedActionId),
                this.selectedClipHash,
                this.plugin.BuildActionFamily(this.selectedActionId),
                Plugin.EnglishActionName(this.selectedActionId));
        }

        if (!canAdd)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Use last action"))
        {
            if (this.plugin.LastLocalCastActionId != 0)
            {
                this.selectedActionId = this.plugin.LastLocalCastActionId;
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Fills in whatever you last pressed — faster than searching.");
        }

        ImGui.Separator();

        // ---- existing mappings ----
        var totalRules = 0;
        foreach (var profile in store.Profiles)
        {
            totalRules += profile.Rules.Count;
        }

        var shown = this.ShownRuleCount(store);

        ImGui.TextUnformatted(shown == totalRules
            ? $"{totalRules} mapping(s)"
            : $"{shown} of {totalRules} mapping(s) shown");

        ImGui.SameLine();
        var fallback = this.plugin.Config.FallBackToTestTone;
        if (ImGui.Checkbox("Tone for unmapped actions", ref fallback))
        {
            this.plugin.Config.FallBackToTestTone = fallback;
            this.plugin.Config.Save();
        }

        // ---- bulk pitch ----
        if (shown > 0)
        {
            var scope = this.jobFilter == 0 ? "all" : JobLabel(this.jobFilter);

            ImGui.SetNextItemWidth(190);
            ImGui.SliderFloat("##bulkpitch", ref this.bulkPitch, -24f, 24f, $"set pitch {this.bulkPitch:+0.0;-0.0;0.0} st");
            ImGui.SameLine();
            if (ImGui.Button($"Apply to {shown} ({scope})##applypitch"))
            {
                this.ApplyToShownRules(store, r => r.PitchSemitones = this.bulkPitch);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Overwrites the pitch of every mapping listed below.\nPer-mapping values are replaced and there is no undo.");
            }

            ImGui.SetNextItemWidth(190);
            ImGui.SliderFloat("##bulkrandom", ref this.bulkRandom, 0f, 12f, $"set random +/- {this.bulkRandom:0.0} st");
            ImGui.SameLine();
            if (ImGui.Button($"Apply to {shown} ({scope})##applyrandom"))
            {
                this.ApplyToShownRules(store, r => r.PitchRandomSemitones = this.bulkRandom);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Overwrites the random spread of every mapping listed below.\nA small amount on everything is usually better than none.");
            }
        }

        if (!ImGui.BeginChild("##mappinglist", ImGui.GetContentRegionAvail(), true))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var profile in store.Profiles.ToArray())
        {
            if (store.Profiles.Count > 1)
            {
                ImGui.TextDisabled($"{profile.Name} — {profile.Match.Describe(RaceName, TribeName)}");
            }

            // Sorted for DISPLAY only, on a materialised copy. The backing list is
            // ordered by rule specificity and the resolver takes the first match, so
            // reordering it in place would silently change which clip wins.
            var visible = profile.Rules
                .Where(this.IsRuleShown)
                .Select(r => (Rule: r, ActionId: r.When.ActionIds.Count > 0 ? r.When.ActionIds[0] : 0u))
                .OrderBy(x => x.ActionId)
                .ToArray();

            foreach (var (rule, actionId) in visible)
            {
                ImGui.PushID($"{profile.Id}:{rule.Id}");

                var name = actionId != 0 ? this.ActionName(actionId) : rule.Label;

                if (ImGui.SmallButton("X"))
                {
                    store.RemoveRule(profile, rule.Id);
                    ImGui.PopID();
                    continue;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted($"{name}  #{actionId}");

                var extraIds = rule.When.ActionIds.Count - 1;
                if (extraIds > 0 || rule.When.ActionNames.Count > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(extraIds > 0 ? $"(+{extraIds})" : "(by name)");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            "Also matches:\n" +
                            (extraIds > 0 ? $"  ids {string.Join(", ", rule.When.ActionIds)}\n" : string.Empty) +
                            (rule.When.ActionNames.Count > 0 ? $"  name \"{string.Join("\", \"", rule.When.ActionNames)}\"\n" : string.Empty) +
                            "\nSo it keeps working under level sync and in variant content.");
                    }
                }

                ImGui.SameLine();
                ImGui.TextDisabled("->");

                foreach (var clipRef in rule.Clips.ToArray())
                {
                    ImGui.SameLine();
                    var label = lib.TryGet(clipRef.Hash, out var c) ? c.Info.DisplayName : "(missing)";
                    if (ImGui.SmallButton(label))
                    {
                        if (lib.TryGet(clipRef.Hash, out var play))
                        {
                            // Audition through the rule's own pitch settings, otherwise
                            // you cannot hear what you are setting.
                            this.plugin.PlayClip(
                                play,
                                CachedClip.SemitonesToRate(rule.PitchSemitones),
                                rule.PitchMode,
                                rule.PitchFftSize);
                        }
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Click to audition at this pitch. Right-click to unassign.");
                    }

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        store.RemoveClipFromRule(profile, rule, clipRef.Hash);
                        break;
                    }
                }

                if (rule.Clips.Count > 1)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"({rule.Clips.Count} variants, no immediate repeat)");
                }

                // ---- pitch gauges ----
                ImGui.Indent(22f);

                var pitch = rule.PitchSemitones;
                ImGui.SetNextItemWidth(190);
                if (ImGui.SliderFloat("##pitch", ref pitch, -24f, 24f, $"pitch {pitch:+0.0;-0.0;0.0} st"))
                {
                    rule.PitchSemitones = pitch;
                }

                // Save on release, not per-frame: SavePluginConfig-style writes are
                // synchronous and dragging a slider would stutter the game.
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    store.Save();
                }

                ImGui.SameLine();
                ImGui.TextDisabled(rule.PitchMode == PitchMode.Varispeed
                    ? $"{CachedClip.SemitonesToRate(rule.PitchSemitones):0.00}x speed"
                    : "duration held");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(130);
                var modeIndex = rule.PitchMode == PitchMode.Varispeed ? 0 : 1;
                if (ImGui.Combo("##pitchmode", ref modeIndex, "Varispeed\0Keep duration\0"))
                {
                    rule.PitchMode = modeIndex == 0 ? PitchMode.Varispeed : PitchMode.PreserveDuration;
                    store.Save();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "Varispeed: pitch and length move together, like tape speed.\n" +
                        "Cheap, clean, and usually more natural for a voice.\n\n" +
                        "Keep duration: phase vocoder. Costs CPU and adds some\n" +
                        "phasiness and transient smearing that filtering cannot remove.");
                }

                if (rule.PitchMode == PitchMode.PreserveDuration)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150);
                    var qualityIndex = rule.PitchFftSize switch
                    {
                        <= 1024 => 0,
                        <= 2048 => 1,
                        _ => 2,
                    };

                    if (ImGui.Combo("##pitchquality", ref qualityIndex, "Crisp (1024)\0Balanced (2048)\0Smooth (4096)\0"))
                    {
                        rule.PitchFftSize = qualityIndex switch { 0 => 1024, 1 => 2048, _ => 4096 };
                        store.Save();
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            "Larger windows smooth sustained vowels but smear the attack.\n" +
                            "For a short shout, Crisp usually beats Smooth.");
                    }
                }

                ImGui.SameLine();
                var spread = rule.PitchRandomSemitones;
                ImGui.SetNextItemWidth(170);
                if (ImGui.SliderFloat("##pitchrand", ref spread, 0f, 12f, $"random +/- {spread:0.0} st"))
                {
                    rule.PitchRandomSemitones = spread;
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    store.Save();
                }

                ImGui.Unindent(22f);

                ImGui.PopID();
            }
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// The single definition of "shown" — used for the count, the bulk-apply scope and
    /// the list itself, so the button can never affect something you cannot see.
    /// </summary>
    private bool IsRuleShown(VoiceRule rule)
    {
        if (rule.When.ActionIds.Count == 0)
        {
            return true; // category/job/wildcard rules are not job-specific
        }

        return this.ActionMatchesJobFilter(rule.When.ActionIds[0]);
    }

    private int ShownRuleCount(ProfileStore store)
    {
        var count = 0;
        foreach (var profile in store.Profiles)
        {
            foreach (var rule in profile.Rules)
            {
                if (this.IsRuleShown(rule))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private void ApplyToShownRules(ProfileStore store, Action<VoiceRule> apply)
    {
        foreach (var profile in store.Profiles)
        {
            foreach (var rule in profile.Rules)
            {
                if (this.IsRuleShown(rule))
                {
                    apply(rule);
                }
            }
        }

        store.Save();
    }

    private static uint CurrentJobId()
    {
        var lp = Plugin.Objects.LocalPlayer;
        return lp?.ClassJob.RowId ?? 0u;
    }

    /// <summary>Localized — for display only.</summary>
    private static string JobLabel(uint jobId)
    {
        var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        return sheet.TryGetRow(jobId, out var row) ? row.Abbreviation.ExtractText() : jobId.ToString();
    }

    /// <summary>
    /// Always English — used to match Lumina's schema-generated ClassJobCategory
    /// property names, which are not localized. Never use <see cref="JobLabel"/> for that.
    /// </summary>
    private static string EnglishJobAbbreviation(uint jobId)
    {
        var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>(Dalamud.Game.ClientLanguage.English);
        return sheet.TryGetRow(jobId, out var row) ? row.Abbreviation.ExtractText() : string.Empty;
    }

    /// <summary>
    /// A job's actions are split across the job AND its base class — a Black Mage's
    /// early spells are attributed to Thaumaturge — so filtering on the job alone would
    /// silently hide half of them.
    /// </summary>
    private static uint ParentJobOf(uint jobId)
    {
        var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        if (!sheet.TryGetRow(jobId, out var row))
        {
            return 0;
        }

        var parent = row.ClassJobParent.RowId;
        return parent == jobId ? 0 : parent;
    }

    /// <summary>
    /// Some real player actions carry no <c>ClassJob</c> at all — conditional or
    /// transformed ones such as RDM's Enchanted Riposte, which replace another action
    /// rather than being learned and slotted. For those, <c>ClassJobCategory</c> is the
    /// only signal that says which jobs can use them.
    /// </summary>
    /// <remarks>
    /// Lumina generates ClassJobCategory with ~40 bool properties named by job
    /// abbreviation (ADV, GLA, ... RDM) and no indexer, so the property is resolved by
    /// reflection once per selected job and reused for every row.
    /// </remarks>
    private bool CategoryIncludesJob(in GameAction row, uint jobId)
    {
        if (this.cachedCategoryJob != jobId)
        {
            this.cachedCategoryJob = jobId;

            // MUST be the English abbreviation. Lumina names these properties from the
            // schema (RDM, BLM, ...), but ClassJob.Abbreviation is LOCALIZED — on a
            // French client RDM reads "MRG", and GetProperty would silently return null.
            var abbreviation = EnglishJobAbbreviation(jobId);
            this.categoryProperty = string.IsNullOrWhiteSpace(abbreviation)
                ? null
                : typeof(Lumina.Excel.Sheets.ClassJobCategory)
                    .GetProperty(abbreviation, BindingFlags.Public | BindingFlags.Instance);
        }

        if (this.categoryProperty is null)
        {
            return false;
        }

        var category = row.ClassJobCategory.ValueNullable;
        return category.HasValue && this.categoryProperty.GetValue(category.Value) is true;
    }

    private bool ActionBelongsToJob(in GameAction row, uint jobId)
    {
        if (jobId == 0)
        {
            return true;
        }

        var actionJob = row.ClassJob.RowId;

        if (actionJob == jobId)
        {
            return true;
        }

        // A job's kit is split across the job and its base class — BLM's early spells
        // are attributed to THM — so the parent must be accepted too.
        var parent = ParentJobOf(jobId);
        if (parent != 0 && actionJob == parent)
        {
            return true;
        }

        // Consulted whenever the direct match fails, not only when ClassJob is unset:
        // enchanted/conditional variants can carry a ClassJob that is neither the job
        // nor its parent. The broader categories this admits — role actions, Sprint,
        // general actions — are all things the job can genuinely press, so they belong
        // in a list of "actions I might want a voiceline on".
        return this.CategoryIncludesJob(in row, jobId);
    }

    /// <summary>Why a given action did or did not land in the filtered list.</summary>
    private string ActionDebugInfo(uint actionId)
    {
        var sheet = Plugin.Data.GetExcelSheet<GameAction>();
        if (!sheet.TryGetRow(actionId, out var row))
        {
            return "no sheet row";
        }

        var job = row.ClassJob.IsValid
            ? $"{row.ClassJob.RowId} ({row.ClassJob.ValueNullable?.Abbreviation.ExtractText()})"
            : $"{row.ClassJob.RowId} (invalid)";

        var category = row.ClassJobCategory.ValueNullable?.Name.ExtractText() ?? "-";

        return $"ClassJob {job}\nCategory {row.ClassJobCategory.RowId} \"{category}\"\nIsPlayerAction {row.IsPlayerAction}";
    }

    private bool ActionMatchesJobFilter(uint actionId)
    {
        if (this.jobFilter == 0)
        {
            return true;
        }

        if (this.actionJobMatch.TryGetValue(actionId, out var cached))
        {
            return cached;
        }

        var sheet = Plugin.Data.GetExcelSheet<GameAction>();
        var result = sheet.TryGetRow(actionId, out var row) && this.ActionBelongsToJob(in row, this.jobFilter);
        this.actionJobMatch[actionId] = result;
        return result;
    }

    private void RebuildActionMatches()
    {
        this.actionMatches.Clear();
        this.actionJobMatch.Clear();
        this.matchesTruncated = false;

        // With no job selected the sheet is far too large to browse, so require a search.
        if (this.jobFilter == 0 && this.actionSearch.Length < 2)
        {
            return;
        }

        var sheet = Plugin.Data.GetExcelSheet<GameAction>();
        foreach (var row in sheet)
        {
            if (row.Icon == 0 || row.IsPvP)
            {
                continue;
            }

            var name = row.Name.ExtractText();
            if (name.Length == 0)
            {
                continue;
            }

            if (this.actionSearch.Length > 0 &&
                name.IndexOf(this.actionSearch, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var seen = this.plugin.ObservedActions.Contains(row.RowId);

            // An observed id is proof this row is the one that actually fires, which the
            // sheet alone cannot tell you. It therefore bypasses every heuristic below.
            if (!seen)
            {
                if (this.onlyObserved)
                {
                    continue;
                }

                if (this.jobFilter != 0)
                {
                    // The job constraint is itself the noise filter, so IsPlayerAction is
                    // not required here — that flag is false for conditional actions like
                    // Enchanted Riposte, which are exactly what we must not hide.
                    if (!this.ActionBelongsToJob(in row, this.jobFilter))
                    {
                        continue;
                    }
                }
                else if (!row.IsPlayerAction && !this.includeUnassignable)
                {
                    continue;
                }
            }

            var job = row.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? string.Empty;
            if (job.Length == 0 && !row.IsPlayerAction)
            {
                job = "cond.";
            }

            this.actionMatches.Add((row.RowId, name, job, seen));

            if (this.actionMatches.Count >= 300)
            {
                this.matchesTruncated = true;
                break;
            }
        }

        // Actions you have actually used float to the top — among five rows called
        // "Riposte", the one that has fired is the one you want.
        this.actionMatches.Sort((a, b) =>
        {
            if (a.Seen != b.Seen)
            {
                return a.Seen ? -1 : 1;
            }

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ------------------------------------------------------------------------ Clips

    private void DrawClips()
    {
        var lib = this.plugin.Clips;

        // Declared once per frame; the label is the shared payload key with the target below.
        if (Plugin.DragDrop.ServiceAvailable)
        {
            Plugin.DragDrop.CreateImGuiSource(
                "WarcryAudio",
                m => m.Extensions.Overlaps([".wav", ".ogg"]),
                m =>
                {
                    ImGui.Text($"Import {m.Files.Count} audio file(s) into Warcry");
                    return true;
                });
        }

        if (ImGui.Button("Import audio..."))
        {
            this.fileDialog.OpenFileDialog(
                "Import audio",
                "Audio files{.wav,.ogg}",
                (ok, paths) =>
                {
                    if (!ok)
                    {
                        return;
                    }

                    foreach (var p in paths)
                    {
                        lib.Import(p);
                    }
                },
                selectionCountMax: 0);
        }

        ImGui.SameLine();
        if (ImGui.Button("Open folder"))
        {
            Dalamud.Utility.Util.OpenLink(lib.ClipsDirectory);
        }

        ImGui.TextDisabled(Plugin.DragDrop.ServiceAvailable
            ? "wav and ogg, mono or stereo, any sample rate, up to 15s. You can also drag files onto this list."
            : "wav and ogg, mono or stereo, any sample rate, up to 15s.");

        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        var listHeight = MathF.Max(120f, avail.Y - 90f);

        if (ImGui.BeginChild("##cliplist", new Vector2(0, listHeight), true))
        {
            if (lib.Count == 0)
            {
                ImGui.TextDisabled("No clips yet. Import a wav or ogg, then use an action.");
            }

            foreach (var clip in lib.Clips.ToArray())
            {
                ImGui.PushID(clip.Info.Hash);

                if (ImGui.SmallButton("Play"))
                {
                    this.plugin.PlayClip(clip);
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    lib.Remove(clip.Info.Hash);
                    ImGui.PopID();
                    continue;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted(
                    $"{clip.Info.DisplayName}   {clip.Info.DurationMs / 1000.0:0.00}s" +
                    $"   [{clip.Info.SourceSampleRate} Hz, {(clip.Info.SourceChannels == 1 ? "mono" : "stereo")}]");

                ImGui.PopID();
            }
        }

        ImGui.EndChild();

        // Attach the drop target to the list we just drew.
        if (Plugin.DragDrop.ServiceAvailable &&
            Plugin.DragDrop.CreateImGuiTarget("WarcryAudio", out var files, out _))
        {
            foreach (var f in files)
            {
                lib.Import(f);
            }
        }

        if (lib.Errors.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"{lib.Errors.Count} problem(s):");
            foreach (var e in lib.Errors.Take(4))
            {
                ImGui.TextDisabled($"  {e}");
            }

            if (ImGui.SmallButton("Dismiss"))
            {
                lib.ClearErrors();
            }
        }
    }

    // ------------------------------------------------------- Game sounds (spike day 1)

    private void DrawGameSounds()
    {
        var w = this.plugin.SoundWatcher;

        if (!w.Installed)
        {
            ImGui.TextUnformatted("SoundManager.PlaySound did not resolve — nothing to observe.");
            return;
        }

        ImGui.TextDisabled("Read-only observation of the game's own sound engine. Native-audio spike, day 1.");

        var logging = w.Logging;
        if (ImGui.Checkbox("Log game sounds", ref logging))
        {
            w.Logging = logging;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("(the hook is inert until ticked — PlaySound fires for every sound in the game)");

        var filter = this.soundFilter;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("Path filter", "vo_", ref filter, 128))
        {
            this.soundFilter = filter;
            w.Filter = filter;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("vo_battle"))
        {
            this.soundFilter = "vo_battle";
            w.Filter = this.soundFilter;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("all"))
        {
            this.soundFilter = string.Empty;
            w.Filter = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear log"))
        {
            w.Clear();
        }

        ImGui.TextUnformatted($"{w.TotalMatched} matched of {w.TotalSeen} sounds seen; showing last {w.Count}.");
        if (w.Tripped)
        {
            ImGui.TextUnformatted("TRIPPED — too many faults, observation disabled until reload.");
        }

        ImGui.TextDisabled("'ms after cast' on a vo_battle line is the ground-truth grunt offset the native route must match.");

        // ---- replay: the game as its own oracle ----
        ImGui.Separator();
        ImGui.TextWrapped(
            "Every row is a complete, replayable PlaySound call — all eighteen arguments as the " +
            "game passed them. Replaying one that demonstrably produced audio proves the call " +
            "mechanism works; substituting only the path then tests exactly one thing.");

        var substitute = this.soundReplayPath;
        ImGui.SetNextItemWidth(360);
        if (ImGui.InputTextWithHint(
                "Substitute path", "blank = replay exactly as captured", ref substitute, 260))
        {
            this.soundReplayPath = substitute;
        }

        var overrideSound = this.soundReplayOverrideNumber;
        if (ImGui.Checkbox("Override soundNumber", ref overrideSound))
        {
            this.soundReplayOverrideNumber = overrideSound;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "soundNumber selects a sound GROUP, not a waveform. The Hrothgar file has five\n" +
                "groups; group 3 holds eight weighted choices, which is why one soundNumber\n" +
                "still gives you eight different grunts, and group 4 is empty, which means\n" +
                "selecting it plays nothing at all.\n\n" +
                "Sweep this 0-4 to hear each group. 'Inspect container' on the spike tab prints\n" +
                "the whole table.");
        }

        if (this.soundReplayOverrideNumber)
        {
            ImGui.SameLine();
            var n = this.soundReplayNumber;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderInt("##replaysnd", ref n, 0, 7))
            {
                this.soundReplayNumber = n;
            }
        }

        ImGui.TextDisabled(
            "'dist' is how far the emitter was placed from you. Near zero means the engine wants\n" +
            "listener-relative coordinates — in which case the spike's world coordinates put every\n" +
            "test out of earshot, which would explain the original silence on its own.");
        ImGui.Separator();

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders
                                    | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable
                                    | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##gamesounds", 9, flags, ImGui.GetContentRegionAvail()))
        {
            return;
        }

        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("Path");
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 78);
        ImGui.TableSetupColumn("3D", ImGuiTableColumnFlags.WidthFixed, 34);
        ImGui.TableSetupColumn("dist", ImGuiTableColumnFlags.WidthFixed, 54);
        ImGui.TableSetupColumn("snd#", ImGuiTableColumnFlags.WidthFixed, 42);
        ImGui.TableSetupColumn("ms after cast", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("after action", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 54);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        for (var i = 0; i < w.Count; i++)
        {
            var row = w.At(i);
            if (string.IsNullOrEmpty(row.Path))
            {
                continue;
            }

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.When.ToString("HH:mm:ss"));

            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{row.Path}##path{i}"))
            {
                ImGui.SetClipboardText(row.Describe());
            }

            if (ImGui.IsItemHovered())
            {
                // The whole tuple, so the values the spike used to guess at are visible.
                ImGui.SetTooltip($"{row.Describe()}\n\nClick to copy.\nThen: /warcry dumpscd {row.Path}");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Category.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.IsPositional ? "yes" : "-");

            ImGui.TableNextColumn();
            if (row.IsPositional)
            {
                ImGui.TextUnformatted($"{row.EmitterDistanceFromPlayer:0.0}");
            }
            else
            {
                ImGui.TextDisabled("-");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.SoundNumber.ToString());

            ImGui.TableNextColumn();
            if (row.MsSinceLocalCast >= 0 && row.MsSinceLocalCast < 5000)
            {
                ImGui.TextUnformatted($"{row.MsSinceLocalCast:0}");
            }
            else
            {
                ImGui.TextDisabled("-");
            }

            ImGui.TableNextColumn();
            if (row.AfterActionId != 0 && row.MsSinceLocalCast >= 0 && row.MsSinceLocalCast < 5000)
            {
                ImGui.TextUnformatted($"{row.AfterActionId} {this.ActionName(row.AfterActionId)}");
            }
            else
            {
                ImGui.TextDisabled("-");
            }

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"replay##{i}"))
            {
                this.plugin.Spike.ReplayCapture(
                    row,
                    string.IsNullOrWhiteSpace(this.soundReplayPath) ? null : this.soundReplayPath.Trim(),
                    this.soundReplayOverrideNumber ? (uint)this.soundReplayNumber : null);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Calls PlaySound with this row's arguments, byte for byte.\n\n" +
                    "Verbatim: proves the call mechanism works — a control the original spike\n" +
                    "never had. With a substitute path: proves whether a redirected path can be\n" +
                    "served, with every other argument known-good.\n\n" +
                    "The result lands in the Native spike tab.");
            }
        }

        ImGui.EndTable();
    }

    // ---------------------------------------------------------- Native spike

    private SpikeMode spikeMode = SpikeMode.StockGamePath;
    private readonly List<string> spikeEngineState = [];

    /// <summary>
    /// Modes in the order they are worth running, each with the reason it exists.
    /// </summary>
    /// <remarks>
    /// The old tab exposed eight modes as eight buttons with eight paragraphs of hover
    /// text, in the order they happened to be written. One combo, ordered by diagnostic
    /// value, says the same thing without the wall.
    /// </remarks>
    private static readonly (SpikeMode Mode, string Label, string Help)[] SpikeModes =
    [
        (SpikeMode.StockGamePath, "Stock game path  (POSITIVE CONTROL)",
            "A real, indexed game .scd. No file written, no redirect registered.\n" +
            "Nothing of ours is involved anywhere in this test.\n\n" +
            "This is the test the original spike never ran, and everything else\n" +
            "depends on it. Audible -> the call works, so any later silence is our\n" +
            "file or our redirect. Silent -> the CALL is wrong, and neither Penumbra\n" +
            "nor the writer was ever the problem."),

        (SpikeMode.NegativeControl, "Bogus path  (negative control)",
            "A path that does not exist, with no file and no redirect.\n" +
            "It must be silent. Establishes what failure sounds like."),

        (SpikeMode.OneClipEverywhere, "LADDER 3 - our alarm on one bank  (deterministic)",
            "The production shape, and the one to reach for now that the container is\n" +
            "understood.\n\n" +
            "A battle-voice SCD picks its waveform from an explicit weighted-random\n" +
            "table; soundNumber only chooses WHICH table. A caller can never select a\n" +
            "specific grunt. So instead of fighting the randomisation, this rewrites\n" +
            "the audio offset table so every index a group can roll resolves to one\n" +
            "entry holding our clip.\n\n" +
            "Use the Scope control below: the banks are NOT interchangeable. Group 1 is\n" +
            "damage-taken and group 2 is death, so retargeting everything would fire the\n" +
            "voiceline every time you got hit. Group 3 is what the game passes for an\n" +
            "action.\n\n" +
            "The clip is appended past the end of the file, so nothing is overwritten and\n" +
            "the payload has no length limit."),

        (SpikeMode.VerbatimTemplate, "Real SCD served from a synthetic path",
            "A byte-for-byte copy of a real game SCD, redirected onto a path we\n" +
            "invented. The file is definitionally valid, so silence indicts the PATH."),

        (SpikeMode.ForceSingleEntry, "Our alarm, counts forced to one entry",
            "Clones a real SCD, forces the sound/audio counts at 0x32/0x34 to 1 and\n" +
            "gives entry 0 the freed space. Selection cannot be random, and two\n" +
            "seconds of 440/880 Hz alarm cannot be mistaken for a voice grunt."),

        (SpikeMode.RetargetToExistingBank, "LADDER 1 - retarget in place  (no audio of ours)",
            "Three things differ between an untouched file and one carrying our clip:\n" +
            "the offset table is rewritten, an entry is appended past the end of file,\n" +
            "and that entry holds audio we encoded. Testing all three at once is what\n" +
            "makes a silent result unreadable. These three payloads add ONE each.\n\n" +
            "Rung 1: retargets the scoped group at the DEATH bank, in place. File length\n" +
            "unchanged, and the audio is the game's own HCA.\n\n" +
            "Death grunt -> the offset rewrite works.\n" +
            "Normal attack grunt, or silence -> it does not, and nothing downstream\n" +
            "matters."),

        (SpikeMode.AppendExistingBank, "LADDER 2 - append the game's own audio",
            "Rung 2: copies the DEATH bank's entry verbatim to past the end of the file\n" +
            "and retargets at the copy. Still not one byte of our audio.\n\n" +
            "Death grunt -> appending past EOF is fine, so only the codec is left.\n" +
            "Silence -> the engine will not follow an offset past the original file\n" +
            "length, and no encoder would ever have helped. Overwrite an existing slot\n" +
            "instead of appending."),

        (SpikeMode.InPlaceFillAll, "Our tone in every audio entry",
            "Overwrites all ~22 entries with the same payload, so whichever the\n" +
            "engine picks is ours. Sidesteps the randomisation tables entirely."),

        (SpikeMode.InPlaceAudioSwap, "Our tone in audio entry 0 only",
            "Overwrites only entry 0's header and payload. Every other byte, and\n" +
            "the file length, are identical to a file the engine accepts."),

        (SpikeMode.AuthoredPcm, "Container built from scratch (PCM)",
            "ScdWriter.BuildPcm. Never demonstrated to play -- but never\n" +
            "demonstrated to fail either, since nothing in the original spike ever\n" +
            "played. Worth re-running once the positive control passes."),

        (SpikeMode.AuthoredAdpcmTag, "Container from scratch, MS-ADPCM tag",
            "As above but tagged 0x0C, to separate container rejection from codec\n" +
            "rejection."),
    ];

    private void DrawSpike()
    {
        var spike = this.plugin.Spike;
        var penumbra = this.plugin.Penumbra;

        ImGui.TextUnformatted("Can the game's own engine play a file we wrote?");
        ImGui.TextDisabled(
            "Reopened 2026-08-17. The earlier NO-GO was reached without ever establishing that\n" +
            "PlaySound makes a noise at all. Run the positive control first — everything else\n" +
            "is uninterpretable until it passes. Background: docs/native-spike.md.");

        this.DrawSpikeInterference();

        // These are INDEPENDENT probes, not a pipeline. Numbering them 1-2-3 read as a
        // sequence and led to "I clicked the redirect button, then Run attempt, and heard
        // a Midlander" — two unrelated experiments run back to back.
        if (ImGui.CollapsingHeader("Engine state", ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.DrawSpikeEngineState(penumbra);
        }

        if (ImGui.CollapsingHeader("Probe A — does a .scd redirect apply?", ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.DrawSpikeRedirectCheck(spike, penumbra);
        }

        if (ImGui.CollapsingHeader("Probe B — play something", ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.DrawSpikePlay(spike, penumbra);
        }

        if (ImGui.CollapsingHeader("Full report", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawSpikeResult(spike);
        }
    }

    /// <summary>
    /// The plugin's own sink firing on every action sounds exactly like "random grunts" and
    /// has nothing to do with the spike. This cost the original run several days.
    /// </summary>
    private void DrawSpikeInterference()
    {
        var interfering = this.plugin.Config.Enabled
                          && this.plugin.Config.PlayTestToneOnActions
                          && this.plugin.Clips.Count > 0;

        if (interfering)
        {
            ImGui.Separator();
            ImGui.TextUnformatted(
                $"WARNING: Warcry is playing its own {this.plugin.Clips.Count} clips on your actions.");
            ImGui.TextUnformatted("Anything you hear while fighting is probably that, not the spike.");

            if (ImGui.Button("Silence the plugin for this test"))
            {
                this.plugin.Config.PlayTestToneOnActions = false;
                this.plugin.Config.Save();
            }
        }
        else if (this.plugin.Clips.Count > 0)
        {
            ImGui.TextDisabled("Plugin playback is off — any sound you hear now is the spike.");
            ImGui.SameLine();
            if (ImGui.SmallButton("re-enable"))
            {
                this.plugin.Config.PlayTestToneOnActions = true;
                this.plugin.Config.Save();
            }
        }

        ImGui.TextDisabled("Test out of combat, no target, weapon sheathed — the game's own battle voices confound everything.");
    }

    /// <summary>
    /// What the engine's mixer thinks, read live.
    /// </summary>
    /// <remarks>
    /// If <c>disabled</c> is set, or the bus is muted, or the window is inactive with
    /// playWhenInactive off, then nothing below the mixer can be heard and every other test
    /// on this tab is meaningless. That was never checked once in five days.
    /// </remarks>
    private void DrawSpikeEngineState(PenumbraBridge penumbra)
    {
        this.spikeEngineState.Clear();
        SoundDiagnostics.DescribeManager(this.spikeEngineState);

        foreach (var line in this.spikeEngineState)
        {
            ImGui.TextDisabled(line);
        }

        ImGui.TextDisabled(penumbra.PenumbraAvailable
            ? "penumbra   detected"
            : "penumbra   NOT FOUND — a game-relative path is mandatory, so redirects are unavailable");

        ImGui.TextDisabled($"sink       {this.plugin.Sink.Status}");
    }

    /// <summary>
    /// Asks Penumbra directly, with no audio involved.
    /// </summary>
    private void DrawSpikeRedirectCheck(NativeSpike spike, PenumbraBridge penumbra)
    {
        ImGui.TextDisabled("Independent of Probe B below — nothing here feeds into 'Run attempt'.");
        ImGui.TextWrapped(
            "Self-contained. Registers a temporary redirect for a fresh .scd path, then asks " +
            "Penumbra's ResolveDefaultPath what the default collection resolves it to, and prints " +
            "the answer below. Nothing is played, so there is nothing to mishear — and there is " +
            "nothing to press afterwards. The line under the button IS the result.");

        ImGui.TextDisabled(
            "The DEFAULT collection is the one that matters: a PlaySound call carries no character\n" +
            "context for Penumbra to resolve against.");

        if (!penumbra.PenumbraAvailable)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Register a redirect and verify it"))
        {
            spike.RedirectOnly();
        }

        ImGui.SameLine();
        if (ImGui.Button("Texture control"))
        {
            this.plugin.Probe.Run();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Points icon A's path at icon B's bytes — a redirect with no audio anywhere.\n" +
                "Already known to work, so this is a regression check on the IPC itself:\n" +
                "if the icons stop matching, the problem is our Penumbra plumbing, not .scd.");
        }

        if (!penumbra.PenumbraAvailable)
        {
            ImGui.EndDisabled();
        }

        // Inline, next to the button that produced it. It used to appear only in the report
        // section further down, which is how a one-click answer got missed.
        if (spike.LastRedirectCheck.Length > 0)
        {
            ImGui.TextWrapped($"-> {spike.LastRedirectCheck}");
        }

        this.DrawTextureProbeResult();
    }

    private void DrawTextureProbeResult()
    {
        var probe = this.plugin.Probe;
        if (probe.RedirectedIcon == 0)
        {
            return;
        }

        var size = new Vector2(40, 40);
        var reference = Plugin.Textures
            .GetFromGameIcon(new GameIconLookup(PenumbraProbe.ReferenceIcon)).GetWrapOrEmpty();
        var redirected = Plugin.Textures
            .GetFromGameIcon(new GameIconLookup(probe.RedirectedIcon)).GetWrapOrEmpty();

        ImGui.Image(reference.Handle, size);
        ImGui.SameLine();
        ImGui.Image(redirected.Handle, size);
        ImGui.SameLine();
        ImGui.TextDisabled("identical artwork = the redirect applied");
    }

    /// <summary>The attempt itself: which entry point, which payload, which arguments.</summary>
    private void DrawSpikePlay(NativeSpike spike, PenumbraBridge penumbra)
    {
        // ---- entry point ----
        var entry = (int)spike.Entry;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Entry point", ref entry, "PlaySound (18 args)\0PlaySystemSound (6 args)\0PlayCutsceneVoSound (1 arg)\0"))
        {
            spike.Entry = (PlayEntry)entry;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "PlaySound takes eighteen arguments, four of them booleans nobody has named,\n" +
                "and the original spike guessed at all of them.\n\n" +
                "PlaySystemSound takes six and is non-positional. PlayCutsceneVoSound takes\n" +
                "one. There is correspondingly less to get wrong, which makes them much\n" +
                "better first tests.");
        }

        // ---- mode ----
        var current = Array.FindIndex(SpikeModes, m => m.Mode == this.spikeMode);
        if (current < 0)
        {
            current = 0;
        }

        ImGui.SetNextItemWidth(340);
        if (ImGui.BeginCombo("Payload", SpikeModes[current].Label))
        {
            for (var i = 0; i < SpikeModes.Length; i++)
            {
                if (ImGui.Selectable(SpikeModes[i].Label, i == current))
                {
                    this.spikeMode = SpikeModes[i].Mode;
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(SpikeModes[i].Help);
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(SpikeModes[current].Help);
        }

        // Path source is orthogonal to the payload: a synthetic path has no sqpack index
        // entry, a real one does. When a redirect verifies as applied and the engine still
        // will not play it, that difference is the next thing to rule out.
        if (this.spikeMode is not (SpikeMode.StockGamePath or SpikeMode.NegativeControl))
        {
            var source = (int)spike.PathSource;
            ImGui.SetNextItemWidth(340);
            if (ImGui.Combo(
                    "Served from",
                    ref source,
                    "Synthetic path (fresh each attempt)\0Real indexed Vo_Battle path\0"))
            {
                spike.PathSource = (PathSource)source;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "A synthetic path exists nowhere in the game's index; a real one does.\n" +
                    "Penumbra can report a redirect as applied for either, but the engine's\n" +
                    "resource system may only accept the second.\n\n" +
                    "Real paths redirect ONCE per session — the handle is cached after the\n" +
                    "first load, so a second attempt on the same path proves nothing. Reload\n" +
                    "the plugin between real-path attempts.");
            }
        }

        // Say up front what the control is going to do. Hearing a voice that is obviously
        // not your character is the POINT, and reads as a bug if nobody says so first.
        if (this.spikeMode == SpikeMode.StockGamePath)
        {
            var stock = spike.StockPath;
            ImGui.TextWrapped(stock.Length > 0
                ? $"Will play stock game data: {stock}"
                : "No stock Vo_Battle path resolved — press 'Probe real paths' below.");
            ImGui.TextDisabled(
                "Deliberately a race, gender and language you are NOT. Hearing the wrong voice is\n" +
                "success: it cannot be your own character and it cannot be ambient. That is the\n" +
                "control the original spike never had.");
        }

        if (this.spikeMode == SpikeMode.OneClipEverywhere)
        {
            var scope = spike.TargetGroup + 1; // -1 (all) sits at index 0
            ImGui.SetNextItemWidth(340);
            if (ImGui.Combo(
                    "Scope",
                    ref scope,
                    "ALL indices (also replaces damage + death)\0" +
                    "group 0 — attack (light)\0" +
                    "group 1 — damage taken\0" +
                    "group 2 — death\0" +
                    "group 3 — attack, what the game uses for actions\0" +
                    "group 4 — unused\0"))
            {
                spike.TargetGroup = scope - 1;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Which bank to overwrite. Verified in game 2026-08-17 and corroborated by\n" +
                    "the parsed weight table and the audio lengths — damage grunts are the\n" +
                    "shortest bank, death the longest.\n\n" +
                    "Group 3 is the one to use: it is what the game passes for an action, so\n" +
                    "the character keeps grunting normally when hurt or killed.\n\n" +
                    "Set soundNumber below to match the scope, or the roll lands in a bank you\n" +
                    "did not touch.");
            }

            var codec = spike.Codec == ScdWriter.FormatMsAdPcm ? 0 : 1;
            ImGui.SetNextItemWidth(340);
            if (ImGui.Combo("Codec", ref codec, "MS-ADPCM (what the game uses)\0PCM (known rejected)\0"))
            {
                spike.Codec = codec == 0 ? ScdWriter.FormatMsAdPcm : ScdWriter.FormatPcm;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "A survey of the game's own SCDs found 267 MS-ADPCM entries, 92 HCA, and\n" +
                    "not one PCM entry. The engine loaded a PCM entry of ours and refused to\n" +
                    "decode it, which fits Format 0x01 being dead code.\n\n" +
                    "PCM is kept only so that negative result stays reproducible.");
            }

            if (spike.TargetGroup >= 0 && spike.SoundNumber != (uint)spike.TargetGroup)
            {
                ImGui.TextDisabled($"  soundNumber is {spike.SoundNumber}, scope is group {spike.TargetGroup}.");
                ImGui.SameLine();
                if (ImGui.SmallButton($"set soundNumber to {spike.TargetGroup}"))
                {
                    spike.SoundNumber = (uint)spike.TargetGroup;
                }
            }
        }

        // ---- arguments ----
        var positional = spike.IsPositional;
        if (ImGui.Checkbox("isPositional", ref positional))
        {
            spike.IsPositional = positional;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "OFF by default, deliberately.\n\n" +
                "The original spike always passed true together with the player's WORLD\n" +
                "coordinates. If the engine wants listener-relative coordinates, every test\n" +
                "was emitted hundreds of units away and attenuated to nothing — which alone\n" +
                "explains the whole NO-GO. Off removes the variable.\n\n" +
                "The Game sounds tab now shows what the game itself passes, which settles it.");
        }

        ImGui.SameLine();
        var auto = spike.AutoRelease;
        if (ImGui.Checkbox("autoRelease", ref auto))
        {
            spike.AutoRelease = auto;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "OFF: we retain the SoundData so its resource handle can be inspected — the\n" +
                "readout that says whether our file reached the engine.\n\n" +
                "ON: the engine owns and reclaims the slot, exactly as production will.");
        }

        var category = (int)spike.Category;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Category", ref category, "Player\0Party\0Other\0Unk3\0Unk4\0NoPlay\0BypassVolumeRules\0"))
        {
            spike.Category = (FFXIVClientStructs.FFXIV.Client.Sound.SoundVolumeCategory)category;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Player is what the game uses for your own character.\n" +
                "NoPlay (5) is presumably silent by design — useful as another negative control.\n" +
                "BypassVolumeRules (6) is the one to try if everything else is inaudible.");
        }

        var volume = spike.Volume;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("volume", ref volume, 0f, 1f))
        {
            spike.Volume = volume;
        }

        var sn = (int)spike.SoundNumber;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("soundNumber", ref sn, 0, 24))
        {
            spike.SoundNumber = (uint)Math.Max(0, sn);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "A battle-voice SCD holds ~22 entries and one file covers a whole race,\n" +
                "gender and language — so something must select the entry, and this is the\n" +
                "candidate. The Game sounds tab now records what the game passes.");
        }

        ImGui.Separator();

        var pos = this.plugin.CachedPlayerPosition;
        var needsPenumbra = this.spikeMode is not (SpikeMode.StockGamePath or SpikeMode.NegativeControl);

        if (needsPenumbra && !penumbra.PenumbraAvailable)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Run attempt"))
        {
            spike.Run(this.spikeMode, pos);
        }

        if (needsPenumbra && !penumbra.PenumbraAvailable)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("Replay (warm)") && spike.LastPath.Length > 0)
        {
            spike.ReplayLast(pos);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Re-plays the previous path with no rewrite and no re-registration.\n" +
                "Every fresh attempt is a cold asynchronous load; this one is warm.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Probe real paths"))
        {
            spike.ProbeRealPaths();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Lists which real Vo_Battle paths this installation actually contains.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Inspect container"))
        {
            spike.Inspect();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Dumps the template's audio entries and its weighted-random sound groups.\n" +
                "Also: /warcry scdinfo <game path>");
        }

        ImGui.SameLine();
        if (ImGui.Button("Survey formats"))
        {
            // Everything the watcher has seen this session feeds the survey, so logging
            // sounds first (with the filter cleared) makes it much more representative.
            var observed = new List<string>();
            var watcher = this.plugin.SoundWatcher;
            for (var i = 0; i < watcher.Count; i++)
            {
                var path = watcher.At(i).Path;
                if (!string.IsNullOrEmpty(path))
                {
                    observed.Add(path);
                }
            }

            spike.SurveyFormats(observed);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Which audio formats the game's own .scd files actually contain.\n\n" +
                "The ladder has narrowed the failure to 'the engine will not decode the audio\n" +
                "entry we wrote'. Rather than guess at a codec and build an encoder on a hunch,\n" +
                "find out what the engine demonstrably eats and copy that structure.\n\n" +
                "Reads every .scd path logged on the Game sounds tab this session, plus a dense\n" +
                "probe of sound/foot/dev/. Clear the filter and log for a minute first.");
        }

        if (spike.Tracking)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("watching...");
        }

        if (spike.LastPath.Length > 0)
        {
            ImGui.TextDisabled($"last path: {spike.LastPath}");
        }

        ImGui.TextDisabled(
            "To replay a call the GAME made, argument for argument, use the Game sounds tab.");
    }

    private static void DrawSpikeResult(NativeSpike spike)
    {
        if (spike.LastVerdict.Length > 0)
        {
            ImGui.TextUnformatted($"Verdict: {spike.LastVerdict}");
            ImGui.Separator();
        }

        if (spike.Report.Count == 0)
        {
            ImGui.TextDisabled("No attempt yet.");
            return;
        }

        foreach (var line in spike.Report)
        {
            if (line.StartsWith("FAIL", StringComparison.Ordinal))
            {
                ImGui.TextUnformatted(line);
            }
            else
            {
                ImGui.TextDisabled(line);
            }
        }

        if (ImGui.SmallButton("Copy report"))
        {
            ImGui.SetClipboardText(string.Join('\n', spike.Report));
        }
    }

    private void DrawSheets()
    {
        if (ImGui.CollapsingHeader("ActionCategory rows"))
        {
            ImGui.TextDisabled("1 and 2 are source-confirmed as Auto-attack and Spell. 3 and 4 were inferred.");
            var cats = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.ActionCategory>();
            foreach (var cat in cats)
            {
                var name = cat.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    ImGui.TextUnformatted($"  {cat.RowId,3}  {name}");
                }
            }
        }

        if (ImGui.CollapsingHeader("Voice slots for your race + gender"))
        {
            var lp = Plugin.Objects.LocalPlayer;
            if (lp is null)
            {
                ImGui.TextDisabled("  not logged in");
            }
            else
            {
                unsafe
                {
                    var c = (CSChar*)lp.Address;
                    ref var cd = ref c->DrawData.CustomizeData;
                    var mine = (ushort)(c->Vfx.VoiceId & 0xFF);
                    var voices = this.plugin.VoiceSlots.VoicesFor(cd.Race, cd.Sex);

                    ImGui.TextDisabled($"  race {cd.Race}, sex {cd.Sex} — {voices.Count} entries");
                    for (var i = 0; i < voices.Count; i++)
                    {
                        var marker = voices[i] == mine ? "  <- you" : string.Empty;
                        ImGui.TextUnformatted($"  Voice {i + 1,2}   id {voices[i],3}{marker}");
                    }
                }
            }
        }

        if (ImGui.CollapsingHeader("All voice ranges"))
        {
            ImGui.TextDisabled("Confirms whether each race+gender owns a contiguous exclusive range.");
            foreach (var (race, sex, voices) in this.plugin.VoiceSlots.All())
            {
                var min = ushort.MaxValue;
                var max = ushort.MinValue;
                foreach (var v in voices)
                {
                    if (v < min) { min = v; }
                    if (v > max) { max = v; }
                }

                ImGui.TextUnformatted($"  race {race,2} sex {sex}  {voices.Length,2} voices, {min}-{max}   {RaceName(race, sex)}");
            }
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

    private string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Warcry M1 diagnostics");
        sb.AppendLine($"hookInstalled={this.plugin.Watcher.Installed} tripped={this.plugin.Watcher.Tripped} eventsSeen={this.plugin.Diag.TotalSeen}");

        var lp = Plugin.Objects.LocalPlayer;
        if (lp is not null)
        {
            unsafe
            {
                var c = (CSChar*)lp.Address;
                if (c != null)
                {
                    ref var cd = ref c->DrawData.CustomizeData;
                    var voiceId = (ushort)(c->Vfx.VoiceId & 0xFF);
                    sb.AppendLine($"race={cd.Race}({RaceName(cd.Race, cd.Sex)}) tribe={cd.Tribe}({TribeName(cd.Tribe, cd.Sex)}) sex={cd.Sex} voiceId={voiceId} slot={this.plugin.VoiceSlots.SlotOf(cd.Race, cd.Sex, voiceId)}");
                }
            }
        }

        sb.AppendLine("--- recent events (newest first) ---");
        var shown = 0;
        for (var i = 0; i < this.plugin.Diag.Count && shown < 25; i++)
        {
            var row = this.plugin.Diag.At(i);
            if (row.Drop is DropStage.NotPc or DropStage.NotAction)
            {
                continue;
            }

            var ev = row.Event;
            sb.AppendLine(
                $"{row.When:HH:mm:ss} {(ev.IsLocalPlayer ? "ME " : "   ")}" +
                $"actionId={ev.ActionId}(\"{this.ActionName(ev.ActionId)}\") " +
                $"spellId={ev.SpellId}(\"{this.ActionName(ev.SpellId)}\") " +
                $"cat={this.CategoryOf(ev.ActionId)} cast={this.CastSecondsOf(ev.ActionId):0.0}s " +
                $"wasCasting={ev.WasCasting} castLeft={ev.CastRemaining:0.000} " +
                $"var={ev.AnimationVariation} " +
                $"gseq={ev.GlobalSequence} sseq={ev.SourceSequence} targets={ev.NumTargets}");
            shown++;
        }

        return sb.ToString();
    }
}
