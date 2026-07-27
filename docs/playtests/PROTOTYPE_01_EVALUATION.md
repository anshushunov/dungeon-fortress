# Prototype 1 — evaluation and owner playtest

Status: methodology frozen; automated evidence and owner feedback are pending.

Scope: Issue #12, pre-human decision gate only.

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
- methodology commit: pending
- evidence commit: pending

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

Automation may establish determinism, visible state changes and contract
coverage. Agent readability may establish that the graybox/state/log exposes
those facts. Neither can establish whether the game is clear, influential or
interesting to its owner. The only permitted conclusion before owner feedback is
**pending owner playtest**.

## Automated evidence

Pending the committed batch run.

## Agent readability pass

Pending the completed graybox/state/log inspection.

## Owner playtest (10–15 minutes)

Pending implementation of the final launch command and spoiler-light checklist.

## Owner feedback and decision gate

Pending owner playtest. Do not record `iterate`, `pivot` or `discard` until the
owner has answered the playtest questions.
