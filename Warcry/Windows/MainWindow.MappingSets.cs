using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Warcry.Game;
using Warcry.Profiles;

namespace Warcry.Windows;

// The mapping-set bar at the top of the Actions tab: which set new mappings land in, and
// who that set plays for. A "mapping set" is a VoiceProfile; the word profile never reaches
// the user here.
// Two different questions live one line apart: a set's target decides WHICH clips someone
// gets, the People tab decides whether they are heard at all. Aiming a set at someone the
// People tab never admits is silent, so both places say so.
public sealed partial class MainWindow
{
    private string selectedProfileId = string.Empty;
    private string renameBuffer = string.Empty;
    private string setPersonName = string.Empty;

    // Never null — creates the default set if there is none.
    private VoiceProfile ActiveProfile(ProfileStore store)
    {
        foreach (var profile in store.Profiles)
        {
            if (profile.Id == this.selectedProfileId)
            {
                return profile;
            }
        }

        var fallback = store.GetOrCreateDefault();
        this.selectedProfileId = fallback.Id;
        return fallback;
    }

    private void DrawMappingSetBar(ProfileStore store)
    {
        var active = this.ActiveProfile(store);

        ImGui.SetNextItemWidth(260f);
        if (ImGui.BeginCombo("##mappingset", $"{active.Name}  ({active.Match.Describe(RaceName, TribeName)})"))
        {
            foreach (var profile in store.Profiles)
            {
                if (ImGui.Selectable($"{profile.Name}  ({profile.Match.Describe(RaceName, TribeName)})",
                        profile.Id == active.Id))
                {
                    this.selectedProfileId = profile.Id;
                }
            }

            ImGui.Separator();
            if (ImGui.Selectable("New set..."))
            {
                // Aimed at nobody in particular until the user says otherwise, like the
                // first set.
                var created = store.Add(new VoiceProfile { Name = $"Set {store.Profiles.Count + 1}" });
                this.selectedProfileId = created.Id;
            }

            ImGui.EndCombo();
        }

        Tip(
            "Which set new mappings are added to.\n\n" +
            "Sets exist so different people can get different clips: one aimed at you,\n" +
            "one at your party, one at a friend by name. Every set is checked, most\n" +
            "specific first, until one has a clip for the action.");

        ImGui.SameLine();
        this.DrawSetRename(store, active);

        ImGui.SameLine();
        this.DrawSetDelete(store, active);

        this.DrawSetTarget(store, active);
    }

