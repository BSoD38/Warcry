using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Warcry.Audio;

namespace Warcry.Windows;

/// <summary>The Settings tab.</summary>
/// <remarks>
/// <para>Grouped by the question a user is actually asking — how loud, when does it play,
/// where does it stay quiet — rather than by which subsystem owns the field. Anything that
/// only reports what happened lives on the Status tab instead; this tab is controls only.
/// The drop-count table used to sit at the bottom here, which made the longest page in the
/// plugin end in something nobody could act on.</para>
/// <para>Labels name what the setting does to the sound. The implementation vocabulary —
/// sink, mixer, forge, variant — stays in tooltips and on Status, because a player deciding
/// how loud their voicelines are should not have to learn any of it.</para>
/// </remarks>
public sealed partial class MainWindow
{
    private void DrawSettings()
    {
        var cfg = this.plugin.Config;
        var dirty = false;

        // The one question this tab exists to answer, answered before anything else on it.
        var silence = this.plugin.ExplainSilence();
        if (silence.Length > 0)
        {
            ImGui.TextUnformatted("⚠ Nothing will play right now");
            ImGui.TextWrapped($"   {silence}");
        }
        else
        {
            ImGui.TextDisabled("✓ Ready. Voicelines will play when you use a mapped action.");
        }

        Section("Playback");

        var enabled = cfg.Enabled;
        if (ImGui.Checkbox("Warcry enabled", ref enabled))
        {
            cfg.Enabled = enabled;
            dirty = true;
        }

        Tip(
            "The master switch. Off means nothing at all: no watching your actions,\n" +
            "no sound, no work done in the background.");

        cfg.PlayTestToneOnActions = Toggle("Play voicelines", cfg.PlayTestToneOnActions, ref dirty,
            "Off keeps watching your actions, so the Events tab keeps filling up, but plays\n" +
            "nothing. Useful when you want to see what would have played without hearing it.\n\n" +
            "'Warcry enabled' above is the harder switch: that one stops watching too.");

        cfg.FallBackToTestTone = Toggle("Beep when an action has no clip", cfg.FallBackToTestTone, ref dirty,
            "Plays a short synthesised tone instead of silence, so you can tell 'the action\n" +
            "was not detected' apart from 'nothing is mapped to it yet'.\n\n" +
            "Handy while setting up, noise once you are done.");

        this.DrawSoundOutputSetting(cfg, ref dirty);
        this.DrawVoicePositionSetting(cfg, ref dirty);

        Section("Volume");

        cfg.MasterGain = Slider("Warcry volume", cfg.MasterGain, 0f, 2f, "%.2f", ref dirty,
            "A trim on top of the game's own volume sliders, which always apply first.\n" +
            "1.00 means 'exactly as loud as the game would play it'.");

        cfg.UseVoiceSliderNotSe = Toggle("Follow the game's Voice slider", cfg.UseVoiceSliderNotSe, ref dirty,
            "On: your voicelines get quieter when you lower the game's Voice volume.\n" +
            "Off: they follow Sound Effects instead.\n\n" +
            "Voice is the better match. It is the slider you reach for when you want\n" +
            "dialogue quieter without deadening combat.");

        Section("When lines play");

        cfg.WaitForCastToFinish = Toggle("Wait for the cast bar to finish", cfg.WaitForCastToFinish, ref dirty,
            "The game commits an action about half a second before its cast bar visually\n" +
            "fills, so without this a spell's line arrives early. Instant actions have no\n" +
            "cast bar and are never delayed.");

        cfg.SkipAutoAttacks = Toggle("Skip auto-attacks", cfg.SkipAutoAttacks, ref dirty,
            "Auto-attacks fire constantly and are never worth a line.");

        cfg.CastsOnly = Toggle("Only actions with a cast bar", cfg.CastsOnly, ref dirty,
            "Drops every instant action, including most weaponskills and all oGCDs.\n" +
            "Very quiet, and mostly useful for casters.");

        cfg.SelfCooldownSeconds = Slider(
            "Minimum gap between your lines", cfg.SelfCooldownSeconds, 0f, 15f,
            cfg.SelfCooldownSeconds <= 0f ? "off" : "%.1f s", ref dirty,
            "The single most important setting for whether this stays fun past the\n" +
            "first hour.\n\n" +
            "A rotation fires roughly every 2.5s, so 2s lets most casts through while\n" +
            "still collapsing bursts of instants into one line.\n\n" +
            "Other people have their own gaps, on the People tab.");

        cfg.MaxConcurrent = SliderInt("Voicelines at once", cfg.MaxConcurrent, 1, 6, "%d", ref dirty,
            "How many lines may overlap. Deliberately capped low: the game has a limited\n" +
            "number of sound slots and they are shared with everything else the client\n" +
            "is playing, so taking too many would start silencing the game itself.\n\n" +
            "When you are listening to other people, one of these is always kept free\n" +
            "for your own lines, so a crowd cannot drown you out.");

        Section("The game's own grunts");

        this.DrawGruntSetting(cfg, ref dirty);

        Section("Where lines stay quiet");

        // Evaluate live. Gates.Reason is only written when a cast is processed, so reading
        // the cached value showed the reason from the last action you used rather than the
        // state you are in now — which reads as a bug when you walk into a cutscene and the
        // line still says "not suppressed".
        this.plugin.Gates.IsSuppressed();

        var suppressed = this.plugin.Gates.Reason;
        if (suppressed.Length > 0)
        {
            ImGui.TextUnformatted($"  Quiet right now: {suppressed}");
        }
        else
        {
            ImGui.TextDisabled("  Nothing is keeping Warcry quiet right now.");
        }

        ImGui.Spacing();

        // NOTE: a property cannot be passed by ref, hence the return-the-value shape.
        cfg.DisableInCutscenes = Toggle("Cutscenes", cfg.DisableInCutscenes, ref dirty,
            "Battle cries over dialogue is the worst thing this plugin can do, so this is\n" +
            "on by default.");
        cfg.DisableInQuestEvents = Toggle("Quest events", cfg.DisableInQuestEvents, ref dirty,
            "Scripted quest interactions.");
        cfg.DisableInPvP = Toggle("PvP", cfg.DisableInPvP, ref dirty,
            "PvP actions are a different kit and the pace is much higher.");
        cfg.DisableInGpose = Toggle("Group pose", cfg.DisableInGpose, ref dirty, null);

        ImGui.TextDisabled("  Loading screens are always quiet and cannot be turned on.");

        ImGui.Spacing();

        // Zone mute, using wherever you are right now.
        var here = Plugin.ClientState.TerritoryType;
        var zone = this.ZoneName(here);
        var muted = cfg.BlockedTerritories.Contains(here);
        if (ImGui.Button(muted ? $"Unmute {zone}" : $"Mute {zone}"))
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

        Tip(
            muted
            ? "Start playing here again."
            : "Never play anything while you are in this zone. Good for hub cities.");

        if (cfg.BlockedTerritories.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{cfg.BlockedTerritories.Count} muted zone(s)");
            ImGui.SameLine();
            if (ImGui.SmallButton("Unmute all zones"))
            {
                cfg.BlockedTerritories.Clear();
                dirty = true;
            }
        }

        Section("Muted actions");

        if (cfg.MutedActionIds.Count == 0)
        {
            ImGui.TextDisabled("  None. Use the Mute button beside a row on the Events tab.");
        }
        else
        {
            ImGui.TextDisabled($"  {cfg.MutedActionIds.Count} action(s) never play, whatever is mapped to them.");

            // Individually removable. Previously the only control was "Unmute all", so one
            // mis-click meant redoing the whole list.
            uint? unmute = null;
            foreach (var id in cfg.MutedActionIds.OrderBy(x => x))
            {
                if (ImGui.SmallButton($"unmute##{id}"))
                {
                    unmute = id;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted($"{this.ActionName(id)}   #{id}");
            }

            if (unmute is { } removed)
            {
                cfg.MutedActionIds.Remove(removed);
                dirty = true;
            }

            if (ImGui.SmallButton("Unmute all actions"))
            {
                cfg.MutedActionIds.Clear();
                dirty = true;
            }
        }

        if (dirty)
        {
            cfg.Save();
        }

        // More space above a heading than below it, so a long page still reads as groups
        // rather than one list.
        static void Section(string title)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextUnformatted(title);
        }

        static bool Toggle(string label, bool current, ref bool dirty, string? tooltip)
        {
            var value = current;
            if (ImGui.Checkbox(label, ref value))
            {
                dirty = true;
            }

            if (tooltip is not null)
            {
                Tip(tooltip);
            }

            return value;
        }
    }

