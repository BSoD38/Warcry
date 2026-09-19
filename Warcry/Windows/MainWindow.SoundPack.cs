using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Warcry.Audio;
using Warcry.Native;

namespace Warcry.Windows;

// Clip preparation: the readiness bar on the Actions tab, and the per-clip breakdown under
// Details on the Status tab. Preparing also runs on its own a couple of seconds after any
// edit, so the button is a reassurance rather than a step.
public sealed partial class MainWindow
{
    private void DrawPrepareBar()
    {
        // Only the game engine needs prepared clips. The built-in player reads the audio
        // files directly, so saying anything here would be noise.
        if (this.plugin.Config.Sink is not (SinkMode.NativeOnly or SinkMode.Auto))
        {
            return;
        }

        var packs = this.plugin.Packs;
        var jobId = packs.CurrentJobId;
        var jobLabel = jobId == 0 ? "your job" : this.JobLabel(jobId);

        if (packs.Errors.Count > 0)
        {
            ImGui.TextUnformatted($"⚠ {packs.Errors.Count} clip(s) could not be prepared, so they will not play.");
            foreach (var error in packs.Errors)
            {
                ImGui.TextWrapped($"   {error}");
            }
        }
        else if (packs.PlannedCount == 0)
        {
            ImGui.TextDisabled("Nothing mapped yet, so there is nothing to prepare.");
        }
        else if (packs.CompiledCount < packs.PlannedCount)
        {
            ImGui.TextUnformatted(
                $"Preparing clips for the game engine: {packs.CompiledCount} of {packs.PlannedCount} done.");
        }
        else if (packs.WarmForActiveJobs < packs.ReachableForActiveJobs)
        {
            ImGui.TextDisabled(
                $"All {packs.PlannedCount} clip(s) prepared. Loading the {packs.ReachableForActiveJobs} " +
                $"that {this.ReachableFrom(jobLabel)} can trigger.");
        }
        else
        {
            ImGui.TextDisabled(
                $"All {packs.PlannedCount} clip(s) ready. {packs.ReachableForActiveJobs} of them can be " +
                $"triggered by {this.ReachableFrom(jobLabel)}.");
        }

        if (ImGui.SmallButton("Prepare clips now"))
        {
            packs.Apply();
        }

        Tip(
            "Converts every mapped clip into a form the game's sound engine can play,\n" +
            "then loads the ones your current job can trigger.\n\n" +
            "This already runs by itself a couple of seconds after you change anything,\n" +
            "so you only need this button if something looks stuck.");
    }

    private void DrawClipPreparation()
    {
        var packs = this.plugin.Packs;
        var forge = this.plugin.Forge;

        ImGui.TextUnformatted("Clip preparation");
        ImGui.TextUnformatted($"  Forge         {forge.Status}");

        var jobId = packs.CurrentJobId;
        var jobLabel = jobId == 0 ? "(none, not in game?)" : this.JobLabel(jobId);
        ImGui.TextUnformatted($"  Current job   {jobLabel}");

        var (totalBytes, warmedBytes) = forge.ByteTotals();
        ImGui.TextUnformatted(
            $"  Variants      {packs.PlannedCount} planned, {packs.CompiledCount} compiled, " +
            $"{packs.ReachableForActiveJobs} reachable from {this.ReachableFrom(jobLabel)}, " +
            $"{packs.WarmForActiveJobs} warm");
        ImGui.TextUnformatted(
            $"  Memory        {totalBytes / 1024.0:0} KB on disk, ~{warmedBytes / 1024.0:0} KB resident");
        ImGui.TextDisabled("                loaded containers cannot be freed until the game exits");
        ImGui.TextDisabled(packs.LastApplyAt == 0
            ? "                never prepared this session"
            : $"                {packs.Status}");

        if (packs.Errors.Count > 0)
        {
            ImGui.TextUnformatted($"  Blocked       {packs.Errors.Count}");
            foreach (var error in packs.Errors)
            {
                ImGui.TextWrapped($"                {error}");
            }
        }

        if (!ImGui.TreeNode($"Every clip ({packs.PlannedCount})"))
        {
            return;
        }

        var rows = new List<PackBuilder.PlannedVariant>(packs.Plan.Values);
        rows.Sort(static (a, b) => string.CompareOrdinal(a.Label, b.Label));

        foreach (var entry in rows)
        {
            string state;
            if (entry.Error.Length > 0)
            {
                state = $"blocked: {entry.Error}";
            }
            else if (forge.TryGetForged(entry.VariantKey, out var clip) && clip is not null)
            {
                state = clip.WarmedAt != 0 ? "ready, loaded" : "ready, loads on job switch";
            }
            else if (forge.IsInFlight(entry.VariantKey))
            {
                state = "preparing...";
            }
            else if (forge.TryGetFailure(entry.VariantKey, out var why))
            {
                // The encode gave up after Apply returned, so the plan entry knows nothing
                // about it until the next Apply. Ask the forge directly.
                state = $"blocked: {why}";
            }
            else
            {
                state = "not prepared yet";
            }

            ImGui.TextUnformatted($"  {entry.Label}");
            ImGui.SameLine(300f);
            ImGui.TextDisabled(state);
        }

        ImGui.TreePop();
    }

    // Who the reachable count covers: your job, plus the jobs of anyone audible around you.
    private string ReachableFrom(string jobLabel)
    {
        var others = this.plugin.Packs.ActiveJobs.Count - 1;
        return others > 0 ? $"{jobLabel} and {others} nearby job(s)" : jobLabel;
    }
}
