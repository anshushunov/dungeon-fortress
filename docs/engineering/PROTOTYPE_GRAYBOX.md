# Godot graybox — Prototype 1

Status: active
Source: Issues #10–#12, #24

The graybox is the visual, top-down projection of the headless Prototype 1
economy and raid. It starts with the `baseline` gameplay-v2 fixture.

## Run

Godot 4.7.1 .NET is required as described in
[`ENVIRONMENT_SETUP.md`](ENVIRONMENT_SETUP.md). From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1
```

The launcher first runs Godot's incremental headless asset-import pass. This is
automatic and creates the worktree-local `.godot/imported` cache required for
generated goblin sprites in a clean checkout; it is a no-op when that cache is
current.

The default is `baseline`. To contrast the starvation-prone setup:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 -Fixture neglected
```

The viewport starts paused at tick 1, which makes the first job assignment
visible and keeps initial inspection repeatable.

## Controls

| Action | Mouse | Keyboard |
|---|---|---|
| Pause / run | `RUN` / `PAUSE` | `P` or `Space` |
| Advance exactly one simulation tick | `STEP` | `S` |
| Select time speed | `0.5x`, `1x`, `4x`, `16x` | `1`, `2`, `3`, `4` |
| Reset fixture | `BASE`, `NEGLECT` | `R`, `N` |
| Inspect | click creature or cell | `I` |
| Designate rock for digging | `DIG` | `D` |
| Withdraw a dig designation | `CANCEL DIG` | `X` |

## Indirect controls (Phase B)

The second control strip is a row of buttons: select `INSPECT`, `PAINT`, `ERASE`,
`DIG` or `CANCEL DIG` with the mouse (or `I`, `B`, `E`, `D`, `X`), choose the
active zone with `Z`, and click a map cell. This produces a `zone_paint`,
`zone_erase`, `dig_designate` or `dig_cancel` v2 command; none of them addresses
a creature. `J` selects a global job priority — including `Dig` — and `K` selects
one of `ration_reserve`, `drill_min_satiety` or `muster_lead_ticks`; `+` / `-`
changes the selected bounded value. `Y` rebuilds and replays the current log.

Every accepted edit is appended to the visible in-memory log, fully validated,
then replayed from the fixture to the current tick before replacing the Godot
projection. Invalid edits leave both world and log unchanged and appear in the
feedback/diagnostic buffer. A command applied at the current tick becomes active
on the next simulation tick.

Speed, pause and stepping are presentation controls only. They only choose how
often the adapter calls `PrototypeWorld.RunTicks`; they are not gameplay
commands and never enter canonical state or a command log.

The inspector exposes the selected creature's needs, martial form, mode,
current job, carried item, last reason and its structured numeric details. Cell
inspection shows its zones and relevant jobs. Colored lines/dots are jobs;
colored circle/square pairs and name labels distinguish all nine creatures.

## Excavation (Issue #24)

`DIG [D]` marks internal rock for excavation; `CANCEL DIG [X]` withdraws a mark.
Both brushes support click and drag, and `Esc` or right-click returns to Inspect.
Neither brush chooses a worker: the player states intent, and a free creature
picks the `Dig` job through the normal autonomous scoring.

A stroke only emits a command for a tile the simulation would accept. Dragging
across floor, the gate, the map boundary or an existing mark changes nothing and
explains itself in the feedback line, so a drag never produces a rejected
command.

Reading the map without the log:

| Reading | What it means |
|---|---|
| dark tile, no marks | plain rock |
| amber tile with an X | designated and reachable, or reserved by a worker |
| amber fill rising from the bottom plus a yellow bar | excavation in progress |
| red tile with an X | designated but no free neighbouring floor to work from |
| gray tile with an X | designated while the `Dig` priority is 0 |
| pale blue tile | floor created by excavation |
| gray dot with a dark rim | loose stone left by a finished dig |

