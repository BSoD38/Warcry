using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Warcry.Native;

/// <summary>
/// Registers runtime game-path redirects through Penumbra, so the game's resource system
/// can load a file we authored.
/// </summary>
/// <remarks>
/// <para>Deliberately raw Dalamud IPC rather than the <c>Penumbra.Api</c> NuGet package:
/// no new dependency, no version coupling to a plugin we do not control, and "Penumbra is
/// not installed" is simply a caught exception.</para>
/// <para><c>SoundManager.PlaySound</c> takes a game path resolved through the resource
/// system, not a filesystem path — which is the entire reason Penumbra is needed here.</para>
/// <para><b>Resource caching is the trap.</b> Once the game has loaded a given path, its
/// handle is cached and re-pointing the redirect will NOT cause a re-read. So every
/// distinct payload gets its own never-before-used synthetic path.</para>
/// </remarks>
public sealed class PenumbraBridge
{
    /// <summary>Redirects for forged voiceline clips. Owned by <see cref="ScdForge"/>.</summary>
    public const string ClipsTag = "Warcry.Clips";

    /// <summary>
    /// How long a Penumbra-availability answer is reused before asking again.
    /// </summary>
    /// <remarks>
    /// <see cref="PenumbraAvailable"/> is reached from <c>NativeVoiceSink.Available</c>,
    /// which the router evaluates on every request — i.e. from inside the action detour —
    /// and once per frame from the Settings tab. A LINQ scan over the installed-plugin list
    /// has no business running there. Penumbra loading or unloading is a once-a-session
    /// event, so a two-second-stale answer is always good enough.
    /// </remarks>
    private const double AvailabilityCacheSeconds = 2.0;

    // Note the V6/V5 inconsistency across Penumbra's IPC surface — it is real.
    private const string AddAllLabel = "Penumbra.AddTemporaryModAll.V5";
    private const string RemoveAllLabel = "Penumbra.RemoveTemporaryModAll.V5";

    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;

    /// <summary>
    /// Tags we have registered at least one redirect under, so teardown clears all of them.
    /// </summary>
    /// <remarks>
    /// Each caller owns its own tag. <c>AddTemporaryModAll</c> replaces a tag's entire set
    /// atomically, so two callers sharing one tag silently drop each other's redirects —
    /// the engine is then asked for paths Penumbra no longer serves, and native audio goes
    /// quiet with nothing anywhere saying why.
    /// </remarks>
    private readonly HashSet<string> registeredTags = [];

    private bool availabilityCache;
    private long availabilityCheckedAt;

    public PenumbraBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pi = pluginInterface;
        this.log = log;
    }

    /// <summary>
    /// Is Penumbra installed and loaded? Answers from a short-lived cache.
    /// </summary>
    /// <remarks>
    /// The scan over <c>InstalledPlugins</c> is deferred to at most once per
    /// <see cref="AvailabilityCacheSeconds"/> because this property is on the cast path.
    /// </remarks>
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

    /// <summary>
    /// Points one or more game paths at files on disk, under one caller's tag. Repeating
    /// the same tag replaces that tag's previous set atomically — and only that tag's, so
    /// callers cannot clobber each other's redirects.
    /// </summary>
    /// <returns>Penumbra's error code, 0 on success, or null if the call could not be made.</returns>
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

    /// <summary>Drops one caller's redirect set, leaving every other tag in force.</summary>
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

    /// <summary>Drops every redirect this plugin registered, whatever the tag. Unload only.</summary>
    public void ClearAll()
    {
        foreach (var tag in new List<string>(this.registeredTags))
        {
            this.Clear(tag);
        }
    }
}
