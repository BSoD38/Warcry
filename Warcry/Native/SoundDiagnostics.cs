using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace Warcry.Native;

/// <summary>
/// Reads what the game itself thinks happened, instead of inferring it from a return value
/// or from what a human heard.
/// </summary>
/// <remarks>
/// <para>The original spike ran for five days on two signals: a non-null <c>SoundData*</c>
/// and whether a grunt was audible. Both turned out to be noise. Everything below was
/// available the whole time — <c>SoundData.SoundResourceHandle</c> in particular, which
/// answers "did the redirect apply" and "did the file load" as a plain readout.</para>
/// <para>All reads are guarded: a null or torn pointer produces a line of text, never an
/// exception into the draw loop.</para>
/// </remarks>
public static unsafe class SoundDiagnostics
{
    /// <summary>Hard cap on the active-list walk, so a corrupt link cannot hang the frame.</summary>
    private const int WalkLimit = 512;

    /// <summary>Buses worth showing. The full enum runs to 20 and most are never used here.</summary>
    private static readonly SoundBus[] InterestingBuses =
    [
        SoundBus.Music, SoundBus.SE, SoundBus.Voice, SoundBus.System, SoundBus.CommonVFX,
    ];

    /// <summary>
    /// The engine's own mixer state. If <c>Disabled</c> is set or the bus is muted, nothing
    /// below the mixer matters and every other test in the tab is meaningless.
    /// </summary>
    public static void DescribeManager(ICollection<string> into)
    {
        var manager = SoundManager.Instance();
        if (manager == null)
        {
            into.Add("SoundManager.Instance() is null — the engine is not up.");
            return;
        }

        try
        {
            into.Add($"engine     disabled={manager->Disabled}  master={manager->MasterVolume:0.000}  " +
                     $"active={manager->ActiveVolume:0.000}");
            into.Add($"window     inactive={manager->WindowInactive}  " +
                     $"playWhenInactive={manager->PlaySoundsWhenWindowInactive}");

            var busMuted = manager->BusMuted;
            var line = "buses      ";
            foreach (var bus in InterestingBuses)
            {
                var index = (int)bus;
                var muted = index >= 0 && index < busMuted.Length && busMuted[index];
                line += $"{bus}={manager->GetEffectiveVolume(bus):0.00}{(muted ? " MUTED" : string.Empty)}  ";
            }

            into.Add(line.TrimEnd());
            into.Add($"pool       {CountActive()} sound(s) active");
        }
        catch (Exception ex)
        {
            into.Add($"reading SoundManager threw: {ex.Message}");
        }
    }

