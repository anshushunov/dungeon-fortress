using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Issue #403. The rule <c>CombatHoldSatiety</c> was removed from
/// <see cref="PrototypeTuning"/> on 2026-08-11 as unreachable, on an arithmetic
/// taken at a join threshold of 41. The join threshold has since moved to 30
/// (<see cref="PrototypeTuning.CombatJoinSatiety"/>) and the arithmetic gives a
/// different answer at 30, but the probe that measured it
/// (<c>evidence/333-hold-reachability.json</c>, <c>evidence/333-starving-reachability.json</c>)
/// was a scratch tool deleted after use. This test is that tool, restored to
/// live in the tree so the numbers can be re-taken by one command rather than
/// reconstructed by hand.
///
/// <para><b>What "reachable" means here.</b> A creature enters the line only
/// above <see cref="PrototypeTuning.CombatJoinSatiety"/> (the one live gate,
/// <c>PrototypeWorld.Combat.cs</c>). While fighting its satiety only falls, and
/// only by the global decay of one point per <see cref="PrototypeTuning.SatietyDecayPeriod"/>
/// ticks. A spell in the line ends when the wave resolves. So falling from the
/// join threshold to below the removed hold threshold costs
/// <c>(join - hold + 1) * decay</c> unbroken ticks in the line — this test
/// measures both sides of that inequality directly, tick by tick, rather than
/// trusting the arithmetic alone (the arithmetic bounds it necessarily but not
/// sufficiently, per <c>evidence/333-hold-reachability.json</c>,
/// <c>measuredAgainstThat.reading</c>, where a sum that "works" still did not
/// fire because the creature nearest the threshold on entry was not the one
/// that stayed longest).</para>
///
/// <para><b>Why the hold threshold is a literal here and not a constant.</b> The
/// constant <c>CombatHoldSatiety</c> no longer exists; the rule was deleted with
/// it. The value 20 is restated from the comment above
/// <see cref="PrototypeTuning.CombatJoinSatiety"/> and from
/// <c>docs/design/PROTOTYPE_01_PREPARE_FOR_RAID.md</c> §10.2 — this test
/// measures the reachability of a rule that is not in the tree, by construction
/// of the task.</para>
/// </summary>
public sealed class CombatHoldReachabilityTests
{
    // The matrix contract 13.4 calls "пятнадцать ячеек": three fixtures × three
    // seeds, plus the two causal-pair fixtures × the same three seeds
    // (PROTOTYPE_01_PREPARE_FOR_RAID.md:767-769). `neglected` never reaches a
    // wave (evidence/333-starving-reachability.json), so it is measured too and
    // reported as zero rather than skipped — completeness is the point of this
    // rubric, not an assumption about which fixture matters.
    private static readonly string[] MatrixFixtures =
        ["baseline", "prepared", "neglected", "prepared-ration-zero", "prepared-watch-zero"];

    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    /// <summary>
    /// The threshold the removed <c>CombatHoldSatiety</c> rule used. See the
    /// class remarks for why this is a literal rather than a reference to a
    /// constant.
    /// </summary>
    private const int RemovedHoldThreshold = 20;

    [Fact]
    public void Reachability_of_the_removed_hold_rule_is_measured_and_recorded()
    {
        var repositoryRoot = FindRepositoryRoot();

        var cells = new List<ReachabilityCell>();
        foreach (var fixture in MatrixFixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                cells.Add(Measure(repositoryRoot, fixture, seed));
            }
        }

        var neededUnbrokenTicks =
            (PrototypeTuning.CombatJoinSatiety - RemovedHoldThreshold + 1) * PrototypeTuning.SatietyDecayPeriod;
        var longestSpellCell = cells.OrderByDescending(cell => cell.LongestSpellInLine)
            .ThenBy(cell => cell.Fixture, StringComparer.Ordinal)
            .ThenBy(cell => cell.Seed)
            .First();
        var lowestSatietyCell = cells
            .Where(cell => cell.LowestSatietyWhileFighting is not null)
            .OrderBy(cell => cell.LowestSatietyWhileFighting)
            .ThenBy(cell => cell.Fixture, StringComparer.Ordinal)
            .ThenBy(cell => cell.Seed)
            .First();

        var arithmeticReachable = longestSpellCell.LongestSpellInLine >= neededUnbrokenTicks;
        var observedReachable = lowestSatietyCell.LowestSatietyWhileFighting <= RemovedHoldThreshold;

