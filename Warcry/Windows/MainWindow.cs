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
        ImGui.Separator();

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders
                                    | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable
                                    | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##gamesounds", 7, flags, ImGui.GetContentRegionAvail()))
        {
            return;
        }

        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("Path");
        ImGui.TableSetupColumn("Vol", ImGuiTableColumnFlags.WidthFixed, 46);
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 78);
        ImGui.TableSetupColumn("3D", ImGuiTableColumnFlags.WidthFixed, 34);
        ImGui.TableSetupColumn("ms after cast", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("after action", ImGuiTableColumnFlags.WidthFixed, 150);
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
            if (ImGui.Selectable(row.Path))
            {
                ImGui.SetClipboardText(row.Path);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"Click to copy.\nThen: /warcry dumpscd {row.Path}");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.Volume:0.00}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Category.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.IsPositional ? "yes" : "-");

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
        }

        ImGui.EndTable();
    }

    // ---------------------------------------------------------- Native spike

    private void DrawSpike()
    {
        var spike = this.plugin.Spike;
        var penumbra = this.plugin.Penumbra;

        ImGui.TextUnformatted("Can the game's own engine play a file we wrote?");
        ImGui.TextDisabled("Go/no-go criteria are in docs/PLAN.md section 6.");
        ImGui.Separator();

        ImGui.TextUnformatted(penumbra.PenumbraAvailable
            ? "  Penumbra   detected"
            : "  Penumbra   NOT FOUND — required, PlaySound takes a game path, not a file path");

        ImGui.TextUnformatted($"  Sink today  {this.plugin.Sink.Status}");

        // Critical control. With clips imported and action playback on, the plugin's own
        // managed sink fires on every action — which sounds exactly like "random grunts"
        // and has nothing to do with the spike. Any sound heard during a spike test must
        // come from the spike.
        var interfering = this.plugin.Config.Enabled
                          && this.plugin.Config.PlayTestToneOnActions
                          && this.plugin.Clips.Count > 0;

        if (interfering)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"WARNING: the plugin is currently playing its own {this.plugin.Clips.Count} clips on your actions.");
            ImGui.TextUnformatted("Anything you hear while fighting is probably that, not the spike.");

            if (ImGui.Button("Silence the plugin for this test"))
            {
                this.plugin.Config.PlayTestToneOnActions = false;
                this.plugin.Config.Save();
            }
        }
        else if (this.plugin.Clips.Count > 0)
        {
            ImGui.TextDisabled("  Plugin playback is off — any sound you hear now is the spike.");
            ImGui.SameLine();
            if (ImGui.SmallButton("re-enable"))
            {
                this.plugin.Config.PlayTestToneOnActions = true;
                this.plugin.Config.Save();
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Test conditions: out of combat, no target, weapon sheathed.");
        ImGui.TextDisabled("The game plays its own battle voices when you fight, which confounds everything.");
        ImGui.Separator();

        var lp = Plugin.Objects.LocalPlayer;
        var pos = lp?.Position ?? System.Numerics.Vector3.Zero;
        var cat = FFXIVClientStructs.FFXIV.Client.Sound.SoundVolumeCategory.Player;

        // E (absolute filesystem path, no Penumbra) is REMOVED — it hard-crashes the
        // client. See docs/native-spike.md: the resource category is parsed from the
        // leading path segment, so "C:\..." indexes ResourceGraph out of bounds inside
        // FindResourceHandle. A path-redirect mechanism is genuinely required.
        ImGui.TextDisabled("A game-relative path is mandatory — an absolute one crashes the client (see native-spike.md).");
        ImGui.Separator();

        if (!penumbra.PenumbraAvailable)
        {
            ImGui.BeginDisabled();
        }

        // ---- texture probe: does our Penumbra integration do anything at all? ----
        var probe = this.plugin.Probe;

        ImGui.TextUnformatted("Does our Penumbra redirect work AT ALL? (no audio involved)");

        if (ImGui.Button("T: redirect an icon to another icon's bytes"))
        {
            probe.Run();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Points icon A's path at icon B's bytes. Both are real game files, so\n" +
                "nothing can render corrupt.\n\n" +
                "Same artwork below = the redirect APPLIED, so our IPC is fine and .scd is\n" +
                "special (Penumbra treats it as a protected file type).\n\n" +
                "Different artwork = weaker evidence: Dalamud may load textures via Lumina,\n" +
                "bypassing the game's resource system and Penumbra with it.");
        }

        if (probe.RedirectedIcon != 0)
        {
            var size = new Vector2(56, 56);

            var reference = Plugin.Textures
                .GetFromGameIcon(new GameIconLookup(PenumbraProbe.ReferenceIcon)).GetWrapOrEmpty();
            var redirected = Plugin.Textures
                .GetFromGameIcon(new GameIconLookup(probe.RedirectedIcon)).GetWrapOrEmpty();

            ImGui.TextUnformatted($"reference {PenumbraProbe.ReferenceIcon}");
            ImGui.SameLine(180);
            ImGui.TextUnformatted($"redirected {probe.RedirectedIcon}");

            ImGui.Image(reference.Handle, size);
            ImGui.SameLine(180);
            ImGui.Image(redirected.Handle, size);

            ImGui.TextUnformatted("Identical artwork means the redirect applied.");
        }

        foreach (var line in probe.Report)
        {
            ImGui.TextDisabled($"  {line}");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Are the grunts even ours? Run these two before anything else.");

        if (ImGui.Button("R: REDIRECT ONLY — register a mod, never call PlaySound"))
        {
            spike.RedirectOnly();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Writes the file and registers the Penumbra redirect, then stops.\n" +
                "PlaySound is NOT called.\n\n" +
                "A grunt here means Penumbra is redrawing your character and reloading its\n" +
                "voice — and every 'it played' result so far, mode A included, was that.\n" +
                "Press it several times.");
        }

        ImGui.SameLine();
        if (ImGui.Button("P: probe real paths"))
        {
            spike.ProbeRealPaths();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Lists which real Vo_Battle paths your install actually contains.\n" +
                "Mode B needs one; my earlier guesses did not resolve, which is why B\n" +
                "never produced a result.");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("N should be completely silent (it was, 40/40):");

        if (ImGui.Button("N: NEGATIVE CONTROL — bogus path, no file, no redirect"))
        {
            spike.Run(SpikeMode.NegativeControl, pos, cat);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Plays a path that does not exist, with nothing written and nothing\n" +
                "redirected. It MUST be silent.\n\n" +
                "If it produces grunts, then PlaySound is replaying whatever was left in\n" +
                "the recycled SoundData pool slot — and every 'it played' result so far,\n" +
                "mode A included, is void.\n\n" +
                "This should have been the first test in the spike, not the last.");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Our file is verified byte-correct, so these test selection, not the writer:");

        var auto = spike.AutoRelease;
        if (ImGui.Checkbox("autoRelease (production behaviour, disables polling)", ref auto))
        {
            spike.AutoRelease = auto;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "OFF: we retain the SoundData to poll it, then force-release it on the next\n" +
                "press. That churns a 256-slot pool shared with the whole game and is the\n" +
                "prime suspect for the intermittent silence.\n\n" +
                "ON: the engine owns and reclaims the slot, exactly as production will.\n" +
                "No measurement possible — judge by ear. Try repeated presses both ways.");
        }

        var sn = (int)spike.SoundNumber;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("soundNumber", ref sn, 0, 24))
        {
            spike.SoundNumber = (uint)Math.Max(0, sn);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "We have only ever passed 0. If this selects a specific entry rather than\n" +
                "meaning 'any', it is the answer to the randomisation.\n\n" +
                "Sweep it with F: if a particular value always plays our tone, selection\n" +
                "is deterministic and no container rebuild is needed at all.");
        }

        if (ImGui.Button("H: force ONE entry, 1 second long"))
        {
            spike.Run(SpikeMode.ForceSingleEntry, pos, cat);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Sets the template's sound and audio counts to 1, so there is exactly one\n" +
                "candidate and selection cannot be random. Entry 0 then gets all the freed\n" +
                "space, which lifts the 60ms ceiling to over a second.\n\n" +
                "A full second of falling tone is impossible to mistake for a voice grunt —\n" +
                "which finally settles whether we are hearing OUR audio or the game's.");
        }

        ImGui.Separator();

        if (ImGui.Button("G: fill EVERY audio entry"))
        {
            spike.Run(SpikeMode.InPlaceFillAll, pos, cat);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Overwrites all ~22 entries with the same payload, so whichever the engine\n" +
                "picks is ours. Sidesteps the layout tables entirely.\n\n" +
                "If this plays reliably, we have a working native path today — no need to\n" +
                "understand the randomisation at all.");
        }

        ImGui.SameLine();
        if (ImGui.Button("F: swap entry 0 only"))
        {
            spike.Run(SpikeMode.InPlaceAudioSwap, pos, cat);
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Every attempt above uses a FRESH path, so every one is a cold async load.");
        ImGui.TextUnformatted("Press this repeatedly instead — same path, already loaded:");

        if (ImGui.Button("REPLAY the same path (warm)") && spike.LastPath.Length > 0)
        {
            spike.Replay(pos, cat);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Re-plays the previous path with no rewrite and no re-redirect.\n\n" +
                "Reliable here means the intermittency was cold-load latency, not our file —\n" +
                "and the production design already calls for pre-warming each clip once.\n\n" +
                "Still intermittent here means the problem really is in the file.");
        }

        if (spike.LastPath.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(spike.LastPath);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Takes a real game SCD and overwrites ONLY audio entry 0's header and\n" +
                "payload. Every other byte, and the file length, are identical to a file\n" +
                "the engine demonstrably accepts.\n\n" +
                "Plays  -> our audio entry is correct; the container rebuild is the bug.\n" +
                "Silent -> our audio entry (or PCM support itself) is the bug.\n\n" +
                "Short by necessity: it has to fit audio entry 0's existing slot.");
        }

        ImGui.Separator();
        ImGui.TextDisabled("Earlier controls:");

        if (ImGui.Button("A: verbatim template -> synthetic path"))
        {
            spike.Run(SpikeMode.VerbatimTemplate, pos, cat);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Serves a byte-for-byte copy of a real game SCD from a synthetic path.\n" +
                "The file is definitionally valid.\n\n" +
                "Silence here means Penumbra-invented paths do not work with PlaySound —\n" +
                "our writer is not the problem.");
        }

        ImGui.TextUnformatted("B is now THE test — real indexed path, loud alarm payload:");

        if (ImGui.Button("B: our alarm -> real indexed path"))
        {
            spike.Run(SpikeMode.AuthoredOnRealPath, pos, cat);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Serves our authored file from a real vo_battle path that exists in the\n" +
                "index but has not been loaded this session.\n\n" +
                "The plumbing is definitionally sound, so silence here indicts the writer.");
        }

        ImGui.Separator();
        ImGui.TextDisabled("Both unknowns at once (what we ran before):");

        if (ImGui.Button("C: our PCM -> synthetic"))
        {
            spike.Run(SpikeMode.AuthoredPcm, pos, cat);
        }

        ImGui.SameLine();
        if (ImGui.Button("D: MS-ADPCM tag -> synthetic"))
        {
            spike.Run(SpikeMode.AuthoredAdpcmTag, pos, cat);
        }

        if (spike.Tracking)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("watching...");
        }

        if (!penumbra.PenumbraAvailable)
        {
            ImGui.EndDisabled();
        }

        ImGui.Separator();

        // Shown separately from the report so it survives log rotation and rapid presses.
        if (spike.LastVerdict.Length > 0)
        {
            ImGui.TextUnformatted($"Last verdict: {spike.LastVerdict}");
            ImGui.Separator();
        }

        if (spike.Report.Count == 0)
        {
            ImGui.TextDisabled("No attempt yet.");
            ImGui.TextDisabled("This is the first step that can crash the client — use a striking dummy, not a duty.");
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
