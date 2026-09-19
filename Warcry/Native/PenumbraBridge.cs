using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Warcry.Native;

// Registers runtime game-path redirects through Penumbra, so the game's resource system
// can load a file we authored. SoundManager.PlaySound takes a game path resolved through
// the resource system, not a filesystem path — which is why Penumbra is needed at all.
// Raw Dalamud IPC rather than the Penumbra.Api package: no version coupling to a plugin we
// do not control, and "Penumbra is not installed" is just a caught exception.
//
// Resource caching is the trap: once the game has loaded a path its handle is cached, and
// re-pointing the redirect will NOT cause a re-read. Every distinct payload therefore gets
// its own never-before-used synthetic path.
public sealed class PenumbraBridge
{
    // Redirects for forged voiceline clips. Owned by ScdForge.
    public const string ClipsTag = "Warcry.Clips";

    // PenumbraAvailable is reached from NativeVoiceSink.Available, which the router
    // evaluates on every request — inside the action detour — and once per frame from the
    // Settings tab. Penumbra loading or unloading is a once-a-session event, so a
    // two-second-stale answer is good enough to keep a LINQ scan off that path.
    private const double AvailabilityCacheSeconds = 2.0;

    // The V6/V5 inconsistency across Penumbra's IPC surface is real.
    private const string AddAllLabel = "Penumbra.AddTemporaryModAll.V5";
    private const string RemoveAllLabel = "Penumbra.RemoveTemporaryModAll.V5";

    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;

    // Every tag we have registered a redirect under, so teardown clears all of them. Each
    // caller owns its own tag: AddTemporaryModAll replaces a tag's entire set atomically,
    // so two callers sharing one tag silently drop each other's redirects and native audio
    // goes quiet with nothing saying why.
    private readonly HashSet<string> registeredTags = [];

    private bool availabilityCache;
    private long availabilityCheckedAt;

    public PenumbraBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pi = pluginInterface;
        this.log = log;
    }

    public bool PenumbraAvailable
    {
        get
        {
            var now = Stopwatch.GetTimestamp();
            if (this.availabilityCheckedAt != 0 &&
                now - this.availabilityCheckedAt < (long)(AvailabilityCacheSeconds * Stopwatch.Frequency))
            {
                return this.availabilityCache;
            }

            this.availabilityCheckedAt = now;
            this.availabilityCache = this.pi.InstalledPlugins.Any(p =>
                string.Equals(p.InternalName, "Penumbra", StringComparison.OrdinalIgnoreCase) && p.IsLoaded);
            return this.availabilityCache;
        }
    }

    public string LastError { get; private set; } = string.Empty;

    // Points game paths at files on disk under one caller's tag, replacing that tag's
    // previous set atomically. Returns Penumbra's error code, 0 on success, or null if the
    // call could not be made.
    public int? Redirect(string tag, Dictionary<string, string> gamePathToLocalPath)
    {
        if (!this.PenumbraAvailable)
        {
            this.LastError = "Penumbra is not installed or not loaded";
            return null;
        }

        try
        {
            // Penumbra normalises requested game paths to lowercase with forward slashes
            // (Utf8GamePath). Registering a key with capitals — as the real index paths
            // have, e.g. "Vo_Battle/Vo_Battle_PC_mid_Ma_ja.scd" — cannot match the
            // normalised lookup, so the redirect is silently never applied.
            var normalised = new Dictionary<string, string>(gamePathToLocalPath.Count);
            foreach (var (gamePath, localPath) in gamePathToLocalPath)
            {
                normalised[Normalise(gamePath)] = localPath;
            }

            var subscriber = this.pi.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>(AddAllLabel);
            var result = subscriber.InvokeFunc(tag, normalised, string.Empty, 0);
            this.registeredTags.Add(tag);
            this.LastError = result == 0 ? string.Empty : $"Penumbra returned {result}";
            this.log.Information(
                "PenumbraBridge: registered {Count} redirect(s) under {Tag}, result {Result}",
                gamePathToLocalPath.Count,
                tag,
                result);
            return result;
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            this.log.Error(ex, "PenumbraBridge: {Label} failed", AddAllLabel);
            return null;
        }
    }

    private static string Normalise(string path)
        => path.Replace('\\', '/').ToLowerInvariant();

    // Drops one caller's redirect set, leaving every other tag in force.
    public void Clear(string tag)
    {
        if (!this.registeredTags.Contains(tag) || !this.PenumbraAvailable)
        {
            return;
        }

        try
        {
            // Signature per Penumbra.Api: (tag, priority).
            var subscriber = this.pi.GetIpcSubscriber<string, int, int>(RemoveAllLabel);
            subscriber.InvokeFunc(tag, 0);
            this.registeredTags.Remove(tag);
            this.log.Information("PenumbraBridge: cleared redirects under {Tag}", tag);
        }
        catch (Exception ex)
        {
            // Never throw from teardown — a leaked temporary mod is annoying, a crash on
            // unload is much worse.
            this.log.Warning(ex, "PenumbraBridge: {Label} failed during teardown", RemoveAllLabel);
        }
    }

    // Unload only.
    public void ClearAll()
    {
        foreach (var tag in new List<string>(this.registeredTags))
        {
            this.Clear(tag);
        }
    }
}
