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

    /// <summary>The sink actually installed, typed so the UI can report native-vs-managed.</summary>
    public CompositeVoiceSink Composite { get; }

    /// <summary>Encodes clips into game-loadable <c>.scd</c> and owns their redirects.</summary>
    public ScdForge Forge { get; }

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
    private double nextObservedFlush = double.PositiveInfinity;

    /// <summary>
    /// How long after learning a new action to persist the list.
    /// </summary>
    /// <remarks>
    /// Observed actions used to be written only in <see cref="Dispose"/>, so a game crash
    /// lost everything learned that session — and any mid-session <c>Config.Save()</c> from
    /// the settings UI wrote the stale list back over it. Debounced rather than immediate
    /// because <c>SavePluginConfig</c> is synchronous, and because new actions arrive in
    /// bursts when you first play a job.
    /// </remarks>
    private const double ObservedFlushSeconds = 10.0;

    private readonly WindowSystem windows = new("Warcry");
    private readonly MainWindow mainWindow;
    private double elapsed;

    public Plugin()
    {
        this.Config = Configuration.LoadOrCreate(PluginInterface, Log);

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

        this.Forge = new ScdForge(Data, Log, this.Penumbra, PluginInterface.GetPluginConfigDirectory());
        this.Composite = new CompositeVoiceSink(
            Log,
            this.Config,
            new NativeVoiceSink(Log, this.Config, this.Forge),
            new ManagedVoiceSink(Log, this.Volume, this.Config));

        this.Sink = this.Composite;
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
                this.nextObservedFlush = this.elapsed + ObservedFlushSeconds;
            }
        }

        if (drop != DropStage.None)
        {
            return;
        }

        if (!this.Config.PlayTestToneOnActions)
        {
            // Counted rather than returned silently. This is the commonest cause of
            // "I hear nothing", and it used to leave no trace anywhere in the UI.
            this.Diag.Drop(DropStage.PlaybackOff);
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

        Func<NAudio.Wave.ISampleProvider> createSource;
        string variantKey;
        var gain = 1f;

        if (resolved is { } r && this.Clips.TryGet(r.Clip.Hash, out var cached))
        {
            var rate = r.Rate;
            var mode = r.Rule.PitchMode;
            var fft = r.Rule.PitchFftSize;

            createSource = () => cached.CreateProvider(rate, mode, fft);
            variantKey = VariantKey(r.Clip.Hash, rate, mode, fft);
            gain = r.CombinedGain;
        }
        else if (this.Config.FallBackToTestTone)
        {
            // Nothing mapped for this action. Audible feedback while setting mappings up.
            createSource = () => new TestTone();
            variantKey = TestToneKey;
        }
        else
        {
            this.Diag.Drop(DropStage.NoClip);
            return;
        }

        var request = new VoiceRequest(
            createSource: createSource,
            variantKey: variantKey,
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
        else
        {
            // Every sink refused: at the concurrency cap, muted by the game's own sliders,
            // or no output device. Previously invisible — the row read "ok" and nothing
            // came out of the speakers.
            this.Diag.Drop(DropStage.SinkFull);
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

        if (this.observedDirty && this.elapsed >= this.nextObservedFlush)
        {
            this.FlushObservedActions();
        }
    }

    /// <summary>Persists the learned action list. Cheap no-op when nothing has changed.</summary>
    private void FlushObservedActions()
    {
        if (!this.observedDirty)
        {
            return;
        }

        this.Config.ObservedActionIds = [.. this.ObservedActions];
        this.Config.Save();
        this.observedDirty = false;
        this.nextObservedFlush = double.PositiveInfinity;
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

    /// <summary>
    /// The first reason nothing would be heard right now, or empty if nothing is blocking.
    /// </summary>
    /// <remarks>
    /// <para>"I don't hear the sounds I mapped" has about eight distinct causes, most of
    /// them switches the user set themselves — including the spike tab's own "silence the
    /// plugin" button. Working through them by hand means knowing which of eight places to
    /// look, so this checks them in the order the pipeline does and names the first one
    /// that would stop a line.</para>
    /// <para>Ordered deliberately: the checks a user can fix come before the ones they
    /// cannot.</para>
    /// </remarks>
    public string ExplainSilence()
    {
        if (!this.Config.Enabled)
        {
            return "\"Warcry enabled\" is off, on the Settings tab. Nothing is detected or played.";
        }

        if (!this.Config.PlayTestToneOnActions)
        {
            return "\"Play clips on my actions\" is off, on the Settings tab. Actions are still " +
                   "detected — the Events tab keeps filling — but nothing is played. The spike " +
                   "tab's \"silence the plugin for this test\" button turns this off.";
        }

        if (!this.Watcher.Installed)
        {
            return "The action hook did not install, so no action is ever detected. Expected " +
                   "after a game patch; wait for a FFXIVClientStructs update.";
        }

        if (!this.Sink.Available)
        {
            return $"No audio sink is available: {this.Sink.Status}";
        }

        var gameGain = this.Volume.GainFor(0, this.Config.UseVoiceSliderNotSe);
        if (gameGain <= 0.0001f)
        {
            var bus = this.Config.UseVoiceSliderNotSe ? "Voice" : "Sound Effects";
            return $"The game's own volume settings multiply out to zero (Master {this.Volume.Master}, " +
                   $"{bus} {(this.Config.UseVoiceSliderNotSe ? this.Volume.Voice : this.Volume.Se)}, " +
                   $"Player {this.Volume.Player}) — or one of them is muted.";
        }

        if (this.Config.MasterGain <= 0.0001f)
        {
            return "Plugin volume is at zero, on the Settings tab.";
        }

        if (this.Gates.IsSuppressed())
        {
            return $"Playback is gated right now: {this.Gates.Reason}. That is a live condition, " +
                   "not a setting — it will clear on its own.";
        }

        if (this.Clips.Count == 0)
        {
            return "No clips have been imported. Use the Clips tab.";
        }

        var rules = 0;
        foreach (var profile in this.Profiles.Profiles)
        {
            if (profile.Enabled)
            {
                foreach (var rule in profile.Rules)
                {
                    if (rule.Enabled && rule.Clips.Count > 0)
                    {
                        rules++;
                    }
                }
            }
        }

        if (rules == 0)
        {
            return "No enabled mapping has a clip attached. Use the Mappings tab.";
        }

        // Nothing is blocking as a matter of configuration, so point at the counters, which
        // record what actually happened to recent events.
        var noClip = this.Diag.DropCount(DropStage.NoClip);
        var throttled = this.Diag.DropCount(DropStage.Throttle);
        var sinkFull = this.Diag.DropCount(DropStage.SinkFull);

        if (noClip > 0 && noClip >= throttled && noClip >= sinkFull)
        {
            return $"Nothing is blocking playback, but {noClip} event(s) resolved to no clip — " +
                   "the actions you are using are not the ones your mappings cover. The Events " +
                   "tab shows the ActionId that actually fired.";
        }

        if (throttled > 0 && throttled >= sinkFull)
        {
            return $"Nothing is blocking playback, but {throttled} event(s) were throttled — " +
                   "cooldown, auto-attack skip, casts-only, or a muted action.";
        }

        if (sinkFull > 0)
        {
            return $"Nothing is blocking playback, but {sinkFull} event(s) were refused by the " +
                   "sink — usually the \"max at once\" cap.";
        }

        return string.Empty;
    }

    /// <summary>The synthesised tone is deterministic, so it caches like any other clip.</summary>
    private const string TestToneKey = "warcry:testtone:v1";

    /// <summary>
    /// Identity of one exact rendering of a clip.
    /// </summary>
    /// <remarks>
    /// Every parameter that changes a sample has to be in here. The native sink
    /// content-addresses encoded files by this key and will serve a later request the
    /// earlier one's bytes, so a missing parameter means the wrong audio plays — quietly,
    /// and only for mappings that differ solely by the parameter that was left out.
    /// </remarks>
    private static string VariantKey(string hash, float rate, PitchMode mode, int fftSize)
        => $"{hash}:{rate:0.0000}:{(byte)mode}:{fftSize}";

    /// <summary>Fires a tone at your own position, through the full gain chain.</summary>
    public void PlayTestTone()
    {
        var lp = Objects.LocalPlayer;
        var request = new VoiceRequest(
            createSource: () => new TestTone(),
            variantKey: TestToneKey,
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
            createSource: () => clip.CreateProvider(rate, mode, fftSize),
            variantKey: VariantKey(clip.Info.Hash, rate, mode, fftSize),
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

        // Catch anything learned inside the last debounce window. Never from the detour
        // itself: SavePluginConfig is synchronous and would stutter the game.
        this.FlushObservedActions();
    }
}
