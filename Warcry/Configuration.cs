using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Warcry.Audio;
using Warcry.Game;

namespace Warcry;

// Plugin settings only. Profiles and clip metadata live in separate System.Text.Json files
// under GetPluginConfigDirectory(), because SavePluginConfig writes through
// IReliableFileStorage — duplicating every byte into a SQLite backup, hard-failing above
// 64MB, synchronous — and serialises with TypeNameHandling.Objects. See docs/PLAN.md 5.8.
// That serialisation bakes this type's namespace into every user's config file, so it is
// fixed as Warcry.Configuration: moving or renaming it orphans their settings.
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    // Bump and add a Migrate step whenever a field changes meaning or goes away.
    public const int CurrentVersion = 5;

    public int Version { get; set; } = CurrentVersion;

    public bool Enabled { get; set; } = true;

    // Master trim on top of the game's own sliders.
    public float MasterGain { get; set; } = 1.0f;

    // Scale by the game's Voice slider rather than Sound Effects.
    public bool UseVoiceSliderNotSe { get; set; } = true;

    // Hard cap: the game's SoundData pool is 256 entries shared with the entire client and
    // the Voice bus has 5 tracks.
    public int MaxConcurrent { get; set; } = 3;

    // The playback master switch. Off keeps detection and the Events tab running but plays
    // nothing. The name is historical; renaming it would orphan saved configs.
    public bool PlayTestToneOnActions { get; set; } = true;

    // Auto means the engine when Penumbra is present and NAudio otherwise, so a fresh
    // install is never silent. NativeOnly and Auto both require Penumbra, and NativeOnly
    // never substitutes NAudio — a refused line is a counted, explained drop.
    public SinkMode Sink { get; set; } = SinkMode.Auto;

    // Native sink only; NAudio has no positional model, so a line that falls back to it in
    // Auto ignores this.
    // Follow by default: the engine treats a position handed to PlaySound as fixed for the
    // whole clip while the listener follows the camera, so without following, any skill that
    // displaces you leaves your voiceline behind and it fades out mid-word.
    public VoicePositionMode VoicePosition { get; set; } = VoicePositionMode.Follow;

    // The synthesised tone for an action with no clip mapped: audible proof the action was
    // detected while setting mappings up, and noise once you are done.
    public bool FallBackToTestTone { get; set; } = true;

    // Delay playback until the cast bar has finished, using the per-event measured
    // remainder. Instants self-gate — no cast bar, so zero delay.
    public bool WaitForCastToFinish { get; set; } = true;

    // Off by default, and no migration step raises it: an update that changes what the game
    // sounds like without being asked is as unwelcome as one that starts voicing strangers.
    // Damage-taken and death grunts are never touched in any mode — see GruntSuppressor.
    public GruntMode Grunts { get; set; } = GruntMode.Off;

    // How long after a cast the game's grunt for it stays suppressed; only WhenVoiced reads
    // it. The grunt is animation-driven and lands 5-1071 ms after snapshot, fixed per action
    // (docs/native-spike.md), so nothing shorter than about a second covers the slow end.
    // Exposed rather than fixed because a long window eats the NEXT action's grunt on a fast
    // rotation.
    public float GruntWindowSeconds { get; set; } = 1.5f;

    public bool DisableInCutscenes { get; set; } = true;

    public bool DisableInPvP { get; set; } = true;

    public bool DisableInGpose { get; set; } = true;

    public bool DisableInQuestEvents { get; set; } = true;

    // TerritoryType ids where nothing plays. Useful for hub cities.
    public HashSet<uint> BlockedTerritories { get; set; } = [];

    // Whose actions are heard at all; anything else is a counted DropStage.Audience drop.
    // Self by default, and widening it is always the user's explicit act — a plugin that
    // starts voicing strangers after an update would be a nasty surprise.
    public AudienceBucket Audience { get; set; } = AudienceBucket.Self;

    // Heard when AudienceBucket.Named is enabled, and separately targetable by a profile.
    public List<NamedPlayer> NamedPeople { get; set; } = [];

    // Never play a line, whatever else would have matched. Checked before every bucket
    // except Self.
    public List<NamedPlayer> BlockedPeople { get; set; } = [];

    // Yalms past which someone else's line is not played; 0 disables the gate. Not the
    // engine's falloff, which only makes a distant line quiet: this stops the line being
    // requested at all, so it costs no voice from the concurrency cap and no slot in the
    // game's shared sound pool. Never applied to your own actions.
    public int MaxDistanceYalms { get; set; } = 30;

    // Volume trim applied to everyone but you.
    public float OtherPlayerGain { get; set; } = 0.8f;

    // Seconds before the same caster can trigger another line. 0 disables.
    public float SelfCooldownSeconds { get; set; } = 2.0f;

    public float NamedCooldownSeconds { get; set; } = 3.0f;

    // Party, alliance and friends.
    public float PartyCooldownSeconds { get; set; } = 4.0f;

    public float OtherCooldownSeconds { get; set; } = 6.0f;

    // Stretch other people's cooldowns as the crowd grows (docs/PLAN.md 5.7 stage 3). Your
    // own lines are never scaled: a hub city should quieten the strangers around you without
    // making you inaudible.
    public bool ScaleWithCrowd { get; set; } = true;

    // Nearby audience members above which cooldowns start stretching.
    public int SoftCrowdLimit { get; set; } = 12;

    // Ceiling on the crowd multiplier, so a full raid cannot mute everyone forever.
    public float MaxCrowdScale { get; set; } = 6.0f;

    // Caps the total rate of other people's lines (docs/PLAN.md 5.7 stage 4). The per-caster
    // cooldown bounds one person; this bounds the sum of them. Requests are dropped rather
    // than queued — a voiceline that arrives late is worse than one that never arrives.
    public bool LimitTotalRate { get; set; } = true;

    // How many other people's lines may fire back to back before the rate bites.
    public int RateBurst { get; set; } = 4;

    // Seconds to earn back one line of burst.
    public float RateRefillSeconds { get; set; } = 1.5f;

    // Auto-attacks fire constantly and are never worth a voiceline.
    public bool SkipAutoAttacks { get; set; } = true;

    // Only voice actions that have a cast bar.
    public bool CastsOnly { get; set; }

    // Actions that never play, whatever is mapped to them.
    public HashSet<uint> MutedActionIds { get; set; } = [];

    // Action ids actually observed firing on this character. The Action sheet contains
    // duplicates, unused rows and NPC copies, so "which of the five Riposte rows is the real
    // one" is not answerable from the sheet, only from what the hook has seen. Persisted so
    // the list survives a restart.
    public List<uint> ObservedActionIds { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    // Reads the stored config, migrates it, and repairs anything unusable. A config that
    // fails to deserialise must not silently reset every setting, and a collection property
    // that comes back null — which a hand-edited or truncated file does, since the
    // initialisers above only apply to a fresh object — would throw from inside the action
    // hook the first time anything read it.
    // Writes back only when something changed: SavePluginConfig is synchronous and writes
    // through IReliableFileStorage, so a normal start should not touch the disk.
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

    // Walks the version ladder, one version per step, so a config of any age arrives intact.
    // v5 is the first schema any user can hold; every earlier version existed only between
    // development commits. Add steps as if (this.Version < N) { …; this.Version = N; }, in
    // order.
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

        if (this.Version < 5)
        {
            // v5: the audience filter. Everything before it was self-only, which is what the
            // user consented to, so an upgrade must not start voicing anyone new. Existing
            // profiles become "everyone" targets — ProfileMatch.Audience defaults that way —
            // but the global filter stays shut until the user opens it.
            this.Audience = AudienceBucket.Self;
            this.Version = 5;
        }

        this.Version = CurrentVersion;
        log.Information("Warcry: migrated configuration from version {From} to {To}.", from, this.Version);
        return true;
    }

    // Forces every value back into a range the plugin can run with: a hand-edited file, a
    // partial write, or NaN, which propagates silently through the gain chain and turns into
    // inaudible output rather than an error.
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

        this.MaxDistanceYalms = ClampInt(this.MaxDistanceYalms, 0, 255, nameof(this.MaxDistanceYalms));
        this.SoftCrowdLimit = ClampInt(this.SoftCrowdLimit, 1, 100, nameof(this.SoftCrowdLimit));
        this.RateBurst = ClampInt(this.RateBurst, 1, 32, nameof(this.RateBurst));

        // Hard ceiling, not taste: the game's SoundData pool is shared with the whole
        // client and its Voice bus has five tracks.
        this.MaxConcurrent = ClampInt(this.MaxConcurrent, 1, 8, nameof(this.MaxConcurrent));

        this.Sink = Defined(this.Sink, SinkMode.ManagedOnly, nameof(this.Sink));
        this.VoicePosition = Defined(
            this.VoicePosition, VoicePositionMode.Follow, nameof(this.VoicePosition));

        // Off rather than a suppressing mode: an unreadable value must not be resolved
        // into silencing the game's audio.
        this.Grunts = Defined(this.Grunts, GruntMode.Off, nameof(this.Grunts));

        return repairs > 0;

        int ClampInt(int value, int min, int max, string name)
        {
            if (value >= min && value <= max)
            {
                return value;
            }

            log.Warning("Warcry: {Field} was {Value}; clamped into {Min}-{Max}.", name, value, min, max);
            repairs++;
            return Math.Clamp(value, min, max);
        }

        T Defined<T>(T value, T fallback, string name) where T : struct, Enum
        {
            if (Enum.IsDefined(value))
            {
                return value;
            }

            log.Warning("Warcry: {Field} was {Value}; reset to {Fallback}.", name, value, fallback);
            repairs++;
            return fallback;
        }

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
