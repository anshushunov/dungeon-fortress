using System.Text;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

public sealed class PrototypeScenarioTests
{
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    [InlineData("neglected")]
    public void Same_seed_commands_and_tick_produce_identical_state_log_and_checksum(
        string fixtureName)
    {
        var commands = LoadFixture(fixtureName);
        var first = PrototypeScenario.Run(commands, PrototypeTuning.FirstRaidTick + 1);
        var second = PrototypeScenario.Run(commands, PrototypeTuning.FirstRaidTick + 1);

        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.CanonicalEventLog, second.CanonicalEventLog);
        Assert.Equal(first.Checksum, second.Checksum);
    }

    [Fact]
    public void Scenario_label_does_not_change_canonical_state()
    {
        const string template =
            """
            {"schemaVersion":2,"scenario":"SCENARIO","seed":42,"commands":[]}
            """;
        var baseline = PrototypeCommandDocument.Parse(
            Encoding.UTF8.GetBytes(template.Replace("SCENARIO", "baseline")));
        var custom = PrototypeCommandDocument.Parse(
            Encoding.UTF8.GetBytes(template.Replace("SCENARIO", "custom")));

        Assert.Equal(
            PrototypeScenario.Run(baseline, 64).Checksum,
            PrototypeScenario.Run(custom, 64).Checksum);
    }

    [Fact]
    public void Changing_seed_changes_canonical_state()
    {
        var fixture = LoadFixture("baseline");
        var changed = fixture with { Seed = fixture.Seed + 1 };

        var original = PrototypeScenario.Run(fixture, 128);
        var alternate = PrototypeScenario.Run(changed, 128);

        Assert.NotEqual(
            original.State.Creatures
                .Select(creature => (creature.Satiety, creature.Fatigue))
                .ToArray(),
            alternate.State.Creatures
                .Select(creature => (creature.Satiety, creature.Fatigue))
                .ToArray());
        Assert.NotEqual(original.State.Events, alternate.State.Events);
    }

    [Fact]
    public void Changing_a_relevant_indirect_command_changes_canonical_state()
    {
        const string baselineJson =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[]}
            """;
        const string changedJson =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[
              {"tick":0,"kind":"set_priority","jobKind":"Harvest","value":0}
            ]}
            """;

        var baseline = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(baselineJson));
        var changed = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(changedJson));
        Assert.NotEqual(
            PrototypeScenario.Run(baseline, 128).Checksum,
            PrototypeScenario.Run(changed, 128).Checksum);
    }

    [Fact]
    public void Tile_order_step_size_and_current_culture_do_not_change_state()
    {
        const string firstJson =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[
              {"tick":0,"kind":"zone_paint","zoneKind":"Watch","tiles":[[3,2],[2,2]]}
            ]}
            """;
        const string secondJson =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[
              {"tick":0,"kind":"zone_paint","zoneKind":"Watch","tiles":[[2,2],[3,2]]}
            ]}
            """;
        var firstLog = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(firstJson));
        var secondLog = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(secondJson));
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            var first = new PrototypeWorld(firstLog);
            first.RunTicks(128);

            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            var second = new PrototypeWorld(secondLog);
            for (var tick = 0; tick < 128; tick++)
            {
                second.Step();
            }

            Assert.Equal(
                PrototypeScenario.Capture(first).CanonicalJson,
                PrototypeScenario.Capture(second).CanonicalJson);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Gameplay_v2_rejects_addressing_unknown_fields_and_invalid_bounds()
    {
        var addressed =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"set_priority","jobKind":"Harvest","value":3,"creatureId":4}
            ]}
            """;
        var outOfBounds =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"zone_paint","zoneKind":"Farm","tiles":[[28,1]]}
            ]}
            """;
        var legacy =
            """
            {"schemaVersion":1,"scenario":"custom","seed":1,"commands":[]}
            """;

        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(addressed)));
        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(outOfBounds)));
        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(legacy)));
    }

    [Fact]
    public void Semantic_error_rejects_the_whole_document_before_world_creation()
    {
        const string json =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"zone_erase","zoneKind":"Larder",
               "tiles":[[14,7],[15,7]]}
            ]}
            """;
        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json)));

        var direct = new PrototypeCommandLog(
            "custom",
            1,
            [
                new SetPriorityCommand(0, JobKind.Harvest, 4),
                new ZoneEraseCommand(
                    1,
                    ZoneKind.Larder,
                    [new(14, 7), new(15, 7)]),
            ]);
        Assert.Throws<InvalidDataException>(() => new PrototypeWorld(direct));
    }

    [Fact]
    public void Invalid_later_command_rejects_the_whole_document_before_a_world_exists()
    {
        const string json =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"set_priority","jobKind":"Harvest","value":4},
              {"tick":1,"kind":"direct_order","creatureId":2}
            ]}
            """;

        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Baseline_completes_the_economic_chain_and_exposes_structured_state()
    {
        var result = PrototypeScenario.Run(
            LoadFixture("baseline"),
            PrototypeTuning.FirstRaidTick + 1);

        Assert.True(result.State.Stocks.MealsProduced > 0);
        Assert.True(result.State.Stocks.MealsEaten > 0);
        Assert.True(result.State.Economy.HarvestsCompleted > 0);
        Assert.True(result.State.Economy.RawHaulsCompleted > 0);
        Assert.True(result.State.Economy.CookBatchesCompleted > 0);
        Assert.True(result.State.Economy.MealHaulsCompleted > 0);
        Assert.True(result.State.Economy.MealsEaten > PrototypeTuning.StartMeals);
        Assert.Equal(9, result.State.Creatures.Count);
        Assert.All(result.State.Creatures, creature =>
        {
            Assert.False(string.IsNullOrWhiteSpace(creature.Name));
            Assert.False(string.IsNullOrWhiteSpace(creature.LastDecision.ReasonCode));
            Assert.NotNull(creature.ReadinessAtRaid);
        });
        Assert.Contains(result.State.Events, @event => @event.ReasonCode == "chosen_need_hunger");
        Assert.Contains(
            result.State.Events,
            @event => @event.JobKind is not null);
    }

    /// <summary>
    /// The contract corridors of 13.4, re-measured for a party of waves. The
    /// comparison moment is the arrival of the first wave, because that is the
    /// last tick at which all three scenarios are still comparable: after it the
    /// three domains stop living the same life.
    ///
    /// <c>neglected</c> no longer reaches a wave at all. Forbidding the harvest
    /// empties the larder by tick ~500 and leaves nobody above the exhaustion
    /// threshold to refill it, which is now stated as an end of the party
    /// instead of being played out as eight hundred ticks of standing still.
    /// </summary>
    [Fact]
    public void Contract_scenarios_satisfy_the_precombat_invariants_of_a_wave_party()
    {
        var baseline = PrototypeScenario.Run(
            LoadFixture("baseline"),
            PrototypeTuning.FirstRaidTick + 1);
        var prepared = PrototypeScenario.Run(
            LoadFixture("prepared"),
            PrototypeTuning.FirstRaidTick + 1);
        var neglected = PrototypeScenario.Run(
            LoadFixture("neglected"),
            PrototypeTuning.SessionTicks);
        var baselineEnd = PrototypeScenario.Run(
            LoadFixture("baseline"),
            PrototypeTuning.SessionTicks);
        var preparedEnd = PrototypeScenario.Run(
            LoadFixture("prepared"),
            PrototypeTuning.SessionTicks);

        var readiness = (
            Baseline: AverageReadiness(baseline),
            Prepared: AverageReadiness(prepared));
        Assert.True(
            readiness.Prepared > readiness.Baseline,
            Describe(baseline, prepared, neglected, readiness));
        Assert.All(baseline.State.Creatures, creature => Assert.Equal(0, creature.MartialForm));
        Assert.Contains(
            neglected.State.Events,
            @event => @event.ReasonCode == "refused_rule_min_satiety");
        Assert.Contains(
            neglected.State.Events,
            @event => @event.ReasonCode == "refused_too_exhausted");
        Assert.Contains(
            prepared.State.Events,
            @event => @event.ReasonCode == "chosen_muster");
        Assert.Contains(
            prepared.State.Events,
            @event => @event.ReasonCode == "chosen_ration");
        Assert.Contains(
            preparedEnd.State.Events,
            @event => @event.ReasonCode == "combat_refused_starving");
        Assert.InRange(baseline.State.Creatures.Average(c => c.Satiety), 45, 75);
        Assert.InRange(prepared.State.Creatures.Average(c => c.Satiety), 45, 75);
        Assert.InRange(neglected.State.Creatures.Average(c => c.Satiety), 0, 15);
        Assert.InRange(readiness.Baseline, 38, 58);
        Assert.InRange(readiness.Prepared, 58, 78);
        Assert.True(prepared.State.Creatures.Average(c => c.MartialForm) >= 60);
        Assert.True(
            baselineEnd.State.Stocks.MealsProduced is >= 95 and <= 130 &&
            preparedEnd.State.Stocks.MealsProduced is >= 90 and <= 125 &&
            neglected.State.Stocks.MealsProduced is >= 0 and <= 6,
            $"end production baseline={baselineEnd.State.Stocks.MealsProduced}, " +
            $"prepared={preparedEnd.State.Stocks.MealsProduced}, " +
            $"neglected={neglected.State.Stocks.MealsProduced}");

        // Neither fixture holds the domain: both let a wave through, so both end
        // `raided`. That is the honest reading of what they actually did, and it
        // is why the third end form exists.
        Assert.Equal("raided", baselineEnd.State.SessionResult.Outcome);
        Assert.Equal("raided", preparedEnd.State.SessionResult.Outcome);
        Assert.Equal("fallen", neglected.State.SessionResult.Outcome);
        AssertEndOfPartyInvariants(baselineEnd.State, preparedEnd.State, neglected.State);
        AssertFirstWaveInvariants(baseline.State, prepared.State);
    }

    /// <summary>
    /// Contract 13.4 says its invariants hold "for any seed of the matrix". That
    /// sentence used to be prose while the test ran one seed, which is how a
    /// broken invariant survived: `renown(prepared) > renown(baseline)` is false
    /// on seed 20260727 and nothing said so. The claim is now executed.
    /// </summary>
    [Theory]
    [InlineData(20_260_726UL)]
    [InlineData(20_260_727UL)]
    [InlineData(20_260_728UL)]
    public void The_contract_invariants_hold_on_every_seed_of_the_matrix(ulong seed)
    {
        AssertEndOfPartyInvariants(
            RunAtSeed("baseline", seed, PrototypeTuning.SessionTicks),
            RunAtSeed("prepared", seed, PrototypeTuning.SessionTicks),
            RunAtSeed("neglected", seed, PrototypeTuning.SessionTicks));
        AssertFirstWaveInvariants(
            RunAtSeed("baseline", seed, PrototypeTuning.FirstRaidTick + 1),
            RunAtSeed("prepared", seed, PrototypeTuning.FirstRaidTick + 1));
    }

    /// <summary>
    /// The invariants read where the fixtures are still comparable: the arrival
    /// of the first wave. Both are condition numbers, and condition is only
    /// comparable at the same moment of the same story — read at the end of the
    /// party, every fixture is starving and every number collapses towards zero.
    /// </summary>
    private static void AssertFirstWaveInvariants(
        PrototypeSnapshot baseline,
        PrototypeSnapshot prepared)
    {
        Assert.True(
            AverageReadiness(prepared) > AverageReadiness(baseline),
            $"readiness at wave 1 prepared={AverageReadiness(prepared)}, " +
            $"baseline={AverageReadiness(baseline)}");
        Assert.True(
            prepared.Domain.Strength > baseline.Domain.Strength,
            $"strength at wave 1 prepared={prepared.Domain.Strength}, " +
            $"baseline={baseline.Domain.Strength}");
    }

    /// <summary>
    /// The invariants of 13.4 read at the end of the party. What is deliberately
    /// absent is as load-bearing as what is here: renown ranks a domain that
    /// lives against one that died, and does not reliably rank two living plans
    /// on a single party, because it leans on raiders put down and that number
    /// swings with combat jitter. Ranking the plans is the party score's job
    /// now, and it is asserted here rather than described in prose — that is
    /// the whole point of ADR 0016.
    /// </summary>
    private static void AssertEndOfPartyInvariants(
        PrototypeSnapshot baseline,
        PrototypeSnapshot prepared,
        PrototypeSnapshot neglected)
    {
        Assert.True(
            baseline.Domain.Renown > neglected.Domain.Renown &&
            prepared.Domain.Renown > neglected.Domain.Renown,
            $"renown baseline={baseline.Domain.Renown}, prepared={prepared.Domain.Renown}, " +
            $"neglected={neglected.Domain.Renown}");

        // The invariant renown could not carry, returned in its own number: a
        // party that survived outscores one that died, and preparation outscores
        // living as one always did. Both are read on the same seed, because a
        // plan is only comparable with the same combat rolls behind it.
        var score = (
            Baseline: Score(baseline),
            Prepared: Score(prepared),
            Neglected: Score(neglected));
        Assert.True(
            score.Baseline > score.Neglected && score.Prepared > score.Neglected,
            $"score baseline={score.Baseline}, prepared={score.Prepared}, " +
            $"neglected={score.Neglected}");
        Assert.True(
            score.Prepared > score.Baseline,
            $"score prepared={score.Prepared} must beat baseline={score.Baseline}; " +
            $"repelled {prepared.SessionResult.WavesRepelled}/{baseline.SessionResult.WavesRepelled}, " +
            $"stolen {prepared.SessionResult.MealsStolen}/{baseline.SessionResult.MealsStolen}, " +
            $"defenders lost " +
            $"{prepared.SessionResult.DefendersDowned + prepared.SessionResult.DefendersFled}/" +
            $"{baseline.SessionResult.DefendersDowned + baseline.SessionResult.DefendersFled}");

        // Preparation buys the price of the raid, not attendance at it. The
        // comparison excludes `neglected` on purpose: it never meets a wave, so
        // its zeroes are an absence of the event and not a better result.
        Assert.True(
            prepared.SessionResult.MealsStolen < baseline.SessionResult.MealsStolen,
            $"meals stolen prepared={prepared.SessionResult.MealsStolen}, " +
            $"baseline={baseline.SessionResult.MealsStolen}");
        Assert.True(
            CountEvents(prepared, "combat_fled_morale") < CountEvents(baseline, "combat_fled_morale"),
            $"broken by morale prepared={CountEvents(prepared, "combat_fled_morale")}, " +
            $"baseline={CountEvents(baseline, "combat_fled_morale")}");
    }

    /// <summary>
    /// The score of a party that ended. A party without one has not ended, and
    /// reading it as a zero would rank an interrupted run against finished ones.
    /// </summary>
    private static int Score(PrototypeSnapshot state) =>
        state.SessionResult.Score ??
        throw new InvalidOperationException(
            $"The party did not end (outcome {state.SessionResult.Outcome ?? "null"}, " +
            $"unresolved {state.SessionResult.Unresolved}), so it has no score to compare.");

    private static int CountEvents(PrototypeSnapshot state, string reasonCode) =>
        state.Events.Where(@event => @event.ReasonCode == reasonCode).Sum(@event => @event.Repeats);

    private static PrototypeSnapshot RunAtSeed(string fixtureName, ulong seed, int ticks) =>
        PrototypeScenario.Run(LoadFixture(fixtureName) with { Seed = seed }, ticks).State;

    private static int AverageReadiness(PrototypeSnapshot state) =>
        (int)state.Creatures.Average(creature => creature.ReadinessAtRaid!.Value);

    [Fact]
    public void Replay_from_loaded_command_log_is_byte_identical()
    {
        var path = FixturePath("prepared");
        var first = PrototypeScenario.Run(
            PrototypeCommandDocument.Load(path),
            PrototypeTuning.FirstRaidTick + 1);
        var replay = PrototypeScenario.Run(
            PrototypeCommandDocument.Load(path),
            PrototypeTuning.FirstRaidTick + 1);

        Assert.Equal(first.CanonicalJson, replay.CanonicalJson);
    }

    [Fact]
    public void Performance_sanity_completes_three_full_parties()
    {
        var results = new List<PrototypeRunResult>();
        foreach (var scenario in new[] { "baseline", "prepared", "neglected" })
        {
            results.Add(PrototypeScenario.Run(
                LoadFixture(scenario),
                PrototypeTuning.SessionTicks));
        }

        Assert.Equal(3, results.Count);
        // A party ends on its own tick, so what is asserted is that it ended at
        // all and inside the fuse — not that all three ended together.
        Assert.All(results, result => Assert.NotNull(result.State.SessionResult.Outcome));
        Assert.All(results, result => Assert.InRange(result.Tick, 1, PrototypeTuning.SessionTicks));
    }

    [Fact]
    public void Preparation_changes_the_deterministic_party_without_direct_orders()
    {
        var prepared = PrototypeScenario.Run(LoadFixture("prepared"), PrototypeTuning.SessionTicks);
        var baseline = PrototypeScenario.Run(LoadFixture("baseline"), PrototypeTuning.SessionTicks);
        var preparedReplay = PrototypeScenario.Run(LoadFixture("prepared"), PrototypeTuning.SessionTicks);

        Assert.Equal(
            prepared.State.Waves.Where(wave => wave.Arrived).Sum(wave => wave.RaiderCount),
            prepared.State.Raiders.Count);
        Assert.Equal(
            baseline.State.Waves.Where(wave => wave.Arrived).Sum(wave => wave.RaiderCount),
            baseline.State.Raiders.Count);
        Assert.All(prepared.State.Waves, wave => Assert.NotNull(wave.Outcome));
        Assert.All(baseline.State.Waves, wave => Assert.NotNull(wave.Outcome));
        Assert.True(
            prepared.State.SessionResult.RaidersDowned >
            baseline.State.SessionResult.RaidersDowned,
            $"prepared put down {prepared.State.SessionResult.RaidersDowned} raiders, " +
            $"baseline {baseline.State.SessionResult.RaidersDowned}");
        Assert.True(
            prepared.State.Creatures.Average(creature => creature.ReadinessAtRaid!.Value) >
            baseline.State.Creatures.Average(creature => creature.ReadinessAtRaid!.Value));
        Assert.Equal(prepared.Checksum, preparedReplay.Checksum);
    }

    [Fact]
    public void Raid_steals_one_meal_per_period_and_preserves_meal_accounting()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        var theftTicks = new Dictionary<int, List<int>>();
        var previousCarrying = new Dictionary<int, int>();

        while (!world.IsComplete)
        {
            world.Step();
            var state = world.GetSnapshot();
            foreach (var raider in state.Raiders)
            {
                var before = previousCarrying.GetValueOrDefault(raider.Id);
                if (raider.CarryingMeals > before)
                {
                    Assert.Equal(before + 1, raider.CarryingMeals);
                    Assert.Equal(0, raider.StealTicks);
                    if (!theftTicks.TryGetValue(raider.Id, out var ticks))
                    {
                        ticks = [];
                        theftTicks.Add(raider.Id, ticks);
                    }
                    ticks.Add(state.Tick);
                }

                previousCarrying[raider.Id] = raider.CarryingMeals;
            }
        }

        Assert.NotEmpty(theftTicks.SelectMany(pair => pair.Value));
        Assert.All(theftTicks.Values, ticks =>
            Assert.All(ticks.Zip(ticks.Skip(1)), pair =>
                Assert.True(pair.Second - pair.First >= PrototypeTuning.StealPeriod)));

        var result = world.GetSnapshot();
        var looseMeals = result.LooseItems
            .Where(item => item.Resource == ResourceKind.Meal)
            .Sum(item => item.Quantity);
        Assert.Equal(
            PrototypeTuning.StartMeals + result.Stocks.MealsProduced,
            result.Stocks.Meals + looseMeals + result.Stocks.MealsEaten + result.SessionResult.MealsStolen);
        Assert.NotNull(result.SessionResult.Outcome);
    }

    [Fact]
    public void Defender_max_hp_comes_from_might_tuning()
    {
        var state = PrototypeScenario.Run(LoadFixture("prepared"), PrototypeTuning.FirstRaidTick).State;
        Assert.All(state.Creatures, creature =>
            Assert.Equal(
                PrototypeTuning.DefenderHpBase + creature.Might * PrototypeTuning.DefenderHpPerMight,
                creature.MaxHp));
    }

    /// <summary>
    /// A raider that reaches an empty larder turns round instead of standing
    /// there. The witness used to be the <c>neglected</c> fixture, whose larder
    /// was empty before the raid ever started; that domain now falls from hunger
    /// long before a wave arrives, so the branch is witnessed where it actually
    /// happens in a party — in a later wave, after an earlier one has carried
    /// the larder away.
    /// </summary>
    [Fact]
    public void Empty_larder_raider_turns_back_to_the_gate_instead_of_waiting()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        var observedReturn = false;

        while (!world.IsComplete && !observedReturn)
        {
            world.Step();
            var beforeReturn = world.GetSnapshot();
            var raider = beforeReturn.Raiders.FirstOrDefault(item =>
                item.Mode == RaiderMode.Raiding &&
                !item.ReturningToGate &&
                item.CarryingMeals == 0 &&
                item.Position == new GridPoint(14, 7) &&
                beforeReturn.Stocks.Meals == 0);
            if (raider is null)
            {
                continue;
            }

            world.Step();
            var afterReturn = world.GetSnapshot();
            var moved = afterReturn.Raiders.Single(item => item.Id == raider.Id);
            Assert.Equal(RaiderMode.Raiding, moved.Mode);
            Assert.True(moved.ReturningToGate);
            Assert.Equal(0, moved.CarryingMeals);
            observedReturn = true;
        }

        Assert.True(observedReturn, "Baseline party did not reach the empty-larder return branch.");
    }

    private static int AverageReadiness(PrototypeRunResult result)
    {
        return (int)result.State.Creatures.Average(creature => creature.ReadinessAtRaid!.Value);
    }

    private static string Describe(
        PrototypeRunResult baseline,
        PrototypeRunResult prepared,
        PrototypeRunResult neglected,
        (int Baseline, int Prepared) readiness)
    {
        static string One(PrototypeRunResult result, int ready) =>
            $"ready={ready},sat={result.State.Creatures.Average(c => c.Satiety):F1}," +
            $"fat={result.State.Creatures.Average(c => c.Fatigue):F1}," +
            $"form={result.State.Creatures.Average(c => c.MartialForm):F1}," +
            $"made={result.State.Stocks.MealsProduced},ate={result.State.Stocks.MealsEaten}," +
            $"meals={result.State.Stocks.Meals},raw={result.State.Stocks.RawMushroom}," +
            $"looseRaw={result.State.Stocks.LooseRawMushroom}";
        return $"baseline[{One(baseline, readiness.Baseline)}] " +
            $"prepared[{One(prepared, readiness.Prepared)}] " +
            $"neglected[{One(neglected, 0)}]";
    }

    private static PrototypeCommandLog LoadFixture(string name)
    {
        return PrototypeCommandDocument.Load(FixturePath(name));
    }

    private static string FixturePath(string name)
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "scenarios",
            "prototype1",
            $"{name}.commands.v2.json");
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