    /// <summary>
    /// Where the sound comes out, in terms of what it costs the listener rather than which
    /// library is involved.
    /// </summary>
    /// <remarks>
    /// The labels used to read "NAudio only" and "Game engine, NAudio fallback". NAudio is
    /// a NuGet package; naming it in a player-facing combo asks the player to care which
    /// code path they are on in order to pick one.
    /// </remarks>
    private static readonly (SinkMode Mode, string Label, string Blurb)[] SinkModes =
    [
        (SinkMode.Auto, "Game engine, with backup",
         "The game's own sound engine when it can, Warcry's built-in player when it cannot,\n" +
         "for instance while a clip is still being prepared, or if Penumbra is not running.\n\n" +
         "Forgiving, and the best choice unless you specifically care that every line comes\n" +
         "out of the game engine."),
        (SinkMode.NativeOnly, "Game engine only",
         "Every line goes through the game's own sound engine, or it does not play at all.\n\n" +
         "This is the mode where lines sit in the world properly, with distance, direction and the\n" +
         "game's own volume rules. A line the engine cannot play is skipped and the reason\n" +
         "is shown on the Events tab, never quietly swapped for something else.\n\n" +
         "Needs Penumbra, and needs your clips prepared on the Actions tab."),
        (SinkMode.ManagedOnly, "Built-in player",
         "Warcry mixes and plays everything itself. No Penumbra needed, and no waiting for\n" +
         "clips to be prepared.\n\n" +
         "The trade-off is that lines are not in the world at all: no distance, no direction.\n" +
         "They play flat, like a notification sound."),
        (SinkMode.Off, "Nothing plays",
         "No sound on your actions. Previewing a clip from the Clips tab still works."),
    ];

