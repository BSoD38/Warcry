using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Warcry.Game;

namespace Warcry.Windows;

// The People tab: whose actions you hear, and how much of them. Ordered by the question
// being asked — who, then which of them by name, then how loud and how often. The noise
// controls sit here rather than on Settings because every one of them is meaningless until
// somebody other than you can be heard.
// Labels talk about people, not buckets, tiers or filters; the implementation vocabulary
// stays in tooltips.
public sealed partial class MainWindow
{
    // One buffer per row: ImGui widgets are distinct by ID, but a shared string behind them
    // is not, so typing into one box echoes into the other.
    private string newNamedName = string.Empty;
    private string newBlockedName = string.Empty;

    private readonly List<string> recentCasters = [];

    private void DrawPeople()
    {
        var cfg = this.plugin.Config;
        var filter = this.plugin.Audience;
        var dirty = false;

        PeopleSection("Whose actions you hear");

        foreach (var bucket in Audience.ClassifyOrder)
        {
            var on = (cfg.Audience & bucket) != 0;
            if (ImGui.Checkbox(Audience.Label(bucket), ref on))
            {
                cfg.Audience = on ? cfg.Audience | bucket : cfg.Audience & ~bucket;
                dirty = true;
            }

            Tip(BucketTooltip(bucket));
        }

        if (cfg.Audience == AudienceBucket.None)
        {
            ImGui.TextUnformatted("  ⚠ Nobody is selected, so nothing will ever play.");
        }

        this.DrawCrowdReadout();

        PeopleSection("People you name");

        this.DrawNamedList(cfg, filter);

        PeopleSection("People you block");

        this.DrawBlockedList(cfg, filter);

        PeopleSection("How loud, how far, how often");

        this.DrawRemoteTuning(cfg, ref dirty);

        if (dirty)
        {
            cfg.Save();
        }

        // Same shape as the Settings tab's Section: more space above a heading than below,
        // so a long page reads as groups.
        static void PeopleSection(string title)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextUnformatted(title);
        }
    }

    private static string BucketTooltip(AudienceBucket bucket) => bucket switch
    {
        AudienceBucket.Self =>
            "Your own actions. Turning this off is not the same as turning Warcry off:\n" +
            "everyone else you have selected is still heard.",
        AudienceBucket.Named =>
            "Only the people on your list below. The most controlled way to hear anyone\n" +
            "but yourself, and the one that survives a crowded zone.",
        AudienceBucket.Party =>
            "Everyone in your light party. Works cross-world.",
        AudienceBucket.Alliance =>
            "The other two parties in an alliance raid. Up to 16 more people, so expect\n" +
            "the crowd controls below to be doing real work.",
        AudienceBucket.Friend =>
            "Anyone on your friend list who is nearby. The game tells us this without the\n" +
            "friend list ever being opened.",
        AudienceBucket.Other =>
            "Every other player character in range. This is the loud one: in a hub city it\n" +
            "means strangers, constantly.\n\n" +
            "The distance limit and the crowd controls below are what make it survivable.\n\n" +
            "It also costs memory on the game-engine output: clips are loaded for every job\n" +
            "around you and the game cannot unload them again until it restarts. The Status\n" +
            "tab shows how much.",
        _ => string.Empty,
    };

    // Live numbers rather than a description of the algorithm: "stranger lines are on a 9 s
    // gap because there are 18 people around you" is actionable, "crowd scaling enabled" is
    // not.
    private void DrawCrowdReadout()
    {
        var crowd = this.plugin.Crowd;

        if (!this.plugin.Audience.HearsAnyoneElse)
        {
            ImGui.TextDisabled("  Only your own actions play, so none of the limits below apply.");
            return;
        }

        var scale = crowd.CooldownScale;
        var strangerGap = this.plugin.Throttle.CooldownFor(AudienceBucket.Other);

        ImGui.TextDisabled(crowd.NearbyCount == 0
            ? "  Nobody you can hear is nearby right now."
            : $"  {crowd.NearbyCount} audible player(s) nearby.");

        if (scale > 1.001f)
        {
            ImGui.TextDisabled(
                $"  Crowded, so other people's lines are held to {strangerGap:0.0}s apart " +
                $"(x{scale:0.0}). Yours are not.");
        }
    }

    private void DrawNamedList(Configuration cfg, Gating.AudienceFilter filter)
    {
        if ((cfg.Audience & AudienceBucket.Named) == 0)
        {
            ImGui.TextDisabled("  \"People I named\" is off above, so this list is only used by profiles.");
        }

        this.DrawAddPersonRow(filter, blocked: false);

        if (cfg.NamedPeople.Count == 0)
        {
            ImGui.TextDisabled("  Nobody named yet.");
        }

        NamedPlayer? removeNamed = null;
        NamedPlayer? blockInstead = null;

        foreach (var person in cfg.NamedPeople)
        {
            ImGui.PushID($"named:{person.Name}:{person.World}");

            if (ImGui.SmallButton("X"))
            {
                removeNamed = person;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("block"))
            {
                blockInstead = person;
            }

            Tip("Move them to the blocked list, so nothing of theirs ever plays.");

            ImGui.SameLine();
            ImGui.TextUnformatted(person.Describe());

            if (person.World == 0)
            {
                Tip("Matches this name on any world. Add them from your target to pin a world.");
            }

            ImGui.PopID();
        }

        if (removeNamed is not null)
        {
            filter.RemoveNamed(removeNamed);
        }

        if (blockInstead is not null)
        {
            filter.RemoveNamed(blockInstead);
            filter.AddBlocked(blockInstead);
        }

        this.DrawProfileNameWarning(filter);
    }

    private void DrawBlockedList(Configuration cfg, Gating.AudienceFilter filter)
    {
        this.DrawAddPersonRow(filter, blocked: true);

        if (cfg.BlockedPeople.Count == 0)
        {
            ImGui.TextDisabled("  Nobody blocked. Blocking beats every other reason to hear someone.");
            return;
        }

        NamedPlayer? unblock = null;

        foreach (var person in cfg.BlockedPeople)
        {
            ImGui.PushID($"blocked:{person.Name}:{person.World}");

            if (ImGui.SmallButton("X"))
            {
                unblock = person;
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(person.Describe());
            ImGui.PopID();
        }

        if (unblock is not null)
        {
            filter.RemoveBlocked(unblock);
        }
    }

    // Three ways to name someone: type it, take your target, or pick a caster the Events tab
    // has seen. The target button is the only route that captures the home world and needs
    // no spelling; typing is the fallback for someone not in the zone, and matches on any
    // world for that reason.
    private void DrawAddPersonRow(Gating.AudienceFilter filter, bool blocked)
    {
        ImGui.PushID(blocked ? "##addblocked" : "##addnamed");

        var entered = blocked
            ? DrawPlayerEntry("Block", ref this.newBlockedName)
            : DrawPlayerEntry("Add", ref this.newNamedName);

        if (entered is not null)
        {
            Add(entered);
        }

        ImGui.SameLine();
        this.DrawRecentCasterPicker(Add);

        ImGui.PopID();

        void Add(NamedPlayer person)
        {
            if (blocked)
            {
                filter.AddBlocked(person);
            }
            else
            {
                filter.AddNamed(person);
            }
        }
    }

    // Names the hook has seen cast something, newest first: the only route that cannot be
    // misspelled for someone no longer targetable. The event ring keeps the name and not the
    // world, so these match on any world.
    private void DrawRecentCasterPicker(System.Action<NamedPlayer> add)
    {
        if (!ImGui.BeginCombo("##recent", "From a recent event", ImGuiComboFlags.HeightLarge))
        {
            return;
        }

        var casters = this.RecentCasterNames(20);

        if (casters.Count == 0)
        {
            ImGui.TextDisabled("Nobody else has cast anything yet.");
            ImGui.TextDisabled("The Events tab fills up even while nothing is playing.");
        }

        foreach (var caster in casters)
        {
            if (ImGui.Selectable(caster))
            {
                add(new NamedPlayer { Name = caster });
            }
        }

        ImGui.EndCombo();
    }

    // Points out profiles aimed at someone who can never be heard. The two lists answer
    // different questions — this one decides who is heard, a profile's decides which clips
    // they get — and the failure mode of that split is silence.
    private void DrawProfileNameWarning(Gating.AudienceFilter filter)
    {
        List<NamedPlayer>? missing = null;

        foreach (var profile in this.plugin.Profiles.Profiles)
        {
            foreach (var person in profile.Match.Names)
            {
                if (filter.IsNamed(person.Name) || filter.IsBlocked(person.Name))
                {
                    continue;
                }

                missing ??= [];
                if (!missing.Exists(p => PlayerId.Of(p.Name) == PlayerId.Of(person.Name)))
                {
                    missing.Add(person);
                }
            }
        }

        if (missing is null)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(
            $"  ⚠ {missing.Count} player(s) have a profile aimed at them but are not on this list,");
        ImGui.TextUnformatted("     so nothing of theirs can ever play.");

        if (ImGui.SmallButton($"Add all {missing.Count} to the list"))
        {
            foreach (var person in missing)
            {
                filter.AddNamed(new NamedPlayer
                {
                    Name = person.Name,
                    World = person.World,
                    WorldName = person.WorldName,
                });
            }
        }

        foreach (var person in missing)
        {
            ImGui.TextDisabled($"     {person.Describe()}");
        }
    }

    private void DrawRemoteTuning(Configuration cfg, ref bool dirty)
    {
        if (!this.plugin.Audience.HearsAnyoneElse)
        {
            ImGui.TextDisabled("  Nothing here applies while you are the only one being voiced.");
        }

        cfg.OtherPlayerGain = Slider("Other players' volume", cfg.OtherPlayerGain, 0f, 2f, "%.2f", ref dirty,
            "A trim on everyone but you, on top of the game's own sliders.\n" +
            "Below 1.00 keeps other people present without competing with your own lines.");

        cfg.MaxDistanceYalms = SliderInt(
            "Hear people within", cfg.MaxDistanceYalms, 0, 100,
            cfg.MaxDistanceYalms == 0 ? "any distance" : "%d yalms", ref dirty,
            "Past this, a line is never even requested. That is different from it being\n" +
            "quiet: it costs no sound slot, so a distant crowd cannot crowd out the\n" +
            "people next to you.\n\n" +
            "Never applies to your own actions.");

        ImGui.Spacing();
        ImGui.TextDisabled("  Minimum gap between one person's lines");

        // Your own gap lives on Settings, beside the rest of the settings about your own
        // lines, and is deliberately not repeated here.
        ImGui.TextDisabled(cfg.SelfCooldownSeconds <= 0f
            ? "    Me: off — set under \"When lines play\" on the Settings tab."
            : $"    Me: {cfg.SelfCooldownSeconds:0.0}s — set under \"When lines play\" on the Settings tab.");

        // A property cannot be passed by ref, so each gap is read, edited, written back.
        cfg.NamedCooldownSeconds = Gap("People I named", cfg.NamedCooldownSeconds, ref dirty);
        cfg.PartyCooldownSeconds = Gap("Party, alliance and friends", cfg.PartyCooldownSeconds, ref dirty);
        cfg.OtherCooldownSeconds = Gap("Everyone else", cfg.OtherCooldownSeconds, ref dirty);

        ImGui.Spacing();

        var scale = cfg.ScaleWithCrowd;
        if (ImGui.Checkbox("Quieten other people as the crowd grows", ref scale))
        {
            cfg.ScaleWithCrowd = scale;
            dirty = true;
        }

        Tip(
            "Above the crowd size below, everyone else's gap stretches in proportion.\n" +
            "Your own lines and their timing are never affected.\n\n" +
            "This is what stops a hub city or an alliance raid becoming a wall of noise.");

        if (cfg.ScaleWithCrowd)
        {
            cfg.SoftCrowdLimit = SliderInt(
                "Crowd size before quietening", cfg.SoftCrowdLimit, 1, 48, "%d", ref dirty);
            cfg.MaxCrowdScale = Slider(
                "Stretch gaps at most", cfg.MaxCrowdScale, 1f, 20f, "x%.1f", ref dirty);
        }

        ImGui.Spacing();

        var rate = cfg.LimitTotalRate;
        if (ImGui.Checkbox("Cap how fast other people's lines can arrive", ref rate))
        {
            cfg.LimitTotalRate = rate;
            dirty = true;
        }

        Tip(
            "The gaps above bound one person. This bounds all of them together, so ten\n" +
            "people each casting once still cannot fire ten lines at you.\n\n" +
            "Your own lines never spend from it. Lines over the cap are dropped rather\n" +
            "than queued: one arriving late is worse than one that never arrives.");

        if (cfg.LimitTotalRate)
        {
            cfg.RateBurst = SliderInt("Lines in a row", cfg.RateBurst, 1, 12, "%d", ref dirty);
            cfg.RateRefillSeconds = Slider(
                "One line back every", cfg.RateRefillSeconds, 0.25f, 10f, "%.2f s", ref dirty);

            ImGui.TextDisabled($"  {this.plugin.Throttle.TokensAvailable} of {cfg.RateBurst} available right now.");
        }

        // The three per-tier gaps share one range and one "off" format.
        static float Gap(string label, float current, ref bool dirty)
            => Slider(label, current, 0f, 20f, current <= 0f ? "off" : "%.1f s", ref dirty);
    }
}
