# #403 — handoff / blocker note

Not a context-exhaustion stop. Recorded here anyway because a checkpoint
(`Рубежи`) is being closed with a known gap, and the rule requires a written
record either way.

## Where this stands

- Checkpoint 1 (tool exists, runs, gives a number) — done, commit `ce0f7a7`.
- Checkpoint 2 (matrix measured, `evidence/403-reachability.json` committed
  with commands and the commit the numbers were taken on) — done, commit
  `6a2e41c`.
- Checkpoint 3 (mutant) — **blocked**, not done. See `evidence/403-mutants.json`
  for the full recipe, prediction, and why it could not be run: the only lever
  is `PrototypeTuning.CombatJoinSatiety`, a `const int` inside
  `src/DungeonFortress.Simulation/**`, which the dispatch brief for #403 marks
  as held by Issue #418 (`claimed`, still open) and forbidden by this task's
  own non-goals independently of the partition table. An attempted transient,
  uncommitted, git-reverted edit was refused by the harness's auto-mode
  permission classifier on exactly that ground. No workaround tool was used —
  the classifier's denial explicitly asked not to be routed around, and the
  task's own rule 36 says to stop and escalate in the Issue rather than force
  it.
- Checkpoint 4 (both outcomes stated with numbers) — done in the PR body: the
  measured outcome is **UNREACHABLE** (observed) at commit `86537cf`, with the
  arithmetic bound already crossed (56 ≥ 55) but the empirical satiety floor
  short by 4 (24 vs. 20). Both what "reachable" and "unreachable" would mean
  are stated; no choice between them is made here.
- Checkpoint 5 (full `verify.ps1` green once on the final state) — not run
  as a final gate yet, because criterion 3 is unmet and "final state" has not
  been reached. `dotnet build` and the new test alone are green (see PR body).

## What the next agent (or the coordinator) needs to do

1. Decide how to resolve the partition conflict on
   `src/DungeonFortress.Simulation/PrototypeTuning.cs`: grant a scoped
   exception to run the recipe in `evidence/403-mutants.json` once, wait for
   #418 to land or release the file, or accept the PR without the mutant and
   track it as a follow-up.
2. If the recipe is run: follow the exact commit-then-mutate-then-revert order
   in `evidence/403-mutants.json` (`theRecipe.commitOrder`) — do **not** run
   the reachability test with `--no-build` after reverting the mutant, or the
   binaries will still carry it while the source looks clean (the failure mode
   named as Issue #147 in the dispatch brief).
3. After the mutant result is in, re-run
   `dotnet test tests/DungeonFortress.Simulation.Tests --filter FullyQualifiedName~CombatHoldReachabilityTests`
   once more on the clean, reverted, rebuilt tree to confirm
   `evidence/403-reachability.json` is unchanged from commit `6a2e41c` (i.e.
   the revert left no residue), then run the full `verify.ps1` once for
   checkpoint 5 and close out the PR.

## Numbers already measured (commit `86537cf`, full command in
`evidence/403-reachability.json.Command`)

- Needed unbroken ticks for the removed hold rule to fire at JOIN=30, removed
  HOLD=20, decay=5: `(30-20+1)*5 = 55`.
- Longest observed continuous spell in the line across all 15 matrix cells:
  **56** ticks (`prepared/20260726`) — arithmetically reachable.
- Lowest satiety ever observed on a fighting creature across all 15 cells:
  **24** (`prepared/20260726` and `prepared-ration-zero/20260726`) — never at
  or below the removed hold threshold of 20.
- Verdict on this measurement: **UNREACHABLE (observed)**, closer than the
  numbers the Issue was opened on (69/55 and 21/20 on the pre-#409 mechanic)
  but not crossed.
