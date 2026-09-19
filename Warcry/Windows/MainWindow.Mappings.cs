using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Warcry.Clips;
using Warcry.Profiles;
using GameAction = Lumina.Excel.Sheets.Action;

namespace Warcry.Windows;

// The Mappings tab: the editor that assigns clips to actions.
public sealed partial class MainWindow
{
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
    private bool onlyJobActions;
    private bool matchesTruncated;

    private void DrawMappings()
    {
        var lib = this.plugin.Clips;
        var store = this.plugin.Profiles;

        if (lib.Count == 0)
        {
            ImGui.TextDisabled("Import a sound file on the Clips tab first, then come back here.");
            return;
        }

        this.DrawMappingSetBar(store);

        ImGui.Separator();

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

                if (ImGui.Selectable($"{abbr}  {job.Name.ExtractText()}"))
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

            Tip(
                "The Action sheet contains duplicates, old versions and NPC copies,\n" +
                "so several rows can share a name and only one ever fires.\n" +
                "This lists only ids actually observed from your character.\n\n" +
                "Press the skill once and it appears here.");

            // Observed ids normally bypass the job filter (they are proven, whatever job
            // they belong to), so a job's list still shows everything you have ever used.
            // This makes the job filter strict.
            if (this.jobFilter != 0)
            {
                ImGui.SameLine();
                var strictJob = this.onlyJobActions;
                if (ImGui.Checkbox($"Only {this.JobLabel(this.jobFilter)} actions", ref strictJob))
                {
                    this.onlyJobActions = strictJob;
                    this.RebuildActionMatches();
                }

                Tip(
                    "Actions you have used on other jobs normally stay in this list.\n" +
                    $"Tick to show only what belongs to {this.JobLabel(this.jobFilter)}'s kit.");
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

                // Guarded, not Tip(): ActionDebugInfo reads the sheet, and this runs for
                // every row in the list.
                if (ImGui.IsItemHovered())
                {
                    var prefix = seen ? "You have used this one, so it is the id that actually fires.\n\n" : string.Empty;
                    ImGui.SetTooltip(prefix + this.ActionDebugInfo(id));
                }
            }

            if (this.matchesTruncated)
            {
                ImGui.Separator();
                ImGui.TextDisabled("List truncated. Narrow the search.");
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
            var profile = this.ActiveProfile(store);
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

        Tip("Fills in whatever you last pressed, which is faster than searching.");

        ImGui.Separator();

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

        // Same wording as the Settings tab: two labels for one setting reads as two
        // settings.
        var fallback = this.plugin.Config.FallBackToTestTone;
        if (ImGui.Checkbox("Beep when an action has no clip", ref fallback))
        {
            this.plugin.Config.FallBackToTestTone = fallback;
            this.plugin.Config.Save();
        }

        // Readiness sits here because this is the moment the user has just changed a
        // mapping and wants to know it took effect.
        this.DrawPrepareBar();

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

            Tip("Overwrites the pitch of every mapping listed below.\nPer-mapping values are replaced and there is no undo.");

            ImGui.SetNextItemWidth(190);
            ImGui.SliderFloat("##bulkrandom", ref this.bulkRandom, 0f, 12f, $"set random +/- {this.bulkRandom:0.0} st");
            ImGui.SameLine();
            if (ImGui.Button($"Apply to {shown} ({scope})##applyrandom"))
            {
                this.ApplyToShownRules(store, r => r.PitchRandomSemitones = this.bulkRandom);
            }

            Tip("Overwrites the random spread of every mapping listed below.\nA small amount on everything is usually better than none.");
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
                // Marked rather than filtered: seeing every set at once is how you notice
                // two of them covering the same action for overlapping people.
                var active = profile.Id == this.selectedProfileId;
                ImGui.TextDisabled(
                    $"{(active ? "> " : "  ")}{profile.Name}  {profile.Match.Describe(RaceName, TribeName)}");
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
                if (this.DrawRuleActionName(name, actionId))
                {
                    this.selectedActionId = actionId;
                }

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

                // Unassigning a rule's last clip deletes the whole rule from the store, so
                // the widgets after the clip loop must not draw: they belong to an object no
                // longer in the document, and an edit to them would Save() a document
                // without it, discarding the edit.
                var ruleDeleted = false;

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

                    Tip("Click to audition at this pitch. Right-click to unassign.");

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        store.RemoveClipFromRule(profile, rule, clipRef.Hash);
                        ruleDeleted = rule.Clips.Count == 0;
                        break;
                    }
                }

