# Warcry — FFXIV Dalamud Plugin

## Project Context

Warcry is a Dalamud plugin for Final Fantasy XIV that plays user-supplied voice clips in 3D space when a character uses an action. Clips are mapped per action and per voice type from an in-game ImGui editor.

Product decisions that bound the design space (do not re-litigate; see the project memory and `docs/PLAN.md`):

- **Audience is a filter, never a hardcoded `== LocalPlayer`.** M9 (2026-08-24) replaced the self-only seam with `Gating/AudienceFilter`: Self / Named / Party / Alliance / Friend / Other, plus a block list and a distance gate. **A caster's buckets are a set, not a first match** — a friend in your party is `Party | Friend`. The narrowest *enabled* bucket is the cooldown tier; the whole set is what a profile's target matches against. **Self-only is still the shipped default** and the v5 migration forces it, because an update that starts voicing strangers is a nasty surprise.
- **Profiles carry a target** (`ProfileMatch.Audience` + `Names`), so different people can get different clips. Specificity is banded so *who* outranks *what they sound like*: names 128, audience tier ×32, appearance ≤18. A profile with no target means "everyone", which is why pre-M9 profiles needed no migration.
- **Distribution: self-hosted third-party repo** (`repo.json`), not DalamudPluginsD17 — official-repo review constraints (native deps, UGC moderation) are not binding.
- **Native audio path**: a hard dependency on Penumbra IPC is acceptable; `.scd` redirection puts clips on the game's own voice channel with native 3D falloff. **Verified working in game (2026-08-18)**: sliders, positional attenuation, load test, engine-side pitch.
- **Timing: at impact.** `ActionEffectHandler.Receive` fires at *snapshot*, roughly one slidecast window (~0.5 s, latency-dependent) before the cast bar visually completes — the offset is on the critical path.

Load-bearing invariants:

- **Native mode means native only.** When the native sink is selected, no gameplay line may play through NAudio. A refusal is a visible, counted drop with a reason — never a quiet substitute. NAudio survives only as the decoder and the editor's audition path.
- **Selection/probability lives in the plugin resolver** (weighted roll + no-immediate-repeat), never compiled into SCD groups — groups are forced deterministic, one container per clip.
- **Compile everything, activate per job.** All mappings encode ahead of time; only reachable clips are warmed, because warmed resource handles are unevictable client memory. "Reachable" is the union of jobs from `Gating/CrowdWatch` — yours plus the audible players around you, refreshed at 1 Hz. It is an *earned* set, never "every job in the game".
- **The detour allocates nothing for the audience.** Everything the filter needs is read off `Character*` into a `Game/CasterFacts` inside `ActionWatcher`; names are compared as FNV-1a hashes (`Game/PlayerId`) because `NameString` allocates per cast. `CrowdWatch` builds the identical struct from the object table, so both paths classify through one code path and cannot drift.
- **Grunt suppression touches the attack banks only.** A `Vo_Battle` container's `soundNumber` picks a group: **1 is damage taken, 2 is death**, everything else is attack. `Audio/GruntSuppressor` and `Native/ScdForge` both honour that split, so a character Warcry has taken over still grunts when hurt and killed. Suppression zeroes the gain and always calls `Original` — the game passes `autoRelease: false` and keeps the `SoundData*`, so refusing the call would break its bookkeeping.

`docs/PLAN.md` is the verified technical plan (API level, hook target, offsets, repo mechanics, milestones). **Read it before re-researching anything.** Items marked ⚠ in it are genuinely unverified.

## Tech Stack

- **.NET 10 / C# 14** via **`Dalamud.NET.Sdk/15.0.0`** — the SDK supplies `net10.0-windows`, LangVersion 14, x64, nullable, unsafe blocks, the lock file, DalamudPackager, and the Dalamud references. **Do not set any of these in the csproj.**
- **NAudio 2.3.0 — pinned.** NAudio 3.0.0 is a hard breaking release (`Read(Span<byte>)`, `WaveOutEvent` → `WaveOut`, assembly split). Do not upgrade. See `docs/PLAN.md` 5.6.
- **NAudio.Vorbis 1.5.0** — ogg decoding.
- **Penumbra IPC** — hard dependency for the native sink (`Native/PenumbraBridge.cs`).
- **ImGui** via Dalamud windowing for all UI.
- No database, no auth, no web stack, no test project — verification happens **in game** (see Workflow).

## Architecture

Single project, feature folders — each folder is a self-contained concern:

```
Warcry/
  Plugin.cs            # Entry point; [PluginService] static properties (PROPERTY, not field)
  Configuration.cs     # Dalamud config persistence
  Audio/               # IVoiceSink abstraction: Native / Managed / Composite sinks,
                       #   PlaybackScheduler (impact-timing offset), GameVolume, SinkMode,
                       #   GruntSuppressor (silences the game's own battle grunt)
  Clips/               # Clip library and decoded-clip cache
  Detection/           # ActionWatcher hooks ActionEffectHandler.Receive → CastEvent
  Game/                # Game-side lookups and value types: CasterKey, CasterFacts,
                       #   CasterIdentity, AudienceBucket, PlayerId, NamedPlayer,
                       #   JobIndex, VoiceSlotTable
  Gating/              # Gates + AudienceFilter (who) + CrowdWatch (how many) +
                       #   Throttle (how often) — when a line is allowed to fire
  Native/              # SCD pipeline: ScdForge/ScdWriter/ScdInspector, MsAdPcm encoder,
                       #   PackBuilder (per-job pre-compiled packs), PenumbraBridge (IPC)
  Profiles/            # Mappings (Models), ClipResolver (weighted roll), ProfileStore
  Windows/             # MainWindow split into partial classes, one file per tab
                       #   (.Clips / .Mappings / .People / .Settings / .Status / .Events),
                       #   plus shared drawing (.SoundPack, .MappingSets)
docs/PLAN.md           # Verified technical plan — the source of truth
```

