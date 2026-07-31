using System.Text;
using System.Text.Json;

using DungeonFortress.Scenarios;
using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

public sealed class PrototypeContractRegressionTests
{
    [Theory]
    [InlineData("""{"schemaVersion":2,"scenario":"custom","commands":[]}""")]
    [InlineData("""{"schemaVersion":2,"scenario":"custom","seed":1}""")]
    [InlineData(
        """{"schemaVersion":2,"scenario":"custom","seed":1,"commands":[{"kind":"set_priority","jobKind":"Harvest","value":3}]}""")]
    [InlineData(
        """{"schemaVersion":2,"scenario":"custom","seed":1,"commands":[{"tick":0,"kind":"set_priority","jobKind":"Harvest"}]}""")]
    public void Missing_required_fields_are_rejected_as_invalid_data(string json)
    {
        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Sequential_semantic_preflight_rejects_a_late_invalid_larder_state()
    {
        const string json =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"zone_erase","zoneKind":"Larder","tiles":[[14,7]]},
              {"tick":900,"kind":"zone_erase","zoneKind":"Larder","tiles":[[15,7]]}
            ]}
            """;

        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("--seed", "2")]
    [InlineData("--agents", "9")]
    public void Prototype_cli_rejects_explicit_legacy_authority(
        string option,
        string value)
    {
        var result = CaptureConsole(() => Program.Main(
        [
            "--prototype",
            "--commands",
            FixturePath("baseline"),
            "--ticks",
            "0",
            option,
            value,
        ]));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not accepted", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_cli_still_accepts_explicit_seed_and_agent_count()
    {
        var result = CaptureConsole(() => Program.Main(
        [
            "--seed",
            "2",
            "--agents",
            "9",
            "--ticks",
            "0",
        ]));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"event\":\"scenario_result\"", result.Output);
    }

    /// <summary>
    /// The headline `prototype_result` line is a derived report and may hold
    /// facts the canonical snapshot does not — but where it repeats a canonical
    /// fact it repeats its form. The score is a fact whose form is its presence:
    /// a party that has not ended has no score at all rather than an empty one
    /// (ADR 0016). Reflection over the summary record used to print
    /// `"Score": null` mid-party, which is the single form the decision rules
    /// out. The composition of the snapshot itself is pinned by
    /// <see cref="PrototypeSnapshotShapeTests"/>; this is the same rule in the
    /// other output form, and the versioning rule that ties them together is in
    /// `docs/engineering/PROTOTYPE_HEADLESS.md`.
    /// </summary>
    [Theory]
    [InlineData("baseline", 1, false)]
    [InlineData("neglected", PrototypeTuning.SessionTicks, true)]
    public void The_headline_result_line_carries_a_score_only_for_a_party_that_ended(
        string fixtureName,
        int ticks,
        bool partyEnded)
    {
        var result = CaptureConsole(() => Program.Main(
        [
            "--prototype",
            "--commands",
            FixturePath(fixtureName),
            "--ticks",
            ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ]));

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(
            result.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Last(line => line.Contains("prototype_result", StringComparison.Ordinal)));
        var sessionResult = document.RootElement.GetProperty("sessionResult");

        Assert.Equal(
            partyEnded,
            sessionResult.GetProperty("Outcome").ValueKind != JsonValueKind.Null);
        Assert.Equal(partyEnded, sessionResult.TryGetProperty("Score", out _));
        // The rest of the summary is carried all along and is unaffected: only
        // the score waits for the party to end.
        Assert.True(sessionResult.TryGetProperty("WavesRepelled", out _));
        Assert.True(sessionResult.TryGetProperty("MealsStolen", out _));
    }

    [Fact]
    public void Canonical_state_contains_pending_commands_and_all_future_counters()
    {
        var result = PrototypeScenario.Run(LoadFixture("prepared"), 500);
        using var document = JsonDocument.Parse(result.CanonicalJson);
        var root = document.RootElement;

        Assert.Equal(PrototypeCanonical.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.NotEmpty(root.GetProperty("pendingCommands").EnumerateArray());
        var creature = root.GetProperty("creatures")[0];
        foreach (var property in new[]
                 {
                     "carrying", "mealReserved", "musterTarget", "workTicks",
                     "watchTicks", "moveCount", "lastMoveTick", "blockedTicks",
                     "yieldCount", "lastYieldTick",
                 })
        {
            Assert.True(creature.TryGetProperty(property, out _), property);
        }

        var job = root.GetProperty("jobs").EnumerateArray().First();
        foreach (var property in new[]
                 {
                     "key", "origin", "target", "quantity", "personalCreatureId",
                     "reservedBy", "remainingTicks", "progressTicks", "pickedUp",
                 })
        {
            Assert.True(job.TryGetProperty(property, out _), property);
        }

        Assert.True(root.TryGetProperty("map", out _));
        Assert.True(root.TryGetProperty("digDesignations", out _));
        Assert.True(root.TryGetProperty("beds", out _));
        Assert.True(root.TryGetProperty("looseItems", out _));
        Assert.True(root.TryGetProperty("economy", out _));
        Assert.True(root.TryGetProperty("labor", out _));
        Assert.True(root.TryGetProperty("stations", out _));
    }

    [Fact]
    public void A_future_command_changes_the_current_checksum()
    {
        const string template =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[
              {"tick":100,"kind":"set_priority","jobKind":"Harvest","value":VALUE}
            ]}
            """;
        var first = PrototypeCommandDocument.Parse(
            Encoding.UTF8.GetBytes(template.Replace("VALUE", "1")));
        var second = PrototypeCommandDocument.Parse(
            Encoding.UTF8.GetBytes(template.Replace("VALUE", "4")));

        Assert.NotEqual(
            PrototypeScenario.Run(first, 0).Checksum,
            PrototypeScenario.Run(second, 0).Checksum);
    }

    [Fact]
    public void Baseline_observability_proves_conservation_labor_and_full_chain()
    {
        var result = PrototypeScenario.Run(
            LoadFixture("baseline"),
            PrototypeTuning.FirstRaidTick + 1);
        var state = result.State;
        var carriedRaw = state.Creatures
            .Where(creature => creature.Carrying == ResourceKind.RawMushroom)
            .Sum(creature => creature.CarryAmount);
        var carriedMeals = state.Creatures
            .Where(creature => creature.Carrying == ResourceKind.Meal)
            .Sum(creature => creature.CarryAmount);

        Assert.Equal(
            state.Economy.HarvestsCompleted * PrototypeTuning.HarvestOutput,
            state.Stocks.RawMushroom +
            state.Stocks.LooseRawMushroom +
            carriedRaw +
            state.Economy.CookBatchesCompleted * PrototypeTuning.CookInput);
        Assert.Equal(
            PrototypeTuning.StartMeals +
            state.Economy.CookBatchesCompleted * PrototypeTuning.CookOutput,
            state.Stocks.Meals +
            state.Stocks.LooseMeals +
            carriedMeals +
            state.Economy.MealsEaten);

        Assert.True(state.Economy.HarvestsCompleted > 0);
        Assert.True(state.Economy.RawHaulsCompleted > 0);
        Assert.True(state.Economy.CookBatchesCompleted > 0);
        Assert.True(state.Economy.MealHaulsCompleted > 0);
        Assert.True(state.Economy.MealsEaten > PrototypeTuning.StartMeals);
        Assert.Equal(
            state.Tick * state.Creatures.Count,
            state.Labor.FoodWorkTicks +
            state.Labor.RestTicks +
            state.Labor.EatTicks +
            state.Labor.DrillTicks +
            state.Labor.WatchTicks +
            state.Labor.DigTicks +
            state.Labor.StoneHaulTicks +
            state.Labor.MusterTicks +
            state.Labor.IdleTicks);
        Assert.InRange(state.Labor.FoodWorkPercent, 30, 70);
        Assert.Equal(
            state.Stations.Count,
            state.Stations.Select(station => station.Position).Distinct().Count());
    }

    [Fact]
    public void Priority_cancellation_drops_carried_stock_in_the_same_tick()
    {
        var scout = new PrototypeWorld(LoadFixture("baseline"));
        PrototypeSnapshot? carryingState = null;
        while (scout.CurrentTick < 500)
        {
            scout.Step();
            var snapshot = scout.GetSnapshot();
            if (snapshot.Creatures.Any(creature => creature.CarryAmount > 0))
            {
                carryingState = snapshot;
                break;
            }
        }

        Assert.NotNull(carryingState);
        var commandTick = carryingState.Tick;
        var carryingKinds = carryingState.Creatures
            .Where(creature => creature.CarryAmount > 0)
            .Select(creature => creature.CurrentJobId is { } jobId
                ? carryingState.Jobs.Single(job => job.JobId == jobId).Kind
                : throw new Xunit.Sdk.XunitException("Cargo has no owning job."))
            .Distinct()
            .ToArray();
        Assert.Single(carryingKinds);

        var world = new PrototypeWorld(
            new PrototypeCommandLog(
                "custom",
                carryingState.Seed,
                [new SetPriorityCommand(commandTick, carryingKinds[0], 0)]));
        world.RunTicks(commandTick);
        var before = world.GetSnapshot();
        var carriedBefore = before.Creatures.Sum(creature => creature.CarryAmount);
        var looseBefore = before.Stocks.LooseRawMushroom + before.Stocks.LooseMeals;
        Assert.True(carriedBefore > 0);

        world.Step();
        var after = world.GetSnapshot();

        Assert.Equal(0, after.Creatures.Sum(creature => creature.CarryAmount));
        Assert.Equal(
            looseBefore + carriedBefore,
            after.Stocks.LooseRawMushroom + after.Stocks.LooseMeals);
    }

    [Fact]
    public void A_reserved_meal_does_not_globally_block_cook_or_haul_work()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        var witnessed = false;
        for (var tick = 0; tick < PrototypeTuning.FirstRaidTick; tick++)
        {
            world.Step();
            var state = world.GetSnapshot();
            if (state.Creatures.Any(creature => creature.MealReserved) &&
                state.Creatures.Any(creature =>
                    creature.CurrentJobId is { } jobId &&
                    state.Jobs.Single(job => job.JobId == jobId).Kind is
                        JobKind.Cook or JobKind.Haul))
            {
                witnessed = true;
                break;
            }
        }

        Assert.True(
            witnessed,
            "The deterministic baseline never ran Cook/Haul while a meal was reserved.");
    }

    [Fact]
    public void Prepared_observability_has_bounded_post_occupancy()
    {
        var state = PrototypeScenario.Run(
            LoadFixture("prepared"),
            PrototypeTuning.FirstRaidTick + 1).State;

        Assert.True(state.Labor.PostOccupiedTicks > 0);
        Assert.True(state.Labor.PostCapacityTicks > 0);
        Assert.InRange(state.Labor.PostOccupancyPercent, 1, 80);
        Assert.Contains(
            state.Stations,
            station => station.Kind == TileKind.Kitchen && station.OccupiedTicks > 0);
        Assert.Contains(
            state.Stations,
            station => station.Kind == TileKind.Post && station.OccupiedTicks > 0);
    }

    /// <summary>
    /// One tile a tick, one creature a tile, and nobody walking through anybody.
    ///
    /// It reads the whole party rather than the run-up to the first wave, and the
    /// difference is not thoroughness for its own sake. Everything the domain did
    /// before tick 1301 was walking to work, and walking to work went through the
    /// one movement routine of the world, so the invariant could not fail there.
    /// The one thing in the prototype that assigned a position outright happened
    /// in combat — a defender broken by morale was placed at the far wall inside
    /// one tick — and this test ran out exactly one tick after the first wave
    /// landed, which is before anybody had lost their nerve. It witnessed nothing
    /// and passed (Issue #101). Reading to the end of the party is what turns it
    /// into evidence that flight is a walk.
    /// </summary>
    [Fact]
    public void Traffic_arbitration_preserves_one_move_no_overlap_and_no_swap()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        var previous = world.GetSnapshot();
        var flightsWitnessed = 0;
        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            Assert.Equal(
                current.Creatures.Count,
                current.Creatures.Select(creature => creature.Position).Distinct().Count());

            flightsWitnessed += current.Creatures.Count(creature =>
                creature.Mode == CreatureMode.Fled &&
                previous.Creatures.Single(item => item.Id == creature.Id).Mode != CreatureMode.Fled);

            foreach (var creature in current.Creatures)
            {
                var before = previous.Creatures.Single(item => item.Id == creature.Id);
                var distance = Math.Abs(creature.Position.X - before.Position.X) +
                    Math.Abs(creature.Position.Y - before.Position.Y);
                Assert.InRange(distance, 0, 1);
                Assert.InRange(creature.MoveCount - before.MoveCount, 0, 1);
            }

            foreach (var left in current.Creatures)
            {
                foreach (var right in current.Creatures.Where(item => item.Id > left.Id))
                {
                    var oldLeft = previous.Creatures.Single(item => item.Id == left.Id);
                    var oldRight = previous.Creatures.Single(item => item.Id == right.Id);
                    Assert.False(
                        left.Position == oldRight.Position &&
                        right.Position == oldLeft.Position);
                }
            }

            previous = current;
        }

        Assert.Contains(
            previous.Events,
            @event => @event.ReasonCode == "chosen_traffic_yield" &&
                @event.Details["dependencyCycle"] == 0);
        Assert.Contains(
            previous.Events,
            @event => @event.ReasonCode == "chosen_traffic_yield" &&
                @event.Details["dependencyCycle"] == 1);
        Assert.All(previous.Creatures, creature => Assert.True(creature.YieldCount > 0));
        // Reading to the end of the party is only evidence about flight if the
        // party contained some. It does — 22 on this fixture and seed — and the
        // floor is set well under that, because how many break is tuning and this
        // test is not the place that pins it.
        Assert.True(
            flightsWitnessed >= 5,
            $"only {flightsWitnessed} defenders broke in this party, which is too few " +
            "for the walk out of a fight to have been read at all.");
        // A soft fairness bound, not a rule: the corridor was re-measured over the
        // whole party rather than over the run-up to the first wave, and came out
        // at 30 against the 27 of the shorter window on `origin/main`. The bound
        // keeps roughly the proportion of slack it had before.
        Assert.InRange(
            previous.Creatures.Max(creature => creature.YieldCount) -
            previous.Creatures.Min(creature => creature.YieldCount),
            0,
            40);
    }

    [Fact]
    public void Occupied_larder_creates_a_meal_intent_and_yields_before_progress()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        var witnessed = false;
        for (var tick = 0; tick < PrototypeTuning.FirstRaidTick; tick++)
        {
            var before = world.GetSnapshot();
            var larderOccupants = before.Creatures
                .Where(creature =>
                    creature.Position is { X: 14 or 15, Y: 7 } &&
                    creature.CurrentJobId is null &&
                    !creature.MealReserved &&
                    creature.Satiety >= PrototypeTuning.EatThreshold)
                .ToArray();
            var hungry = before.Creatures.Any(creature =>
                creature.Satiety < PrototypeTuning.EatThreshold &&
                !creature.MealReserved);
            if (larderOccupants.Length == 2 &&
                hungry &&
                before.Stocks.Meals > 0)
            {
                var eaten = before.Economy.MealsEaten;
                var yields = before.Creatures.Sum(creature => creature.YieldCount);
                world.Step();
                var after = world.GetSnapshot();
                Assert.Contains(after.Creatures, creature => creature.MealReserved);
                for (var followup = 0;
                     followup < 80 &&
                     world.GetSnapshot().Creatures.Sum(creature => creature.YieldCount) == yields;
                     followup++)
                {
                    world.Step();
                }

                Assert.True(
                    world.GetSnapshot().Creatures.Sum(creature => creature.YieldCount) > yields);
                for (var followup = 0;
                     followup < 80 && world.MealsEaten == eaten;
                     followup++)
                {
                    world.Step();
                }

                Assert.True(world.MealsEaten > eaten);
                witnessed = true;
                break;
            }

            world.Step();
        }

        Assert.True(witnessed, "The deterministic baseline did not reach the occupied-larder witness.");
    }

    [Fact]
    public void Fatigue_accumulates_across_short_jobs()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        var initial = world.GetSnapshot();
        world.RunTicks(300);
        var after = world.GetSnapshot();

        Assert.Contains(after.Creatures, creature => creature.WorkTicks >= 20);
        foreach (var creature in after.Creatures)
        {
            var start = initial.Creatures.Single(item => item.Id == creature.Id);
            Assert.Equal(
                start.Fatigue + creature.WorkTicks / PrototypeTuning.FatigueGainPeriod,
                creature.Fatigue);
        }
    }

    [Fact]
    public void Rest_jobs_are_personal_start_at_fifty_and_preempt_only_above_seventy_five()
    {
        var world = new PrototypeWorld(LoadFixture("prepared"));
        var previous = world.GetSnapshot();
        var witnessedAssignment = false;
        for (var tick = 0; tick < 1_200; tick++)
        {
            world.Step();
            var current = world.GetSnapshot();
            foreach (var creature in current.Creatures)
            {
                if (creature.CurrentJobId is not { } jobId)
                {
                    continue;
                }

                var job = current.Jobs.Single(job => job.JobId == jobId);
                if (job.Kind != JobKind.Rest)
                {
                    continue;
                }

                Assert.Equal(creature.Id, job.PersonalCreatureId);
                var before = previous.Creatures.Single(item => item.Id == creature.Id);
                if (before.CurrentJobId != jobId)
                {
                    Assert.True(before.Fatigue >= PrototypeTuning.RestSeekThreshold);
                    witnessedAssignment = true;
                }
            }

            previous = current;
        }

        Assert.True(witnessedAssignment);
        Assert.Contains(
            previous.Events,
            @event => @event.ReasonCode == "chosen_need_fatigue");
    }

    [Fact]
    public void Priority_zero_makes_rest_unavailable_without_forcing_a_preemption()
    {
        var log = new PrototypeCommandLog(
            "custom",
            42,
            [
                new ZonePaintCommand(
                    0,
                    ZoneKind.TrainingGround,
                    [
                        new(7, 11), new(8, 11), new(9, 11), new(10, 11),
                        new(7, 12), new(8, 12), new(9, 12), new(10, 12),
                        new(7, 13), new(8, 13), new(9, 13), new(10, 13),
                    ]),
                new SetPriorityCommand(0, JobKind.Drill, 4),
                new SetPriorityCommand(0, JobKind.Rest, 0),
            ]);
        var result = PrototypeScenario.Run(log, 1_000);

        Assert.DoesNotContain(result.State.Jobs, job => job.Kind == JobKind.Rest);
        Assert.Contains(
            result.State.Events,
            @event => @event.ReasonCode == "refused_priority_zero" &&
                @event.JobKind == JobKind.Rest);
    }

    [Fact]
    public void Neutral_tick_zero_diagnostic_does_not_invent_a_meal_haul_urgency()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        world.Step();
        var creature = world.GetSnapshot().Creatures.Single(item => item.Id == 1);

        Assert.Equal(JobKind.Harvest, creature.LastDecision.JobKind);
        Assert.Equal("waiting_crop_not_ripe", creature.LastDecision.ReasonCode);
    }

    [Fact]
    public void Enclosed_larder_is_an_observable_unreachable_wait_not_an_exception()
    {
        var log = new PrototypeCommandLog(
            "custom",
            42,
            [
                new ZonePaintCommand(
                    0,
                    ZoneKind.Forbidden,
                    [
                        new(14, 6), new(15, 6), new(13, 7),
                        new(16, 7), new(14, 8), new(15, 8),
                    ]),
            ]);
        var result = PrototypeScenario.Run(log, 500);

        Assert.Contains(
            result.State.Events,
            @event => @event.ReasonCode == "refused_zone_unreachable");
        Assert.DoesNotContain(result.State.Creatures, creature => creature.MealReserved);
    }

    [Fact]
    public void One_remaining_physical_larder_lane_still_runs_without_special_cases()
    {
        var log = new PrototypeCommandLog(
            "custom",
            42,
            [
                new ZoneEraseCommand(0, ZoneKind.Larder, [new(15, 7)]),
            ]);
        var result = PrototypeScenario.Run(log, 600);

        Assert.True(result.State.Economy.HarvestsCompleted > 0);
        Assert.True(result.State.Economy.RawHaulsCompleted > 0);
        Assert.True(result.State.Economy.CookBatchesCompleted > 0);
    }

    private static (int ExitCode, string Output, string Error) CaptureConsole(
        Func<int> action)
    {
        lock (typeof(PrototypeContractRegressionTests))
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var output = new StringWriter();
            using var error = new StringWriter();
            try
            {
                Console.SetOut(output);
                Console.SetError(error);
                return (action(), output.ToString(), error.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }
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
