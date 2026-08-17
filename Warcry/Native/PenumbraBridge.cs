using System;
using System.Collections.Generic;
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
    private const string Tag = "Warcry";

    // Note the V6/V5 inconsistency across Penumbra's IPC surface — it is real.
    private const string AddAllLabel = "Penumbra.AddTemporaryModAll.V5";
    private const string RemoveAllLabel = "Penumbra.RemoveTemporaryModAll.V5";

    // Unversioned, unlike the temporary-mod calls above. Func&lt;string, string&gt;.
    private const string ResolveDefaultLabel = "Penumbra.ResolveDefaultPath";

    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;

    private bool registered;

    public PenumbraBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pi = pluginInterface;
        this.log = log;
    }

    /// <summary>Is Penumbra installed and loaded right now?</summary>
    public bool PenumbraAvailable =>
        this.pi.InstalledPlugins.Any(p =>
            string.Equals(p.InternalName, "Penumbra", StringComparison.OrdinalIgnoreCase) && p.IsLoaded);

    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// Points one or more game paths at files on disk. Repeating the same tag replaces
    /// the previous set atomically.
    /// </summary>
    /// <returns>Penumbra's error code, 0 on success, or null if the call could not be made.</returns>
    public int? Redirect(Dictionary<string, string> gamePathToLocalPath)
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
            var result = subscriber.InvokeFunc(Tag, normalised, string.Empty, 0);
            this.registered = true;
            this.LastError = result == 0 ? string.Empty : $"Penumbra returned {result}";
            this.log.Information(
                "PenumbraBridge: registered {Count} redirect(s), result {Result}",
                gamePathToLocalPath.Count,
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

    public void Clear()
    {
        if (!this.registered || !this.PenumbraAvailable)
        {
            return;
        }

        try
        {
            // Signature per Penumbra.Api: (tag, priority).
            var subscriber = this.pi.GetIpcSubscriber<string, int, int>(RemoveAllLabel);
            subscriber.InvokeFunc(Tag, 0);
            this.registered = false;
            this.log.Information("PenumbraBridge: cleared redirects");
        }
        catch (Exception ex)
        {
            // Never throw from teardown — a leaked temporary mod is annoying, a crash on
            // unload is much worse.
            this.log.Warning(ex, "PenumbraBridge: {Label} failed during teardown", RemoveAllLabel);
        }
    }
}
