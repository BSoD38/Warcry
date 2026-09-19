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

    // [PluginService] is AttributeTargets.Property. A FIELD will not compile.
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
    [PluginService] internal static IDragDropManager       DragDrop    { get; private set; } = null!;
    [PluginService] internal static ITargetManager         Targets     { get; private set; } = null!;

    public Configuration Config { get; }

    public VoiceSlotTable VoiceSlots { get; }

    public ClipLibrary Clips { get; }

    public ProfileStore Profiles { get; }

    public ClipResolver Resolver { get; }

    public Diagnostics Diag { get; } = new();

    public ActionWatcher Watcher { get; }

    public GameVolume Volume { get; } = new();

    public Gates Gates { get; }

    // Whose actions are heard. The caster seam — see docs/PLAN.md 5.3.
    public AudienceFilter Audience { get; }

    // Once-a-second read of who is nearby and what they are playing.
    public CrowdWatch Crowd { get; }

    public Throttle Throttle { get; }

    // The sink everything routes through; it owns the native and managed leaves.
    public CompositeVoiceSink Composite { get; }

    // Encodes clips into game-loadable .scd and owns their redirects.
    public ScdForge Forge { get; }

    // Job-membership answers, shared by the editor's filter and the warm scoping.
    public JobIndex Jobs { get; }

    // Compiles every mapping ahead of time and keeps the active jobs' set warm.
    public PackBuilder Packs { get; }

    public PlaybackScheduler Scheduler { get; }

    // Keeps the game's own battle grunt from doubling up with ours.
    public GruntSuppressor Grunts { get; }

    public PenumbraBridge Penumbra { get; }

    // So "Use last action" can fill the editor.
    public uint LastLocalCastActionId { get; private set; }

    // Every action id seen firing from the local player. The Action sheet is full of
    // duplicates and unused rows, so this is the only reliable answer to "which id does this
    // button actually use".
    public HashSet<uint> ObservedActions { get; } = [];

    private bool observedDirty;
    private double nextObservedFlush = double.PositiveInfinity;

    // How long after learning a new action to persist the list. Debounced rather than
    // immediate because SavePluginConfig is synchronous and new actions arrive in bursts
    // when you first play a job; persisted rather than left to Dispose because a crash
    // would lose the session, and a mid-session Config.Save() would write the stale list
    // back over it.
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
        this.Audience = new AudienceFilter(this.Config);
        this.Crowd = new CrowdWatch(this.Config, Objects, this.Audience);
        this.Throttle = new Throttle(this.Config, this.Audience, this.Crowd);

        this.Penumbra = new PenumbraBridge(PluginInterface, Log);

        foreach (var id in this.Config.ObservedActionIds)
        {
            this.ObservedActions.Add(id);
        }

        // Shared: the sink follows a sounding voice with it, the suppressor keeps its armed
        // windows current with it. One object-table seam, not two.
        var locator = new CasterLocator(Objects);

        this.Forge = new ScdForge(Data, Log, this.Penumbra, PluginInterface.GetPluginConfigDirectory());
        this.Composite = new CompositeVoiceSink(
            Log,
            this.Config,
            new NativeVoiceSink(Log, this.Config, this.Forge, locator),
            new ManagedVoiceSink(Log, this.Volume, this.Config));

        this.Jobs = new JobIndex(Data);
        this.Packs = new PackBuilder(
            Log, this.Config, this.Profiles, this.Clips, this.Forge, this.Jobs, this.Composite.Native);

        this.Scheduler = new PlaybackScheduler(
            this.Composite,
            () => this.Diag.Drop(DropStage.SinkRefused));

        // Installed disabled: PlaySound is far too hot to sit in while the feature is off.
        // Update() is what turns it on and off from the config.
        this.Grunts = new GruntSuppressor(Interop, Log, this.Config, locator);

        // Installs the hook. Failure is logged and left inert — never thrown.
        this.Watcher = new ActionWatcher(Interop, Log, this.VoiceSlots, () => PlayerState.EntityId, this.OnCast);

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
            this.Composite.Status);
    }

    // Called on the game main thread from inside the ActionEffect detour: gates, throttles,
    // resolves a clip and schedules it.
    private void OnCast(in CastEvent ev, DropStage drop, string casterName)
    {
        if (!this.Config.Enabled)
        {
            return;
        }

        // Classified before the row is recorded, so a cast that goes no further still says
        // who it was from and why it was not heard.
        var verdict = drop == DropStage.None
            ? this.Audience.Classify(in ev.Facts)
            : AudienceVerdict.Unclassified;

        this.Diag.Record(new DiagRow(
            DateTime.Now, in ev, casterName, drop, verdict.Primary, verdict.Refusal));

        if (ev.IsLocalPlayer && drop == DropStage.None)
        {
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
            // Counted rather than returned silently: this is the commonest cause of "I hear
            // nothing".
            this.Diag.Drop(DropStage.PlaybackOff);
            return;
        }

        // The caster seam. Whose actions are heard is decided here and nowhere else, so a
        // refusal is a counted drop with a reason rather than an unexplained silence.
        if (!verdict.Admitted)
        {
            this.Diag.Drop(DropStage.Audience);
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

        if (!this.Throttle.Admit(in ev, in actionKey, verdict.Primary, out var throttleStage))
        {
            this.Diag.Drop(throttleStage);
            return;
        }

        // Which clip, for this caster, for this action. The membership set goes in rather
        // than the primary bucket: a profile aimed at your party has to fire for a party
        // member who is also on your friend list.
        var who = new CasterIdentity(
            ev.Caster, verdict.Membership, ev.Facts.NameHash, ev.Facts.HomeWorld);
        var resolved = this.Resolver.Resolve(in who, in actionKey);

        Func<float, NAudio.Wave.ISampleProvider> createSource;
        string variantKey;
        var speed = 1f;
        var gain = 1f;

        if (resolved is { } r && this.Clips.TryGet(r.Clip.Hash, out var cached))
        {
            var mode = r.Rule.PitchMode;
            var fft = r.Rule.PitchFftSize;

            if (mode == PitchMode.Varispeed)
            {
                // One stable base variant per clip; the per-cast pitch roll rides on the
                // request's Speed — the engine's own speed argument natively, baked at
                // play time by the managed sink. Random pitch costs no extra encodes.
                createSource = extra => cached.CreateProvider(extra, mode, fft);
                variantKey = VariantKey(r.Clip.Hash, 1f, mode, fft);
                speed = r.Rate;
            }
            else
            {
                // Baked pitch. Snapped to half-semitone steps so a random spread stays a
                // finite, pre-compilable set of variants rather than one per roll.
                var rate = CachedClip.QuantiseRate(r.Rate);
                createSource = extra => cached.CreateProvider(rate * extra, mode, fft);
                variantKey = VariantKey(r.Clip.Hash, rate, mode, fft);
            }

            gain = r.CombinedGain;
        }
        else if (this.Config.FallBackToTestTone)
        {
            // Nothing mapped for this action. Audible feedback while setting mappings up.
            createSource = _ => new TestTone();
            variantKey = TestToneKey;
        }
        else
        {
            this.Diag.Drop(DropStage.NoClip);
            return;
        }

        // Applied here rather than inside the resolver so it lands on the fallback tone
        // too: "other people are too loud" has to be fixable in one place whether or not
        // the action they used happens to be mapped.
        gain *= this.Audience.GainFor(verdict.Primary);

        var request = new VoiceRequest(
            createSource: createSource,
            variantKey: variantKey,
            speed: speed,
            position: ev.Position,
            soundCategory: ev.SoundCategory,
            gain: gain,
            casterEntityId: ev.CasterEntityId,
            isSelf: ev.IsLocalPlayer);

        // The measurement gates itself: an instant has no cast bar, so CastRemaining is 0
        // and this dispatches immediately. Only real casts get held back, by exactly the
        // amount their own bar has left to run.
        var delay = this.Config.WaitForCastToFinish ? ev.CastRemaining : 0f;
        if (this.Scheduler.Schedule(in request, delay, out var refusal))
        {
            // Stamped on success only, so an unmapped action does not eat the window —
            // nor, for anyone but you, a token from the global rate cap.
            this.Throttle.Mark(ev.CasterEntityId, verdict.Primary);

            // Armed at snapshot rather than at playback: the game's grunt is
            // animation-driven, 5-1071 ms after this point, so it can land well before our
            // own line finishes waiting out the cast bar. Nothing is armed for a cast we are
            // not voicing.
            this.Grunts.Arm(ev.CasterEntityId, ev.Position);
        }
        else
        {
            // SinkRefused: every sink said no — at the concurrency cap, muted by the game's
            // own sliders, or no output device. TooFarOut: no sink was asked at all, because
            // the cast bar ran past the scheduler's ceiling.
            this.Diag.Drop(refusal == ScheduleRefusal.TooFarOut
                ? DropStage.TooFarOut
                : DropStage.SinkRefused);
        }
    }

    // Refreshed once per frame beside the position.
    public uint CachedJobId { get; private set; }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.elapsed += framework.UpdateDelta.TotalSeconds;

        // One object-table read per frame, for the pack builder's warm scoping. Never
        // read from the detour; never read twice.
        this.CachedJobId = Objects.LocalPlayer?.ClassJob.RowId ?? 0u;

        this.Volume.Update(this.elapsed);
        this.Scheduler.Update();

        // Before the builder: the crowd scan is what tells it which jobs are reachable now,
        // and it self-throttles to 1 Hz (and to nothing at all while you are the only
        // audience).
        this.Crowd.Update(this.elapsed, this.CachedJobId);

        // Before the sink pumps: the builder's job answer is what the sink's warm gate
        // consults for anything registered this frame.
        this.Packs.Update(this.CachedJobId, this.Crowd.Jobs, this.Crowd.Revision);

        this.Composite.Update();

        // Follows the config toggles, keeps armed positions current and retires expired
        // windows. The detour itself never reads the object table.
        this.Grunts.Update();

        if (this.observedDirty && this.elapsed >= this.nextObservedFlush)
        {
            this.FlushObservedActions();
        }
    }

    // No-op when nothing has changed.
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

    // Action<uint> at API 15 — Action<ushort> in older Dalamud.
    private void OnTerritoryChanged(uint territory)
    {
        // A voiceline arriving after a loading screen is worse than none at all — and a
        // voice still sounding through one is holding a slot in the game's shared pool.
        this.Scheduler.CancelAll();
        this.Composite.StopAll();
        this.Throttle.Clear();

        // Entity ids are reassigned across a zone change, so a surviving window would
        // silence a stranger who happens to inherit one.
        this.Grunts.Clear();

        // Everyone who was nearby is gone, so the crowd count and the job set it implies
        // are both stale. Left alone, a hub city's scaling would follow you into a dungeon.
        this.Crowd.Reset();
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

        this.ToggleMainUi();
    }

    // The first reason nothing would be heard right now, or empty if nothing is blocking.
    // "I don't hear the sounds I mapped" has about eight distinct causes, most of them
    // switches the user set themselves, so this checks them in the order the pipeline does
    // and names the first one that would stop a line. The checks a user can fix come first.
    public string ExplainSilence()
    {
        if (!this.Config.Enabled)
        {
            return "\"Warcry enabled\" is off. Nothing is detected or played.";
        }

        if (!this.Config.PlayTestToneOnActions)
        {
            return "\"Play voicelines\" is off. Actions are still detected, and the Events tab " +
                   "keeps filling, but nothing is played.";
        }

        if (this.Config.Audience == Game.AudienceBucket.None)
        {
            return "Nobody is selected on the People tab — not even you — so every action is " +
                   "detected and then discarded.";
        }

        if (!this.Watcher.Installed)
        {
            return "The action hook did not install, so no action is ever detected. Expected " +
                   "after a game patch. Wait for a FFXIVClientStructs update.";
        }

        if (!this.Composite.Available)
        {
            return $"No audio sink is available: {this.Composite.Status}";
        }

        var gameGain = this.Volume.GainFor(0, this.Config.UseVoiceSliderNotSe);
        if (gameGain <= 0.0001f)
        {
            var bus = this.Config.UseVoiceSliderNotSe ? "Voice" : "Sound Effects";
            return $"The game's own volume settings multiply out to zero (Master {this.Volume.Master}, " +
                   $"{bus} {(this.Config.UseVoiceSliderNotSe ? this.Volume.Voice : this.Volume.Se)}, " +
                   $"Player {this.Volume.Player}), or one of them is muted.";
        }

        if (this.Config.MasterGain <= 0.0001f)
        {
            return "\"Warcry volume\" is at zero.";
        }

        if (this.Gates.IsSuppressed())
        {
            return $"Playback is gated right now: {this.Gates.Reason}. That is a live condition, " +
                   "not a setting, and it will clear on its own.";
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
            return "No enabled mapping has a clip attached. Use the Actions tab.";
        }

        // The one counter-derived answer kept here: it fires when your own actions are off
        // but somebody else's are on, which reads as "nothing works" rather than as a
        // setting. Every other drop counter is listed by the Status tab.
        if ((this.Config.Audience & Game.AudienceBucket.Self) == 0)
        {
            return "\"Me\" is off on the People tab, so your own actions never play. " +
                   $"{this.Diag.DropCount(DropStage.Audience)} action(s) have been skipped for " +
                   "not being from someone you listen to.";
        }

        return string.Empty;
    }

    // The synthesised tone is deterministic, so it caches like any other clip.
    public const string TestToneKey = "warcry:testtone:v1";

    // Identity of one exact rendering of a clip. Every parameter that changes a sample has
    // to be in here: the native sink content-addresses encoded files by this key and will
    // serve a later request the earlier one's bytes, so a missing parameter means the wrong
    // audio plays, quietly, and only for mappings that differ solely by it.
    // Public because PackBuilder must enumerate ahead of time the exact keys this produces
    // at cast time. Invariant culture always — the rate would otherwise format as "1,0000"
    // on a French client, and the builder parses the format back.
    public static string VariantKey(string hash, float rate, PitchMode mode, int fftSize)
    {
        // The FFT size is a phase-vocoder parameter; varispeed never reads it. Left in
        // the key, two rules differing only by FFT would be two "variants" encoding
        // byte-identical files — wasted work, and a race onto the same content-addressed
        // cache file when the pack builder runs both encodes concurrently.
        if (mode == PitchMode.Varispeed)
        {
            fftSize = 0;
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{hash}:{rate:0.0000}:{(byte)mode}:{fftSize}");
    }

    // A tone at your own position, through the full gain chain.
    public void PlayTestTone()
    {
        var lp = Objects.LocalPlayer;
        var request = new VoiceRequest(
            createSource: _ => new TestTone(),
            variantKey: TestToneKey,
            speed: 1f,
            position: lp?.Position ?? System.Numerics.Vector3.Zero,
            soundCategory: 0, // Player
            gain: 1f,
            casterEntityId: PlayerState.EntityId,
            isSelf: true);

        if (!this.Scheduler.Schedule(in request, 0f, out _))
        {
            Log.Warning("Test tone refused — sink unavailable, muted, or at the concurrency cap.");
        }
    }

    // Every action id that currently resolves to actionId, so a mapping survives level sync
    // and variant content. GetAdjustedActionId answers "what does this action become right
    // now" and has no reverse lookup, so the family is found by scanning player actions and
    // asking each one. Level-dependent, hence captured at assign time and stored.
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

    // A stable, language-independent identity for mappings.
    public static string EnglishActionName(uint actionId)
    {
        var sheet = Data.GetExcelSheet<Lumina.Excel.Sheets.Action>(Dalamud.Game.ClientLanguage.English);
        return sheet.TryGetRow(actionId, out var row) ? row.Name.ExtractText() : string.Empty;
    }

    // Auditions one clip at your own position. Straight to the managed sink, never the
    // composite: a preview must work before any compile has happened, must not depend on
    // Penumbra, and must never spend one of the native path's voices. /warcry test is the
    // pipeline test.
    public void PlayClip(CachedClip clip, float rate = 1f, PitchMode mode = PitchMode.Varispeed, int fftSize = 2048)
    {
        var lp = Objects.LocalPlayer;
        var request = new VoiceRequest(
            createSource: extra => clip.CreateProvider(rate * extra, mode, fftSize),
            variantKey: VariantKey(clip.Info.Hash, rate, mode, fftSize),
            speed: 1f,
            position: lp?.Position ?? System.Numerics.Vector3.Zero,
            soundCategory: 0,
            gain: 1f,
            casterEntityId: PlayerState.EntityId,
            isSelf: true);

        if (!this.Composite.Managed.TryPlay(in request))
        {
            Log.Warning("Audition refused — no audio device, muted, or at the concurrency cap.");
        }
    }

    public void Dispose()
    {
        // Order matters: stop producing events before tearing down consumers.
        this.Watcher.Dispose();

        // Unhooked here rather than left to the finaliser: while this is installed the game
        // cannot play a battle grunt without going through us.
        this.Grunts.Dispose();

        Framework.Update -= this.OnFrameworkUpdate;
        ClientState.TerritoryChanged -= this.OnTerritoryChanged;

        this.Scheduler.CancelAll();

        this.Composite.Dispose();
        this.Clips.Dispose();
        this.Penumbra.ClearAll();

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
