using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.DragDrop;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Warcry.Audio;
using Warcry.Clips;
using Warcry.Detection;
using Warcry.Game;
using Warcry.Gating;
using Warcry.Native;
using Warcry.Profiles;
using Warcry.Windows;

namespace Warcry;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/warcry";

    // NOTE: [PluginService] is AttributeTargets.Property. A FIELD will not compile.
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog             Log         { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework   { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider   Interop     { get; private set; } = null!;
    [PluginService] internal static IObjectTable           Objects     { get; private set; } = null!;
    [PluginService] internal static IPlayerState           PlayerState { get; private set; } = null!;
    [PluginService] internal static IClientState           ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition             Condition   { get; private set; } = null!;
    [PluginService] internal static IDataManager           Data        { get; private set; } = null!;
    [PluginService] internal static IGameConfig            GameConfig  { get; private set; } = null!;
    [PluginService] internal static ICommandManager        Commands    { get; private set; } = null!;
    [PluginService] internal static ITextureProvider       Textures    { get; private set; } = null!;
    [PluginService] internal static IDragDropManager       DragDrop    { get; private set; } = null!;

    public Configuration Config { get; }

    public VoiceSlotTable VoiceSlots { get; }

    public ClipLibrary Clips { get; }

    public ProfileStore Profiles { get; }

    public ClipResolver Resolver { get; }

    public Diagnostics Diag { get; } = new();

    public ActionWatcher Watcher { get; }

    public GameVolume Volume { get; } = new();

    public Gates Gates { get; }

    public Throttle Throttle { get; }

    public IVoiceSink Sink { get; }

    public PlaybackScheduler Scheduler { get; }

    /// <summary>Native-audio spike, day 1. Read-only observer, disabled by default.</summary>
    public SoundManagerWatcher SoundWatcher { get; }

    public PenumbraBridge Penumbra { get; }

    public NativeSpike Spike { get; }

    /// <summary>Visual check that our Penumbra integration has any effect at all.</summary>
    public PenumbraProbe Probe { get; }

    /// <summary>
    /// Stopwatch timestamp of the local player's last ActionEffect. Used to measure how
    /// long after snapshot the game plays its own battle grunt.
    /// </summary>
    public long LastLocalCastTicks { get; private set; }

    /// <summary>Action id of that last local cast, so grunt delays can be attributed per action.</summary>
    public uint LastLocalCastActionId { get; private set; }

    /// <summary>
    /// The local player's world position, refreshed once per frame.
    /// </summary>
    /// <remarks>
    /// <c>SoundManagerWatcher</c> needs this from inside the <c>PlaySound</c> detour, to
    /// compare against the emitter position the game passes — the check that establishes
    /// whether the engine wants world or listener-relative coordinates. The object table
    /// must not be touched off the main thread, so the value is cached here instead. A torn
    /// read is possible and harmless: this is diagnostic, not gameplay.
    /// </remarks>
    public System.Numerics.Vector3 CachedPlayerPosition { get; private set; }

    /// <summary>
    /// Every action id seen firing from the local player. The Action sheet is full of
    /// duplicates and unused rows, so this is the only reliable answer to "which id does
    /// this button actually use".
    /// </summary>
    public HashSet<uint> ObservedActions { get; } = [];

    private bool observedDirty;

    private readonly WindowSystem windows = new("Warcry");
    private readonly MainWindow mainWindow;
    private double elapsed;

    public Plugin()
    {
        this.Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        this.VoiceSlots = VoiceSlotTable.Build(Data, Log);
        this.Volume.Refresh();

        this.Clips = new ClipLibrary(PluginInterface.GetPluginConfigDirectory(), Log);
        _ = this.Clips.LoadAsync(); // decode off-thread; never block the ctor

        this.Profiles = new ProfileStore(PluginInterface.GetPluginConfigDirectory(), Log);
        this.Resolver = new ClipResolver(this.Profiles, Data);
        this.Gates = new Gates(this.Config);
        this.Throttle = new Throttle(this.Config);

        this.Penumbra = new PenumbraBridge(PluginInterface, Log);
        this.Spike = new NativeSpike(Data, Log, this.Penumbra, PluginInterface.GetPluginConfigDirectory());
        this.Probe = new PenumbraProbe(Data, Textures, this.Penumbra, Log, PluginInterface.GetPluginConfigDirectory());

        foreach (var id in this.Config.ObservedActionIds)
        {
            this.ObservedActions.Add(id);
        }

        this.Sink = new ManagedVoiceSink(Log, this.Volume, this.Config);
        this.Scheduler = new PlaybackScheduler(this.Sink);

        // Installs the hook. Failure is logged and left inert — never thrown.
        this.Watcher = new ActionWatcher(Interop, Log, this.VoiceSlots, () => PlayerState.EntityId, this.OnCast);
        this.SoundWatcher = new SoundManagerWatcher(
            Interop,
            Log,
            () => (this.LastLocalCastTicks, this.LastLocalCastActionId),
            () => this.CachedPlayerPosition);

        this.mainWindow = new MainWindow(this);
        this.windows.AddWindow(this.mainWindow);

        Framework.Update += this.OnFrameworkUpdate;
        ClientState.TerritoryChanged += this.OnTerritoryChanged;

        PluginInterface.UiBuilder.Draw         += this.windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi   += this.ToggleMainUi;

        Commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open Warcry. /warcry test fires a test tone.",
        });

        Log.Information(
            "Warcry {Version} loaded. Hook: {Installed}. Sink: {Sink}.",
            PluginInterface.Manifest.AssemblyVersion,
            this.Watcher.Installed,
            this.Sink.Status);
    }

    /// <summary>
    /// Called on the game main thread from inside the ActionEffect detour.
    /// The audience filter, resolver and throttle land in M4-M6; M3 plays a test tone.
    /// </summary>
    private void OnCast(in CastEvent ev, DropStage drop, string casterName)
    {
        if (!this.Config.Enabled)
        {
            return;
        }

        this.Diag.Record(new DiagRow(DateTime.Now, in ev, casterName, drop));

        if (ev.IsLocalPlayer && drop == DropStage.None)
        {
            // Reference point for measuring the game's own battle grunt latency.
            this.LastLocalCastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            this.LastLocalCastActionId = ev.ActionId;

            if (ev.ActionId != 0 && this.ObservedActions.Add(ev.ActionId))
            {
                this.observedDirty = true;
            }
        }

        if (drop != DropStage.None || !this.Config.PlayTestToneOnActions)
        {
            return;
        }

        // v1 is self-only. The audience filter seam replaces this at M9.
        if (!ev.IsLocalPlayer)
        {
            return;
        }

        // Nothing should play during a cutscene, a loading screen, or wherever the user
        // has said not to.
        if (this.Gates.IsSuppressed())
        {
            this.Diag.Drop(DropStage.Gate);
            return;
        }

        var actionKey = this.Resolver.GetActionKey(ev.ActionId);

        if (!this.Throttle.Admit(in ev, in actionKey, out var throttleStage))
        {
            this.Diag.Drop(throttleStage);
            return;
        }

        // Which clip, for this caster, for this action.
        var resolved = this.Resolver.Resolve(in ev.Caster, in actionKey);

        NAudio.Wave.ISampleProvider source;
        var gain = 1f;

        if (resolved is { } r && this.Clips.TryGet(r.Clip.Hash, out var cached))
        {
            source = cached.CreateProvider(r.Rate, r.Rule.PitchMode, r.Rule.PitchFftSize);
            gain = r.CombinedGain;
        }
        else if (this.Config.FallBackToTestTone)
        {
            // Nothing mapped for this action. Audible feedback while setting mappings up.
            source = new TestTone();
        }
        else
        {
            this.Diag.Drop(DropStage.NoClip);
            return;
        }

        var request = new VoiceRequest(
            source: source,
            position: ev.Position,
            soundCategory: ev.SoundCategory,
            gain: gain,
            casterEntityId: ev.CasterEntityId);

        // The measurement gates itself: an instant has no cast bar, so CastRemaining is 0
        // and this dispatches immediately. Only real casts get held back, by exactly the
        // amount their own bar has left to run.
        var delay = this.Config.WaitForCastToFinish ? ev.CastRemaining : 0f;
        if (this.Scheduler.Schedule(in request, delay))
        {
            // Stamped on success only, so an unmapped action does not eat the window.
            this.Throttle.Mark(ev.CasterEntityId);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.elapsed += framework.UpdateDelta.TotalSeconds;
        this.CachedPlayerPosition = Objects.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero;
        this.Volume.Update(this.elapsed);
        this.Scheduler.Update();
        this.Sink.Update();
        this.Spike.Update();
    }

    // NOTE: Action<uint> at API 15 — this was Action<ushort> in older Dalamud.
    private void OnTerritoryChanged(uint territory)
    {
        // A voiceline arriving after a loading screen is worse than none at all.
        this.Scheduler.CancelAll();
        this.Throttle.Clear();
    }

    private void ToggleMainUi() => this.mainWindow.IsOpen = !this.mainWindow.IsOpen;

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();

        if (trimmed.Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            this.PlayTestTone();
            return;
        }

        if (trimmed.StartsWith("dumpscd ", StringComparison.OrdinalIgnoreCase))
        {
            this.DumpGameFile(trimmed[8..].Trim());
            return;
        }

        if (trimmed.StartsWith("scdinfo", StringComparison.OrdinalIgnoreCase))
        {
            // Blank argument inspects the default template.
            this.Spike.Inspect(trimmed.Length > 7 ? trimmed[7..].Trim() : null);
            this.mainWindow.IsOpen = true;
            return;
        }

        this.ToggleMainUi();
    }

    /// <summary>
    /// Extracts a real game file to the config directory so its byte layout can be
    /// studied. The .scd writer is built against a genuine battle-voice file as a
    /// structural template rather than against notes about the format.
    /// </summary>
    public void DumpGameFile(string gamePath)
    {
        try
        {
            var file = Data.GetFile(gamePath);
            if (file is null)
            {
                Log.Warning("dumpscd: no such game file: {Path}", gamePath);
                return;
            }

            var dir = System.IO.Path.Combine(PluginInterface.GetPluginConfigDirectory(), "dump");
            System.IO.Directory.CreateDirectory(dir);

            var name = gamePath.Replace('/', '_').Replace('\\', '_');
            var dest = System.IO.Path.Combine(dir, name);
            System.IO.File.WriteAllBytes(dest, file.Data);

            Log.Information("dumpscd: wrote {Bytes} bytes to {Dest}", file.Data.Length, dest);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "dumpscd failed for {Path}", gamePath);
        }
    }

    /// <summary>Fires a tone at your own position, through the full gain chain.</summary>
    public void PlayTestTone()
    {
        var lp = Objects.LocalPlayer;
        var request = new VoiceRequest(
            source: new TestTone(),
            position: lp?.Position ?? System.Numerics.Vector3.Zero,
            soundCategory: 0, // Player
            gain: 1f,
            casterEntityId: PlayerState.EntityId);

        if (!this.Scheduler.Schedule(in request, 0f))
        {
            Log.Warning("Test tone refused — sink unavailable, muted, or at the concurrency cap.");
        }
    }

    /// <summary>
    /// Every action id that currently resolves to <paramref name="actionId"/>, so a
    /// mapping survives level sync and variant content.
    /// </summary>
    /// <remarks>
    /// <c>GetAdjustedActionId</c> answers "what does this action become right now",
    /// accounting for level, sync, traits and transformations. There is no reverse
    /// lookup, so the family is found by scanning player actions and asking each one.
    /// Level-dependent, hence captured at assign time and stored rather than recomputed.
    /// </remarks>
    public unsafe HashSet<uint> BuildActionFamily(uint actionId)
    {
        var family = new HashSet<uint> { actionId };

        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
            if (manager == null)
            {
                return family;
            }

            foreach (var row in Data.GetExcelSheet<Lumina.Excel.Sheets.Action>())
            {
                if (row.RowId == 0 || !row.IsPlayerAction)
                {
                    continue;
                }

                if (manager->GetAdjustedActionId(row.RowId) == actionId)
                {
                    family.Add(row.RowId);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BuildActionFamily failed for {ActionId}; falling back to the bare id", actionId);
        }

        return family;
    }

    /// <summary>Language-independent name, used as a stable identity for mappings.</summary>
    public static string EnglishActionName(uint actionId)
    {
        var sheet = Data.GetExcelSheet<Lumina.Excel.Sheets.Action>(Dalamud.Game.ClientLanguage.English);
        return sheet.TryGetRow(actionId, out var row) ? row.Name.ExtractText() : string.Empty;
    }

    /// <summary>Auditions one clip at your own position, through the full gain chain.</summary>
    public void PlayClip(CachedClip clip, float rate = 1f, PitchMode mode = PitchMode.Varispeed, int fftSize = 2048)
    {
        var lp = Objects.LocalPlayer;
        var request = new VoiceRequest(
            source: clip.CreateProvider(rate, mode, fftSize),
            position: lp?.Position ?? System.Numerics.Vector3.Zero,
            soundCategory: 0,
            gain: 1f,
            casterEntityId: PlayerState.EntityId);

        if (!this.Scheduler.Schedule(in request, 0f))
        {
            Log.Warning("Audition refused — sink unavailable, muted, or at the concurrency cap.");
        }
    }

    public void Dispose()
    {
        // Order matters: stop producing events before tearing down consumers.
        this.Watcher.Dispose();
        this.SoundWatcher.Dispose();

        Framework.Update -= this.OnFrameworkUpdate;
        ClientState.TerritoryChanged -= this.OnTerritoryChanged;

        this.Scheduler.CancelAll();
        this.Sink.Dispose();
        this.Clips.Dispose();
        this.Penumbra.Clear();

        Commands.RemoveHandler(CommandName);

        PluginInterface.UiBuilder.Draw         -= this.windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi   -= this.ToggleMainUi;

        this.windows.RemoveAllWindows();
        this.mainWindow.Dispose();

        // Persisted here rather than per-cast: SavePluginConfig is synchronous and
        // writing it from the detour would stutter the game.
        if (this.observedDirty)
        {
            this.Config.ObservedActionIds = [.. this.ObservedActions];
        }

        PluginInterface.SavePluginConfig(this.Config);
    }
}
