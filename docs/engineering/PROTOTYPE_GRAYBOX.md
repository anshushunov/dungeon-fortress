# Godot graybox — Prototype 1

Status: active
Source: Issues #10–#12, #24, #26, #28, #36

The graybox is the visual, top-down projection of the headless Prototype 1
economy and raid. It starts with the `baseline` gameplay-v2 fixture.

The frame is 1280x720. It used to be 960x540, and that frame could not hold the
HUD text: at its worst moment the side column needs about 33 lines of
explanation and 540 px offers about 29, which is the deficit Issue #28 measured
and Issue #36 cleared. The map is still drawn at a 22 px tile pinned to a fixed
origin, so the larger frame leaves empty space around it; growing the tile to
32–48 px and adding a camera is
[ADR 0008](../decisions/0008-three-quarter-projection.md), not this.

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
| Withdraw a dig designation | `CANCEL` | `X` |
| Paint a material stockpile | `STOCK` | `M` |

## Indirect controls (Phase B)

The second control strip is a row of buttons: select `INSPECT`, `PAINT`, `ERASE`,
`DIG`, `CANCEL` or `STOCK` with the mouse (or `I`, `B`, `E`, `D`, `X`, `M`),
choose the active zone with `Z`, and click a map cell. This produces a
`zone_paint`, `zone_erase`, `dig_designate` or `dig_cancel` v2 command; none of
them addresses a creature. `J` selects a global job priority — including `Dig` —
and `K` selects one of `ration_reserve`, `drill_min_satiety` or
`muster_lead_ticks`; `+` / `-` changes the selected bounded value. `Y` rebuilds
and replays the current log.

`STOCK [M]` is a shortcut, not a new mechanism: it selects the zone
`MaterialStockpile` and the `PAINT` mode in one key, because hunting for that
zone with `Z` is where the intent gets lost. The command it emits is an ordinary
`zone_paint`.

Every accepted edit is appended to the visible in-memory log, fully validated,
then replayed from the fixture to the current tick before replacing the Godot
projection. Invalid edits leave both world and log unchanged and appear in the
feedback/diagnostic buffer. A command applied at the current tick becomes active
on the next simulation tick.

Speed, pause and stepping are presentation controls only. They only choose how
often the adapter calls `PrototypeWorld.RunTicks`; they are not gameplay
commands and never enter canonical state or a command log.

## Movement between ticks

The simulation still advances in whole ticks at `TicksPerSecond = 6.0`. Drawing
no longer does: a frame lerps every creature and raider — and with them whatever
they are carrying — between the tile they stood on when the current tick started
and the tile the snapshot puts them on now.

The lerp deliberately runs **one tick behind** canonical state. Alpha 0 draws the
tile a body came from and alpha 1 the tile it is already on, so the picture can
never show a creature in a tile the simulation has not moved it to. Anything that
is not free-running time — pause, `STEP`, loading a fixture, an accepted command,
a replay — is drawn at alpha 1, which is exactly the canonical position; `STEP`
has to show the result of the step it just ran.

None of this is state. The interpolation buffer is written from snapshots and is
never read by `PrototypeWorld`, and `--frame-pacing` below is the check that says
so out loud.

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
| light warm block filling the whole cell, no grid gap | diggable internal rock |
| dark warm block | the map boundary; it is never diggable |
| amber outline on every rock cell | shown only while the `DIG` brush is active: these are the legal targets of a stroke |
| amber tile with an X | designated and reachable, or reserved by a worker |
| amber fill rising from the bottom plus a yellow bar | excavation in progress |
| red tile with an X | designated but no free neighbouring floor to work from |
| gray tile with an X | designated while the `Dig` priority is 0 |
| pale blue tile | floor created by excavation |
| gray dot with a dark rim | loose stone left by a finished dig |

Rock is drawn as a gapless warm block, well above the cool blue floor in both
hue and brightness, and it fills the 1px grid gap so a wall reads as one solid
mass. The first attempt used a near-black rock that owner playtest reported as
indistinguishable from floor.

The cell inspector states whether the tile is diggable, why it is not, who chose
the job, which neighbouring tile they work from, the tick progress, and the
result the player will get. The top line adds `stone`, `dug` and `marks`
counters.