    /// <summary>
    /// The sound-output choice, with the honest caveats attached to it rather than buried
    /// in a document.
    /// </summary>
    /// <summary>
    /// One labelled combo over a mode table, with a blurb on hover and an optional reason
    /// a mode is unpickable right now.
    /// </summary>
    /// <remarks>
    /// Shared by all three mode settings on this tab. Each used to carry its own copy of
    /// "scan for the current label, BeginCombo, Selectable per mode, tooltip, EndCombo",
    /// which is the part none of them differ in — what differs is the table and the
    /// consequence line each draws underneath.
    /// </remarks>
    private static void ModeCombo<T>(
        string label,
        (T Mode, string Label, string Blurb)[] modes,
        ref T current,
        ref bool dirty,
        Func<T, string?>? unavailable = null)
        where T : struct, Enum
    {
        var currentLabel = "?";
        foreach (var (mode, text, _) in modes)
        {
            if (mode.Equals(current))
            {
                currentLabel = text;
            }
        }

        ImGui.SetNextItemWidth(260f);
        if (!ImGui.BeginCombo(label, currentLabel))
        {
            return;
        }

        foreach (var (mode, text, blurb) in modes)
        {
            var why = unavailable?.Invoke(mode);

            if (why is not null)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Selectable(text, mode.Equals(current)))
            {
                current = mode;
                dirty = true;
            }

            if (why is not null)
            {
                ImGui.EndDisabled();
            }

            Tip(why is null ? blurb : $"{blurb}\n\n({why})", ImGuiHoveredFlags.AllowWhenDisabled);
        }

