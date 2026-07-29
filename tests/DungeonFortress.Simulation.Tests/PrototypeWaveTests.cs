using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// The observable claims of slice 1: a party is a sequence of waves, renown is a
/// score that impoverishment cannot improve, domain strength is the mirror next
/// to it, a wound closes in a creature that rests and eats, reach is a parameter
/// rather than a literal, and none of it costs determinism.
/// </summary>
public sealed class PrototypeWaveTests
{
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void A_party_is_a_sequence_of_waves_each_stronger_than_the_one_before(string fixtureName)
    {
        var state = PrototypeScenario.Run(LoadFixture(fixtureName), PrototypeTuning.SessionTicks).State;

        Assert.InRange(state.Waves.Count, 3, 5);
        Assert.All(state.Waves, wave => Assert.NotNull(wave.Outcome));
        Assert.All(
            state.Waves.Zip(state.Waves.Skip(1)),
            pair =>
            {
                Assert.True(
                    pair.Second.ArriveTick > pair.First.ArriveTick,
                    $"wave {pair.Second.Number} arrives at {pair.Second.ArriveTick}, " +
                    $"wave {pair.First.Number} at {pair.First.ArriveTick}");
                Assert.True(
                    pair.Second.RaiderCount > pair.First.RaiderCount,
                    $"wave {pair.Second.Number} brings {pair.Second.RaiderCount} raiders, " +
                    $"wave {pair.First.Number} brought {pair.First.RaiderCount}");
                Assert.True(pair.Second.RaiderMight >= pair.First.RaiderMight);
                Assert.True(
                    pair.Second.RenownAtAnnounce > pair.First.RenownAtAnnounce,
                    "the strength of a wave follows renown, so renown has to have grown");
            });

        // Every raider on the map belongs to exactly one wave and to a wave that
        // has arrived, so the structured state answers "who is this?" without a
        // picture.
        Assert.All(
            state.Raiders,
            raider => Assert.True(state.Waves.Single(wave => wave.Number == raider.Wave).Arrived));
        Assert.Equal(
            state.Waves.Where(wave => wave.Arrived).Sum(wave => wave.RaiderCount),
            state.Raiders.Count);

        // ... and the log names the wave every defender answered.
        var waveNumbers = state.Events
            .Where(@event => @event.Details.ContainsKey("wave"))
            .Select(@event => @event.Details["wave"])
            .Distinct()
            .ToArray();
        Assert.True(waveNumbers.Length > 1, "the event log never names more than one wave");
        Assert.All(waveNumbers, number => Assert.InRange(number, 1, state.Waves.Count));
    }

