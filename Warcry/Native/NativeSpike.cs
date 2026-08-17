using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using NAudio.Wave;
using Warcry.Audio;

namespace Warcry.Native;

/// <summary>Which of the two unknowns an attempt is testing.</summary>
public enum SpikeMode
{
    /// <summary>Our file, synthetic path. Both unknowns at once — the original attempt.</summary>
    AuthoredPcm,

    /// <summary>As above but tagged MS-ADPCM, to separate container from codec rejection.</summary>
    AuthoredAdpcmTag,

    /// <summary>
    /// CONTROL: a byte-for-byte copy of a real game SCD, served from a synthetic path.
    /// The file is definitionally valid, so silence here indicts the <em>path</em>.
    /// </summary>
    VerbatimTemplate,

    /// <summary>
    /// CONTROL: our authored file, served from a real game path that exists in the index
    /// but has not been loaded this session. Plumbing is definitionally sound, so silence
    /// here indicts the <em>writer</em>.
    /// </summary>
    AuthoredOnRealPath,

    /// <summary>
    /// A real game SCD with ONLY audio entry 0's header and payload overwritten — every
    /// other byte identical, file length unchanged. Isolates "our audio entry is wrong"
    /// from "our container rebuild is wrong".
    /// </summary>
    InPlaceAudioSwap,

    /// <summary>
    /// Every audio entry overwritten with the same payload, so the engine's random pick
    /// always lands on ours. Sidesteps the layout/randomisation tables entirely.
    /// </summary>
    InPlaceFillAll,

    /// <summary>
    /// Template with its sound/audio counts forced to 1 and entry 0 given all the freed
    /// space. Deterministic selection, and long enough to be unmistakable.
    /// </summary>
    ForceSingleEntry,

    /// <summary>
    /// NEGATIVE CONTROL. Plays a path that does not exist and has no redirect at all.
    /// </summary>
    /// <remarks>
    /// Should be silent. If it produces grunts, then PlaySound is replaying whatever was
    /// previously in the recycled SoundData pool slot, and EVERY earlier "it played"
    /// result in this spike is void — including mode A. This should have been the first
    /// test written, not the last.
    /// </remarks>
    NegativeControl,
}

/*
 * REMOVED: AuthoredAbsoluteNoPenumbra — handing PlaySound an absolute filesystem path.
 *
 * Tested 2026-08-17 and it HARD-CRASHES the client:
 *   Client::System::Resource::ResourceGraph.FindResourceHandle+0x23, C0000005
 *   via SoundManager.PlaySound -> LoadSoundDataScd -> ResourceManager.GetResourceAsync
 *
 * The resource graph is indexed by ResourceCategory, and the category is derived from the
 * leading segment of the path. "C:\Users\..." produces no valid category, so the lookup
 * indexes out of bounds. This is not a missing feature — a game-relative path is
 * structurally required, which is why a redirect mechanism (Penumbra) is unavoidable.
 *
 * Do not reintroduce this mode.
 */

/// <summary>
/// The experiment: can the game's own engine play a file we wrote?
/// </summary>
/// <remarks>
/// <para>Every step reports separately, and the returned <c>SoundData*</c> is retained and
/// polled rather than trusted. A non-null return only means a pool slot was allocated —
/// <c>SoundData</c> has an <c>IsLoadingSoundResource</c> flag, so loading is asynchronous
/// and the pointer says nothing about whether the file parsed.</para>
/// <para>Go/no-go criteria: docs/PLAN.md section 6.</para>
/// </remarks>
public sealed unsafe class NativeSpike
{
    private const uint SampleRate = 44100;

    /// <summary>Structural templates, tried in order. Read from the user's install, never shipped.</summary>
    private static readonly string[] TemplateCandidates =
    [
        "sound/voice/Vo_Battle/Vo_Battle_PC_ros_Ma_fr.scd",
        "sound/voice/Vo_Battle/Vo_Battle_PC_mid_Ma_fr.scd",
        "sound/voice/Vo_Battle/Vo_Battle_PC_mid_Ma_en.scd",
        "sound/voice/Vo_Battle/Vo_Battle_PC_mid_Ma_ja.scd",
    ];

