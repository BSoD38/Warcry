using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.SQEX.CDev.Engine.Sd.Driver;
using Warcry.Game;
using Warcry.Native;
using EngineVector4 = FFXIVClientStructs.FFXIV.Common.Math.Vector4;

namespace Warcry.Audio;

/// <summary>
/// Plays through the game's own sound engine: a clip we encode, in a container we assemble,
/// handed to <c>SoundManager::PlaySound</c>.
/// </summary>
/// <remarks>
/// <para><b>What this buys over the managed sink.</b> The engine does the mixing, so the
/// audio lands on the game's own bus, follows the game's output device, obeys its volume
/// rules, and is positioned by the same code that positions every other sound in the world.
/// None of that is emulated.</para>
/// <para><b>Gain is deliberately not scaled by the game's sliders here.</b> The managed sink
/// reads <c>SoundMaster</c>/<c>SoundVoice</c> and multiplies them in by hand because it has
/// to. This sink must not: the engine applies its own bus volume downstream, and applying
/// them twice squares the curve and makes everything vanish at low settings.</para>
/// <para><b>Warm-up is real and visible.</b> Resource loading is asynchronous, so the first
/// request for a newly forged path returns before the bytes are in memory. That first play
/// is used as the warm-up and reported as a refusal, which lets a composite fall back to the
/// managed sink for exactly one line rather than dropping it.</para>
/// <para><b>Position is read at dispatch, not at snapshot.</b> A cast line is held back by
/// its own cast bar (~0.45 s), so <c>VoiceRequest.Position</c> is already stale by the time
/// it plays. The caster is located again here; the request's position survives only as the
/// answer for a caster that has since despawned.</para>
/// <para><b>Following costs ownership of a pool slot.</b> See <see cref="VoicePositionMode"/>:
/// in <see cref="VoicePositionMode.Follow"/> the call passes <c>autoRelease: false</c> and
/// this class must release the slot itself. The pool is 256 entries shared with the entire
/// client, so a leak here silences the game, not just the plugin — which is why every exit
/// path releases and why <see cref="Forced"/>/<see cref="Orphaned"/> are counted and shown
/// in the UI rather than logged and forgotten.</para>
/// <para><b>Following takes two writes, and only one of them is audible.</b> Repositioning a
/// sounding voice goes through the audio driver — see <see cref="PushToDriver"/>. Writing the
/// <c>SoundData</c> record is measurably not enough, which cost several in-game rounds to
/// establish, so do not "simplify" the second call away.</para>
/// <para>Verified end to end in <c>docs/native-spike.md</c>, including PLAN.md §6
/// (b)–(e) — volume sliders, positional attenuation, sustained load — and the speed
/// argument, all passed in game on 2026-08-18. Re-verify after a game patch or
/// FFXIVClientStructs bump. Follow mode is newer and its own in-game criteria are listed
/// in docs/PLAN.md §9.</para>
/// </remarks>
public sealed unsafe class NativeVoiceSink : IDisposable
{
    /// <summary>
    /// Assumed tail beyond a clip's own length before an engine-owned voice slot is
    /// considered free.
    /// </summary>
    /// <remarks>
    /// Applies to the modes that do not retain a handle. Nothing is released on this
    /// deadline — the engine reclaims its own slot — so the number only has to be a decent
    /// estimate for the concurrency count. Contrast <see cref="ReleaseGraceSeconds"/>.
    /// </remarks>
    private const double TailSeconds = 0.25;

    /// <summary>
    /// Extra time past a retained voice's expected end before its slot is taken back by
    /// force.
    /// </summary>
    /// <remarks>
    /// Deliberately more generous than <see cref="TailSeconds"/>, because this deadline
    /// <em>releases</em>: expiring early cuts a line off mid-word. It is the backstop for a
    /// voice whose natural end is never observed — the length estimate being wrong, a frame
    /// hitch, <c>IsPlaying()</c> lying — and reaching it while the sound is still playing is
    /// counted as <see cref="Forced"/>, because that means the estimate is off.
    /// </remarks>
    private const double ReleaseGraceSeconds = 1.0;