    /// <summary>
    /// The guard against the defect that made <c>overrun</c> the best outcome of
    /// the previous evaluation. Renown is checked at every single tick of a full
    /// party, in the scenario that loses the most.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    [InlineData("neglected")]
    public void Renown_never_decreases_at_any_tick_of_a_party(string fixtureName)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName));
        var previous = world.GetSnapshot().Domain.Renown;
        var lostSomething = false;

        while (!world.IsComplete)
        {
            var before = world.GetSnapshot();
            world.Step();
            var current = world.GetSnapshot();
            Assert.True(
                current.Domain.Renown >= previous,
                $"renown fell from {previous} to {current.Domain.Renown} at tick {current.Tick}");
            lostSomething |= current.Stocks.Meals < before.Stocks.Meals ||
                current.Domain.DownedCreatures > before.Domain.DownedCreatures;
            previous = current.Domain.Renown;
        }

        Assert.True(lostSomething, "the fixture never lost a portion or a creature");
    }

    /// <summary>
    /// Criterion 6, stated as an experiment rather than an opinion: the same
    /// seed and the same start, one plan that keeps its people fed and mended
    /// and one that stops doing either half way through the party.
    ///
    /// The comparison is on renown, because renown is the score. Domain strength
    /// is deliberately not compared: it is the mirror, and a domain that never
    /// fights keeps a higher one precisely because it did nothing.
    /// </summary>
    [Fact]
    public void Deliberately_losing_creatures_and_stock_never_scores_better()
    {
        var kept = PrototypeScenario.Run(LoadFixture("prepared"), PrototypeTuning.SessionTicks).State;

        // The very same plan, until the wave after the first one: from there the
        // domain stops harvesting, stops cooking and stops letting anybody lie
        // down. Its people go hungry, its wounded stay wounded and its larder is
        // carried away.
        var abandonedHalfWay = PrototypeScenario.Run(
            LoadFixture("prepared") with
            {
                Scenario = "custom",
                Commands =
                [
                    .. LoadFixture("prepared").Commands,
                    new SetPriorityCommand(1_400, JobKind.Harvest, 0),
                    new SetPriorityCommand(1_400, JobKind.Cook, 0),
                    new SetPriorityCommand(1_400, JobKind.Rest, 0),
                ],
            },
            PrototypeTuning.SessionTicks).State;

        // ... and the same start given up on entirely, which is the extreme of
        // the same strategy: lose everything as fast as possible.
        var abandonedAtOnce = PrototypeScenario.Run(
            new PrototypeCommandLog(
                "custom",
                PrototypeTuning.DefaultSeed,
                [
                    new SetPriorityCommand(0, JobKind.Harvest, 0),
                    new SetPriorityCommand(0, JobKind.Cook, 0),
                ]),
            PrototypeTuning.SessionTicks).State;

        Assert.True(
            abandonedHalfWay.Stocks.MealsProduced < kept.Stocks.MealsProduced &&
            abandonedHalfWay.SessionResult.MealsStolen > 0,
            "the half-way run did not actually lose stock");
        Assert.True(
            abandonedHalfWay.Domain.Renown <= kept.Domain.Renown,
            $"giving up half way scored {abandonedHalfWay.Domain.Renown}, " +
            $"keeping scored {kept.Domain.Renown}");

        Assert.Equal("fallen", abandonedAtOnce.SessionResult.Outcome);
        Assert.Equal(0, abandonedAtOnce.Stocks.MealsProduced);
        Assert.True(
            abandonedAtOnce.Domain.Renown < kept.Domain.Renown,
            $"giving up at once scored {abandonedAtOnce.Domain.Renown}, " +
            $"keeping scored {kept.Domain.Renown}");
    }

    [Fact]
    public void A_wound_closes_in_a_creature_that_rests_and_eats()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        var wounded = new HashSet<int>();
        var mendedTicks = new List<int>();

        while (!world.IsComplete)
        {
            world.Step();
            var state = world.GetSnapshot();
            foreach (var creature in state.Creatures.Where(creature => creature.Injury != InjuryKind.None))
            {
                wounded.Add(creature.Id);
            }
        }

        var final = world.GetSnapshot();
        foreach (var @event in final.Events.Where(@event => @event.ReasonCode == "injury_healed"))
        {
            mendedTicks.Add(@event.LastTick);
            Assert.Equal(JobKind.Rest, @event.JobKind);
            Assert.Equal(@event.Details["maxHp"], @event.Details["hp"]);
        }

        Assert.NotEmpty(wounded);
        Assert.NotEmpty(mendedTicks);
        Assert.Contains(final.Events, @event => @event.ReasonCode == "injury_mending");
    }

    /// <summary>
    /// The same claim as a controlled experiment, so it does not depend on the
    /// baseline party happening to produce a wound: a creature is wounded by the
    /// simulation itself, and the only thing that closes the wound is lying in a
    /// bunk with food in the larder.
    /// </summary>
    [Fact]
    public void Mending_needs_both_the_bunk_and_the_food()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        PrototypeCreatureSnapshot? patient = null;
        while (!world.IsComplete && patient is null)
        {
            world.Step();
            patient = world.GetSnapshot().Creatures
                .FirstOrDefault(creature => creature.Injury == InjuryKind.Light);
        }

        Assert.NotNull(patient);
        var start = patient;
        var restedTicks = 0;
        var healedHp = start.Hp;
        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot().Creatures.Single(creature => creature.Id == start.Id);
            if (current.Mode == CreatureMode.Resting &&
                current.Satiety >= PrototypeTuning.RecoveryMinSatiety)
            {
                restedTicks++;
            }

            healedHp = Math.Max(healedHp, current.Hp);
            if (current.Injury == InjuryKind.None)
            {
                break;
            }
        }

        Assert.True(healedHp > start.Hp, "the patient never regained a point of health");
        Assert.True(
            restedTicks >= PrototypeTuning.HpRecoveryPeriod,
            $"the patient only rested {restedTicks} ticks");
    }

    /// <summary>
    /// Criterion 7. Reach is read out of the tuning layer by combat resolution,
    /// so the rule is stated in terms of the parameter rather than of the number
    /// one: no blow ever lands from further away than the parameter allows, and
    /// a target that is exactly at the parameter's distance is hit rather than
    /// walked towards.
    /// </summary>
    [Fact]
    public void An_attack_reaches_exactly_as_far_as_the_tuning_layer_says()
    {
        Assert.True(PrototypeTuning.MeleeAttackRange >= 1);
        Assert.True(PrototypeTuning.RaiderAttackRange >= 1);

        var world = new PrototypeWorld(LoadFixture("prepared"));
        var blows = 0;
        while (!world.IsComplete)
        {
            var before = world.GetSnapshot();
            world.Step();
            var after = world.GetSnapshot();
            foreach (var creature in after.Creatures
                         .Where(creature => creature.LastDecision.Tick == before.Tick &&
                             creature.LastDecision.ReasonCode == "combat_attack"))
            {
                var raiderId = creature.LastDecision.Details["raiderId"];
                var target = before.Raiders.Single(raider => raider.Id == raiderId);
                var attacker = after.Creatures.Single(item => item.Id == creature.Id);
                var distance = Math.Abs(attacker.Position.X - target.Position.X) +
                    Math.Abs(attacker.Position.Y - target.Position.Y);
                Assert.InRange(distance, 0, PrototypeTuning.MeleeAttackRange);
                blows++;
            }
        }

        Assert.True(blows > 0, "the prepared party never landed a blow");
    }

    [Fact]
    public void A_party_ends_by_itself_once_the_last_wave_is_resolved()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        world.RunTicks(PrototypeTuning.SessionTicks);
        var state = world.GetSnapshot();

        Assert.True(world.IsComplete);
        Assert.True(state.Tick < PrototypeTuning.SessionTicks, "the party ran into the fuse");
        Assert.Equal("raided", state.SessionResult.Outcome);
        Assert.False(state.SessionResult.Unresolved);
        Assert.Equal(state.Waves.Count, state.SessionResult.WavesResolved);
        Assert.Equal(state.Tick - 1, state.SessionResult.EndTick);
        Assert.Throws<InvalidOperationException>(world.Step);
    }

    /// <summary>
    /// The end of a party has three forms, and the line between the two that
    /// survive is drawn on whether every wave was actually repelled. A domain
    /// that let a wave through is `raided` however many portions happened to be
    /// left in the larder for it to lose — otherwise an already empty pantry
    /// would be reported as a victory.
    /// </summary>
    [Fact]
    public void Surviving_a_wave_that_got_through_is_reported_as_raided_and_not_as_held()
    {
        var state = PrototypeScenario.Run(LoadFixture("baseline"), PrototypeTuning.SessionTicks).State;

        Assert.Equal("raided", state.SessionResult.Outcome);
        Assert.Contains(
            state.Waves,
            wave => wave.Outcome is "larder_raided" or "overrun");
        Assert.DoesNotContain(state.Waves, wave => wave.Outcome is null);

        // `held` is reachable only when nothing got through, which is exactly
        // what the four wave outcomes already say.
        var repelled = state.Waves.Count(
            wave => wave.Outcome is "repelled_clean" or "repelled_costly");
        Assert.True(repelled < state.Waves.Count);
        Assert.Equal(repelled, state.SessionResult.WavesRepelled);
    }

    /// <summary>
    /// The mirror must not flatter. A domain dying of hunger used to report the
    /// best strength of its whole party, because inborn might and drilled form
    /// survive starvation on paper; the summary then read "renown 4 against
    /// strength 86" at the very moment the domain died, which is the one place
    /// the panel actively misled — and it did it in the negative example.
    /// </summary>
    [Fact]
    public void A_starving_domain_cannot_report_its_best_strength_of_the_party()
    {
        var dying = new PrototypeWorld(LoadFixture("neglected"));
        var peak = dying.GetSnapshot().Domain.Strength;
        while (!dying.IsComplete)
        {
            dying.Step();
            peak = Math.Max(peak, dying.GetSnapshot().Domain.Strength);
        }

        var fall = dying.GetSnapshot();
        Assert.Equal("fallen", fall.SessionResult.Outcome);
        Assert.True(
            fall.Domain.Strength < peak,
            $"strength at the fall {fall.Domain.Strength} is not below the peak {peak}");

        // ... and it is below what the domains that were still alive showed on
        // that very tick, so the number ranks the three the way a player would.
        var atTheSameTick = fall.Tick;
        var baseline = PrototypeScenario.Run(LoadFixture("baseline"), atTheSameTick).State;
        var prepared = PrototypeScenario.Run(LoadFixture("prepared"), atTheSameTick).State;
        Assert.True(
            fall.Domain.Strength < baseline.Domain.Strength &&
            fall.Domain.Strength < prepared.Domain.Strength,
            $"at tick {atTheSameTick}: fallen={fall.Domain.Strength}, " +
            $"baseline={baseline.Domain.Strength}, prepared={prepared.Domain.Strength}");
    }

    /// <summary>
    /// Strength leaves out creatures the fight itself would turn away. The tick
    /// is chosen so the rule actually discriminates: `neglected` at 450 is still
    /// a living domain, but part of it is already too hungry to be let into a
    /// fight and the rest is not.
    ///
    /// What is asserted is the consequence of the filter, not the filter: that
    /// the published strength is strictly below what the same creatures would
    /// add up to if everyone still standing were counted, and that each excluded
    /// creature would have added something. Remove the filter from the
    /// simulation and both fail; restate the formula here and neither would.
    /// </summary>
    [Fact]
    public void Strength_leaves_out_those_the_fight_would_turn_away()
    {
        const int discriminatingTick = 450;
        var state = PrototypeScenario.Run(LoadFixture("neglected"), discriminatingTick).State;
        Assert.Null(state.SessionResult.Outcome);

        int Potential(PrototypeCreatureSnapshot creature) =>
            (creature.Might * PrototypeTuning.StrengthPerMight +
             creature.MartialForm / PrototypeTuning.StrengthMartialDivisor) *
            creature.Readiness / PrototypeTuning.StrengthReadinessScale;

        var standing = state.Creatures
            .Where(creature => creature.Mode != CreatureMode.Downed)
            .ToArray();
        var turnedAway = standing
            .Where(creature => creature.Satiety < PrototypeTuning.CombatMinSatiety)
            .ToArray();

        // The tick has to contain both kinds and the excluded ones have to be
        // worth something, otherwise the assertion below would pass on a domain
        // the rule never touched. Individually some of them round to nothing —
        // a creature of might 2 with no training and readiness 19 contributes
        // zero either way — so what has to be non-zero is their total.
        Assert.NotEmpty(turnedAway);
        Assert.NotEmpty(standing.Except(turnedAway));
        Assert.True(
            turnedAway.Sum(Potential) > 0,
            "at this tick everyone the fight would turn away would have added " +
            "nothing anyway, so the tick cannot witness the exclusion");

        Assert.True(
            state.Domain.Strength < standing.Sum(Potential),
            $"strength {state.Domain.Strength} is not below the {standing.Sum(Potential)} " +
            "that counting everyone still standing would give");
    }

    [Fact]
    public void A_domain_that_starves_itself_falls_and_the_party_stops_there()
    {
        var state = PrototypeScenario.Run(
            LoadFixture("neglected"),
            PrototypeTuning.SessionTicks).State;

        Assert.Equal("fallen", state.SessionResult.Outcome);
        Assert.True(state.Tick < PrototypeTuning.FirstRaidTick);
        Assert.Equal(0, state.Stocks.Meals);
        Assert.All(
            state.Creatures,
            creature => Assert.True(
                creature.Mode == CreatureMode.Downed ||
                creature.Satiety < PrototypeTuning.CollapseThreshold));
    }

    /// <summary>
    /// Criterion 8. The same party, replayed from the same command log, has to
    /// land on the same canonical state whatever the shape of the run: in one
    /// call, one tick at a time, or in uneven chunks.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    [InlineData("neglected")]
    public void A_party_of_waves_replays_byte_for_byte_however_it_is_stepped(string fixtureName)
    {
        var whole = PrototypeScenario.Run(LoadFixture(fixtureName), PrototypeTuning.SessionTicks);

        var stepped = new PrototypeWorld(LoadFixture(fixtureName));
        while (!stepped.IsComplete)
        {
            stepped.Step();
        }

        var chunked = new PrototypeWorld(LoadFixture(fixtureName));
        foreach (var chunk in new[] { 7, 293, 1_000, 411, 989 })
        {
            chunked.RunTicks(Math.Min(chunk, PrototypeTuning.SessionTicks - chunked.CurrentTick));
        }

        while (!chunked.IsComplete)
        {
            chunked.Step();
        }

        var steppedResult = PrototypeScenario.Capture(stepped);
        var chunkedResult = PrototypeScenario.Capture(chunked);
        Assert.Equal(whole.Checksum, steppedResult.Checksum);
        Assert.Equal(whole.Checksum, chunkedResult.Checksum);
        Assert.Equal(whole.CanonicalJson, steppedResult.CanonicalJson);
        Assert.Equal(whole.CanonicalEventLog, chunkedResult.CanonicalEventLog);
        Assert.Equal(whole.Tick, steppedResult.Tick);
        Assert.Equal(whole.Tick, chunkedResult.Tick);
    }

    /// <summary>
    /// The trend the HUD draws is canonical state and not a panel's memory, so a
    /// headless check and the panel can never disagree about which way the arrow
    /// points.
    /// </summary>
    [Fact]
    public void The_domain_summary_carries_both_numbers_and_their_value_at_the_previous_wave()
    {
        var world = new PrototypeWorld(LoadFixture("prepared"));
        var beforeAnyWave = world.GetSnapshot().Domain;
        Assert.Null(beforeAnyWave.RenownAtPreviousWave);
        Assert.Null(beforeAnyWave.StrengthAtPreviousWave);
        Assert.Equal(9, beforeAnyWave.LivingCreatures);

        while (!world.IsComplete && world.GetSnapshot().Domain.WavesArrived == 0)
        {
            world.Step();
        }

        var atFirstWave = world.GetSnapshot().Domain;
        Assert.Equal(atFirstWave.Renown, atFirstWave.RenownAtPreviousWave);
        Assert.Equal(atFirstWave.Strength, atFirstWave.StrengthAtPreviousWave);

        while (!world.IsComplete && world.GetSnapshot().Domain.WavesArrived < 2)
        {
            world.Step();
        }

        // The baseline the arrow measures from moved on to the second wave, and
        // it is a sample taken at that wave rather than a running value. How the
        // strength itself is computed is asserted by
        // Strength_counts_only_those_who_could_answer_the_call; restating the
        // formula here would only make two copies of it to keep in step.
        var atSecondWave = world.GetSnapshot().Domain;
        Assert.True(atSecondWave.RenownAtPreviousWave > atFirstWave.RenownAtPreviousWave);
        Assert.NotEqual(atSecondWave.StrengthAtPreviousWave, atFirstWave.StrengthAtPreviousWave);
    }

    private static PrototypeCommandLog LoadFixture(string name)
    {
        return PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "scenarios",
            "prototype1",
            $"{name}.commands.v2.json"));
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
}
