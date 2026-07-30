using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Panic has to read as one creature's decision rather than as herd behaviour,
/// and the only way to tell the two apart is to look at *when* each defender
/// broke. These tests measure that distribution instead of describing it.
/// </summary>
public sealed class PrototypeMoraleTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];
    private static readonly string[] Fixtures = ["baseline", "prepared"];

    [Fact]
    public void Report_the_distribution_of_flight_ticks_over_the_seed_matrix()
    {
        var report = new StringBuilder();
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                var state = RunAtSeed(fixtureName, seed);
                foreach (var cohort in Cohorts(state))
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"{fixtureName}/{seed} {cohort}");
                }

                report.AppendLine(CultureInfo.InvariantCulture,
                    $"{fixtureName}/{seed} total fled={state.SessionResult.DefendersFled} " +
                    $"ticks={Cohorts(state).Count} largestCohort={LargestCohort(state)}");
            }
        }

        output.WriteLine(report.ToString());
    }

    private static IReadOnlyList<string> Cohorts(PrototypeSnapshot state) =>
        state.Events
            .Where(@event => @event.ReasonCode == "combat_fled_morale")
            .GroupBy(@event => @event.FirstTick)
            .OrderBy(group => group.Key)
            .Select(group => $"tick {group.Key}: {group.Count()} ({string.Join(",", group.Select(item => item.CreatureId).OrderBy(id => id))})")
            .ToArray();

    private static int LargestCohort(PrototypeSnapshot state)
    {
        var groups = state.Events
            .Where(@event => @event.ReasonCode == "combat_fled_morale")
            .GroupBy(@event => @event.FirstTick)
            .ToArray();
        return groups.Length == 0 ? 0 : groups.Max(group => group.Count());
    }

    private static PrototypeSnapshot RunAtSeed(string fixtureName, ulong seed) =>
        PrototypeScenario.Run(
            PrototypeCommandDocument.Load(FixturePath(fixtureName)) with { Seed = seed },
            PrototypeTuning.SessionTicks).State;

    private static string FixturePath(string fixtureName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "scenarios")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory!.FullName,
            "scenarios",
            "prototype1",
            $"{fixtureName}.commands.v2.json");
    }
}
