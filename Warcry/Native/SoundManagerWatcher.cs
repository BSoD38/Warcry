using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using InteropGenerator.Runtime;

namespace Warcry.Native;

public readonly struct SoundLogRow
{
    public readonly DateTime When;
    public readonly string Path;
    public readonly float Volume;
    public readonly SoundVolumeCategory Category;
    public readonly bool IsPositional;
    public readonly Vector3 Position;
    public readonly bool Played;

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
    public readonly double MsSinceLocalCast;

    /// <summary>The local player's last action id, so the delay above can be attributed.</summary>
    public readonly uint AfterActionId;

    public SoundLogRow(
        DateTime when, string path, float volume, SoundVolumeCategory category,
        bool isPositional, Vector3 position, bool played, double msSinceLocalCast,
        uint afterActionId)
    {
        this.AfterActionId = afterActionId;
        this.When = when;
        this.Path = path;
        this.Volume = volume;
        this.Category = category;
        this.IsPositional = isPositional;
        this.Position = position;
        this.Played = played;
        this.MsSinceLocalCast = msSinceLocalCast;
    }
}

/// <summary>
/// Read-only observer of <c>SoundManager::PlaySound</c>. Day 1 of the native-audio spike.
/// </summary>
/// <remarks>
/// <para>Answers three things at once, without writing a single byte of .scd: what real
/// game-path .scd files look like (templates for the writer), whether the signature
/// resolves at all, and — for <c>vo_battle</c> lines — exactly how long after snapshot
/// the game plays its own battle grunt.</para>
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

    private readonly SoundLogRow[] rows = new SoundLogRow[Capacity];
    private int next;
    private int faults;
    private volatile bool tripped;

    private byte[] filterBytes = "vo_"u8.ToArray();

    public SoundManagerWatcher(IGameInteropProvider interop, IPluginLog log, Func<(long, uint)> lastLocalCast)
    {
        this.log = log;
        this.lastLocalCast = lastLocalCast;

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

            this.rows[this.next] = new SoundLogRow(
                DateTime.Now, path.ToString(), volume, volumeCategory,
                isPositional, new Vector3(posX, posY, posZ), result != null, delta, lastActionId);

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