The excavation pocket is `(25..26, 1..3)`. Its right column touches the map
boundary, so `(26,2)` is walled in until one of its neighbours is dug — an
intentional, self-explaining `dig_unreachable` case rather than a defect.

## Material stockpile and stone hauling (Issue #26)

`STOCK [M]` selects `MaterialStockpile` and the paint brush together. Click or
drag **plain floor that was already floor at tick 0**; each cell holds
`T.stockpile_cell_capacity` = 2 stone. `ERASE [E]` removes a cell and drops
whatever it stored back onto the same tile as a loose pile — the stone is never
destroyed.

The brush is filtered the same way the dig brush is: dragging across rock, a
mushroom bed, a station, the larder, a bunk, a post, the gate or excavated ground
changes nothing and explains itself in the feedback line. The legal targets come
from `map.stockpileFloorTiles` in the snapshot, so the adapter holds no copy of
the rule, and while the brush is active every legal cell is outlined.

Nobody is ordered to carry anything. A free creature picks the `Haul` job through
the same autonomous scoring the food chain uses, and the same global `Haul`
priority governs both. Setting `Haul` to 0 stops stone and food alike; restoring
it resumes both.

Reading stone without the log:

| Reading | What it means |
|---|---|
| grey dot with a dark rim on a tile | loose stone: dug, not yet carried |
| grey box on a creature | that creature is carrying stone right now |
| dark cell with pale corner ticks | a `MaterialStockpile` cell |
| filled pale pip inside a cell | one stone stored there |
| hollow blue pip inside a cell | that slot is booked by a carrier on the way |
| pale-blue cell outline | every remaining slot is booked (`stockpile_incoming`) |
| white cell outline | the cell is full (`stockpile_full`) |
| red cell outline | the cell is inside `Forbidden` and cannot be served |
| grey route line | a stone haul: pile → destination cell |

The HUD reports the three states separately as `stone {loose}L {carried}C
{stored}/{capacity}S`, because one combined number would hide exactly the part of
the chain this step adds.

Clicking a tile with loose stone states why it is not moving: no stockpile, `Haul`
priority 0, no free capacity, no reachable cell, or simply nobody free yet.
Clicking a stockpile cell states how full it is, how much is already booked by a
carrier in transit, and that erasing it drops the stone back rather than deleting
it. Clicking the carrier states what it holds and which cell it booked.

**Known limitation.** Pathfinding routes around rock and `Forbidden`, not around
creatures. A carrier whose shortest route crosses a larder tile occupied by an
eating creature can stand still for a long stretch and repeat
`waiting_blocked_by_other`. It resolves on its own and no stone is lost, but the
carrier looks frozen while it lasts. Making movement avoid occupied tiles would
change every existing scenario and is deliberately out of this step.

### Reproducible stone frames

`--demo-stone` replays a fixed brush session through the same code path a human
uses: `DIG` marks `(25,1) (25,2) (25,3) (26,1)`, then `[M]` paints the material
stockpile `(22,1) (23,1)` at tick 200 — after the pocket is dug, so the earlier
frames legitimately show stone with nowhere to go. `--select-cell X,Y` points the
inspector at the tile each frame is about.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoStone -ScreenshotTicks 190 -SelectCell 25,3 `
  -ScreenshotPath issue26\stone-1-loose-no-stockpile.png
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoStone -ScreenshotTicks 336 -SelectCell 25,1 `
  -ScreenshotPath issue26\stone-2-in-transit.png
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoStone -ScreenshotTicks 950 -SelectCell 23,1 `
  -ScreenshotPath issue26\stone-3-stockpile-full.png
```

Each capture prints `stoneProduced`, `looseStone`, `carriedStone`, `storedStone`
and `stockpileCapacity` next to its checksum, so a frame carries its own
conservation evidence instead of being trusted as a picture.

Stone is still never consumed. Spending it on a functional object is the next
step of Issue #23.

### Reproducible excavation frames

