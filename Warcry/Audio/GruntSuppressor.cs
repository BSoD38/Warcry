using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using InteropGenerator.Runtime;
using Warcry.Game;

namespace Warcry.Audio;

// Silences the game's own battle grunt so it does not double up with ours. The mirror image
// of NativeVoiceSink — both talk to SoundManager, one to start a voice and this to stop one
// — which is why it lives in Audio rather than Native.
//
// Only the attack banks. A Vo_Battle container holds five sound groups and soundNumber
// picks one: 0 and 3 are attack, 1 is damage taken and 2 is death, and out-of-range values
// 4-7 fall back to the attack bank. So the filter is "not 1 and not 2", and a suppressed
// character still grunts when hurt and killed. ScdForge honours the same split when it
// retargets audio indices.
//
// The window opens at snapshot, not at playback: the grunt runs off the action's animation
// timeline rather than the effect packet, landing 5-1071 ms after snapshot, fixed per
// action. It can therefore land well before our own line, which is held back by the cast
// bar, so arming any later would miss the fast half of that range.
//
// Suppression is by gain, never by refusal. Original is always called and only the volume
// argument is zeroed: the game passes autoRelease: false for its own grunt, so its caller
// retains the SoundData*, and returning null would change engine bookkeeping it depends on.
// The same pool slot is consumed either way.
//
// ⚠ Unverified in game: that volume: 0f fully silences the call. If a fragment leaks, the
// next lever is SoundVolumeCategory.NoPlay (= 5). See docs/PLAN.md 5.6.
public sealed unsafe class GruntSuppressor : IDisposable
{
    // Casters that can be inside their window at once. Far above what the throttle can
    // produce, so the overwrite path below is a backstop rather than a working mode.
    private const int MaxWindows = 16;

    // How close the emitter must be to an armed caster to count as theirs. Positions are
    // refreshed every frame, so the stored one is at most ~16 ms stale; the tolerance is
    // this wide only to absorb the gap between the object's position and wherever the engine
    // puts the emitter.
    private const float MatchRadiusYalms = 1.5f;

    private const int FaultLimit = 10;

    // Lower-case ASCII, matched case-insensitively against the raw path bytes. Real paths
    // look like sound/voice/Vo_Battle/Vo_Battle_PC_ros_Ma_fr.scd — one file per race, gender
    // and language — so nothing narrower covers them all. Our own
    // sound/vfx/warcry/clip/*.scd never matches, which keeps this hook off the native sink.
    private static readonly byte[] BattleVoiceMarker = "vo_battle"u8.ToArray();

    private readonly Hook<SoundManager.Delegates.PlaySound>? hook;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly CasterLocator locator;

    private readonly Window[] windows = new Window[MaxWindows];

    private bool active;
    private int faults;
    private volatile bool tripped;

    public GruntSuppressor(
        IGameInteropProvider interop,
        IPluginLog log,
        Configuration config,
        CasterLocator locator)
    {
        this.log = log;
        this.config = config;
        this.locator = locator;

        try
        {
            this.HookAddress = SoundManager.Addresses.PlaySound.Value;
            this.hook = interop.HookFromAddress<SoundManager.Delegates.PlaySound>(
                this.HookAddress, this.PlaySoundDetour);

            // Installed INERT. PlaySound fires for every sound in the game — footsteps, UI
            // clicks, VFX — so sitting in it while the feature is off is not acceptable.
            this.hook.Disable();
            this.log.Information("GruntSuppressor: hooked PlaySound at 0x{Address:X} (idle)", this.HookAddress);
        }
        catch (Exception ex)
        {
            // Patch-day signature failure must be inert, not a crash. The Status tab reads
            // Installed and explains itself.
            this.hook = null;
            this.log.Error(ex, "GruntSuppressor: could not resolve SoundManager.PlaySound. Suppression is unavailable.");
        }
    }

