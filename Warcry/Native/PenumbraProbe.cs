using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace Warcry.Native;

/// <summary>
/// Answers one question, visually: does our Penumbra integration work <em>at all</em>?
/// </summary>
/// <remarks>
/// <para>The native-audio spike failed without ever establishing whether our
/// <c>AddTemporaryModAll</c> calls have any effect. This isolates that from everything
/// about sound: redirect icon A's path to icon B's <em>bytes</em> and look at it.</para>
/// <para>Both files are real game data, so nothing can render corrupt or crash — the only
/// question is whether A shows B's artwork.</para>
/// <para><b>Positive is decisive</b> (our IPC works, so `.scd` is special — consistent with
/// Penumbra treating SCD as a protected file type). <b>Negative is weaker evidence</b>:
/// if Dalamud's texture provider loads via Lumina rather than the game's resource system,
/// Penumbra never sees the request and the result says nothing.</para>
/// </remarks>
public sealed class PenumbraProbe
{
    /// <summary>Reference artwork, shown for comparison. A job icon — distinctive and always present.</summary>
    public const uint ReferenceIcon = 62125;

    /// <summary>
    /// Fresh target per attempt. A texture already displayed is cached by path, so reusing
    /// one would ignore the redirect and look like failure.
    /// </summary>
    private static readonly uint[] Candidates =
    [
        62101, 62102, 62103, 62104, 62105, 62106, 62107, 62108, 62109, 62110,
    ];

    private readonly IDataManager data;
    private readonly ITextureProvider textures;
    private readonly PenumbraBridge penumbra;
    private readonly IPluginLog log;
    private readonly string cacheDir;

    private int nextCandidate;

    public PenumbraProbe(
        IDataManager data,
        ITextureProvider textures,
        PenumbraBridge penumbra,
        IPluginLog log,
        string configDirectory)
    {
        this.data = data;
        this.textures = textures;
        this.penumbra = penumbra;
        this.log = log;
        this.cacheDir = Path.Combine(configDirectory, ".cache", "tex");

        // Guarded because this runs inside the Plugin constructor: an I/O failure must
        // degrade the probe, not fail the whole plugin load.
        try
        {
            Directory.CreateDirectory(this.cacheDir);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "PenumbraProbe: could not create the cache directory");
        }
    }

    public List<string> Report { get; } = [];

    /// <summary>The icon whose path was redirected, or 0 if no attempt has run.</summary>
    public uint RedirectedIcon { get; private set; }

    public bool Exhausted => this.nextCandidate >= Candidates.Length;

    public void Run()
    {
        this.Report.Clear();

        if (this.Exhausted)
        {
            this.Say("Out of fresh icons — reload the plugin to reset (cached paths ignore redirects).");
            return;
        }

        if (!this.penumbra.PenumbraAvailable)
        {
            this.Say("FAIL — Penumbra is not installed or not loaded.");
            return;
        }

        var target = Candidates[this.nextCandidate++];

        // Path of the icon we will REPLACE.
        if (!this.textures.TryGetIconPath(new GameIconLookup(target), out var targetPath) || targetPath is null)
        {
            this.Say($"FAIL — no path for icon {target}.");
            return;
        }

        // Bytes of the icon we will replace it WITH.
        if (!this.textures.TryGetIconPath(new GameIconLookup(ReferenceIcon), out var sourcePath) || sourcePath is null)
        {
            this.Say($"FAIL — no path for reference icon {ReferenceIcon}.");
            return;
        }

        byte[] sourceBytes;
        try
        {
            var file = this.data.GetFile(sourcePath);
            if (file?.Data is not { Length: > 0 })
            {
                this.Say($"FAIL — could not read {sourcePath}.");
                return;
            }

            sourceBytes = file.Data;
        }
        catch (Exception ex)
        {
            this.Say($"FAIL — reading reference: {ex.Message}");
            return;
        }

        var localPath = Path.Combine(this.cacheDir, $"probe_{target}.tex");
        try
        {
            File.WriteAllBytes(localPath, sourceBytes);
        }
        catch (Exception ex)
        {
            this.Say($"FAIL — writing {localPath}: {ex.Message}");
            return;
        }

        this.Say($"replacing icon {target}  ({targetPath})");
        this.Say($"        with icon {ReferenceIcon}  ({sourceBytes.Length} bytes)");

        var code = this.penumbra.Redirect(PenumbraBridge.ProbeTag, new Dictionary<string, string> { [targetPath] = localPath });
        if (code is null)
        {
            this.Say($"FAIL — redirect call failed: {this.penumbra.LastError}");
            return;
        }

        this.Say(code == 0 ? "redirect registered (code 0)" : $"redirect returned {code}");
        this.RedirectedIcon = target;
        this.Say("Now compare the two icons below. Same artwork means the redirect APPLIED.");
    }

    private void Say(string line)
    {
        this.Report.Add(line);
        this.log.Information("[texprobe] {Line}", line);
    }
}