                if (ruleDeleted)
                {
                    ImGui.PopID();
                    continue;
                }

                if (rule.Clips.Count > 1)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"({rule.Clips.Count} variants, no immediate repeat)");
                }

                ImGui.Indent(22f);

                var pitch = rule.PitchSemitones;
                ImGui.SetNextItemWidth(190);
                if (ImGui.SliderFloat("##pitch", ref pitch, -24f, 24f, $"pitch {pitch:+0.0;-0.0;0.0} st"))
                {
                    rule.PitchSemitones = pitch;
                }

                // Save on release: these writes are synchronous, and dragging a slider would
                // stutter the game.
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

                Tip(
                    "Varispeed: pitch and length move together, like tape speed.\n" +
                    "Cheap, clean, and usually more natural for a voice.\n\n" +
                    "Keep duration: phase vocoder. Costs CPU and adds some\n" +
                    "phasiness and transient smearing that filtering cannot remove.");

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

                    Tip(
                        "Larger windows smooth sustained vowels but smear the attack.\n" +
                        "For a short shout, Crisp usually beats Smooth.");
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

    // A rule's action name, drawn as a link back into the picker at the top of the tab, so
    // an existing mapping can be re-selected without hunting for it. True on the frame it
    // is clicked.
    private bool DrawRuleActionName(string name, uint actionId)
    {
        // Rules that match by name or category carry no id, so there is nothing to load
        // into the picker — those stay plain text.
        if (actionId == 0)
        {
            ImGui.TextUnformatted(name);
            return false;
        }

        ImGui.TextUnformatted($"{name}  #{actionId}");

        if (!ImGui.IsItemHovered())
        {
            return false;
        }

        // Text does not look clickable, so underline it under the cursor. Hover state is
        // valid for the item just submitted, so the row drawn this frame is the one
        // underlined.
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(min.X, max.Y),
            new Vector2(max.X, max.Y),
            ImGui.GetColorU32(ImGuiCol.Text));

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.SetTooltip("Click to load this action into the picker at the top.");

        return ImGui.IsItemClicked();
    }

    // The single definition of "shown", used for the count, the bulk-apply scope and the
    // list itself, so the button can never affect something you cannot see.
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

    // Localized — display only.
    private string JobLabel(uint jobId) => this.plugin.Jobs.JobLabel(jobId);

    // Through JobIndex, so the editor's list and the pack builder's warm set can never
    // disagree.
    private bool ActionBelongsToJob(in GameAction row, uint jobId)
        => this.plugin.Jobs.ActionBelongsToJob(in row, jobId);

    // Why a given action did or did not land in the filtered list.
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

        // Memoised inside JobIndex, keyed (job, action). Sheet facts never change, so the
        // cache needs no invalidation when the filter moves.
        return this.plugin.Jobs.ActionBelongsToJob(actionId, this.jobFilter);
    }

    private void RebuildActionMatches()
    {
        this.actionMatches.Clear();
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

            // The strict filter is the exception to the observed-id bypass below: with it
            // on, even a proven id from another job's kit stays out of this job's list.
            if (this.onlyJobActions && this.jobFilter != 0 &&
                !this.ActionBelongsToJob(in row, this.jobFilter))
            {
                continue;
            }

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
                    // The job constraint is itself the noise filter, so IsPlayerAction is not
                    // required: that flag is false for conditional actions like Enchanted
                    // Riposte, which are exactly what must not be hidden.
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
}