    // One caster whose grunt is currently unwanted. Written on the framework thread and read
    // from the detour; every field is a value with no pointer in it, so if PlaySound is ever
    // called off-thread the worst a torn read produces is one mis-measured distance, never a
    // fault.
    private struct Window
    {
        public uint EntityId;
        public Vector3 Position;
        public long ExpiresAt;
    }

    public nint HookAddress { get; }

    // False means the feature cannot run at all.
    public bool Installed => this.hook is not null;

    public bool Active => this.active;

    // Disabled after repeated faults, until the plugin is reloaded.
    public bool Tripped => this.tripped;

    public long Suppressed { get; private set; }

    public string LastPath { get; private set; } = string.Empty;

    public uint LastSoundNumber { get; private set; }

    // -1 under GruntMode.Always, which attributes nothing.
    public float LastMatchDistance { get; private set; }

    public DateTime LastAt { get; private set; }

    public int ArmedCount
    {
        get
        {
            var now = Stopwatch.GetTimestamp();
            var armed = 0;
            for (var i = 0; i < this.windows.Length; i++)
            {
                if (this.windows[i].EntityId != 0 && this.windows[i].ExpiresAt > now)
                {
                    armed++;
                }
            }

            return armed;
        }
    }

    // Called from inside the ActionEffect detour, so it allocates nothing and never touches
    // the object table: the position comes from the event, and Update keeps it current.
    public void Arm(uint casterEntityId, Vector3 position)
    {
        if (casterEntityId == 0 || this.tripped || this.hook is null || this.config.Grunts != GruntMode.WhenVoiced)
        {
            return;
        }

        var seconds = float.IsFinite(this.config.GruntWindowSeconds)
            ? Math.Clamp(this.config.GruntWindowSeconds, 0.1f, 5f)
            : 1.5f;
        var now = Stopwatch.GetTimestamp();
        var expires = now + (long)(seconds * Stopwatch.Frequency);

        var free = -1;
        var soonest = 0;

        for (var i = 0; i < this.windows.Length; i++)
        {
            ref var window = ref this.windows[i];

            // Re-arm rather than spend a second slot: a caster firing again inside their
            // own window is the common case, not an exception.
            if (window.EntityId == casterEntityId)
            {
                window.Position = position;
                window.ExpiresAt = expires;
                return;
            }

            if (free < 0 && (window.EntityId == 0 || window.ExpiresAt <= now))
            {
                free = i;
            }

            if (window.ExpiresAt < this.windows[soonest].ExpiresAt)
            {
                soonest = i;
            }
        }

        // Full: take the slot that was about to expire anyway.
        var slot = free >= 0 ? free : soonest;
        this.windows[slot] = new Window
        {
            EntityId = casterEntityId,
            Position = position,
            ExpiresAt = expires,
        };
    }

    // Follows the hook to the config, keeps armed positions current and retires expired
    // windows. Framework thread only — it reads the object table.
    public void Update()
    {
        // Enabled is advertised as "nothing at all, no work done in the background", and a
        // hook sitting in every sound the game plays is not nothing.
        this.SetActive(!this.tripped
            && this.hook is not null
            && this.config.Enabled
            && this.config.Grunts != GruntMode.Off);

        var now = Stopwatch.GetTimestamp();
        var targeting = this.config.Grunts == GruntMode.WhenVoiced && !this.tripped;

        for (var i = 0; i < this.windows.Length; i++)
        {
            ref var window = ref this.windows[i];
            if (window.EntityId == 0)
            {
                continue;
            }

            if (!targeting || window.ExpiresAt <= now)
            {
                window = default;
                continue;
            }

            // A caster who has despawned keeps their last known position: the grunt, if it
            // is still coming, comes from where they were.
            if (this.locator.TryGetPosition(window.EntityId, out var position))
            {
                window.Position = position;
            }
        }
    }

    // For when the world changes underneath them.
    public void Clear() => Array.Clear(this.windows);

