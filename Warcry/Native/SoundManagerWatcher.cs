using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using InteropGenerator.Runtime;

namespace Warcry.Native;

/// <summary>
/// One observed <c>SoundManager::PlaySound</c> call, captured whole.
/// </summary>
/// <remarks>
/// <para><b>Every argument, not a selection.</b> The first version of this struct kept five
/// of the eighteen parameters, which meant the spike spent days guessing at values the game
/// was handing us on every frame — <c>a9</c>, <c>a13</c>, <c>a15</c>, <c>a18</c>,
/// <c>midiNote</c>, <c>soundNumber</c>, <c>speed</c> and the fade flags. A captured row is
/// now a complete, replayable call.</para>
/// <para>Rows are snapshots: written once inside the detour and only read afterwards.</para>
/// </remarks>
public struct SoundLogRow
{
    public DateTime When;
    public string Path;

    // ---- the PlaySound argument tuple, verbatim ----
    public float Volume;
    public uint FadeInDuration;
    public Vector3 Position;
    public float Speed;
    public int A9;
    public uint SoundNumber;
    public bool AutoRelease;
    public SoundVolumeCategory Category;
    public bool A13;
    public int MidiNote;
    public bool A15;
    public bool DefaultFadeOut;
    public bool IsPositional;
    public bool A18;

    /// <summary>Whether the game's own call returned a <c>SoundData*</c>.</summary>
    public bool Played;

    /// <summary>
    /// The local player's world position at the moment of the call.
    /// </summary>
    /// <remarks>
    /// The whole point of this field: the spike passed <em>world</em> coordinates to
    /// <c>PlaySound</c> because that is what it had. If the engine actually wants
    /// listener-relative coordinates, every spike attempt was emitted a couple of hundred
    /// units from the listener and attenuated to nothing — which would explain total
    /// silence in every mode without any fault in the file or the redirect. Comparing this
    /// against <see cref="Position"/> on a real <c>vo_battle</c> line settles it.
    /// </remarks>
    public Vector3 PlayerPosition;

    /// <summary>
    /// Milliseconds since the local player's last ActionEffect, or -1 if none.
    /// For a <c>vo_battle</c> line this is the ground-truth offset between snapshot
    /// and the game playing its own grunt.
    /// </summary>
    /// <remarks>
    /// Observed 2026-08-17: this is NOT one constant. It is fixed per action and ranges
    /// 5-1071 ms — the game's battle voice is driven by the action's animation timeline,
    /// not by the effect packet. So "match the game's grunt" means a per-action table,
    /// which the plugin can learn by watching. See docs/native-spike.md.
    /// </remarks>
    public double MsSinceLocalCast;

    /// <summary>The local player's last action id, so the delay above can be attributed.</summary>
    public uint AfterActionId;

    /// <summary>
    /// How far the emitter was placed from the player. Near zero means the engine is being
    /// handed listener-relative coordinates; roughly the player's distance from the map
    /// origin means world coordinates.
    /// </summary>
    public readonly float EmitterDistanceFromPlayer => Vector3.Distance(this.Position, this.PlayerPosition);

    /// <summary>The full argument tuple on one line, for the clipboard.</summary>
    public readonly string Describe()
        => $"{this.Path}\n" +
           $"  volume={this.Volume:0.000} fadeIn={this.FadeInDuration} speed={this.Speed:0.000}\n" +
           $"  pos=({this.Position.X:0.00}, {this.Position.Y:0.00}, {this.Position.Z:0.00}) " +
           $"positional={this.IsPositional} playerPos=({this.PlayerPosition.X:0.00}, " +
           $"{this.PlayerPosition.Y:0.00}, {this.PlayerPosition.Z:0.00}) " +
           $"distance={this.EmitterDistanceFromPlayer:0.00}\n" +
           $"  a9={this.A9} soundNumber={this.SoundNumber} autoRelease={this.AutoRelease} " +
           $"category={this.Category}\n" +
           $"  a13={this.A13} midiNote={this.MidiNote} a15={this.A15} " +
           $"defaultFadeOut={this.DefaultFadeOut} a18={this.A18}\n" +
           $"  -> returned {(this.Played ? "a SoundData*" : "null")}";
}

/// <summary>
/// Read-only observer of <c>SoundManager::PlaySound</c>.
/// </summary>
/// <remarks>
/// <para>Its original job was to find real <c>.scd</c> paths and measure the game's own
/// grunt latency. Its more important job now is to be the <em>oracle</em>: the game calls
/// this function successfully hundreds of times a minute, so rather than guessing at the
/// eighteen arguments we record a call that demonstrably produced audio and replay it
/// verbatim, changing one thing at a time.</para>
/// <para><b>This function is extremely hot</b> — it fires for every sound in the game,
/// including UI clicks and footsteps. The hook is therefore installed DISABLED and only
/// enabled while the user is actively logging. When enabled, the path filter is applied
/// over the raw UTF-8 bytes so a non-matching sound allocates nothing.</para>
/// <para>This is also the hook grunt suppression will need later (zero
/// <c>soundData->Volume</c> for the caster's own vo_battle line), so it is not
/// throwaway spike code.</para>
/// </remarks>
public sealed unsafe class SoundManagerWatcher : IDisposable
{
    public const int Capacity = 120;
    private const int FaultLimit = 10;