        ImGui.EndCombo();
    }

    private void DrawSoundOutputSetting(Configuration cfg, ref bool dirty)
    {
        var penumbra = this.plugin.Penumbra.PenumbraAvailable;
        var sink = cfg.Sink;

        ModeCombo(
            "Sound output",
            SinkModes,
            ref sink,
            ref dirty,
            m => m is SinkMode.NativeOnly or SinkMode.Auto && !penumbra
                ? "Needs Penumbra, which is not running."
                : null);

        cfg.Sink = sink;

        // The consequence line: one sentence saying what the current choice means, in the
        // same words the Status tab will use when it happens.
        ImGui.TextDisabled(cfg.Sink switch
        {
            SinkMode.NativeOnly => "  Game engine or nothing. A skipped line is listed on Events with a reason.",
            SinkMode.Auto => "  Game engine when it can, built-in player when it cannot.",
            SinkMode.ManagedOnly => "  Everything plays flat, with no distance or direction.",
            _ => "  Nothing plays on your actions.",
        });

        if (!penumbra && cfg.Sink is SinkMode.NativeOnly or SinkMode.Auto)
        {
            ImGui.TextUnformatted("  ⚠ Penumbra is not running, so nothing can reach the game engine right now.");
        }

        if (cfg.Sink is not (SinkMode.NativeOnly or SinkMode.Auto))
        {
            return;
        }

        var composite = this.plugin.Composite;

        if (composite.Demoted)
        {
            ImGui.TextUnformatted(cfg.Sink == SinkMode.NativeOnly
                ? "  ⚠ The game engine path failed repeatedly and has been given up on for this session,\n" +
                  "    and this mode never falls back, so NOTHING IS PLAYING. Reload the plugin to retry."
                : "  ⚠ The game engine path failed repeatedly and has been given up on for this session.\n" +
                  "    The built-in player is handling everything. Reload the plugin to retry.");
        }
    }

    private static readonly (VoicePositionMode Mode, string Label, string Blurb)[] VoicePositions =
    [
        (VoicePositionMode.Follow, "Follows your character",
         "The line moves with you while it plays, so a dash, a jump or a gap closer cannot\n" +
         "outrun it.\n\n" +
         "This is what you want in almost every case."),
        (VoicePositionMode.Fixed, "Stays where it started",
         "The line sounds from wherever you were standing when it began, and stays there.\n\n" +
         "Anything that moves you away from that spot, like a dash or a knockback, leaves the\n" +
         "line behind, and it fades out as you go."),
        (VoicePositionMode.Listener, "Ignore distance",
         "Full volume wherever you are, with no falloff and no direction.\n\n" +
         "Immune to being moved by construction, at the cost of not sounding like it is\n" +
         "coming from your character at all."),
    ];

    private static readonly (GruntMode Mode, string Label, string Blurb)[] GruntModes =
    [
        (GruntMode.Off, "Leave them alone",
         "Your character grunts exactly as the game intends, and a Warcry line plays on\n" +
         "top of it."),
        (GruntMode.WhenVoiced, "Silence the one Warcry replaces",
         "The grunt is silenced only for actions Warcry actually voices, so you hear your\n" +
         "clip instead of both at once.\n\n" +
         "Actions you have nothing mapped to still grunt normally, and so does everyone\n" +
         "whose lines you are not playing."),
        (GruntMode.Always, "Silence all of them",
         "Nobody grunts when they attack — not you, not anyone near you — whether or not\n" +
         "Warcry has anything to say for it.\n\n" +
         "This one is about the game's audio rather than about Warcry: it needs nothing\n" +
         "mapped and no clips loaded."),
    ];

    /// <summary>
    /// How much of the game's own battle voice to take away. Off by default, because an
    /// update that changes what the game sounds like unasked is a nasty surprise.
    /// </summary>
    private void DrawGruntSetting(Configuration cfg, ref bool dirty)
    {
        if (!this.plugin.Grunts.Installed)
        {
            ImGui.TextUnformatted("⚠ Unavailable — Warcry could not find the game's sound function.");
            ImGui.TextDisabled("   Usually a game patch. Detection and playback are unaffected.");
            return;
        }

        var grunts = cfg.Grunts;
        ModeCombo("Grunt when you attack", GruntModes, ref grunts, ref dirty);
        cfg.Grunts = grunts;

        // Only one mode has to attribute a grunt to a caster, so only one has a window.
        if (cfg.Grunts == GruntMode.WhenVoiced)
        {
            cfg.GruntWindowSeconds = Slider(
                "Stay silent for", cfg.GruntWindowSeconds, 0.1f, 5f, "%.1f s", ref dirty,
                "The grunt comes from the action's animation rather than from the action\n" +
                "itself, so it can arrive anything up to a second after Warcry hears the\n" +
                "cast. This is how long to keep waiting for it.\n\n" +
                "Too short and it slips through on slow animations. Too long and a fast\n" +
                "rotation loses the grunt of the action after the one Warcry voiced.");
        }

        // Worth saying under either suppressing mode: "silence all of them" reads like it
        // would take the character's whole voice, and it does not.
        ImGui.TextDisabled(cfg.Grunts == GruntMode.Off
            ? "  Nothing of the game's own voice is taken away."
            : "  Grunts for taking damage and dying are never touched.");
    }

    /// <summary>
    /// Where a line sounds from. Follow is the default because the alternative is a line
    /// that fades out mid-word whenever a skill moves you.
    /// </summary>
    private void DrawVoicePositionSetting(Configuration cfg, ref bool dirty)
    {
        var position = cfg.VoicePosition;
        ModeCombo("Line follows you", VoicePositions, ref position, ref dirty);
        cfg.VoicePosition = position;

        // Said here rather than left for the user to discover: the built-in player has no
        // positional model at all, so it cannot honour any of these.
        ImGui.TextDisabled(cfg.Sink switch
        {
            SinkMode.ManagedOnly => "  The built-in player ignores this. It has no positioning at all.",
            SinkMode.Auto => "  Game engine lines only. A line played by the backup has no position.",
            SinkMode.Off => "  Nothing plays on your actions.",
            _ => cfg.VoicePosition switch
            {
                VoicePositionMode.Follow => "  Lines follow you, so being displaced does not cut them off.",
                VoicePositionMode.Fixed => "  Lines stay put, and moving away fades them out.",
                _ => "  Lines are not placed in the world at all.",
            },
        });
    }
}
