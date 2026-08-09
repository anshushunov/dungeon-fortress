# Godot graybox — Prototype 1

Status: active
Source: Issues #10–#12, #24, #26, #28, #36, #48, #49, #55, #58, #79, #83, #86

The graybox is the visual, three-quarter projection of the headless Prototype 1
economy and raid on its unchanged orthogonal grid. It starts with the `baseline`
gameplay-v2 fixture.

1280x720 is the rectangle the HUD is authored against. It is not a launch default
and not a description of anybody's monitor, and conflating those two meanings is
the whole of Issues #86 and #100: the launcher used to open that rectangle on
every screen, which on a large one meant a small window with text 8–15 physical
pixels tall. The rectangle itself used to be 960x540, and that frame could not
hold the HUD text at all — at its worst moment the side column needs about 33
lines of explanation and 540 px offers about 29, which is the deficit Issue #28
measured and Issue #36 cleared.

`run-game.ps1` therefore has no `-FrameSize`, `-UiScale` or `-CameraZoom`
default. A launch without them asks the screen and the layout: the window takes
90 % of the usable area of the screen it opens on, the UI scale is the largest of
`1`, `1.25`, `1.5`, `1.75`, `2` at which the authored rectangle still fits that
frame, and the camera starts at the largest declared level at which the whole map
still fits the world viewport. The rule, its
measurements and what happens without a screen are in
[`ENVIRONMENT_SETUP.md`](ENVIRONMENT_SETUP.md#стартовый-кадр-и-масштаб-интерфейса).
A reproducible capture never reaches that rule: it names its exact frame rather
than inheriting the live window or the screen.

The world uses a 40 px tile and a `Camera2D`; the HUD is a separate `CanvasLayer`
and does not move or zoom with the world. The five discrete camera levels are
`0.5`, `0.75`, `1`, `1.5` and `2`. This is the first camera-and-scale slice of
[ADR 0008](../decisions/0008-three-quarter-projection.md). Rock now has a raised
top, an observer-facing facade and depth order with bodies and tall structures;
the grid, camera and input geometry remain orthogonal.

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

That pass also writes a `*.png.import` next to every asset and a `*.uid` next to
every script. None of it is tracked, so a clean checkout plus an import leaves
`git status` empty; the rule and the reason are in
[`ENVIRONMENT_SETUP.md`](ENVIRONMENT_SETUP.md#производные-файлы-godot-и-git).

The default is `baseline`. To contrast the starvation-prone setup:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 -Fixture neglected
```

The viewport starts paused at tick 1, which makes the first job assignment
visible and keeps initial inspection repeatable.

## Controls

Both strips are `Button` nodes with an icon, a hotkey badge in the corner and a
tooltip that names the button and says in one sentence what it does. Nothing is
drawn into the canvas and hit-tested by hand any more; see
[Icon toolbar and rectangle selection](#icon-toolbar-and-rectangle-selection-issue-55).

The top strip is time: run/pause and step as icons, then `0.5x / 1x / 4x / 16x`,
`BASE`, `NEGLECT` and `REPLAY` as text. Speeds stay digits because a digit is
already universal, and the three that rebuild the world from a log are debug
affordances rather than game actions.

| Action | Mouse | Keyboard |
|---|---|---|
| Pause / run | play / pause icon | `P` or `Space` |
| Advance exactly one simulation tick | step icon | `S` |
| Step one twelfth of the blow being drawn, running no tick | — | `F` |
| Select time speed | `0.5x`, `1x`, `4x`, `16x` | `1`, `2`, `3`, `4` |
| Reset fixture | `BASE`, `NEGLECT` | `R`, `N` |
| Rebuild and replay the current log | `REPLAY` | `Y` |
| Inspect | inspect icon | `I` |
| Paint / erase the selected zone | brush / eraser icon | `B`, `E` |
| Designate rock for digging | pick icon | `D` |
| Withdraw a dig designation | crossed pick icon | `X` |
| Paint a material stockpile | stockpile icon | `M` |
| Mark a training-post blueprint | blueprint icon | `C` |
| Withdraw a blueprint | crossed blueprint icon | `V` |
| Cycle zone / job priority / rule | the three selectors | `Z`, `J`, `K` |
| Zoom the world | mouse wheel over the map | — |
| Pan the world | middle-button drag over the map | arrow keys, three tiles per press |

The wheel steps only through the five declared zoom levels. At `0.5` the whole
28×16 ownership grid fits in the default world viewport; `2` is the detail view.

`run-game.ps1` has no `-CameraZoom` default either. A launch without one starts
at the largest declared level at which the whole ownership map still fits the
world viewport the HUD reserved — `0.75` in the authored 1280x720 frame, `1.5` in
the owner's maximized one. The fixed `0.75` it used to pass was chosen for the
small frame, and on a large window it left a 1120×640 map drawn at 1:1 in the
middle of a viewport twice its size: the second half of Issue #86. A resize
re-derives it, **until the player turns the wheel** — from that moment the zoom
is theirs and only the HUD scale keeps following the window. An explicit
`-CameraZoom` is an override the rule never touches, and a capture must name one.

Pan stops when the camera focus reaches the center of an edge tile. This keeps
the focus on the ownership map without cancelling movement at overview zooms
where the whole map fits in the world viewport.

`UiScale` is independent of the native frame, but a declared combination must
leave at least 1024x720 logical pixels after scaling. The authored 1280x720
rectangle therefore supports scale 1 (and smaller), while scale 2 requires at
least a 2048x1440 frame. `run-game.ps1` rejects an impossible **declared**
combination before restore or engine startup, and the game performs the same
validation for direct launches. A frame derived from the screen has nothing left
to reject: the automatic rule picks the largest scale at which the authored
rectangle still fits, so an impossible pair cannot arise by construction.

Camera input is presentation-only. A map click first has to land in the explicit
world viewport, then the live Godot canvas transform is inverted and the
resulting world point is converted to a grid cell. Clicks on the title,
toolbars, roster and side panel never become map input.

## Indirect controls (Phase B)

The second strip is the brushes: eight actions as icons and three selectors.
Pick a brush with the mouse (or `I`, `B`, `E`, `D`, `X`, `M`, `C`, `V`), choose
the active zone with `Z`, and **drag a rectangle** on the map. This produces one
`zone_paint`, `zone_erase`, `dig_designate`, `dig_cancel`, `build_designate` or
`build_cancel` v2 command carrying the whole selection; none of them addresses a
creature. `J` selects a global job priority — including `Dig` and `Build` — and
`K` selects one of `ration_reserve`, `drill_min_satiety` or
`muster_lead_ticks`; `+` / `-` changes the selected bounded value.

The three selectors — `zone <kind> [Z]`, `<job> <priority> [J]`,
`<rule> <value> [K]` — are the only elements of the strip that keep text on
screen, and they keep it deliberately. An icon can say "this is the zone
selector"; it cannot say *which* zone. Their form is an icon plus the current
value, and clicking one cycles it exactly as the key does.

`STOCK [M]` is a shortcut, not a new mechanism: it selects the zone
`MaterialStockpile` and the `PAINT` mode in one key, because hunting for that
zone with `Z` is where the intent gets lost. The command it emits is an ordinary
`zone_paint`.

Every accepted edit is appended to the visible in-memory log, fully validated,
then replayed from the fixture to the current tick before replacing the Godot
projection. Invalid edits leave both world and log unchanged and appear in the
feedback/diagnostic buffer. A command accepted at the current tick becomes
active in canonical state on the next simulation tick, and is drawn on the map
straight away — see
[Marking while time is stopped](#marking-while-time-is-stopped-issue-58).

Speed, pause and stepping are presentation controls only. They only choose how
often the adapter calls `PrototypeWorld.RunTicks`; they are not gameplay
commands and never enter canonical state or a command log.

## Icon toolbar and rectangle selection (Issue #55)

The spec is [`UI_CONTROL_PASS.md`](../design/UI_CONTROL_PASS.md), written from
the owner playtest after Issue #48: "very awkward to dig with buttons". Every
brush worked one cell at a time, so a 4×3 pocket cost twelve clicks. For a game
whose only way of expressing intent is marking space, that was the main source
of friction in the minute-long loop.

Nothing below changes the simulation, the contract, tuning, the canonical
snapshot schema or the command vocabulary. `dig_designate`, `zone_paint` and
`build_designate` have always taken a **list** of tiles; a rectangle collapses
into one of them with a longer list.

### Where a click goes

The map is **not** a `Control`. Its input moved to `_UnhandledInput`, and Godot
offers every event to the Control tree first, so only what nothing consumed
reaches the map.

This replaces a hand-written hit test. The strips used to be `DrawRect` plus
`DrawString` in `_Draw()` next to a parallel table of rectangles that
`TryHandleToolbarClick` compared the pointer against — two descriptions of where
a button is, kept in step by nothing but care. Ownership of a click is now a
property of the node tree, which makes "a click on a button fell through to the
map" inexpressible rather than merely fixed.

### Dragging a rectangle

- pressing the left button starts a selection, dragging updates it;
- the selection is drawn cell by cell: cells the command will carry in the brush
  colour, cells it will skip in red, and the **count of carried cells** above the
  rectangle. The count is the filtered one, so a drag across floor and rock says
  how much of it the brush will actually take;
- releasing applies the brush as **one atomic command** with the whole tile list.
  The world validates every tile before it records the first mark, so a rejected
  rectangle changes nothing and partially applied marking does not exist;
- `Esc` or right-click during a drag cancels it. Nothing is emitted until the
  button comes up, so a cancelled drag leaves no entry in the command log;
- a single click is a 1×1 rectangle and goes through the same path, which is why
  the `--demo-*` sessions and the golden UI frames are unchanged;
- brushes are toggles and stay held. `Esc` during a drag cancels the drag; `Esc`
  with no drag in progress puts the brush away.

One command cannot carry more than `T.maximum_tiles_per_command` = 256 tiles, so
a selection larger than that is refused whole and the feedback line asks for two
strokes. Splitting it into several commands would put back exactly the partially
applied marking the rectangle exists to remove.

### Icons, and what happens before they exist

Icons come from [Issue #54](https://github.com/anshushunov/dungeon-fortress/issues/54),
which runs in parallel. The dependency is broken by a manifest —
`DungeonFortress.Presentation.UiIconManifest` — that names sixteen files in
`src/DungeonFortress.Game/assets/icons/` and the element that draws each one.
The adapter loads by name and draws a placeholder for anything missing, so
dropping the real PNGs in requires **no code change**. Every icon is resampled
to its 24×24 drawn size on load rather than being scaled by the button: at the
manifest's 48×48 that is an exact 2× downscale, and it is the lesson of the
goblin pack, where 96×96 art squeezed into a 20×20 rectangle by the renderer
turned to mush with nothing measuring it.

Fourteen of the sixteen are toolbar buttons. `icon_food` and `icon_stone` belong
to the resource header, which this step deliberately leaves as text: the header
band ends where the time strip begins, and a row of icons does not fit it
without moving the map. They are declared in the manifest with their owner and
named by `UiIconManifestTests` as undrawn, which is the difference between a
deferred decision and a silent one.

### What is checked without reading pixels

Three things, in the spirit of Issue #28: text stays the source of truth for
checks even where it stops being the source of truth for the eye.

**`ui.controls`** joins `ui` in every structured output: one entry per button
with `{id, label, hotkey, tooltip, active, enabled, icon}`. "Which brushes exist,
what do they do and which one is held" is an ordinary unit test in
`DungeonFortress.Presentation.Tests`, so it runs on every pull request in the
"Pure .NET" job. `ui.selection` reports the rectangle in progress — its mode,
carried cells, area and refusal — and is `null` unless a button is actually
down.

**Manifest integrity** is `UiIconManifestTests`. It asserts the bijection in
both directions: every control that draws an icon has a manifest entry, and
every toolbar entry is drawn by exactly one control. Neither failure is visible
in a diff — an unused icon is a file nobody mentions, and a button without one
quietly draws its placeholder forever. Two filesystem checks come with it: a PNG
in the assets folder that no entry names is a failure, and so is a *partial*
pack, because a half-delivered set leaves some buttons on placeholders with
nothing saying which.

**Strip width.** `AssertControlStripsFit()` runs next to `AssertLabelsFit()`, on
every entry point and at the same frame/UI-scale pairs, and requires each strip
to be no wider than the explicit world viewport. The brush strip used to end at
676 px, wider than the map it marks. It is now at most **455 px**, and the time
strip is 306 px; both are published as `controlStrips` in every structured output
next to `labelFit`, so a run states the widths instead of the guard being trusted.

`REPLAY [Y]` moved from the brush strip to the time strip. It rebuilds the world
from the command log, which is what `BASE` and `NEGLECT` do, and it is not a
brush — so the brush strip is exactly the eight actions and three selectors the
width budget is drawn against.

`ui.summary` is untouched and stays a semantic dump, so `tests/golden/ui/*.json`
pass **without regeneration**. A regenerated golden file here would be a defect
report, not a chore.

## Marking while time is stopped (Issue #58)

Pause is the planning mode: the player stops time in order to lay out space
calmly. Until Issue #58 that was the one mode with no feedback at all. A command
carrying tick `T` is applied at the **start** of tick `T`, so a world stopped at
`T` holds the intent in its log and not yet in its designations. Running, that
gap lasts a sixth of a second and nobody sees it; paused it never closes, so the
player marked rock and the rock stayed bare. The same thing made `STEP` — mark,
advance one tick, see what happened — read as a broken control rather than as a
way to learn the mechanic.

Two ways out were possible: **show the intent before it is applied**, or **apply
designation commands immediately**. The second moves the order of operations
inside a tick, which is an invariant under
[ADR 0010](../decisions/0010-contract-invariants-and-tuning.md) and would need an
ADR of its own. The first was taken.

`DungeonFortress.Presentation.MapProjection` is that layer. It is a pure function
of one snapshot: it reads `pendingCommands`, keeps the ones whose tick is the
tick the world is sitting on, and folds them over the canonical designations,
blueprints, stockpile cells and zones. Nothing is written back, no simulation
rule is copied to this side of the seam, and neither the tick order, the command
vocabulary nor the canonical snapshot changes — which is why the checksum, the
event log and a replay are the same whether or not the player was paused.

Four properties follow, and each is a unit test in
`DungeonFortress.Presentation.Tests`:

- a mark, a withdrawal, a blueprint, a stockpile cell and a zone edit are on the
  map the instant the command is accepted, at any speed and on `STEP`;
- **the tick that finally applies the command changes neither which cells are
  drawn nor how they read**, within the boundary stated below;
- the brush reads the same projection, so a cell that already carries a waiting
  mark is not offered again, the count above a drag is what the command will
  really carry, and the legal-target outline matches;
- a command a fixture scheduled for a *later* tick is **not** shown early. It is
  in the log, but it is not an intent waiting for this frame.

The fold follows the world's own tolerance: marking a tile that already carries
the mark is a no-op, and withdrawing from a tile that carries nothing is skipped,
exactly as `ApplyDigDesignate`, `ApplyDigCancel`, `ApplyBuildCancel` and
`zone_erase` do. A command that would change nothing is not reported as waiting.

### How a waiting mark reads, and where that still moves

A colour is not a detail here: "it did not blink" is a claim about the accent,
not only about the set of cells. The accent is therefore chosen in
`DungeonFortress.Presentation.MapAccents` and not in the adapter, because
`Main.cs` is not built by the "Pure .NET" CI job — a reading decided there is
decided where nothing can check it. `MapAccents` states the two halves
separately, and `MapAccentTests` compares them across the very tick that applies
the command, running the real simulation:

| Mark | While it waits | The world's answer |
|---|---|---|
| dig | grey when `Dig` priority is 0, else amber | `dig_blocked_priority`, else `dig_waiting` / `dig_reserved` |
| blueprint | grey when `Build` or `Haul` priority is 0; red on `Forbidden`; amber when no free stone; else teal | `build_blocked_priority` / `build_haul_blocked`, `build_unreachable`, `build_no_stone` / `build_stone_reserved`, `build_waiting_carrier` |
| stockpile cell | red on `Forbidden`, else grey | `stockpile_unreachable`, else `stockpile_empty` |

Every gate above is a published snapshot fact — the priorities, the `Forbidden`
zone, the stock counters and the job list. None of it re-derives map topology.

The gates are read **through the projection, not from the snapshot**, and that
holds for a mark the world already carries as much as for one still waiting.
Switching digging off with `[J]` and then marking rock with `[D]` is one gesture
to the player, and the tick applies both — the world sets the priority first and
then asks about it on the first rung of its ladder. Correcting only the new mark
would put two designations of different colours side by side on the same map,
making opposite claims about the same fact. The same is true of a `Forbidden`
paint over a blueprint or a stockpile cell, so neither reading uses the
`reachable` field of the snapshot: that field was computed under the zones the
world holds, and the zones the player is looking at are the folded ones.

`MapAccents` is the only place that reads a folded value. Everything that
explains what the world is doing *now* — the inspector's "the Dig priority is 0",
the reason a pile is not moving — keeps reading canonical state, because those
sentences explain a status the world produced under the old value.

### Where the line is

The line is a rule, not a list. It was written as a list of exceptions four times
and the list turned out to be incomplete every time.

> **The projection answers what follows from published facts folded through it.
> The world answers what needs a tick to run.**

So instead of a list of exceptions, here is the list of *inputs*. Every fact the
three status ladders in `PrototypeWorld` ask about is below, and each one is
folded, impossible to have waiting, or the world's to answer. The same table
lives on `MapAccents`, next to the code that implements it.

| Fact, and where the world asks it | Verdict |
|---|---|
| `priorities[Dig]`, `[Build]`, `[Haul]` — first rung of the dig ladder, first and next-to-last of the construction one | **Folded.** `set_priority` is folded by `MapProjection` and read through `MapProjection.Priority` |
| `Forbidden` over a construction site or a stockpile cell — `IsBuildSiteWorkable`, `ToStockpileSnapshot` | **Folded.** `zone_paint` / `zone_erase` are folded like any other marking and read through `MapProjection.IsInZone` |
| `Forbidden` over a tile marked for digging | **Impossible while waiting.** A zone on rock is refused before any world exists, by `PrototypeCommandValidator`, and again by `ValidateZoneTiles` on its tick |
| buildable floor under a site, passable ground under a stockpile cell | **Impossible while waiting.** The only map mutations are rock → floor and floor → post, and both need a tick; no command moves them |
| reachability of rock — has the tile any orthogonal neighbour that is passable, not the gate and not `Forbidden` | **The world's.** It is a question about a tile's neighbours, and answering it here would put map topology on both sides of the seam [ADR 0011](../decisions/0011-presentation-layer-without-engine.md) draws |
| who volunteered, whether work started — `reservedBy`, `progressTicks` | **The world's.** Jobs are generated and matched inside the tick |
| material on a site and stone in the world — `delivered`, `incomingReserved`, `looseStone`, `storedStone`, the booked part of `jobs` | **The world's.** No command delivers, picks up or books stone. Two commands move material as a *side effect* — `zone_erase` spills a cell, `build_cancel` spills a site — and the fold covers geometry, not side effects |
| the world's split between `build_no_stone` and `build_stone_reserved` | **Never reaches a reading.** Both are the same accent |
| tuning — `build_stone_cost`, `stockpile_cell_capacity` | **Impossible while waiting.** No command changes tuning |

Two consequences of the third block are worth stating plainly, because they are
the readings that can still move when the tick runs. Painting `Forbidden` on a
floor tile *next to* marked rock changes that mark's reading on the applying
tick — the neighbour question is the world's. And on the shipped `baseline` map
two of the twelve diggable tiles, `(26,1)` and `(26,2)`, are walled in until a
neighbour is dug, so marking the whole pocket while paused shows two amber cells
that turn red one tick later. `dig_in_progress` is the gentle case: a creature
already standing next to the rock starts work, which is the world answering the
mark rather than the mark being redrawn.

`MapAccentTests` pins the table from both ends. It names those two baseline
tiles; it checks an old mark and a new one in the same frame under a waiting
priority and under a waiting `Forbidden`, in both directions; and it sweeps a
whole session comparing the layer's prediction against the world's own
`statusCode` on every tick where nothing is waiting, so a rung that stops
matching fails in CI rather than in a playtest.

### What the fold does not model

The projection folds **geometry**, not the side effects of applying a command.
Erasing a stockpile cell that holds stone removes the square and its pips at
once, but the loose pile the world drops on that tile appears only when the tick
runs; withdrawing a blueprint that already holds delivered stone behaves the same
way. Modelling those would mean predicting where the world puts material, which
is exactly the rule this layer must not own. The geometry is what the player is
marking, and the geometry is what answers immediately.

The inspector states what the picture deliberately does not: a cell whose mark
is still waiting reads `marked as … on this tick; the world applies it when time
advances`. The top line counts such a mark in `marks`, because "the log has it
and the HUD denies it" was the text half of the same defect.

Two consequences are worth naming rather than leaving to a diff. **The brush now
declines a cell that carries a waiting mark** — no legal-target list changed, the
same "already marked" test is simply told the truth while time is stopped, and
without it the player would re-mark what is already marked, which is the
complaint Issue #58 was opened about. **Zone paint and erase go through the same
fold as the rest**, so painting a room while paused shows its outline
immediately; no colour, style or legend row changed, only where the tiles are
read from.

`ui.pending` reports the whole thing structurally — the waiting dig marks and
withdrawals, blueprints, blueprint withdrawals, stockpile cells and priority
changes — and is `null` whenever nothing waits, which is every frame of
free-running time. So
"the mark showed up straight away" is a field in a headless run rather than
something judged from a screenshot. `--smoke-controls` asserts it end to end,
including the no-blink property.

`tests/golden/ui/*.json` is unaffected and passes **without regeneration**: the
three frames it records are `--demo-stone` moments at ticks 190, 336 and 950,
and no command in that session carries any of those ticks.

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

## A blow on screen (Issue #210)

A fight used to be bodies standing next to each other and bars changing. The
picture now says who struck whom and what came of it, and every part of it is
drawn from the canonical journal — no new canonical fact, no new frame of
animation.

**Where the reading comes from.** `DungeonFortress.Presentation.BlowReadout`
turns one snapshot into the blows of the moment being drawn.
`PrototypeWorld` increments `CurrentTick` at the end of a step, so a snapshot
whose `Tick` is `T` is the world after step `T - 1`, and the entries it draws are
the ones stamped `T - 1`:

| Reason code | What the picture makes of it |
|---|---|
| `combat_attack` | crew member → raider, with the damage |
| `combat_raider_downed` | the same blow, now with the outcome `Downed` |
| `combat_downed` | raider → defender, the blow that put the defender down |

A raider's blow that a defender *survives* is recorded nowhere at all, so it is
read off the fall in hit points between the previous tick and this one. Such a
blow names the body that was struck and the damage, and leaves the striker
unnamed: an arrow drawn from a guess is indistinguishable on screen from an arrow
drawn from a fact. The hit points of the previous tick are the second piece of
per-frame state the adapter keeps, written from snapshots and never read back.

**Collapsed repeats do not hide a blow.** `RecordDecision` folds an identical
repeat into the creature's previous entry — over the shipped `prepared` journal,
136 blows in 52 entries, nine folded into one at worst. It costs the picture
nothing, because a fold moves `LastTick` to the tick of the latest repeat and a
frame asks about one tick only.
`BlowJournalSourceTests.Every_blow_of_the_party_is_recoverable_tick_by_tick`
walks a whole party and recovers all 136. Nothing in the simulation changed.

**What is drawn.** Four marks, all of them presentation only:

- the **pose**: the striker draws back (`windup`), the struck body recoils
  (`flinch`). Both poses shipped with the v2 pack and were unreachable until this
  change, because the adapter passed an unconditional `BodyActionPhase.None`.
  Being struck wins over striking when the same body does both on one tick, and
  a body on the ground stays on the ground — `downed` outranks both;
- the **flash**: the body's own pose silhouette, tinted. Warm white for a body
  still standing, red-white for one that has just gone down;
- the **number**: what the body lost, written as a loss (`-5`). White when the
  blow put the body down, amber when a raider lost the hit points, red when a
  crew member did. It carries a dark rim, because a number without one is
  unreadable over a goblin;
- the **streak**: a piece of the line between the two bodies, pointing from the
  striker to the struck. Only a blow the journal names both ends of gets one.

All four last exactly the tick the blow was recorded on, and all four fade
towards a floor rather than to nothing — a paused frame and a captured screenshot
are drawn at alpha 1, and an effect that reached zero there would be invisible in
every piece of evidence.

The flash, the number and the streak are drawn **above the depth pass**, for the
reason the HP bar is (Issue #83): a raised wall top erased a body's readout
completely, and bodies stack besides — three raiders share one larder tile in the
first wave of the shipped journal. They are declared in `WorldDrawOrder` under
`OverlayMark.BlowFeedback` and none of them fills anything.

There is no mark for a **miss**, and that is a fact about the simulation rather
than a gap: an attack in reach always lands and the damage is floored at
`PrototypeTuning.DamageFloor`, so no reason code says "missed". The third reading
next to "hit" and "put down" is the absence of every mark — a fighting body with
no blow on this tick.

**Hit-stop stops the drawing and not the tick.** On a tick a blow landed on, the
picture holds at the position that tick started from for the first 35 % of it and
then catches up; the tick itself is already over by then. It rides entirely on
`MotionAlpha`, which is the one place that decides *which frame of the journey
between two canonical positions* is shown, and the remapping can only ever lower
that alpha — so no body is drawn ahead of the simulation. Measured on the shipped
`prepared` journal to tick 1400 (`evidence/210-determinism.json`): the canonical
checksum at 20 fps, at 60 fps, in a frameless replay and in a run of the build
without any of this is one and the same, `interpolationLeadViolations` stays 0,
and the largest distance a body moves in one frame goes from 12 px to 18.462 px
at 20 fps — the catch-up factor 1 / (1 − 0.35), still well inside the 40 px cell
the check exists to protect.

## A body in motion (Issue #221)

A body used to switch pictures and slide along a straight line between two
cells. It now turns, rides, leans and takes a blow, and none of it is a new
frame of art: the four motions are multipliers on the sprites the v2 pack
already ships, decided in `DungeonFortress.Presentation.BodyMotion` and applied
by the adapter as one canvas transform.

**One frame per body, three drawings in it.** `Main.PushBodyPose` puts the
canvas origin on the body's feet — the line `CameraView.GoblinFootLine` already
stands the sprite on — and hangs the four motions off it. Everything that draws
the body goes inside that frame: the sprite, the eight offset copies of the side
outline (Issue #208), the load it is carrying and the blow flash (Issue #210),
which live in two different passes of `WorldDrawOrder`. A flip applied to one of
them and not the others is a body wearing somebody else's silhouette, so the
frame is opened and closed by every routine that draws a body, and
`BodyMotionAdapterTests` holds that. What stays outside it is the body's
readouts — the HP bar, the state dot, the damage number, the streak — because a
reading that rides up and down with the body is harder to read, not easier.

| Motion | What it is taken from | What decides it |
|---|---|---|
| facing and flip | the sideways part of the step, and the two cells a blow names — for both bodies of it | `BodyMotion.Turn`, `BodyMotion.TurnToExchange`, `BodyMotion.FlipScale` |
| bob | how far the body has walked, in cells | `BodyMotion.PathCells`, `BodyMotion.BobOffsetRef` |
| lean | the sideways part of the step | `BodyMotion.LeanRadians` |
| squash and stretch | the pose the blow reading gives this body | `BodyMotion.BlowHeightScale`, `BodyMotion.BlowWidthScale` |

**A blow turns both of the bodies it names** (Issue #259). A step turns the body
that took it; a blow turns the one that struck *and* the one that was struck,
towards each other, and it wins over the step for both of them. Until the owner's
duel playtest of ADR 0020's probe found it, only the striker was turned: the
struck body kept whatever its own step had left it with, which on the duel scene
is a body standing with its back to the spear. A blow struck **along a column**
has no sideways part at all, so neither facing points at the other body; there the
answer is the pair's rather than each body's memory — both are drawn the way the
pack is authored (`BodyMotion.VerticalExchangeFacing`), because inheriting would
leave one arrangement out of four with the two standing back to back, which is the
picture the Issue exists to remove.

**That answer is written into a memory and lives after the blow.** The facing is
one value per body, and every rule that decides a facing writes it there, so a
body that walked left and then struck — or was struck — straight up or down is
turned to the authored side and stays that way until its next step with a sideways
part; `BodyMotion.Turn` keeps a facing when the step has none. It changes the
**striker** too, which no earlier rule did: before Issue #259 a blow along a
column left both bodies alone. Nothing in the repository can run into it — the
duel scene picks a blow struck sideways on purpose, so no frame here shows a
vertical exchange at all — so it is registered in
[`DEBT_LEDGER.md`](DEBT_LEDGER.md) with the condition that promotes it: the first
mass-combat frame of Issue #260 on which a body after a vertical exchange faces
away from where it was going.

**One facing cannot answer two blows.** A body struck twice in the same tick is
turned by the last of them in journal order. That is a limit of keeping one facing
per body, not an unfinished rule: when both strikers are on the same side every
rule gives the same answer, and when they are on opposite sides every rule leaves
somebody looking at a back.

**The walk phase is the path and not the clock.** A phase taken from elapsed
time keeps running while a body stands still, and a captured frame — always
drawn at alpha 1 with time stopped — would show every body at whatever phase the
clock happened to hold. Here the phase is `X + Y` of the cell the body is on,
interpolated across the step: a body moves one cell per tick and only along an
axis, so **every single step changes that sum by exactly one**, whichever of the
four directions it was. That is the number of steps the body has taken, it is
canonical, it needs no counter of its own, and it is the same for the same tick
of the same fixture however the frame was reached.
`BodyMotionTests.Every_single_step_advances_the_path_by_exactly_one_cell` walks
400 ticks of the shipped party and measures it rather than arguing it from the
movement code — a diagonal step would leave the sum unchanged or move it by two,
and the gait would silently stop being a gait.

The cycle is two cells long. One cell would be worse than short: every body
would be at the same phase whenever it stands on a cell centre, and a cell
centre is exactly where every paused frame and every screenshot draws it. With
two, a body rises over one step and settles over the next, and the difference is
visible in a frame anybody can stop on. A body that is **not** walking gets
exactly zero — not a small amplitude, not a frozen phase, which would leave it
hanging above its own feet — and the curve never goes below zero either, because
the ground a body stands on is not the drawing's to move.

**A blow keeps the body's area.** The wind-up stretches it and the recoil
squashes it, and the width is the height's reciprocal at every phase, so the
body reads as the same body under tension rather than as a bigger one. Both
curves fade towards a floor above zero for the reason every curve in
`BlowEffects` has one: a paused frame is drawn at alpha 1 and an effect that
rested there would be missing from every piece of evidence.

**A captured frame runs its last tick on its own.** A fixture used to be run in
one go, so nothing in a screenshot had a previous cell and every body in it was
standing still by construction. `LoadFixture` now runs everything but the last
tick, remembers where the bodies stand and then runs that one — the same number
of ticks, in the same order, and the canonical checksum of a capture is
unchanged at every tick measured (`evidence/221-invariants.json`, tick 289 also
against a run with no engine at all).

**Nothing here reaches the simulation.** The facing is presentation state of the
same kind as the interpolation buffer: written from snapshots and from the
canonical journal, never read back. The bob moves the drawing and not
`RenderCenter`, which is what the depth pass sorts by and what `--frame-pacing`
converts back into a cell — a vertical offset there would change depth order and
could report a body in a cell the simulation has not reached. Measured on the
shipped `prepared` journal to tick 1400: the canonical checksum at 20 fps, at
60 fps, in a frameless replay and in a run of the build without any of this is
one and the same, `interpolationLeadViolations` stays 0, and the largest
distance a body moves in one frame is unchanged to the third decimal —
18.462 px at 20 fps and 6.154 at 60.

## A body built from parts (Issue #244, ADR 0020)

A body is no longer one flat picture per state. In `idle`, `combat`, `windup`
and `flinch` it is **seven parts hung off their joints** —
`assets/generated/goblins/cutout_v1`, delivered by Issue #243 — and the blow it
is in is played as a chain of poses rather than shown as one of them. `work` and
`downed` keep their flat sprite: [ADR 0020](../decisions/0020-body-animation-cutout-rig.md)'s
probe is about the blow, and converting the rest is explicitly out of this
Issue's scope.

**The rig is read, not retyped.** `BodyRig.Parse` loads
`goblin_cutout_rig_v1.json` at start-up and refuses a rig this runtime cannot
draw: a part the layer order names and the file does not have, a parent that is
not a part, a root that is not the torso, a depth order that is not the file's
own. The pivots are art — measured once, against the pixels the builder cut —
and Issue #243's provenance says in as many words that #244 "must not retype or
replace" them. A second copy in C# would be a second truth nothing compares with
the first, and the failure is silent: a limb turning a few pixels off its own
shoulder.

**The order of the layers is the rig's, and it is named.** Back to front:
`leg_far`, `arm_far`, `torso`, `leg_near`, `head`, `arm_near`, `weapon`. The
figure is drawn in three-quarter view, so the order is the depth of the body
itself — far side behind the trunk, near side in front of it. The head is above
the trunk and below the near arm because the strike arm crosses the chest and
the chin during the wind-up; over the head it would disappear into the face at
exactly the frame the player is meant to read. The weapon is last because the
strike hand carries it. Every one of those is the rig's own `z_index` sequence,
and `BodyRigTests.The_layer_order_is_the_rig_s_own_back_to_front_order` is what
keeps the written list and the cut art the same list.

**The body lands where the flat pack stood.** The rig's `source_body_bbox` maps
onto the 116x168 target box inside the unchanged 272x192 canvas
`CameraView.GoblinDrawRect` already places on the map — top on the first row any
flat state has a pixel in, bottom on the row its feet end on, centred
horizontally. One scale for both axes, not two: the rig's own ratios differ in
the fourth digit because the builder rounded a target size to whole pixels, and
a part turned under a non-uniform scale stops being the shape it was drawn as.

### The chain of a blow

| Share of the tick | Striker | Struck body |
|---|---|---|
| 0 to 0.28 | stance into wind-up, spear raised | stands |
| 0.28 to 0.35 | wind-up into the strike | stands |
| **0.35** | **contact** | |
| 0.35 to 0.52 | strike into follow-through, thrown back | driven away, torso back |
| 0.52 to 0.78 | follow-through | settling |
| 0.78 to 1 | back to the stance | back to the stance |

Contact is `BlowEffects.HitStopShare` and deliberately not a number of its own:
hit-stop and the strike pose are the same event seen from two sides, and two
constants is how they end up a frame apart. The chain is driven by the **raw**
share of the tick, not by `MotionAlpha` — hit-stop maps the whole wind-up onto
zero, so a chain driven by it would stand still through the anticipation and
then jump.

Which chain a body plays comes from `BlowReadout`, i.e. from the canonical
journal's `combat_attack`, `combat_raider_downed` and `combat_downed` entries.
**No phase is a snapshot field**, and a body no blow touches is in
`StrikePhase.Stance` at every alpha, which is what makes the whole of this
invisible on the ticks with no fighting in them.

**Both ends of a blow move.** `StrikeChain.RecoilOffsetRef` answers a signed
distance along the line from the striker to the body it struck: the striker
lunges in before contact and is thrown *back* after it, and the target is only
ever pushed away. The adapter owns the line — `Main.BlowAxis`, target cell minus
striker cell — and subtracting it the other way round turns both signs over at
once, which compiles, animates and looks like a goblin sucked into the spear it
just planted. That is why the direction is held by a structural guard over
`Main.cs` and not by a value comparison inside a test.

**The lean is a rotation of the whole frame**, not an angle on the `torso` part.
An earlier revision of this paragraph gave the reason as "turning the trunk
against the lower body opens the widest gap of any joint in this rig", and that
was wrong twice over. `torso` is the rig's **root** — `parent: null`, every other
part is its descendant — so an angle on it turns the whole figure and cannot turn
the trunk against anything; and by `evidence/244-rig-gaps.json` the worst seam
`torso` reaches is 1465 px², *less* than `arm_near` (2393) and `head` (1702), so
it is not the widest joint either. That second number is inflated anyway:
`slit_pixels` compares a posed body with an **unturned** rest pose, so a figure
that only moved reads as a figure that tore — turn the rest pose the same way and
the same poses measure 0 (Issue #263, `evidence/263-measurement.json`,
`rigidRotation`). The decision stands, for the reason that survives: a lean tips
the body about its feet while the parts keep their places relative to each other,
and the only thing in this rig that does that is the drawing frame.

**One contact effect: a spark.** Eight rays from the point the blow arrives at,
for the window between contact and the follow-through. The choice is
readability at this scale, and the argument is in `BlowEffects`: at a body drawn
61.8 px tall the long ray is 15.5 world px with a 2.9 px stroke, while a
believable splash would be a dozen marks each under the pixel at which a mark
stops being one, and a weapon trail needs a legible path at exactly the moment
hit-stop holds the picture still. The flash now waits for the blow to arrive as
well — it used to burn for the whole tick, which at the duel's zoom lights a
body up before the spear reaches it.

### Choosing the angles against a measurement

[ADR 0020](../decisions/0020-body-animation-cutout-rig.md) names the risk it was
written for: «стыки частей видны, поворот руки вокруг сустава на мелком масштабе
легко читается как поломка». The review of Issue #243 photographed it, and the
review of this Issue's brief found a second one on the far arm. So the shipped
angles are chosen by `evidence/244-measure-rig-gaps.py`, which composites the rig
at a pose and counts **slit pixels**: background visible *through* the body —
the rest pose had material there, this pose does not, and the body still has
material on both sides of it along its own row. Zero on the rest pose by
construction.

```powershell
python evidence/244-measure-rig-gaps.py --sweep --chain --json evidence/244-rig-gaps.json
```

Two things it decided, both recorded in `evidence/244-rig-gaps.json`:

- **the directions are not symmetric.** The near arm opens 21-55 px² of slit
  swinging one way (-30° to -15°) and 639 px² at +5°, 2393 at +30° the other.
  The far arm is the mirror image: 373-769 px² backwards, 0-2 forwards. So the
  wind-up *raises* the spear and the strike brings it down, and the far arm
  counterbalances forwards — not a taste, a property of the cut;
- **a small slide hides most of what is left.** Each keyframe carries an offset
  in source pixels beside its angle, found by search: 758 to 224 px² on the
  wind-up, 1510 to 534 on the strike, 1888 to 782 on the follow-through, 607 to
  405 on the recoil. Nothing is repainted; the offsets are at most 8 source
  pixels, i.e. 1.3 world px at the shipped tile.

Two other numbers are reported and deliberately **not** gated on. Enclosed holes
never saw the photographed defect at all, because that wedge is open to the
outside; and the area a limb vacates is the animation happening rather than a
fault. Keeping both in the report is what makes those two statements checkable.

### The duel scene

ADR 0020 asks the probe for «сцена один на один, крупно». `--demo-duel` runs the
`prepared` raid journal forward to the first tick the canonical journal records
a **side-on** blow on, points the camera at the two bodies that tick names at
the largest declared zoom, and stops there.

```powershell
# watch it: SPACE runs the exchange, F steps one twelfth of a blow, S runs a tick
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-game.ps1 -DemoDuel

# the same scene with the flat pack, for the A/B ADR 0020's revision condition needs
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-game.ps1 -DemoDuel -FlatBody
```

The scene **hides every body but those two**, and that is a measurement rather
than a preference: the shipped journal has no moment to point a camera at. The
blow the scene settles on — tick 2061 — has **five** other standing bodies within
two cells of it, and a run reports that number itself as `duel.crowd` in the view
state, so a scene built only out of the camera would be the stack of bodies the
review of vertical 3 rejected. The hidden bodies go on fighting and reach the
same checksum; only pixels change.

The emptiest blow of the whole session is a different one — tick 1302, with three
other bodies — and the scene does not take it, because `DuelScore` prefers a blow
struck **sideways**: a blow along the camera's own column reads as one body
standing behind another. So the number a reader gets from `duel.crowd` is the
crowd around the blow that was chosen, not the smallest crowd in the session, and
the case for hiding is the stronger of the two. An earlier revision of this
paragraph claimed the run prints three; independent review of PR #256 ran the
command this paragraph itself offers and got five.

`-DuelFrame <0..12>` freezes the blow at one twelfth of the tick, which is what
the evidence frames are captured with and what the `F` key does live. It runs no
tick at all: stepping a paused blow from its first twelfth to its tenth changes
every pose, both bodies' places and the spark, and leaves the checksum identical
(`evidence/244-invariants.json`).

### Nothing here reaches the simulation

Measured on `prepared` to tick 1400, four runs — the rig and the flat body, at
20 and at 60 fps: one canonical checksum
`18fdcdb30fb338ae85ecb6398529c8eb0aebd23e3b9c0819e9f4d423ee503002`, equal to the
checksum of a frameless replay of the same log in every one of them. The two
frame rates really differ (4665 frames against 13992),
`interpolationLeadViolations` stays 0 and the largest distance a body moves in
one frame is 18.462 px at 20 fps against a 40 px tile. The throw of a blow goes
into the drawing and never into `RenderCenter`, for the same reason the bob does
not: `--frame-pacing` turns a render centre back into a cell.

The before/after pictures of the change are
`evidence/244-before-windup.png`, `evidence/244-after-windup.png`,
`evidence/244-before-contact.png` and `evidence/244-after-contact.png` — the same
duel, the same tick, the same camera, differing in the body and in nothing else.

### How much of a blow is visible, measured (Issue #263)

The owner played the duel scene twice. After Issue #244: «на демке плохо видно,
но движения в целом ок». After Issue #259, and sharper: «нет плавности движения,
очень быстрые удары даже на скорости 0.5 (должны быть сильно медленнее), не
хватает кадров как будто, просто две позы». Combat then got a ceiling —
**readable rather than beautiful** — and the visual branch was wound up; that is
an owner decision of 2026-08-06 and it is recorded in the gate log of
[`docs/product/GATE_DECISIONS.md`](../product/GATE_DECISIONS.md), row «Потолок
боя», which is
where it is decided and amended. This section is the measurement it rests on, and
there is no change of behaviour to go with it: the chain described above is the
chain that ships.

`evidence/263-measure-strike-readability.py` composites the rig the way
`evidence/244-measure-rig-gaps.py` does, puts it through the rest of what
`Main.PushBodyPose` does to a body — the lean, the squash and stretch, the throw —
and reduces the result to the size a body has on screen **at the working zoom**.
That last part matters: `--demo-duel` forces the largest declared zoom (2.0) and
does not give it back, so every evidence frame this repository has of a blow is
twice the size the map is played at. At zoom 1.0 a goblin is 54.1 px tall and
1533 px of silhouette.

```powershell
python evidence/263-measure-strike-readability.py --readability --timing `
  --budget --gaps --rigid --sweep-wide --json evidence/263-measurement.json
```

**A blow is one tick, and that is the binding constraint.** At
`TicksPerSecond = 6.0` the whole exchange lasts 167 ms at speed 1 and 333 ms at
speed 0.5 — 58 ms and 117 ms of it before contact, since `ContactShare` is 0.35.
Against the reference a melee swing is usually given (150–250 ms of anticipation,
200–300 ms of recovery, 500–800 ms in all) the exchange is short by **3.0–4.8
times at speed 1 and 1.5–2.4 times at speed 0.5**. Nothing inside `StrikeChain`
can change that: the chain is a function of the share of *its own tick* that has
been drawn.

**«Просто две позы» is arithmetic, not taste.** A 60 Hz display draws that tick in
ten frames at speed 1 and twenty at speed 0.5. At speed 0.5 exactly **one drawn
frame of twenty** changes more than half as much silhouette as the busiest one:
the profile is `153 159 152 139 126 185 404 61 60 65 …`, so the peak of 404 px
stands between neighbours of **185 and 61**. The eye gets the pose before that
frame and the pose after it.

The cause is the interpolation, not the frame budget. The wind-up keyframe sits at
0.28 and contact at 0.35 — seven hundredths of a tick, which is 11.7 ms at speed 1
and **23.3 ms at speed 0.5, i.e. 1.4 drawn frames**, not less than one. What makes
that span carry everything is that the chain is straight lines between keyframes:
speed is constant inside a span and changes in one step at each keyframe, so the
span with the largest change per unit of tick takes the whole movement and the
others get almost none. The largest step of speed between neighbouring frames is
**80–85 % of the peak**.

**And the body's own outline barely moves.** At the busiest frame of the chain
(frame 3, the wind-up) the whole figure changes 1087 px of silhouette, but with
the lean, the throw and the stretch taken out — i.e. counting only what the parts
do — the body without its striking arm changes **79 px**, 6 % of its own 1259 px
standing area. Its widest point anywhere on the chain is 151 px (frame 6), 12 %.
Nearly everything an eye sees is the figure sliding and tilting rigidly. The trunk
is one PNG with no waist joint, so the arched back the flat pack's `windup` was
drawn with is not reachable by posing at all. The A/B is
`evidence/263-frames-working-zoom.png`: the same three moments of the same tick,
drawn by the rig and by the flat pack, reduced to the working zoom. Note what that
sheet does **not** show — `BlowReadout` assigns a `BodyActionPhase` once per tick
and the scrub does not enter the choice of sprite, so the flat striker is the same
`windup` PNG on all three frames and only slides, tilts and squashes. Inside one
tick the rig gives *more* change, not less; what the flat pack has and the rig
cannot reach is the drawn turn of the trunk in that single PNG.

Two things the measurement found that nobody had asked about:

- **The near shoulder still has unused travel.** Issue #244 swept ±30° and
  stopped; past that, `arm_near` rises to −35, −40, −50 and −60° for 25, 22, 36
  and 53 px² of seam, while the shipped chain goes no further than −30°. The far
  arm's `0 px²` at +30…+60° is **not** the same finding and must not be read as
  one: `arm_far` is drawn *under* the trunk (`z_index` 1 against 2), so the free
  angle buys the arm disappearing behind the body rather than a wider silhouette —
  measured, the figure loses 1302 px of outline and gains 90, 143 and 188 at +30,
  +45 and +60. The sweep also runs the body **unarmed** (`GAPS.compose` without
  `show_weapon`), which is right for seams and says nothing about how a wind-up
  reads with a spear in it.
- **The seam measurement cannot tell a rigid turn from a tear.** `torso` is the
  rig's root and every part hangs off it, so an angle on the root turns the whole
  figure and no joint can open — yet `slit_pixels` prices it at 617–1423 px²,
  because it compares against an *unturned* rest pose. Turn the rest pose the same
  way and the same poses measure **0**. That is a caveat on every `torso` row of
  `evidence/244-rig-gaps.json`, and it is why the lean turns the drawing frame
  instead of the part.

The worst seam anywhere on the shipped chain — sampled between the keyframes and
not only at them, which nothing had done before — is **782 px²**, on the striker's
follow-through at alpha 0.52. That is the reference point to compare against if
the angles are ever touched again.

**What "приятно смотреть" runs into.** Both limits are real and they are not
equal. The cutout limit is real but narrow: the trunk cannot bend, so the body's
own outline is stuck near the 79–151 px it moves today, and lifting that needs art
(a waist joint, or a redrawn trunk). The tick limit is the binding one: an
exchange that lives inside one tick has 167 ms at speed 1, three to five times
under the reference, and no amount of posing buys time the tick does not have.
Treating it would mean drawing a tick or two behind the simulation — presentation
lag, which would give the animation a 500–1000 ms window and is presentation-only,
touching neither the snapshot nor the checksum. That is an architecture decision
under ADR 0011; it is **deferred and not refused**, and the decision, its ground
and its candidate row live in the gate log of
[`docs/product/GATE_DECISIONS.md`](../product/GATE_DECISIONS.md), row «Потолок боя» of
2026-08-06, not in this file.

## Wall volume and depth order (Issue #83)

Rock is rendered in immediate mode rather than through `TileMapLayer`. This is
an implementation choice inside ADR 0008, not a new architecture decision:

- the same interpolated body center already used to draw a goblin is also its
  depth anchor;
- neighbour choice and Y-order remain pure .NET functions in
  `DungeonFortress.Presentation`, with no Godot runtime in their tests;
- the existing explicit screenshot path continues to render the exact adapter
  used by play.

Each rock cell selects one of sixteen variants from a four-bit `N/E/S/W`
neighbour mask. Diagonals never join walls. Connected sides have no internal
seam, so a rock mass remains continuous instead of becoming a checkerboard.
Missing sides receive an outer edge; a missing south neighbour also exposes the
dark facade facing the observer. A wall's top rises eight reference pixels into
the cell behind it and the facade overhangs the lower edge of its footprint by
three reference pixels. These two small overlaps make both sides of depth visible
without changing the cell or collision model. No atlas or generated asset is
involved.

The world draws in four passes:

1. floor and base material that belongs below elevated world geometry, including
   blueprint and stockpile silhouettes but not their countable pips, and — last of
   the four, after the things standing on the floor — a room's border;
2. walls, training posts, creatures and raiders in stable back-to-front Y-order;
3. zone outlines, translucent routes and work goals, dig intent, material pips,
   body information, zone labels and the one part of a room's border a wall in
   front would swallow, above world depth;
4. legal-target and selection outlines, followed by the active brush preview,
   above the informational marks.

Walls use the lower edge of their footprint as depth anchor. Training posts use
the cell centre because a creature performing `Drill` legitimately occupies the
same cell and must remain visible over the post. Creatures and raiders use their
current **interpolated** centre. At an exact wall tie, bodies are still behind
the wall; they move in front only after the interpolated anchor crosses it. At
an exact post tie, the post is the background and the body is drawn above it. X
and stable identifiers break otherwise equal ties, so collection order cannot
change a frame.

HP bars, state dots, downed marks and the selected-creature ring are information,
not opaque world material. They use the same interpolated centre as the body but
are drawn after the depth pass, so a wall can hide the lower body without also
hiding its readable state. Haul routes and work goals likewise remain complete
instead of losing their south edge under a wall. A room's border used to be in
that list and no longer is: see
[A room's border is under the body standing on it](#a-rooms-border-is-under-the-body-standing-on-it-issue-156).

**One rule governs every mark in this pass: a mark that can share a cell with a
body must not hide it.** Its fill is translucent, which is what keeps a countable
mark countable. The rule is stated once because it is not a style preference —
three separate marks broke it in turn, each landing opaque on the very creature it
explains. The simulation is what makes this the normal case rather than an edge
one: `Drill` requires the post cell, `Build` requires the site cell for every one
of its ticks, and storing stone requires the stockpile cell. So work-goal dots,
blueprint delivery pips, stockpile occupancy pips and the build progress bar are
all translucent, and a new mark added to this pass inherits the rule instead of
rediscovering it.

This paragraph used to end "an outline may stay opaque", and Issue #156 is the
owner's playtest refuting the reason behind that: **an outline covers what it is
drawn over too**, it just covers less of it. Opacity is what a *fill* is asked
about, and having no fill answers that question and no other. A stroke that can
land on a body still owes an answer to "what is underneath me" — the room border's
is draw order, below. Whether the other marks declared `StrokeOnly` owe the same
answer is a sweep of its own and has its own Issue; nothing here claims it has
been done.

### What now protects that rule (Issue #90)

The rule above was written down after the third review round and broken again in
the fourth. Writing it down is not what stops that happening: the rule lived in
`Main.cs`, and `Main.cs` is not built by the "Pure .NET" CI job, so every
violation of it was found by an eye on a captured frame. A pixel golden is
deliberately not the answer — the reasons are in
[Golden UI state](#golden-ui-state) and they have not changed.

The rule is now **data in `DungeonFortress.Presentation`**, and the adapter reads
it rather than repeating it:

- `WorldDrawOrder` declares every drawing routine of `DrawMap`, the order the
  twelve top-level ones run in, and the pass each of them belongs to;
- `InformationalOverlays` declares, for each mark, what it explains, whether the
  simulation can put a body on that cell, and the answer it gives — drawn as it
  is, translucent fill, strokes only, or skipped while a body stands there. The
  fill alphas live there too, and `Main.MarkFill` / `Main.MarkAccent` are the
  whole of the adapter's part in it;
- `BodyOccupancy` is "which cells hold a body", as a pure function of the
  snapshot.

Two subjects, because the rule reaches them differently. A **cell** mark explains
a tile and is the case the rule is about. A **body** mark is the body's own
readout — HP, state dot, downed cross, selection ring — anchored to the body and
drawn above the depth pass precisely so a raised wall top cannot erase it. The
one cell mark that is not asked to be translucent is the dig mark, and only
because rock is impassable, which is measured rather than assumed.

One mark is opaque over cells that hold bodies **as a stated exception**: the
count above a drag. Its plate lands on the row above the selection, and on a rock
selection in row 0 it is pushed inside the selection itself, so the rule reaches
it. It stays opaque for one reason — a number drawn over a sprite is unreadable,
and translucency would be an appearance change this step forbids. That is
declared as `OpaqueByExemption` with a required reason and reported by the rule
test as an accepted exception, rather than as a third subject claiming the rule
does not apply. The distinction matters: a subject that means "out of scope" is
an escape hatch, and the first version of this manifest had one.

Four checks in `DungeonFortress.Presentation.Tests` hold it up, and each one was
chosen against a mutation that nothing used to catch:

| Mutation | What fails |
|---|---|
| the alpha taken off a mark above the depth pass | `Every_translucent_mark_reads_its_fill_alpha_from_the_policy` |
| a policy relaxed to "draw it as it is" | `A_mark_that_can_share_a_cell_with_a_body_is_never_drawn_as_it_is` |
| `DrawHpBar` called from the depth pass again | `A_routine_only_calls_routines_of_its_own_pass` |
| a mark moved between passes | `DrawMap_runs_the_declared_steps_in_the_declared_order` |
| a new mark added to the pass with no declared policy | `Every_drawing_routine_of_the_adapter_is_declared` |
| an opaque fill written as `this.DrawRect(…)` instead of `DrawRect(…)` | `No_covering_primitive_hides_behind_a_receiver` |
| an opaque mark drawn inline in `DrawMap` itself | `DrawMap_draws_nothing_of_its_own` |

The last four read `src/DungeonFortress.Game/Main.cs` **as text**. That is the
consequence of the root cause Issue #90 names: no test project references
`DungeonFortress.Game`, and none should, because the assembly needs the engine
runtime that ADR 0011 keeps out of the CI job. The reader checks structure only —
which methods exist, which calls each makes, how many arguments a call has — and
`UiIconManifestTests` already reads the adapter's asset folder for the same
reason. A manifest is a contract only while something compares it with the thing
it describes.

Two declarations are checked against the world rather than believed. "No body
ever stands on rock", which is what lets a dig mark stay opaque, is a sweep of a
real 1800-tick session including raid waves. "Bodies really do stand on the cells
the translucent marks explain" is the same sweep from the other side, so the rule
is measured to be the normal case rather than asserted to be.

**Where the checks stop.** They hold code that follows one naming convention: a
drawing method is a method whose name starts with `Draw`. A drawing method called
something else is outside the manifest and outside every check built on it —
that is a property of a convention, not an oversight, and saying so is what makes
"a new mark cannot reach this pass without a policy" a true statement rather than
an overclaim. The two ways out that were *not* conventions are closed instead:
`DrawMap` now draws no primitive of its own, so there is no unnamed body inside
the passes, and a call written on `this` counts as a call, so a receiver cannot
hide a fill from the alpha check.

Rock selection, DIG previews and excavation progress use
the wall's raised top-plus-facade bounds rather than the flat cell footprint.

### The selection frame follows the same shape (Issue #99)

Issue #83 gave rock volume and taught the hover highlight, the selected cell and
the dig marks about it — all of them ask `CellInteractionRect`. The rectangle a
drag stretches did not: it was built in grid coordinates and knew nothing about
volume. The owner reported it from playtest as "hovering rock outlines its whole
shape, clicking it snaps back to a square", which happens exactly at the moment
the player moves from looking to acting.

`DungeonFortress.Presentation.SelectionGeometry` is now the one function both
shapes come from. The frame is the union of the interaction rectangles of the
cells the drag covers, walked column by column, so:

- a drag over rock rises with the wall's raised top and hangs with its facade,
  the same way the hover highlight does;
- a drag over floor is exactly the grid rectangle it always was, to the pixel;
- a **mixed** drag rises only over the columns whose first cell is rock. It is
  therefore neither the flat grid rectangle nor the bounding box of the raised
  ones — the bounding box was tried during Issue #83 and rejected, because it
  lifts the frame over floor columns that were never raised.

Which cells the command carries is untouched: `BrushSelection` still decides
that, the accepted and skipped cells are still tinted one by one, and the count
above the selection is still the accepted count.

`SelectionGeometryTests` pins the shape from both ends, which is what makes
"the two geometries agree" a check rather than a convention. Containment says
every selected cell's rectangle lies inside the frame, so building the frame
from grid coordinates fails; tightness says each column ends exactly where its
own cells do, so a bounding box fails. The caption is placed by
`SelectionGeometry.CaptionBox` and checked to stay inside the map at every tile
size — the top of a rock selection on row 0 is genuinely above the map, so the
clamp stopped being a formality.

Two ignored, reproducible frames show the same internal wall column with a
selected creature on opposite sides:

Both pairs moved with the dungeon of Issue #117 and were re-derived rather than
guessed: the tiles the old commands named — `(9,4)` and `(9,5)` — are floor on
the new map, so the two commands framed empty ground and the check they document
could not be repeated. The positions below were found by walking the shipped
`baseline` journal and asking which creature stands orthogonally next to
masonry.

```powershell
# Creature #2 at (10,4), behind rock at (10,5): the raised wall hides its lower body.
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -ScreenshotTicks 40 -SelectCreature 2 `
  -TileSize 40 -CameraZoom 2 -CameraPosition '420,180' -UiScale 1 -FrameSize 1280x720 `
  -ScreenshotPath issue83\behind-t40.png

# Creature #0 at (13,6), in front of rock at (13,5): the whole body draws over it.
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -ScreenshotTicks 80 -SelectCreature 0 `
  -TileSize 40 -CameraZoom 2 -CameraPosition '540,260' -UiScale 1 -FrameSize 1280x720 `
  -ScreenshotPath issue83\front-t80.png
```

The screenshot events state the selected cells, ticks, checksums and all five
view inputs. The pure tests cover all sixteen neighbour masks and their stable
numeric values, exposed-edge mapping, isolated rock, corners and map edges.
Render-geometry tests cover a body north and south of a wall, a body sharing a
training-post cell, stable cell-ID round trips, exact/stable ties and the order
change on interpolated Y.

All sixteen variants are also checked as **drawn strokes**, not only as masks:
each one states how many segments of each kind it must have and which edge each
one lies on, derived from `ExposedSides` rather than from a list of expected
coordinates. Two variants used to be covered and both had north and west exposed
at the same time, so swapping those two conditions inside the geometry changed
nothing any test could see.

The inspector exposes the selected creature's needs, martial form, mode,
current job, carried item, last reason and its structured numeric details. Cell
inspection shows its zones and relevant jobs. Colored lines/dots are jobs;
colored circle/square pairs and name labels distinguish all nine creatures.

### Where a room's border stands next to a wall (Issues #139 and #147)

A room's outline is drawn inset from its own cells. How far in is not a taste:
rock has volume, and the volume reaches out of the wall's own footprint in three
different directions, each with a different answer.

The numbers below are **reference pixels** — the pre-scale units of a 22 px cell
that `CameraView.WorldVisualScale` turns into screen pixels. Mixing them up with
screen pixels is a mistake this corner of the code has made twice, so every
figure here is stated in reference pixels and every constant lives in
`DungeonFortress.Presentation` rather than in the adapter.

| Rock is | What it paints inside the room's cell | What the border does |
|---|---|---|
| north | facade overhanging 3.0, plus the lower half of the seam that closes it (0.625) | inset 5.625 + purpose step |
| east or west, or diagonally so | half the dark side seam, which is centred on the shared cell boundary (0.625) | inset 2.625 + purpose step |
| south | the whole lifted top mass, 8.0, plus the upper half of the bright seam along it (0.625) | nothing — see below |
| nothing | — | inset 2.0 + purpose step, the ladder Issue #52 bought |

The base is the deepest any of the room's own walled sides demands; the
per-purpose step (0, 1.5 or 3.0) is added on top, so two rooms painted over each
other still draw two lines. `RoomGeometry.BorderInsetFor` is the single place
this is decided, and both `DrawRoomBorder` and `DrawRoomLabel` read it — the
caption moves down with the border it would otherwise sit on.

Two things are worth naming rather than leaving to be rediscovered.

**East and west read the diagonal too.** A wall's side seam runs the height of a
mass that is lifted above the wall's own row and hangs below it, so a wall one
row up and one column across paints inside the cell at exactly the same depth as
a wall straight beside it. `quarters@19,2` has both; a room whose only wall is
diagonal would have neither, under a predicate that only read straight
neighbours.

**South is an exception, and it is the projection's, not the border's.** A wall
standing directly south of a room is drawn *in front of* it: its top rises eight
reference pixels above its own footprint and covers the bottom of the cell
behind it outright. Clearing that would need a base inset of 10.625 — past
`RoomGeometry.MaximumBorderInset` of 10.0, the depth at which the **stroke
bands** of the two opposite sides of a one-cell room meet, the sides themselves
meeting at 11.0 — and that is before a single purpose step is added. So no inset
is the answer, and the answer the project already gave is the one above: the
segment such a wall would swallow is drawn after the depth pass, so a room keeps
its south edge instead of losing it under the wall. Since Issue #156 that is only
that segment and not the whole border — the next section is why.

**A wall is its rectangles plus its seams, and the seams are bands.** That
sentence is the whole issue in one line, and it is stated separately because
getting it wrong is what left a defect behind twice: Issue #139 cleared the
facade *rectangle* and not the seam drawn along its lower edge, so it bought
0.625 fewer reference pixels than it meant to; independent review of Issue #147
then found the same omission in the south arithmetic above, where the seam is on
top of the mass rather than under the facade. Three measurements pin it now, one
per direction a seam reaches out of the footprint — `A_walls_side_seam…`,
`A_walls_facade_seam…`, `A_walls_top_seam…` — and each has its own mutant,
because a coarse one that zeroes every seam at once cannot show which direction
is covered.

What checks the borders themselves is `RoomWallClearanceTests`, and it is
deliberately not written per mechanism. It takes every rectangle a wall actually
paints (`WallRenderGeometry.DrawnBands` — top mass, facade, and every seam
widened into the band `DrawLine` paints it as) against every border segment a
room actually draws (`RoomGeometry.BorderEdges`, which keeps the cell and side
each segment came from), for every room of the shipped map, at 32, 40 and 48 px.
A mechanism nobody has thought of fails it the same way a named one does. The
gap it requires is `RoomGeometry.WallVisibleGap`, one reference pixel, and the
shipped map's tightest sides — `farm@1,1` north, `quarters@19,2` west and east —
sit exactly there by construction.

The same file also measures the ladder that shipped before Issue #147, so the
"before" column of `evidence/147-gaps-before.json` stays reproducible after the
policy is gone. Under it `quarters@19,2` cleared the wall beside it by 0.375
reference pixels — 0.55 screen pixels at the smallest tile — and `farm@1,1`
cleared the facade above it by the same 0.375, for the reason above.

### A room's border is under the body standing on it (Issue #156)

The owner reported the consequence of the paragraph above from playtest: «наверно
существо должно быть над границей комнаты, а не под ней». Every goblin on the
bottom row of the kitchen and the larder had its own room's outline drawn across
it. The border was an informational mark, informational marks are drawn after the
depth pass, and the whole border paid the price of the one segment that needed it.

The rule of that pass — *a mark that can share a cell with a body must not hide
it* — did not catch this, and the reason is worth keeping: the border is declared
`StrokeOnly`, and the recorded reason was that a line with no fill hides nothing.
A stroke two reference pixels wide across the middle of a twenty-two pixel cell
does. Fill was never the point; **covering** was.

So the border is drawn in **two layers**, `RoomGeometry.RoomBorderLayer`, and the
split is a measurement rather than a named side:

| Layer | Pass | What is in it |
|---|---|---|
| `UnderBodies` | below depth, last of that pass | every piece a wall in front does not paint over whole |
| `OverWallInFront` | above depth | a piece whose entire stroke band is inside the union of the drawn bands of the walls in the row below it |

`RoomGeometry.LayerOf` is the whole of the decision, and
`RoomGeometry.WallBandsInFrontOf` is what it is asked about. Three things are load
bearing:

- coverage is asked of the **union** of the wall's bands, not of any one of them.
  The first version asked for a single band and answered "no" for the kitchen,
  whose south stroke straddles the boundary between the wall's lifted top mass and
  the bright seam along it, with both of them painting over it;
- only the row directly south counts, because that is the only direction from
  which a wall is drawn *after* a body standing on the cell. A wall to the north
  hangs its facade into the cell as well and the body walks over that, so its band
  is no shelter;
- answering "no" wrongly costs a piece a wall clips; answering "yes" wrongly costs
  the whole issue. It is built to fail towards "no".

### The unit is a piece, not an edge

The first round of Issue #156 classified a whole boundary edge at a time, and
independent review found what that costs at a corner. On `quarters@19,2` the south
edge of the cell with a wall in front was swallowed whole and stayed above the
depth pass; the west edge meeting it is covered by that same wall only along its
lower few pixels, so it went below and the wall cut it off short. **The outline
opened at that corner** while the opposite corner of the same room, with no wall in
front, stayed shut.

That matters more than it sounds. ADR 0013 and Issue #52 bought exactly the
property that broke: a room is *one line around the whole patch*, not a frame per
cell. The gap is `8.625 − (inset + 1.0)` reference pixels — the wall's reach above
its own footprint less where the horizontal stroke's upper edge sits:

| Room | Inset | Gap in the first round | Gap now |
|---|---:|---:|---:|
| `quarters@19,2` | 2.625 | **5.0** | −1.0 (they overlap by a half-stroke) |
| `kitchen@9,6` | 7.125 | 0.5 | −1.0 |
| `larder@13,6` | 8.625 | −1.0 (already met) | −1.0 |

So `RoomGeometry.BorderPieces` cuts each edge **where the answer changes** instead
of classifying it whole: the covered tail of a vertical edge goes above the depth
pass together with the horizontal edge it meets, and the corner closes. The cut
points are the wall bands' own boundaries along the segment's axis, so the answer
is constant inside every piece — the same argument `IsCoveredBy` makes across two
axes, used along one.

`An_outline_closes_at_the_corner_a_wall_in_front_reaches` measures the gap on the
frame as a player sees it — a piece below the depth pass is followed only as far as
the wall's paint starts — and `The_first_round_of_156_opened_the_corner_at_a_wall_in_front`
keeps the "before" column of that table reproducible, the same way every other
"before" in this corner of the codebase is kept.

### What holds it

`RoomBorderDepthTests` holds both halves, and each half has its own mutant on the
same one-line expression:

| Mutation | What fails |
|---|---|
| `LayerOf` hardwired `OverWallInFront` — the whole border back above the depth pass | `No_stroke_above_the_depth_pass_lands_on_a_body_that_is_visible` |
| `LayerOf` hardwired `UnderBodies` — the whole border below it | `A_wall_in_front_keeps_the_piece_it_swallows_above_the_depth_pass` |
| the decision taken per edge instead of per piece | `An_outline_closes_at_the_corner_a_wall_in_front_reaches` |
| `WallBandsInFrontOf` reading the row *north* instead of south | collapses into the second row above, and fails the same way |
| partial coverage accepted as coverage instead of `IsCoveredBy` | `No_stroke_above_the_depth_pass_lands_on_a_body_that_is_visible` — the larder's two cells move above the depth pass and the goblin on 13,8 is crossed again |
| either routine moved between passes in `WorldDrawOrder` | `The_two_halves_of_the_border_are_declared_in_the_two_passes`, and `DrawMap_runs_the_declared_steps_in_the_declared_order` |
| the adapter drawing both layers from one routine | `The_adapter_draws_each_layer_in_the_pass_it_is_declared_in` |

The first check is the owner's complaint as a measurement: for every room, every
piece drawn above the depth pass, and every cell of the map a body can stand on
(plus the midpoint of every step between two of them, because a render centre is
interpolated), an overlap between a stroke and a body's drawn rectangle has to be
painted over by walls this frame draws *in front of* that body. An overlap with no
such wall is a creature with a line through it. `The_border_used_to_be_drawn_over_
every_body_that_stood_on_it` runs the same measurement against the arrangement
that shipped before, so the "before" column of `evidence/156-before.json` stays
reproducible and the check is known to be able to fail: **226** crossings, in all
four rooms of the map, at every tile size — pinned as `Assert.Equal(226, …)` and by
name, not merely as "more than none". `evidence/156-mutations.json` records each
mutant against a committed green state, with what stayed green as well as what went
red, and which of the greens is vacuous.

**The price, in two cells.** The larder's two front-wall cells keep their south
stroke below the depth pass, because its ladder reaches 8.625 reference pixels and
one of the two pixels of that stroke is drawn above everything the wall paints.
Above the depth pass that pixel lands on the goblin standing there; below it the
wall clips the other pixel, and the larder keeps a line half as thick along those
two cells — about 4.4 screen pixels against 8.7 on the cells beside it at tile 48.
The neighbouring kitchen, whose shallower ladder lets the wall swallow its stroke
whole, keeps full thickness over its own wall, so two adjacent rooms behave
differently in one frame with nothing in the world to explain it. The trade is
taken deliberately and `The_shipped_map_pays_for_the_exception_in_two_cells_of_the_larder`
pins both halves of it by name; the asymmetry is a finding for the debt ledger
rather than something to argue away here.

There is no second visible change: a part of an east or west edge that is *behind*
a wall in front — above the tail that closes the corner — does not exist. The
vertical stroke strip sits at `inset ± 1.0` reference pixels from the cell
boundary, and `inset ≥ 2.625`, so in x it is always strictly inside the cell;
the wall's top and its bright seam run the full width, so any point of the
vertical inside the wall's paint is covered entirely and drawn above the depth
pass.

## Memory of place (Issue #117)

A creature that broke or was put down remembers the tile it was standing on and
will not start work within `T.memory_avoid_radius` of it again. Three things show
that to the player, and none of them needs the log:

- **the map.** Selecting a creature outlines the places it remembers: a ring and
  a diagonal, amber for a broken nerve and red for a wound. `DrawRememberedPlaces`
  in the adapter, declared in `WorldDrawOrder` and governed by
  `OverlayMark.RememberedPlace`, which is `StrokeOnly` — the mark never fills,
  because the whole reading is that somebody else is visibly still working on the
  tile next to it. It is drawn for the selected creature only: nine creatures'
  memories at once would be a map full of crosses saying nothing about anybody;
- **the inspector**, on one line beginning `AVOIDS`: every remembered place,
  newest first, each naming the tile, the tick and which of the two things
  happened there — `AVOIDS (18,7) t1703 panic · (24,7) t1316 panic`. It is one
  line rather than a heading and a line per place because the HUD overflow guard
  refused the taller version: a creature carrying several remembered places
  overflowed the panel. It is on the panel and not only in the feed because the
  feed is a digest (Issue #145), not a ticker: its top can stand still for
  hundreds of ticks, and the question "why is this one standing about" is asked
  long after;
- **the event feed**, as a sentence. Since this step the feed no longer prints
  reason codes at all: `DungeonFortress.Presentation.EventNarration` turns the
  code plus its own `details`, `jobKind` and `target` into a sentence with the
  creature's name in front of it;
- **the story of the selected creature** (Issue #128, reordered by Issue #140).
  The event feed is the domain's until somebody is selected and that creature's
  afterwards: a header naming it and three counts — shown, in all, and how many
  of them mattered, `STORY · Брусок · 4 of 397 · 29 mattered` — then up to
  `HudText.CreatureStoryLines` decisions, newest first, a folded one printing the
  span of ticks it held and how many. The marks answered "it remembers something
  happened here"; this answers "and here is what it did about it". Reading it
  needs no tick: it is `events[]` filtered by creature id, which the snapshot
  already publishes.

  **The lines are the decisions that mattered, not the newest ones.** A creature's
  journal is 91.1 % to 97.5 % waiting for stock, being blocked in a corridor and
  stepping aside, over the six runs of the seed matrix, so the newest four almost
  always were: on `baseline` at tick 2400 none of the three creatures that ever
  refused work by memory of a place had that refusal on its panel
  (`evidence/140-before.json`). Per creature the promise is that routine is the
  majority rather than that it is over 92 %: that figure was one party, and after
  Issue #129 the minimum over the matrix is 75.9 % on the party that starves
  after winning its fights (Issue #171). What the paragraph above actually needs
  is asserted instead, without a percentage — every creature of every run is shown
  at least one decision that mattered and that its four newest entries would have
  missed, 9 of 9 on all six runs before and after #129
  (`evidence/129-presentation.json`). That share is a run rather than a remembered
  number, and it is the run this document quotes:

  ```powershell
  dotnet test .\tests\DungeonFortress.Presentation.Tests -c Release `
    --filter "FullyQualifiedName~Most_of_what_a_creature_decides_is_routine" `
    --logger "console;verbosity=detailed"
  ```

  The panel takes one entry per reason code — the **last** one of that kind, so
  fourteen refusals cannot become four lines of the same refusal — orders them by
  `HudText.StoryWeight` and then by recency, cuts to four and puts them back in
  time order. Routine fills whatever is left over, so a panel is never blank
  before the first wave. It is as tall as the creature has *kinds* of decision,
  so early in a party it is genuinely shorter: at tick 20 eight creatures of nine
  show one line, and by tick 600 all nine are back to four.

  Run the frame the reading below comes from:

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
    -GodotPath "<path-to-godot-console>" `
    -Fixture baseline -ScreenshotTicks 2390 -SelectCreature 0 `
    -ScreenshotPath .artifacts\story.png `
    -TileSize 40 -CameraZoom 0.75 -CameraPosition 560,320 -UiScale 1 -FrameSize 1280x720
  ```

  `ui.feedback` of that run, every line of it:

  ```text
  STORY · Брусок · 4 of 397 · 29 mattered
  t2384 · will not take cooking at (14,7): nerve broke at (18,7) t1703.
  t2350 · joined the fight for wave 4.
  t2271 · is whole again.
  t2011 · broke and ran: 35% health, 4 raiders close, 0 ally down.
  ```

  Read bottom to top that is the exit criterion of the slice in four lines: it
  broke and ran at t2011 on 35 % health, it was whole again at t2271, it went
  back to the fourth wave at t2350, and at t2384 it would not take the cooking at
  (14,7) because its nerve had gone at (18,7) at t1703. The inspector on the same
  frame reads `AVOIDS (25,8) t2011 panic · (18,7) t1703 panic · (24,7) t1316
  panic`, so the panel and the inspector are two views of the same journal.

**The code did not go anywhere.** It is still what `lastDecision` and every
entry of the canonical event log carry, which is an invariant of
[ADR 0010](../decisions/0010-contract-invariants-and-tuning.md); the adapter
reads it. A code the adapter has never been taught is refused rather than
rendered as a code it knows — the same choice `HudText.WavePhase` makes about the
end of a party, and for the same reason.

The boundary this side of the seam is unchanged and the overlay respects it: the
remembered places are read straight off `creatures[].rememberedPlaces` in the
published snapshot, so the mark is a projection of facts and needs no tick to
run.

## Excavation (Issue #24)

`DIG [D]` marks internal rock for excavation; `CANCEL DIG [X]` withdraws a mark.
Both are dragged as a rectangle and both stay held until `Esc` or right-click
puts them away. Neither brush chooses a worker: the player states intent, and a
free creature picks the `Dig` job through the normal autonomous scoring.

A stroke only carries a tile the simulation would accept. Floor, the gate, the
map boundary and an already designated tile are dropped from the selection —
they are not part of the count and not part of the command — so a drag never
produces a rejected command and the refusal is explained in the feedback line.

Reading the map without the log:

| Reading | What it means |
|---|---|
| light warm wall with raised top and dark facade | diggable internal rock |
| dark warm wall with the same geometry | the map boundary; it is never diggable |
| amber outline on every rock cell | shown only while the `DIG` brush is active: these are the legal targets of a stroke |
| amber tile with an X | designated and reachable, or reserved by a worker |
| amber fill rising from the bottom plus a yellow bar | excavation in progress |
| red tile with an X | designated but no free neighbouring floor to work from |
| gray tile with an X | designated while the `Dig` priority is 0 |
| pale blue tile | floor created by excavation |
| gray dot with a dark rim | loose stone left by a finished dig |

Rock is drawn as a gapless warm mass, well above the cool blue floor in both hue
and brightness. Neighbour variants suppress internal seams; exposed south edges
show a darker facade. The first attempt used a near-black flat block that owner
playtest reported as indistinguishable from floor.

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
  -TileSize 40 -CameraZoom 0.75 -CameraPosition '560,320' -UiScale 1 -FrameSize 1280x720 `
  -ScreenshotPath issue26\stone-1-loose-no-stockpile.png
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoStone -ScreenshotTicks 336 -SelectCell 25,1 `
  -TileSize 40 -CameraZoom 0.75 -CameraPosition '560,320' -UiScale 1 -FrameSize 1280x720 `
  -ScreenshotPath issue26\stone-2-in-transit.png
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoStone -ScreenshotTicks 950 -SelectCell 23,1 `
  -TileSize 40 -CameraZoom 0.75 -CameraPosition '560,320' -UiScale 1 -FrameSize 1280x720 `
  -ScreenshotPath issue26\stone-3-stockpile-full.png
```

Each capture prints `stoneProduced`, `looseStone`, `carriedStone`, `storedStone`
and `stockpileCapacity` next to its checksum, so a frame carries its own
conservation evidence instead of being trusted as a picture.

## Building the first functional room (Issue #48)

`BUILD [C]` marks plain floor as a training-post blueprint; `UNBLD [V]` withdraws
one. Unlike the stockpile brush, the legal targets **include ground the player
created by digging** — a room out of carved space is the point of the step. The
list comes from `map.buildFloorTiles` in the snapshot, so the adapter holds no
copy of the rule, and while the brush is active every legal cell is outlined.

Nobody is ordered to fetch anything or to build anything. A post costs
`T.build_stone_cost` = 2 stone; free creatures pick the `Haul` job that brings it
— from a loose pile or back out of the material stockpile — and then the `Build`
job, through the same autonomous scoring the food chain uses.

Once the post stands, paint `TrainingGround` over it with `Z` + `B`, raise the
`Drill` priority with `J` and `+`, and the crew starts training at a post that
did not exist at tick 0. Nothing distinguishes it from the four authored posts:
the built one enters the same station list and produces the same `Drill` work.

The post is a **graybox primitive with a caption**, deliberately: `ADR 0008` is
accepted but not implemented, the map is still flat squares, and a labelled teal
block answers the slice's question — "does turning a plan into a working object
feel like ownership?" — as well as finished art would. Asset generation is Codex's
job and comes after the projection lands; see
[`ANIMATION_PIPELINE.md`](../art/ANIMATION_PIPELINE.md).

Reading construction without the log:

| Reading | What it means |
|---|---|
| teal outlined cell with `POST?` and hollow pips | a blueprint; each pip is one of the two stones it needs |
| filled pale pip inside a blueprint | that stone has arrived |
| hollow blue pip inside a blueprint | that stone is booked by a carrier on the way |
| amber blueprint outline | there is no free stone for it yet |
| grey blueprint outline | `Build` or `Haul` priority is 0 |
| red blueprint outline | nobody may step on the site |
| teal bar across the top of a blueprint | construction in progress |
| solid teal block labelled `POST` | a training post — authored or built |

Clicking a blueprint states which of those it is, how much stone arrived, who
volunteered and how far the work got. Clicking the finished post states what it
cost and the one condition still standing between it and training: the
`TrainingGround` zone or the `Drill` priority. Clicking the carrier states
whether it is taking the stone out of the stockpile and which site booked it.

The top line still reports stone as `stone {loose}L {carried}C {stored}/{cap}S`.
Stone on a site and stone already spent are deliberately **not** folded into that
line — the HUD summary is exactly two lines and a third would be drawn over the
toolbar. They are reported structurally instead, in `stocks.siteStone` and
`economy.stoneConsumed`, which is where the conservation invariant is checked.

### Reproducible construction frames

`--demo-build` replays a fixed brush session through the same code path a human
uses: `DIG` marks `(25,1) (25,2) (25,3) (26,1)`, `[M]` paints the material
stockpile `(22,1) (23,1)` at tick 200, and at tick 1000 — after every block is
already put away — `[C]` marks a blueprint on `(25,2)`, `[B]` zones it as a
`TrainingGround` and `Drill` is switched on. The stone therefore has to come back
out of the stockpile.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoBuild -ScreenshotTicks 1001 -SelectCell 25,2 `
  -TileSize 40 -CameraZoom 0.75 -CameraPosition '560,320' -UiScale 1 -FrameSize 1280x720 `
  -ScreenshotPath issue48\build-1-blueprint-waiting.png
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoBuild -ScreenshotTicks 1030 -SelectCell 25,2 `
  -TileSize 40 -CameraZoom 0.75 -CameraPosition '560,320' -UiScale 1 -FrameSize 1280x720 `
  -ScreenshotPath issue48\build-2-carrier-on-the-way.png
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoBuild -ScreenshotTicks 1150 -SelectCell 25,2 `
  -TileSize 40 -CameraZoom 0.75 -CameraPosition '560,320' -UiScale 1 -FrameSize 1280x720 `
  -ScreenshotPath issue48\build-3-post-and-drill.png
```

Stone is now consumed, and the conservation invariant grew to match:
`stoneProduced = looseStone + carriedStone + storedStone + siteStone +
stoneConsumed`. Every capture and every headless run prints all five, so a frame
still carries its own conservation evidence instead of being trusted as a picture.

### Reproducible excavation frames

`--demo-dig` replays a fixed brush session through the same code path a human
uses: `DIG` marks `(25,1) (25,2) (25,3) (26,1)`, then `CANCEL DIG` withdraws
`(26,3)`. It deliberately ends holding the `DIG` brush, so the capture also shows
the outline every still-diggable tile gets. Combined with `--screenshot-ticks` it
captures the before, during and after frames:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoDig -ScreenshotTicks 3 `
  -TileSize 40 -CameraZoom 0.75 -CameraPosition '560,320' -UiScale 1 -FrameSize 1280x720 `
  -ScreenshotPath issue24\dig-before.png
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoDig -ScreenshotTicks 30 `
  -TileSize 40 -CameraZoom 0.75 -CameraPosition '560,320' -UiScale 1 -FrameSize 1280x720 `
  -ScreenshotPath issue24\dig-during.png
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline -DemoDig -ScreenshotTicks 120 `
  -TileSize 40 -CameraZoom 0.75 -CameraPosition '560,320' -UiScale 1 -FrameSize 1280x720 `
  -ScreenshotPath issue24\dig-after.png
```

### The frame a wave ends on (Issue #331)

`--demo-moment-of-truth` plays the shipped journal until the party stops by
itself between two waves and waits there. It stops on a **state** and not on a
tick, for the reason the simulation's own tests give: the tick a wave ends on is
emergent, and a number would be a balance value pretending to be a fixture. That
is also why `--screenshot-ticks` cannot reach this frame — running "to tick N"
past the end of a wave spends the whole 40-step window on the way and arrives
after the question has closed.

Nothing about it is simulation: it runs ordinary steps of the shipped log and
stops on one of them.

`run-game.ps1` has no switch for it yet — its `-Demo*` switches are enumerated
one by one, and that script was outside the partition of Issue #331. Until one is
added the flag is reached through the engine directly, in the same isolated
runtime profile `verify.ps1` uses:

```powershell
. .\scripts\GodotTools.ps1
Initialize-GodotRuntimeEnvironment -RepositoryRoot (Get-Location).Path
Invoke-GodotChecked -GodotPath (Resolve-GodotExecutable) -Arguments @(
  "--path", "src\DungeonFortress.Game", "--resolution", "1600x900",
  "--", "--fixture", "baseline", "--demo-moment-of-truth",
  "--screenshot", "evidence\331-moment-of-truth.png", "--screenshot-ticks", "1",
  "--tile-size", "40", "--camera-zoom", "0.5", "--camera-position", "560,320",
  "--ui-scale", "1", "--frame-size", "1600x900"
) -ExpectedSuccessEvent "godot_graybox_screenshot"
```

The frame it produces, and the readings taken from it, are
`evidence/331-moment-of-truth.png` and `evidence/331-frame.json`.

### Where a command lands while the window is open (Issue #351)

Every command the player issues is *proved rather than trusted*: the adapter
appends it to the session's log, validates the whole log, replays it from tick 0
and makes the world that replay produces the live one. So a running session stays
a function of its log, and the position that replay is driven to is what decides
which state the player is looking at.

That position is **a tick and the number of steps an open moment of truth has
spent** — `DungeonFortress.Presentation.WorldPosition` — and not a tick alone.
That names two of the three states tick `T` can be in; the third one it still
cannot name is the caveat below. A
tick alone stopped naming one state in Issue #312: while the window is open
`PrototypeWorld.Step` spends a step waiting and never moves `CurrentTick`, so one
tick number names up to 41 different states of the same party. Addressed by the
tick alone, every press rebuilt the world to the step the window *opened* on: the
counter stayed at `3 of 3 unanswered` no matter what was pressed, and the second
answer silently erased the first. That was the Issue #351 playtest blocker; the
rule now lives in one place, `WorldReplay`, and
`MomentOfTruthVerdictReplayTests` walks the whole circle — press, rebuild,
snapshot, band — on it.

Three consequences worth knowing at the keyboard:

- **An answer is visible at once.** Away from the window a command still
  activates on the *next* tick, exactly as the feedback line says. Inside it the
  clock is stopped on purpose, so a command that waited for the next step would
  wait for a step that never comes; the replay therefore runs the window on far
  enough for the world to hear it. That costs one of the 40 steps, and costs it
  once — the same step hears every command dated at the frozen tick.
- **Answering the last card ends the pause.** `CloseMomentOfTruth` has always
  closed a fully answered window, so the tick it was holding back runs and the
  band disappears. Nothing else runs: exactly one tick.
- **`REPLAY` (`Y`) agrees while the window is open.** It replays the session's own
  log to the same address, so a session whose verdicts were cast inside an open
  window reproduces its own checksum instead of reporting
  `replay checksum MISMATCH`. It does **not** agree once the window has run out —
  see the caveat below.

No rule of the moment of truth moved: three cards, 40 steps and what silence
costs are all `PrototypeWorld`'s, and `src/DungeonFortress.Simulation/**` is
untouched by this change.

#### The state this address still cannot name (Issue #354)

Tick `T` has **three** distinguishable states, and `WorldPosition` names two of
them. The third is *the window has closed by running out of its 40 steps and the
tick it was holding back has not run yet*: a snapshot of it reads
`tick = T, open = false`, which is the same reading a snapshot taken before the
window ever opened gives. `WorldReplay.PositionOf` therefore answers
`WaitedSteps = 0` — a closed window carries no step count in the snapshot at
all — and a rebuild from that address lands back where the window *opened*.

Measured by independent review of PR #353 on `baseline`, whose first window opens
on tick 1336:

```
after 40 steps: tick=1336 open=False
grudges after silence: 2,2,0
REPLAY agrees? False
after the nudge: tick=1336 open=True unanswered=3 stepsLeft=39
grudges after the nudge: 0,0,0
```

The "nudge" is an ordinary `SetPriorityCommand`. So a player who spent the whole
window in silence, and was charged the grudge that silence costs, has that charge
undone by the next press of anything — and `REPLAY` reports a mismatch about the
same stretch of the session.

This predates Issue #351 and is not a regression of it: on the base commit the
same press also erased every verdict already cast. What Issue #351 closed is the
open window; the expired one is
[#354](https://github.com/anshushunov/dungeon-fortress/issues/354), with the
reproduction above and the cure independent review proposed — the adapter
counting steps itself, so that an expired window is addressed as `(T, 40)`
instead of as a tick with nothing after it. Curing it needs `Main.Session.cs`,
which was outside the partition of Issue #351.

#### The presentation layer now holds a rule that runs the world

`WorldReplay` is the first inhabitant of `DungeonFortress.Presentation` that
**builds and runs** a `PrototypeWorld` rather than projecting a snapshot of one.
Everything else in the assembly — `MomentOfTruthPanel`, `HudText`,
`MapProjection`, `BlowReadout` — is a pure function of canonical state.

It is here for the reason [ADR
0011](../decisions/0011-presentation-layer-without-engine.md) put the other rules
here: no test project references `DungeonFortress.Game`, so a rule that lives
there can only be read as text. The rebuild is the seam every player command
passes through, and while it lived in `Main` nothing executed it in a test —
which is exactly how a verdict that never reached the screen survived two rounds
of independent review and two playtests.

The widening is written down rather than left to be discovered: **"engine-free"
is not the same as "simulation-free"**. ADR 0011 is not amended by this —
changing a decision already taken is its own decision and needs its own ADR — but
a reader who takes the ADR's title literally should know that the layer now also
owns the answer to *which position a rebuilt world is driven to*.

## Wave checkpoints

A party is a sequence of waves, not one raid. At tick 300 the HUD announces wave
1 with the composition its renown earned; every later wave is announced 200 ticks
before it lands. The summary line names the wave in hand
(`WAVE 2/4 IN 120t ×6`), and the first line carries the two numbers the party is
read by — `renown` and `strength`, each with a trend arrow against the previous
wave — plus the head count.

The projection reads `threat`, `waves`, `domain`, `raiders` and `sessionResult`
from the same `PrototypeWorld` snapshot as the economy; it never owns combat
state. Capture a deterministic wave frame with `-ScreenshotTicks 1340` (wave 1 in
progress) or `-ScreenshotTicks 1700` (wave 2).

## Deterministic visual evidence

This writes an ignored PNG, advances the same fixture a fixed number of ticks,
then exits after emitting a structured result line:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 `
  -Fixture baseline `
  -ScreenshotTicks 180 `
  -SelectCreature 3 `
  -DemoControls `
  -TileSize 40 `
  -CameraZoom 0.75 `
  -CameraPosition '560,320' `
  -UiScale 1 `
  -FrameSize 1280x720 `
  -ScreenshotPath visual\graybox-baseline-t180.png
```

The result includes fixture, seed, tick, canonical checksum, PNG path,
`loadedSpriteStates`, `fallbackSpriteDraws` and a `view` object. `view` records
the actual and requested frame, world viewport, tile size, camera position,
camera node position, zoom, visible world size, UI scale, texture filtering and
whether the goblin sprites have mipmaps. It also reports the goblin's world-space
and resulting screen-space draw size — height and width both, since Issue #77
connected a 17:12 pack — so readability and the drawn shape at a chosen view can
be checked without estimating pixels from a screenshot. Visual smoke requires all
**six** goblin states, mipmaps and zero fallback draws. The path must be relative and is always
resolved below `.artifacts/`; rooted and traversal paths are rejected. Do not
commit the image. `--smoke` and
`--visible-smoke` continue to report structured runtime diagnostics for
automation.

For PR evidence, prefer the bundled wrapper described in
[`EVIDENCE_WORKFLOW.md`](EVIDENCE_WORKFLOW.md). It reads a tracked declarative
spec, performs the capture twice, compares the canonical checksum and PNG bytes,
then writes ignored JSON/Markdown manifests with the exact command and SHA-256.
The direct command above remains the single-frame launcher and the command that
the manifest records.

A screenshot made by calling Godot directly is rejected unless all five
pixel-affecting inputs are explicit: `--tile-size`, `--camera-zoom`,
`--camera-position`, `--ui-scale` and `--frame-size`. `run-game.ps1` passes every
one it was given and refuses a `-ScreenshotPath` without `-CameraZoom` before
restore and build, so a capture can never inherit the zoom the automatic rule
picked for this window. The project remains `canvas_items` + `expand`; for an explicit
frame the launcher also makes that frame the logical rendering size, so
1600x900 at zoom 1 exposes more world than 1280x720 instead of scaling the same
1280x720 rectangle. An ordinary interactive window resize synchronizes the
logical rendering size to the native window too, and since Issue #100 it also
recomputes the UI scale from the new frame rather than leaving the HUD at the
scale it launched with; a reproducible capture keeps its explicit frame fixed.

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
| `momentOfTruth` | the band the moment of truth is answered on (Issue #331): whether it is `open`, whether the node is `visible`, whether the clock is `paused`, the path of that node under the HUD root, the wave, `unanswered`, `stepsLeft`, the heading, the explanation, and one entry per card with its text, its verdict, whether it is selected and the ids of its three buttons. `open: true` with `paused: false` is the defect shape: a step of an open window is spent waiting rather than played, so a running clock burns all 40 steps in 6.7 seconds at 1x and 0.42 at 16x, and the band flashes past unread |
| `pending` | intent accepted for this tick that the tick has not applied yet — marks, withdrawals and priority changes — or `null` |

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

**Every number in HUD text is formatted with the invariant culture** (Issue #46).
The same text is compared in two environments with two different cultures —
locally through `verify.ps1` and in CI through `DungeonFortress.Presentation.Tests`
— so a decimal separator taken from the machine would pass in one and fail in the
other. The playback speed was the only such number and used to print `0,5x` on a
ru-RU desktop; nothing caught it because all three reference frames are paused and
never reached the branch. HUD text is a checked artefact, not a localised
interface: localisation, if it ever happens, is a decision of its own and not a
property of the build machine.

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

**It runs in `_Ready`, on every entry point, at the live frame/UI-scale pair plus
seven fixed pairs.** Since the HUD became a Control tree the measurement is only
meaningful *after* a layout pass,
which is the opposite of what the old absolute layout needed: a container hands a
label its size, so that size is the designed one and an unclipped label can no
longer re-expand to its own content. Godot sorts containers on a deferred pass, so
`LayoutHud()` notifies the subtree and gets the same placement a frame would
produce, synchronously. It then repeats the whole check at 1280x720@1,
1366x768@1, 1600x900@1, 1024x768@1, 1920x1080@1.25, 2048x1440@2 and
3044x1722@2, so "the layout follows the viewport and UI scale" is a checked claim
rather than an intention — a guard that only ever saw one pair cannot tell a
responsive layout from a lucky one. The last pair is the owner's maximized client
area at the scale the automatic rule gives it, and until Issue #86 no check had
ever measured a frame that large.

The Godot stage also injects an 80-line inspector at 1024x768@1 and requires the
guard to emit a structured error and exit 1. This negative proof pins the
required logical width to 1024 and shows that a layout regression is actually
distinguishable from a fitting layout.

A size that is absent from that list is not unsupported, it is unmeasured. The old
960x540 frame is absent because the current text does not fit it: the side column
needs about 33 lines and that frame offers about 29.

Every structured output carries `labelFit`, now shaped as the live `viewport`, the
`checkedViewports`, and a `labels` array with `neededLines`, `visibleLines`,
`hardLines`, `width` and `height` per label. A run therefore states what the guard
had to work with instead of the guard being trusted.

### HUD readability guard

Fitting and being readable are different questions, and until Issue #86 only the
first one had a check. On a 3044x1722 client area at UI scale 1 every line fitted
its rectangle and the legend was drawn at eight physical pixels; the overflow
guard was green and the interface was unusable.

The rule is engine-free and lives in
`src/DungeonFortress.Presentation/HudReadability.cs`, per
[ADR 0011](../decisions/0011-presentation-layer-without-engine.md). It has two
halves:

| Rule | What it says | Where it bites |
|---|---|---|
| physical floor | no HUD text may be drawn smaller than the smallest size the HUD is authored with at the frame it is authored for — 8 px | a piece of HUD text re-authored smaller than the rest |
| density ceiling | `logicalDensity` — how many authored 1280x720 rectangles the frame is worth, divided by the UI scale — may not exceed 1.25 while the scale can still rise | a window that grew without the HUD scale following it, which is the defect verbatim: 3044x1722 at scale 1 measures 2.38 |

1.25 is not a taste. It is the largest ratio between two neighbouring automatic
scale steps, so a policy that always picks the largest step a frame allows can
never exceed it. `HudReadabilityTests` computes that ratio from
`CameraView.AutomaticUiScales` and compares it with the constant, which closes
both directions at once: raising the ceiling fails, and changing the steps
without revisiting the ceiling fails too. Past scale 2 nothing can rise further,
so a frame that reached the ceiling is excused **by name** rather than silently.

The adapter measures and calls, nothing more: `HudTextSizes()` **walks the HUD
subtree** and reads `GetThemeFontSize` off every `Label` and `Button` it finds,
and `AssertHudTextReadable()` hands the result to the policy on every entry
point. The walk is the reason the guard reacts to a change in the HUD rather than
only to a change in its own constants — and it is a walk rather than a list
because review measured the difference: the first version listed the nodes the
adapter kept a reference to, the inspector column's `STATE / WHY` heading is a
local variable held by nothing, and re-authoring it at four physical pixels left
every guard green.

**Visibility is deliberately not consulted.** The walk measures what a piece of
text is authored at, not whether it happens to be on screen at that moment: a
four-pixel font is an authoring mistake whether or not a flag currently hides it,
and a rule that skipped hidden nodes would go quiet exactly when a panel is
collapsed. The cost is stated rather than discovered — the first HUD panel with
conditional display will be measured while nobody is looking at it, and the
answer is to author it at a readable size, not to hide it from the guard. An
empty measurement is refused outright for the same reason: nothing to measure
produces no violations, which would read as a pass.

The policy is held against the supported frame matrix at the scale the automatic
rule would choose for each frame — not against the run's own pair, because an
explicit `--ui-scale` is an override a capture declares on purpose, including the
deliberately small ones `verify.ps1` uses, and because a screen larger than the
2× ceiling must not be refused a launch. It ends by requiring the pair Issue #86
was opened about to still be refused; that is a floor under the rules rather than
a pin on them, since it only fires once the ceiling has been relaxed past that
frame's own 2.38 density. Pinning the ceiling is the unit test's job.

A run does not act on its own pair, but it does state a verdict on it:
`view.hudReadability.readable` and `violations` say whether the frame in front of
the player is readable. `--frame-size 3044x1722 --ui-scale 1` — the pair the Issue
was reported on — therefore exits 0 and says `"readable": false` with the density
named, instead of exiting 0 and looking fine.

Measured on the frames that matter, with today's 8 px smallest authored text:

| Frame | Automatic UI scale | Logical density | Smallest HUD text |
|---|---|---|---|
| 1280x720 (authored) | 1 | 1.00 | 8 px |
| 1920x1080 | 1.5 | 1.00 | 12 px |
| 2560x1440 | 2 | 1.00 | 16 px |
| 3044x1722 (owner, maximized) | 2 | 1.19 | 16 px |
| 3840x2160 | 2 (ceiling) | 1.50 | 16 px |

Every structured output carries these numbers as `view.hudReadability`: the
thresholds, the live frame's scale, density, smallest physical text and verdict,
the size of every measured piece of text, and the same measurement repeated over
the whole supported matrix. A run on a laptop therefore still states what the
owner's maximized window would get. The `godot` stage reads three of them and
refuses a run where 1280x720 stops reporting 8 px at scale 1 or reports itself
unreadable, or where 3044x1722 leaves the HUD anywhere in the 8–15 px band.

`--smoke-hud-readability-regression` re-authors the first legend row at four
pixels. Nothing about the text changes, so the overflow guard stays green and
only readability can notice; the `godot` stage requires that run to exit 1. It is
the exact counterpart of the overflow guard's own negative run, and together they
are what replaced the inert `--strict-hud-fit` flag removed in Issue #49.

**The tooltip (Issue #127).** A toolbar button's tooltip is not a descendant of
`_hudRoot`: Godot 4.7 parents it to a separate `PopupPanel` (a `Window`) next to
the hovered button, and that `Window` scales its own content by
`content_scale_factor`, not by `_hudRoot.Scale`.

**How firmly this is known, and it is less firmly than the paragraph above
reads.** The mechanism was established by reading Godot's sources and is
corroborated by the owner's own playtest — a tooltip inheriting `_hudRoot.Scale`
would have grown on the maximised window, and there would have been no defect to
report. It has **not** been confirmed by a live hover-triggered render: that
needs a windowed run with a mouse, which the headless harness cannot do.
`evidence/127-tooltip-scale.json` records the same claim at that lower
confidence, and the two must not drift apart. If the premise is wrong, the fix
over-corrects rather than under-corrects — the tooltip would scale twice.

`HudButton.UiScale` carries the
live scale there instead, kept in step by `Main.LayoutHud`. `CreateControlStrips`
keeps one instance of the tooltip's Control tree, built at UI scale 1
(`HudButton.MakeAuthoredTooltip`), invisible and permanent under `_hudRoot`, so
`HudTextSizes`' existing subtree walk reaches it under the names
`Label[TooltipTitle]` / `Label[TooltipBody]` without any change to the walk
itself. `--smoke-hud-tooltip-readability-regression` is that guard's negative
run, shrinking the sample instead of a legend row.

Two more things prove the *live* scale actually reaches the popup, since the
readability sample above is deliberately frozen at scale 1 and cannot: a
structural test on `Main.LayoutHud`'s own body proves it still assigns the live
`uiScale` to every button, and a raw read of `HudButton.cs` proves
`_MakeCustomTooltip` still calls `BuildTooltip` with that scale rather than a
fixed `1.0` — both in `HudReadabilityTests.cs`, both provable without an engine.

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
60 fps, both landing on the same checksum, `maxRenderStepPixels` 12 and 4
against a 40 px tile. Before interpolation that number was the tile size itself.

The last row only means something while a frame is shorter than a tick. At a frame
rate low enough to cover several ticks the picture legitimately moves more than a
tile, which is why the check pins the frame rate instead of sampling whatever the
machine produces.

### Camera, scale and deterministic evidence (Issue #79)

[ADR 0008](../decisions/0008-three-quarter-projection.md) makes the camera part of
the capture inputs: the same tick at a different camera position or zoom produces a
different picture. The camera position, zoom, tile size, UI scale and frame size
are therefore recorded next to the seed and tick for every reproducible frame.

The golden UI state is unaffected on purpose — it holds no camera-dependent value.
It is what proved the Issue #36 reflow changed where the HUD text sits and not
what it says: all three frames passed without regeneration.

`scripts/verify.ps1 -Stage godot` proves one non-empty canonical checksum is identical
across four combinations of camera position, zoom, frame size and UI scale. The
same stage compares a pure `CameraFrame` prediction with the live `Camera2D`
transform through all five zoom levels at three requested positions (15
transform checks and 15 click checks), drives both map extremes at every zoom
(10 bounds checks), proves a pan moves at every zoom (5 pan checks), and rejects
a point in the HUD. A separate fault-injection run offsets the actual Camera2D
node and must fail that comparison, so the transform evidence does not invert
the same value on both sides. Every camera and view case also reports the
`headless` display server. Screenshot verification waits through the deferred
container layout, then requires the actual Camera2D node to match the frame
derived from that final world viewport. The stage also compares
1280x720 and 1600x900 at zoom 1 and requires the latter to expose a larger world
rectangle.

Five invalid startup cases (zoom, tile size, UI scale, camera position and
fixture) must each emit a `"status":"error"` event and exit 1 within 20 seconds.
This proves structured error reporting remains usable even when parsing or
fixture loading fails before the HUD or canonical snapshot exists.

`scripts/verify.ps1 -Stage screenshots` captures the same explicit baseline
frame twice and requires the PNG files to be byte-for-byte identical before it
captures the prepared raid. These are same-machine repeatability checks, not
committed golden screenshots: the portability reasons in
[Golden UI state](#golden-ui-state) still apply.

The selected 40 px tile scales world-space primitives from the previous 22 px
grid instead of leaving their silhouettes behind at the old size. That is
`CameraView.WorldVisualScale`, and it answers only «how do the authored 22 px
proportions land on this grid».

How large a creature is against that grid is a second, separate question, and
its answer is the owner's: `CameraView.BodyVisualScale = 1.70`, chosen on
2026-08-01 from spike [#142](https://github.com/anshushunov/dungeon-fortress/issues/142)
by clicking through the sizes in a live scene (gate log in
[`GATE_DECISIONS.md`](../product/GATE_DECISIONS.md); 100 % was rejected outright, 200 % as too
large). A body is therefore drawn at `20 × 1.70 × tile / 22` world pixels —
**61.82 px at the shipped 40 px tile**, 49.45 at 32 and 74.18 at 48 — which is
30.91 screen pixels in the `0.5` overview and 123.64 at `2×`. Visual body size is
presentation tuning under [ADR 0010](../decisions/0010-contract-invariants-and-tuning.md):
it reaches no canonical state, and the before/after captures in
`evidence/77-scale-before.json` and `evidence/77-scale-after.json` record the same
checksum on both sides of the change.

The runtime draws the **272×192 v2 creature pack**
([`goblin-v2-provenance.md`](../art/goblin-v2-provenance.md)), which was authored
for exactly this 61.8 px canvas height, and every zoom in the declared range is
inside it: `2×` takes 123.64 px of canvas height from 192 source rows and 175.15
px of width from 272 columns, 0.64 of the source in both directions. Until the
second subtask of Issue #77 connected it the runtime loaded the 96×96
`goblin_*_v1` sheet and `2×` magnified it 1.29×; that number is kept in the pure
camera test as the statement of what changed. The Godot stage publishes and checks
overview, base and detail sizes, and now the width beside the height, because a
run reporting only the height cannot be asked whether the canvas is drawn in the
shape it was authored in. Runtime mipmaps plus `LinearWithMipmaps` keep the source
usable below `1×`.

Six states, not four: `idle`, `work`, `combat`, `windup`, `flinch`, `downed`.
Which pose a body is drawn in is `DungeonFortress.Presentation.BodySprites`, so it
has cases that are checked without starting the engine, and the adapter only hangs
the returned texture on a rectangle. `windup` and `flinch` are loaded and
reachable, and both call sites pass `BodyActionPhase.None`: **nothing in the
snapshot says when a creature is drawing back or being struck.** What it does say
is nearby and not the same thing — `LastDecision` carries `combat_attack` with the
tick the blow *landed* on, a defender that is hit and survives records nothing, and
a raider has no decision field at all. The subtask that makes a blow readable
decides where the phase comes from.

### The rectangle, and where it stands

A body is drawn into a **17:12 rectangle**, because 272/192 is. The height is the
61.82 px above; the width follows from the canvas — `61.82 × 17/12 = 87.58` px at
tile 40 — rather than being written down. The pack's provenance quotes 87.55,
which is the same quantity from a 61.8 px height rounded to one decimal.

`CameraView.GoblinDrawRect` says where it goes: **a body grows upward out of the
ground it stands on.** It is centred horizontally on the render point, which is
what puts the pack's own support centre there — that centre was placed at canvas
`x = 135.5` in four states and `136.0` in the other two, i.e. within 0.5 of a
source pixel, or 0.16 px of drawn width. Vertically it stands on
`CameraView.GoblinFootLine` — 16.67 px below the render centre at tile 40, which
is where the feet of the authored 20-reference-pixel body landed long before Issue
#77 — so the drawn feet move by exactly 0.000000 px at every tile size, and the
body reaches 43.86 px above its centre instead of 18.18. This is also the rule
spike #142's own scene used, so it is the picture the owner was judging when he
chose 170 %.

«Where the feet are» is a property of the sprite pack and is named as one:
`CameraView.SpriteSupportFraction` = 188/192, because every v2 state's last opaque
row is 187 of 192. It moved with the pack — the v1 sheet's was 92/96 — and leaving
it behind would have drawn every creature 1.29 px into the ground at once, without
any frame looking broken.

**Where the ground is, is not a property of the pack**, and connecting the second
one is what separated the two. `GoblinFootLine` was built from
`SpriteSupportFraction` while the game had a single pack; had it stayed that way,
the ground under every creature would have dropped 0.76 px at tile 40 the moment a
canvas grew a shorter transparent tail. It is now stated with the v1 measurement
that produced it, which cannot change again.

The first round of this change shipped a **centred** square instead, which sank
the feet from 16.67 px below the render centre to 28.33 — 11.67 px, 29 % of a
cell, landing outside the cell the body stands on. It was accepted because growing
upward was thought to undo part of Issue #156. Re-measurement after review of PR
#176 found that cost belongs to a *different* rule, about a pixel away: anchoring
the canvas's **bottom edge** where the old square's was lifted the feet by 1.06 px
and added 2 crossings. With the v2 pack the two rules are 0.23 px apart and it is
the feet rule that reaches higher — the gap between them was always the pack's
transparent tail, and the tail went from 4/96 of the canvas to 4/192.

### The canvas is not the creature

The v2 canvas is a frame shared by six poses and sized for the widest of them, so
it describes a creature much less well than the v1 crop did: `idle` fills 116 of
its 272 columns, and **every** state leaves the top 20 of 192 rows empty.
`CameraView.GoblinOpaqueRect` is the union of the six states' alpha bounds inside
that canvas — columns 26–268, rows 20–187 — and it is what Issue #156's sweep now
models a body with.

That distinction is load-bearing and it is a change of unit rather than a
relaxation, in two numbers. Vertically the highest pixel a creature can have is
**37.424242 px** above its render centre with the v1 pack and **37.424242 px**
with v2 — equal to the last binary place, because the body fills 0.875 of the
canvas in both. Horizontally it is 27.05 px each way with v1 against **42.82**
with v2, since `combat` and `windup` hold a spear out, so the sweep became 58 %
stricter sideways. Measured against the canvas instead, the same sweep reports 2
crossings at every tile size, all of them inside the empty header —
`evidence/77-pack-geometry.json` keeps both columns and the two probes that name
the cause: a body 42 % wider adds none of them, and the 1.29 px the canvas rose
adds all of them.

`combat → flinch` raises the head 3.2–3.5 px of a 47.97 px silhouette at tile 40
when the top is asked at ten columns or more; at five columns it is 4.51 px, and
the alpha bbox says 4.83 because the topmost row of `flinch` is a sliver — three
columns at the `alpha > 32` threshold this method uses, five if any non-zero alpha
counts. It was accepted as the recoil doing its job — registering `flinch` by its
head would hold the head still and move the feet — and the timing question belongs
to the feedback loop that will play it. `evidence/77-pack-head-jump.json`.

## Readability pass

`B` starts painting and `E` starts erasing the selected zone. A bright preview
follows the cursor, and dragging turns it into a rectangle with the count of
cells the command will carry. `Esc`, `I` or right-click puts the brush away —
during a drag they cancel the drag first, so a misdrag is not also a lost brush.
The held brush is the lit button in the second control strip.

The purple room is `Quarters`: it contains bunks and is visited only when a
creature has fatigue at least 50 and a bunk is free. Its empty early-economy
state is expected by the Prototype 1 contract, not a routing failure. The map
labels it as `QUARTERS • REST`, and selecting it repeats this condition.

During a raid, a teal outline around a body is crew and a red one is a raider;
the raider outline is the wider of the two, so the rarer thing is the louder
mark. The outline is derived from the sprite's own alpha, which is what keeps it
working when a race pack changes (`docs/design/SIDE_INDICATOR.md`). It replaced
the stroke rings of Issue #177: those were visible, but at 27 reference pixels a
ring is 98.18 px across against a 40 px cell, so nine bodies in a cluster turned
the map into overlapping arcs. HP bars appear
under both. Crew dots show working (green), fighting (amber), fled (pink), or
downed (gray); a white X is a downed body. The battle legend is in the side
panel and selected-creature inspection states `ALIVE`, `DOWNED`, or `FLED` with
HP. Crew and raider sprites are exploratory generated art with provenance in
[`goblin-v2-provenance.md`](../art/goblin-v2-provenance.md) — one pack for both
sides, with teal/red stroke rings telling them apart; they are not a commitment
to production art direction. The retired v1 sheet and its
[`goblin-v1-provenance.md`](../art/goblin-v1-provenance.md) stay in the
repository as the record of what the pack replaced.

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
`--frame-pacing` is the check that it never travels the other way. Issue #210
added two more of the same kind and no others: the blows of the moment being
drawn and the hit points of the previous tick they are measured against, both
derived from snapshots and both invisible to `PrototypeWorld`. It remains a
graybox: the wall topology is derived from the published rock set and creates no
new canonical fact or asset. Art assets, animation, production onboarding and
Ivan runtime integration are outside Prototype 1.
