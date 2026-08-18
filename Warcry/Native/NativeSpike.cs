using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using NAudio.Wave;
using Warcry.Audio;

namespace Warcry.Native;

/// <summary>What an attempt is testing. Ordered by how much it can tell you.</summary>
public enum SpikeMode
{
    /// <summary>
    /// THE POSITIVE CONTROL. A real, indexed game <c>.scd</c>, no file written and no
    /// redirect registered — nothing but a <c>PlaySound</c> call on stock game data.
    /// </summary>
    /// <remarks>
    /// The single test the five-day spike never ran, and the reason its NO-GO does not
    /// hold. Until this makes a noise, "silence" is uninterpretable: it cannot distinguish
    /// a bad file from a bad redirect from a call that was never going to work. Silence
    /// here indicts the <em>call</em> — the arguments, the category, the position
    /// convention — and clears both Penumbra and the writer completely.
    /// </remarks>
    StockGamePath,

    /// <summary>
    /// NEGATIVE CONTROL. A path that does not exist, with no file and no redirect.
    /// </summary>
    NegativeControl,

    /// <summary>
    /// CONTROL: a byte-for-byte copy of a real game SCD, served from a synthetic path.
    /// The file is definitionally valid, so silence here indicts the <em>path</em>.
    /// </summary>
    VerbatimTemplate,

    /// <summary>
    /// Every audio index retargeted at a single entry holding our clip, so the file's
    /// weighted-random selection has nothing left to choose between.
    /// </summary>
    /// <remarks>
    /// The production shape. A battle-voice SCD picks its waveform from an explicit
    /// weighted table and <c>soundNumber</c> only chooses which table — so a caller can
    /// never select a specific grunt. This sidesteps the whole mechanism instead of
    /// fighting it, and removes the empty stub entries at the same time.
    /// </remarks>
    OneClipEverywhere,

    /// <summary>
    /// Template with its sound/audio counts forced to 1 and entry 0 given all the freed
    /// space. Deterministic selection, and long enough to be unmistakable.
    /// </summary>
    ForceSingleEntry,

    /// <summary>
    /// CONTROL, rung 1: retargets the scoped group at an existing bank in place. Isolates
    /// the offset-table rewrite.
    /// </summary>
    RetargetToExistingBank,

    /// <summary>
    /// CONTROL, rung 2: appends a verbatim copy of an existing bank's audio past the end of
    /// the file and retargets at it. Isolates appending from the codec.
    /// </summary>
    AppendExistingBank,

    /// <summary>
    /// Every audio entry overwritten with the same payload, so the engine's random pick
    /// always lands on ours. Sidesteps the layout/randomisation tables entirely.
    /// </summary>
    InPlaceFillAll,

    /// <summary>
    /// A real game SCD with ONLY audio entry 0's header and payload overwritten — every
    /// other byte identical, file length unchanged.
    /// </summary>
    InPlaceAudioSwap,

    /// <summary>Container built from scratch. Never demonstrated to play — nor to fail.</summary>
    AuthoredPcm,

    /// <summary>As above but tagged MS-ADPCM, to separate container from codec rejection.</summary>
    AuthoredAdpcmTag,
}

/// <summary>
/// Which of the engine's entry points to call.
/// </summary>
/// <remarks>
/// <c>PlaySound</c> takes eighteen arguments, four of them booleans nobody has named. The
/// other two take a path and almost nothing else, so there is correspondingly less to get
/// wrong — which makes them far better first tests than the one the spike fixated on.
/// </remarks>
public enum PlayEntry
{
    /// <summary>The 18-argument workhorse. Positional, category-aware.</summary>
    PlaySound,

    /// <summary>Six arguments, non-positional: path, volume, soundNumber, fadeIn, autoRelease, category.</summary>
    PlaySystemSound,

    /// <summary>One argument. Nothing to get wrong but the path.</summary>
    PlayCutsceneVoSound,
}

/// <summary>Where an authored payload is served from.</summary>
/// <remarks>
/// Orthogonal to the payload, and it has to be: a synthetic path has no entry in the sqpack
/// index at all, while a real one does. If Penumbra reports a redirect as applied and the
/// engine still will not play it, that difference is the next thing to rule out.
/// </remarks>
public enum PathSource
{
    /// <summary>A path we invented, fresh every attempt so no cached handle can shadow it.</summary>
    Synthetic,