The cell inspector states whether the tile is diggable, why it is not, who chose
the job, which neighbouring tile they work from, the tick progress, and the
result the player will get. The top line adds `stone`, `dug` and `marks`
counters.

The excavation pocket is `(25..26, 1..3)`. Its right column touches the map
boundary, so `(26,2)` is walled in until one of its neighbours is dug — an
intentional, self-explaining `dig_unreachable` case rather than a defect.

Loose stone stays where the rock was. There is no stone hauling, storage or
consumption yet; that is the next step of Issue #23.

### Reproducible excavation frames

`--demo-dig` replays a fixed brush session through the same code path a human
uses: `DIG` marks `(25,1) (25,2) (25,3) (26,1)`, then `CANCEL DIG` withdraws
`(26,3)`. Combined with `--screenshot-ticks` it captures the before, during and
after frames:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoDig -ScreenshotTicks 3 `
  -ScreenshotPath issue24\dig-before.png
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoDig -ScreenshotTicks 30 `
  -ScreenshotPath issue24\dig-during.png
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoDig -ScreenshotTicks 120 `
  -ScreenshotPath issue24\dig-after.png
```

## Raid checkpoint

At tick 300 the HUD announces the fixed raid countdown. From tick 1500 it shows
the live raid outcome and draws red `R0`–`R3` raiders with health bars. The
projection reads `raiders` and `sessionResult` from the same `PrototypeWorld`
snapshot as the economy; it never owns combat state. Capture a deterministic
raid frame with `-ScreenshotTicks 1540` (or a later resolved outcome).

## Deterministic visual evidence

This writes an ignored PNG, advances the same fixture a fixed number of ticks,
then exits after emitting a structured result line:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline `
  -ScreenshotTicks 180 `
  -SelectCreature 3 `
  -DemoControls `
  -ScreenshotPath visual\graybox-baseline-t180.png
```

The result includes fixture, seed, tick, canonical checksum, PNG path,
`loadedSpriteStates` and `fallbackSpriteDraws`. Visual smoke requires all four
goblin states and zero fallback draws. The path must be relative and is always
resolved below `.artifacts/`; rooted and traversal paths are rejected. Do not
commit the image. `--smoke` and
`--visible-smoke` continue to report structured runtime diagnostics for
automation.

## Readability pass

`B` starts painting and `E` starts erasing the selected zone. A bright preview
follows the cursor; click or drag to edit cells. `Esc`, `I`, or right-click
returns to Inspect immediately, so painting never captures the cursor after the
button is released. The current mode and its cancel key are shown in the second
control strip.

The purple room is `Quarters`: it contains bunks and is visited only when a
creature has fatigue at least 50 and a bunk is free. Its empty early-economy
state is expected by the Prototype 1 contract, not a routing failure. The map
labels it as `QUARTERS • REST`, and selecting it repeats this condition.

During a raid, teal circles are crew and red-ring goblins are raiders. HP bars
appear under both. Crew dots show working (green), fighting (amber), fled
(pink), or downed (gray); a white X is a downed body. The battle legend is in
the side panel and selected-creature inspection states `ALIVE`, `DOWNED`, or
`FLED` with HP. Crew and raider sprites are exploratory generated art with provenance in
[`goblin-v1-provenance.md`](../art/goblin-v1-provenance.md); they are not a
commitment to production art direction.

## Boundary

`DungeonFortress.Simulation.PrototypeWorld` owns the fixture, commands, the
mutable map, dig designations, jobs, creatures, economy, event log and canonical
checksum. Which tiles are rock and which of them may be designated both come from
the snapshot, so the adapter holds no copy of the map rules. Godot reads
only `GetSnapshot()` and owns only rendering, hit-testing selection and the
non-canonical time controls. No Node stores an alternative job, creature or
economy state, and no input sends a direct creature command. It remains a
graybox: art assets, animation, production onboarding and Ivan runtime
integration are outside Prototype 1.
