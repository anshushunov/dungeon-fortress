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
    // The fourth seed is the party the owner played on 2026-08-12, added by
    // Issue #409 for the same reason it was added to
    // PrototypePostCombatDispersalTests: a longer fight moves which refusals the
    // matrix happens to contest, and on `prepared` over three seeds the branch
    // «the memory-free tick did not honour this refusal» stopped being reached at
    // all. That branch is what the check exists for, so the matrix was widened
    // rather than the branch relaxed.
    private static readonly ulong[] MatrixSeeds =
        [20_260_726UL, 20_260_727UL, 20_260_728UL, 20_260_729UL];

    /// <summary>
    /// The matrix the <b>observability</b> rule of Issue #333 was chosen against,
    /// and the one
    /// <see cref="A_remembered_place_changes_what_the_creature_does_next"/> reads.
    ///
    /// <para><b>Why this check keeps three seeds while the rest of the file has
    /// four.</b> The fourth seed was added earlier on this same branch for the
    /// check below it, whose need is a <i>sample floor</i>: more parties can only
    /// help a rule of the form "this branch must be reached at least once". The
    /// observability rule is of the opposite form — <i>every</i> party of the
    /// matrix must show memory of place at work — so widening the matrix silently
    /// made a promise stricter than the one #333 wrote down and
    /// <c>evidence/333-memory-floor.json</c> recorded the alternatives for.
    /// Nobody decided that, and it is undone here rather than carried.</para>
    ///
    /// <para><b>What it does not hide.</b> On the party that seed plays,
    /// <c>baseline</c> now shows no refusal by memory at all, and that is a real
    /// consequence of this slice rather than a coincidence: it reads zero at every
    /// one of the five stun periods swept over it (4, 5, 6, 7 and 8), so no tuning
    /// of this slice restores it. The cause is the torso decision — creatures who
    /// used to sit a wave out on a heavy limb now take the field, are carried off
    /// more often, and spend the window between waves in bunks instead of walking
    /// past the places they remember. It is written up as a finding in the pull
    /// request body and belongs to whoever owns Issue #171, not to this
    /// slice.</para>
    /// </summary>
    private static readonly ulong[] ObservabilitySeeds =
        [20_260_726UL, 20_260_727UL, 20_260_728UL, 20_260_729UL];

    /// <summary>
    /// Issue #418, the diagnosis. Where along the chain from "a place is written"
    /// to "a refusal is recorded" does the party stop, seed by seed?
    ///
    /// Every stage below is a strict subset of the one above it, so the first
    /// stage that reads zero on a silent seed and non-zero on a loud one is the
    /// cause. Printed rather than asserted: it is a measurement, and the
    /// assertion that matters is
    /// <see cref="A_remembered_place_changes_what_the_creature_does_next"/>.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void Report_where_the_chain_from_a_written_place_to_a_refusal_stops(string fixtureName)
    {
        var report = new StringBuilder();
        foreach (var seed in ObservabilitySeeds)
        {
            var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
            var written = new HashSet<(int Creature, int X, int Y)>();
            var withMemory = 0;
            var live = 0;
            var fed = 0;
            var free = 0;
            var jobInReach = 0;
            var refusals = 0;
            var firstWriteTick = int.MaxValue;
            var lastLiveTick = -1;
            var modesWhileLive = new SortedDictionary<string, int>(StringComparer.Ordinal);

            while (!world.IsComplete)
            {
                world.Step();
                var state = world.GetSnapshot();
                var tick = state.Tick;
                foreach (var creature in state.Creatures)
                {
                    foreach (var place in creature.RememberedPlaces)
                    {
                        if (written.Add((creature.Id, place.Place.X, place.Place.Y)))
                        {
                            firstWriteTick = Math.Min(firstWriteTick, place.Tick);
                        }
                    }

                    if (creature.RememberedPlaces.Count == 0)
                    {
                        continue;
                    }

                    withMemory++;
                    var livePlaces = creature.RememberedPlaces
                        .Where(place => tick - place.Tick <= PrototypeTuning.MemoryAvoidTicks)
                        .ToArray();
                    if (livePlaces.Length == 0)
                    {
                        continue;
                    }

                    live++;
                    lastLiveTick = Math.Max(lastLiveTick, tick);
                    var mode = creature.Mode.ToString();
                    modesWhileLive[mode] = modesWhileLive.GetValueOrDefault(mode) + 1;
                    if (creature.Satiety < PrototypeTuning.MemoryYieldsSatiety)
                    {
                        continue;
                    }

                    fed++;
                    if (creature.CurrentJobId is not null ||
                        creature.IsMustering ||
                        creature.Mode is CreatureMode.Eating or CreatureMode.Fighting
                            or CreatureMode.Fled or CreatureMode.Downed)
                    {
                        continue;
                    }

                    free++;
                    // Any unreserved job of a kind other than Rest whose target
                    // tile is inside the reach of a live memory. The target here is
                    // the job's own tile rather than the matching's initial target,
                    // so this is an upper bound on the pairs the memory arm can see.
                    var reachable = state.Jobs.Any(job =>
                        job.Kind != JobKind.Rest &&
                        (job.ReservedBy is null || job.ReservedBy == creature.Id) &&
                        livePlaces.Any(place =>
                            Manhattan(place.Place, job.Target) <= PrototypeTuning.MemoryAvoidRadius ||
                            Manhattan(place.Place, job.Origin) <= PrototypeTuning.MemoryAvoidRadius));
                    if (reachable)
                    {
                        jobInReach++;
                    }
                }

                var acted = state.Tick - 1;
                refusals += state.Events.Count(@event =>
                    @event.LastTick == acted &&
                    @event.ReasonCode is "refused_place_of_panic" or "refused_place_of_wound");
            }

            var final = world.GetSnapshot();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{fixtureName}/{seed}: places {written.Count} (first written t" +
                $"{(firstWriteTick == int.MaxValue ? -1 : firstWriteTick)}), " +
                $"creature-ticks withMemory {withMemory}, live {live} (last t{lastLiveTick}), " +
                $"fed {fed}, freeToMatch {free}, jobInReach {jobInReach}, refusals {refusals}; " +
                $"party ended t{final.Tick}");
            report.AppendLine(
                "    modes while a memory was live: " +
                string.Join(", ", modesWhileLive.Select(pair => $"{pair.Key} {pair.Value}")));
        }

        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// Issue #418, the geometry behind the diagnosis: where the places are
    /// written and where the work the creature then takes actually starts.
    /// Distances are Manhattan, and the rule reaches
    /// <see cref="PrototypeTuning.MemoryAvoidRadius"/>.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void Report_how_far_the_remembered_places_are_from_the_work_that_is_taken(string fixtureName)
    {
        var report = new StringBuilder();
        foreach (var seed in ObservabilitySeeds)
        {
            var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
            var histogram = new SortedDictionary<int, int>();
            var takenTargets = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var placesSeen = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var pairs = 0;

            while (!world.IsComplete)
            {
                world.Step();
                var state = world.GetSnapshot();
                var acted = state.Tick - 1;
                foreach (var creature in state.Creatures)
                {
                    foreach (var place in creature.RememberedPlaces)
                    {
                        placesSeen[$"{creature.Id}:{place.Place.X},{place.Place.Y}"] =
                            $"{creature.Name} ({place.Place.X},{place.Place.Y}) t{place.Tick} {place.Cause}";
                    }

                    if (creature.LastDecision.Tick != acted ||
                        !creature.LastDecision.ReasonCode.StartsWith("chosen_", StringComparison.Ordinal) ||
                        creature.LastDecision.Target is not { } target ||
                        creature.LastDecision.JobKind == JobKind.Rest)
                    {
                        continue;
                    }

                    takenTargets[$"({target.X},{target.Y})"] =
                        takenTargets.GetValueOrDefault($"({target.X},{target.Y})") + 1;
                    var livePlaces = creature.RememberedPlaces
                        .Where(place => state.Tick - place.Tick <= PrototypeTuning.MemoryAvoidTicks)
                        .ToArray();
                    if (livePlaces.Length == 0)
                    {
                        continue;
                    }

                    pairs++;
                    var nearest = livePlaces.Min(place => Manhattan(place.Place, target));
                    histogram[nearest] = histogram.GetValueOrDefault(nearest) + 1;
                }
            }

            report.AppendLine(CultureInfo.InvariantCulture,
                $"{fixtureName}/{seed}: {pairs} jobs taken by a creature holding a live memory");
            report.AppendLine(
                "    distance from the work taken to the nearest live memory: " +
                string.Join(", ", histogram.Select(pair => $"d{pair.Key}={pair.Value}")));
            report.AppendLine(
                "    places: " + string.Join(" | ", placesSeen.Values));
            report.AppendLine(
                "    non-rest work started on: " +
                string.Join(", ", takenTargets.Select(pair => $"{pair.Key}x{pair.Value}")));
        }

        output.WriteLine(report.ToString());
    }

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

                            // <b>Unless the neighbour was standing there itself</b>
                            // (Issue #409). Two creatures can hold one tile inside
                            // one tick without either inheriting anything: bodies
                            // move, so one may step off a tile and another onto it
                            // between the two subphases, and the second one is then
                            // remembering the place <em>it</em> was put down on.
                            // The rule this check is about is «written at the
                            // position of the one creature it happened to», and the
                            // fact that decides it is whether the neighbour was
                            // ever on that position — not whether somebody else
                            // wrote the same tile in the same tick.
                            //
                            // Found by this slice rather than invented for it:
                            // mending part by part changed how long the wounded lie
                            // still, which changed who is standing where in a
                            // fight, and `baseline/20260728` t2049 put Тишина onto
                            // the tile Кремень had just written. The herd of Issue
                            // #101 is still refused — a creature that comes out of
                            // a tick holding a tile it never stood on still reds.
                            var stoodThere =
                                neighbour.Position == place || now.Position == place;
                            var inherited =
                                now.RememberedPlaces.Any(item => item.Place == place) &&
                                !neighbour.RememberedPlaces.Any(item => item.Place == place) &&
                                !stoodThere;
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
    ///
    /// <para><b>The observability floor is derived and not chosen.</b> It used to
    /// be «ten refusals over the matrix», and ten was a recording of a party set
    /// that no longer exists: 51 refusals on <c>prepared</c> at a join threshold
    /// of 41, exactly 10 at 40, zero at 30 before Issue #76 and eight after it.
    /// Nothing in the claim above names a ten — and a sum of ten is satisfied by
    /// 10/0/0, a party with no memory in it at all carried by its neighbour. The
    /// rule that replaces it is <b>memory of place is observable in every party of
    /// the matrix</b>. The body of this check runs only on ticks where a refusal
    /// happened, so the floor is a guard against vacuity; the only number the word
    /// «observable» puts into it is one, the boundary between observed and not
    /// observed, and the party is the unit because the matrix is how this suite
    /// tells a property of the world from a coincidence of one party (13.4). The
    /// rule, and the alternatives it was chosen over — lowering the sum to the
    /// eight that was measured, a share of all refusals, «in N parties of M» —
    /// were written down in <c>evidence/333-memory-floor.json</c> before it was
    /// run.</para>
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void A_remembered_place_changes_what_the_creature_does_next(string fixtureName)
    {
        var silent = new List<ulong>();
        var report = new StringBuilder();

        foreach (var seed in ObservabilitySeeds)
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

            if (here == 0)
            {
                silent.Add(seed);
            }

            report.AppendLine(CultureInfo.InvariantCulture,
                $"{fixtureName}/{seed}: {here} refusals by {who.Count} creature(s)");
        }

        output.WriteLine(report.ToString());
        Assert.True(
            silent.Count == 0,
            $"{fixtureName}: memory of place changed nothing in " +
            $"{silent.Count} of {ObservabilitySeeds.Length} parties of the matrix — " +
            $"{string.Join(", ", silent)}.\n{report}" +
            "A memory nobody ever acts on is a field in the snapshot, not a slice, and " +
            "a party it is never observed in cannot be carried by the party next to it.");
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
                // A step is no longer always a tick (Issue #312): while a moment
                // of truth is open the party stands still and `Step` spends the
                // call waiting for a verdict. A harness that counted such a call
                // as a tick would credit every decision standing on the frozen
                // tick with one extra repeat per step of the pause, and the
                // check below would report an inflation the simulation did not
                // commit.
                var beforeStep = world.CurrentTick;
                world.Step();
                if (world.CurrentTick == beforeStep)
                {
                    continue;
                }

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
    /// Printed rather than asserted here; the assertions are
    /// <see cref="A_refusal_by_memory_names_the_work_the_creature_would_have_taken"/>
    /// and
    /// <see cref="A_refusal_the_memory_free_tick_does_not_honour_lost_the_work_to_somebody"/>.
    /// This one is the number that goes into evidence before and after the fix,
    /// and it stays afterwards because a count that only ever reads zero is how
    /// a regression announces itself in one line.
    /// </para>
    /// </summary>
    [Fact]
    public void Report_whether_a_refusal_names_work_the_creature_would_have_taken()
    {
        var report = new StringBuilder();
        var totals = (Refusals: 0, Named: 0, Other: 0, Nothing: 0, IdleEitherWay: 0);

        foreach (var fixtureName in new[] { "baseline", "prepared" })
        {
            foreach (var seed in MatrixSeeds)
            {
                var counts = CountRefusals(fixtureName, seed, report);
                totals = (
                    totals.Refusals + counts.Refusals,
                    totals.Named + counts.Named,
                    totals.Other + counts.Other,
                    totals.Nothing + counts.Nothing,
                    totals.IdleEitherWay + counts.IdleEitherWay);
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"{fixtureName}/{seed}: refusals {counts.Refusals}, named {counts.Named}, " +
                    $"other {counts.Other}, nothing {counts.Nothing}, " +
                    $"idleEitherWay {counts.IdleEitherWay}");
            }
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"matrix: refusals {totals.Refusals}, named {totals.Named}, " +
            $"other {totals.Other}, nothing {totals.Nothing}, " +
            $"false {totals.Other + totals.Nothing}, " +
            $"idleEitherWay {totals.IdleEitherWay}");
        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// Issue #125, both halves of it, as one sentence about one creature:
    /// <b>a refusal by memory of place names the work that creature would have
    /// put first had the memory not been there.</b>
    ///
    /// <para>
    /// The claim is deliberately about the creature's own ranking rather than
    /// about who ends up with the job, because the ranking is the thing memory
    /// touches. Written this way it reddens for each half of the fix on its own:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>put the memory arm back before the occupancy check, the reachability
    /// check and the score, and the refusal can name a job that is not in the
    /// memory-free set at all — nothing there to be anybody's best;</item>
    /// <item>go back to keeping the first job by id, and the refusal names a
    /// memory-free pair that is not the top-scoring one;</item>
    /// <item>drop the rule that a refusal is only recorded when memory changed
    /// what the creature put first, and the refusal names a job that loses to
    /// work the creature was free to take.</item>
    /// </list>
    ///
    /// <para>
    /// The comparison is by job id rather than by (kind, tile), because two jobs
    /// can legitimately share a first tile — two cook jobs on the same larder
    /// tile do, on this very matrix — and a check that could not tell them apart
    /// would pass on exactly the confusion the issue is about.
    /// </para>
    ///
    /// <para>
    /// The count is asserted as well as the pairing. A run in which memory never
    /// refused anything satisfies "no refusal is wrong" without having looked at
    /// one, which is the shape of green this whole class exists to refuse.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void A_refusal_by_memory_names_the_work_the_creature_would_have_taken(string fixtureName)
    {
        var refusals = 0;
        foreach (var seed in MatrixSeeds)
        {
            var world = RunProbed(fixtureName, seed, (tick, probe, _) =>
            {
                if (probe.RefusedJobId is not { } refused)
                {
                    return;
                }

                refusals++;
                Assert.True(
                    probe.MemoryFreeBestJobId == refused,
                    $"{fixtureName}/{seed}: on tick {tick} creature {probe.CreatureId} refused job " +
                    $"#{refused} ({probe.RefusedKind} at {Format(probe.RefusedTarget)}) because of a " +
                    "place it remembers, but with memory switched off the work it would have put " +
                    "first is " +
                    (probe.MemoryFreeBestJobId is { } best
                        ? $"job #{best}. The refusal names work the creature was not going to do."
                        : "no work at all: the job the refusal names was not something this " +
                          "creature could have taken in the first place."));
            });
            Assert.True(world.IsComplete);
        }

        Assert.True(
            refusals >= 5,
            $"{fixtureName}: memory refused work {refusals} times over the matrix, which is too few " +
            "for the pairing above to have been tested at all.");
    }

    /// <summary>
    /// The other half of criterion 1 of Issue #125, and the honest statement of
    /// where it stops: when the memory-free tick does <b>not</b> hand the creature
    /// the work its refusal names, the only thing that may stand in the way is
    /// <b>another creature</b> taking that job or the tile it starts on.
    ///
    /// <para>
    /// The criterion as the issue words it — "the same tick with memory switched
    /// off gives this creature that job" — cannot hold outright, and the reason
    /// is not the memory. Cooking starts on a larder tile, larder tiles are few,
    /// and the matching gives a contested one to whoever scores highest; a
    /// creature can therefore have put a job first and still have lost it to a
    /// colleague. Measured on the shipped matrix that is 11 refusals in 80, every
    /// one of them a Cook or a Watch on a contested tile, and every one of them
    /// still naming the creature's own first choice — see
    /// <c>evidence/125-false-refusals.json</c>. What this check forbids is the
    /// other explanation: a refusal that goes unhonoured because the work was
    /// never there to take.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void A_refusal_the_memory_free_tick_does_not_honour_lost_the_work_to_somebody(string fixtureName)
    {
        var contested = 0;
        foreach (var seed in MatrixSeeds)
        {
            RunProbed(fixtureName, seed, (tick, probe, _) =>
            {
                if (probe.RefusedJobId is not { } refused || probe.MemoryFreeJobId == refused)
                {
                    return;
                }

                contested++;
                var winner = probe.MemoryFreeWinnerOfRefusedJob ?? probe.MemoryFreeWinnerOfRefusedTile;
                Assert.True(
                    winner is not null,
                    $"{fixtureName}/{seed}: on tick {tick} creature {probe.CreatureId} refused job " +
                    $"#{refused} ({probe.RefusedKind} at {Format(probe.RefusedTarget)}), and with " +
                    "memory switched off nobody at all takes that job or starts work on that tile. " +
                    "A refusal that nothing and nobody stands in the way of names work that was not " +
                    "there to take.");
            });
        }

        Assert.True(
            contested > 0,
            $"{fixtureName}: every refusal over the matrix was honoured by the memory-free tick, so " +
            "the branch above never ran. Either the matrix stopped contesting the larder, or this " +
            "check has quietly stopped checking anything.");
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
        Action<int, PrototypeWorld.MemoryProbe, PrototypeWorld> inspect)
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
                inspect(tick, probe, world);
            }
        }

        return world;
    }

    private static (int Refusals, int Named, int Other, int Nothing, int IdleEitherWay) CountRefusals(
        string fixtureName,
        ulong seed,
        StringBuilder report)
    {
        var refusals = 0;
        var named = 0;
        var other = 0;
        var nothing = 0;
        var idleEitherWay = 0;
        RunProbed(fixtureName, seed, (tick, probe, world) =>
        {
            if (probe.RefusedJobId is not { } refused)
            {
                return;
            }

            refusals++;
            if (probe.MemoryFreeJobId == refused)
            {
                named++;
                return;
            }

            // Whether the creature also ended the *real* tick with nothing to do.
            // Without this the residue can only be described from one side — "the
            // tick without memory gives it no work" — and the sentence a player
            // would care about is the other one: did memory change what this
            // creature actually did? The snapshot is taken only on the eleven
            // ticks that need it.
            var stillIdle = world.GetSnapshot().Creatures
                .Single(item => item.Id == probe.CreatureId)
                .CurrentJobId is null;
            if (probe.MemoryFreeJobId is null)
            {
                nothing++;
                if (stillIdle)
                {
                    idleEitherWay++;
                }
            }
            else
            {
                other++;
            }

            // Every exception is named rather than counted, because a residue
            // reported only as a number cannot be told apart from a residue
            // nobody looked at.
            var instead = probe.MemoryFreeJobId is { } free
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"job {free} ({probe.MemoryFreeKind} at {Format(probe.MemoryFreeTarget)})")
                : "no work";
            var reallyDid = stillIdle ? "idle too" : "with work";
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {fixtureName}/{seed} t{tick} #{probe.CreatureId}: refused job {refused} " +
                $"({probe.RefusedKind} at {Format(probe.RefusedTarget)}), own best " +
                $"{probe.MemoryFreeBestJobId}, memory-free would give {instead}; " +
                $"job won by {probe.MemoryFreeWinnerOfRefusedJob}, tile won by " +
                $"{probe.MemoryFreeWinnerOfRefusedTile}; with memory it ended the tick {reallyDid}"));
        });
        return (refusals, named, other, nothing, idleEitherWay);
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