`--demo-dig` replays a fixed brush session through the same code path a human
uses: `DIG` marks `(25,1) (25,2) (25,3) (26,1)`, then `CANCEL DIG` withdraws
`(26,3)`. It deliberately ends holding the `DIG` brush, so the capture also shows
the outline every still-diggable tile gets. Combined with `--screenshot-ticks` it
captures the before, during and after frames:

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

## Checking the HUD without reading pixels

The picture is evidence for a human. Everything an automated check needs is text,
and text is reported structurally. This section is the tooling from Issue #28; it
changes no gameplay, no contract and no tuning.

### `ui`: the HUD as text

Both structured outputs — `godot_headless_smoke` from `--smoke` and
`godot_graybox_screenshot` from a capture — carry a `ui` object:

| Field | What it is |
|---|---|
| `summary` | the two-line top bar, including the stone counters |
| `inspector` | the whole side-panel explanation for the current selection |
| `feedback` | the event feedback buffer |
| `roster` | crew line, control feedback line and the command log tail |
| `controlFeedback` | the raw control feedback string |
| `editMode`, `brushZone` | which brush is held |
| `selectedCell`, `selectedCreatureId` | what the inspector is pointed at |

This turns every inspector branch into an ordinary testable artifact: choose the
moment with `--screenshot-ticks`, point at a tile with `--select-cell`, and assert
a substring of `ui.inspector`. Before this, only the one branch that happened to
land in a captured frame was ever checked.

Nothing in `ui` depends on the camera or on the frame. Pixel positions, the visible
tile range and the viewport size are deliberately absent, because
[ADR 0008](../decisions/0008-three-quarter-projection.md) drops the fixed frame and
those values stop being stable. That is what let the Issue #36 reflow — and the
move from 960x540 to 1280x720 — leave `tests/golden/ui/*.json` untouched.

A headless run now also reports `stoneProduced`, `looseStone`, `carriedStone`,
`storedStone` and `stockpileCapacity`, the same conservation evidence a capture
carries. A frame can therefore be recorded without producing a picture at all.

### Golden UI state

`tests/golden/ui/*.json` holds the reference HUD state for the three reproducible
`--demo-stone` moments — stone with nowhere to go (t190), stone in transit (t336)
and a full stockpile (t950). `scripts/verify.ps1` captures each frame headless and
compares it field by field.

```powershell
# regenerate after an intended change, then review the diff
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-golden-ui.ps1
```

Golden **screenshots** were considered and rejected. The three Issue #26 frames did
reproduce byte-for-byte, but on one machine and one driver; elsewhere the pixels
move and the test becomes a source of false failures. `.artifacts/` is ignored, so
the references would also have to be committed as binary blobs that cannot be read
in a review. A perceptual hash fixes the first problem at the price of a threshold
nobody can justify. The committed JSON is deterministic, cross-platform and
readable in a diff, and it catches the same two failures: "the HUD stopped showing
what it should" and "an explanation changed silently".

### HUD overflow guard

A `Label` that does not fit its rectangle loses text silently: clipped it drops
the overflowing lines, unclipped it draws over the panel below it. Both happened
in Issue #26 and both were caught by eye on a PNG.

`AssertLabelsFit()` compares `GetLineCount()` with `GetVisibleLineCount()` for
every piece of HUD text — the four panels the golden state records, the header,
and each legend row. It is measured against the rectangle the layout produced,
never against a window constant, because ADR 0008 removes the fixed frame.

**It runs in `_Ready`, on every entry point, at five frame sizes.** Since the HUD
became a Control tree the measurement is only meaningful *after* a layout pass,
which is the opposite of what the old absolute layout needed: a container hands a
label its size, so that size is the designed one and an unclipped label can no
longer re-expand to its own content. Godot sorts containers on a deferred pass, so
`LayoutHud()` notifies the subtree and gets the same placement a frame would
produce, synchronously. It then repeats the whole check at 1280x720, 1366x768,
1600x900 and 1024x768, so "the layout follows the viewport" is a checked claim
rather than an intention — a guard that only ever saw one size cannot tell a
responsive layout from a lucky one.

A size that is absent from that list is not unsupported, it is unmeasured. The old
960x540 frame is absent because the current text does not fit it: the side column
needs about 33 lines and that frame offers about 29.

