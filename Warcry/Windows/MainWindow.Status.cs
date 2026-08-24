using System.Text;
using Dalamud.Bindings.ImGui;
using Warcry.Audio;
using CSChar = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Warcry.Windows;

/// <summary>The Status tab, and the "Copy diagnostics" report it produces.</summary>
/// <remarks>
/// <para>Two audiences, one tab, in that order: a player asking "is it working?", then
/// whoever is reading a bug report. The first answer is one line at the top; everything
/// that needs vocabulary to interpret — hook addresses, the encoder, scheduler counters,
/// the sound-slot accounting, raw character bytes — is collapsed under Details.</para>
/// <para>It opened flat before, which meant the answer to "is it working" was somewhere in
/// four screens of counters, and the counters most likely to alarm someone were the ones
/// that are normal.</para>
/// </remarks>
public sealed partial class MainWindow
{
    private void DrawStatus()
    {
        this.DrawVerdict();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawSoundSummary();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawWhyNotPlayed();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Details (for bug reports)"))
        {
            this.DrawDetectionDetail();
            ImGui.Spacing();
            this.DrawEngineDetail();
            ImGui.Spacing();
            this.DrawClipPreparation();
            ImGui.Spacing();
            this.DrawLocalPlayer();
            ImGui.Spacing();
            DrawGameSoundConfig();
        }

        ImGui.Spacing();
        if (ImGui.Button("Copy diagnostics"))
        {
            ImGui.SetClipboardText(this.BuildReport());
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Copies everything under Details as text, for pasting into a bug report.");
        }
    }

    /// <summary>The whole tab in one line, for someone who wants only that.</summary>
    private void DrawVerdict()
    {
        var silence = this.plugin.ExplainSilence();

        if (silence.Length > 0)
        {
            ImGui.TextUnformatted("⚠ Nothing will play right now");
            ImGui.TextWrapped($"   {silence}");
            ImGui.TextDisabled("   Most of these are settings. See the Settings tab.");
            return;
        }

        var played = this.plugin.Composite.NativePlays + this.plugin.Composite.ManagedPlays;
        ImGui.TextUnformatted("✓ Warcry is working");
        ImGui.TextDisabled(played == 0
            ? "   Nothing has played yet this session. Use a mapped action to try it."
            : $"   {played} line(s) played this session.");
    }