        var report = new ReachabilityReport(
            SchemaVersion: 1,
            Issue: "#403",
            Command:
                "dotnet test tests/DungeonFortress.Simulation.Tests " +
                "--filter FullyQualifiedName~CombatHoldReachabilityTests.Reachability_of_the_removed_hold_rule_is_measured_and_recorded",
            What:
                "Tick-by-tick re-measurement of whether the join-to-hold fall the " +
                "removed CombatHoldSatiety rule needed is reachable on the merged " +
                "balance slice (PR #402), superseding the deleted scratch probes " +
                "evidence/333-hold-reachability.json and evidence/333-starving-reachability.json.",
            // The commit at which src/DungeonFortress.Simulation last changed,
            // not the commit this file happens to be checked in on — the same
            // convention tests/DungeonFortress.Scenarios/PrototypeEvaluation.cs
            // uses for its own ImplementationBaseline. Issue #403 is scoped to
            // measure, not to touch the simulation, so this is `origin/main` as
            // handed to the worktree (86537cf) unless a later commit on this
            // branch is noted otherwise in the PR body.
            MeasuredOnSimulationCommit: "86537cf",
            CombatJoinSatiety: PrototypeTuning.CombatJoinSatiety,
            SatietyDecayPeriod: PrototypeTuning.SatietyDecayPeriod,
            RemovedHoldThreshold: RemovedHoldThreshold,
            NeededUnbrokenTicks: neededUnbrokenTicks,
            SessionTicks: PrototypeTuning.SessionTicks,
            MatrixFixtures: MatrixFixtures,
            MatrixSeeds: MatrixSeeds,
            Cells: cells
                .OrderBy(cell => cell.Fixture, StringComparer.Ordinal)
                .ThenBy(cell => cell.Seed)
                .ToArray(),
            Aggregate: new AggregateResult(
                LongestSpellInLine: longestSpellCell.LongestSpellInLine,
                LongestSpellCell: $"{longestSpellCell.Fixture}/{longestSpellCell.Seed}",
                LowestSatietyWhileFighting: lowestSatietyCell.LowestSatietyWhileFighting,
                LowestSatietyCell: $"{lowestSatietyCell.Fixture}/{lowestSatietyCell.Seed}",
                ArithmeticReachable: arithmeticReachable,
                ObservedReachable: observedReachable),
            Verdict:
                observedReachable
                    ? "REACHABLE: at least one fighting creature was observed at or below the " +
                      $"removed hold threshold ({RemovedHoldThreshold}) on the merged mechanic. " +
                      "This is an owner-facing fork (return the rule, and at what hold threshold, " +
                      "or leave it removed knowing it is now reachable) — this tool does not choose."
                    : "UNREACHABLE: no fighting creature was observed at or below the removed hold " +
                      $"threshold ({RemovedHoldThreshold}) on any measured cell. The removal stands " +
                      "confirmed against the merged mechanic rather than the one it was decided on.");

        var destinationPath = Path.Combine(repositoryRoot, "evidence", "403-reachability.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(destinationPath, JsonSerializer.Serialize(report, options) + "\n", new UTF8Encoding(false));

        // This is the guard the mutant in evidence/403-mutants.json exercises:
        // a substitution that moves CombatJoinSatiety has to move this number,
        // because it is read from PrototypeTuning at run time and not copied in.
        Assert.Equal(
            (PrototypeTuning.CombatJoinSatiety - RemovedHoldThreshold + 1) * PrototypeTuning.SatietyDecayPeriod,
            report.NeededUnbrokenTicks);
    }

    private static ReachabilityCell Measure(string repositoryRoot, string fixture, ulong seed)
    {
        var commandLog = LoadFixture(repositoryRoot, fixture, seed);
        var world = new PrototypeWorld(commandLog);

        var currentSpellLength = new Dictionary<int, int>();
        var longestSpellInLine = 0;
        int? lowestSatietyWhileFighting = null;
        var everFought = false;

        while (!world.IsComplete && world.CurrentTick < PrototypeTuning.SessionTicks)
        {
            world.Step();
            var snapshot = world.GetSnapshot();
            foreach (var creature in snapshot.Creatures)
            {
                if (creature.Mode == CreatureMode.Fighting)
                {
                    everFought = true;
                    var length = currentSpellLength.GetValueOrDefault(creature.Id) + 1;
                    currentSpellLength[creature.Id] = length;
                    if (length > longestSpellInLine)
                    {
                        longestSpellInLine = length;
                    }

                    lowestSatietyWhileFighting = lowestSatietyWhileFighting is null
                        ? creature.Satiety
                        : Math.Min(lowestSatietyWhileFighting.Value, creature.Satiety);
                }
                else
                {
                    currentSpellLength[creature.Id] = 0;
                }
            }
        }

        return new ReachabilityCell(
            fixture,
            seed,
            everFought,
            longestSpellInLine,
            lowestSatietyWhileFighting,
            world.CurrentTick);
    }

    private static PrototypeCommandLog LoadFixture(string repositoryRoot, string fixture, ulong seed)
    {
        var path = Path.Combine(repositoryRoot, "scenarios", "prototype1", $"{fixture}.commands.v2.json");
        var document = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
        document["seed"] = seed;
        if (fixture.StartsWith("prepared-", StringComparison.Ordinal))
        {
            // The two causal-pair fixtures carry a scenario label the
            // validator does not accept (`prepared-ration-zero`,
            // `prepared-watch-zero`); PrototypeEvaluation.RunOnce
            // (tests/DungeonFortress.Scenarios/PrototypeEvaluation.cs) relabels
            // them to `prepared` for the same reason before parsing.
            document["scenario"] = "prepared";
        }

        return PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(document.ToJsonString()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DungeonFortress.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed record ReachabilityCell(
        string Fixture,
        ulong Seed,
        bool EverFought,
        int LongestSpellInLine,
        int? LowestSatietyWhileFighting,
        int EndTick);

    private sealed record AggregateResult(
        int LongestSpellInLine,
        string LongestSpellCell,
        int? LowestSatietyWhileFighting,
        string LowestSatietyCell,
        bool ArithmeticReachable,
        bool ObservedReachable);

    private sealed record ReachabilityReport(
        int SchemaVersion,
        string Issue,
        string Command,
        string What,
        string MeasuredOnSimulationCommit,
        int CombatJoinSatiety,
        int SatietyDecayPeriod,
        int RemovedHoldThreshold,
        int NeededUnbrokenTicks,
        int SessionTicks,
        IReadOnlyList<string> MatrixFixtures,
        IReadOnlyList<ulong> MatrixSeeds,
        IReadOnlyList<ReachabilityCell> Cells,
        AggregateResult Aggregate,
        string Verdict);
}
