using System;
using System.Text;
using Dalamud.Bindings.ImGui;
using Warcry.Audio;
using Warcry.Game;
using CSChar = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Warcry.Windows;

// The Status tab, and the "Copy diagnostics" report it produces. Two audiences in order: a
// player asking "is it working?", then whoever is reading a bug report. The first answer is
// one line at the top; everything that needs vocabulary to interpret — hook addresses, the
// encoder, scheduler counters, sound-slot accounting, raw character bytes — is collapsed
// under Details.
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

        this.DrawAudienceSummary();
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
            this.DrawGruntDetail();
            ImGui.Spacing();
            this.DrawEngineDetail();
            ImGui.Spacing();
            this.DrawClipPreparation();
            ImGui.Spacing();
            this.DrawLocalPlayer();
        }

        ImGui.Spacing();
        if (ImGui.Button("Copy diagnostics"))
        {
            ImGui.SetClipboardText(this.BuildReport());
        }

        Tip("Copies everything under Details as text, for pasting into a bug report.");
    }

    // The whole tab in one line.
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

    // Where sound is going and how loud, without naming a single class.
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

        ImGui.TextUnformatted($"  Playing now   {this.plugin.Composite.ActiveVoices} of {cfg.MaxConcurrent} allowed");

        // This number tracks the in-game sliders live. If it stays 1.00 while Master moves,
        // the game config is not wired up.
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

        Tip(
            "Plays the built-in tone through the current output, at the current volume.\n" +
            "Hearing this but not your voicelines points at mappings, not at audio.");
    }

    // Sits above the drop counters because it explains most of them: "1 240 lines skipped
    // for not being from someone you listen to" is alarming alone and unremarkable next to
    // "listening to: me", in a city.
    private void DrawAudienceSummary()
    {
        var cfg = this.plugin.Config;
        var crowd = this.plugin.Crowd;

        ImGui.TextUnformatted("People");
        ImGui.TextUnformatted($"  Listening to  {Audience.Describe(cfg.Audience)}");

        if (cfg.NamedPeople.Count > 0 || cfg.BlockedPeople.Count > 0)
        {
            ImGui.TextDisabled(
                $"                {cfg.NamedPeople.Count} named, {cfg.BlockedPeople.Count} blocked");
        }

        if (!this.plugin.Audience.HearsAnyoneElse)
        {
            ImGui.TextDisabled("                nobody else, so no crowd scan and no rate cap run at all");
            return;
        }

        ImGui.TextUnformatted($"  Nearby        {crowd.NearbyCount} audible player(s)"
                              + (cfg.MaxDistanceYalms > 0 ? $" within {cfg.MaxDistanceYalms} yalms" : string.Empty));

        var scale = crowd.CooldownScale;
        ImGui.TextUnformatted(scale > 1.001f
            ? $"  Crowd         x{scale:0.0} on other people's gaps " +
              $"({this.plugin.Throttle.CooldownFor(AudienceBucket.Other):0.0}s for strangers)"
            : "  Crowd         not stretching anyone's gaps");

        if (cfg.LimitTotalRate)
        {
            ImGui.TextUnformatted(
                $"  Rate cap      {this.plugin.Throttle.TokensAvailable} of {cfg.RateBurst} line(s) available");
        }
    }

    // The drop counters, in plain language, and only the ones that have happened: a raw
    // DropStage name needs the source to interpret.
    private void DrawWhyNotPlayed()
    {
        var diag = this.plugin.Diag;

        ImGui.TextUnformatted("Lines that did not play (this session)");

        var any = false;
        foreach (var stage in new[]
                 {
                     DropStage.PlaybackOff, DropStage.Gate, DropStage.Throttle,
                     DropStage.RateLimited, DropStage.NoClip, DropStage.SinkRefused,
                     DropStage.TooFarOut, DropStage.Audience, DropStage.NotPc,
                     DropStage.NotAction,
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

    // A player-facing sentence per drop stage.
    private static string DropReason(DropStage stage) => stage switch
    {
        DropStage.PlaybackOff => "playback is switched off in settings",
        DropStage.Gate => "you were somewhere Warcry stays quiet (cutscene, PvP, muted zone…)",
        DropStage.Throttle => "too soon after the previous line, or the action is muted",
        DropStage.NoClip => "nothing is mapped to that action",
        DropStage.SinkRefused => "the sound could not be played, and the reason is on the Events tab",
        DropStage.TooFarOut => "the cast bar was longer than a line will be held for",
        DropStage.RateLimited => "other people's lines were arriving faster than the cap on the People tab allows",
        DropStage.Audience => "you are not listening to whoever cast it — see the People tab",
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

    // What the plugin has silenced, and on what grounds. A suppression the user cannot see
    // is indistinguishable from Warcry having broken their game audio, so the last match is
    // shown whole: path, group, and how far the emitter was from the caster it was
    // attributed to.
    private void DrawGruntDetail()
    {
        var g = this.plugin.Grunts;
        var cfg = this.plugin.Config;

        ImGui.TextUnformatted("Game grunts");

        if (!g.Installed)
        {
            ImGui.TextUnformatted("  Hook          NOT INSTALLED - signature did not resolve.");
            ImGui.TextDisabled("                Expected after a game patch. Nothing else is affected.");
            return;
        }

        ImGui.TextUnformatted($"  Hook          {(g.Active ? "live" : "idle")} at 0x{g.HookAddress:X}");

        if (g.Tripped)
        {
            ImGui.TextUnformatted("  State         TRIPPED - too many faults, passing everything through.");
        }

        var mode = cfg.Grunts switch
        {
            GruntMode.Always => "every attack grunt",
            GruntMode.WhenVoiced => $"casters Warcry voices, for {cfg.GruntWindowSeconds:0.0}s",
            _ => "nothing - the setting is off",
        };

        ImGui.TextUnformatted($"  Silencing     {mode}");
        ImGui.TextUnformatted($"  Suppressed    {g.Suppressed}   ({g.ArmedCount} caster(s) armed now)");

        if (g.LastPath.Length > 0)
        {
            var how = g.LastMatchDistance < 0f
                ? "blanket"
                : $"{g.LastMatchDistance:0.00}y from the caster";

            // The age makes this checkable in game: press a mapped action and it has to read
            // a fraction of a second, or the grunt you just heard was not ours to silence.
            var age = (DateTime.Now - g.LastAt).TotalSeconds;
            ImGui.TextDisabled(
                $"                last: {g.LastPath}");
            ImGui.TextDisabled(
                $"                      group {g.LastSoundNumber}, {how}, {age:0.0}s ago");
        }
    }

    private void DrawEngineDetail()
    {
        var sched = this.plugin.Scheduler;
        var composite = this.plugin.Composite;
        var native = composite.Native;
        var forge = this.plugin.Forge;
        var cfg = this.plugin.Config;

        ImGui.TextUnformatted("Audio path");
        ImGui.TextUnformatted($"  Sink          {this.plugin.Composite.Status}");
        ImGui.TextUnformatted($"  Routed        {composite.NativePlays} engine / {composite.ManagedPlays} built-in");

        if (composite.NativeRefusal.Length > 0)
        {
            ImGui.TextDisabled($"                last native refusal: {composite.NativeRefusal}");
        }

        ImGui.TextUnformatted(
            $"  Voices        {this.plugin.Composite.ActiveVoices} / {cfg.MaxConcurrent}" +
            $"   ({native.Following} following)");

        // Follow mode holds a slot in the game's 256-entry sound pool — shared with the
        // whole client — until this plugin hands it back. These are the leak instruments:
        // "following" must return to zero after every line, and the other two should stay
        // at zero permanently. See docs/PLAN.md 9.
        if (cfg.VoicePosition == VoicePositionMode.Follow
            && cfg.Sink is SinkMode.NativeOnly or SinkMode.Auto)
        {
            ImGui.TextUnformatted(
                $"  Follow        {native.Moves} position update(s), {native.DriverMoves} reached the driver");
            ImGui.TextUnformatted(
                $"  Pool          {native.Released} released, {native.Forced} forced, {native.Orphaned} orphaned");

            // The two ways following fails sound identical, so name them apart here.
            if (native.Moves == 0 && composite.NativePlays > 0)
            {
                ImGui.TextUnformatted(
                    "                ⚠ no position update has ever been pushed. The caster is not " +
                    "being resolved, so nothing can follow. This is a plugin bug, not an engine limit.");
            }
            else if (native.Moves > 0 && native.DriverMoves == 0)
            {
                // Writing the SoundData record alone is inaudible, so this combination is
                // the difference between working and looking like it works.
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

    private string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Warcry diagnostics");
        sb.AppendLine($"hookInstalled={this.plugin.Watcher.Installed} tripped={this.plugin.Watcher.Tripped} eventsSeen={this.plugin.Diag.TotalSeen}");

        // The pool counters belong in a pasted report: "it went quiet after a while" and
        // "orphaned climbs by one per line" are the same bug.
        var nativeSink = this.plugin.Composite.Native;
        sb.AppendLine(
            $"sink={this.plugin.Config.Sink} voicePosition={this.plugin.Config.VoicePosition} " +
            $"voices={this.plugin.Composite.ActiveVoices} following={nativeSink.Following} " +
            $"moves={nativeSink.Moves} driverMoves={nativeSink.DriverMoves} " +
            $"released={nativeSink.Released} forced={nativeSink.Forced} orphaned={nativeSink.Orphaned}");

        // "The game went quiet" and "a grunt slipped through" are both this line.
        var grunts = this.plugin.Grunts;
        sb.AppendLine(
            $"gruntHook={grunts.Installed} active={grunts.Active} tripped={grunts.Tripped} " +
            $"gruntMode={this.plugin.Config.Grunts} " +
            $"window={this.plugin.Config.GruntWindowSeconds:0.0}s suppressed={grunts.Suppressed} " +
            $"armed={grunts.ArmedCount} lastGroup={grunts.LastSoundNumber} lastDistance={grunts.LastMatchDistance:0.00}");

        // Which tiers are on decides whether a "nothing plays" report is a bug at all, and
        // the crowd numbers decide the same for "it goes quiet in raids".
        var cfg = this.plugin.Config;
        sb.AppendLine(
            $"audience={cfg.Audience} named={cfg.NamedPeople.Count} blocked={cfg.BlockedPeople.Count} " +
            $"maxDistance={cfg.MaxDistanceYalms} nearby={this.plugin.Crowd.NearbyCount} " +
            $"crowdScale={this.plugin.Crowd.CooldownScale:0.00} " +
            $"tokens={this.plugin.Throttle.TokensAvailable}/{cfg.RateBurst} " +
            $"activeJobs={this.plugin.Packs.ActiveJobs.Count}");

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
                $"who={row.Audience}{(row.AudienceRefusal.Length > 0 ? $"(refused:{row.AudienceRefusal})" : string.Empty)} " +
                $"dist={ev.Facts.Distance} " +
                $"actionId={ev.ActionId}(\"{this.ActionName(ev.ActionId)}\") " +
                $"cat={this.CategoryOf(ev.ActionId)} cast={this.CastSecondsOf(ev.ActionId):0.0}s " +
                $"wasCasting={ev.WasCasting} castLeft={ev.CastRemaining:0.000} " +
                $"result={row.Drop}");
            shown++;
        }

        return sb.ToString();
    }
}
