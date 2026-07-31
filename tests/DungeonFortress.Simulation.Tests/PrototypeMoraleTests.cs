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
///
/// The owner's report behind Issue #101 was two sentences, and they are two
/// different findings, so they are two different tests here:
///
/// - the moment of breaking is personal, which is a statement about the
///   distribution of `combat_fled_morale` over ticks;
/// - a broken defender leaves on foot, which is a statement about position and
///   is asserted for the whole party by
///   <c>Traffic_arbitration_preserves_one_move_no_overlap_and_no_swap</c>. What
///   is asserted here is the half that test cannot see: that the flight is a
///   walk of several tiles rather than a creature that never left.
/// </summary>
public sealed class PrototypeMoraleTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    /// <summary>
    /// The two fixtures that actually meet waves. `neglected` dies of hunger
    /// before the first one arrives, so it has no morale to measure.
    /// </summary>
    public static TheoryData<string, ulong> Matrix()
    {
        var data = new TheoryData<string, ulong>();
        foreach (var fixtureName in new[] { "baseline", "prepared" })
        {
            foreach (var seed in MatrixSeeds)
            {
                data.Add(fixtureName, seed);
            }
        }

        return data;
    }

    /// <summary>
    /// No single tick may take more than a third of the domain. The bound is
    /// written against the size of the domain rather than as a literal 3 so that
    /// it keeps meaning the same thing if the roster changes.
    ///
    /// Chosen from the measurement, not from taste. On `origin/main` the largest
    /// cohort over the matrix was 6, 6 and 5 of nine on `baseline` and 2, 4 and 3
    /// on `prepared` — that is the herd the owner saw, and it fails this bound
    /// twice over. With the personal check it is 2, 3, 2 and 1, 2, 1. Three is
    /// therefore both the honest edge of what a spread distribution produces and
    /// the point past which "a third of everyone at once" starts to read as one
    /// event rather than as several decisions.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void No_single_tick_breaks_more_than_a_third_of_the_domain(string fixtureName, ulong seed)
    {
        var state = RunAtSeed(fixtureName, seed);
        var flights = Flights(state);

        Assert.True(
            LargestCohort(flights) * 3 <= state.Creatures.Count,
            $"{fixtureName}/{seed}: {LargestCohort(flights)} of {state.Creatures.Count} " +
            $"broke on one tick. {Describe(flights)}");
    }

    /// <summary>
    /// The same claim read the other way round, because a maximum can be met by
    /// a distribution that is still a herd: three cohorts of three are within the
    /// bound above and are not what "each of them decided" looks like.
    ///
    /// The average cohort separates the two shapes cleanly and with room to
    /// spare. On `origin/main` it was 4.00, 3.00, 2.83 on `baseline` and 1.57,
    /// 2.20, 1.57 on `prepared` — every cell of the matrix above 1.5. With the
    /// personal check it is 1.00, 1.13, 1.04 and 1.00, 1.05, 1.00 — every cell
    /// below 1.15. The bound is set at 1.5: below the cheapest thing the old
    /// shape ever produced, and a quarter clear of the dearest the new one does.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void The_average_tick_of_flight_takes_one_creature_rather_than_a_group(
        string fixtureName,
        ulong seed)
    {
        var state = RunAtSeed(fixtureName, seed);
        var flights = Flights(state);
        var ticks = DistinctTicks(flights);

        // A party in which almost nobody breaks would satisfy every bound above
        // by not exercising the rule at all, so the sample itself is asserted.
        // The matrix produces 15 to 26 flights a party; five is a floor, not a
        // measurement.
        Assert.True(
            flights.Length >= 5,
            $"{fixtureName}/{seed}: only {flights.Length} defenders broke all party, " +
            "which is too few to say anything about how the moment is spread.");

        Assert.True(
            flights.Length * 2 <= ticks * 3,
            $"{fixtureName}/{seed}: {flights.Length} flights over {ticks} ticks is " +
            $"{(double)flights.Length / ticks:F2} creatures a tick. {Describe(flights)}");
    }

    /// <summary>
    /// The second finding of #101: the position used to be assigned outright, so
    /// a broken defender crossed half the map inside one tick and the
    /// presentation layer had a jump to interpolate. Flight is now ordinary
    /// movement, which means it takes ticks and can be watched.
    ///
    /// What is asserted is the walk itself — several ticks in flight, several
    /// tiles apart, one tile at a time. That no creature ever moves more than a
    /// tile, for the whole party and not only for a runner, is
    /// <c>Traffic_arbitration_preserves_one_move_no_overlap_and_no_swap</c>.
    /// </summary>
    [Fact]
    public void A_broken_defender_walks_out_of_the_fight_rather_than_arriving()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        var previous = world.GetSnapshot();
        var runs = new Dictionary<int, List<GridPoint>>();
        var longest = 0;

        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            foreach (var creature in current.Creatures)
            {
                var before = previous.Creatures.Single(other => other.Id == creature.Id);
                if (creature.Mode != CreatureMode.Fled)
                {
                    runs.Remove(creature.Id);
                    continue;
                }

                if (!runs.TryGetValue(creature.Id, out var trail))
                {
                    // The tile the creature broke on: the run starts where it was
                    // standing, which is the whole point.
                    trail = [before.Position];
                    runs.Add(creature.Id, trail);
                }

                Assert.InRange(Manhattan(before.Position, creature.Position), 0, 1);
                if (creature.Position != trail[^1])
                {
                    trail.Add(creature.Position);
                }

                longest = Math.Max(longest, Manhattan(trail[0], creature.Position));
            }

            previous = current;
        }

        Assert.True(
            longest >= 3,
            $"the furthest any broken defender got from the tile it broke on was {longest} " +
            "tiles. Flight is supposed to be a walk that the domain can watch, so a run " +
            "that never leaves the spot means the mode is set and nothing follows.");
    }

    /// <summary>
    /// The distribution itself, printed rather than asserted. The bounds above
    /// say whether the shape is right; this says what the shape is, which is what
    /// the next person to move a morale weight actually needs.
    /// </summary>
    [Fact]
    public void Report_the_distribution_of_flight_ticks_over_the_seed_matrix()
    {
        var report = new StringBuilder();
        foreach (var fixtureName in new[] { "baseline", "prepared" })
        {
            foreach (var seed in MatrixSeeds)
            {
                var state = RunAtSeed(fixtureName, seed);
                var flights = Flights(state);
                var summary = state.SessionResult;
                report.AppendLine(CultureInfo.InvariantCulture, $"{fixtureName}/{seed} {Describe(flights)}");
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"{fixtureName}/{seed} outcome={summary.Outcome} score={summary.Score} " +
                    $"repelled={summary.WavesRepelled}/{summary.WavesResolved} " +
                    $"defendersDowned={summary.DefendersDowned} " +
                    $"raidersDowned={summary.RaidersDowned} " +
                    $"mealsStolen={summary.MealsStolen} " +
                    $"waves={string.Join("|", state.Waves.Select(wave => wave.Outcome ?? "unresolved"))}");
            }
        }

        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// One entry per defender that broke, in the tick it broke on. The canonical
    /// log folds a repeated decision into one event with a count, so the count is
    /// read rather than the entries: a flight that repeated would otherwise be
    /// invisible to every bound here.
    /// </summary>
    private static (int Tick, int CreatureId)[] Flights(PrototypeSnapshot state) =>
        state.Events
            .Where(@event => @event.ReasonCode == "combat_fled_morale")
            .SelectMany(@event => Enumerable
                .Range(0, @event.Repeats)
                .Select(_ => (@event.FirstTick, @event.CreatureId)))
            .OrderBy(flight => flight.Item1)
            .ThenBy(flight => flight.Item2)
            .ToArray();

    private static int DistinctTicks((int Tick, int CreatureId)[] flights) =>
        flights.Select(flight => flight.Tick).Distinct().Count();

    private static int LargestCohort((int Tick, int CreatureId)[] flights) =>
        flights.Length == 0
            ? 0
            : flights.GroupBy(flight => flight.Tick).Max(cohort => cohort.Count());

    private static string Describe((int Tick, int CreatureId)[] flights) =>
        $"flights={flights.Length} ticks={DistinctTicks(flights)} " +
        $"largestCohort={LargestCohort(flights)} [" +
        string.Join(
            "; ",
            flights
                .GroupBy(flight => flight.Tick)
                .OrderBy(cohort => cohort.Key)
                .Select(cohort =>
                    $"{cohort.Key}: {string.Join(",", cohort.Select(flight => flight.CreatureId).Order())}")) +
        "]";

    private static int Manhattan(GridPoint left, GridPoint right) =>
        Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private static PrototypeSnapshot RunAtSeed(string fixtureName, ulong seed) =>
        PrototypeScenario.Run(
            LoadFixture(fixtureName) with { Seed = seed },
            PrototypeTuning.SessionTicks).State;

    private static PrototypeCommandLog LoadFixture(string fixtureName) =>
        PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(), "scenarios", "prototype1", $"{fixtureName}.commands.v2.json"));

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
}