    /// <summary>Number of <c>SoundData</c> currently on the engine's active list.</summary>
    public static int CountActive()
    {
        var manager = SoundManager.Instance();
        if (manager == null)
        {
            return -1;
        }

        var count = 0;
        for (var node = (ISoundData*)manager->ActiveSoundDataListHead;
             node != null && count < WalkLimit;
             node = node->Next)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Position of <paramref name="needle"/> on the engine's active list.
    /// </summary>
    /// <remarks>
    /// Membership is a fact; <c>IsPlaying()</c> sampled at 25 ms is a guess about a signal
    /// that may be shorter than the sampling interval. A sound that never appears here was
    /// never accepted by the engine, whatever the return value said.
    /// </remarks>
    public static bool FindInActiveList(SoundData* needle, out int index, out int total)
    {
        index = -1;
        total = 0;

        var manager = SoundManager.Instance();
        if (manager == null || needle == null)
        {
            return false;
        }

        for (var node = (ISoundData*)manager->ActiveSoundDataListHead;
             node != null && total < WalkLimit;
             node = node->Next)
        {
            if (node == (ISoundData*)needle && index < 0)
            {
                index = total;
            }

            total++;
        }

        return index >= 0;
    }

    /// <summary>
    /// Everything the engine knows about one sound, including its resource handle.
    /// </summary>
    /// <param name="expectedBytes">
    /// Length of the file we believe should have been served, or 0 if unknown. When the
    /// handle reports this many bytes, the redirect demonstrably reached the game — that is
    /// the single most useful fact this whole tab can produce.
    /// </param>
    public static void DescribeSoundData(SoundData* sd, ICollection<string> into, long expectedBytes = 0)
    {
        if (sd == null)
        {
            into.Add("SoundData is null.");
            return;
        }

        try
        {
            var name = sd->GetFileName();
            into.Add($"sound      file='{(name.HasValue ? name.ToString() : "<none>")}'");
            into.Add($"           playing={sd->IsPlaying()}  loadingResource={sd->GetIsLoadingSoundResource()}  " +
                     $"active={sd->IsActive}  fadingOut={sd->GetIsFadingOut()}");
            into.Add($"           elapsed={sd->GetElapsedTime():0.000}s  volume={sd->GetVolume():0.000}  " +
                     $"speed={sd->GetSpeed():0.00}  soundNumber={sd->GetSoundNumber()}");
            into.Add($"           category={sd->VolumeCategory}  positional={sd->GetIsPositional()}  " +
                     $"pos=({sd->GetPositionX():0.0}, {sd->GetPositionY():0.0}, {sd->GetPositionZ():0.0})");
            into.Add($"           isLocalPlayer={sd->IsLocalPlayer}  " +
                     $"autoRelease={sd->GetIsAutoReleaseEnabled()}  fadeIn={sd->GetFadeInDuration()}");

            if (FindInActiveList(sd, out var index, out var total))
            {
                into.Add($"           ON the active list at {index} of {total}");
            }
            else
            {
                into.Add($"           NOT on the active list ({total} other sound(s) are)");
            }
        }
        catch (Exception ex)
        {
            into.Add($"reading SoundData threw: {ex.Message}");
            return;
        }

        DescribeHandle(sd->SoundResourceHandle, into, expectedBytes);
    }

    /// <summary>
    /// The resource handle behind a sound — the readout that settles whether a Penumbra
    /// redirect reached the game.
    /// </summary>
    /// <remarks>
    /// <c>LoadState</c> and <c>LastIOResult</c> have no published enum, so they are printed
    /// raw: record what a known-good stock sound reports and compare. <c>GetLength()</c>
    /// needs no interpretation at all — bytes are bytes.
    /// </remarks>
    public static void DescribeHandle(SoundResourceHandle* handle, ICollection<string> into, long expectedBytes = 0)
    {
        if (handle == null)
        {
            into.Add("handle     NULL — the engine never obtained a resource for this sound.");
            return;
        }

        try
        {
            string fileName;
            try
            {
                fileName = handle->FileName.ToString();
            }
            catch
            {
                fileName = "<unreadable>";
            }

            var data = handle->GetData();
            var length = (long)handle->GetLength();

            into.Add($"handle     name='{fileName}'");
            into.Add($"           fileSize={handle->FileSize}  length={length}  data={(data == null ? "NULL" : "present")}");
            into.Add($"           loadState={handle->LoadState}  readState={handle->ReadState}  " +
                     $"lastIO={handle->LastIOResult}  refCount={handle->RefCount}  soundRefs={handle->SoundDataRefCount}");

            if (expectedBytes > 0)
            {
                if (length == expectedBytes)
                {
                    into.Add($"           MATCH — {length} bytes is exactly the file we wrote. " +
                             "Our bytes are in the engine.");
                }
                else
                {
                    into.Add($"           MISMATCH — we wrote {expectedBytes} bytes, the engine holds {length}.");

                    // Say what this usually means. A resource handle is cached per path for
                    // the life of the game process, so the commonest cause by far is that
                    // this path was loaded earlier with different content and the engine
                    // never re-read it — not that the redirect failed.
                    into.Add("           Most likely a STALE CACHED HANDLE: this path was already loaded " +
                             "earlier in");
                    into.Add("           this GAME session (plugin reloads do not clear it), so the engine " +
                             "kept the old");
                    into.Add("           bytes and ignored the new redirect. Use a path these bytes have " +
                             "never used.");
                }
            }
        }
        catch (Exception ex)
        {
            into.Add($"reading the resource handle threw: {ex.Message}");
        }
    }
}
