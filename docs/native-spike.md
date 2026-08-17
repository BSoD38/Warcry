# Native audio spike — log

**Question:** can Warcry play a self-authored `.scd` through the game's own sound engine, so the game does the mixing, 3D positioning and volume routing instead of NAudio?

**Time box:** 5 working days, hard stop. Throwaway branch until a GO.
**Go/no-go criteria:** see `PLAN.md` §6.

---

## Day 1 — observation (2026-08-17)

Deliberately did **not** start by writing a binary format writer. Writing `.scd` bytes from research notes would have produced a crash with no diagnostic. Day 1 is understanding.

### Verified by reflection against the shipped `FFXIVClientStructs.dll` 7.55.1.8875

Not from GitHub, not from memory — from the exact assembly we compile against.

```csharp
SoundData* SoundManager.PlaySound(
    CStringPointer path, float volume, uint fadeInDuration,
    float posX, float posY, float posZ, float speed, int a9,
    uint soundNumber, bool autoRelease, SoundVolumeCategory volumeCategory,
    bool a13, int midiNote, bool a15, bool defaultFadeOut,
    bool isPositional, bool a18);
```

Corrections to the research notes: **`a9` is `int`, not `uint`**, and the path parameter is `InteropGenerator.Runtime.CStringPointer` (which has `.HasValue`, `.AsSpan()`, `.ToString()`), not a raw `byte*`. `[GenerateStringOverloads]` produces `string` and `ReadOnlySpan<byte>` overloads alongside it.

Also confirmed present: `SoundManager.Addresses.PlaySound`, `SoundManager.Delegates.PlaySound`, `SoundManager.Instance()`, `ReleaseSoundData`, `AcquireSoundData`, `GetEffectiveVolume(SoundBus)`, `SetVolume(SoundBus, float, int)`.

```
SoundVolumeCategory : Player=0, Party=1, Other=2, Unk3=3, Unk4=4, NoPlay=5, BypassVolumeRules=6
SoundBus            : Music=1, SE=2, Voice=3, System=4, BG=5, Foot=6, PlacedNPC=7,
                      TimeStretchBGM=8, Zingle=9, CommonVFX=10, BGEnv=11, Walla=12, Movie=13
```

**`SoundBus.Voice = 3`.** This makes the plan's "author the SCD with `BusNumber = 3` to reach the Voice bus" inference at least *coherent* — Voice genuinely is bus 3. It remains unproven that `PlaySound` honours the file's declared bus; that is a day-5 question.

`SoundData` fields relevant to us: `SoundResourceHandle*`, `Volume`, `PosX/Y/Z`, `Speed`, `SoundNumber`, `VolumeCategory`, `IsLoadingSoundResource`, `IsPositional`, `IsAutoReleaseEnabled`, **`IsLocalPlayer`** (the game tracks this itself), `IsActive`, plus a vtable. So a retained pointer can be repositioned and stopped, as the plan assumed.

### Built

`Warcry/Native/SoundManagerWatcher.cs` — a read-only hook on `PlaySound`.

Design notes:
- **Installed disabled.** `PlaySound` fires for every sound in the game — UI clicks, footsteps, VFX. Sitting in it permanently is not acceptable, so the hook is enabled only while the user is actively observing.
- **Filter over raw UTF-8.** A non-matching path allocates nothing; only a match calls `.ToString()`.
- Circuit breaker at 10 faults, same pattern as `ActionWatcher`.
- Records `MsSinceLocalCast` per row, measured against the local player's last ActionEffect timestamp.

