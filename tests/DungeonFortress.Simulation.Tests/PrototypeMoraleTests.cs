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
    /// twice over. With the personal check it is 1, 3, 2 and 1, 2, 2.
    ///
    /// One cell of six therefore sits exactly on the bound, with no slack, and
    /// that was raised in review as a reason to move it. It is deliberately not
    /// moved. Three of nine is not a percentile of the current build that wants
    /// a safety margin — it is the definition the bound exists to state, and a
    /// tick that takes four of nine is the thing the owner complained about
    /// whether or not the build that produced it was well tuned. The failure
    /// mode this guards against sits at 5 and 6, nowhere near the line. If a
    /// tuning change reddens this, the answer is to look at the distribution the
    /// report below prints, not to raise the number.
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
    /// personal check it is 1.00, 1.16, 1.04 and 1.00, 1.05, 1.15 — every cell
    /// below 1.2. The bound is set at 1.5: below the cheapest thing the old
    /// shape ever produced, and a fifth clear of the dearest the new one does.
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
    /// The first version of this test asserted that *some* runner in the party
    /// got at least three tiles from where it broke, and review was right that
    /// this proves nothing: the actual maximum is 26, so the bound was met by a
    /// margin that hid the thing worth measuring. Eleven flights out of
    /// fifty-five on `prepared` moved the creature no tile at all, and the test
    /// was green through all of them. What matters is not the best run of the
    /// party but the share of runs that are runs.
    ///
    /// Measured over the matrix, per cell: 23/25, 22/22, 23/24 on `baseline` and
    /// 12/13, 20/21, 10/15 on `prepared` — 110 of 120, and one cell at 67 %
    /// because of an escalated cause recorded in contract 10.3. The bounds below
    /// are set under those and above what would read as "flight is a pose": half
    /// per cell, 85 % over the matrix.
    ///
    /// What this bound does **not** do is worth stating, because the alternative
    /// is for somebody to assume it does. It does not separate this build from
    /// the one review measured: that build's worst cell was 73 % against this
    /// one's 67 %, and its total was 112 of 127 against 110 of 120. Traffic
    /// arbitration moved the shape of the standing rather than removing it, and
    /// the cause of what is left is not something a bound can express — it is
    /// recorded, with numbers, in contract 10.3. This test is here to stop the
    /// share getting worse, not to certify that it is good.
    ///
    /// That no creature ever moves more than a tile, for the whole party and not
    /// only for a runner, is
    /// <c>Traffic_arbitration_preserves_one_move_no_overlap_and_no_swap</c>.
    /// </summary>
    [Fact]
    public void Most_broken_defenders_actually_leave_the_tile_they_broke_on()
    {
        var walked = 0;
        var total = 0;
        var report = new StringBuilder();

        foreach (var (fixtureName, seed) in Cells())
        {
            var runs = FlightRuns(fixtureName, seed);
            var moved = runs.Count(run => run.Distance > 0);
            walked += moved;
            total += runs.Count;
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{fixtureName}/{seed}: {moved} of {runs.Count} flights moved the creature, " +
                $"furthest {(runs.Count == 0 ? 0 : runs.Max(run => run.Distance))} tiles");

            Assert.True(
                moved * 2 >= runs.Count,
                $"{fixtureName}/{seed}: only {moved} of {runs.Count} broken defenders left the " +
                "tile they broke on. A creature that announces panic and then stands in the " +
                $"middle of a fight reads as broken rather than as frightened.\n{report}");
        }

        Assert.True(
            walked * 100 >= total * 85,
            $"over the matrix only {walked} of {total} flights moved the creature.\n{report}");
    }

    /// <summary>
    /// A runner that stands still is not automatically a defect — a corridor can
    /// be full — but a runner that stands still for a reason the domain cannot
    /// state is. This is the assertion that survives tuning: whatever the traffic
    /// does, every tick a broken defender spends without moving, short of its
    /// refuge, is a tick the canonical log explains.
    ///
    /// It is also the one that would have caught the teleport had it been written
    /// first, and the one that catches the opposite failure — a mode that is set
    /// and followed by nothing at all.
    ///
    /// What it does **not** catch is a runner that was told to yield and walked
    /// its own way instead: <c>Move</c> writes <c>waiting_blocked_by_other</c>
    /// after the yield was recorded, so the last word of the tick is an accepted
    /// explanation either way. That claim used to be made here and was false.
    /// It is made, and proved by mutation, in
    /// <see cref="A_runner_told_to_yield_goes_to_the_booked_tile_or_nowhere"/>.
    /// </summary>
    [Fact]
    public void Every_tick_a_runner_stands_still_is_explained_in_the_log()
    {
        foreach (var (fixtureName, seed) in Cells())
        {
            var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
            var previous = world.GetSnapshot();

            while (!world.IsComplete)
            {
                world.Step();
                var current = world.GetSnapshot();
                foreach (var creature in current.Creatures)
                {
                    var before = previous.Creatures.Single(other => other.Id == creature.Id);
                    Assert.InRange(Manhattan(before.Position, creature.Position), 0, 1);
                    if (creature.Mode != CreatureMode.Fled ||
                        creature.Position != before.Position ||
                        creature.Position.X <= RefugeColumn)
                    {
                        continue;
                    }

                    var decision = creature.LastDecision;
                    Assert.True(
                        decision is not null &&
                        decision.Tick >= current.Tick - 1 &&
                        decision.ReasonCode is "waiting_blocked_by_other"
                            or "refused_zone_unreachable",
                        $"{fixtureName}/{seed}: creature {creature.Id} was in flight at " +
                        $"({creature.Position.X},{creature.Position.Y}) on tick {current.Tick - 1}, " +
                        $"did not move, and the last thing it said was " +
                        $"'{decision?.ReasonCode ?? "nothing"}' on tick {decision?.Tick ?? -1}. " +
                        "Standing is allowed; standing unexplained is not.");
                }

                previous = current;
            }
        }
    }

    /// <summary>
    /// The whole of what the traffic half of Issue #101 changed, stated so that
    /// removing the change fails.
    ///
    /// A creature that traffic arbitration picks as a yielder is given one tile
    /// to step onto, that tile is booked for the tick in
    /// <c>_yieldReservations</c>, and <c>chosen_traffic_yield</c> goes into the
    /// canonical log naming it. A broken defender used to take all three and then
    /// walk towards its refuge instead, because the <c>Fled</c> branch never read
    /// <c>TrafficTarget</c>: the log claimed a yield that did not happen and the
    /// booked tile was closed to everybody else for nothing.
    ///
    /// So the assertion is about where the creature actually is at the end of the
    /// tick. Having been booked onto tile T it may be **on T** — it yielded — or
    /// **where it started** — the step was blocked by somebody the chain had not
    /// cleared yet. A third tile means it went its own way, which is exactly the
    /// removed defect and nothing else: the refuge lies in the other direction
    /// from the yield by construction, or there would have been no need to yield.
    ///
    /// The booking is read from the event log rather than from
    /// <c>lastDecision</c>, because the decision is overwritten later in the same
    /// tick by whatever the movement routine says. That overwrite is what made
    /// the previous attempt at this test unable to fail.
    /// </summary>
    [Fact]
    public void A_runner_told_to_yield_goes_to_the_booked_tile_or_nowhere()
    {
        var observed = 0;
        var yieldedFor = 0;

        foreach (var (fixtureName, seed) in Cells())
        {
            var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
            var previous = world.GetSnapshot();

            while (!world.IsComplete)
            {
                world.Step();
                var current = world.GetSnapshot();
                var acted = current.Tick - 1;

                foreach (var @event in current.Events)
                {
                    if (@event.ReasonCode != "chosen_traffic_yield" || @event.LastTick != acted)
                    {
                        continue;
                    }

                    // The other half of the change, read from the same event: a
                    // yield names who it was made for, and a runner can only be
                    // named there if it published a destination and took part in
                    // the arbitration as a mover.
                    var beneficiary = @event.Details["beneficiaryId"];
                    if (previous.Creatures.Single(item => item.Id == beneficiary).Mode
                        == CreatureMode.Fled)
                    {
                        yieldedFor++;
                    }

                    var creature = current.Creatures.Single(item => item.Id == @event.CreatureId);
                    var before = previous.Creatures.Single(item => item.Id == @event.CreatureId);
                    if (before.Mode != CreatureMode.Fled && creature.Mode != CreatureMode.Fled)
                    {
                        continue;
                    }

                    var booked = new GridPoint(@event.Details["targetX"], @event.Details["targetY"]);
                    observed++;
                    Assert.True(
                        creature.Position == booked || creature.Position == before.Position,
                        $"{fixtureName}/{seed}: on tick {acted} creature {@event.CreatureId} was in " +
                        $"flight, was booked onto ({booked.X},{booked.Y}) and the tile was closed to " +
                        $"everybody else for the tick — and it went to " +
                        $"({creature.Position.X},{creature.Position.Y}) instead of yielding or " +
                        "waiting. A yield in the canonical log has to be a yield that happened.");
                }

                previous = current;
            }
        }

        Assert.True(
            observed >= 5,
            $"traffic arbitration booked a runner {observed} times over the whole matrix — 71 " +
            "is what it does today — " +
            "which is too few for the rule above to have been exercised at all. Either the " +
            "runner has dropped out of the arbitration again, or the fixtures stopped " +
            "producing corridors.");
        Assert.True(
            yieldedFor >= 5,
            $"over the whole matrix somebody stepped aside for a runner {yieldedFor} times, " +
            "against 640 today. A runner that publishes no destination is not a mover and " +
            "nothing is arbitrated on its behalf: reverting that half of the fix drops this " +
            "to 2, and the two survivors are creatures that broke after the arbitration had " +
            "already planned the tick. That is the state Issue #101 found.");
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
                var runs = FlightRuns(fixtureName, seed);
                var summary = state.SessionResult;
                report.AppendLine(CultureInfo.InvariantCulture, $"{fixtureName}/{seed} {Describe(flights)}");
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"{fixtureName}/{seed} runs={runs.Count} " +
                    $"movedNoTile={runs.Count(run => run.Distance == 0)} " +
                    $"furthest={(runs.Count == 0 ? 0 : runs.Max(run => run.Distance))} " +
                    $"longestRun={(runs.Count == 0 ? 0 : runs.Max(run => run.Ticks))} ticks");
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

    /// <summary>
    /// The column the refuge tiles live in (contract 15.6, <c>T.flee_tile</c>).
    /// A runner standing there has arrived and is not stalled.
    /// </summary>
    private const int RefugeColumn = 1;

    private static IEnumerable<(string Fixture, ulong Seed)> Cells()
    {
        foreach (var fixtureName in new[] { "baseline", "prepared" })
        {
            foreach (var seed in MatrixSeeds)
            {
                yield return (fixtureName, seed);
            }
        }
    }

    /// <summary>
    /// One entry per flight: how far the creature got from the tile it broke on,
    /// and how long it was in flight. A run that ends because the wave resolved
    /// counts as it stands — the point is whether the domain watched somebody
    /// move, not whether they reached the wall.
    /// </summary>
    private static IReadOnlyList<(int Distance, int Ticks)> FlightRuns(string fixtureName, ulong seed)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var previous = world.GetSnapshot();
        var live = new Dictionary<int, (GridPoint Start, int Distance, int Ticks)>();
        var finished = new List<(int Distance, int Ticks)>();

        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            foreach (var creature in current.Creatures)
            {
                var before = previous.Creatures.Single(other => other.Id == creature.Id);
                if (creature.Mode != CreatureMode.Fled)
                {
                    if (live.Remove(creature.Id, out var ended))
                    {
                        finished.Add((ended.Distance, ended.Ticks));
                    }

                    continue;
                }

                var run = live.TryGetValue(creature.Id, out var known)
                    ? known
                    : (Start: before.Position, Distance: 0, Ticks: 0);
                live[creature.Id] = (
                    run.Start,
                    Math.Max(run.Distance, Manhattan(run.Start, creature.Position)),
                    run.Ticks + 1);
            }

            previous = current;
        }

        finished.AddRange(live.Values.Select(run => (run.Distance, run.Ticks)));
        return finished;
    }

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
