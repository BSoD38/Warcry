using System.Text;
using Dalamud.Bindings.ImGui;
using Warcry.Audio;
using CSChar = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Warcry.Windows;

/// <summary>The Status tab, and the "Copy diagnostics" report it produces.</summary>
public sealed partial class MainWindow
{
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

        var composite = this.plugin.Composite;
        var forge = this.plugin.Forge;

        ImGui.TextUnformatted("Audio");
        ImGui.TextUnformatted($"  Sink          {sink.Status}");
        ImGui.TextUnformatted($"  Voices        {sink.ActiveVoices} / {this.plugin.Config.MaxConcurrent}");
        ImGui.TextUnformatted($"  Routed        {composite.NativePlays} engine / {composite.ManagedPlays} NAudio");

        if (this.plugin.Config.Sink is SinkMode.NativeOnly or SinkMode.Auto)
        {
            ImGui.TextUnformatted($"  Forge         {forge.Status}");
            if (forge.TemplatePath.Length > 0)
            {
                ImGui.TextDisabled($"                container cloned from {forge.TemplatePath}");
            }
        }
        ImGui.TextUnformatted($"  Scheduler     {sched.PendingCount} pending, {sched.Dispatched} dispatched, {sched.Refused} refused, {sched.Cancelled} cancelled");

        // The whole point of reading the game's config: this number should track your
        // in-game sliders live. If it stays 1.00 while you move Master, it isn't wired.
        var gameGain = vol.GainFor(0, this.plugin.Config.UseVoiceSliderNotSe);
        var bus = this.plugin.Config.UseVoiceSliderNotSe ? "Voice" : "SoundEffects";
        ImGui.TextUnformatted($"  Game gain     {gameGain:0.000}   (Master {vol.Master} x {bus} {(this.plugin.Config.UseVoiceSliderNotSe ? vol.Voice : vol.Se)} x Player {vol.Player}, /100 each)");

        if (gameGain <= 0f)
        {
            ImGui.TextDisabled("                muted by the game's own settings — nothing will play");
        }

        // Diagnostics only. The settings that used to live here — plugin volume, the two
        // playback toggles — moved to the Settings tab, both because that is where anyone
        // would look for them and because they saved the whole config on every frame of a
        // slider drag.
        if (ImGui.Button("Play test tone"))
        {
            this.plugin.PlayTestTone();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"volume {this.plugin.Config.MasterGain:0.00} — change it on the Settings tab");

        if (!this.plugin.Config.Enabled)
        {
            ImGui.TextUnformatted("  Warcry is DISABLED in settings — nothing will play.");
        }
        else if (!this.plugin.Config.PlayTestToneOnActions)
        {
            ImGui.TextUnformatted("  Playback on actions is off — detection runs, nothing sounds.");
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

    private string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Warcry diagnostics");
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
                $"cat={this.CategoryOf(ev.ActionId)} cast={this.CastSecondsOf(ev.ActionId):0.0}s " +
                $"wasCasting={ev.WasCasting} castLeft={ev.CastRemaining:0.000} " +
                $"var={ev.AnimationVariation} " +
                $"gseq={ev.GlobalSequence} sseq={ev.SourceSequence} targets={ev.NumTargets}");
            shown++;
        }

        return sb.ToString();
    }
}
