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

// Plays through the game's own sound engine: a clip we encode, in a container we assemble,
// handed to SoundManager::PlaySound. The engine does the mixing, so the audio lands on the
// game's own bus, follows its output device, obeys its volume rules, and is positioned by
// the same code that positions every other sound in the world.
//
// Gain is NOT scaled by the game's sliders here. The managed sink reads
// SoundMaster/SoundVoice and multiplies them in by hand because it has to; the engine
// applies its own bus volume downstream, and applying them twice squares the curve and makes
// everything vanish at low settings.
//
// Resource loading is asynchronous, so the first request for a newly forged path returns
// before the bytes are in memory. That first play is the warm-up and is reported as a
// refusal, which lets the composite fall back to the managed sink for one line.
//
// Position is read at dispatch, not at snapshot: a cast line is held back by its own cast
// bar, so VoiceRequest.Position is already stale. The caster is located again here, and the
// request's position survives only as the answer for a caster that has since despawned.
//
// Following costs ownership of a pool slot: in VoicePositionMode.Follow the call passes
// autoRelease: false and this class must release the slot itself. The pool is 256 entries
// shared with the entire client, so a leak silences the game and not just the plugin — hence
// a release on every exit path, and Forced/Orphaned counted in the UI rather than logged.
//
// Following takes two writes and only one of them is audible: repositioning a sounding voice
// goes through the audio driver, see PushToDriver. Writing the SoundData record is not
// enough on its own, so do not "simplify" the second call away.
//
// Verified end to end in docs/native-spike.md, including PLAN.md §6 (b)–(e) — volume
// sliders, positional attenuation, sustained load — and the speed argument. Re-verify after
// a game patch or FFXIVClientStructs bump. Follow mode's own in-game criteria are in
// docs/PLAN.md §9.
public sealed unsafe class NativeVoiceSink : IDisposable
{
    // Assumed tail beyond a clip's own length before an engine-owned slot counts as free.
    // Nothing is released on this deadline — the engine reclaims its own slot — so it only
    // has to be a decent estimate for the concurrency count. Contrast ReleaseGraceSeconds.
    private const double TailSeconds = 0.25;

    // Extra time past a retained voice's expected end before its slot is taken back by
    // force. More generous than TailSeconds because this deadline releases, and expiring
    // early cuts a line off mid-word. It is the backstop for a voice whose natural end is
    // never observed — a wrong length estimate, a frame hitch, IsPlaying() lying — and
    // reaching it while the sound still plays counts as Forced.
    private const double ReleaseGraceSeconds = 1.0;

    // How long a warm-up is given to land before the path is trusted for a real play. A
    // local file behind a Penumbra redirect loads in well under this; the window only
    // matters when a clip is forged and used almost immediately.
    private const double WarmGraceSeconds = 0.25;

    // A retained voice ending sooner than this after a driver position push is taken as
    // proof the push stopped it. Under the shortest clip in a real pack, so a short line
    // retiring normally cannot trip it, and well over the single frame a kill takes.
    private const double DriverKillSeconds = 0.25;

    // The audio index handed to PlaySound. Every index in our containers resolves to the one
    // clip, so it is always zero; a field rather than a literal because the identity check
    // below has to compare against exactly what was passed.
    private const uint SoundNumber = 0u;

    // One voice the engine is currently sounding on our behalf. Handle is an integer rather
    // than a SoundData* so every use has to cast, keeping "not trusted until checked" visible
    // at the point of use; zero means the engine owns the slot and this entry is a
    // concurrency count with a clock on it.
    // The pool is a fixed 256-entry array (SoundDataMemory is 256 * 0xD0 bytes), so a stale
    // handle is never dangling: reading through it cannot fault, it can only describe
    // somebody else's sound. That is what makes PathUtf8 a usable guard.
    private struct LiveVoice
    {
        public nint Handle;
        public uint CasterEntityId;
        public long ExpiresAt;
        public string GamePath;

        // The name the engine holds for this slot, snapshotted at play time: the
        // Penumbra-resolved local file, not the game path we asked for. Null when the engine
        // held no name, in which case autoRelease: false stands alone.
        public byte[]? PathUtf8;

        // Whether IsPlaying() has ever been observed true.
        public bool Started;

        // When the engine took this voice, for the driver-push kill check.
        public long PlayedAt;

        public bool DriverPushed;
    }

    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ScdForge forge;
    private readonly CasterLocator locator;
    private readonly List<LiveVoice> live = [];

    // Latched once the driver-level position push is known unavailable, so a per-frame loop
    // does not retry a call it has already been told it cannot make.
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

    // The number that matters for pool safety: it must come back to zero after every line.
    // A floor it never returns to is a leak, and a leaked slot is one the whole game client
    // cannot use.
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

    // Retained slots handed back to the engine.
    public long Released { get; private set; }

    // Retained slots reclaimed on the expiry backstop while still playing: lines cut short
    // because the length estimate was wrong.
    public long Forced { get; private set; }

