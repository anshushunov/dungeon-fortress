# Prototype 1 — evaluation and owner playtest

Status: complete — owner decision: **ITERATE**.

Scope: Issue #12, reproducible evidence and owner decision gate.

## Provenance and sequence

This document is intentionally committed before a new evaluation batch is run.
The methodology commit SHA is recorded below after commit creation. Measured
values are appended only in the later evidence commit, so a result cannot choose
its own hypothesis or threshold retroactively.

- implementation baseline: `20e31e1` (`Prototype 1: add deterministic raid combat (#17)`)
- gameplay input: schema-v2 documents only; no direct creature, job, or target
  commands (ADR 0005)
- evaluator: deterministic headless `DungeonFortress.Scenarios` runner
- planned evidence output: `docs/playtests/data/prototype-01-agent-batch.json`
- methodology commit: `48491da`
- evidence commit: `f9e37d4`

## Questions and pre-registered hypotheses

The sample tests only the Prototype 1 promise: indirect intent changes the
economy and then an automatic raid. It does not test long-term interest,
relations, memory, art direction, or an optimal balance.

| ID | Hypothesis | Observable support threshold | Failure / insufficient evidence |
|---|---|---|---|
| H1 | Prepared intent changes autonomous preparation. | In at least 2 of 3 seeds, `prepared` has higher raid readiness than `baseline`; median delta is at least 5. | Otherwise contradicted; a tie on all seeds is insufficient for a positive claim. |
| H2 | Preparation changes the raid result through visible state, not a hidden flag. | `prepared` has no worse defender loss than `neglected` in at least 2 of 3 seeds, and its readiness, combat outcome, and meals/stolen-meals metrics are all reported from snapshots. | A worse result on 2+ seeds contradicts the directional claim. Any missing snapshot field is insufficient. |
| H3 | The economy remains legible while preparation occurs. | Every baseline/prepared/neglected run has nonzero harvest, raw-haul, cook and meal-haul completions; labor categories and reason-code coverage are present. | A missing full-chain counter or unreadable reason coverage contradicts this narrow observability claim. |
| H4 | Named creatures provide at least one inspectable individual consequence. | Every completed run exposes the nine stable names plus per-creature mode, health/injury and last reason; agent inspection identifies one concrete event chain without inferring unrecorded motives. | This is only an observability result. It is insufficient evidence of memorable human stories or social simulation (ADR 0006). |

## Fixed evaluation matrix

All rows execute the full 1800 ticks twice. Seeds are intentionally small and
fixed: `20260726`, `20260727`, `20260728`. Each scenario's command log is
identical across seeds except for its root `seed` value. No gameplay tuning,
hidden scenario value, or runtime speed is changed by the evaluator.

| Scenario | Purpose |
|---|---|
| `baseline` | Default zones, priorities and rules; no player commands. |
| `prepared` | The supplied multi-lever preparation plan. |
| `neglected` | The supplied over-drilling / food-neglect plan. |

### Causal pairs

These are separate from the matrix and differ in exactly one allowed v2 intent
field while retaining the same seed, ticks, map and every other command. They
use no entity address, job address or combat target.

| Pair | Control | Treatment | Single changed player intent | Expected measured contrast |
|---|---|---|---|---|
| CP1 | `prepared` | `prepared-ration-zero` | `ration_reserve`: `6` → `0` at tick 320 | Raid readiness, meals/stolen meals, defender losses/outcome. |
| CP2 | `prepared` | `prepared-watch-zero` | `Watch` priority: `3` → `0` at tick 900 | Watch labor/post occupancy, raid readiness, defender losses/outcome. |

The pair tests are diagnostic causal evidence, not a claim that either value is
optimal. A pair is reported as `different`, `same`, or `inconclusive` from its
recorded observable deltas; no causal result is inferred from an unrecorded
engine flag.

## Metrics recorded from snapshots

- economy: harvests, raw hauls, cooks, meal hauls, meals produced/eaten/current;
- labor: food/rest/eat/drill/watch/muster/idle ticks and post occupancy;
- creatures: average satiety, fatigue, martial form, readiness-at-raid, modes,
  health, injuries, downed/fled counts;
- combat/session: outcome, end tick, raiders downed, meals stolen/left;
- explainability: distinct reason codes, event count and per-code occurrence;
- reproducibility: canonical checksum for both repetitions and their equality.

## Interpretation boundary

Automation establishes determinism, visible state changes and contract coverage.
Agent readability establishes that the graybox/state/log exposes those facts.
Only the owner can judge clarity, influence and desire to continue; that feedback
is recorded separately below.

## Automated evidence

