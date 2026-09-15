namespace Warcry.Game;

/// <summary>One player identified by hand, for the named list, the block list or a profile.</summary>
/// <remarks>
/// <para>A dumb DTO on purpose. The 64-bit name hashes the cast path actually compares are
/// precomputed by whoever holds the list — <c>Gating.AudienceFilter</c> for the config's
/// lists, <c>Profiles.ProfileMatch</c> for a profile's — so nothing here has to stay in
/// sync with an edit, and the detour never touches a string.</para>
/// <para>This type's namespace is baked into every user's config file by Newtonsoft's
/// TypeNameHandling.Objects, exactly as <see cref="Warcry.Configuration"/>'s is. Moving or
/// renaming it orphans their named lists.</para>
/// </remarks>
public sealed class NamedPlayer
{
    /// <summary>Character name as it appears in game, e.g. "Alice Smith".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Home-world row id. 0 means "whatever world they are on".</summary>
    public uint World { get; set; }

    /// <summary>World name for display only. Never matched on.</summary>
    public string WorldName { get; set; } = string.Empty;

    public string Describe() => this.World == 0 || this.WorldName.Length == 0
        ? this.Name
        : $"{this.Name} @ {this.WorldName}";
}