    /// <summary>A real indexed <c>Vo_Battle</c> path the game has not loaded this session.</summary>
    RealIndexed,
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
/// <para><b>Rebuilt 2026-08-17</b> after the original spike returned a NO-GO it had not
/// earned. Three things changed, and each replaces a guess with a readout:</para>
/// <list type="number">
/// <item>a positive control (<see cref="SpikeMode.StockGamePath"/>) so silence means
/// something;</item>
/// <item>the Penumbra redirect is <em>verified</em> before playing, via
/// <c>ResolveDefaultPath</c>, so "did the redirect apply" is never again inferred from
/// whether a sound was heard;</item>
/// <item>the returned <c>SoundData</c> and its <c>SoundResourceHandle</c> are inspected —
/// filename, byte length, load state — so "did our file reach the engine" is a number, not
/// an opinion.</item>
/// </list>
/// <para>Default <c>isPositional</c> is now <b>false</b>. A non-positional sound cannot be
/// attenuated to nothing by distance, which removes the most likely cause of the original
/// all-modes silence from the very first test.</para>
/// </remarks>
public sealed unsafe class NativeSpike : IDisposable
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
    /// Real, indexed paths. Deliberately Japanese/German voices for races the player is
    /// not, so the handle is almost certainly not already cached — a cached path keeps its
    /// handle and silently ignores a new redirect.
    /// </summary>
    private static readonly string[] RealPathCandidates =
    [
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
    private string? stockPath;

    // Retained SoundData under observation.
    private nint tracked;
    private bool trackedIsOurs;
    private nint trackedHandle;
    private long expectedBytes;
    private float expectedSeconds;
    private long nextPoll;
    private long trackUntil;
    private int polls;
    private int playingSamples;
    private int onListSamples;
    private float maxElapsed;
    private bool everPlayed;
    private bool everOnList;
    private bool handleReported;

    public NativeSpike(IDataManager data, IPluginLog log, PenumbraBridge penumbra, string configDirectory)
    {
        this.data = data;
        this.log = log;
        this.penumbra = penumbra;
        this.cacheDir = Path.Combine(configDirectory, ".cache", "scd");

        // Guarded because this runs inside the Plugin constructor: a read-only config
        // directory or an antivirus hold must degrade the spike tab, not fail the whole
        // plugin load. ScdForge treats the identical call the same way.
        try
        {
            Directory.CreateDirectory(this.cacheDir);

            // Legacy attempt-numbered files. Their names are the bug described on
            // SyntheticPathFor, so remove them rather than leave them to confuse a future dump.
            foreach (var legacy in Directory.EnumerateFiles(this.cacheDir, "spike_*.scd"))
            {
                try
                {
                    File.Delete(legacy);
                }
                catch (Exception ex)
                {
                    this.log.Warning(ex, "NativeSpike: could not remove the legacy cache file {File}", legacy);
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "NativeSpike: could not prepare the cache directory");
        }
    }

    /// <summary>
    /// A synthetic game path derived from the payload's content hash.
    /// </summary>
    /// <remarks>
    /// <para><b>This must not be derived from the attempt counter.</b> It was, and it cost a
    /// full round of misdiagnosis on 2026-08-17: the counter resets to zero on every plugin
    /// reload, so reloading and re-running produced <c>s0006.scd</c> a second time within
    /// one <em>game</em> session. The engine had already cached a resource handle for that
    /// path from the earlier load and never re-read the file, so a 282,208-byte payload on
    /// disk was served to the engine as the 105,776-byte one from twenty minutes earlier —
    /// and the report duly said the container was at fault.</para>
    /// <para>Content addressing makes the class of bug impossible: identical bytes reuse a
    /// path, which is correct and warm; different bytes can never collide with a cached
    /// handle. It also matches how <c>ClipLibrary</c> already stores clips.</para>
    /// </remarks>
    private static string SyntheticPathFor(byte[] payload)
        => $"sound/vfx/warcry/spike/{ContentHash(payload)}.scd";

    private static string ContentHash(byte[] payload)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload), 0, 8).ToLowerInvariant();

    public List<string> Report { get; } = [];

    public bool Tracking => this.tracked != 0;

    /// <summary>Which engine entry point to call.</summary>
    public PlayEntry Entry { get; set; } = PlayEntry.PlaySound;

    /// <summary>
    /// <c>PlaySound</c>'s soundNumber. For a multi-entry container this is the likely
    /// selector; the game's own captured calls say what it really uses.
    /// </summary>
    public uint SoundNumber { get; set; }

    public float Volume { get; set; } = 1.0f;

    /// <summary>
    /// Off by default, deliberately.
    /// </summary>
    /// <remarks>
    /// The original spike always passed <c>true</c> together with the player's <em>world</em>
    /// coordinates. If the engine wants listener-relative coordinates, that placed every
    /// test hundreds of units away and attenuated it to silence — a complete explanation
    /// for the whole NO-GO with no fault anywhere else. Non-positional removes the variable.
    /// </remarks>
    public bool IsPositional { get; set; }

    public SoundVolumeCategory Category { get; set; } = SoundVolumeCategory.Player;

    /// <summary>
    /// Which sound group <see cref="SpikeMode.OneClipEverywhere"/> retargets, or -1 for all
    /// audio indices in the file.
    /// </summary>
    /// <remarks>
    /// Defaults to 3 — the group the game passes for an action. Retargeting everything also
    /// replaces the damage-taken (group 1) and death (group 2) banks, which would fire the
    /// player's voiceline every time they got hit.
    /// </remarks>
    public int TargetGroup { get; set; } = 3;

    /// <summary>Where authored payloads are served from.</summary>
    public PathSource PathSource { get; set; } = PathSource.Synthetic;

    /// <summary>
    /// Which codec to encode our clip as. MS-ADPCM by default.
    /// </summary>
    /// <remarks>
    /// A survey of the game's own SCDs found 267 MS-ADPCM entries, 92 HCA, and no PCM at
    /// all — and the engine loaded a PCM entry of ours and refused to decode it. PCM is
    /// retained only so the negative result stays reproducible.
    /// </remarks>
    public uint Codec { get; set; } = ScdWriter.FormatMsAdPcm;

    /// <summary>The path most recently played, so it can be replayed warm.</summary>
    public string LastPath { get; private set; } = string.Empty;

