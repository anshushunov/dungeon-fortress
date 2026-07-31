using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// What the traffic arbitration does to a party, measured rather than described.
///
/// Issue #119: the order to step aside used to be handed to whoever happened to
/// stand in the way, including a defender in a fight and a creature on the
/// floor. Neither can take the step — <c>ActCreatures</c> sends the first to
/// <c>ActCombatant</c> and skips the second — so the tile booked for the yield
/// was closed for the tick with nobody entering it, and the canonical log
/// carried a <c>chosen_traffic_yield</c> for a move that never happened.
///
/// The measurement lives next to the assertion on purpose. Issue #117 turned the
/// starting hall into chambers joined by corridors, which is exactly the layout
/// where a tile locked for nobody stops being invisible: in a hall the traffic
/// walks around it, in a doorway there is nothing to walk around.
///
/// Measured by <see cref="Report_traffic_over_the_seed_matrix"/> on the hall
/// layout, before either change of #117, and kept here because it is the "before"
/// half of the comparison the issue asks for:
///
/// <code>
/// baseline/20260726 blockedSteps=6313 yields=556 unexecuted=126 fighting=23  downed=87
/// baseline/20260727 blockedSteps=4540 yields=406 unexecuted=49  fighting=21  downed=0
/// baseline/20260728 blockedSteps=5828 yields=540 unexecuted=176 fighting=88  downed=44
/// prepared/20260726 blockedSteps=5355 yields=467 unexecuted=200 fighting=109 downed=82
/// prepared/20260727 blockedSteps=4530 yields=351 unexecuted=77  fighting=73  downed=0
/// prepared/20260728 blockedSteps=5841 yields=524 unexecuted=280 fighting=95  downed=173
/// </code>
/// </summary>
public sealed class PrototypeTrafficTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    public static TheoryData<string, ulong> Matrix()
    {
        var data = new TheoryData<string, ulong>();
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                data.Add(fixtureName, seed);
            }
        }

        return data;
    }

    /// <summary>
    /// The whole of Issue #119, stated so that removing either half fails.
    ///
    /// A creature is only asked to step aside if the tick it is in will actually
    /// let it step. Two modes will not: <see cref="CreatureMode.Fighting"/>, which
    /// <c>ActCreatures</c> hands to <c>ActCombatant</c> before it ever looks at
    /// the booking, and <see cref="CreatureMode.Downed"/>, which it skips
    /// outright. Both used to be picked as yielders — measured on `main` before
    /// #101 at 30/63/39 ticks a party for the first and 30/45/60 for the second —
    /// and every one of those ticks closed a tile for nobody.
    ///
    /// The mode is read at the end of the tick rather than at the moment of
    /// planning, which is the only place a test can stand. That reading is exact
    /// for both modes: combat participation is decided before traffic is planned
    /// and <c>ActCombatant</c> never changes the mode, so a creature that ends the
    /// tick fighting was fighting when the yield was planned. A creature put down
    /// by a raider later in the same tick is the one case where the two readings
    /// differ, and it is excluded by the move test — it had already taken its
    /// step before the raider reached it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void A_yield_is_never_ordered_to_a_creature_that_cannot_take_the_step(
        string fixtureName,
        ulong seed)
    {
        var measurement = Measure(fixtureName, seed);

        Assert.True(
            measurement.OrdersToFighting == 0,
            $"{fixtureName}/{seed}: {measurement.OrdersToFighting} tick(s) ordered a defender " +
            "in a fight to step aside. It cannot: ActCreatures hands a Fighting creature to " +
            "ActCombatant and never reads TrafficTarget, so the booked tile stays shut for the " +
            $"tick with nobody in it. {measurement}");
        Assert.True(
            measurement.OrdersToDowned == 0,
            $"{fixtureName}/{seed}: {measurement.OrdersToDowned} tick(s) ordered a creature on " +
            "the floor to step aside. It cannot: ActCreatures skips a Downed creature entirely, " +
            $"so the booked tile stays shut for the tick with nobody in it. {measurement}");
    }

    /// <summary>
    /// The rule above is only worth anything while the arbitration is actually
    /// running, so the sample is asserted next to it. A layout change that stopped
    /// producing doorways would make every count above zero for the wrong reason.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void The_matrix_still_produces_enough_traffic_to_measure(string fixtureName, ulong seed)
    {
        var measurement = Measure(fixtureName, seed);

        Assert.True(
            measurement.Yields >= 20,
            $"{fixtureName}/{seed}: traffic arbitration handed out {measurement.Yields} yields " +
            "over the whole party, which is too few for the rule about who may receive one to " +
            $"have been exercised. {measurement}");
    }

    /// <summary>
    /// The numbers themselves, printed rather than asserted: how often a step was
    /// refused, how often somebody was asked to move out of the way, and how often
    /// that order went to a creature that could not obey it. This is the "before
    /// and after" the layout change is judged by.
    /// </summary>
    [Fact]
    public void Report_traffic_over_the_seed_matrix()
    {
        var report = new StringBuilder();
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"{Measure(fixtureName, seed)}");
            }
        }

        output.WriteLine(report.ToString());
    }

    private static readonly string[] Fixtures = ["baseline", "prepared"];

    /// <summary>
    /// One party, walked tick by tick, counting the four traffic facts the
    /// canonical state already publishes.
    /// </summary>
    private static TrafficMeasurement Measure(string fixtureName, ulong seed)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var previous = world.GetSnapshot();
        var blockedSteps = 0;
        var yields = 0;
        var ordersToFighting = 0;
        var ordersToDowned = 0;
        var unexecuted = 0;

        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            var acted = current.Tick - 1;

            foreach (var @event in current.Events)
            {
                if (@event.LastTick != acted)
                {
                    continue;
                }

                if (@event.ReasonCode == "waiting_blocked_by_other")
                {
                    blockedSteps++;
                    continue;
                }

                if (@event.ReasonCode != "chosen_traffic_yield")
                {
                    continue;
                }

                yields++;
                var creature = current.Creatures.Single(item => item.Id == @event.CreatureId);
                if (creature.LastMoveTick == acted)
                {
                    continue;
                }

                unexecuted++;
                if (creature.Mode == CreatureMode.Fighting)
                {
                    ordersToFighting++;
                }
                else if (creature.Mode == CreatureMode.Downed)
                {
                    ordersToDowned++;
                }
            }

            previous = current;
        }

        return new TrafficMeasurement(
            fixtureName,
            seed,
            previous.Tick,
            blockedSteps,
            yields,
            unexecuted,
            ordersToFighting,
            ordersToDowned);
    }

    private sealed record TrafficMeasurement(
        string Fixture,
        ulong Seed,
        int Ticks,
        int BlockedSteps,
        int Yields,
        int UnexecutedYields,
        int OrdersToFighting,
        int OrdersToDowned)
    {
        public override string ToString() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Fixture}/{Seed} ticks={Ticks} blockedSteps={BlockedSteps} yields={Yields} " +
                $"unexecutedYields={UnexecutedYields} ordersToFighting={OrdersToFighting} " +
                $"ordersToDowned={OrdersToDowned}");
    }

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
