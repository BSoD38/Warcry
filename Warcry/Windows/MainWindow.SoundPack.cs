using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Warcry.Audio;
using Warcry.Native;

namespace Warcry.Windows;

/// <summary>
/// Clip preparation: the compact readiness bar on the Actions tab, and the full
/// per-clip breakdown under Details on the Status tab.
/// </summary>
/// <remarks>
/// <para>This used to be a tab of its own called "Sound pack", which asked the user to
/// know that clips have to be encoded and registered before the game engine can play
/// them. That is true and it is entirely our problem, so the two things a user can act on
/// (is it ready, and press the button) now sit where they were already working, and the
/// per-clip detail moved to Status with the other diagnostics.</para>
/// <para>Preparing also happens on its own, a couple of seconds after any edit, so the
/// button is a reassurance rather than a step.</para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>
    /// One line and one button, for the tab where the user just changed a mapping.
    /// </summary>
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

    /// <summary>
    /// The full breakdown, for the Details section of the Status tab.
    /// </summary>
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
                // The encode gave up after Apply returned, so the plan entry itself
                // knows nothing about it until the next Apply. Ask the forge directly.
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

    /// <summary>
    /// Who the reachable count is counted for. While you are the only audience that is just
    /// your job, which is what this used to say unconditionally; once other people can be
    /// heard, their jobs are reachable too and saying "your job" would understate it.
    /// </summary>
    private string ReachableFrom(string jobLabel)
    {
        var others = this.plugin.Packs.ActiveJobs.Count - 1;
        return others > 0 ? $"{jobLabel} and {others} nearby job(s)" : jobLabel;
    }
}
