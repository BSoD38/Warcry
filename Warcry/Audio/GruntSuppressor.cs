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

/// <summary>
/// Silences the game's own battle grunt so it does not double up with ours.
/// </summary>
/// <remarks>
/// <para>The mirror image of <see cref="NativeVoiceSink"/>: both talk to
/// <c>SoundManager</c>, one to start a voice and this one to stop one. It lives in
/// <c>Audio</c> for that reason rather than in <c>Native</c>, which owns the SCD and
/// Penumbra layer.</para>
/// <para><b>Only the attack banks.</b> A <c>Vo_Battle</c> container holds five sound
/// groups and <c>soundNumber</c> picks one: 0 and 3 are attack, <b>1 is damage taken and
/// 2 is death</b> (parsed from a real file and confirmed by ear — see
/// <c>docs/native-spike.md</c>). Out-of-range values 4-7 fall back to the attack bank. So
/// the filter is "not 1 and not 2", and a suppressed character still grunts when hurt and
/// killed. <c>ScdForge</c> honours the same split when it retargets audio indices.</para>
/// <para><b>The window opens at snapshot, not at playback.</b> The grunt is driven by the
/// action's animation timeline, not the effect packet: measured at 5-1071 ms after
/// snapshot, fixed per action. It can therefore land well before our own line, which is
/// held back by the cast bar. Arming any later would miss the fast half of that range.</para>
/// <para><b>Suppression is by gain, never by refusal.</b> <c>Original</c> is always called;
/// only the volume argument is zeroed. The game passes <c>autoRelease: false</c> for its own
/// grunt, so its caller retains the <c>SoundData*</c> — returning null would change engine
/// bookkeeping the game depends on, and the same pool slot is consumed either way.</para>
/// <para><b>Unverified in game.</b> That <c>volume: 0f</c> fully silences the call is the one
/// link in the chain a green build cannot establish. If a fragment still leaks, the lever to
/// try next is <c>SoundVolumeCategory.NoPlay</c> (= 5). See docs/PLAN.md 5.6.</para>
/// </remarks>
public sealed unsafe class GruntSuppressor : IDisposable
{
    /// <summary>Casters that can be inside their window at once.</summary>
    /// <remarks>
    /// Far above what the throttle can produce — the cooldown is seconds per caster and the
    /// global rate cap is a handful per second — so the overwrite path below is a backstop
    /// rather than a working mode.
    /// </remarks>
    private const int MaxWindows = 16;

    /// <summary>How close the emitter must be to an armed caster to be counted as theirs.</summary>
    /// <remarks>
    /// Positions are refreshed every frame, so the stored one is at most ~16 ms stale — a
    /// sprinting player moves under a tenth of a yalm in that time. The tolerance is this
    /// wide only to absorb the difference between the object's position and wherever the
    /// engine decides to put the emitter.
    /// </remarks>
    private const float MatchRadiusYalms = 1.5f;

    private const int FaultLimit = 10;

    /// <summary>Lower-case ASCII, matched case-insensitively against the raw path bytes.</summary>
    /// <remarks>
    /// Real paths look like <c>sound/voice/Vo_Battle/Vo_Battle_PC_ros_Ma_fr.scd</c> — one
    /// file per race, gender and language, so nothing narrower than this substring would
    /// cover them all. Our own <c>sound/vfx/warcry/clip/*.scd</c> never matches, which is
    /// what keeps this hook off the native sink's back.
    /// </remarks>
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

    /// <summary>One caster whose grunt is currently unwanted.</summary>
    /// <remarks>
    /// Written on the framework thread and read from the detour. Every field is a value
    /// with no pointer in it, so if <c>PlaySound</c> ever turns out to be called off-thread
    /// the worst a torn read can produce is one mis-measured distance — a grunt kept that
    /// should have gone, or the reverse — never a fault.
    /// </remarks>
    private struct Window
    {
        public uint EntityId;
        public Vector3 Position;
        public long ExpiresAt;
    }

    public nint HookAddress { get; }

    /// <summary>Whether the hook resolved. False means the feature cannot run at all.</summary>
    public bool Installed => this.hook is not null;

    /// <summary>Whether the detour is currently live.</summary>
    public bool Active => this.active;

    /// <summary>Disabled after repeated faults, until the plugin is reloaded.</summary>
    public bool Tripped => this.tripped;

    /// <summary>Grunts silenced this session.</summary>
    public long Suppressed { get; private set; }

    public string LastPath { get; private set; } = string.Empty;

    public uint LastSoundNumber { get; private set; }

    /// <summary>
    /// Distance from the emitter to the matched caster, or -1 under
    /// <see cref="GruntMode.Always"/>, which attributes nothing.
    /// </summary>
    public float LastMatchDistance { get; private set; }

    public DateTime LastAt { get; private set; }

    /// <summary>Casters currently inside their window.</summary>
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

    /// <summary>
    /// Opens the suppression window for a caster whose line is about to play.
    /// </summary>
    /// <remarks>
    /// Called from inside the ActionEffect detour, so it allocates nothing and never
    /// touches the object table — the position comes from the event, and
    /// <see cref="Update"/> keeps it current from there.
    /// </remarks>
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

    /// <summary>
    /// Follows the hook to the config, keeps armed positions current and retires expired
    /// windows. Framework thread only — it reads the object table.
    /// </summary>
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

    /// <summary>Forgets every window. Used when the world changes underneath them.</summary>
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
        // did not. Both writes are cheap; guessing which one is needed is not.
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

        // Rare enough to afford the string. A suppression the user cannot see is
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
