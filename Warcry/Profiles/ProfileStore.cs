using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace Warcry.Profiles;

// Owns the user's action-to-clip mappings, persisted to profiles.json.
// Deliberately NOT in IPluginConfiguration: that serialises with TypeNameHandling.Objects,
// so renaming a class orphans every user's data, and writes synchronously through a 64 MB
// capped store. Plain System.Text.Json with its own schema version instead, so the mapping
// table can evolve independently. See docs/PLAN.md 5.8.
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string path;
    private readonly IPluginLog log;

    private ProfileDocument document = new();

    // Most-specific first. THIS ORDER IS THE FALLBACK CHAIN.
    private VoiceProfile[] sorted = [];

    public ProfileStore(string configDirectory, IPluginLog log)
    {
        this.log = log;
        Directory.CreateDirectory(configDirectory);
        this.path = Path.Combine(configDirectory, "profiles.json");
        this.Load();
    }

    public IReadOnlyList<VoiceProfile> Profiles => this.document.Profiles;

    public IReadOnlyList<VoiceProfile> Sorted => this.sorted;

    // Bumped on every successful save. The pack builder watches it to notice a changed
    // mapping set without holding a reference into the document.
    public int Revision { get; private set; }

    public void Load()
    {
        try
        {
            if (!File.Exists(this.path))
            {
                this.document = new ProfileDocument();
                this.Resort();
                return;
            }

            var json = File.ReadAllText(this.path);
            var parsed = JsonSerializer.Deserialize<ProfileDocument>(json, JsonOptions);

            // A broken file must never replace a good in-memory state silently.
            if (parsed is null)
            {
                this.log.Error("ProfileStore: profiles.json parsed to null — keeping current state");
                return;
            }

            this.document = parsed;
            this.Resort();
            this.log.Information("ProfileStore: {Count} profiles loaded", this.document.Profiles.Count);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "ProfileStore: could not read profiles.json — keeping current state");
        }
    }

    public void Save()
    {
        try
        {
            // Sort BEFORE serialising, so the file records the order the resolver will
            // actually use.
            this.Resort();

            var json = JsonSerializer.Serialize(this.document, JsonOptions);
            var tmp = this.path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, this.path, overwrite: true);
            this.Revision++;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "ProfileStore: could not save profiles.json");
        }
    }

    // Sorting once here is what makes resolution a linear scan: the fallback chain
    // (voiceId -> voiceSlot -> tribe -> race -> sex -> wildcard) falls out of the order
    // rather than being hand-coded.
    private void Resort()
    {
        // Load and Save are the only two moments a profile's name list can have changed,
        // and both come through here. Rebuilding the derived hashes at this one point is
        // what lets the cast path match a named player without touching a string.
        foreach (var profile in this.document.Profiles)
        {
            profile.Match.Names ??= [];
            profile.Match.RebuildNameCache();
        }

        this.sorted = this.document.Profiles
            .OrderByDescending(p => p.Match.Specificity)
            .ThenByDescending(p => p.Priority)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToArray();

        foreach (var profile in this.document.Profiles)
        {
            // OrderByDescending, not List.Sort: List.Sort is an unstable introsort and most
            // rules tie on specificity, so it would permute the ties on every save. The
            // resolver takes the FIRST matching rule, so which clip wins would change
            // because of an unrelated edit.
            var ordered = profile.Rules.OrderByDescending(r => r.When.Specificity).ToList();
            profile.Rules.Clear();
            profile.Rules.AddRange(ordered);
        }
    }

    public VoiceProfile Add(VoiceProfile profile)
    {
        this.document.Profiles.Add(profile);
        this.Save();
        return profile;
    }

    public void Remove(string profileId)
    {
        this.document.Profiles.RemoveAll(p => p.Id == profileId);
        this.Save();
    }

    // Where new mappings land when the user has not set any profile up. Matches everything,
    // so the common case — one person, one character — needs no thought about profiles. The
    // match fields are for alts and for aiming a set at someone else.
    public VoiceProfile GetOrCreateDefault()
    {
        var existing = this.document.Profiles.FirstOrDefault();
        if (existing is not null)
        {
            return existing;
        }

        var profile = new VoiceProfile
        {
            Name = "Default",
            Match = new ProfileMatch(),
        };

        return this.Add(profile);
    }

    // Reuses an existing rule for the action if there is one. family is every id that
    // resolves to this action, the action itself plus anything that upgrades into it,
    // because a bare id breaks under level sync; englishName is the language-independent
    // identity, which catches duplicate and variant rows.
    public void MapClipToAction(
        VoiceProfile profile,
        uint actionId,
        string actionName,
        string clipHash,
        IReadOnlyCollection<uint>? family = null,
        string? englishName = null)
    {
        var rule = profile.Rules.FirstOrDefault(r => r.When.ActionIds.Contains(actionId));

        if (rule is null)
        {
            rule = new VoiceRule
            {
                Label = actionName,
                When = new RuleWhen
                {
                    ActionIds = family is { Count: > 0 } ? [.. family] : [actionId],
                    ActionNames = string.IsNullOrWhiteSpace(englishName) ? [] : [englishName],
                },
            };
            profile.Rules.Add(rule);
        }
        else
        {
            // Widen an existing rule if we now know about more forms of the same action.
            if (family is not null)
            {
                foreach (var id in family)
                {
                    if (!rule.When.ActionIds.Contains(id))
                    {
                        rule.When.ActionIds.Add(id);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(englishName) && !rule.When.ActionNames.Contains(englishName))
            {
                rule.When.ActionNames.Add(englishName);
            }
        }

        if (rule.Clips.All(c => c.Hash != clipHash))
        {
            rule.Clips.Add(new ClipRef { Hash = clipHash });
        }

        this.Save();
    }

    public void RemoveRule(VoiceProfile profile, string ruleId)
    {
        profile.Rules.RemoveAll(r => r.Id == ruleId);
        this.Save();
    }

    public void RemoveClipFromRule(VoiceProfile profile, VoiceRule rule, string clipHash)
    {
        rule.Clips.RemoveAll(c => c.Hash == clipHash);
        if (rule.Clips.Count == 0)
        {
            profile.Rules.Remove(rule);
        }

        this.Save();
    }
}
