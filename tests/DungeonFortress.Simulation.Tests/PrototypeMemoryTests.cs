using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Memory of place: the first thing in Prototype 1 that lets a creature's past
/// change its future (Issue #117).
///
/// The tests are split by the claim they make rather than by the code they
/// touch, because the two halves of the change fail for different reasons and
/// the issue asks for a check that reddens when either half alone is reverted:
///
/// <list type="bullet">
/// <item><b>writing</b> — a creature that broke or was put down remembers the
/// tile, and only that creature does. Revert <c>Remember</c> and every test here
/// that asks for a memory fails;</item>
/// <item><b>reading</b> — a creature refuses work that starts at a place it
/// remembers, and says so. Revert the <c>AvoidedPlace</c> arm of
/// <c>MatchJobs</c> and <see cref="A_remembered_place_changes_what_the_creature_does_next"/>
/// fails while the writing tests stay green.</item>
/// </list>
/// </summary>
public sealed class PrototypeMemoryTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    /// <summary>
    /// The writing half. Over the matrix, creatures come out of a party with
    /// places written on them, both causes are reached, and no creature holds
    /// more than the cap.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void A_party_leaves_its_survivors_with_places_they_remember(string fixtureName)
    {
        var causes = new HashSet<string>(StringComparer.Ordinal);
        var withMemory = 0;

        foreach (var seed in MatrixSeeds)
        {
            var state = RunAtSeed(fixtureName, seed);
            foreach (var creature in state.Creatures)
            {
                Assert.True(
                    creature.RememberedPlaces.Count <= PrototypeTuning.MemoryPlacesMax,
                    $"{fixtureName}/{seed}: {creature.Name} remembers " +
                    $"{creature.RememberedPlaces.Count} places, and the cap is " +
                    $"{PrototypeTuning.MemoryPlacesMax}. A creature that remembered every wave " +
                    "would end the party unable to work anywhere the fighting reached.");
                Assert.Equal(
                    creature.RememberedPlaces.Select(place => place.Place).Distinct().Count(),
                    creature.RememberedPlaces.Count);
                if (creature.RememberedPlaces.Count > 0)
                {
                    withMemory++;
                }

                foreach (var place in creature.RememberedPlaces)
                {
                    causes.Add(place.Cause);
                    Assert.InRange(place.Tick, 0, state.Tick);
                }
            }
        }

        Assert.True(
            withMemory >= 5,
            $"{fixtureName}: only {withMemory} creature-parties over the matrix came out with a " +
            "memory at all, which is too few for anything below to have been exercised.");
        // Both causes, and both asserted. There are two places in the simulation
        // that write a memory, and without naming the second one here removing it
        // is a change no check can see: measured by mutation, deleting the write
        // on `combat_downed` left this whole class green.
        Assert.Contains("panic", causes);
        Assert.Contains("wound", causes);
    }

    /// <summary>
    /// Criterion 2 of Issue #117, and the property #101 bought for panic that
    /// this slice must not spend: <b>the same place is avoided by one creature,
    /// not by the group.</b>
    ///
    /// It is asserted twice over, because one form of it can pass while the other
    /// fails. First, <b>no spillover</b>: whenever anybody writes a tile, nobody
    /// else standing within the avoidance radius of it comes out of that same tick
    /// having written it too. Second, <b>no cohort</b>: no single tick writes a
    /// memory into more than a third of the domain, which is the same bound and
    /// the same number <c>PrototypeMoraleTests</c> holds the moment of breaking
    /// to.
    ///
    /// Between them they catch the change this slice must not make: writing the
    /// memory from a shared event instead of from the creature's own position,
    /// which is the exact shape of defect Issue #101 removed from morale.
    ///
    /// What is deliberately <b>not</b> asserted is how many creatures end a party
    /// remembering the same tile. On the dungeon that number reaches five of nine
    /// (seed 20260727, tile (24,7)) and it is not a herd: the corridor is where
    /// the raiders walk, so defenders meet them on the same few tiles wave after
    /// wave and break there on their own occasions, ticks apart. A bound on it
    /// would be a bound on the map, not on the memory. It is printed by
    /// <see cref="Report_what_the_domain_remembers_after_a_party"/> instead.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void A_place_is_remembered_by_one_creature_and_not_by_the_group(string fixtureName)
    {
        foreach (var seed in MatrixSeeds)
        {
            var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
            var witnessed = 0;
            var previous = world.GetSnapshot();

            while (!world.IsComplete)
            {
                world.Step();
                var current = world.GetSnapshot();
                var cohort = current.Creatures.Count(creature =>
                    creature.RememberedPlaces
                        .Select(place => place.Place)
                        .Except(previous.Creatures
                            .Single(item => item.Id == creature.Id)
                            .RememberedPlaces
                            .Select(place => place.Place))
                        .Any());
                Assert.True(
                    cohort * 3 <= current.Creatures.Count,
                    $"{fixtureName}/{seed}: tick {current.Tick - 1} wrote a memory into {cohort} of " +
                    $"{current.Creatures.Count} creatures at once. A memory of place is written " +
                    "where one creature was standing when one thing happened to it; a cohort means " +
                    "it has become a property of the wave instead.");

                foreach (var creature in current.Creatures)
                {
                    var before = previous.Creatures.Single(item => item.Id == creature.Id);
                    var written = creature.RememberedPlaces
                        .Select(place => place.Place)
                        .Except(before.RememberedPlaces.Select(place => place.Place))
                        .ToArray();
                    foreach (var place in written)
                    {
                        // Anybody else who was near enough that a shared memory
                        // would have caught them too.
                        var neighbours = previous.Creatures
                            .Where(other =>
                                other.Id != creature.Id &&
                                Manhattan(other.Position, place) <= PrototypeTuning.MemoryAvoidRadius)
                            .ToArray();
                        foreach (var neighbour in neighbours)
                        {
                            witnessed++;
                            var now = current.Creatures.Single(item => item.Id == neighbour.Id);
                            var inherited =
                                now.RememberedPlaces.Any(item => item.Place == place) &&
                                !neighbour.RememberedPlaces.Any(item => item.Place == place);
                            Assert.False(
                                inherited,
                                $"{fixtureName}/{seed}: on tick {current.Tick - 1} {creature.Name} " +
                                $"wrote ({place.X},{place.Y}) and {neighbour.Name}, standing " +
                                $"{Manhattan(neighbour.Position, place)} tiles away, came out of the " +
                                "same tick remembering it too. Memory of place is written at the " +
                                "position of the one creature it happened to; a second creature " +
                                "inheriting it is the herd Issue #101 removed, coming back.");
                        }
                    }
                }

                previous = current;
            }

            Assert.True(
                witnessed >= 3,
                $"{fixtureName}/{seed}: only {witnessed} memories were written with anybody else " +
                "standing within the avoidance radius, which is too few for the claim above to " +
                "have been tested at all.");
        }
    }

    /// <summary>
    /// The reading half, and the observable point of the whole slice: a memory
    /// changes what the creature does next, and the change is in the canonical
    /// log by name.
    ///
    /// The assertion is deliberately about the pair (creature, place) rather
    /// than about the count: every refusal names the tile the creature is
    /// avoiding, and that tile has to be one of the tiles that creature — and
    /// not some other one — actually remembers.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void A_remembered_place_changes_what_the_creature_does_next(string fixtureName)
    {
        var refusals = 0;
        var report = new StringBuilder();

        foreach (var seed in MatrixSeeds)
        {
            // Walked tick by tick rather than read off the final state, because a
            // memory can be pushed out by the cap after the refusal it caused:
            // the log would then name a place the creature no longer holds, and
            // checking the end of the party would call that a defect.
            var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
            var here = 0;
            var who = new HashSet<int>();

            while (!world.IsComplete)
            {
                world.Step();
                var state = world.GetSnapshot();
                var acted = state.Tick - 1;
                foreach (var @event in state.Events)
                {
                    if (@event.LastTick != acted ||
                        @event.ReasonCode is not ("refused_place_of_panic" or "refused_place_of_wound"))
                    {
                        continue;
                    }

                    here++;
                    who.Add(@event.CreatureId);
                    var creature = state.Creatures.Single(item => item.Id == @event.CreatureId);
                    var place = new GridPoint(@event.Details["placeX"], @event.Details["placeY"]);
                    Assert.Contains(creature.RememberedPlaces, item => item.Place == place);
                    Assert.Equal(
                        @event.ReasonCode == "refused_place_of_wound" ? "wound" : "panic",
                        creature.RememberedPlaces.Single(item => item.Place == place).Cause);
                    Assert.NotNull(@event.Target);
                    Assert.True(
                        Manhattan(place, @event.Target!.Value) <= PrototypeTuning.MemoryAvoidRadius,
                        $"{fixtureName}/{seed}: {creature.Name} refused work at " +
                        $"({@event.Target.Value.X},{@event.Target.Value.Y}) naming a place " +
                        $"({place.X},{place.Y}) that is further away than the rule reaches.");
                    // Lying down is exempt on purpose: a wound closes in a bunk.
                    Assert.NotEqual(JobKind.Rest, @event.JobKind);
                }
            }

            refusals += here;
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{fixtureName}/{seed}: {here} refusals by {who.Count} creature(s)");
        }

        output.WriteLine(report.ToString());
        Assert.True(
            refusals >= 10,
            $"{fixtureName}: memory refused work {refusals} times over the whole matrix.\n{report}" +
            "A memory nobody ever acts on is a field in the snapshot, not a slice.");
    }

    /// <summary>
    /// A creature that refuses the only work it could reach says so as its own
    /// last word, rather than reporting the next-best diagnostic about a job it
    /// was never going to take. This is what the inspector shows when a player
    /// asks why somebody is standing about.
    /// </summary>
    [Fact]
    public void An_idle_creature_that_is_avoiding_a_place_says_so_rather_than_something_else()
    {
        var seen = 0;
        foreach (var fixtureName in new[] { "baseline", "prepared" })
        {
            foreach (var seed in MatrixSeeds)
            {
                var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
                while (!world.IsComplete)
                {
                    world.Step();
                    var state = world.GetSnapshot();
                    seen += state.Creatures.Count(creature =>
                        creature.Mode == CreatureMode.Waiting &&
                        creature.LastDecision.Tick == state.Tick - 1 &&
                        creature.LastDecision.ReasonCode is
                            "refused_place_of_panic" or "refused_place_of_wound");
                }
            }
        }

        Assert.True(
            seen > 0,
            "over the whole matrix no creature ever stood idle with the refusal as its last " +
            "word. Either the refusal never leaves a creature without work, or " +
            "RecordWaitingReason stopped preferring it, and the inspector is answering " +
            "'why is this one doing nothing' with the wrong sentence.");
    }

    /// <summary>
    /// <c>repeats</c> counts the ticks a decision was taken on, not the calls
    /// that recorded it.
    ///
    /// This is a statement about the canonical event log rather than about
    /// memory, and it is here because memory is what broke it. The refusal was
    /// written twice on the same tick — once before matching, so that a creature
    /// which then took other work still said what it would not do, and once again
    /// by <c>RecordWaitingReason</c> for a creature that ended up with nothing.
    /// <c>RecordDecision</c> folds an identical repeat, so the second call did not
    /// make a second event: it made the first one claim two. On its first tick.
    ///
    /// The damage was canonical and it was published: the feed printed "(x2)" for
    /// one refusal, and <c>ReasonCodeOccurrences</c> — which sums <c>Repeats</c>,
    /// and which the contract quotes — doubled every count of these two codes.
    ///
    /// The check is written over every reason code rather than over the two this
    /// slice added, because the rule is not about them.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void A_repeat_counts_a_tick_rather_than_a_call_that_recorded_it(string fixtureName)
    {
        foreach (var seed in MatrixSeeds)
        {
            var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
            // An event is identified by its position in the log. The list is
            // append-only and never reordered, so the index is stable across
            // snapshots — and it is the only stable identity: one creature can
            // legitimately take two *different* decisions on one tick, so
            // (creature, firstTick) is not unique and keying on it made this test
            // fail on `waiting_crop_not_ripe` for a reason that was not a defect.
            var ticksSeen = new Dictionary<int, int>();

            while (!world.IsComplete)
            {
                world.Step();
                var state = world.GetSnapshot();
                var acted = state.Tick - 1;
                for (var index = 0; index < state.Events.Count; index++)
                {
                    if (state.Events[index].LastTick == acted)
                    {
                        ticksSeen[index] = ticksSeen.GetValueOrDefault(index) + 1;
                    }
                }
            }

            var events = world.GetSnapshot().Events;
            for (var index = 0; index < events.Count; index++)
            {
                var @event = events[index];
                Assert.True(
                    @event.Repeats == ticksSeen[index],
                    $"{fixtureName}/{seed}: the event '{@event.ReasonCode}' of creature " +
                    $"{@event.CreatureId}, first seen on tick {@event.FirstTick}, claims " +
                    $"{@event.Repeats} repeats and was recorded on {ticksSeen[index]} tick(s). " +
                    "A repeat counts a tick the decision was taken on; anything else means " +
                    "some path writes the same decision twice in one tick and the canonical " +
                    "counter is inflated.");
            }
        }
    }

    /// <summary>
    /// The counterfactual of Issue #125 is a <b>question</b> about the party and
    /// never a change to it.
    ///
    /// <see cref="PrototypeWorld.TrackMemoryFreeMatching"/> makes every tick
    /// resolve the matching a second time with memory switched off. The second
    /// pass books nothing, records no decision and moves nobody, so the canonical
    /// document and the canonical event log of a probed party have to be the same
    /// bytes as those of an unprobed one. Without this check the measurement
    /// below would be evidence about a world nobody plays.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void The_counterfactual_probe_changes_nothing_the_party_does(string fixtureName)
    {
        foreach (var seed in MatrixSeeds)
        {
            var plain = PrototypeScenario.Capture(RunToEnd(fixtureName, seed, probe: false));
            var probed = PrototypeScenario.Capture(RunToEnd(fixtureName, seed, probe: true));
            Assert.Equal(plain.Checksum, probed.Checksum);
            Assert.Equal(plain.CanonicalEventLog, probed.CanonicalEventLog);
        }
    }

    /// <summary>
    /// Criterion 1 of Issue #125, measured rather than argued: for every refusal
    /// by memory of place, does the same tick with memory switched off give that
    /// creature the very job the refusal names?
    ///
    /// <para>
    /// Three outcomes are counted apart, because they are three different
    /// sentences to a player:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><b>named</b> — memory took exactly this work away, and the refusal
    /// is true;</item>
    /// <item><b>other</b> — without memory the creature would have taken a
    /// different job, so the refusal names work it was not going to do
    /// anyway;</item>
    /// <item><b>nothing</b> — without memory the creature would still have been
    /// left with no work at all, so memory explains an idleness it did not
    /// cause.</item>
    /// </list>
    ///
    /// <para>
    /// Printed rather than asserted here; the assertion —
    /// <c>A_refusal_by_memory_names_the_work_the_creature_would_have_taken</c> —
    /// arrives with the fix, because on this tree it is red by construction.
    /// This one is the number that goes into evidence before and after the fix,
    /// and it stays afterwards because a count that only ever reads zero is how
    /// a regression announces itself in one line.
    /// </para>
    /// </summary>
    [Fact]
    public void Report_whether_a_refusal_names_work_the_creature_would_have_taken()
    {
        var report = new StringBuilder();
        var totals = (Refusals: 0, Named: 0, Other: 0, Nothing: 0);

        foreach (var fixtureName in new[] { "baseline", "prepared" })
        {
            foreach (var seed in MatrixSeeds)
            {
                var counts = CountRefusals(fixtureName, seed);
                totals = (
                    totals.Refusals + counts.Refusals,
                    totals.Named + counts.Named,
                    totals.Other + counts.Other,
                    totals.Nothing + counts.Nothing);
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"{fixtureName}/{seed}: refusals {counts.Refusals}, named {counts.Named}, " +
                    $"other {counts.Other}, nothing {counts.Nothing}");
            }
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"matrix: refusals {totals.Refusals}, named {totals.Named}, " +
            $"other {totals.Other}, nothing {totals.Nothing}, " +
            $"false {totals.Other + totals.Nothing}");
        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// The distribution itself, printed rather than asserted: which places each
    /// creature came out of the party carrying. This is what a person reads when
    /// they want to know whether the stories are worth telling.
    /// </summary>
    [Fact]
    public void Report_what_the_domain_remembers_after_a_party()
    {
        var report = new StringBuilder();
        foreach (var fixtureName in new[] { "baseline", "prepared" })
        {
            var state = RunAtSeed(fixtureName, MatrixSeeds[0]);
            foreach (var creature in state.Creatures.Where(item => item.RememberedPlaces.Count > 0))
            {
                var places = string.Join(
                    ", ",
                    creature.RememberedPlaces.Select(place =>
                        $"({place.Place.X},{place.Place.Y}) t{place.Tick} {place.Cause}"));
                report.AppendLine(CultureInfo.InvariantCulture, $"{fixtureName} {creature.Name}: {places}");
            }
        }

        output.WriteLine(report.ToString());
    }

    private static int Manhattan(GridPoint left, GridPoint right) =>
        Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private static string Format(GridPoint? point) =>
        point is { } tile
            ? string.Create(CultureInfo.InvariantCulture, $"({tile.X},{tile.Y})")
            : "nowhere";

    /// <summary>
    /// A whole party, optionally answering the counterfactual of Issue #125 on
    /// every tick.
    /// </summary>
    private static PrototypeWorld RunToEnd(string fixtureName, ulong seed, bool probe)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed })
        {
            TrackMemoryFreeMatching = probe,
        };
        while (!world.IsComplete)
        {
            world.Step();
        }

        return world;
    }

    /// <summary>
    /// The same party, with <paramref name="inspect"/> shown every probe of every
    /// tick as it happens. The probe is per-tick state, so it cannot be read off
    /// the end of the run.
    /// </summary>
    private static PrototypeWorld RunProbed(
        string fixtureName,
        ulong seed,
        Action<int, PrototypeWorld.MemoryProbe> inspect)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed })
        {
            TrackMemoryFreeMatching = true,
        };
        while (!world.IsComplete)
        {
            var tick = world.CurrentTick;
            world.Step();
            foreach (var probe in world.MemoryProbes)
            {
                inspect(tick, probe);
            }
        }

        return world;
    }

    private static (int Refusals, int Named, int Other, int Nothing) CountRefusals(
        string fixtureName,
        ulong seed)
    {
        var refusals = 0;
        var named = 0;
        var other = 0;
        var nothing = 0;
        RunProbed(fixtureName, seed, (_, probe) =>
        {
            if (probe.RefusedJobId is not { } refused)
            {
                return;
            }

            refusals++;
            if (probe.MemoryFreeJobId == refused)
            {
                named++;
            }
            else if (probe.MemoryFreeJobId is null)
            {
                nothing++;
            }
            else
            {
                other++;
            }
        });
        return (refusals, named, other, nothing);
    }

    private static PrototypeSnapshot RunAtSeed(string fixtureName, ulong seed) =>
        PrototypeScenario.Run(
            LoadFixture(fixtureName) with { Seed = seed },
            PrototypeTuning.SessionTicks).State;

    private static PrototypeCommandLog LoadFixture(string fixtureName) =>
        PrototypeCommandDocument.Load(System.IO.Path.Combine(
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
