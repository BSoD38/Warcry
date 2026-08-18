# Native audio spike — log

**Question:** can Warcry play a self-authored `.scd` through the game's own sound engine, so the game does the mixing, 3D positioning and volume routing instead of NAudio?

**Time box:** 5 working days, hard stop. Throwaway branch until a GO.
**Go/no-go criteria:** see `PLAN.md` §6.

---

## Rework to native-only (2026-08-18) — what the next in-game session must measure

The sound system was rebuilt on top of the GO: `SinkMode.NativeOnly` routes every gameplay
line through the engine or drops it with a visible reason, and `PackBuilder` compiles and
registers every mapped variant ahead of time, warming per job. Details in `PLAN.md` §0 M7.

The **Sound pack tab** now carries the checklist for everything still unmeasured. Run it
and record the verdicts here:

- [ ] **(b) Master slider** — fire, zero Master, fire again: second must be silent.
- [ ] **(c) Voice / Sound Effects slider** — same, once per slider. Note *which* one
      affects it: that finally answers the ⚠ bus-routing inference from Day 1.
- [ ] **(d) Positional attenuation** — fire at offsets 0 / 10 / 25 yalms: volume must fall
      with distance, and image L/R with camera turn.
- [ ] **(e) 20 plays in 10 s** — the tab paces it and reports accepted/refused plus the
      active-sound count before/after. The game's own audio must keep working. While
      there, re-check the `SoundDataRefCount` climb (see "phantom PLAYED" below) — it has never been
      re-measured with `autoRelease: true`, which is what production passes.
- [ ] **(f) `speed` argument** — ×0.5 / ×1 / ×2 must change pitch and length. ⚠ This gates
      `NativePitchViaSpeed` (default **on**): the game passes non-1 speeds for its own
      sounds, but nobody has measured it on one of *our* containers. If the three sound
      identical, turn the setting off — pitch then bakes at half-semitone steps instead.

Also note while testing: warm-up at volume 0 (`Warm`) must stay inaudible, and switching
jobs must warm the new job's set (watch the tab's warm counter move).

---

## ✅ GO — it works (2026-08-17)

**A clip we encoded, in a container we assembled, played through the game's own sound
engine.** Two seconds of 440/880 Hz alarm in place of a Hrothgar battle grunt.

The route, end to end, every link independently verified:

1. Clone a real `Vo_Battle` SCD at runtime via `IDataManager.GetFile` — nothing shipped, no
   copyright question, always the right client version.
2. Encode the clip as **mono MS-ADPCM** (`Format 0x0C`) with a 50-byte `WAVEFORMATEX` codec
   header. Confirmed against a real game entry: `subInfo 0x32`, `wFormatTag 2`, 7 coefficient
   pairs.
3. Append it past the original end of file and point **only the scoped group's** audio
   indices at it, so damage and death grunts stay native.
4. Serve it from a **content-addressed** synthetic path — `sound/vfx/warcry/spike/{sha}.scd`
   — through a Penumbra temporary redirect.
5. `SoundManager::PlaySound(path, …, soundNumber: <group>, …)`.

This retracts the NO-GO below in full, and confirms the reopening's central claim: nothing
about the mechanism was ever broken. Six rounds of "silence" were six different instrument
faults — a stale cached handle, a recycled pool slot, world-vs-listener coordinates that
turned out fine, an unmeasured positive control, a payload nobody could hear, and finally a
codec the engine does not implement.

**What is left is engineering, not research.** PLAN.md §6 criteria (b)–(e) — master slider,
Voice/SE slider, positional attenuation, 20-plays-in-10s — have still never been reached.

### A cloned container is authored to be intermittent

Reported after the sink went live: native playback worked but "triggers the actual audio
very rarely". Two things in the template cause it, and cloning inherits both.

**The group body's float at +0x08 is 0.4335, identical in all five groups.** Read as a
per-group chance to fire, which is exactly why a character grunts on some swings and not
others (the Day 2 observation). It is not volume: the 128-byte group *header* blocks hold
four `1.0` floats, so volume lives there.

⚠ Inferred, not confirmed. But setting it to 1 in a container of our own is safe either way
— if the reading is right playback becomes certain, and if it is actually a volume our clips
get louder and the plugin slider compensates.

**Group 0's cumulative weights total 30, not 100.** If the engine rolls against a fixed
denominator rather than the group's own total, that is a second independent source of loss
stacked on the first.