    /// <summary>Last verdict, surfaced in the UI so it survives log rotation.</summary>
    public string LastVerdict { get; private set; } = string.Empty;

    /// <summary>
    /// Result of the last standalone redirect check, kept separate so it can be shown next
    /// to the button that produced it rather than buried in the scrolling report.
    /// </summary>
    public string LastRedirectCheck { get; private set; } = string.Empty;

    /// <summary>
    /// The stock game path the positive control will play, resolved once and cached.
    /// </summary>
    /// <remarks>
    /// Deliberately a race, gender and language the player is not — a Japanese Midlander
    /// when you are a French Hrothgar. That is the entire point: a grunt in the wrong voice
    /// cannot be your own character, and cannot be ambient. It is the control the original
    /// spike never had, and hearing it is the proof that <c>PlaySound</c> works.
    /// </remarks>
    public string StockPath
    {
        get
        {
            if (this.stockPath is null)
            {
                this.stockPath = this.LoadFirst(RealPathCandidates, out var resolved) is null
                    ? string.Empty
                    : resolved;
            }

            return this.stockPath;
        }
    }

    /// <summary>
    /// Let the engine own the SoundData, as production would.
    /// </summary>
    /// <remarks>
    /// With <c>false</c> we retain the pointer so it can be polled and its resource handle
    /// inspected, then release it. With <c>true</c> the engine reclaims the slot itself —
    /// the normal path — and the pointer must not be released by us.
    /// </remarks>
    public bool AutoRelease { get; set; }

    // ------------------------------------------------------------------ attempts

    public void Run(SpikeMode mode, Vector3 playerPosition)
    {
        this.ReleaseTracked();
        this.Report.Clear();
        this.attempt++;
        this.expectedBytes = 0;

        this.expectedSeconds = 0f;

        this.Say($"=== attempt {this.attempt} — {mode} via {this.Entry} ===");
        this.Say($"    volume={this.Volume:0.00} soundNumber={this.SoundNumber} " +
                 $"category={this.Category} positional={this.IsPositional} autoRelease={this.AutoRelease}");

        switch (mode)
        {
            case SpikeMode.StockGamePath:
                this.RunStockGamePath(playerPosition);
                return;

            case SpikeMode.NegativeControl:
                var bogus = $"sound/vfx/warcry/nonexistent/none{this.attempt:D4}.scd";
                this.Say("    no file written, no redirect registered — this MUST be silent");
                this.Say($"    {bogus}");
                this.Play(bogus, playerPosition);
                return;

            default:
                this.RunAuthored(mode, playerPosition);
                return;
        }
    }

    /// <summary>
    /// Plays stock game data with nothing of ours involved anywhere.
    /// </summary>
    private void RunStockGamePath(Vector3 playerPosition)
    {
        if (this.LoadFirst(RealPathCandidates, out var realPath) is null)
        {
            this.Say("FAIL — no real Vo_Battle path resolved. Run 'probe real paths' first.");
            this.LastVerdict = "no stock path available";
            return;
        }

        this.Say($"OK  real indexed path {realPath}");
        this.Say("    NO file written. NO redirect registered. Nothing of ours is involved.");
        this.Say("    If this is silent, the problem is the CALL — not Penumbra, not the writer.");
        this.Play(realPath, playerPosition);
    }