    /// <summary>
    /// How long a warm-up is given to land before the path is trusted for a real play.
    /// </summary>
    /// <remarks>
    /// A local file behind a Penumbra redirect loads in well under this. The window only
    /// matters when a clip is forged and used almost immediately; the throttle cooldown is
    /// an order of magnitude longer in normal play.
    /// </remarks>
    private const double WarmGraceSeconds = 0.25;

    /// <summary>
    /// A retained voice that ends sooner than this after a driver position push is taken as
    /// proof the push stopped it.
    /// </summary>
    /// <remarks>
    /// Comfortably under the shortest clip in a real pack (0.35 s at the time of writing) so
    /// a genuinely short line retiring normally cannot trip it, and comfortably over the
    /// single frame it actually took.
    /// </remarks>
    private const double DriverKillSeconds = 0.25;

    /// <summary>
    /// The audio index handed to <c>PlaySound</c>. Every index in our containers resolves
    /// to the one clip, so it is always zero — and it is a field rather than a literal at
    /// the call site because the identity check below has to compare against exactly what
    /// was passed.
    /// </summary>
    private const uint SoundNumber = 0u;

    /// <summary>One voice the engine is currently sounding on our behalf.</summary>
    /// <remarks>
    /// <para><see cref="Handle"/> is an integer rather than a <c>SoundData*</c> so that
    /// every use has to cast, which keeps "this is not trusted until checked" visible at
    /// the point of use. Zero means the engine owns the slot and this entry is nothing but
    /// a concurrency count with a clock on it.</para>
    /// <para>The pool is a fixed 256-entry array (<c>SoundDataMemory</c> is
    /// <c>256 * 0xD0</c> bytes), so a stale handle is never a <em>dangling</em> pointer:
    /// reading through it cannot fault, it can only describe somebody else's sound. That is
    /// what makes <see cref="PathUtf8"/> a usable guard rather than wishful thinking.</para>
    /// </remarks>
    private struct LiveVoice
    {
        public nint Handle;
        public uint CasterEntityId;
        public long ExpiresAt;
        public string GamePath;

        /// <summary>
        /// The name the engine itself holds for this slot, snapshotted at play time — the
        /// Penumbra-resolved local file, not the game path we asked for. Null when the
        /// engine held no name, in which case <c>autoRelease: false</c> stands alone.
        /// </summary>
        public byte[]? PathUtf8;

        /// <summary>Whether <c>IsPlaying()</c> has ever been observed true.</summary>
        public bool Started;

        /// <summary>When the engine took this voice, for the driver-push kill check.</summary>
        public long PlayedAt;

        /// <summary>Whether a driver-level position push was made against this voice.</summary>
        public bool DriverPushed;
    }

    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ScdForge forge;
    private readonly CasterLocator locator;
    private readonly List<LiveVoice> live = [];

    /// <summary>
    /// Latched once the driver-level position push is known not to be available, so a
    /// per-frame loop does not retry a call it has already been told it cannot make.
    /// </summary>
    private bool driverUnusable;

    public NativeVoiceSink(IPluginLog log, Configuration config, ScdForge forge, CasterLocator locator)
    {
        this.log = log;
        this.config = config;
        this.forge = forge;
        this.locator = locator;
    }

    public string Status => this.Available
        ? $"Native (game engine). {this.forge.Status}"
        : $"Native unavailable. {this.forge.Status}";

    public bool Available
    {
        get
        {
            if (!this.forge.Initialise())
            {
                return false;
            }

            return SoundManager.Instance() != null;
        }
    }

    public int ActiveVoices => this.live.Count;