`ScdInspector.ForceDeterministicPlayback` fixes both on the forged copy: the chance goes to
1, and each group's running total is rescaled to end at 100 — correct whichever denominator
the engine uses, and it moves no offsets, only rewriting `u16`s already in each record.
Verified idempotent, container still parses, audio entry untouched:

```
before  group 0: 6 rec, total  30, playChance 0.4335
after   group 0: 6 rec, total 100, playChance 1      weights [17,33,50,67,83,100]
```

The inspector now prints `playChance` per group.

### A real bug the milestone run exposed

Inspecting `sound/battle/mon/13157.scd` (30 groups, 1.1 MB) produced ids like `-16777216`,
a 251-record group, negative weights and audio indices past the end of the file. The
group-body layout — bodies contiguous after the last fixed-size header — was inferred from a
battle-voice file and does **not** generalise.

`ParseGroups` now validates and refuses: group id must equal its index, record count must be
plausible, every audio index must exist, cumulative weights must be non-decreasing. Any
failure discards the whole parse and says so. This is a safety fix as much as a display one —
a wrong index set would make `PointAudioAtOneEntry` overwrite the wrong bank. Verified that
the battle-voice file still parses to its 5 known groups and a corrupted one yields zero.

---

## REOPENED (2026-08-17) — the NO-GO below was not earned

**Status: the NO-GO recorded further down is withdrawn.** Not because it was pessimistic,
but because it was *unfounded*: it names a cause the evidence cannot support, and it rests
on an experiment that was never run.

### Three reasons it does not hold

