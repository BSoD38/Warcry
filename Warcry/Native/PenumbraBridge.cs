using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Warcry.Native;

/// <summary>Result of asking Penumbra whether a redirect is really in force.</summary>
public enum RedirectState
{
    /// <summary>Penumbra could not be asked at all.</summary>
    Unavailable,

    /// <summary>The default collection resolves the game path to our file.</summary>
    Applied,

    /// <summary>Penumbra echoed the game path back — no redirect is in force.</summary>
    NotApplied,

    /// <summary>Some other mod owns this path.</summary>
    OtherTarget,
}

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

    /// <summary>Redirects the native spike registers while experimenting.</summary>
    public const string SpikeTag = "Warcry.Spike";

    /// <summary>Redirects the texture probe registers as a non-audio control.</summary>
    public const string ProbeTag = "Warcry.Probe";

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

    // Unversioned, unlike the temporary-mod calls above. Func&lt;string, string&gt;.
    private const string ResolveDefaultLabel = "Penumbra.ResolveDefaultPath";

    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;

    /// <summary>
    /// Tags we have registered at least one redirect under, so teardown clears all of them.
    /// </summary>
    /// <remarks>
    /// Each caller owns its own tag. <c>AddTemporaryModAll</c> replaces a tag's entire set
    /// atomically, so when the forge, the spike and the probe all shared one tag, running a
    /// spike attempt silently dropped every forged clip redirect while the forge still
    /// reported those clips as registered and warm — the engine was then asked for paths
    /// Penumbra no longer served, and native audio went quiet for the rest of the session
    /// with nothing anywhere saying why.
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
    /// the forge, the spike and the probe cannot clobber each other's redirects.
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

    /// <summary>
    /// Asks Penumbra what the <em>default</em> collection resolves a game path to.
    /// </summary>
    /// <returns>
    /// The resolved path — our local file if a redirect applies, otherwise the game path
    /// echoed back — or null if the call could not be made.
    /// </returns>
    /// <remarks>
    /// The default collection is the one that matters here. A sound played through
    /// <c>SoundManager::PlaySound</c> carries no character context, so Penumbra has no
    /// game object to resolve against; reports of SCD replacement working only from the
    /// Base collection (Penumbra issue #275) are consistent with that. Character-assigned
    /// collections are therefore the wrong thing to test against.
    /// </remarks>
    public string? ResolveDefault(string gamePath)
    {
        if (!this.PenumbraAvailable)
        {
            this.LastError = "Penumbra is not installed or not loaded";
            return null;
        }

        try
        {
            return this.pi.GetIpcSubscriber<string, string>(ResolveDefaultLabel)
                .InvokeFunc(Normalise(gamePath));
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            this.log.Error(ex, "PenumbraBridge: {Label} failed", ResolveDefaultLabel);
            return null;
        }
    }

    /// <summary>
    /// Whether a registered redirect is actually in force, established without playing
    /// anything.
    /// </summary>
    /// <remarks>
    /// This is the check the original spike never made. It separates "Penumbra is not
    /// serving our file" from "the engine is not playing it" in one call, with no audio,
    /// no listening and nothing to misattribute.
    /// </remarks>
    public RedirectState Verify(string gamePath, string expectedLocalPath, out string message)
    {
        var resolved = this.ResolveDefault(gamePath);

        if (resolved is null)
        {
            message = $"could not ask Penumbra: {this.LastError}";
            return RedirectState.Unavailable;
        }

        if (PathsMatch(resolved, expectedLocalPath))
        {
            message = $"APPLIED — the default collection resolves it to our file:\n    {resolved}";
            return RedirectState.Applied;
        }

        if (PathsMatch(resolved, gamePath))
        {
            message = "NOT APPLIED — Penumbra echoed the game path back unchanged, so no\n" +
                      "    redirect is in force for the default collection.";
            return RedirectState.NotApplied;
        }

        message = $"REDIRECTED ELSEWHERE — something else claims this path:\n    {resolved}";
        return RedirectState.OtherTarget;
    }

    private static string Normalise(string path)
        => path.Replace('\\', '/').ToLowerInvariant();

    private static bool PathsMatch(string a, string b)
        => string.Equals(Normalise(a), Normalise(b), StringComparison.Ordinal);

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