    /// <summary>
    /// Real, indexed paths for the AuthoredOnRealPath control. Chosen as races the user is
    /// unlikely to have encountered this session, so the resource handle is not already
    /// cached — a cached path ignores a new redirect entirely.
    /// </summary>
    private static readonly string[] RealPathCandidates =
    [
        // CONFIRMED to resolve by the P probe. Deliberately Japanese/German voices for
        // races the player is not, so the handle is almost certainly not already cached —
        // a cached path keeps its handle and silently ignores a new redirect.
        // (The probe also showed only _Ma_ resolves; female battle voices use a different
        // token, which matters for grunt suppression later.)
        "sound/voice/Vo_Battle/Vo_Battle_PC_mid_Ma_ja.scd",
        "sound/voice/Vo_Battle/Vo_Battle_PC_hil_Ma_ja.scd",
        "sound/voice/Vo_Battle/Vo_Battle_PC_ele_Ma_ja.scd",
        "sound/voice/Vo_Battle/Vo_Battle_PC_mid_Ma_de.scd",
    ];

    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly PenumbraBridge penumbra;
    private readonly string cacheDir;

    private int attempt;

    // Retained SoundData under observation.
    private nint tracked;
    private long nextPoll;
    private long trackUntil;
    private int polls;
    private int playingSamples;
    private int loadingSamples;
    private bool everPlayed;

    public NativeSpike(IDataManager data, IPluginLog log, PenumbraBridge penumbra, string configDirectory)
    {
        this.data = data;
        this.log = log;
        this.penumbra = penumbra;
        this.cacheDir = Path.Combine(configDirectory, ".cache", "scd");
        Directory.CreateDirectory(this.cacheDir);
    }

    public List<string> Report { get; } = [];

    public bool Tracking => this.tracked != 0;

    /// <summary>
    /// <c>PlaySound</c>'s soundNumber. We have only ever passed 0; if it selects a
    /// specific entry rather than meaning "any", it is the answer to the randomisation.
    /// </summary>
    public uint SoundNumber { get; set; }

    /// <summary>The path most recently played, so it can be replayed warm.</summary>
    public string LastPath { get; private set; } = string.Empty;

    /// <summary>
    /// Let the engine own the SoundData, as production would.
    /// </summary>
    /// <remarks>
    /// The spike has been passing <c>false</c> so it can retain and poll the pointer, then
    /// force <c>Stop(0)</c> + <c>ReleaseSoundData</c> on the next press. That churns a
    /// 256-entry pool shared with the whole game and is the prime suspect for the
    /// intermittent silence. With <c>true</c> the engine reclaims the slot itself — the
    /// normal path — but the pointer must not be retained, so polling is unavailable.
    /// </remarks>
    public bool AutoRelease { get; set; }

    /// <summary>
    /// Plays the previous path again WITHOUT rewriting or re-redirecting it.
    /// </summary>
    /// <remarks>
    /// The decisive test for the async-load hypothesis. Resource loading is asynchronous,
    /// so a path being played for the first time may not have finished reading — every
    /// spike attempt so far used a fresh path and was therefore always cold. If a warm
    /// replay is reliable, nothing is wrong with our file and the production design simply
    /// has to pre-warm each clip once, which is what PLAN.md 5.6 already specifies.
    /// </remarks>
    public void Replay(System.Numerics.Vector3 position, SoundVolumeCategory category)
    {
        if (this.LastPath.Length == 0)
        {
            this.Say("Nothing to replay — run an attempt first.");
            return;
        }

        this.ReleaseTracked();
        this.Say($"--- replay (warm) {this.LastPath}, soundNumber {this.SoundNumber} ---");
        this.Play(this.LastPath, position, category);
    }