    /// <summary>Where sound is going and how loud, without naming a single class.</summary>
    private void DrawSoundSummary()
    {
        var cfg = this.plugin.Config;
        var composite = this.plugin.Composite;
        var vol = this.plugin.Volume;

        ImGui.TextUnformatted("Sound");

        ImGui.TextUnformatted($"  Output        {OutputLabel(cfg.Sink)}");
        if (cfg.Sink is SinkMode.Auto)
        {
            ImGui.TextDisabled($"                {composite.NativePlays} through the game engine, " +
                               $"{composite.ManagedPlays} through the backup player");
        }

        ImGui.TextUnformatted($"  Playing now   {this.plugin.Sink.ActiveVoices} of {cfg.MaxConcurrent} allowed");

        // The whole point of reading the game's config: this number should track the
        // in-game sliders live. If it stays 1.00 while Master moves, it isn't wired.
        var gameGain = vol.GainFor(0, cfg.UseVoiceSliderNotSe);
        var slider = cfg.UseVoiceSliderNotSe ? "Voice" : "Sound Effects";
        ImGui.TextUnformatted($"  Loudness      {gameGain * cfg.MasterGain:0.00}");
        ImGui.TextDisabled(
            $"                the game's sliders ({slider}) work out to {gameGain:0.00}, " +
            $"times your Warcry volume of {cfg.MasterGain:0.00}");

        if (gameGain <= 0f)
        {
            ImGui.TextUnformatted("                ⚠ the game's own volume settings mute this entirely");
        }

        if (ImGui.Button("Play a test sound"))
        {
            this.plugin.PlayTestTone();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Plays the built-in tone through the current output, at the current volume.\n" +
                "Hearing this but not your voicelines points at mappings, not at audio.");
        }
    }

    /// <summary>
    /// The drop counters, in plain language, and only the ones that have happened.
    /// </summary>
    /// <remarks>
    /// This lived at the bottom of the Settings tab, printing raw enum names —
    /// <c>SinkRefused</c>, <c>TooFarOut</c>, <c>NotPc</c> — under the heading "Drops by
    /// stage". Every one of those needs the source to interpret, on the one page a player
    /// visits most.
    /// </remarks>
    private void DrawWhyNotPlayed()
    {
        var diag = this.plugin.Diag;

        ImGui.TextUnformatted("Lines that did not play (this session)");

        var any = false;
        foreach (var stage in new[]
                 {
                     DropStage.PlaybackOff, DropStage.Gate, DropStage.Throttle,
                     DropStage.NoClip, DropStage.SinkRefused, DropStage.TooFarOut,
                     DropStage.Audience, DropStage.NotPc, DropStage.NotAction,
                 })
        {
            var n = diag.DropCount(stage);
            if (n == 0)
            {
                continue;
            }

            any = true;
            ImGui.TextUnformatted($"  {n,6}  {DropReason(stage)}");
        }

        if (!any)
        {
            ImGui.TextDisabled("  Nothing has been skipped.");
        }
    }

    /// <summary>
    /// A player-facing sentence per drop stage. The enum name is the developer's answer;
    /// this is the user's.
    /// </summary>
    private static string DropReason(DropStage stage) => stage switch
    {
        DropStage.PlaybackOff => "playback is switched off in settings",
        DropStage.Gate => "you were somewhere Warcry stays quiet (cutscene, PvP, muted zone…)",
        DropStage.Throttle => "too soon after the previous line, or the action is muted",
        DropStage.NoClip => "nothing is mapped to that action",
        DropStage.SinkRefused => "the sound could not be played, and the reason is on the Events tab",
        DropStage.TooFarOut => "the cast bar was longer than a line will be held for",
        DropStage.Audience => "somebody else cast it, and Warcry only voices you",
        DropStage.NotPc => "not a player character",
        DropStage.NotAction => "not an action (an item, a mount, a status tick…)",
        _ => stage.ToString(),
    };

    private static string OutputLabel(SinkMode mode) => mode switch
    {
        SinkMode.NativeOnly => "the game's sound engine only",
        SinkMode.Auto => "the game's sound engine, with the built-in player as backup",
        SinkMode.ManagedOnly => "Warcry's built-in player",
        _ => "nothing (output is switched off)",
    };

    // ------------------------------------------------------------------ details

    private void DrawDetectionDetail()
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
            ImGui.TextDisabled("                Expected after a game patch. Wait for a FFXIVClientStructs update.");
        }

        if (w.Tripped)
        {
            ImGui.TextUnformatted("  State         TRIPPED - too many faults, inert until reload.");
        }

        ImGui.TextUnformatted($"  Events seen   {this.plugin.Diag.TotalSeen}");
    }

    private void DrawEngineDetail()
    {
        var sched = this.plugin.Scheduler;
        var composite = this.plugin.Composite;
        var native = composite.Native;
        var forge = this.plugin.Forge;
        var cfg = this.plugin.Config;

        ImGui.TextUnformatted("Audio path");
        ImGui.TextUnformatted($"  Sink          {this.plugin.Sink.Status}");
        ImGui.TextUnformatted($"  Routed        {composite.NativePlays} engine / {composite.ManagedPlays} built-in");

        if (composite.NativeRefusal.Length > 0)
        {
            ImGui.TextDisabled($"                last native refusal: {composite.NativeRefusal}");
        }

        ImGui.TextUnformatted(
            $"  Voices        {this.plugin.Sink.ActiveVoices} / {cfg.MaxConcurrent}" +
            $"   ({native.Following} following)");

        // Follow mode holds a slot in the game's 256-entry sound pool — shared with the
        // whole client — until this plugin hands it back. These are the leak instruments:
        // "following" must return to zero after every line, and the other two should stay
        // at zero permanently. See docs/PLAN.md 9.
        if (cfg.VoicePosition == VoicePositionMode.Follow
            && cfg.Sink is SinkMode.NativeOnly or SinkMode.Auto)
        {
            ImGui.TextUnformatted(
                $"  Follow        {native.Moves} position update(s), {native.DriverMoves} reached the driver, " +
                $"{native.StartsSeen} start(s) seen");
            ImGui.TextUnformatted(
                $"  Pool          {native.Released} released, {native.Forced} forced, {native.Orphaned} orphaned");

            // The two ways following fails look identical from the speakers, so name them
            // apart here rather than leaving it to be guessed at again.
            if (native.Moves == 0 && composite.NativePlays > 0)
            {
                ImGui.TextUnformatted(
                    "                ⚠ no position update has ever been pushed. The caster is not " +
                    "being resolved, so nothing can follow. This is a plugin bug, not an engine limit.");
            }
            else if (native.Moves > 0 && native.DriverMoves == 0)
            {
                // Writing the SoundData record alone is measurably inaudible, so this
                // combination is the difference between working and looking like it works.
                ImGui.TextUnformatted(
                    "                ⚠ updates are being written but none reach the audio driver, " +
                    "so nothing will move. See the log, and use Stays where it started until it is fixed.");
            }

            if (native.Orphaned > 0)
            {
                ImGui.TextUnformatted(
                    "                ⚠ the engine recycled a slot we were holding, so follow mode's " +
                    "ownership assumption does not hold on this build. Switch to Stays where it started.");
            }

            if (native.Forced > 0)
            {
                ImGui.TextDisabled(
                    "                a forced release means a line was cut off at its backstop, " +
                    "so the clip-length estimate is short");
            }
        }

        if (cfg.Sink is SinkMode.NativeOnly or SinkMode.Auto)
        {
            ImGui.TextUnformatted($"  Forge         {forge.Status}");
            if (forge.TemplatePath.Length > 0)
            {
                ImGui.TextDisabled($"                container cloned from {forge.TemplatePath}");
            }
        }

        ImGui.TextUnformatted(
            $"  Scheduler     {sched.PendingCount} pending, {sched.Dispatched} dispatched, " +
            $"{sched.Refused} refused, {sched.Cancelled} cancelled");

        // Only when it has happened: a nonzero count here means a mapping is on a cast
        // longer than the delay ceiling, which is not otherwise guessable.
        if (sched.TooFarOut > 0)
        {
            ImGui.TextDisabled($"                {sched.TooFarOut} refused for a cast bar longer than the delay ceiling");
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
        ImGui.TextUnformatted($"  Zone          {this.ZoneName(Plugin.ClientState.TerritoryType)} " +
                              $"({Plugin.ClientState.TerritoryType})");

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

    private static void DrawGameSoundConfig()
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
    }

    private string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Warcry diagnostics");
        sb.AppendLine($"hookInstalled={this.plugin.Watcher.Installed} tripped={this.plugin.Watcher.Tripped} eventsSeen={this.plugin.Diag.TotalSeen}");

        // The pool counters belong in a pasted report, not only on screen: "it went quiet
        // after a while" and "orphaned climbs by one per line" are the same bug report.
        var nativeSink = this.plugin.Composite.Native;
        sb.AppendLine(
            $"sink={this.plugin.Config.Sink} voicePosition={this.plugin.Config.VoicePosition} " +
            $"voices={this.plugin.Sink.ActiveVoices} following={nativeSink.Following} " +
            $"moves={nativeSink.Moves} driverMoves={nativeSink.DriverMoves} startsSeen={nativeSink.StartsSeen} " +
            $"released={nativeSink.Released} forced={nativeSink.Forced} orphaned={nativeSink.Orphaned}");

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
