using System.Collections.Generic;

namespace Warcry.Clips;

/// <summary>Metadata for one imported clip. The audio itself lives beside it, content-addressed.</summary>
public sealed class ClipInfo
{
    /// <summary>SHA-256 of the source bytes, lowercase hex. The clip's identity everywhere.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Relative path under the clips directory, e.g. "3f/3f2a….wav".</summary>
    public string Relative { get; set; } = string.Empty;

    /// <summary>The user's original filename. They need to recognise their own clips.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public int DurationMs { get; set; }

    public int SourceSampleRate { get; set; }

    public int SourceChannels { get; set; }
}

/// <summary>On-disk shape of clipmeta.json.</summary>
public sealed class ClipManifest
{
    public int Version { get; set; } = 1;

    public List<ClipInfo> Clips { get; set; } = [];
}