    public void Dispose()
    {
        this.hook?.Disable();
        this.hook?.Dispose();
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needleLower)
    {
        if (needleLower.Length == 0 || haystack.Length < needleLower.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needleLower.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needleLower.Length; j++)
            {
                var c = haystack[i + j];
                if (c is >= (byte)'A' and <= (byte)'Z')
                {
                    c += 32;
                }

                if (c != needleLower[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private void SetActive(bool wanted)
    {
        if (this.active == wanted || this.hook is null)
        {
            return;
        }

        this.active = wanted;
        if (wanted)
        {
            this.hook.Enable();
        }
        else
        {
            this.hook.Disable();
            this.Clear();
        }

        this.log.Information("GruntSuppressor: {State}", wanted ? "live" : "idle");
    }

    private SoundData* PlaySoundDetour(
        SoundManager* thisPtr, CStringPointer path, float volume, uint fadeInDuration,
        float posX, float posY, float posZ, float speed, int a9, uint soundNumber,
        bool autoRelease, SoundVolumeCategory volumeCategory, bool a13, int midiNote,
        bool a15, bool defaultFadeOut, bool isPositional, bool a18)
    {
        var suppress = false;

        try
        {
            suppress = this.ShouldSuppress(path, soundNumber, posX, posY, posZ);
        }
        catch (Exception ex)
        {
            this.Fault(ex);
        }

        var result = this.hook!.OriginalDisposeSafe(
            thisPtr, path, suppress ? 0f : volume, fadeInDuration, posX, posY, posZ, speed,
            a9, soundNumber, autoRelease, volumeCategory, a13, midiNote, a15, defaultFadeOut,
            isPositional, a18);

        // Redundant if the zeroed argument did its job, and the only thing that works if it
        // did not. Both writes are cheap.
        if (suppress && result != null)
        {
            result->Volume = 0f;
        }

        return result;
    }

    private bool ShouldSuppress(CStringPointer path, uint soundNumber, float x, float y, float z)
    {
        if (this.tripped)
        {
            return false;
        }

        var mode = this.config.Grunts;
        if (mode == GruntMode.Off)
        {
            return false;
        }

        if (!path.HasValue)
        {
            return false;
        }

        // Groups 1 and 2 are damage taken and death. Silencing those would take the
        // character's whole voice, not their action grunt.
        if (soundNumber is 1 or 2)
        {
            return false;
        }

        // Over the raw UTF-8: a non-matching sound — which is nearly all of them — costs
        // no allocation at all.
        if (!ContainsAsciiIgnoreCase(path.AsSpan(), BattleVoiceMarker))
        {
            return false;
        }

        var distance = -1f;
        if (mode == GruntMode.WhenVoiced && !this.TryMatch(new Vector3(x, y, z), out distance))
        {
            return false;
        }

        // Rare enough to afford the string, and a suppression the user cannot see is
        // indistinguishable from the plugin breaking their game audio.
        this.Suppressed++;
        this.LastPath = path.ToString();
        this.LastSoundNumber = soundNumber;
        this.LastMatchDistance = distance;
        this.LastAt = DateTime.Now;
        return true;
    }

    private bool TryMatch(Vector3 emitter, out float distance)
    {
        var now = Stopwatch.GetTimestamp();
        var best = float.MaxValue;

        for (var i = 0; i < this.windows.Length; i++)
        {
            var window = this.windows[i];
            if (window.EntityId == 0 || window.ExpiresAt <= now)
            {
                continue;
            }

            var gap = Vector3.Distance(emitter, window.Position);
            if (gap < best)
            {
                best = gap;
            }
        }

        distance = best;
        return best <= MatchRadiusYalms;
    }

    private void Fault(Exception ex)
    {
        this.log.Error(ex, "GruntSuppressor detour threw");
        if (Interlocked.Increment(ref this.faults) > FaultLimit)
        {
            // Leave the hook installed but pass everything straight through. Unhooking
            // mid-frame from inside the detour is the more dangerous option.
            this.tripped = true;
            this.log.Error("GruntSuppressor: {Limit} faults — suppression disabled until reload.", FaultLimit);
        }
    }
}