    public void Run(SpikeMode mode, System.Numerics.Vector3 position, SoundVolumeCategory category)
    {
        this.ReleaseTracked();
        this.Report.Clear();
        this.attempt++;

        this.Say($"=== attempt {this.attempt} — {mode}, soundNumber {this.SoundNumber} ===");

        if (mode == SpikeMode.NegativeControl)
        {
            // No file, no redirect, a path the index has never heard of. Must be silent.
            var bogus = $"sound/vfx/warcry/nonexistent/none{this.attempt:D4}.scd";
            this.Say("     no file written, no redirect registered — this MUST be silent");
            this.Say($"     {bogus}");
            this.Play(bogus, position, category);
            return;
        }

        // ---- 1. template ----
        var templateBytes = this.LoadFirst(TemplateCandidates, out var templateUsed);
        if (templateBytes is null)
        {
            this.Say("FAIL 1/5 — no template SCD readable. Log a real vo_battle path from the Game sounds tab.");
            return;
        }

        this.Say($"OK 1/5 template {templateUsed} ({templateBytes.Length} bytes)");

        // ---- 2. parse ----
        if (!ScdWriter.TryParse(templateBytes, out var template, out var parseError) || template is null)
        {
            this.Say($"FAIL 2/5 — parse: {parseError}");
            return;
        }

        this.Say($"OK 2/5 parsed: {template.SoundOffsets.Length} sound, {template.AudioOffsets.Length} audio entries");

        // ---- 3. produce the payload ----
        byte[] payload;
        try
        {
            switch (mode)
            {
                case SpikeMode.VerbatimTemplate:
                    payload = templateBytes;
                    break;

                case SpikeMode.InPlaceAudioSwap:
                    payload = ScdWriter.SwapFirstAudioInPlace(
                        template, RenderTestTone(), SampleRate, ScdWriter.FormatPcm, out var note);
                    this.Say($"     {note}");
                    break;

                case SpikeMode.ForceSingleEntry:
                    // Two seconds of loud alternating alarm — unmistakable against a grunt.
                    payload = ScdWriter.ForceSingleAudioEntry(
                        template, RenderAlarm(2.0f), SampleRate, ScdWriter.FormatPcm, out var singleNote);
                    this.Say($"     {singleNote}");
                    this.Say("     payload: 440/880 Hz alarm, 85% full scale, no decay");
                    break;

                case SpikeMode.InPlaceFillAll:
                    payload = ScdWriter.SwapAllAudioInPlace(
                        template, RenderTestTone(), SampleRate, ScdWriter.FormatPcm, out var fillNote);
                    this.Say($"     {fillNote}");
                    break;

                case SpikeMode.AuthoredAdpcmTag:
                    payload = ScdWriter.BuildPcm(template, RenderTestTone(), SampleRate, ScdWriter.FormatMsAdPcm);
                    break;

                case SpikeMode.AuthoredOnRealPath:
                    // The loud alarm here too, so B's result is judgeable by ear. B is now
                    // the decisive test: it shadows a REAL indexed path, so if synthetic
                    // paths are the problem this is what proves it.
                    payload = ScdWriter.ForceSingleAudioEntry(
                        template, RenderAlarm(2.0f), SampleRate, ScdWriter.FormatPcm, out var realNote);
                    this.Say($"     {realNote}");
                    this.Say("     payload: 440/880 Hz alarm, 85% full scale");
                    break;

                default:
                    payload = ScdWriter.BuildPcm(template, RenderTestTone(), SampleRate, ScdWriter.FormatPcm);
                    break;
            }
        }
        catch (Exception ex)
        {
            this.Say($"FAIL 3/5 — building the payload: {ex.Message}");
            return;
        }

        var localPath = Path.Combine(this.cacheDir, $"spike_{this.attempt}.scd");
        try
        {
            File.WriteAllBytes(localPath, payload);
        }
        catch (Exception ex)
        {
            this.Say($"FAIL 3/5 — writing: {ex.Message}");
            return;
        }

        this.Say(mode == SpikeMode.VerbatimTemplate
            ? $"OK 3/5 copied the template verbatim ({payload.Length} bytes) — this file is known-good"
            : $"OK 3/5 wrote {payload.Length} bytes");

        // ---- 4. choose a path and redirect ----
        string gamePath;

        if (mode == SpikeMode.AuthoredOnRealPath)
        {
            // Must exist in the index; GetFile resolving proves it does.
            if (this.LoadFirst(RealPathCandidates, out var realPath) is null)
            {
                this.Say("FAIL 4/5 — no unused real vo_battle path resolved, so this control cannot run.");
                return;
            }

            gamePath = realPath;
            this.Say($"     using REAL indexed path {gamePath}");
            this.Say("     (registered lowercased — Penumbra normalises game paths)");
        }
        else
        {
            // Fresh every attempt: a path the game has already requested keeps its cached
            // handle and silently ignores a new redirect.
            gamePath = $"sound/vfx/warcry/spike/s{this.attempt:D4}.scd";
        }

        if (!this.penumbra.PenumbraAvailable)
        {
            this.Say("FAIL 4/5 — Penumbra not loaded; the game cannot see our file.");
            return;
        }

        var code = this.penumbra.Redirect(new Dictionary<string, string> { [gamePath] = localPath });
        if (code is null)
        {
            this.Say($"FAIL 4/5 — redirect failed: {this.penumbra.LastError}");
            return;
        }

        this.Say(code == 0 ? $"OK 4/5 redirected {gamePath}" : $"WARN 4/5 Penumbra returned {code}; continuing");

        this.Play(gamePath, position, category);
    }

