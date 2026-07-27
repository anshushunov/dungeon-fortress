# Godot graybox — Prototype 1

Status: active
Source: Issues #10–#12

The graybox is the visual, top-down projection of the headless Prototype 1
economy and raid. It starts with the `baseline` gameplay-v2 fixture.

## Run

Godot 4.7.1 .NET is required as described in
[`ENVIRONMENT_SETUP.md`](ENVIRONMENT_SETUP.md). From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1
```

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
| Inspect | click creature or cell | — |

## Indirect controls (Phase B)

The second control strip is deliberately compact: select `INSPECT`, `PAINT` or
`ERASE` with the mouse (or `I`, `B`, `E`), choose the active zone with `Z`, and
click a map cell. This produces a `zone_paint` or `zone_erase` v2 command; it
does not address a creature. `J` selects a global job priority and `K` selects
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

The result includes fixture, seed, tick, canonical checksum and PNG path. The
path must be relative and is always resolved below `.artifacts/`; rooted and
traversal paths are rejected. Do not commit the image. `--smoke` and
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
`FLED` with HP. Raider sprites are exploratory generated art with provenance in
[`goblin-v1-provenance.md`](../art/goblin-v1-provenance.md); they are not a
commitment to production art direction.

## Boundary

`DungeonFortress.Simulation.PrototypeWorld` owns the fixture, commands, map
rules, jobs, creatures, economy, event log and canonical checksum. Godot reads
only `GetSnapshot()` and owns only rendering, hit-testing selection and the
non-canonical time controls. No Node stores an alternative job, creature or
economy state, and no input sends a direct creature command. It remains a
graybox: art assets, animation, production onboarding and Ivan runtime
integration are outside Prototype 1.
