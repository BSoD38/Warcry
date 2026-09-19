using System.Collections.Generic;

namespace Warcry.Clips;

public sealed class ClipInfo
{
    // SHA-256 of the source bytes, lowercase hex. The clip's identity everywhere.
    public string Hash { get; set; } = string.Empty;

    // Path under the clips directory, e.g. "3f/3f2a….wav".
    public string Relative { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int DurationMs { get; set; }

    public int SourceSampleRate { get; set; }

    public int SourceChannels { get; set; }
}

// On-disk shape of clipmeta.json.
public sealed class ClipManifest
{
    public int Version { get; set; } = 1;

    public List<ClipInfo> Clips { get; set; } = [];
}