Dependency direction: `Detection` → `Gating`/`Profiles` → `Audio` → `Native`. `Windows` reads everything but owns nothing.

### Manifest rule (critical)

The csproj **is** the plugin manifest (Name, Punchline, Description, Tags…). **NEVER add a `Warcry.json` beside the csproj** — DalamudPackager would silently ignore every manifest field. (`bin/Debug/Warcry.json` is generated output; that one is fine.) See `docs/PLAN.md` 3.6.

## Coding Standards

- **File-scoped namespaces** — always. Root namespace `Warcry`, folder = namespace segment.
- **Column alignment in `Plugin.cs` is deliberate** — the `[PluginService]` block and the UiBuilder `+=`/`-=` wiring are hand-aligned. It is the only thing `dotnet format` disputes in the whole solution; do not let a blanket format pass collapse it.
- **Explicit usings**, ordered System → Dalamud/third-party → `Warcry.*`. No global usings.
- **`sealed`** on concrete classes by default.
- **C# 14 features** — pattern matching, collection expressions, records/readonly structs for value-shaped data (`CastEvent`, `CasterKey`, `VoiceRequest`).
- **Comments state constraints, not narration** — the codebase's comments record *why* and cite `docs/PLAN.md` sections (e.g. "NOTE: [PluginService] is AttributeTargets.Property. A FIELD will not compile."). Match that: write a comment only when the code can't show the constraint.
- **XML `<summary>` docs** on cross-feature contracts (interfaces, structs passed between folders); not required on internals.
- **Dispose discipline** — every hook, IPC subscription, and native handle registered in `Plugin` is torn down in `Dispose`, in reverse order.
- **Thread discipline** — game reads/hooks run on the framework thread; ImGui only in draw; audio encode/IO off-thread. Never block either thread on file or encode work.

## Skills

Load these dotnet-claude-kit skills when relevant:

- `modern-csharp` — C# 14 idioms (baseline for all code)
- `code-review` — MCP-powered review before merging a milestone
- `build-fix` — autonomous loop when a refactor breaks the build
- `workflow-mastery` — verification loops, context discipline
- `instinct-system` — capture corrections and in-game discoveries

Most web-stack skills (ef-core, minimal-api, authentication, caching, messaging, aspire, docker…) do not apply to this project.

## MCP Tools

The `cwm-roslyn-navigator` MCP server is provided by the dotnet-claude-kit plugin itself — no project `.mcp.json` is needed; the tools are already available in-session.

Use it to minimize token consumption:

- **Before modifying a type** — `find_symbol`, then `get_public_api` / `get_symbol_source`
- **Before changing a signature** — `find_references` / `find_callers` (hooks and IPC make grep unreliable)
- **After changes** — `get_diagnostics` instead of a full rebuild when possible
- **Interface work** — `find_implementations` for `IVoiceSink` and friends

## Commands

```bash
# Build (also produces the dev plugin + generated manifest in Warcry/bin/Debug/)
dotnet build

# Release build (DalamudPackager zip for the third-party repo)
dotnet build -c Release

# There is no `dotnet test` — there is no test project.
```

**In-game verification loop:** build → in game, `/xlplugins` dev-reload Warcry → use the plugin's **Sound pack checklist** (Status/SoundPack tabs) for the native-path criteria, and the Events tab for detection timing. Anything touching the native sink, SCD encoding, or hook offsets can only be truly verified in game — say so explicitly rather than claiming verification from a green build.

## Workflow

- **Read `docs/PLAN.md` and the project memory first** — decisions and offsets are already researched and partially in-game-verified; do not re-derive them.
- **Plan first** for any non-trivial task (3+ steps or anything touching the native pipeline).
- **Verify before done** — `dotnet build` clean + `get_diagnostics`; state clearly what still needs an in-game check and how to run it.
- **Stop and re-plan** if the approach fights the invariants above (e.g. a "small" NAudio fallback in native mode) — the invariant wins.
- **Learn from corrections** — capture non-obvious game-client behavior (offsets, resource lifetime, which slider governs which bus) in memory/instincts; it's unGoogleable.

## Anti-patterns

Do NOT generate code that:

- **Hand-authors a `Warcry.json`** beside the csproj — silently kills the manifest
- **Upgrades NAudio to 3.x** or introduces APIs only present there
- **Plays gameplay audio through NAudio while the native sink is selected** — refusals must be counted drops, not fallbacks
- **Sets SDK-provided csproj properties** (TFM, LangVersion, platform, nullable, unsafe) — the Dalamud SDK owns them
- **Hardcodes `LocalPlayer` checks outside `Gating/AudienceFilter`** — remote casters are live; the filter is the only place allowed to answer "should this person play a line"
- **Compiles selection weights or probability into SCD groups** — variety lives in `ClipResolver`
- **Warms clips outside the active job SET's reachable clips** — warmed handles are unevictable; widening the set to "all jobs" because remote players exist is exactly the mistake `CrowdWatch` exists to avoid
- **Allocates in the ActionEffect detour for audience work** — no `NameString`, no object-table lookup; read into `CasterFacts` and compare hashes
- **Blocks the framework or draw thread** with file I/O, encoding, or IPC waits
- **Uses `async void`**, `.Result`/`.Wait()`, or static mutable state outside `Plugin`'s service properties
- **Swallows a native-path failure silently** — every refusal carries a visible reason
- **Assumes official-repo constraints** (no native deps, feedback button, D17 review) — this ships via a third-party repo