**1. The stated cause contradicts shipped practice.** The NO-GO blames Penumbra treating
`.scd` as a protected file type behind an undocumented CRC/RSF workaround. But injecting
*brand-new* SCD files at *new* game paths through Penumbra is ordinary, widely-used
modding: Soundy converts WAV→OGG→SCD and injects straight into Penumbra, VFXEdit exports
SCD replacements as Penumbra modpacks, and beginner guides treat it as routine. No evidence
of an SCD gate was found. There *is* one real, relevant constraint — several reports
(Penumbra issue #275) that SCD redirects apply only from the **Base/default collection**,
which makes sense because a `PlaySound` call carries no character context for Penumbra to
resolve against.

**2. There was never a positive control.** Every mode wrote a file, or registered a
redirect, or both. Not one test asked *"does `PlaySound` produce audio at all, on stock
game data, with nothing of ours involved?"* The negative control proves the instrument is
not hallucinating; it does not prove the instrument works. Without a positive control,
silence is uninterpretable — it cannot distinguish a bad file from a bad redirect from a
call that was never going to work.

**3. Mode B is the tell, read the other way.** A real, indexed, valid game SCD played
*nothing* — not our alarm, and not the original Japanese grunt either. The NO-GO reads that
as "the redirect never applied". But a redirect that fails should leave the original audio
playing. Producing neither points at **the call**, not the file and not Penumbra.

### The most likely single cause, and it is embarrassing

`PlaySound` was always given `isPositional: true` together with the player's **world**
coordinates. If the engine wants listener-relative coordinates, every attempt in the spike
was emitted a couple of hundred units from the listener and attenuated to nothing. That one
mistake would produce exactly the observed result — total silence in every mode, including
the verbatim-real-file control — with no fault anywhere else in the stack.

This is now measurable rather than arguable: the watcher records the emitter position *and*
the player's world position for every call the game makes, and the Game sounds tab shows
the distance between them. On a real `vo_battle` line, near-zero means listener-relative.

### What was built for the next session

| Piece | What it replaces |
|---|---|
| `SpikeMode.StockGamePath` | Guessing. A real indexed path, no file, no redirect — pure `PlaySound` on stock data. |
| `PenumbraBridge.Verify` / `ResolveDefault` | Inferring "did the redirect apply" from whether a sound was heard. One IPC call, no audio, targets the **default** collection. |
| `SoundDiagnostics` | The non-null-pointer fallacy. Reads `SoundData` *and its `SoundResourceHandle`* — filename, byte length, load state, active-list membership. When the handle reports the byte count of the file we wrote, the redirect demonstrably reached the game. |
| Full 18-argument capture + per-row **replay** | Guessing at `a9`, `a13`, `a15`, `a18`, `midiNote`, `soundNumber`, `speed` and the position convention. The game calls `PlaySound` successfully hundreds of times a minute; replay one verbatim, then substitute one thing. |
| `PlayEntry` (`PlaySystemSound`, `PlayCutsceneVoSound`) | Fixating on the 18-argument entry point. `PlayCutsceneVoSound` takes **one** argument — there is nothing to get wrong but the path. |
| `isPositional` defaulting to **false** | The distance-attenuation trap above. |
| Three-way verdict | "Played / never played". *Accepted but silent* (on the active list, no audio → container or codec) and *never accepted* (never on the list → path or arguments) point at completely different suspects. |

### Running order — do these in this order, and stop at the first surprise

1. **Game sounds tab.** Log with filter `vo_`, use a few actions, and read the `dist`
   column on a `vo_battle` row. World or listener-relative? Record the answer.
2. **Replay a captured row verbatim** (blank substitute path). This must make a noise. If it
   does not, nothing else on the tab means anything and the problem is the call.
3. **Spike tab → §1 Engine state.** Confirm `disabled=False`, the Voice/SE buses are not
   muted and their effective volume is non-zero.
4. **Spike tab → §3, payload = Stock game path.** The positive control, via `PlaySound`.
   Then repeat with entry point `PlayCutsceneVoSound`.
5. **Spike tab → §2, "Register a redirect and verify it".** Yes or no, no listening.
6. Only now run the authored payloads — and read the resource-handle byte count in §4
   rather than listening.

### What each outcome means

| Observation | Conclusion |
|---|---|
| Step 2 silent | The call is wrong, or the mixer is. Penumbra and the writer are exonerated. |
| Step 2 audible, step 4 silent | Our argument tuple is wrong. Diff it against the captured row. |
| Step 5 says NOT APPLIED | Penumbra is the blocker after all — and now we know it without guessing. Next: try the Base collection explicitly, or write our own resource hook. |
| Step 5 APPLIED, handle byte count matches, still silent | *Now* the container is genuinely the suspect, and the spike's SCD work becomes the live question. |

### What is still unknown

- ~~Whether `soundNumber` selects an entry in a multi-entry container.~~ **Answered** — it
  selects a *sound group*, and the group picks a waveform by weighted random. See
  "Container decoded" below.
- Whether the engine plays `Format = 0x01` (PCM). Still untested — nothing ever played, so
  the Day 5 claim that PCM works and the final verdict's claim that `BuildPcm` does not are
  *both* unsupported. Neither writer path has been fairly tried.
- Whether the game's audio backend is XAudio2. The exe references `XAudio2_7.dll`,
  `X3DAudio1_7.dll`, `XAPOFX1_5.dll` and `XactEngine3_7.dll`, all four are installed on this
  machine, and **none is loaded in the running process** — so probably not. Ambiguous
  because Warcry's own NAudio output pulls in `dsound.dll` and `winmm`. To settle it: unload
  Warcry and re-check the module list.

### The rule that would have prevented all of this

Establish the positive control before the negative control, and both before any experiment.
"A sound happened" is not evidence until you know what success and failure each sound like.

---

## Steps 1–2 ran, and both landed (2026-08-17)

```
sound/voice/Vo_Battle/Vo_Battle_PC_ros_Ma_fr.scd
  volume=1.000 fadeIn=0 speed=1.000
  pos=(418.89, -148.75, -422.31) positional=True
  playerPos=(418.89, -148.75, -422.31) distance=0.00
  a9=0 soundNumber=3 autoRelease=False category=Player
  a13=False midiNote=-1 a15=False defaultFadeOut=False a18=False
  -> returned a SoundData*
```

**`PlaySound` works.** A captured call replayed verbatim produced audio. The call mechanism
was never broken, and the NO-GO's premise is dead.

**Confirmed again by the positive control**, and this one is airtight. `StockGamePath` plays
`Vo_Battle_PC_mid_Ma_ja.scd` — a *Japanese Midlander* — from a French Hrothgar character,
with no file written and no redirect registered. It produced a Midlander grunt. A voice in
the wrong race, gender and language cannot be the player's own character and cannot be
ambient, which is exactly the confound that voided every result in the original spike. The
final verdict below explicitly said "if it was ignored and the original loaded, we would
hear a Japanese Midlander grunt… neither happened." It happens now.

**Coordinates are WORLD, not listener-relative.** `distance=0.00` — the game passes the
emitter's world position, which for a battle voice is the player's own. So the original
spike's `isPositional: true` with world coordinates was *correct*, and the leading
hypothesis in the section above is **wrong**. Ruled out, cheaply, which is what it was for.

**`soundNumber` is not always 0.** The game passed `3`. The spike only ever passed `0`.

Two behaviours remained: replaying gave a *random* grunt from the right voice type, and it
was *sometimes* silent. Both are now explained.

---

## Container decoded (2026-08-17)

Parsed from the real `Vo_Battle_PC_ros_Ma_fr.scd` (105,776 bytes) with `ScdInspector`, and
verified by round-tripping the shipping code over it.

### `soundNumber` selects a group, not a waveform

The count at **0x30** — previously written off as an unknown "table3 count" — is the number
of **sound groups**. The table at **0x40** points at that many 128-byte group *headers*
(0x170, 0x1F0, 0x270, 0x2F0, 0x370), and the variable-length group *bodies* follow
contiguously after the last header, starting at **0x3F0**, in the same order.

Each body is a 32-byte header — **first byte is the record count**, the `u32` at **+0x0C**
is the group id — followed by that many **8-byte records of four `u16`**:

```
u16 cueIndex        index into the sound-entry table
u16 audioIndex      index into the audio-entry table — what actually plays
u16 cumulativeWeight running total; the engine rolls against the group's final value
u16 localIndex      position within the group
```

`PlaySound`'s `soundNumber` picks the group. The group then picks a record by **weighted
random**. This file:

| soundNumber | offset | choices | total weight | audio entries reachable | what it is |
|---|---|---|---|---|---|
| 0 | 0x3F0 | 6 | 30 | 0–5, evenly | attack (light) |
| 1 | 0x440 | 6 | 30 | 9–14, evenly | **damage taken** |
| 2 | 0x490 | 2 | 50 | 18, 19 at 50% each | **death** |
| 3 | 0x4C0 | 8 | 50 | 20, 21 at 14%; 0–5 at 12% | **attack — what the game passes for an action** |
| 4 | 0x520 | **0** | 0 | none | unused |

**That is the random grunt, exactly.** `soundNumber=3` is an eight-way weighted roll. No
caller can narrow it: the choice is data in the file, not a parameter.

### The banks are not interchangeable

The right-hand column was established **by ear** (2026-08-17, sweeping the replay override)
and lines up with the parsed table and the audio lengths independently — damage grunts are
the shortest bank (~180–240 ms), death the longest, attack in between. Three sources, one
answer.

This matters more than the randomisation does. **Retargeting every audio index would make
the player's voiceline fire every time they took a hit or died.** Only groups 0 and 3 are
attack; 1 and 2 must be left alone.

One behaviour the file does not explain: **out-of-range and empty `soundNumber` values fall
back to the attack bank** rather than playing nothing. Group 4 parses as zero records, but
4–7 all produced attack grunts in game. So an empty group is not a usable way to get
silence.

### The empty audio entries are padding, not silence

Six of the 22 audio entries — 6, 7, 8, 15, 16, 17 — are 32-byte stubs with
`dataLength = 0` and `format = 0xFFFFFFFF`. But **no group record references any of them**;
the records skip straight from audio 5 to audio 9 and from 14 to 18. They are gaps in the
numbering between banks, not a 27% chance of nothing.

So the intermittent silence was **ours**: the spike passed `autoRelease: false`, retained
the pointer, and force-released it on the next press. Pressing replay twice inside 1.5 s
killed the first sound mid-playback. `ReplayCapture` now honours the `autoRelease` toggle
instead of hardcoding `false` — tick it, or leave a gap between presses.

### The consequence for the product

You cannot ask for a specific waveform. You do not have to: records reference audio by
**index**, and the table at 0xE0 is what resolves an index to an offset. Point the indices
*one group can roll* at a single entry holding our clip, and the randomisation still runs —
it just has nothing left to choose between. The empty stubs stop mattering in the same
stroke, because nothing references them anyway.

`ScdWriter.PointAudioAtOneEntry(template, audioIndices, …)` does exactly that, and it is the
shape a production `ScdForge` should take. Pass `ScdInspector.AudioIndicesFor(group 3)` and
the character keeps grunting normally when hurt and killed while the action voice becomes
ours. Pass null only if replacing every bank is genuinely what you want.

The clip is **appended past the end of the original file** rather than overwriting entry 0,
so every byte the untouched banks depend on survives, and the payload has no length limit.

Verified end to end by compiling the shipping `ScdWriter`/`ScdInspector` against the real
dumped file — group 3 scope, 1.5 s payload:

```
group 0: audio [0,1,2,3,4,5]          attack (light)
group 1: audio [9,10,11,12,13,14]     damage taken
group 2: audio [18,19]                death
group 3: audio [0,1,2,3,4,5,20,21]    attack, used for actions
group 4: audio []                     unused

8 of 22 audio indices retargeted to an entry appended at 0x19D30
retarget correctness           : PASS   (exactly group 3's indices, no others)
damage bank (9-14) untouched   : True
death bank (18,19) untouched   : True
size field == len-0x70         : True
groups still parse             : 5 (err='')
```

Every differing byte inside the original length was accounted for: the size field at 0x10,
and eight 4-byte slots in the audio offset table. Nothing else moved.

Note that groups 0 and 3 share audio 0–5, so scoping to group 3 also changes group 0. Both
are attack banks, so that is the desired behaviour rather than a leak.

### What this opens up, and its one hard limit

The game already fires `soundNumber = 3` on the action's own animation timeline — the
per-action offset measured on Day 2, for free, positional, on the right bus, with no
`PlaySound` call of ours at all. Redirect `Vo_Battle_PC_{race}_{sex}_{lang}.scd` with only
group 3 retargeted and the player's clip plays on every action while damage and death stay
native.

**The limit: one clip per session.** Once the game has loaded a path its resource handle is
cached, so the content cannot change per action. Per-action clips still require our own
`PlaySound` on our own synthetic paths — which is what §2 of the spike tab is still waiting
to answer.

---

## Penumbra serves `.scd` — and the harness had a stale-path bug (2026-08-17)

Probe A reported **APPLIED**, and the resource readout proved it independently:

```
handle  name='C:/Users/.../pluginConfigs/Warcry/.cache/scd/spike_6.scd'
        fileSize=105776  length=105776  data=present
        loadState=7  readState=2  lastIO=5  refCount=6  soundRefs=5
        MISMATCH — we wrote 282208 bytes, the engine holds 105776
```

The handle's filename is **our disk path**. Penumbra's redirect reaches the game's resource
system for `.scd`, on a synthetic path that has no sqpack index entry at all. That was the
last open question from the reopening, and the answer is yes.

### But the engine was holding a different file

Three independent signs, all pointing the same way:

1. `refCount=6, soundRefs=5` on the *first* play of a supposedly brand-new path. A fresh
   resource cannot already carry five sound references.
2. `length = 105776` — exactly the untouched template size, when the file on disk was
   282,208 bytes (verified: written 20:36, still 282,208).
3. The audio that played. Measured `elapsed` values of **0.803 s, 0.666 s, 0.443 s,
   0.373 s** match the *unmodified* group 3 entries precisely — audio 20 (794 ms), audio 21
   (631 ms), audio 0–5 (346–491 ms). It was playing the original file, note for note.

**Cause: the synthetic path was named from the attempt counter, which resets to zero on
every plugin reload.** Reloading and re-running produced `s0006.scd` a second time inside
one *game* session. The engine had cached a resource handle for that path from the earlier
load and never re-read the file, so the new 282 KB payload was served to the engine as the
105 KB one from twenty minutes earlier — and the report blamed the container.

`PenumbraBridge`'s own doc comment warned about exactly this ("every distinct payload gets
its own never-before-used synthetic path"). The implementation honoured it within a plugin
load and not across one.

**Fix: content-addressed paths.** `sound/vfx/warcry/spike/{sha256[:8]}.scd`, with the cache
file named the same way. Identical bytes reuse a path, which is correct and warm; different
bytes can never collide with a cached handle. It matches how `ClipLibrary` already stores
clips. Legacy `spike_*.scd` files are deleted on construction.

Also added, because conflating them is what made the round unreadable: the report now states
**what we wrote, what is on disk, and what the engine holds** as three separate numbers, and
a mismatch names the stale-handle cause instead of blaming the redirect.

### Our bytes now reach the engine

With content addressing in place, the readout is unambiguous:

```
handle  name='.../570c0fab618776e1.scd'
        fileSize=282208  length=282208  data=present
        MATCH — 282208 bytes is exactly the file we wrote. Our bytes are in the engine.
```

Redirect, container surgery and file delivery are all proven. What the engine does *with*
those bytes is now, at last, the only question.

### And the harness lied again — recycled pool slots

The same run reported `PLAYED — audible for about 825 ms` while nothing was audible. The
cause is visible two lines up:

```
handle  name='sound/foot/dev/6994.scd'
```

A **footstep**. The run had `autoRelease = true`, so the engine reclaimed the `SoundData`
slot and handed it to another sound while the spike was still polling the pointer. Every
`PLAYED` in that log was a measurement of somebody else's audio.

Two fixes:

- **Never poll a slot the engine owns.** With `autoRelease` on, the immediate readout is
  printed and polling is skipped entirely, with the verdict "not measured — listen instead".
- **Identity check on every poll.** The `SoundResourceHandle` pointer is captured on first
  sight and compared each tick; if it changes, the measurement is abandoned and reported as
  recycled rather than believed.

The one trustworthy line in that run was the cold attempt: `ACCEPTED BUT SILENT`, on the
active list for **2 of 55 samples**. The engine took our file, held it for ~50 ms, produced
nothing, and dropped it.

### The ladder — one variable per rung

Three things differ between an untouched file and one carrying our clip, and every silent
result so far has changed all three at once:

| Rung | Payload | Adds | Reads as |
|---|---|---|---|
| 1 | `RetargetToExistingBank` | offset-table rewrite, in place | death grunt → the rewrite works |
| 2 | `AppendExistingBank` | + entry appended past EOF, game's own HCA | death grunt → appending is fine, only the codec is left |
| 3 | `OneClipEverywhere` | + our encoded audio | alarm → done; silence → the codec |

Rung 2 is the one nobody thought to build, and it is the likeliest failure point after the
codec: if the engine will not follow an offset past the original file length, no encoder
would ever have helped and the fix is to overwrite an existing slot instead of appending.

Both rungs verified against the real file before shipping — rung 1 changes 18 bytes, all
inside the offset table; rung 2 appends a byte-identical 10,382-byte copy of audio entry 18
(32 header + 120 codec + 10,230 data, format 0x1A) and leaves every other index alone.

**Run all three with `autoRelease` OFF.**

### Ladder result: rung 2 passes, rung 3 fails

**Rung 2 played the death grunt on `soundNumber = 3`, every time.** So all of this is proven
working: the redirect, the offset-table rewrite, appending an entry past the original end of
file, and the group/weight machinery. A file we constructed, containing an audio entry at an
offset beyond the original EOF, plays.

**Rung 3 — the same construction with our PCM in that entry — is silent.** The two differ in
exactly one thing: the contents of the appended audio entry. Rung 2 copies the game's HCA
(`format 0x1A`, `SubInfoSize 120`, codec header + HCA data); rung 3 writes `format 0x01`,
`SubInfoSize 0`, raw 16-bit samples.

**Conclusion: the engine loads our file and will not decode the audio entry we wrote.** That
is the first time in this spike a failure has been isolated to a single variable.

### One more instrument fixed: phantom PLAYED

Rung 3 still reported `PLAYED — elapsed reached 0.35s` on a **2.0 s** payload, alternating
with `ACCEPTED BUT SILENT` for the same bytes. Nothing was audible either way. A `SoundData`
comes from a shared pool and carries residue from whatever held the slot before, so a reused
slot reads as a healthy short sound.

The verdict now cross-checks elapsed time against the payload's own duration: reported
playing, but under half the expected length, is reported as `SUSPECT — almost certainly pool
residue, not our audio` rather than `PLAYED`.

Also visible and worth noting: `refCount` climbs 19 → 22 across four plays and never falls,
so `ReleaseSoundData` is not decrementing the resource handle's `SoundDataRefCount`. Harmless
for a spike, a leak for production. **Reload the plugin between ladder runs.**

### Next: survey, do not guess

The temptation is to pick a codec and write an encoder. That is the mistake this spike has
already made twice. Instead, `SurveyFormats` reads every `.scd` the watcher logged this
session plus a dense probe of `sound/foot/dev/{n}.scd` — a real observed path family, short
sounds, least likely to be worth compressing — and tallies the `format` field across every
audio entry.

- **PCM turns up** → it is live, and that entry is a template to diff ours against. The bug
  is in our header, not the codec.
- **No PCM in several hundred files** → `Format 0x01` is dead code in the engine, and the
  choice is between MS-ADPCM and Vorbis, decided by whichever *does* appear — because that
  one arrives with a real structure to copy.

Templating from real data is the only technique that has worked in this whole exercise.

### Survey result: MS-ADPCM, and no PCM at all

26 files read:

| Format | Entries | Files | Example |
|---|---|---|---|
| `0xFFFFFFFF` empty stub | 790 | 20 | `sound/battle/mon/13157.scd` |
| **`0x0C` MS-ADPCM** | **267** | **18** | `sound/battle/mon/13157.scd` |
| `0x1A` HCA | 92 | 8 | `sound/battle/enpc/SE_ENPC_d1077_Tequila_Alpaca_foot.scd` |
| `0x01` PCM | **0** | **0** | — |

**Not one PCM entry.** That closes the question the spike has carried since Day 3: `Format
0x01` is dead code in the engine. Day 5's "PCM is a structurally valid SCD codec" was true of
the *container* and false of the *engine*, and rung 3 is where that finally showed.

MS-ADPCM is the format the engine demonstrably plays that we can also write — HCA is
proprietary, and Vorbis would mean a new dependency for a codec the game uses less.

### `MsAdPcm` — the encoder

`Warcry/Native/MsAdPcm.cs`. Mono, 4 bits per sample, no dependencies, ~250 lines. Mono
deliberately: the game's own battle voice is mono 44.1 kHz and so is everything
`ManagedVoiceSink` produces, so nothing resamples or downmixes on the way in.

Two details worth keeping:

- **Predictor chosen per block.** All seven fixed coefficient pairs are trialled and the
  lowest-error one wins. Cheap over a 500-sample block and noticeably better on speech,
  where a single fixed predictor fits badly.
- **Initial step size derived from the block.** A fixed `idelta` of 16 makes the adaptation
  climb for a dozen samples at the start of every block, which on loud material is an
  audible click every 500 samples. It is now seeded from the block's mean first difference.

Verified by round-tripping the encoder against its own decoder on the spike's 2 s alarm:

```
88200 samples -> 45312 bytes (3.89x)   blockAlign 256, 500 samples/block
round-trip SNR : 48.8 dB
worst error    : 3514 of 32767
```

And through the full container pipeline:

```
entry: dataLen=45312 ch=1 rate=44100 fmt=0xC subInfo=0x32
scoped indices retargeted : True     others untouched : True
size field == len-0x70    : True     reparses         : True
```

### ⚠ The codec header is authored, not templated

`BuildCodecHeader` emits the documented Microsoft layout — 18 bytes of `WAVEFORMATEX` plus
32 bytes of ADPCM extra (`cbSize`, `samplesPerBlock`, `numCoef`, seven coefficient pairs) —
which comes to **50 bytes, 0x32**. That is a prediction, not an observation, and authoring
blind is exactly the habit that has cost this spike the most.

The inspector now hex-dumps the codec header of the first entry of each format, so
`/warcry scdinfo sound/battle/mon/13157.scd` prints a real MS-ADPCM header to diff against.
**Check that a real entry reports `subInfo 0x32` and that the bytes line up before trusting
the encoder.** If they differ, copy the real structure and substitute only the fields that
must change.

### Tools

- `ScdInspector.Describe` — audio entries (flagging stubs) and the full group/weight table,
  annotated with what each `soundNumber` means.
- `ScdInspector.AudioIndicesFor(group)` — the index set for a scoped retarget.
- `/warcry scdinfo <game path>`, or **Inspect container** on the spike tab. Blank argument
  inspects the default template.
- Spike tab: payload *"Our alarm on one bank"* plus a **Scope** selector, defaulting to
  group 3, with a nudge when `soundNumber` does not match the scope.
- Game sounds tab: **Override soundNumber** on replay, to sweep the groups by ear.

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

## FINAL VERDICT: NO-GO (2026-08-17) — ⚠ SUPERSEDED, see REOPENED at the top

> **This verdict is withdrawn.** Its observations stand; its conclusion does not. It
> attributes the failure to Penumbra refusing to serve `.scd`, a cause contradicted by
> everyday modding practice, and it was reached without ever running a positive control.
> Retained in full because the negative results are real and the process lessons are worth
> keeping.

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
