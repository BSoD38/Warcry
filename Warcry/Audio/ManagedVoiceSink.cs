using System;
using System.Threading;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Warcry.Audio;

/// <summary>
/// The default sink: NAudio mixer out to the default device, with gain taken from the
/// game's own volume sliders so it behaves like game audio rather than a separate app.
/// </summary>
/// <remarks>
/// <para>Threading: <see cref="TryPlay"/> runs on the game main thread; the mixer pulls
/// on NAudio's own thread. <c>MixingSampleProvider</c> locks its source list internally,
/// and that lock is held only for the duration of a mix, so calling AddMixerInput from
/// the game thread is safe and bounded.</para>
/// <para>Wine/Linux: WASAPI paths are known to fail under Proton, so branch to
/// DirectSound. The native sink, if it ever lands, is unaffected by this — a genuine
/// argument in its favour.</para>
/// </remarks>
public sealed class ManagedVoiceSink : IVoiceSink
{
    private readonly IPluginLog log;
    private readonly GameVolume volume;
    private readonly Configuration config;

    private IWavePlayer? output;
    private MixingSampleProvider? mixer;

    /// <summary>
    /// Interlocked only: incremented on the game thread in <see cref="TryPlay"/>,
    /// decremented on NAudio's output thread in <see cref="OnInputEnded"/>
    /// (MixerInputEnded is raised from inside MixingSampleProvider.Read). A plain
    /// <c>++</c>/<c>--</c> pair here loses updates, and a lost decrement is permanent —
    /// enough of them and the cap check refuses every line for the rest of the session.
    /// </summary>
    private int activeVoices;

    public ManagedVoiceSink(IPluginLog log, GameVolume volume, Configuration config)
    {
        this.log = log;
        this.volume = volume;
        this.config = config;

        try
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(TestTone.SampleRate, 2);
            this.mixer = new MixingSampleProvider(format) { ReadFully = true };
            this.mixer.MixerInputEnded += this.OnInputEnded;

            var wine = SafeIsWine();
            this.output = wine
                ? new DirectSoundOut(100)
                : new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 };

            this.output.Init(this.mixer);
            this.output.Play();

            this.Status = wine
                ? "Managed (NAudio / DirectSound — Wine detected)"
                : "Managed (NAudio / WaveOut)";
            this.Available = true;
            log.Information("ManagedVoiceSink ready: {Status}", this.Status);
        }
        catch (Exception ex)
        {
            this.Status = $"Audio device unavailable: {ex.Message}";
            this.Available = false;
            log.Error(ex, "ManagedVoiceSink: could not open an output device. Audio disabled.");
        }
    }

    public string Name => "Managed";

    public string Status { get; private set; }

    public bool Available { get; private set; }

    public int ActiveVoices => Volatile.Read(ref this.activeVoices);

    public bool TryPlay(in VoiceRequest request)
    {
        if (!this.Available || this.mixer is null)
        {
            return false;
        }

        if (Volatile.Read(ref this.activeVoices) >= this.config.MaxConcurrent)
        {
            // Hard cap. Not a taste call: the game's SoundData pool is 256 entries shared
            // with the whole client, and the Voice bus has only 5 tracks.
            return false;
        }

        var gameGain = this.volume.GainFor(request.SoundCategory, this.config.UseVoiceSliderNotSe);
        var gain = request.Gain * this.config.MasterGain * gameGain;

        if (gain <= 0.0001f)
        {
            // Muted, or the sliders multiply out to silence. Don't burn a voice slot.
            return false;
        }

        try
        {
            // mono -> gain -> pan -> stereo, matching the mixer's format.
            var volumeStage = new VolumeSampleProvider(request.CreateSource(request.Speed))
            {
                Volume = Math.Clamp(gain, 0f, 2f),
            };

            var panStage = new PanningSampleProvider(volumeStage)
            {
                PanStrategy = new SinPanStrategy(),
                Pan = 0f, // Camera-relative azimuth arrives with remote players in v2.
            };

            this.mixer.AddMixerInput(panStage);
            Interlocked.Increment(ref this.activeVoices);
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "ManagedVoiceSink: AddMixerInput failed");
            return false;
        }
    }

    public void Update()
    {
        // Nothing per-frame yet. Moving-caster tracking arrives with v2.
    }

    private void OnInputEnded(object? sender, SampleProviderEventArgs e)
    {
        // Decrement with a floor at zero. A plain Interlocked.Decrement could go negative
        // if the mixer ever raises the event for an input we did not count.
        int current;
        do
        {
            current = Volatile.Read(ref this.activeVoices);
            if (current <= 0)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref this.activeVoices, current - 1, current) != current);
    }

    private static bool SafeIsWine()
    {
        try
        {
            return Util.IsWine();
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        // An orphaned IWavePlayer survives plugin unload and keeps making noise.
        // That is a classic Dalamud bug report; tear down in order.
        try
        {
            if (this.mixer is not null)
            {
                this.mixer.MixerInputEnded -= this.OnInputEnded;
                this.mixer.RemoveAllMixerInputs();
            }

            this.output?.Stop();
            this.output?.Dispose();
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "ManagedVoiceSink: error during teardown");
        }
        finally
        {
            this.output = null;
            this.mixer = null;
            this.Available = false;
        }
    }
}
