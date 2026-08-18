using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Warcry.Native;

namespace Warcry.Windows;

/// <summary>
/// The Sound pack tab: the compile/register/warm state of every mapped clip, and the
/// Apply button.
/// </summary>
public sealed partial class MainWindow
{
    private void DrawSoundPack()
    {
        var packs = this.plugin.Packs;
        var forge = this.plugin.Forge;

        // ---- pipeline state ----------------------------------------------------------
        ImGui.TextUnformatted("Pack");
        ImGui.TextUnformatted($"  Forge         {forge.Status}");

        var jobId = packs.CurrentJobId;
        var jobLabel = jobId == 0 ? "(none — not in game?)" : this.JobLabel(jobId);
        ImGui.TextUnformatted($"  Current job   {jobLabel}");

        var (totalBytes, warmedBytes) = forge.ByteTotals();
        ImGui.TextUnformatted(
            $"  Variants      {packs.PlannedCount} planned, {packs.CompiledCount} compiled, " +
            $"{packs.ReachableForCurrentJob} reachable from {jobLabel}, {packs.WarmForCurrentJob} warm");
        ImGui.TextUnformatted(
            $"  Memory        {totalBytes / 1024.0:0} KB on disk, ~{warmedBytes / 1024.0:0} KB resident (warmed containers cannot be evicted until the game exits)");

        if (ImGui.Button("Apply — compile and register everything"))
        {
            packs.Apply();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Encodes every mapped clip into a game-playable .scd, registers the redirects\n" +
                "with Penumbra, and warms the clips your current job can trigger. Idempotent —\n" +
                "unchanged clips are skipped. Also runs automatically a couple of seconds after\n" +
                "you edit mappings, when a native sink mode is selected.");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(packs.LastApplyAt == 0 ? "never applied this session" : packs.Status);

        // ---- problems ----------------------------------------------------------------
        if (packs.Errors.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted($"Blocked ({packs.Errors.Count})");
            foreach (var error in packs.Errors)
            {
                ImGui.TextWrapped($"  ⚠ {error}");
            }
        }

        // ---- per-variant state -------------------------------------------------------
        ImGui.Spacing();
        if (ImGui.CollapsingHeader($"Variants ({packs.PlannedCount})"))
        {
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
                    state = clip.WarmedAt != 0 ? "ready, warm" : "ready, cold (warms on job switch)";
                }
                else if (forge.IsInFlight(entry.VariantKey))
                {
                    state = "encoding…";
                }
                else
                {
                    state = "not compiled — press Apply";
                }

                ImGui.TextUnformatted($"  {entry.Label}");
                ImGui.SameLine(300f);
                ImGui.TextDisabled(state);
            }
        }
    }
}