    private void RunAuthored(SpikeMode mode, Vector3 playerPosition)
    {
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

                case SpikeMode.RetargetToExistingBank:
                case SpikeMode.AppendExistingBank:
                    payload = this.BuildBankRetarget(template, append: mode == SpikeMode.AppendExistingBank);
                    break;

                case SpikeMode.OneClipEverywhere:
                    this.expectedSeconds = 2.0f;
                    var encoded = this.Codec == ScdWriter.FormatMsAdPcm
                        ? ScdWriter.AudioPayload.MsAdPcmMono(RenderAlarm(2.0f), SampleRate)
                        : ScdWriter.AudioPayload.Pcm16(RenderAlarm(2.0f), SampleRate);

                    payload = ScdWriter.PointAudioAtOneEntry(
                        template, this.ResolveTargetIndices(template), encoded, out var everyNote);

                    this.Say($"    {everyNote}");
                    this.Say("    payload: 440/880 Hz alarm, 85% full scale — any roll in scope, same clip");

                    if (this.Codec == ScdWriter.FormatPcm)
                    {
                        this.Say("    NOTE: PCM. The engine has already refused one of these, and the survey");
                        this.Say("    found no PCM entry anywhere in the game's own data. Expect silence.");
                    }

                    break;

                case SpikeMode.InPlaceAudioSwap:
                    payload = ScdWriter.SwapFirstAudioInPlace(
                        template, RenderTestTone(), SampleRate, ScdWriter.FormatPcm, out var note);
                    this.Say($"    {note}");
                    break;

                case SpikeMode.ForceSingleEntry:
                    payload = ScdWriter.ForceSingleAudioEntry(
                        template, RenderAlarm(2.0f), SampleRate, ScdWriter.FormatPcm, out var singleNote);
                    this.Say($"    {singleNote}");
                    this.Say("    payload: 440/880 Hz alarm, 85% full scale, no decay");
                    break;

                case SpikeMode.InPlaceFillAll:
                    payload = ScdWriter.SwapAllAudioInPlace(
                        template, RenderTestTone(), SampleRate, ScdWriter.FormatPcm, out var fillNote);
                    this.Say($"    {fillNote}");
                    break;

                case SpikeMode.AuthoredAdpcmTag:
                    payload = ScdWriter.BuildPcm(template, RenderTestTone(), SampleRate, ScdWriter.FormatMsAdPcm);
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

        var localPath = Path.Combine(this.cacheDir, $"{ContentHash(payload)}.scd");
        try
        {
            File.WriteAllBytes(localPath, payload);
        }
        catch (Exception ex)
        {
            this.Say($"FAIL 3/5 — writing: {ex.Message}");
            return;
        }

        this.expectedBytes = payload.Length;

        // Read the size back. "We wrote N / disk has M / the engine holds K" is three
        // separate facts, and conflating them is what made the last round unreadable.
        var onDisk = new FileInfo(localPath).Length;
        this.Say(mode == SpikeMode.VerbatimTemplate
            ? $"OK 3/5 copied the template verbatim ({payload.Length} bytes) — this file is known-good"
            : $"OK 3/5 wrote {payload.Length} bytes");
        this.Say($"    on disk: {onDisk} bytes at {Path.GetFileName(localPath)}" +
                 (onDisk == payload.Length ? string.Empty : "  <<< THE WRITE DID NOT TAKE"));

        // ---- 4. choose a path, redirect, and VERIFY the redirect ----
        string gamePath;

        if (this.PathSource == PathSource.RealIndexed)
        {
            if (this.LoadFirst(RealPathCandidates, out var realPath) is null)
            {
                this.Say("FAIL 4/5 — no unused real vo_battle path resolved, so this cannot run.");
                return;
            }

            gamePath = realPath;
            this.Say($"    path: REAL indexed {gamePath}");
            this.Say("    (only redirects once per session — the handle is cached after the first load)");
        }
        else
        {
            // Content-addressed, NOT attempt-numbered. A path the game has already requested
            // keeps its cached handle and silently ignores a new redirect — see
            // SyntheticPathFor for how that produced a whole round of false conclusions.
            gamePath = SyntheticPathFor(payload);
            this.Say($"    path: SYNTHETIC {gamePath}");
            this.Say("    (content-addressed, so these bytes can never collide with a cached handle)");
        }

        if (!this.RegisterAndVerify(gamePath, localPath))
        {
            return;
        }

        this.Play(gamePath, playerPosition);
    }

    /// <summary>
    /// Builds the codec-free control: point the scoped group at another bank's audio.
    /// </summary>
    /// <remarks>
    /// Prefers the death bank as the destination — it is the longest and least like an
    /// attack grunt, so "did the swap take?" is answerable by ear without ambiguity.
    /// </remarks>
    private byte[] BuildBankRetarget(ScdWriter.Template template, bool append)
    {
        var groups = ScdInspector.ParseGroups(template, out var error);
        if (error.Length > 0)
        {
            this.Say($"    group parse: {error}");
        }

        var scoped = groups.Find(g => g.Id == this.TargetGroup);
        if (scoped is null || scoped.Records.Count == 0)
        {
            throw new InvalidOperationException(
                $"group {this.TargetGroup} is missing or empty, so there is nothing to retarget");
        }

        var indices = ScdInspector.AudioIndicesFor(scoped);

        // Somewhere obviously different: the death bank if it exists, else anything the
        // scoped group cannot already reach.
        var donor = groups.Find(g => g.Id == 2 && g.Records.Count > 0);
        var targetIndex = donor is not null
            ? donor.Records[0].AudioIndex
            : FirstIndexOutside(template, indices);

        if (targetIndex < 0)
        {
            throw new InvalidOperationException("no donor audio entry outside the scoped group");
        }

        this.Say($"    scope: group {this.TargetGroup} -> audio {string.Join(", ", indices)}");
        this.Say($"    donor: audio {targetIndex}" +
                 (donor is not null ? " (the death bank — unmistakably not an attack grunt)" : string.Empty));
        this.Say("    NO audio of ours is in this file — the donor is the game's own HCA.");
        this.Say($"    Expect the DONOR sound when you play with soundNumber {this.TargetGroup}.");

        var payload = append
            ? ScdWriter.AppendCopyOfEntry(template, indices, targetIndex, out var note)
            : ScdWriter.PointAudioAtExistingEntry(template, indices, targetIndex, out note);

        this.Say($"    {note}");
        this.Say(append
            ? "    Tests: offset rewrite + APPENDING past the end of file. Codec is not in play."
            : "    Tests: offset rewrite only, in place. Neither appending nor codec is in play.");

        return payload;
    }

