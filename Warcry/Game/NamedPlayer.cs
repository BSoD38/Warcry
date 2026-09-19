namespace Warcry.Game;

// Namespace is baked into every user's config by Newtonsoft's TypeNameHandling.Objects.
// Moving or renaming this type orphans their named lists.
public sealed class NamedPlayer
{
    public string Name { get; set; } = string.Empty;

    // Home-world row id. 0 means "whatever world they are on".
    public uint World { get; set; }

    // Display only, never matched on.
    public string WorldName { get; set; } = string.Empty;

    public string Describe() => this.World == 0 || this.WorldName.Length == 0
        ? this.Name
        : $"{this.Name} @ {this.WorldName}";
}