    /// <summary>Step 5 for every mode: play, retain the result, and watch it load.</summary>
    private void Play(string path, System.Numerics.Vector3 position, SoundVolumeCategory category)
    {
        this.LastPath = path;

        try
        {
            var manager = SoundManager.Instance();
            if (manager == null)
            {
                this.Say("FAIL 5/5 — SoundManager.Instance() was null.");
                return;
            }

            var result = manager->PlaySound(
                path,
                1.0f, 0u,
                position.X, position.Y, position.Z,
                1.0f, 0,
                this.SoundNumber,
                this.AutoRelease,
                category,
                false, -1, false, false,
                true,           // isPositional
                false);

            if (result == null)
            {
                this.Say("FAIL 5/5 — PlaySound returned null (no pool slot).");
                return;
            }

            if (this.AutoRelease)
            {
                // Must not retain a pointer the engine may recycle at any moment.
                this.Say("OK 5/5 got a SoundData*, autoRelease ON — engine owns it, no polling. Judge by ear.");
                this.LastVerdict = "autoRelease ON — not measured, listen instead";
                return;
            }

            this.Say("OK 5/5 got a SoundData* — this only means a slot was allocated. Watching it...");

            this.tracked = (nint)result;
            this.polls = 0;
            this.playingSamples = 0;
            this.loadingSamples = 0;
            this.everPlayed = false;
            this.nextPoll = Stopwatch.GetTimestamp();
            // 1.5s is ample for a 1s payload and less likely to be cut short.
            this.trackUntil = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 3 / 2);
        }
        catch (Exception ex)
        {
            this.Say($"FAIL 5/5 — PlaySound threw: {ex.Message}");
        }
    }

    /// <summary>
    /// CONTROL: registers a redirect and does NOT call PlaySound.
    /// </summary>
    /// <remarks>
    /// Tests whether the grunts come from Penumbra rather than from us. Changing a mod set
    /// makes Penumbra invalidate and redraw affected characters, and a redraw reloads
    /// character sound (VfxContainer.LoadCharacterSound). If a grunt happens here — with
    /// no PlaySound call at all — then every earlier "it played" result, mode A included,
    /// was this and our audio has never once been heard.
    /// </remarks>
    public void RedirectOnly()
    {
        this.ReleaseTracked();
        this.Report.Clear();
        this.attempt++;

        this.Say($"=== attempt {this.attempt} — REDIRECT ONLY, no PlaySound ===");

        var templateBytes = this.LoadFirst(TemplateCandidates, out _);
        if (templateBytes is null)
        {
            this.Say("FAIL — no template SCD readable.");
            return;
        }

        if (!ScdWriter.TryParse(templateBytes, out var template, out var err) || template is null)
        {
            this.Say($"FAIL — template parse: {err}");
            return;
        }

        var payload = ScdWriter.ForceSingleAudioEntry(
            template, RenderAlarm(2.0f), SampleRate, ScdWriter.FormatPcm, out _);

        var localPath = Path.Combine(this.cacheDir, $"spike_{this.attempt}.scd");
        File.WriteAllBytes(localPath, payload);

        var gamePath = $"sound/vfx/warcry/spike/s{this.attempt:D4}.scd";
        var code = this.penumbra.Redirect(new Dictionary<string, string> { [gamePath] = localPath });

        this.Say($"registered {gamePath} (code {code}). NO PlaySound was called.");
        this.Say("If you hear a grunt now, it is Penumbra redrawing your character, not us.");
        this.LastVerdict = "redirect only — any sound you hear is not from PlaySound";
    }

    /// <summary>
    /// Logs which real <c>Vo_Battle</c> paths actually resolve in this installation.
    /// </summary>
    /// <remarks>
    /// Mode B needs a path the index genuinely contains; the earlier guesses did not
    /// resolve, which is why it failed at step 4 and never produced a result. B is the
    /// test that distinguishes "synthetic paths do not work" from "our file is wrong".
    /// </remarks>
    public void ProbeRealPaths()
    {
        this.Report.Clear();
        this.Say("=== probing real Vo_Battle paths ===");

        string[] races = ["mid", "hil", "ele", "lal", "miq", "rog", "aur", "ros", "vie"];
        string[] sexes = ["Ma", "Fe"];
        string[] languages = ["fr", "en", "ja", "de"];

        var found = 0;
        foreach (var race in races)
        {
            foreach (var sex in sexes)
            {
                foreach (var language in languages)
                {
                    var path = $"sound/voice/Vo_Battle/Vo_Battle_PC_{race}_{sex}_{language}.scd";
                    try
                    {
                        var file = this.data.GetFile(path);
                        if (file?.Data is { Length: > 0 })
                        {
                            found++;
                            if (found <= 12)
                            {
                                this.Say($"  OK {path} ({file.Data.Length} bytes)");
                            }
                        }
                    }
                    catch
                    {
                        // Not present; keep probing.
                    }
                }
            }
        }

        this.Say($"{found} of {races.Length * sexes.Length * languages.Length} candidates resolved.");
        this.LastVerdict = $"{found} real Vo_Battle paths exist";
    }

    /// <summary>Polls the retained SoundData so we see whether the resource ever loaded.</summary>
    public void Update()
    {
        if (this.tracked == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();

        if (now >= this.nextPoll)
        {
            // 25 ms, not 250: a 60 ms sound finishes between two coarse samples, which
            // made earlier runs look like nothing ever played.
            this.nextPoll = now + (Stopwatch.Frequency / 40);

            try
            {
                var sd = (SoundData*)this.tracked;
                var loading = sd->GetIsLoadingSoundResource();
                var playing = sd->ISoundData.IsPlaying();

                if (playing)
                {
                    this.everPlayed = true;
                    this.playingSamples++;
                }

                if (loading)
                {
                    this.loadingSamples++;
                }
            }
            catch (Exception ex)
            {
                this.Say($"     poll threw: {ex.Message}");
                this.ReleaseTracked();
                return;
            }

            this.polls++;
        }

        if (now >= this.trackUntil)
        {
            this.Summarise("window elapsed");
            this.ReleaseTracked();
        }
    }

    /// <summary>Last verdict, surfaced in the UI so it survives log rotation.</summary>
    public string LastVerdict { get; private set; } = string.Empty;

    /// <summary>
    /// Called on both timeout and interruption. Previously only the timeout path reported,
    /// so pressing a button twice in three seconds discarded the first result entirely.
    /// </summary>
    private void Summarise(string why)
    {
        if (this.polls == 0)
        {
            return;
        }

        var line = $"observed {this.polls} samples at 25ms ({why}): playing TRUE in " +
                   $"{this.playingSamples}, loading in {this.loadingSamples}";

        this.LastVerdict = this.everPlayed
            ? $"PLAYED — audible for about {this.playingSamples * 25} ms"
            : "NEVER PLAYED — a slot was allocated but no audio was produced";

        this.Say($"     {line}");
        this.Say($"     VERDICT: {this.LastVerdict}");
    }

    private void ReleaseTracked()
    {
        if (this.tracked == 0)
        {
            return;
        }

        // Report before discarding, so a rapid second press does not lose the first result.
        this.Summarise("interrupted");

        try
        {
            var sd = (SoundData*)this.tracked;
            sd->ISoundData.Stop(0);

            var manager = SoundManager.Instance();
            if (manager != null)
            {
                manager->ReleaseSoundData(sd);
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "NativeSpike: releasing the tracked SoundData failed");
        }
        finally
        {
            this.tracked = 0;
        }
    }

    private byte[]? LoadFirst(IReadOnlyList<string> candidates, out string used)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                var file = this.data.GetFile(candidate);
                if (file?.Data is { Length: > 0 })
                {
                    used = candidate;
                    return file.Data;
                }
            }
            catch
            {
                // Missing path; try the next.
            }
        }

        used = string.Empty;
        return null;
    }

    /// <summary>
    /// A deliberately obnoxious two-tone alarm: constant near-full amplitude, no decay.
    /// </summary>
    /// <remarks>
    /// The previous payload was a decaying sine that fell to 3% of full scale by 800 ms.
    /// Against a 27% master volume that is inaudible, which made "did our audio play?"
    /// impossible to answer by ear and cost several rounds of misdiagnosis. This cannot
    /// be confused with a voice grunt, and it cannot be missed.
    /// </remarks>
    private static short[] RenderAlarm(float seconds)
    {
        var total = (int)(SampleRate * seconds);
        var result = new short[total];

        const int rampSamples = 220; // ~5 ms, just enough to avoid a click
        var phase = 0.0;

        for (var i = 0; i < total; i++)
        {
            var t = i / (double)SampleRate;

            // Alternate every 150 ms. Phase is accumulated rather than recomputed, so the
            // frequency switch does not introduce a discontinuity.
            var frequency = (int)(t / 0.15) % 2 == 0 ? 440.0 : 880.0;
            phase += 2.0 * Math.PI * frequency / SampleRate;
            if (phase > Math.PI * 2)
            {
                phase -= Math.PI * 2;
            }

            var envelope = 1.0;
            if (i < rampSamples)
            {
                envelope = i / (double)rampSamples;
            }
            else if (i > total - rampSamples)
            {
                envelope = (total - i) / (double)rampSamples;
            }

            result[i] = (short)(Math.Sin(phase) * envelope * 0.85 * short.MaxValue);
        }

        return result;
    }

    private static short[] RenderTestTone(float seconds = 0.35f)
    {
        ISampleProvider tone = new TestTone(seconds);
        var all = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = tone.Read(buffer, 0, buffer.Length)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, read));
        }

        return ScdWriter.ToPcm16(all.ToArray());
    }

    private void Say(string line)
    {
        this.Report.Add(line);
        this.log.Information("[spike] {Line}", line);
    }
}