    // Retained slots the engine recycled out from under us despite autoRelease: false.
    // Should be permanently zero — anything else falsifies the assumption follow mode is
    // built on, which is why it is a counter on the Status tab. See docs/PLAN.md §9.
    public long Orphaned { get; private set; }

    // Position updates pushed to the engine. Separates the two ways following can fail: zero
    // while a line is audible means this sink never called SetPosition, our bug; climbing
    // while the sound plainly does not move means the engine does not honour a live position
    // change on a playing voice, which is the end of this approach. See docs/PLAN.md §9.
    public long Moves { get; private set; }

    // Position pushes the audio driver accepted. Moves counts writes to the SoundData
    // record, which is inaudible on its own; this counts the ones that reached
    // SoundController, the path that can be heard. Moves climbing while this stays at zero
    // means the driver is out of reach.
    public long DriverMoves { get; private set; }

    // Empty if the last request played. Every path out of TryPlay sets it: a silent boolean
    // is enough to hide a warm-up ramp behind what looks like "native does not work".
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

        // The warm-up is issued at registration or on a job switch, never charged to a play.
        // A cold clip reaching this point means the warm scoping missed it — the player
        // demonstrably can cast it — so warm it now, refuse this one line, and every later
        // one plays.
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
                // forgets it.
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

    // Whether a just-registered clip warms immediately. Set by the pack builder, which
    // scopes warming to the active jobs; null warms everything.
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
                // and this is not ours to release or move. Counted, not routine: it is the
                // measurement that falsifies follow mode's one assumption.
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
            // Position is state on a slot we own, not an event: a voice still loading needs
            // the right position for the instant it does start. Moving only started voices
            // means no following at all whenever IsPlaying() stays false, which looks
            // precisely like the engine ignoring SetPosition.
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

    // Called on a zone change and during teardown. A voiceline surviving a loading screen is
    // worse than none, and a retained slot surviving a plugin unload is worse still — it is
    // gone from the game's pool until the client restarts.
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

    // Asks the engine for a freshly registered path at zero volume, purely to make it load.
    // The first request for any path returns before the resource is in memory, so doing it
    // the instant the redirect is registered puts the cost on an idle frame instead of on a
    // voiceline.
    // Always autoRelease: true and non-positional, whatever the configured position mode is:
    // this is a resource load, not a sound, and must never be tracked or retained.
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

    // Snapshots the name the engine holds for a slot, taken the instant PlaySound returned
    // it, while the pointer is certainly still ours.
    // Snapshotted rather than asserted: the engine reports the Penumbra-resolved local file,
    // e.g. C:/…/.cache/scd/<hash>.scd for sound/vfx/warcry/clip/<hash>.scd, so demanding the
    // game path back would throw the check away for every voice. Whatever it says is still a
    // real test, since a recycled slot names a different file.
    // Null only when there is no name to read, in which case autoRelease: false stands alone.
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

    // Whether a retained slot still holds the voice we put in it.
    // Deliberately does NOT consult IsActive. This test must never produce a false negative:
    // a voice wrongly called an orphan is dropped without being released, which leaks the
    // slot — the exact outcome the test exists to prevent. IsActive is a guess rather than a
    // fact in two states this is asked about every frame: a voice whose resource is still
    // loading, and one that has finished playing but is still ours to hand back.
    // What is left is the name and the audio index, which change when the engine hands the
    // slot to something else. With no name to compare there is no real test, which is
    // honest: the guarantee is autoRelease: false, and this only ever checked it.
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

    // The call that makes following work: nothing else in this class moves a sounding voice.
    // SoundData::SetPosition alone is inaudible — the mixer does not re-read the record once
    // a voice is playing — so the record write is not enough on its own.
    // ⚠ W must be 1. The component is unmapped and it is not cosmetic: passed as 0, every
    // voice dies one frame after its first push, and the audible result is total silence.
    // Read as a homogeneous coordinate it wants 1. Do not "tidy" this to 0.
    // Guarded on the resolved address, because the function is located by byte signature: on
    // a patch where the signature stops matching, calling it is a call through null — an
    // access violation from inside a per-frame loop, taking the client with it. The kill
    // check in Retire is the second half of that guard, switching following off after one
    // line rather than silencing the session.
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

        // Not cosmetic, and not zero: 0 stops every voice one frame after the push. See
        // above.
        target.W = 1f;

        controller->SetPosition(&target);
        this.DriverMoves++;
        return true;
    }

    // Releases a voice, first checking the driver push did not stop it.
    private void Retire(SoundManager* manager, SoundData* sound, in LiveVoice voice, bool stopFirst)
    {
        // A voice that stops moments after we touched the driver was stopped BY touching the
        // driver, which is what a wrong W does. The call is correct, so this should never
        // fire; it stays because a game patch can make the call destructive again, and one
        // clipped line is a better failure than a silent session.
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

    // Hands one slot back to the engine.
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

    // Character+0x2369 is the same byte the engine uses for its own sounds, so a plugin line
    // and a native one are categorised identically rather than merely similarly.
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