Every structured output carries `labelFit`, now shaped as the live `viewport`, the
`checkedViewports`, and a `labels` array with `neededLines`, `visibleLines`,
`hardLines`, `width` and `height` per label. A run therefore states what the guard
had to work with instead of the guard being trusted.

Godot 4.7.1 `--headless` was suspected of degrading font metrics, which would make
the guard vacuous. It does not: shaping, wrapping and font metrics are identical to
a windowed run — the same wrapped line counts and the same font height. The
suspicion was backwards. Headless is where the guard is meaningful, and the
windowed capture path is where it is not.

### Frame pacing: the simulation must not notice the renderer

`--frame-pacing <tick>` unpauses the world, drives the real `_Process` loop until
the simulation reaches that tick, prints `godot_frame_pacing` and quits. Combined
with Godot's `--fixed-fps` it turns "does the frame rate reach the simulation?"
into an ordinary headless comparison:

```powershell
& $godot --headless --fixed-fps 20 --path .\src\DungeonFortress.Game `
  -- --fixture baseline --frame-pacing 200
& $godot --headless --fixed-fps 60 --path .\src\DungeonFortress.Game `
  -- --fixture baseline --frame-pacing 200
```

The result carries `checksum` next to `replayChecksum` — the same command log
replayed in one shot with no frames at all — plus `frames`, `interpolatedFrames`,
`interpolationLeadViolations` and `maxRenderStepPixels`.

`scripts/verify.ps1` runs both frame rates and requires all of:

| Claim | How it is read |
|---|---|
| the frame rate does not reach canonical state | both runs end on the same tick with the same `checksum` |
| interpolation is not state | `checksum` equals `replayChecksum` in each run |
| the runs really differed | `frames` differs between them |
| interpolation never leads the simulation | `interpolationLeadViolations` is 0 |
| interpolation actually engaged | `interpolatedFrames` is above 0 |
| movement no longer teleports | `maxRenderStepPixels` is below `tileSize` |

Measured on the `baseline` fixture at tick 200: 665 frames at 20 fps and 1992 at
60 fps, both landing on the same checksum, `maxRenderStepPixels` 6.6 and 2.2
against a 22 px tile. Before interpolation that number was the tile size itself.

The last row only means something while a frame is shorter than a tick. At a frame
rate low enough to cover several ticks the picture legitimately moves more than a
tile, which is why the check pins the frame rate instead of sampling whatever the
machine produces.

### What ADR 0008 will change here

[ADR 0008](../decisions/0008-three-quarter-projection.md) makes the camera part of
the capture inputs: the same tick at a different camera position or zoom produces a
different picture. When the camera lands, its position and zoom must be recorded
next to the seed and the tick for every reproducible frame in this document. The
camera is not implemented yet and is out of Issue #28 and Issue #36.

The golden UI state is unaffected on purpose — it holds no camera-dependent value.
It is what proved the Issue #36 reflow changed where the HUD text sits and not
what it says: all three frames passed without regeneration.

The stretch aspect stays `keep`, so the viewport is a stable 1280x720 whatever the
window does. `expand` would let the viewport grow with the window, but then a
capture stops having one frame size, which is the same ambiguity ADR 0008 warns
about for the camera. The HUD does not depend on that choice: it is laid out from
`GetViewportRect()` and relaid out whenever the viewport changes.

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
mutable map, dig designations, jobs, creatures, economy, stored stone, stockpile
capacity and reservations, the event log and the canonical checksum. Which tiles
are rock, which of them may be designated and which floor may hold material all
come from the snapshot, so the adapter holds no copy of the map rules. Godot reads
only `GetSnapshot()` and owns only rendering, hit-testing selection and the
non-canonical time controls. No Node stores an alternative job, creature or
economy state, and no input sends a direct creature command. The motion
interpolation buffer is the one piece of per-frame state the adapter keeps: it
holds the tile each body came from, it is written from snapshots only, and
`--frame-pacing` is the check that it never travels the other way. It remains a
graybox: art assets, animation, production onboarding and Ivan runtime
integration are outside Prototype 1.
