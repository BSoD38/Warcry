using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Warcry.Audio;
using Warcry.Game;

namespace Warcry;

/// <summary>
/// Plugin settings only. Profiles and clip metadata deliberately live in separate
/// System.Text.Json files under GetPluginConfigDirectory() — SavePluginConfig writes
/// through IReliableFileStorage (duplicates every byte into a SQLite backup, hard-fails
/// above 64MB, and is synchronous) and serialises with TypeNameHandling.Objects.
/// See docs/PLAN.md 5.8.
/// </summary>
/// <remarks>
/// The namespace of this type is baked into every user's config file by
/// TypeNameHandling.Objects. Moving or renaming it orphans their settings.
/// It is fixed as <c>Warcry.Configuration</c>.
/// </remarks>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>
    /// The schema version this build writes. Bump it and add a step to
    /// <see cref="Migrate"/> whenever a field changes meaning or goes away.
    /// </summary>
    public const int CurrentVersion = 5;

    /// <summary>Schema version. Bump and add an ordered migration step when fields change.</summary>
    public int Version { get; set; } = CurrentVersion;

    public bool Enabled { get; set; } = true;

    // ---- audio (M3) ----

    /// <summary>Master trim on top of the game's own sliders.</summary>
    public float MasterGain { get; set; } = 1.0f;

    /// <summary>Scale by the game's Voice slider rather than Sound Effects.</summary>
    public bool UseVoiceSliderNotSe { get; set; } = true;

    /// <summary>
    /// Hard cap on simultaneous plugin voices. Not a taste setting: the game's SoundData
    /// pool is 256 entries shared with the entire client and the Voice bus has 5 tracks.
    /// </summary>
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>
    /// Play clips on your own actions — the playback master switch. Off keeps detection
    /// and the Events tab running but plays nothing.
    /// </summary>
    /// <remarks>The name is historical; renaming it would orphan saved configs.</remarks>
    public bool PlayTestToneOnActions { get; set; } = true;

    /// <summary>
    /// Which sink gameplay lines are routed to. See <see cref="SinkMode"/>.
    /// </summary>
    /// <remarks>
    /// <para>Defaults to <see cref="SinkMode.Auto"/>: the engine when Penumbra is present,
    /// NAudio otherwise, so a fresh install is never silent. The earlier ManagedOnly
    /// default existed only because PLAN.md §6 (b)–(e) were unmeasured; all of them (and
    /// the speed argument) passed in game on 2026-08-18 — see docs/native-spike.md.</para>
    /// <para><see cref="SinkMode.NativeOnly"/> and <see cref="SinkMode.Auto"/> require
    /// Penumbra. NativeOnly never substitutes NAudio — a refused line is a counted,
    /// explained drop.</para>
    /// </remarks>
    public SinkMode Sink { get; set; } = SinkMode.Auto;

    /// <summary>
    /// Where a line sounds from, and whether it tracks its caster. See
    /// <see cref="VoicePositionMode"/>.
    /// </summary>
    /// <remarks>
    /// <para>Defaults to <see cref="VoicePositionMode.Follow"/>, confirmed working in game on
    /// 2026-08-24: the engine treats a position handed to <c>PlaySound</c> as fixed for the
    /// whole clip and the listener follows the camera, so without following, any skill that
    /// displaces you leaves your own voiceline behind and it fades out mid-word.</para>
    /// <para>Native sink only. NAudio has no positional model at all, so a line that falls
    /// back to it in <see cref="SinkMode.Auto"/> ignores this.</para>
    /// </remarks>
    public VoicePositionMode VoicePosition { get; set; } = VoicePositionMode.Follow;

    /// <summary>
    /// v1 field, superseded by <see cref="Sink"/>. Kept only so old configs deserialise;
    /// <see cref="Migrate"/> folds it in. Nothing else may read it.
    /// </summary>
    public bool PreferNativeSink { get; set; }

    /// <summary>
    /// Play the synthesised tone when an action has no clip mapped. Useful while setting
    /// mappings up — audible proof the action was detected — and noise once you are done.
    /// </summary>
    public bool FallBackToTestTone { get; set; } = true;

    /// <summary>
    /// Delay playback until the cast bar has actually finished, using the per-event
    /// measured remainder. Instants self-gate (no cast bar, so zero delay).
    /// </summary>
    public bool WaitForCastToFinish { get; set; } = true;

    // ---- the game's own battle grunt (M10) ----

    /// <summary>
    /// How much of the game's own battle grunt to silence.
    /// </summary>
    /// <remarks>
    /// <see cref="GruntMode.Off"/> by default, and no migration step raises it: an update
    /// that changes what the game sounds like without being asked is the same nasty
    /// surprise as one that starts voicing strangers. Damage-taken and death grunts are
    /// never touched in any mode — see <see cref="Audio.GruntSuppressor"/>.
    /// </remarks>
    public GruntMode Grunts { get; set; } = GruntMode.Off;

    /// <summary>
    /// How long after a cast the game's grunt for it stays suppressed.
    /// Only <see cref="GruntMode.WhenVoiced"/> reads it.
    /// </summary>
    /// <remarks>
    /// The grunt is animation-driven and lands 5-1071 ms after snapshot, fixed per action
    /// (docs/native-spike.md), so nothing shorter than about a second covers the slow end.
    /// Exposed rather than fixed because the cost of a long window is eating the *next*
    /// action's grunt on a fast rotation.
    /// </remarks>
    public float GruntWindowSeconds { get; set; } = 1.5f;

    // ---- gates (M6) ----

    public bool DisableInCutscenes { get; set; } = true;

    public bool DisableInPvP { get; set; } = true;

    public bool DisableInGpose { get; set; } = true;

    public bool DisableInQuestEvents { get; set; } = true;

    /// <summary>TerritoryType ids where nothing plays. Useful for hub cities.</summary>
    public HashSet<uint> BlockedTerritories { get; set; } = [];

    // ---- audience (M9) ----

    /// <summary>
    /// Whose actions are heard at all. Anything not in this set is a counted
    /// <see cref="DropStage.Audience"/> drop.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="AudienceBucket.Self"/>, which is what every install before
    /// this feature did. Widening it is always the user's explicit act — a plugin that
    /// starts voicing strangers after an update would be a nasty surprise.
    /// </remarks>
    public AudienceBucket Audience { get; set; } = AudienceBucket.Self;

    /// <summary>
    /// Players listed by hand. Heard when <see cref="AudienceBucket.Named"/> is enabled,
    /// and separately targetable by a profile.
    /// </summary>
    public List<NamedPlayer> NamedPeople { get; set; } = [];

    /// <summary>Players who never play a line, whatever else would have matched.</summary>
    /// <remarks>Checked before every bucket except <see cref="AudienceBucket.Self"/>.</remarks>
    public List<NamedPlayer> BlockedPeople { get; set; } = [];

    /// <summary>
    /// Yalms past which someone else's line is not played. 0 disables the gate.
    /// </summary>
    /// <remarks>
    /// Not the same job as the engine's falloff, which only makes a distant line quiet.
    /// This stops it being requested at all, so it costs no voice from the concurrency cap
    /// and no slot in the game's shared sound pool. Never applied to your own actions.
    /// </remarks>
    public int MaxDistanceYalms { get; set; } = 30;

    /// <summary>Volume trim applied to everyone but you.</summary>
    public float OtherPlayerGain { get; set; } = 0.8f;

    // ---- throttle (M6, per-audience at M9) ----

    /// <summary>Seconds before the same caster can trigger another line. 0 disables.</summary>
    public float SelfCooldownSeconds { get; set; } = 2.0f;

    /// <summary>Per-caster cooldown for someone on your named list.</summary>
    public float NamedCooldownSeconds { get; set; } = 3.0f;

    /// <summary>Per-caster cooldown for party, alliance and friends.</summary>
    public float PartyCooldownSeconds { get; set; } = 4.0f;

    /// <summary>Per-caster cooldown for everyone else.</summary>
    public float OtherCooldownSeconds { get; set; } = 6.0f;

    /// <summary>
    /// Stretch other people's cooldowns as the crowd grows. See docs/PLAN.md 5.7 stage 3.
    /// </summary>
    /// <remarks>
    /// Your own lines are never scaled. The whole point is that a hub city or a 48-player
    /// alliance raid quietens the strangers around you without making you inaudible.
    /// </remarks>
    public bool ScaleWithCrowd { get; set; } = true;

    /// <summary>Nearby audience members above which cooldowns start stretching.</summary>
    public int SoftCrowdLimit { get; set; } = 12;

    /// <summary>Ceiling on the crowd multiplier, so a full raid cannot mute everyone forever.</summary>
    public float MaxCrowdScale { get; set; } = 6.0f;

    /// <summary>
    /// Cap the total rate of other people's lines. See docs/PLAN.md 5.7 stage 4.
    /// </summary>
    /// <remarks>
    /// The per-caster cooldown bounds one person; this bounds the sum of them. Requests are
    /// dropped rather than queued — a voiceline that arrives late is worse than one that
    /// never arrives.
    /// </remarks>
    public bool LimitTotalRate { get; set; } = true;

    /// <summary>How many other people's lines may fire back to back before the rate bites.</summary>
    public int RateBurst { get; set; } = 4;

    /// <summary>Seconds to earn back one line of burst.</summary>
    public float RateRefillSeconds { get; set; } = 1.5f;

    /// <summary>Auto-attacks fire constantly and are never worth a voiceline.</summary>
    public bool SkipAutoAttacks { get; set; } = true;

    /// <summary>Only voice actions that have a cast bar.</summary>
    public bool CastsOnly { get; set; }

    /// <summary>Actions that never play, whatever is mapped to them.</summary>
    public HashSet<uint> MutedActionIds { get; set; } = [];

    /// <summary>
    /// Action ids actually observed firing on this character. The Action sheet contains
    /// duplicates, unused rows and NPC copies, so "which of the five Riposte rows is the
    /// real one" is not answerable from the sheet — but it is answerable from what the
    /// hook has seen. Persisted so the list survives a restart.
    /// </summary>
    public List<uint> ObservedActionIds { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    /// <summary>
    /// Reads the stored config, migrates it, and repairs anything unusable.
    /// </summary>
    /// <remarks>
    /// <para>The plain <c>GetPluginConfig() as Configuration ?? new()</c> this replaces had
    /// two silent failure modes: a config that fails to deserialise resets every setting
    /// with no word to the user, and a collection property that comes back <c>null</c> —
    /// which a hand-edited or truncated file will do, since the initialisers here only
    /// apply to a fresh object — throws a <c>NullReferenceException</c> from inside the
    /// action hook the first time anything reads it.</para>
    /// <para>Writes back only when something actually changed, so a normal start does not
    /// touch the disk. <c>SavePluginConfig</c> is synchronous and writes through
    /// IReliableFileStorage, so it is not free.</para>
    /// </remarks>
    public static Configuration LoadOrCreate(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        Configuration config;

        try
        {
            config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        }
        catch (Exception ex)
        {
            // Better a loud default than a silent one: the user needs to know their
            // settings are gone rather than wonder why the plugin went quiet.
            log.Error(ex, "Warcry: the saved configuration could not be read. Starting from defaults.");
            config = new Configuration();
        }

        var changed = config.Migrate(log);
        changed |= config.Repair(log);

        if (changed)
        {
            config.Save();
        }

        return config;
    }

    /// <summary>
    /// Walks the version ladder. Each step upgrades by exactly one version so a config from
    /// any age arrives intact.
    /// </summary>
    /// <remarks>
    /// There are no steps yet — v1 is the first shipped schema. The ladder exists so the
    /// first field change has an obvious home rather than being bolted on under pressure.
    /// Add steps as <c>if (this.Version &lt; N) { …; this.Version = N; }</c>, in order.
    /// </remarks>
    private bool Migrate(IPluginLog log)
    {
        if (this.Version == CurrentVersion)
        {
            return false;
        }

        if (this.Version > CurrentVersion)
        {
            // A newer build wrote this. Fields we do not know about survive round-tripping
            // only by luck, but clobbering the version would make a downgrade-upgrade cycle
            // skip real migrations later, so leave it and say so.
            log.Warning(
                "Warcry: config version {Stored} is newer than this build's {Current}. " +
                "Leaving it alone; unknown settings may not survive.",
                this.Version,
                CurrentVersion);
            return false;
        }

        var from = this.Version;

        // -- add ordered migration steps here --

        if (this.Version < 2)
        {
            // v1's bool became the SinkMode enum. Anyone who opted into the native sink
            // gets the mode this rework was built for: the engine or an explained drop,
            // never a quiet NAudio substitute. Auto remains available for anyone who
            // preferred the old fallback behaviour.
            this.Sink = this.PreferNativeSink ? SinkMode.NativeOnly : SinkMode.ManagedOnly;
            this.Version = 2;
        }

        // v3 (2026-08-18): NativePitchViaSpeed removed — the engine's speed argument is
        // confirmed working, so varispeed pitch always rides it. No data to transform;
        // the stored bool is simply dropped on load.

        // v4 (2026-08-24): FollowViaDriver removed. The driver-level position push was
        // behind a flag only while it was unproven; it is what makes Follow follow, so it
        // is now unconditional. Same shape as v3 — the stored bool is dropped on load, and
        // anyone who had it off gets working following rather than a silently inert mode.

        if (this.Version < 5)
        {
            // v5 (2026-08-24): the audience filter went live. Everything before it was
            // self-only, and that is what the user consented to, so an upgrade must not
            // start voicing anyone new. The profiles they already have become "everyone"
            // targets — see ProfileMatch.Audience, which defaults that way for exactly
            // this reason — but the global filter stays shut until they open it.
            this.Audience = AudienceBucket.Self;
            this.Version = 5;
        }

        this.Version = CurrentVersion;
        log.Information("Warcry: migrated configuration from version {From} to {To}.", from, this.Version);
        return true;
    }

    /// <summary>
    /// Forces every value back into a range the plugin can actually run with.
    /// </summary>
    /// <remarks>
    /// Guards against a hand-edited file, a partial write, and NaN — which propagates
    /// silently through the gain chain and turns into inaudible output rather than an error.
    /// </remarks>
    private bool Repair(IPluginLog log)
    {
        var repairs = 0;

        this.BlockedTerritories ??= Fix<HashSet<uint>>(nameof(this.BlockedTerritories));
        this.MutedActionIds ??= Fix<HashSet<uint>>(nameof(this.MutedActionIds));
        this.ObservedActionIds ??= Fix<List<uint>>(nameof(this.ObservedActionIds));
        this.NamedPeople ??= Fix<List<NamedPlayer>>(nameof(this.NamedPeople));
        this.BlockedPeople ??= Fix<List<NamedPlayer>>(nameof(this.BlockedPeople));

        // A null entry inside the list is a different failure from a null list, and it
        // would throw from inside the filter's rebuild rather than on load.
        repairs += this.NamedPeople.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Name));
        repairs += this.BlockedPeople.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Name));

        this.MasterGain = Clamp(this.MasterGain, 0f, 4f, 1f, nameof(this.MasterGain));
        this.OtherPlayerGain = Clamp(this.OtherPlayerGain, 0f, 4f, 0.8f, nameof(this.OtherPlayerGain));
        this.SelfCooldownSeconds = Clamp(this.SelfCooldownSeconds, 0f, 60f, 2f, nameof(this.SelfCooldownSeconds));
        this.NamedCooldownSeconds = Clamp(this.NamedCooldownSeconds, 0f, 60f, 3f, nameof(this.NamedCooldownSeconds));
        this.PartyCooldownSeconds = Clamp(this.PartyCooldownSeconds, 0f, 60f, 4f, nameof(this.PartyCooldownSeconds));
        this.OtherCooldownSeconds = Clamp(this.OtherCooldownSeconds, 0f, 60f, 6f, nameof(this.OtherCooldownSeconds));
        this.MaxCrowdScale = Clamp(this.MaxCrowdScale, 1f, 20f, 6f, nameof(this.MaxCrowdScale));
        this.RateRefillSeconds = Clamp(this.RateRefillSeconds, 0.1f, 30f, 1.5f, nameof(this.RateRefillSeconds));
        this.GruntWindowSeconds = Clamp(this.GruntWindowSeconds, 0.1f, 5f, 1.5f, nameof(this.GruntWindowSeconds));

        // An unknown bit here would be a bucket this build cannot classify into, so a cast
        // could never land in it and the user would see a tier they can never hear.
        if ((this.Audience & ~AudienceBucket.Anyone) != 0)
        {
            log.Warning("Warcry: Audience had unknown bits ({Value}); masked to the known set.", this.Audience);
            this.Audience &= AudienceBucket.Anyone;
            repairs++;
        }

        if (this.MaxDistanceYalms is < 0 or > 255)
        {
            log.Warning("Warcry: MaxDistanceYalms was {Value}; clamped into 0-255.", this.MaxDistanceYalms);
            this.MaxDistanceYalms = Math.Clamp(this.MaxDistanceYalms, 0, 255);
            repairs++;
        }

        if (this.SoftCrowdLimit is < 1 or > 100)
        {
            log.Warning("Warcry: SoftCrowdLimit was {Value}; clamped into 1-100.", this.SoftCrowdLimit);
            this.SoftCrowdLimit = Math.Clamp(this.SoftCrowdLimit, 1, 100);
            repairs++;
        }

        if (this.RateBurst is < 1 or > 32)
        {
            log.Warning("Warcry: RateBurst was {Value}; clamped into 1-32.", this.RateBurst);
            this.RateBurst = Math.Clamp(this.RateBurst, 1, 32);
            repairs++;
        }

        if (!Enum.IsDefined(this.Sink))
        {
            log.Warning("Warcry: Sink was {Value}; reset to ManagedOnly.", this.Sink);
            this.Sink = SinkMode.ManagedOnly;
            repairs++;
        }

        if (!Enum.IsDefined(this.VoicePosition))
        {
            log.Warning("Warcry: VoicePosition was {Value}; reset to Follow.", this.VoicePosition);
            this.VoicePosition = VoicePositionMode.Follow;
            repairs++;
        }

        // Reset to Off rather than to a suppressing mode: an unreadable value must not be
        // resolved into silencing the game's audio.
        if (!Enum.IsDefined(this.Grunts))
        {
            log.Warning("Warcry: Grunts was {Value}; reset to Off.", this.Grunts);
            this.Grunts = GruntMode.Off;
            repairs++;
        }

        // Hard ceiling, not taste: the game's SoundData pool is shared with the whole
        // client and its Voice bus has five tracks.
        if (this.MaxConcurrent is < 1 or > 8)
        {
            log.Warning(
                "Warcry: MaxConcurrent was {Value}; clamped into 1-8.", this.MaxConcurrent);
            this.MaxConcurrent = Math.Clamp(this.MaxConcurrent, 1, 8);
            repairs++;
        }

        return repairs > 0;

        T Fix<T>(string name) where T : new()
        {
            log.Warning("Warcry: {Field} was null in the saved config; replaced with an empty one.", name);
            repairs++;
            return new T();
        }

        float Clamp(float value, float min, float max, float fallback, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                log.Warning("Warcry: {Field} was {Value}; reset to {Fallback}.", name, value, fallback);
                repairs++;
                return fallback;
            }

            if (value < min || value > max)
            {
                log.Warning("Warcry: {Field} was {Value}; clamped into {Min}-{Max}.", name, value, min, max);
                repairs++;
                return Math.Clamp(value, min, max);
            }

            return value;
        }
    }
}