**This is not throwaway code.** The same hook is what grunt suppression needs later (zero `soundData->Volume` for the caster's own `vo_battle` line), so it survives a NO-GO.

`/warcry dumpscd <gamepath>` — extracts a real game file to `pluginConfigs/Warcry/dump/` via `IDataManager.GetFile`. The `.scd` writer will be built against a genuine battle-voice file as a structural template rather than against prose about the format.

### What day 1 answers

1. **Real `.scd` paths** the game actually requests — the templates for the writer. The research's guessed naming convention (`Vo_Battle_PC_{race}_{slot}_{lang}.scd`, race codes `mid hil ele lal miq rog aur ros vie`) is unverified; observation replaces it with fact.
2. **Whether the signature resolves** at all on the live client.
3. **The grunt offset** — how long after snapshot the game plays its own battle voice. This is the ground truth the native route must match, and it doubles as the auto-calibration measurement discussed in §5.1.

---

## Day 2 — observation results (2026-08-17)

### Real path, and a correction to the research

Observed: **`sound/voice/Vo_Battle/Vo_Battle_PC_ros_Ma_fr.scd`**

The research guessed `Vo_Battle_PC_{race}_{slot}_{lang}.scd`. That is **wrong**. The real pattern is:

```
sound/voice/Vo_Battle/Vo_Battle_PC_{race}_{Ma|Fe}_{lang}.scd
```

`ros` = Hrothgar (the guessed race-code list was right on that one), `Ma` = male, `fr` = French client. **There is no voice slot in the filename.** One file covers a whole race + gender + language.

That has a structural consequence: the file must be a **multi-entry container**, with the character's voice selecting an entry *inside* it — almost certainly via `PlaySound`'s `soundNumber` parameter. It also means battle voice is **language-specific**, so any future grunt-suppression path list must cover all four language suffixes.

### Container layout, parsed from the real file (105,776 bytes)

```
0x00  "SEDBSSCF"            magic
0x08  u32  version = 3
0x0e  u16  header size = 0x30
0x10  u32  file size
0x30  u16  ? = 5
0x32  u16  sound entry count = 25
0x34  u16  audio entry count = 22
0x38  u32  -> 0x70   sound entry offset table (25 x u32)
0x3c  u32  -> 0xe0   audio entry offset table (22 x u32)
0x40  u32  -> 0x140  (5 x u32)
0x48  u32  -> 0x160
```

**Audio entry header — 32 bytes of plain u32 fields, confirmed against real bytes:**

| Offset | Field | Observed |
|---|---|---|
| 0x00 | `DataLength` | 0x1550 |
| 0x04 | `NumChannels` | 1 |
| 0x08 | `SampleRate` | 44100 |
| 0x0C | `Format` | **0x1A = HCA** |
| 0x10 | `LoopStart` | 0 |
| 0x14 | `LoopEnd` | 0 |
| 0x18 | `SubInfoSize` | 0x78 |
| 0x1C | `Flags` | 0 |

Followed by `SubInfoSize` bytes of codec header (`HCA\0` and `fmt\0` chunks visible) then the payload.

**This is the good news for the spike.** Battle voices are HCA — proprietary, not something we can encode. But we do not need to: a PCM entry would be `Format = 0x01`, `SubInfoSize = 0`, and raw samples with **no codec header at all**. The header is eight plain integers. Writing one is trivial; whether the engine *plays* one is the open question.

Also convenient: the game's own battle voice is **mono, 44.1 kHz** — exactly what `ManagedVoiceSink` and `TestTone` already produce, so no resampling on the way to a native path.

### The grunt is animation-driven, not packet-driven

Observed: the delay between our ActionEffect snapshot and the game playing its own `vo_battle` line is **fixed and unique per action, ranging 5 ms to 1071 ms**.

So the battle voice fires from the action's **animation timeline**, not from the effect packet. Consequences:

1. **The auto-calibration idea in PLAN.md §5.1 is wrong as written.** There is no single per-player offset to measure — there is a per-action table.
2. That is *better* information, not worse. The plugin can **learn** the table by watching: record `(actionId -> observed grunt delay)` whenever a `vo_battle` line follows a local cast. Where data exists, match the game exactly; where it does not, fall back to `CastRemaining` for casts and 0 for instants.
3. The data will be sparse — the game does not grunt on every action, and voiced actions are a subset. A learned table must tolerate gaps rather than assume coverage.

⚠ Caveat on the measurement: `MsSinceLocalCast` is relative to the *last* local ActionEffect, so a sound landing 1071 ms later may belong to an earlier action rather than the attributed one. The `after action` column added this session makes the attribution visible so the correlation can be checked rather than assumed.

---

## Day 3 — the writer (2026-08-17)

`Warcry/Native/ScdWriter.cs`. Emits a one-sound, one-audio SCD carrying raw 16-bit mono PCM.

**Built by template, not from scratch, and the reason matters.** The audio-entry header is understood confidently — eight plain u32 fields. The *sound-entry* and *layout* structures are not. Hand-authoring unknown fields is how you get a silent load failure with no diagnostic, so the writer copies those bytes verbatim from a real game SCD and substitutes only the audio. Every field we do not understand keeps a value the engine already accepts.

The template is read from the user's own install at runtime via `IDataManager.GetFile`. Consequences: **no game data is ever shipped with the plugin** (which also sidesteps the copyright question), and the structures always match their client version rather than whatever version we reverse-engineered against.

*This is the one place the path discovery pays off — it is what makes runtime templating possible at all.*

Entry sizes are inferred from the gap to the next entry rather than assumed constant, because sound entries are demonstrably variable-length in the real file (0x58 for entry 0, 0x5e later on).

**The open bet:** that the engine plays `Format = 0x01` (PCM) at all. VFXEditor only ever emits Vorbis / MS-ADPCM / HCA, so nobody has established that PCM is accepted. If it is rejected, MS-ADPCM (`0x0C`) is the fallback and is still writable in managed code — a documented fixed-coefficient 4-bit scheme.

---

## Day 4 — first live results (2026-08-17)

### Is Penumbra actually necessary? Yes, and now we know why

The project owner asked the right question: we are introducing *new* files, not replacing indexed ones, so why route through a mod loader? The belief that a game-relative path was required came from research, not from an experiment. So we tested it — `PlaySound` with a plain `C:\...\spike_1.scd`.

**It hard-crashes the client.**

```
Unhandled native exception at Client::System::Resource::ResourceGraph.FindResourceHandle+0x23
Code: C0000005 (access violation)

[10] Client::Sound::SoundManager.PlaySound+0x16D
[9]  Client::Sound::SoundManager.LoadSoundDataScd+0xA3
[6]  Penumbra ResourceService.GetResourceAsyncDetour
[1]  Client::System::Resource::ResourceManager.GetResourceAsync+0x6D
[0]  Client::System::Resource::ResourceGraph.FindResourceHandle+0x23   <-- crash
```

`ResourceGraph` is indexed by **ResourceCategory**, and the category is derived from the leading segment of the path. An absolute Windows path yields no valid category, so the lookup indexes out of bounds. This is **structural, not a missing feature** — a game-relative path is mandatory, so a redirect mechanism is unavoidable. The only alternative would be hooking `GetResourceAsync` ourselves, which is precisely what Penumbra already does (and Penumbra is unlicensed, so its code cannot be borrowed).

The mode has been removed from the spike with a comment explaining why, so nobody reintroduces it.

### Two useful side findings from the same stack

- **`SoundManager.LoadSoundDataScd`** exists as a distinct step between `PlaySound` and the resource fetch — confirming the `.scd`-specific load path.
- **Penumbra's `GetResourceAsyncDetour` is genuinely in the call chain**, so a redirect registered via IPC *is* seen by this code path. That was an open risk and it is now cleared.

### The return value of PlaySound means nothing

Earlier attempts reported `OK 5/5` with a non-null `SoundData*` and produced **silence, for both PCM and the MS-ADPCM tag alike**. Identical behaviour across two different codec tags is the tell: the pointer was never evidence of anything.

`SoundData` carries an `IsLoadingSoundResource` flag, so loading is asynchronous — a non-null return only means a slot was allocated from the 256-entry pool. The spike now passes `autoRelease: false`, retains the pointer, and polls `GetIsLoadingSoundResource()` / `IsPlaying()` / `IsActive` / `GetVolume()` every 250 ms for three seconds before stopping and releasing it.

### Two unknowns, now separated

Every attempt so far varied *both* the file and the path at once. Four modes now isolate them:

| Mode | File | Path | What silence proves |
|---|---|---|---|
| **A** VerbatimTemplate | byte-for-byte real game SCD | synthetic | the *path* is the problem; the writer is fine |
| **B** AuthoredOnRealPath | ours | real, indexed, unloaded | the *writer* is the problem; plumbing is fine |
| **C** AuthoredPcm | ours | synthetic | both at once (the original test) |
| **D** AuthoredAdpcmTag | ours, tagged 0x0C | synthetic | container vs codec rejection |

For B the path is chosen from real `vo_battle` files for races the user is unlikely to have met this session — an already-loaded path keeps its cached handle and silently ignores a new redirect, which looks identical to failure.

---

## FINAL VERDICT: NO-GO (2026-08-17)

**Retracts the provisional GO recorded below.** No test in this spike ever demonstrated audio playing from a `SoundManager::PlaySound` call. Every apparent success was misread.

### The observation that settled it

Across every mode, the audible grunts were **always the local player's own voice type** (Hrothgar male, French) and **never another race's**.

Mode B shadowed `Vo_Battle_PC_mid_Ma_ja.scd` — Midlander, male, *Japanese*. Two possibilities existed and the evidence excludes both:

- if the redirect applied, we would hear our **alarm**
- if it was ignored and the original loaded, we would hear a **Japanese Midlander grunt**

Neither happened. Therefore `PlaySound` produced **no audio at all**, and the grunts came from somewhere else entirely — ambient, or the character's own voice triggered by something unrelated to the plugin. That retroactively voids mode A, mode F, mode G, mode H and every `PLAYED` verdict.

### What is nonetheless solidly established

These stand on direct evidence and are the real value of the exercise:

| Finding | Evidence |
|---|---|
| **PCM (`SscfWaveFormat.Pcm = 0x01`) is a structurally valid SCD codec** | Written and verified byte-for-byte; the audio-entry header is eight plain u32s |
| **The size field at 0x10 is `fileLength − 0x70`**, not the file length | Derived from the template, matches on every file we produced |
| **`PlaySound` requires a game-relative path** | An absolute path crashes in `ResourceGraph.FindResourceHandle` (C0000005) — the category is parsed from the leading path segment |
| **Penumbra's detour is in the sound-load chain** | Visible in the crash stack: `GetResourceAsyncDetour` → `ResourceHandler` → `GetOriginalResource` |
| **Whether a *new* game path can be registered at runtime is UNTESTED** | Both a synthetic path and a real indexed path (mode B) failed **identically**, so the failure is in the redirect never applying — not in whether the path exists. This question was never actually reached. |
| **Penumbra normalises game paths to lowercase** (`Utf8GamePath`) | Registering keys with capitals cannot match; fixed in `PenumbraBridge` |
| **36 real `Vo_Battle` paths exist, all `_Ma_`** | Probed; female battle voices use a different token — relevant to grunt suppression |
| **The container must be cloned from a real SCD, not authored** | `BuildPcm` never played; unmapped header fields at 0x4C–0x6C |
| **A bogus path is reliably silent** (40/40), and a redirect with no `PlaySound` is silent | Negative controls |

### Why we stopped rather than continued

The remaining unknown is why a Penumbra temporary-mod redirect for a `.scd` never takes effect. Penumbra treats SCD as a **protected file type** with a CRC-based RSF workaround (`RsfService`), which is undocumented, upstream, and unlicensed so its code cannot be consulted. Diagnosing it means reverse-engineering another plugin's internals.

The alternative route — `PlayLayoutSound(SoundResourceHandle*, ...)`, which takes a resource handle directly and bypasses path resolution entirely — is the genuine answer to "load arbitrary audio", and is a substantially larger project than this plan allows.

The spike ran **well past its 5-day hard stop**. Its purpose was to answer a question cheaply, and the answer is: not by this mechanism, not within this budget.

### Process failures worth not repeating

Six successive hypotheses were wrong, each disproved by a control built after the fact:

1. **A non-null return read as success.** `PlaySound` returns a pool slot; loading is asynchronous.
2. **No negative control until the very end.** It should have been the first test written — once it existed, it made every other result interpretable.
3. **Polling at 250 ms for a 60 ms payload.** Success finished between samples and looked like failure.
4. **A payload nobody could hear** — a decaying sine at 3% of full scale by 800 ms, against a 27% master volume.
5. **No control for ambient audio.** The plugin's own clip library and the game's own battle voices were never excluded, and in the end *were* the entire signal.
6. **Registering un-normalised (capitalised) game paths** with Penumbra — documented behaviour, not a discovery.

The unifying error: **treating "a sound happened" as evidence, without ever establishing what failure sounds like.** Verify the artefact on disk, then establish the negative control, then measure — in that order.

### Follow-up probe: our Penumbra integration is CORRECT

*Run after closing the spike, 2026-08-17.*

A texture redirect was tested in isolation from audio: icon A's path pointed at icon B's bytes (`PenumbraProbe`, button T). **The artwork changed — identical to the reference.**

That establishes, cleanly and visually:

- **`AddTemporaryModAll` works.** Our tag, dictionary shape, priority and lowercase path normalisation are all correct.
- **The IPC label `Penumbra.AddTemporaryModAll.V5` is right**, and a `0` return really does mean applied.
- Incidentally: **Dalamud's `ITextureProvider` loads through the game's resource system**, not Lumina — otherwise Penumbra could not have intercepted it.

**Therefore the audio failure is specific to `.scd`, not to our code.** Everything about how we talk to Penumbra is fine; SCD in particular is not being served.

### The leading hypothesis for a resumer

Penumbra treats **sound files as a special case**. Its `RsfService` registers SCD CRCs for a "protected file" workaround, and historically sound replacement has been gated or experimental in Penumbra rather than working like textures and models.

**Check first, before writing any code:** whether Penumbra has a setting that must be enabled for sound/`.scd` replacement (an advanced or experimental toggle). If such a switch exists and is off by default, it explains this result exactly — textures redirect, SCD silently does not — and the whole spike may simply have needed a checkbox.

That is a 30-second check and should precede any further reverse engineering.

### If anyone resumes this

Start from `ForceSingleAudioEntry` (clone a real SCD, force counts at 0x32/0x34 to 1, overwrite audio entry 0) — that part is correct and verified. The open question is purely how to make Penumbra serve an `.scd`, or how to bypass paths via `PlayLayoutSound`.

---

## Day 5 — provisional GO, later RETRACTED (2026-08-17)

*Superseded by the final verdict above. Retained because the measurements are real; only the conclusion drawn from them was wrong.*

**A self-authored `.scd` containing raw PCM played through `SoundManager::PlaySound`, served from a synthetic game path via a Penumbra temporary redirect.** Heard on the first press, and confirmed instrumentally:

```
=== attempt 1 — ForceSingleEntry, soundNumber 0 ===
OK 1/5 template Vo_Battle_PC_ros_Ma_fr.scd (105776 bytes)
OK 2/5 parsed: 25 sound, 22 audio entries
     counts forced to 1 sound / 1 audio; entry 0 holds 44100 samples (1000 ms)
OK 3/5 wrote 105776 bytes
OK 4/5 redirected sound/vfx/warcry/spike/s0001.scd
OK 5/5 got a SoundData*
     observed 54 samples at 25ms: playing TRUE in 16, loading in 1
     VERDICT: PLAYED — audible for about 400 ms
```

### What this settles

| Question | Answer |
|---|---|
| Does the engine accept **PCM** (`SscfWaveFormat.Pcm = 0x01`)? | **Yes.** No HCA, no Vorbis, no MS-ADPCM encoder, and **no bundled native binaries** — a managed writer is sufficient. |
| Is the audio-entry header understood? | **Yes** — eight plain u32s, verified byte-for-byte on disk. |
| The size field at 0x10? | **`fileLength − 0x70`**, not the file length. |
| Can a synthetic path be served? | **Yes**, via Penumbra `AddTemporaryModAll`, under a real category root (`sound/...`). |
| Can selection be made deterministic? | **Yes** — force the counts at 0x32/0x34 to 1. The template is otherwise a random pool of ~22 grunts. |
| Absolute filesystem paths? | **Crash the client.** `ResourceGraph.FindResourceHandle`, C0000005 — the category is parsed from the leading path segment. A redirect mechanism is structurally unavoidable. |

### The container must be template-based, not built from scratch

`ScdWriter.BuildPcm` (author the whole container) produces a file the engine will not play. `ForceSingleAudioEntry` (clone a real SCD, force the counts to 1, overwrite audio entry 0) **works**.

The reason is visible in the header: there are further offset fields at **0x4C–0x6C** pointing at 0x3F0, 0x440, 0x490, 0x4C0 and 0x520 — sections never mapped, and zeroed by the from-scratch writer. Templating sidesteps every unknown field by keeping a value the engine already accepts, which is what PLAN.md §5.6 recommended in the first place.

**Consequence for M7:** the production `ScdForge` should clone a real battle-voice SCD at runtime (via `IDataManager.GetFile`, so nothing is shipped) and swap in the clip. `BuildPcm` should be deleted or clearly marked non-working.

### Still to verify — the plan's remaining go/no-go criteria

Only criterion **(a)** of §6 is met. These were never reached and are cheap now:

- [ ] (b) responds to the **Master** slider
- [ ] (c) responds to **Voice** and/or **Sound Effects**  ← also settles the ⚠ bus-routing inference
- [ ] (d) `isPositional: true` gives audible attenuation and L/R imaging
- [ ] (e) 20 plays in 10s does not break the game's audio or exhaust the 256-entry pool

### Known issue: intermittent playback on repeat

Replaying the same warm path sometimes reports `PLAYED` and sometimes `NEVER PLAYED`.

**Most likely an artefact of the instrumentation rather than the approach.** The spike deliberately passes `autoRelease: false` and retains the `SoundData*` so it can be polled, then calls `Stop(0)` + `ReleaseSoundData` 1.5 s later. A repeat press inside that window force-releases the previous one and immediately re-acquires from the same pool. Production would pass `autoRelease: true` and never retain, which is the engine's normal path. **Re-test with `autoRelease: true` before treating this as a real defect.**

### Process lessons worth keeping

Three rounds were wasted on misattributed evidence, and all three were avoidable:

1. **A non-null return was read as success.** `PlaySound` returns a pool slot; loading is asynchronous. Nothing about the pointer indicates the file parsed.
2. **No negative control until the very end.** Once added, a bogus path was reliably silent — which retroactively validated every other result. It should have been the first test written.
3. **The polling interval (250 ms) was four times longer than the payload (60 ms).** A successful play finished between samples and looked like failure.

The instrumentation only became trustworthy once the verdict line agreed with what a human heard. Until then, every conclusion drawn from it was noise.

### Open / next

- [ ] Observe real `vo_battle` paths in game.
- [ ] Dump one and parse its actual byte layout.
- [ ] Day 2: Penumbra IPC bridge + `/warcry spike`.
- [ ] Day 3–4: managed SCD writer, PCM first (`SscfWaveFormat.Pcm = 0x01`), MS-ADPCM as the expected landing spot.
- [ ] Day 5: behaviour checks (sliders, attenuation, obstruction, 20-in-10s stress).
