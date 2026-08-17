using System;
using System.Collections.Generic;
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
    private const string Tag = "Warcry";

    // Note the V6/V5 inconsistency across Penumbra's IPC surface — it is real.
    private const string AddAllLabel = "Penumbra.AddTemporaryModAll.V5";
    private const string RemoveAllLabel = "Penumbra.RemoveTemporaryModAll.V5";

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
                normalised[gamePath.Replace('\\', '/').ToLowerInvariant()] = localPath;
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