    private static int FirstIndexOutside(ScdWriter.Template template, IReadOnlySet<int> exclude)
    {
        for (var i = 0; i < template.AudioOffsets.Length; i++)
        {
            if (exclude.Contains(i))
            {
                continue;
            }

            var offset = (int)template.AudioOffsets[i];
            if (offset + 32 > template.Bytes.Length)
            {
                continue;
            }

            var format = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                template.Bytes.AsSpan(offset + 0x0C));
            if (format != ScdWriter.FormatEmpty)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The audio indices <see cref="TargetGroup"/> selects, or null for every index.
    /// </summary>
    /// <remarks>
    /// Falls back to null — retarget everything — if the group cannot be parsed or holds no
    /// records, since a scoped rewrite that silently matched nothing would look exactly like
    /// a broken writer.
    /// </remarks>
    private IReadOnlySet<int>? ResolveTargetIndices(ScdWriter.Template template)
    {
        if (this.TargetGroup < 0)
        {
            this.Say("    scope: ALL audio indices (this also replaces damage and death grunts)");
            return null;
        }

        var groups = ScdInspector.ParseGroups(template, out var error);
        if (error.Length > 0)
        {
            this.Say($"    group parse: {error}");
        }

        var group = groups.Find(g => g.Id == this.TargetGroup);
        if (group is null || group.Records.Count == 0)
        {
            this.Say($"    scope: group {this.TargetGroup} is missing or empty — falling back to ALL indices");
            return null;
        }

        var indices = ScdInspector.AudioIndicesFor(group);
        this.Say($"    scope: group {this.TargetGroup} ({ScdInspector.DescribeSoundNumber(this.TargetGroup)}) " +
                 $"-> audio {string.Join(", ", indices)}");
        this.Say($"    every other bank is left untouched; play with soundNumber {this.TargetGroup}");
        return indices;
    }

    /// <summary>
    /// Registers the redirect and then asks Penumbra whether it is actually in force.
    /// </summary>
    /// <remarks>
    /// The whole original spike hinged on an assumption this call tests directly. A
    /// <c>ResolveDefaultPath</c> that echoes the game path back means the redirect is not
    /// applied, and nothing downstream — file, container, codec, arguments — can matter.
    /// </remarks>
    private bool RegisterAndVerify(string gamePath, string localPath)
    {
        if (!this.penumbra.PenumbraAvailable)
        {
            this.Say("FAIL 4/5 — Penumbra not loaded; the game cannot see our file.");
            return false;
        }

        var code = this.penumbra.Redirect(PenumbraBridge.SpikeTag, new Dictionary<string, string> { [gamePath] = localPath });
        if (code is null)
        {
            this.Say($"FAIL 4/5 — redirect failed: {this.penumbra.LastError}");
            return false;
        }

        this.Say(code == 0 ? $"OK 4/5 registered {gamePath}" : $"WARN 4/5 Penumbra returned {code}; continuing");

        var state = this.penumbra.Verify(gamePath, localPath, out var message);
        this.Say($"    verify: {message}");

        if (state != RedirectState.Applied)
        {
            this.Say("    Playing anyway, but the redirect is not in force — expect stock audio or silence.");
        }

        return true;
    }

    /// <summary>
    /// Registers a redirect, verifies it, and stops. No audio at all.
    /// </summary>
    /// <remarks>
    /// Two jobs. It answers "does a Penumbra SCD redirect apply?" as a pure yes/no, and it
    /// doubles as the control for grunts caused by Penumbra redrawing the character when a
    /// mod set changes — which is what the original spike was almost certainly hearing.
    /// </remarks>
    public void RedirectOnly()
    {
        this.ReleaseTracked();
        this.Report.Clear();
        this.attempt++;

        this.Say($"=== attempt {this.attempt} — REDIRECT ONLY, no play call ===");

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

        var localPath = Path.Combine(this.cacheDir, $"{ContentHash(payload)}.scd");
        File.WriteAllBytes(localPath, payload);

        var gamePath = SyntheticPathFor(payload);

        if (!this.penumbra.PenumbraAvailable)
        {
            this.Say("FAIL — Penumbra is not loaded.");
            this.LastRedirectCheck = "Penumbra is not loaded.";
            this.LastVerdict = "Penumbra not loaded";
            return;
        }

        var code = this.penumbra.Redirect(PenumbraBridge.SpikeTag, new Dictionary<string, string> { [gamePath] = localPath });
        this.Say($"registered {gamePath} (code {code}). No play call was made.");

        var state = this.penumbra.Verify(gamePath, localPath, out var message);
        this.Say($"verify: {message}");

        this.LastRedirectCheck = state switch
        {
            RedirectState.Applied =>
                "APPLIED — Penumbra serves .scd on a synthetic path. The native route is open.",
            RedirectState.NotApplied =>
                "NOT APPLIED — Penumbra echoed the game path back. This is the blocker, and it is " +
                "not in our audio code.",
            RedirectState.OtherTarget => "Another mod already owns this path.",
            _ => $"Could not ask Penumbra: {this.penumbra.LastError}",
        };

        this.LastVerdict = this.LastRedirectCheck;
        this.Say("If you hear a grunt now, it is Penumbra redrawing your character, not us.");
    }

    /// <summary>Plays the previous path again without rewriting or re-registering it.</summary>
    public void ReplayLast(Vector3 playerPosition)
    {
        if (this.LastPath.Length == 0)
        {
            this.Say("Nothing to replay — run an attempt first.");
            return;
        }

        this.ReleaseTracked();
        this.Say($"--- replay (warm) {this.LastPath} via {this.Entry}, soundNumber {this.SoundNumber} ---");
        this.Play(this.LastPath, playerPosition);
    }

    /// <summary>
    /// Replays a call the game itself made, argument for argument.
    /// </summary>
    /// <remarks>
    /// <para>The decisive experiment, and the cheapest. The game invokes <c>PlaySound</c>
    /// successfully hundreds of times a minute; a captured row is a tuple that demonstrably
    /// produced audio. Replaying it verbatim proves the call mechanism works, and then a
    /// single substitution — <paramref name="overridePath"/> — tests exactly one thing.</para>
    /// <para>No guessing at <c>a9</c>, <c>a13</c>, <c>a15</c>, <c>a18</c>, the position
    /// convention or the category: the values come from the engine.</para>
    /// </remarks>
    public void ReplayCapture(in SoundLogRow row, string? overridePath = null, uint? overrideSoundNumber = null)
    {
        this.ReleaseTracked();
        this.Report.Clear();
        this.attempt++;
        this.expectedBytes = 0;

        var path = overridePath ?? row.Path;
        var soundNumber = overrideSoundNumber ?? row.SoundNumber;

        this.Say($"=== attempt {this.attempt} — REPLAY of a captured game call ===");
        this.Say(row.Describe());

        if (overridePath is not null)
        {
            this.Say($"    PATH SUBSTITUTED -> {path}");
            this.Say("    Every other argument is byte-identical to the game's own call.");
        }

        if (overrideSoundNumber is not null)
        {
            this.Say($"    soundNumber OVERRIDDEN -> {soundNumber} (captured value was {row.SoundNumber})");
            this.Say("    soundNumber selects a sound GROUP; each group holds a weighted-random list.");
        }

        this.LastPath = path;

        try
        {
            var manager = SoundManager.Instance();
            if (manager == null)
            {
                this.Say("FAIL — SoundManager.Instance() was null.");
                return;
            }

            var result = manager->PlaySound(
                path,
                row.Volume,
                row.FadeInDuration,
                row.Position.X, row.Position.Y, row.Position.Z,
                row.Speed,
                row.A9,
                soundNumber,
                this.AutoRelease,
                row.Category,
                row.A13,
                row.MidiNote,
                row.A15,
                row.DefaultFadeOut,
                row.IsPositional,
                row.A18);

            // Honour the toggle rather than forcing false. Retaining the pointer means the
            // NEXT press force-releases this one mid-playback, which produces exactly the
            // intermittent silence that looks like a bug in the file.
            this.AfterPlay(result, ownsResult: !this.AutoRelease);
        }
        catch (Exception ex)
        {
            this.Say($"FAIL — replay threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Dumps an SCD's structure — audio entries and, crucially, its weighted-random sound
    /// groups.
    /// </summary>
    /// <remarks>
    /// Answers "why did I get a different grunt from the same soundNumber?" from the file
    /// itself rather than by repeated listening. Pass a game path; defaults to the template.
    /// </remarks>
    public void Inspect(string? gamePath = null)
    {
        this.Report.Clear();

        byte[]? bytes;
        string used;

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            bytes = this.LoadFirst(TemplateCandidates, out used);
        }
        else
        {
            bytes = this.LoadFirst([gamePath], out used);
        }

        if (bytes is null)
        {
            this.Say($"FAIL — could not read {(string.IsNullOrWhiteSpace(gamePath) ? "any template" : gamePath)}.");
            this.LastVerdict = "nothing to inspect";
            return;
        }

        var before = this.Report.Count;
        ScdInspector.Describe(bytes, used, this.Report);
        for (var i = before; i < this.Report.Count; i++)
        {
            this.log.Information("[scd] {Line}", this.Report[i]);
        }

        this.LastVerdict = $"inspected {used}";
    }

    /// <summary>
    /// Surveys which audio formats the game's own <c>.scd</c> files actually contain.
    /// </summary>
    /// <remarks>
    /// <para>The ladder has narrowed the failure to one thing: the engine loads our file and
    /// will not decode the audio entry we wrote. Rather than guess at a codec and build an
    /// encoder on a hunch — the mistake this spike has already made twice — find out what
    /// the engine demonstrably eats, then copy that structure. Templating from real data has
    /// been the only technique that has worked here.</para>
    /// <para>If any game file contains <c>Format = 0x01</c>, PCM is live and its entry is a
    /// template we can diff against ours. If several hundred files never contain one, PCM is
    /// dead code in the engine and the choice is between MS-ADPCM and Vorbis — decided by
    /// whichever of those turns up, since that one comes with a structure to copy.</para>
    /// <para><c>sound/foot/dev/{n}.scd</c> is used as the probe family because it is a real
    /// observed path with a dense numeric range, and footsteps are short — the least likely
    /// sounds in the game to be worth compressing.</para>
    /// </remarks>
    public void SurveyFormats(IEnumerable<string> observed, int maxFiles = 250)
    {
        this.Report.Clear();
        this.Say("=== surveying audio formats in the game's own .scd files ===");

        var formatFiles = new Dictionary<uint, int>();
        var formatEntries = new Dictionary<uint, int>();
        var firstExample = new Dictionary<uint, string>();
        var filesRead = 0;

        foreach (var path in this.SurveyCandidates(observed))
        {
            if (filesRead >= maxFiles)
            {
                break;
            }

            byte[] bytes;
            try
            {
                var file = this.data.GetFile(path);
                if (file?.Data is not { Length: > 0 })
                {
                    continue;
                }

                bytes = file.Data;
            }
            catch
            {
                continue;
            }

            if (!ScdWriter.TryParse(bytes, out var parsed, out _) || parsed is null)
            {
                continue;
            }

            filesRead++;
            var seenHere = new HashSet<uint>();

            foreach (var offset in parsed.AudioOffsets)
            {
                var at = (int)offset;
                if (at + 32 > bytes.Length)
                {
                    continue;
                }

                var format = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(at + 0x0C));

                formatEntries[format] = formatEntries.GetValueOrDefault(format) + 1;
                if (seenHere.Add(format))
                {
                    formatFiles[format] = formatFiles.GetValueOrDefault(format) + 1;
                }

                firstExample.TryAdd(format, path);
            }
        }

        this.Say($"read {filesRead} file(s).");

        if (filesRead == 0)
        {
            this.Say("Nothing resolved. Log some sounds on the Game sounds tab first (clear the filter),");
            this.Say("then run this again — observed paths are included in the survey.");
            this.LastVerdict = "survey found no files";
            return;
        }

        foreach (var (format, entries) in formatEntries.OrderByDescending(p => p.Value))
        {
            this.Say($"  format 0x{format:X2} {Describe(format),-22} {entries,6} entries in " +
                     $"{formatFiles[format],4} files   e.g. {firstExample[format]}");
        }

        var pcm = formatEntries.GetValueOrDefault(ScdWriter.FormatPcm);
        this.Say(string.Empty);
        this.Say(pcm > 0
            ? $"PCM IS LIVE — {pcm} entries. Run /warcry scdinfo on the example above and copy its header."
            : "NO PCM ANYWHERE in this sample. Treat Format 0x01 as dead code and pick a format above.");

        this.LastVerdict = pcm > 0
            ? $"PCM found in {formatFiles[ScdWriter.FormatPcm]} files — a template exists"
            : $"no PCM in {filesRead} files — pick an encodable format that does appear";

        static string Describe(uint format) => format switch
        {
            ScdWriter.FormatPcm => "(PCM)",
            0x06 => "(OGG Vorbis)",
            ScdWriter.FormatMsAdPcm => "(MS-ADPCM)",
            ScdWriter.FormatHca => "(HCA)",
            ScdWriter.FormatEmpty => "(empty stub)",
            _ => string.Empty,
        };
    }

    /// <summary>Paths to survey: everything observed this session, then a dense probe family.</summary>
    private IEnumerable<string> SurveyCandidates(IEnumerable<string> observed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in observed)
        {
            if (path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) && seen.Add(path))
            {
                yield return path;
            }
        }

        foreach (var candidate in TemplateCandidates.Concat(RealPathCandidates))
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        // Observed live as 'sound/foot/dev/6994.scd'. Dense, short sounds, real range.
        for (var n = 1; n <= 9999; n++)
        {
            var candidate = $"sound/foot/dev/{n}.scd";
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>Logs which real <c>Vo_Battle</c> paths actually resolve in this installation.</summary>
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

    // ------------------------------------------------------------------ playing

    private void Play(string path, Vector3 playerPosition)
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

            var result = this.Entry switch
            {
                PlayEntry.PlaySystemSound => manager->PlaySystemSound(
                    path, this.Volume, this.SoundNumber, 0u, this.AutoRelease, this.Category),

                PlayEntry.PlayCutsceneVoSound => manager->PlayCutsceneVoSound(path),

                _ => manager->PlaySound(
                    path,
                    this.Volume,
                    0u,
                    playerPosition.X, playerPosition.Y, playerPosition.Z,
                    1.0f,
                    0,
                    this.SoundNumber,
                    this.AutoRelease,
                    this.Category,
                    false,
                    -1,
                    false,
                    false,
                    this.IsPositional,
                    false),
            };

            // Only PlaySound / PlaySystemSound were told whether to auto-release. The
            // one-argument entry point decides for itself, so we must never release it.
            var owns = this.Entry != PlayEntry.PlayCutsceneVoSound && !this.AutoRelease;
            this.AfterPlay(result, owns);
        }
        catch (Exception ex)
        {
            this.Say($"FAIL 5/5 — the play call threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Inspects the returned sound immediately, then starts watching it.
    /// </summary>
    /// <remarks>
    /// The immediate readout is the valuable half: the resource handle's filename and byte
    /// length say whether our file reached the engine, which no amount of listening can.
    /// </remarks>
    private void AfterPlay(SoundData* result, bool ownsResult)
    {
        if (result == null)
        {
            this.Say("FAIL 5/5 — the call returned null (no pool slot).");
            this.LastVerdict = "returned null";
            return;
        }

        this.Say("OK 5/5 got a SoundData*. That only means a slot was allocated — here is what it holds:");

        // DescribeSoundData writes straight into Report, so mirror only the new lines to
        // the log rather than re-logging everything Say has already emitted.
        var before = this.Report.Count;
        SoundDiagnostics.DescribeSoundData(result, this.Report, this.expectedBytes);
        for (var i = before; i < this.Report.Count; i++)
        {
            this.log.Information("[spike] {Line}", this.Report[i]);
        }

        // Never poll a slot the engine owns. It recycles them: an earlier run measured
        // 'sound/foot/dev/6994.scd' — a footstep — through a pointer it still believed was
        // ours, and reported PLAYED for audio nobody could hear.
        if (!ownsResult)
        {
            this.Say("    engine owns this slot and will recycle it, so it CANNOT be measured.");
            this.Say("    Judge by ear, or turn autoRelease off to get a verdict.");
            this.LastVerdict = "not measured — autoRelease is on; listen instead";
            return;
        }

        this.tracked = (nint)result;
        this.trackedIsOurs = ownsResult;
        this.trackedHandle = 0;
        this.polls = 0;
        this.playingSamples = 0;
        this.onListSamples = 0;
        this.maxElapsed = 0f;
        this.everPlayed = false;
        this.everOnList = false;
        this.handleReported = false;
        this.nextPoll = Stopwatch.GetTimestamp();
        this.trackUntil = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 3 / 2);
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

                // Identity check before believing anything this slot says. The pool is
                // shared with the whole game, and a recycled slot reads as a perfectly
                // healthy sound that simply is not ours.
                var handle = (nint)sd->SoundResourceHandle;
                if (handle != 0)
                {
                    if (this.trackedHandle == 0)
                    {
                        this.trackedHandle = handle;
                    }
                    else if (handle != this.trackedHandle)
                    {
                        this.Say("    SLOT RECYCLED — the engine gave this SoundData to another sound.");
                        this.Say("    Measurement abandoned rather than reported; nothing below would be ours.");
                        this.LastVerdict = "slot recycled mid-measurement — inconclusive, run it again";
                        this.tracked = 0;
                        this.trackedIsOurs = false;
                        return;
                    }
                }

                if (sd->IsPlaying())
                {
                    this.everPlayed = true;
                    this.playingSamples++;
                }

                if (SoundDiagnostics.FindInActiveList(sd, out _, out _))
                {
                    this.everOnList = true;
                    this.onListSamples++;
                }

                this.maxElapsed = Math.Max(this.maxElapsed, sd->GetElapsedTime());

                // Loading is asynchronous, so the handle is often still empty at call time.
                // Report it once it has settled — that is the moment the byte count means
                // something.
                if (!this.handleReported && !sd->GetIsLoadingSoundResource() && sd->SoundResourceHandle != null)
                {
                    this.handleReported = true;
                    this.Say("    resource settled:");
                    SoundDiagnostics.DescribeHandle(sd->SoundResourceHandle, this.Report, this.expectedBytes);
                }
            }
            catch (Exception ex)
            {
                this.Say($"    poll threw: {ex.Message}");
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

    /// <summary>
    /// Called on both timeout and interruption, so a rapid second press does not discard
    /// the first result.
    /// </summary>
    private void Summarise(string why)
    {
        if (this.polls == 0)
        {
            return;
        }

        this.Say($"    observed {this.polls} samples at 25ms ({why}): playing in {this.playingSamples}, " +
                 $"on the active list in {this.onListSamples}, max elapsed {this.maxElapsed:0.000}s");

        // A sound that "played" for a fraction of its own payload did not play our audio.
        // Pool slots are reused, and residue in a reused slot reads as a healthy sound.
        var suspect = this.expectedSeconds > 0f
                      && this.everPlayed
                      && this.maxElapsed < this.expectedSeconds * 0.5f;

        if (suspect)
        {
            this.Say($"    SUSPECT — the payload is {this.expectedSeconds:0.00}s but elapsed only reached " +
                     $"{this.maxElapsed:0.000}s.");
            this.Say("    A reused pool slot carries residue from whatever it held before. Treat this as " +
                     "silence unless you actually heard the payload.");
        }

        // Three outcomes, not two. "Accepted but silent" and "never accepted" point at
        // completely different suspects, and the original spike could not tell them apart.
        this.LastVerdict = (this.everPlayed, this.everOnList) switch
        {
            (true, _) when suspect =>
                $"SUSPECT — reported playing, but elapsed reached only {this.maxElapsed:0.00}s of a " +
                $"{this.expectedSeconds:0.00}s payload. Almost certainly pool residue, not our audio.",
            (true, _) => $"PLAYED — audible for about {this.playingSamples * 25} ms " +
                         $"(elapsed reached {this.maxElapsed:0.00}s)",
            (false, true) => "ACCEPTED BUT SILENT — the engine took it onto the active list and never " +
                             "produced audio. The container or the codec is the suspect.",
            (false, false) => "NEVER ACCEPTED — a slot was allocated and the engine never took it onto " +
                              "the active list. The path or the arguments are the suspect.",
        };

        this.Say($"    VERDICT: {this.LastVerdict}");
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
            if (this.trackedIsOurs)
            {
                var sd = (SoundData*)this.tracked;
                sd->Stop(0);

                var manager = SoundManager.Instance();
                if (manager != null)
                {
                    manager->ReleaseSoundData(sd);
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "NativeSpike: releasing the tracked SoundData failed");
        }
        finally
        {
            this.tracked = 0;
            this.trackedIsOurs = false;
        }
    }

    /// <summary>
    /// Releases a still-tracked <c>SoundData</c> on unload. Without this, unloading the
    /// plugin while an attempt is being polled leaks a slot from the engine's 256-entry
    /// pool until the game exits — and leaves a dangling pointer in a dead object.
    /// </summary>
    public void Dispose() => this.ReleaseTracked();

    // ------------------------------------------------------------------ helpers

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
    /// The original payload was a decaying sine that fell to 3% of full scale by 800 ms.
    /// Against a 27% master volume that is inaudible, which made "did our audio play?"
    /// impossible to answer by ear and cost several rounds of misdiagnosis.
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
        // Warm replays deliberately append rather than clear, so a cold and a warm result
        // can be compared side by side — but that grows without bound if you keep pressing.
        if (this.Report.Count > 300)
        {
            this.Report.Clear();
            this.Report.Add("(report truncated — the log has the full history)");
        }

        this.Report.Add(line);
        this.log.Information("[spike] {Line}", line);
    }
}