    private void DrawSetRename(ProfileStore store, VoiceProfile active)
    {
        if (ImGui.SmallButton("Rename"))
        {
            this.renameBuffer = active.Name;
            ImGui.OpenPopup("##renameset");
        }

        if (!ImGui.BeginPopup("##renameset"))
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        var buffer = this.renameBuffer;
        ImGui.SetNextItemWidth(240f);
        var submitted = ImGui.InputText("##rename", ref buffer, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        this.renameBuffer = buffer;

        if (submitted && buffer.Trim().Length > 0)
        {
            active.Name = buffer.Trim();
            store.Save();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawSetDelete(ProfileStore store, VoiceProfile active)
    {
        // The only set is not deletable: there would be nowhere for the next mapping to go,
        // and GetOrCreateDefault would simply make it again.
        var only = store.Profiles.Count <= 1;
        if (only)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.SmallButton("Delete"))
        {
            ImGui.OpenPopup("##deleteset");
        }

        if (only)
        {
            ImGui.EndDisabled();
            Tip(
                "The last set cannot be deleted — mappings need somewhere to live.",
                ImGuiHoveredFlags.AllowWhenDisabled);
        }

        if (!ImGui.BeginPopup("##deleteset"))
        {
            return;
        }

        ImGui.TextUnformatted($"Delete \"{active.Name}\" and its {active.Rules.Count} mapping(s)?");
        ImGui.TextDisabled("The clips themselves stay in your library.");

        if (ImGui.Button("Delete"))
        {
            store.Remove(active.Id);
            this.selectedProfileId = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // Who this set plays for: tiers, or specific people by name.
    private void DrawSetTarget(ProfileStore store, VoiceProfile active)
    {
        var match = active.Match;
        var byName = match.Names.Count > 0;

        ImGui.TextUnformatted("Plays for");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(260f);
        if (ImGui.BeginCombo("##settarget", byName
                ? $"{match.Names.Count} named player(s)"
                : Audience.Describe(match.Audience)))
        {
            foreach (var bucket in Audience.ClassifyOrder)
            {
                var on = (match.Audience & bucket) != 0;
                if (ImGui.Checkbox(Audience.Label(bucket), ref on))
                {
                    match.Audience = on ? match.Audience | bucket : match.Audience & ~bucket;
                    store.Save();
                }
            }

            ImGui.Separator();
            if (ImGui.Selectable("Everyone", match.Audience == AudienceBucket.Anyone))
            {
                match.Audience = AudienceBucket.Anyone;
                store.Save();
            }

            ImGui.EndCombo();
        }

        Tip(
            "Which people this set's clips are for.\n\n" +
            "A set aimed at fewer people wins over one aimed at more, so a set for one\n" +
            "named friend beats a set for your party, which beats a set for everyone.\n\n" +
            "This does not decide whether they are heard at all — the People tab does.");

        if (match.Audience == AudienceBucket.None && !byName)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("⚠ nobody, so this set can never play");
        }

        this.DrawSetNames(store, active);
    }

    // The named-players half of a set's target, collapsed until it has something in it: most
    // sets are aimed at a tier, and a name list permanently open above the mapping list
    // would push the work of the tab off the screen.
    private void DrawSetNames(ProfileStore store, VoiceProfile active)
    {
        var match = active.Match;
        var label = match.Names.Count > 0
            ? $"Only these {match.Names.Count} player(s)###setnames"
            : "Only specific players...###setnames";

        if (!ImGui.TreeNodeEx(label, match.Names.Count > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
        {
            return;
        }

        ImGui.TextDisabled("  Named here, this set plays only for them — whatever it is aimed at above.");

        ImGui.PushID("##setnames");
        var entered = DrawPlayerEntry("Add", ref this.setPersonName);
        ImGui.PopID();

        if (entered is not null)
        {
            this.AddSetName(store, match, entered);
        }

        NamedPlayer? remove = null;

        foreach (var person in match.Names)
        {
            ImGui.PushID($"setname:{person.Name}:{person.World}");

            if (ImGui.SmallButton("X"))
            {
                remove = person;
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(person.Describe());

            // Catch the silent failure here rather than only on the People tab.
            if (!this.plugin.Audience.IsNamed(person.Name))
            {
                ImGui.SameLine();
                ImGui.TextUnformatted("⚠ not heard");
                Tip(
                    "They are not on the People tab's list, so nothing of theirs plays at\n" +
                    "all and this set never gets consulted for them.");

                ImGui.SameLine();
                if (ImGui.SmallButton("hear them"))
                {
                    this.plugin.Audience.AddNamed(new NamedPlayer
                    {
                        Name = person.Name,
                        World = person.World,
                        WorldName = person.WorldName,
                    });

                    // Naming someone states that you want to hear them, but the tier they
                    // arrive in still has to be switched on or the list is inert.
                    this.plugin.Config.Audience |= AudienceBucket.Named;
                    this.plugin.Config.Save();
                }
            }

            ImGui.PopID();
        }

        if (remove is not null)
        {
            match.Names.RemoveAll(p => PlayerId.Of(p.Name) == PlayerId.Of(remove.Name) && p.World == remove.World);
            store.Save();
        }

        ImGui.TreePop();
    }

    private void AddSetName(ProfileStore store, ProfileMatch match, NamedPlayer person)
    {
        var hash = PlayerId.Of(person.Name);
        foreach (var existing in match.Names)
        {
            if (PlayerId.Of(existing.Name) == hash && existing.World == person.World)
            {
                return;
            }
        }

        match.Names.Add(person);

        // Save rebuilds the derived name hashes, so the cast path sees the new entry on the
        // very next action rather than after a reload.
        store.Save();
    }

    // Distinct recent caster names, newest first. Shared by the pickers.
    private IReadOnlyList<string> RecentCasterNames(int limit)
    {
        this.recentCasters.Clear();
        var diag = this.plugin.Diag;

        for (var i = 0; i < diag.Count && this.recentCasters.Count < limit; i++)
        {
            var row = diag.At(i);
            if (row.Event.IsLocalPlayer || string.IsNullOrWhiteSpace(row.CasterName))
            {
                continue;
            }

            if (!this.recentCasters.Contains(row.CasterName))
            {
                this.recentCasters.Add(row.CasterName);
            }
        }

        return this.recentCasters;
    }
}
