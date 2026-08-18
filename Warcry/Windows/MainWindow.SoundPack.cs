using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Warcry.Audio;
using Warcry.Native;

namespace Warcry.Windows;

/// <summary>
/// The Sound pack tab: the compile/register/warm state of every mapped clip, the Apply
/// button, and the PLAN §6 verification checklist.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Session-local checklist verdicts. Written to docs/native-spike.md by hand.</summary>
    private readonly Dictionary<string, bool?> checkResults = [];

    private float positionalOffsetYalms = 15f;
    private string lastFireResult = string.Empty;

    private int stressRemaining;
    private int stressAccepted;
    private int stressRefused;
    private long stressNextAt;
    private int stressPoolBefore = -1;

    private void DrawSoundPack()
    {
        var packs = this.plugin.Packs;
        var forge = this.plugin.Forge;
        var cfg = this.plugin.Config;

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

        // ---- verification checklist ---------------------------------------------------
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Verification — PLAN §6 (b)–(e), plus the speed argument");
        ImGui.TextWrapped(
            "The native path is proven to play; these are the behaviours that have never been " +
            "measured. Every button below plays the test tone STRAIGHT THROUGH THE NATIVE SINK, " +
            "whatever the sink mode — the first press may refuse while it encodes and warms; " +
            "press again. Record each verdict here and copy the results into docs/native-spike.md.");

        if (this.lastFireResult.Length > 0)
        {
            ImGui.TextDisabled($"  Last fire: {this.lastFireResult}");
        }

        ImGui.Spacing();

        // (b) master slider
        this.DrawCheckRow(
            "b-master",
            "(b) Master volume slider",
            "Fire, drop the game's Master slider to 0, fire again — the second must be silent.",
            () => this.FireNative(1f, Vector3.Zero));

        // (c) voice/se slider
        this.DrawCheckRow(
            "c-voice",
            "(c) Voice / Sound Effects slider",
            "Fire, drop Voice (and then Sound Effects) to 0, fire again. Note WHICH of the two " +
            "affects it — that answers the bus-routing question from the spike.",
            () => this.FireNative(1f, Vector3.Zero));

        // (d) positional attenuation
        ImGui.SetNextItemWidth(160f);
        ImGui.SliderFloat("offset (yalms)##posoffset", ref this.positionalOffsetYalms, 0f, 30f, "%.0f");
        ImGui.SameLine();
        this.DrawCheckRow(
            "d-positional",
            "(d) Positional attenuation",
            "Fire at your own position (0), then at 10, 25 — volume must fall with distance and " +
            "the sound should image left/right as you turn the camera.",
            () => this.FireNative(1f, new Vector3(this.positionalOffsetYalms, 0f, 0f)));

        // (e) sustained load
        this.DrawStressRow();

        // (f) speed argument — gates NativePitchViaSpeed
        ImGui.Spacing();
        ImGui.TextUnformatted("(f) Speed argument (pitch)");
        ImGui.SameLine();
        if (ImGui.SmallButton("×0.5##spd05"))
        {
            this.FireNative(0.5f, Vector3.Zero);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("×1##spd10"))
        {
            this.FireNative(1f, Vector3.Zero);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("×2##spd20"))
        {
            this.FireNative(2f, Vector3.Zero);
        }

        ImGui.SameLine();
        this.DrawVerdictButtons("f-speed");
        ImGui.TextDisabled(
            "  ×0.5 must sound an octave down and twice as long; ×2 an octave up and half as long.\n" +
            "  If all three sound identical the engine ignores speed on our containers — turn OFF\n" +
            "  \"pitch via speed\" below, or pitched mappings will play unpitched in native mode.");

        var viaSpeed = cfg.NativePitchViaSpeed;
        if (ImGui.Checkbox("Pitch via the engine's speed argument", ref viaSpeed))
        {
            cfg.NativePitchViaSpeed = viaSpeed;
            cfg.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "On: one encoded variant per clip; every pitch roll rides on the PlaySound call.\n" +
                "Off: pitch is baked into the encode, snapped to half-semitone steps — more\n" +
                "variants, but works even if the engine ignores speed. Changing this re-applies\n" +
                "the pack.");
        }
    }

    private void DrawCheckRow(string id, string title, string instructions, System.Action fire)
    {
        ImGui.TextUnformatted(title);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Fire##{id}"))
        {
            fire();
        }

        ImGui.SameLine();
        this.DrawVerdictButtons(id);
        ImGui.TextDisabled($"  {instructions}");
        ImGui.Spacing();
    }

    private void DrawVerdictButtons(string id)
    {
        this.checkResults.TryGetValue(id, out var verdict);

        if (ImGui.SmallButton($"{(verdict == true ? "[PASS]" : "pass")}##{id}-pass"))
        {
            this.checkResults[id] = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"{(verdict == false ? "[FAIL]" : "fail")}##{id}-fail"))
        {
            this.checkResults[id] = false;
        }
    }

    private void DrawStressRow()
    {
        ImGui.TextUnformatted("(e) 20 plays in 10 seconds");
        ImGui.SameLine();

        if (this.stressRemaining > 0)
        {
            ImGui.TextDisabled($"running… {this.stressRemaining} left, {this.stressAccepted} accepted, {this.stressRefused} refused");
            this.TickStress();
        }
        else if (ImGui.SmallButton("Run##stress"))
        {
            this.stressRemaining = 20;
            this.stressAccepted = 0;
            this.stressRefused = 0;
            this.stressNextAt = 0;
            this.stressPoolBefore = SoundDiagnostics.CountActive();
        }

        if (this.stressRemaining == 0 && this.stressPoolBefore >= 0)
        {
            ImGui.SameLine();
            this.DrawVerdictButtons("e-stress");
            ImGui.TextDisabled(
                $"  {this.stressAccepted} accepted, {this.stressRefused} refused. Active game sounds " +
                $"before {this.stressPoolBefore}, now {SoundDiagnostics.CountActive()} — the pool must " +
                "settle back down, and the game's own audio must keep working. Keep this tab open " +
                "while it runs.");
        }
        else if (this.stressRemaining == 0)
        {
            ImGui.SameLine();
            this.DrawVerdictButtons("e-stress");
            ImGui.TextDisabled(
                "  Fires the tone through the native sink every 0.5s, 20 times. Watch the active-sound " +
                "count and listen for the game's own audio breaking. Keep this tab open while it runs.");
        }

        ImGui.Spacing();
    }

    private void TickStress()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < this.stressNextAt)
        {
            return;
        }

        // 0.5s apart => 20 fires in 10s, per the criterion.
        this.stressNextAt = now + (long)(0.5 * Stopwatch.Frequency);
        this.stressRemaining--;

        if (this.FireNative(1f, Vector3.Zero))
        {
            this.stressAccepted++;
        }
        else
        {
            this.stressRefused++;
        }
    }

    /// <summary>
    /// Fires the test tone straight through the native sink, ignoring the sink mode.
    /// </summary>
    /// <remarks>
    /// Deliberately not the gameplay path: the checklist measures the engine, so the
    /// composite's routing, the throttle and the scheduler must not sit in front of it.
    /// The first press on a cold session encodes and warms instead of playing — the
    /// refusal string says so — and the second press plays.
    /// </remarks>
    private bool FireNative(float speed, Vector3 offset)
    {
        var native = this.plugin.Composite.Native;
        var position = this.plugin.CachedPlayerPosition + offset;

        var request = new VoiceRequest(
            createSource: _ => new TestTone(),
            variantKey: Plugin.TestToneKey,
            speed: speed,
            position: position,
            soundCategory: 0,
            gain: 1f,
            casterEntityId: 0);

        try
        {
            var played = native.TryPlay(in request);
            this.lastFireResult = played
                ? $"played natively (speed ×{speed:0.##}, offset {offset.X:0} yalms)"
                : $"refused: {native.LastRefusal}";
            return played;
        }
        catch (Exception ex)
        {
            this.lastFireResult = $"threw: {ex.Message}";
            Plugin.Log.Error(ex, "Sound pack checklist: native fire failed");
            return false;
        }
    }
}