    private readonly Hook<SoundManager.Delegates.PlaySound>? hook;
    private readonly IPluginLog log;
    private readonly Func<(long Ticks, uint ActionId)> lastLocalCast;
    private readonly Func<Vector3> playerPosition;

    private readonly SoundLogRow[] rows = new SoundLogRow[Capacity];
    private int next;
    private int faults;
    private volatile bool tripped;

    private byte[] filterBytes = "vo_"u8.ToArray();

    public SoundManagerWatcher(
        IGameInteropProvider interop,
        IPluginLog log,
        Func<(long, uint)> lastLocalCast,
        Func<Vector3> playerPosition)
    {
        this.log = log;
        this.lastLocalCast = lastLocalCast;
        this.playerPosition = playerPosition;

        try
        {
            this.HookAddress = SoundManager.Addresses.PlaySound.Value;
            this.hook = interop.HookFromAddress<SoundManager.Delegates.PlaySound>(
                this.HookAddress, this.PlaySoundDetour);

            // Installed but INERT. This function is far too hot to sit in by default.
            this.hook.Disable();
            log.Information("SoundManagerWatcher: hooked PlaySound at 0x{Address:X} (disabled)", this.HookAddress);
        }
        catch (Exception ex)
        {
            this.hook = null;
            log.Error(ex, "SoundManagerWatcher: could not resolve SoundManager.PlaySound.");
        }
    }

    public nint HookAddress { get; }

    public bool Installed => this.hook is not null;

    public bool Tripped => this.tripped;

    public int Count { get; private set; }

    public long TotalSeen { get; private set; }

    public long TotalMatched { get; private set; }

    private bool logging;

    /// <summary>Enable the hook only while actually observing.</summary>
    public bool Logging
    {
        get => this.logging;
        set
        {
            if (this.logging == value || this.hook is null || this.tripped)
            {
                return;
            }

            this.logging = value;
            if (value)
            {
                this.hook.Enable();
            }
            else
            {
                this.hook.Disable();
            }
        }
    }

    public string Filter
    {
        get => System.Text.Encoding.UTF8.GetString(this.filterBytes);
        set => this.filterBytes = System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant());
    }

    private SoundData* PlaySoundDetour(
        SoundManager* thisPtr, CStringPointer path, float volume, uint fadeInDuration,
        float posX, float posY, float posZ, float speed, int a9, uint soundNumber,
        bool autoRelease, SoundVolumeCategory volumeCategory, bool a13, int midiNote,
        bool a15, bool defaultFadeOut, bool isPositional, bool a18)
    {
        var result = this.hook!.OriginalDisposeSafe(
            thisPtr, path, volume, fadeInDuration, posX, posY, posZ, speed, a9, soundNumber,
            autoRelease, volumeCategory, a13, midiNote, a15, defaultFadeOut, isPositional, a18);

        if (this.tripped || !this.logging)
        {
            return result;
        }

        try
        {
            this.TotalSeen++;

            if (!path.HasValue)
            {
                return result;
            }

            // Filter over raw UTF-8 so a non-match costs no allocation at all.
            var span = path.AsSpan();
            if (this.filterBytes.Length > 0 && !ContainsAsciiIgnoreCase(span, this.filterBytes))
            {
                return result;
            }

            this.TotalMatched++;

            var (lastTicks, lastActionId) = this.lastLocalCast();
            var delta = lastTicks > 0
                ? (Stopwatch.GetTimestamp() - lastTicks) * 1000.0 / Stopwatch.Frequency
                : -1.0;

            this.rows[this.next] = new SoundLogRow
            {
                When = DateTime.Now,
                Path = path.ToString(),
                Volume = volume,
                FadeInDuration = fadeInDuration,
                Position = new Vector3(posX, posY, posZ),
                Speed = speed,
                A9 = a9,
                SoundNumber = soundNumber,
                AutoRelease = autoRelease,
                Category = volumeCategory,
                A13 = a13,
                MidiNote = midiNote,
                A15 = a15,
                DefaultFadeOut = defaultFadeOut,
                IsPositional = isPositional,
                A18 = a18,
                Played = result != null,
                PlayerPosition = this.playerPosition(),
                MsSinceLocalCast = delta,
                AfterActionId = lastActionId,
            };

            this.next = (this.next + 1) % Capacity;
            if (this.Count < Capacity)
            {
                this.Count++;
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "SoundManagerWatcher detour threw");
            if (Interlocked.Increment(ref this.faults) > FaultLimit)
            {
                this.tripped = true;
                this.logging = false;
                this.hook?.Disable();
                this.log.Error("SoundManagerWatcher: disabled after {Limit} faults.", FaultLimit);
            }
        }

        return result;
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

    /// <summary>Newest first.</summary>
    public SoundLogRow At(int index)
    {
        var start = (this.next - 1 + Capacity) % Capacity;
        return this.rows[(start - index + (Capacity * 2)) % Capacity];
    }

    public void Clear()
    {
        Array.Clear(this.rows);
        this.next = 0;
        this.Count = 0;
        this.TotalSeen = 0;
        this.TotalMatched = 0;
    }

    public void Dispose()
    {
        this.hook?.Disable();
        this.hook?.Dispose();
    }
}
