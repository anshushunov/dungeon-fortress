# Godot graybox — Phase A

Status: active
Source: Issue #10, Phase A

The Phase A graybox is the visual, top-down projection of the headless
Prototype 1 economy. It starts with the `baseline` gameplay-v2 fixture and is
intentionally a small visual checkpoint before zone painting and command-log UI.

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

Speed, pause and stepping are presentation controls only. They only choose how
often the adapter calls `PrototypeWorld.RunTicks`; they are not gameplay
commands and never enter canonical state or a command log.

The inspector exposes the selected creature's needs, martial form, mode,
current job, carried item, last reason and its structured numeric details. Cell
inspection shows its zones and relevant jobs. Colored lines/dots are jobs;
colored circle/square pairs and name labels distinguish all nine creatures.

## Deterministic visual evidence

This writes an ignored PNG, advances the same fixture a fixed number of ticks,
then exits after emitting a structured result line:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline `
  -ScreenshotTicks 180 `
  -SelectCreature 3 `
  -ScreenshotPath .\.artifacts\graybox-baseline-t180.png
```

The result includes fixture, seed, tick, canonical checksum and PNG path. Do
not commit the image; `.artifacts/` is ignored. `--smoke` and `--visible-smoke`
continue to report structured runtime diagnostics for automation.

## Boundary

`DungeonFortress.Simulation.PrototypeWorld` owns the fixture, commands, map
rules, jobs, creatures, economy, event log and canonical checksum. Godot reads
only `GetSnapshot()` and owns only rendering, hit-testing selection and the
non-canonical time controls. No Node stores an alternative job, creature or
economy state, and no Phase A input sends a direct creature command.

Phase A deliberately does not include zone painting, priority/rule editing,
replay UI, combat, art assets, animation or Ivan runtime integration.