    /// <summary>How many voices this sink is currently repositioning every frame.</summary>
    /// <remarks>
    /// The number that matters for pool safety: it must come back to zero after every
    /// line. A floor it never returns to is a leak, and a leaked slot is one the whole
    /// game client cannot use.
    /// </remarks>
    public int Following
    {
        get
        {
            var n = 0;
            foreach (var voice in this.live)
            {
                if (voice.Handle != 0)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>Retained slots handed back to the engine.</summary>
    public long Released { get; private set; }

    /// <summary>
    /// Retained slots reclaimed on the expiry backstop while still playing — i.e. lines cut
    /// short because the length estimate was wrong.
    /// </summary>
    public long Forced { get; private set; }

    /// <summary>
    /// Retained slots the engine recycled out from under us despite <c>autoRelease: false</c>.
    /// </summary>
    /// <remarks>
    /// Should be permanently zero. Anything else falsifies the assumption follow mode is
    /// built on, and is the reason this is a counter on the Status tab rather than a log
    /// line — see docs/PLAN.md §9.
    /// </remarks>
    public long Orphaned { get; private set; }

    /// <summary>Position updates actually pushed to the engine.</summary>
    /// <remarks>
    /// The one number that separates the two ways following can fail. Zero while a line is
    /// audible means this sink never called <c>SetPosition</c> — our bug. Climbing while the
    /// sound plainly does not move means the engine does not honour a live position change
    /// on a playing voice — assumption (b) in docs/PLAN.md §9, and the end of this approach.
    /// </remarks>
    public long Moves { get; private set; }

    /// <summary>Position pushes the audio driver actually accepted.</summary>
    /// <remarks>
    /// Distinct from <see cref="Moves"/> on purpose. <see cref="Moves"/> counts writes to
    /// the <c>SoundData</c> record, which is demonstrably inaudible on its own; this counts
    /// the ones that reached <c>SoundController</c>, which is the path that can actually be
    /// heard. Moves climbing while this stays at zero means the driver is out of reach.
    /// </remarks>
    public long DriverMoves { get; private set; }

    /// <summary>
    /// Why the last request was not played natively, or empty if it was.
    /// </summary>
    /// <remarks>
    /// Every path out of <see cref="TryPlay"/> sets this. A silent boolean was enough to
    /// hide a three-play warm-up ramp behind what looked like "native does not work".
    /// </remarks>
    public string LastRefusal { get; private set; } = string.Empty;

    public bool TryPlay(in VoiceRequest request)
    {
        if (string.IsNullOrEmpty(request.VariantKey))
        {
            // Nothing stable to content-address, so it can neither be cached nor served.
            this.LastRefusal = "the request has no variant key, so it cannot be encoded";
            return false;
        }

        if (this.live.Count >= this.config.MaxConcurrent)
        {
            this.LastRefusal = $"at the concurrency cap ({this.config.MaxConcurrent})";
            return false;
        }

        // The engine's own bus volume is applied downstream; only our own trim goes here.
        var gain = Math.Clamp(request.Gain * this.config.MasterGain, 0f, 2f);
        if (gain <= 0.0001f)
        {
            this.LastRefusal = "gain is zero";
            return false;
        }

        var createSource = request.CreateSource;
        if (!this.forge.TryForge(request.VariantKey, () => createSource(1f), out var clip) || clip is null)
        {
            // "Encoding now" is only true the first time. A variant the forge has given up
            // on would otherwise report itself as perpetually in progress.
            this.LastRefusal = this.forge.TryGetFailure(request.VariantKey, out var why)
                ? why
                : "the clip is still being prepared";
            return false;
        }

        var manager = SoundManager.Instance();
        if (manager == null)
        {
            this.LastRefusal = "SoundManager is not available";
            return false;
        }

        // The warm-up is issued at registration or on a job switch, not charged to a
        // play. A cold clip reaching this point means the warm scoping missed it — the
        // player demonstrably CAN cast it — so heal immediately: warm it now, refuse
        // this one line, and every later one plays.
        if (clip.WarmedAt == 0)
        {
            this.Warm(clip);
            this.LastRefusal = "the clip was not loaded yet, loading it now, so the next line will play";
            return false;
        }

        if (Stopwatch.GetTimestamp() - clip.WarmedAt < (long)(WarmGraceSeconds * Stopwatch.Frequency))
        {
            this.LastRefusal = "clip warmed a moment ago; giving the resource time to load";
            return false;
        }

        // Anything the pipeline did not bake into the encode rides on the engine's own
        // speed argument. 1 when the variant carries its whole pitch already.
        var speed = request.Speed > 0.01f ? request.Speed : 1f;

        var mode = this.config.VoicePosition;
        var positional = mode != VoicePositionMode.Listener;
        var follow = mode == VoicePositionMode.Follow;

        // request.Position was read at snapshot, one slidecast window ago. Ask where the
        // caster is now; keep the snapshot only for a caster that is no longer there.
        var position = request.Position;
        if (positional && this.locator.TryGetPosition(request.CasterEntityId, out var livePosition))
        {
            position = livePosition;
        }

        try
        {
            var result = manager->PlaySound(
                clip.GamePath,
                gain,
                0u,
                position.X, position.Y, position.Z,
                speed,
                0,
                SoundNumber,                // every audio index resolves to our clip
                !follow,                    // autoRelease: false only when we will track it
                CategoryOf(request.SoundCategory),
                false,
                -1,
                false,
                false,
                positional,                 // world coordinates, confirmed by observation
                false);

            if (result == null)
            {
                this.LastRefusal = "the engine had no free sound slot";
                this.log.Warning("NativeVoiceSink: no pool slot for {Path}", clip.GamePath);
                return false;
            }

            var seconds = clip.Seconds / speed;
            this.live.Add(new LiveVoice
            {
                // Retained only in follow mode. Every other mode hands the sound over and
                // forgets it, which is the call shape verified in game on 2026-08-18.
                Handle = follow ? (nint)result : 0,
                CasterEntityId = request.CasterEntityId,
                ExpiresAt = Stopwatch.GetTimestamp()
                    + (long)((seconds + (follow ? ReleaseGraceSeconds : TailSeconds)) * Stopwatch.Frequency),
                GamePath = clip.GamePath,
                PathUtf8 = follow ? this.SnapshotIdentity(result, clip.GamePath) : null,
                Started = false,
                PlayedAt = Stopwatch.GetTimestamp(),
            });

            this.LastRefusal = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            this.LastRefusal = $"PlaySound threw: {ex.Message}";
            this.log.Error(ex, "NativeVoiceSink: PlaySound threw for {Path}", clip.GamePath);
            return false;
        }
    }

    /// <summary>
    /// Decides whether a just-registered clip warms immediately. Set by the pack builder,
    /// which scopes warming to the current job; null warms everything, the pre-builder
    /// behaviour.
    /// </summary>
    public Func<ForgedClip, bool>? WarmGate { get; set; }

    public void Update()
    {
        // Registers anything the background encoder finished. Must be the game thread:
        // Penumbra IPC is not safe to call from a worker.
        foreach (var clip in this.forge.Pump())
        {
            if (this.WarmGate?.Invoke(clip) ?? true)
            {
                this.Warm(clip);
            }
        }

        if (this.live.Count == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var manager = SoundManager.Instance();

        for (var i = this.live.Count - 1; i >= 0; i--)
        {
            var voice = this.live[i];

            if (voice.Handle == 0)
            {
                // The engine owns this slot and reclaims it itself. Nothing to release,
                // nothing to reposition — just stop counting it against the cap.
                if (voice.ExpiresAt <= now)
                {
                    this.live.RemoveAt(i);
                }

                continue;
            }

            var sound = (SoundData*)voice.Handle;

            if (!Owns(sound, in voice))
            {
                // The slot describes a different sound, so autoRelease: false did not hold
                // and this is not ours to release or to move. Drop the reference and count
                // it — this is the measurement that falsifies follow mode's one assumption,
                // not a routine event.
                this.Orphaned++;
                this.live.RemoveAt(i);
                this.log.Warning(
                    "NativeVoiceSink: the engine recycled our retained slot for {Path}. " +
                    "Dropped without releasing; follow mode's ownership assumption does not hold.",
                    voice.GamePath);
                continue;
            }

            var playing = sound->IsPlaying();

            if (playing && !voice.Started)
            {
                voice.Started = true;
            }

            if (voice.ExpiresAt <= now)
            {
                // The backstop. Reaching it while the sound still plays means the length
                // estimate is wrong, so say so rather than quietly clipping lines.
                if (playing)
                {
                    this.Forced++;
                }

                this.Retire(manager, sound, in voice, playing);
                this.live.RemoveAt(i);
                continue;
            }

            if (!playing && voice.Started)
            {
                // Played and finished. Retiring on !IsPlaying() alone would kill a voice
                // whose resource is still loading, which is what Started guards.
                this.Retire(manager, sound, in voice, false);
                this.live.RemoveAt(i);
                continue;
            }

            // Unconditional, and deliberately NOT gated on the voice having started.
            // Position is state on a slot we own, not an event: a voice still loading
            // needs the right position for the instant it does start, and an earlier
            // build that only moved *started* voices did not follow at all whenever
            // IsPlaying() stayed false — which looks precisely like the engine ignoring
            // SetPosition. Keeping the two decisions apart is the point.
            if (this.locator.TryGetPosition(voice.CasterEntityId, out var position))
            {
                // Both, in this order. The first keeps the record coherent — its position
                // and IsPositional are what everything else reads, our own diagnostics
                // included. The second is the one that is actually audible.
                sound->SetPosition(true, position.X, position.Y, position.Z);
                voice.DriverPushed |= this.PushToDriver(sound, position);
                this.Moves++;
            }

            // No caster: leave the voice where it is. A line whose caster despawned
            // finishes where the caster was, which is what the engine does with its own.
            this.live[i] = voice;
        }
    }

    /// <summary>
    /// Stops and releases every voice this sink still owns. Called on a zone change and
    /// during teardown.
    /// </summary>
    /// <remarks>
    /// A voiceline surviving a loading screen is worse than none, and a retained slot
    /// surviving a plugin unload is worse still — it is gone from the game's pool until
    /// the client restarts.
    /// </remarks>
    public void StopAll()
    {
        var manager = SoundManager.Instance();

        foreach (var voice in this.live)
        {
            if (voice.Handle == 0)
            {
                continue;
            }

            var sound = (SoundData*)voice.Handle;
            if (Owns(sound, in voice))
            {
                this.Release(manager, sound, true, voice.GamePath);
            }
        }

        this.live.Clear();
    }

    /// <summary>
    /// Asks the engine for a freshly registered path at zero volume, purely to make it load.
    /// </summary>
    /// <remarks>
    /// <para>This exists because the first request for any path returns before the resource
    /// is in memory. Doing it here, the instant the redirect is registered, means the cost
    /// lands on an idle frame instead of on a voiceline.</para>
    /// <para>The earlier design charged the warm-up to the first real play and refused that
    /// line — which, combined with the encode also costing a play, meant the <b>third</b>
    /// use of a given clip was the first one you actually heard through the engine. With
    /// several mapped actions and a cooldown between them, that ramp is long enough to look
    /// exactly like the native path not working at all.</para>
    /// <para>A warm-up is always <c>autoRelease: true</c> and non-positional, whatever the
    /// configured position mode is: it is a resource load, not a sound, and must never be
    /// tracked or retained.</para>
    /// </remarks>
    public void Warm(ForgedClip clip)
    {
        if (clip.WarmedAt != 0)
        {
            return;
        }

        var manager = SoundManager.Instance();
        if (manager == null)
        {
            return;
        }

        try
        {
            manager->PlaySound(
                clip.GamePath,
                0f,
                0u,
                0f, 0f, 0f,
                1.0f,
                0,
                SoundNumber,
                true,
                SoundVolumeCategory.Player,
                false,
                -1,
                false,
                false,
                false,      // non-positional: this is a load, not a sound
                false);

            clip.WarmedAt = Stopwatch.GetTimestamp();
            this.log.Information("NativeVoiceSink: warmed {Path}", clip.GamePath);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "NativeVoiceSink: warming {Path} failed", clip.GamePath);
        }
    }

    /// <summary>
    /// Snapshots the name the engine itself holds for a slot, taken while the pointer is
    /// certainly still ours — the instant <c>PlaySound</c> returned it.
    /// </summary>
    /// <remarks>
    /// <para>Snapshotting rather than asserting. The first build here demanded the engine
    /// report the game path we asked for; it does not — it reports the <em>Penumbra-resolved
    /// local file</em>, e.g. <c>C:/…/.cache/scd/&lt;hash&gt;.scd</c> for
    /// <c>sound/vfx/warcry/clip/&lt;hash&gt;.scd</c>. Demanding equality threw the check
    /// away for every voice; keeping whatever it says gives a real one back, since a
    /// recycled slot names a different file either way.</para>
    /// <para>Null only when there is no name to read at all, in which case
    /// <c>autoRelease: false</c> stands alone.</para>
    /// </remarks>
    private byte[]? SnapshotIdentity(SoundData* sound, string gamePath)
    {
        try
        {
            var reported = sound->GetFileName();
            return reported.HasValue ? reported.AsSpan().ToArray() : null;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "NativeVoiceSink: could not read back the name for {Path}", gamePath);
            return null;
        }
    }

    /// <summary>Whether a retained slot still holds the voice we put in it.</summary>
    /// <remarks>
    /// <para>Deliberately does <b>not</b> consult <c>IsActive</c>. This test must never
    /// produce a false negative: a voice wrongly called an orphan is dropped without being
    /// released, which leaks the slot — the exact outcome the test exists to prevent. And
    /// <c>IsActive</c> has two states where its value is a guess rather than a fact, both
    /// of which this is asked about every frame: a voice whose resource is still loading,
    /// and one that has finished playing but is still ours to hand back.</para>
    /// <para>What is left is the name and the audio index, which change when the engine
    /// hands the slot to something else. When the name could not be calibrated there is no
    /// real test — which is honest: the guarantee is <c>autoRelease: false</c>, and this
    /// only ever checked it rather than providing it.</para>
    /// </remarks>
    private static bool Owns(SoundData* sound, in LiveVoice voice)
    {
        if (sound == null || sound->SoundNumber != SoundNumber)
        {
            return false;
        }

        if (voice.PathUtf8 is null)
        {
            return true;
        }

        var name = sound->GetFileName();
        return name.HasValue && name.AsSpan().SequenceEqual(voice.PathUtf8);
    }

    /// <summary>
    /// Pushes the position to the audio driver, which is what actually moves a voice that
    /// is already sounding.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the call that makes following work.</b> Confirmed in game on
    /// 2026-08-24: with it, a line tracks the caster through a displacement skill; without
    /// it, it does not. Nothing else in this class moves a sounding voice.</para>
    /// <para><b>Why the record write is not enough.</b> Measured the same day:
    /// <c>SoundData::SetPosition</c> alone is inaudible. Two displacement casts logged ~280
    /// updates each over 11.24 y and 14.78 y of real movement, the slot reading back exactly
    /// what was written, <c>positional=true</c> — and the line stayed where it started both
    /// times. The mixer does not re-read the record once a voice is playing.</para>
    /// <para><b>⚠ W must be 1.</b> The component is unmapped, and it is not cosmetic: passed
    /// as 0, every voice died one frame — 5 ms — after its first push, where the same clip
    /// otherwise played its full 1.37 s. The audible result was total silence. Read as a
    /// homogeneous coordinate it wants 1, which is what works; do not "tidy" this to 0.</para>
    /// <para>Guarded on the resolved address, because the function is located by byte
    /// signature. On a patch where the signature stops matching, calling it is a call
    /// through null — an access violation, from inside a per-frame loop, taking the client
    /// with it. The refusal is latched and named instead. The kill check in
    /// <see cref="Retire"/> is the second half of that guard: if a future patch makes this
    /// call destructive again, it switches itself off after one line rather than silencing
    /// the session.</para>
    /// </remarks>
    private bool PushToDriver(SoundData* sound, Vector3 position)
    {
        if (this.driverUnusable)
        {
            return false;
        }

        var controller = sound->GetSoundController();
        if (controller == null)
        {
            this.driverUnusable = true;
            this.log.Warning(
                "NativeVoiceSink: the voice has no sound controller, so following cannot reach " +
                "the driver. Switch Voice position to Fixed or Listener.");
            return false;
        }

        if (SoundController.Addresses.SetPosition.Value == nint.Zero)
        {
            this.driverUnusable = true;
            this.log.Warning(
                "NativeVoiceSink: SoundController::SetPosition did not resolve on this game " +
                "version, so following cannot reach the driver. Switch Voice position to Fixed.");
            return false;
        }

        var target = default(EngineVector4);
        target.X = position.X;
        target.Y = position.Y;
        target.Z = position.Z;

        // NOT cosmetic, and not zero. 0 here stopped every voice one frame after the push;
        // 1 follows correctly. Verified in game 2026-08-24 — see the remarks above.
        target.W = 1f;

        controller->SetPosition(&target);
        this.DriverMoves++;
        return true;
    }

    /// <summary>Releases a voice, first checking the driver push did not stop it.</summary>
    private void Retire(SoundManager* manager, SoundData* sound, in LiveVoice voice, bool stopFirst)
    {
        // A voice that stops moments after we touched the driver was stopped BY touching
        // the driver — which is exactly what a wrong W did on 2026-08-24, and it cost every
        // line in the session. The call is correct now, so this should never fire; it stays
        // because a game patch can make it destructive again, and one clipped line is a far
        // better failure than a silent session.
        if (voice.DriverPushed && !stopFirst && !this.driverUnusable)
        {
            var lived = (Stopwatch.GetTimestamp() - voice.PlayedAt) / (double)Stopwatch.Frequency;
            if (lived < DriverKillSeconds)
            {
                this.driverUnusable = true;
                this.log.Warning(
                    "NativeVoiceSink: {Path} stopped {Lived:0.000}s after a driver position push. " +
                    "The push is silencing voices, so following is off for this session — lines " +
                    "will still play, but will not follow. This means SoundController::SetPosition " +
                    "has changed behaviour; see docs/PLAN.md 9.",
                    voice.GamePath,
                    lived);
            }
        }

        this.Release(manager, sound, stopFirst, voice.GamePath);
    }

    /// <summary>Hands one slot back to the engine.</summary>
    private void Release(SoundManager* manager, SoundData* sound, bool stopFirst, string gamePath)
    {
        try
        {
            if (stopFirst)
            {
                sound->Stop(0);
            }

            if (manager == null)
            {
                // Nothing to hand it back to. Only reachable while the client is tearing
                // down, when the pool is going away with it.
                this.log.Warning(
                    "NativeVoiceSink: no SoundManager to release the slot for {Path} to", gamePath);
                return;
            }

            manager->ReleaseSoundData(sound);
            this.Released++;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "NativeVoiceSink: releasing the slot for {Path} threw", gamePath);
        }
    }

    /// <summary>
    /// The game's own Player/Party/Other classification, straight through.
    /// </summary>
    /// <remarks>
    /// Read from <c>Character+0x2369</c>, which is the same byte the engine uses for its own
    /// sounds — so a plugin line and a native one are categorised identically rather than
    /// merely similarly.
    /// </remarks>
    private static SoundVolumeCategory CategoryOf(byte soundCategory) => soundCategory switch
    {
        0 => SoundVolumeCategory.Player,
        1 => SoundVolumeCategory.Party,
        _ => SoundVolumeCategory.Other,
    };

    public void Dispose()
    {
        // Retained slots first. Sounds the engine owns are its problem — they are short and
        // auto-released — but a slot we hold is gone from the game's 256-entry pool until
        // the client restarts, so it must be handed back before anything else.
        try
        {
            this.StopAll();
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "NativeVoiceSink: releasing retained voices during teardown failed");
        }

        // What must go next is the redirect set, so an unloaded plugin does not leave the
        // resource system pointing at files in a cache directory.
        try
        {
            this.forge.ShutDown();
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "NativeVoiceSink: clearing redirects during teardown failed");
        }
    }
}