Run from the implementation baseline with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\evaluate-prototype.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\evaluate-prototype.ps1 -Verify
```

The committed raw compact output is
[`data/prototype-01-agent-batch.json`](data/prototype-01-agent-batch.json). It
contains 15 scenario/seed rows, a byte-for-byte repeated checksum for each row,
and six causal-pair rows. The script is a single in-process batch over the
existing headless runner; it changes only the v2 document root seed per seed.

| Hypothesis | Result | Evidence |
|---|---|---|
| H1 | **supported** | Prepared readiness is `56/52/56` vs baseline `39/39/40`: +17, +13, +16; all three seeds clear the +5 threshold (median +16). |
| H2 | **contradicted by its pre-registered loss threshold** | Prepared repels all three raids (`repelled_costly`), neglected is overrun in all three; however neglected records zero defender downed/fled because its defenders do not engage. Prepared therefore has numerically more losses, so the stated "no worse defender loss" threshold is not met. This is a metric/feedback ambiguity, not evidence that prepared play is worse. |
| H3 | **contradicted** | Baseline and prepared complete the full food chain in every seed; neglected intentionally has zero harvest/raw-haul/cook/meal-haul after its Harvest priority is set to zero. Reason-code coverage remains present (`13` distinct codes in neglected, `21–22` baseline, `27–28` prepared). |
| H4 | **supported as observability only** | Every row exposes the same nine names and per-creature mode, health/injury, readiness and last reason. This does not establish a memorable human story or social-memory evidence. |

The raw output also records throughput, meals, labor allocation, station use,
creature state, session outcome, losses and reason-code occurrences; no hidden
outcome flag is used to derive the table.

### Causal-pair facts

- **CP1 — ration reserve 6 → 0:** the canonical checksum changes for all three
  seeds. Observable deltas are unchanged in seed `20260726`, while seeds
  `20260727`/`20260728` change readiness by `+7`/`+2` and change creature-loss
  or food state. Classification: **different in 2/3, same observed metrics in
  1/3**.
- **CP2 — Watch priority 3 → 0:** Watch labor falls by `862`, `850`, `813`
  ticks in the three seeds; other recorded deltas differ by seed. Classification:
  **different in 3/3**. This verifies the intended indirect lever, not an
  optimum priority.

### Classified observations — no follow-up created

| Type / severity | Evidence | Next action |
|---|---|---|
| Missing feedback / metric ambiguity, P2 | H2's defender-loss metric reads "better" for an overrun because the neglected defenders do not engage. The outcome itself is visible, but the comparison needs owner interpretation. | Hold for owner decision; do not tune or add a system in this block. |
| Human-readability risk, P2 | The graybox presents command controls, timeline, event log, selected creature reason and raid summary simultaneously. Agent inspection can read it; the owner later reported collisions and unclear interactions. | Chosen next experiment: interaction/combat readability; no fix in Issue #12. |

No run was blocked: all 15 sessions reached an outcome, every repeated checksum
matched, and no gameplay defect prevented a completed session.

## Agent readability pass

Completed with the prepared graybox at tick `1540` (uncommitted evidence:
`.artifacts/visual/prototype-01-evaluation-raid.png`, checksum
`57ca…b3ae2`). The visual pass verified that one screen exposes:

- the phase/countdown, `raid repelled_costly`, defenders `6+42`, meals `0+8`,
  jobs and checksum in the HUD;
- four visible raiders, named creature markers and a selected `Смола` inspector
  with satiety, fatigue, martial form, readiness, mode, injury and health;
- a concrete causal reason: `combat_downed`, `raiderId=2`, `damage=1`, plus the
  recent event log (`combat_raider_downed`, `waiting_input_missing` and traffic
  feedback);
- the v2-only control summary and accepted log entries, without a direct
  creature command.

This is an agent readability observation, not a substitute for the owner’s
clarity or enjoyment judgement.

## Owner playtest (10–15 minutes)

From a clean checkout with the documented Godot .NET installation, open the
actual interactive graybox:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 -Fixture baseline
```

Spoiler-light checklist (do not read the command fixtures or source first):

1. Resume the simulation and spend the opening moment reading the map, threat
   countdown and one creature inspector.
2. Before the raid, make at least two changes using any combination of a zone,
   a global work priority and a rule. Do not try to select a creature as a unit.
3. Watch for one visible autonomous response; inspect it if you want to know
   why it happened.
4. Use time speed only to skip quiet waiting, then watch the raid and its final
   summary. No manual combat orders are expected.
5. Stop after the outcome screen; a single ordinary pass should fit 10–15
   minutes. Do not look for an optimal solution.

Answer in a few sentences or ratings:

1. What did you think the available levers were, and was that clear?
2. Did your changes feel like they influenced what happened? Why or why not?
3. What, if anything, was the first small story you noticed about a creature?
4. What was most irritating, confusing or slow?
5. Would you want one more run to try something different? What would you try?

## Owner feedback — separate from automation and agent observations

The owner completed the playtest and reported the following, preserved without
turning it into an automated claim:

| Playtest question | Owner feedback |
|---|---|
| Clarity of levers | Building/painting was unclear. After starting to build, the cursor appeared unable to move anywhere. At approximately a 2500×1400 view, labels and HUD text overlapped badly. |
| Perceived influence | Not confidently perceived. |
| Creature story | No creature story reported. The purple room was never visited, so its purpose and feedback were unclear. |
| Irritation / confusion | Text collisions; unclear building/painting; combat process not understandable; no clear alive-versus-downed indicator. |
| Replay desire | Yes — specifically to continue with a visual/readability iteration and a small generated goblin-sprite pass. |

Human-interest result: **supported only as willingness to iterate**. This is not
evidence of broad fun, replayability after multiple sessions, a validated art
direction, or a successful social simulation.

## Owner decision gate

Decision: **ITERATE**.

Smallest next experiment: **interaction/combat readability plus a first goblin
sprite pass**. It should make painting/building feedback, zone purpose, combat
sequence and alive/downed state readable at the owner’s target viewport, then
test whether that makes influence perceptible. The goblin sprites are a small
generated visual probe, not a production-art commitment.

No UI, gameplay, generated asset or tuning change is implemented in Issue #12.
The next experiment must retain provenance for any generated sprites and keep
the current deterministic headless checks intact.
