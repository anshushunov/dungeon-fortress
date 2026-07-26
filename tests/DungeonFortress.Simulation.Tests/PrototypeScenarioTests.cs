using System.Text;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

public sealed class PrototypeScenarioTests
{
    [Fact]
    public void Same_seed_commands_and_tick_produce_identical_state_log_and_checksum()
    {
        var commands = LoadFixture("prepared");
        var first = PrototypeScenario.Run(commands, PrototypeTuning.RaidTick + 1);
        var second = PrototypeScenario.Run(commands, PrototypeTuning.RaidTick + 1);

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

        Assert.NotEqual(
            PrototypeScenario.Run(fixture, 128).Checksum,
            PrototypeScenario.Run(changed, 128).Checksum);
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
    public void Invalid_runtime_zone_command_is_atomic()
    {
        const string json =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"zone_erase","zoneKind":"Larder",
               "tiles":[[14,7],[15,7]]}
            ]}
            """;
        var world = new PrototypeWorld(
            PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json)));
        var before = PrototypeScenario.Capture(world);

        Assert.Throws<InvalidDataException>(world.Step);
        var after = PrototypeScenario.Capture(world);
        Assert.Equal(before.Checksum, after.Checksum);
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
            PrototypeTuning.RaidTick + 1);

        Assert.True(result.State.Stocks.MealsProduced > 0);
        Assert.True(result.State.Stocks.MealsEaten > 0);
        Assert.Equal(9, result.State.Creatures.Count);
        Assert.All(result.State.Creatures, creature =>
        {
            Assert.False(string.IsNullOrWhiteSpace(creature.Name));
            Assert.False(string.IsNullOrWhiteSpace(creature.LastDecision.ReasonCode));
            Assert.NotNull(creature.ReadinessAtRaid);
        });
        Assert.Contains(result.State.Events, @event => @event.ReasonCode == "chosen_need_hunger");
        Assert.Contains(
            result.State.Creatures,
            creature => creature.LastDecision.JobKind is not null);
    }

    [Fact]
    public void Contract_scenarios_satisfy_issue_9_precombat_invariants()
    {
        var baseline = PrototypeScenario.Run(
            LoadFixture("baseline"),
            PrototypeTuning.RaidTick + 1);
        var prepared = PrototypeScenario.Run(
            LoadFixture("prepared"),
            PrototypeTuning.RaidTick + 1);
        var neglected = PrototypeScenario.Run(
            LoadFixture("neglected"),
            PrototypeTuning.RaidTick + 1);
        var baselineEnd = PrototypeScenario.Run(
            LoadFixture("baseline"),
            PrototypeTuning.SessionTicks);
        var preparedEnd = PrototypeScenario.Run(
            LoadFixture("prepared"),
            PrototypeTuning.SessionTicks);
        var neglectedEnd = PrototypeScenario.Run(
            LoadFixture("neglected"),
            PrototypeTuning.SessionTicks);

        var readiness = (
            Baseline: AverageReadiness(baseline),
            Prepared: AverageReadiness(prepared),
            Neglected: AverageReadiness(neglected));
        Assert.True(
            readiness.Prepared > readiness.Baseline,
            Describe(baseline, prepared, neglected, readiness));
        Assert.True(
            readiness.Baseline > readiness.Neglected,
            Describe(baseline, prepared, neglected, readiness));
        Assert.All(baseline.State.Creatures, creature => Assert.Equal(0, creature.MartialForm));
        Assert.Contains(
            neglected.State.Events,
            @event => @event.ReasonCode == "refused_rule_min_satiety");
        Assert.Contains(
            prepared.State.Events,
            @event => @event.ReasonCode == "chosen_muster");
        Assert.Contains(
            prepared.State.Events,
            @event => @event.ReasonCode == "chosen_ration");
        Assert.InRange(baseline.State.Creatures.Average(c => c.Satiety), 40, 70);
        Assert.InRange(prepared.State.Creatures.Average(c => c.Satiety), 28, 60);
        Assert.InRange(neglected.State.Creatures.Average(c => c.Satiety), 0, 15);
        Assert.InRange(AverageReadiness(baseline), 32, 48);
        Assert.InRange(AverageReadiness(prepared), 50, 75);
        Assert.InRange(AverageReadiness(neglected), 15, 30);
        Assert.True(prepared.State.Creatures.Average(c => c.MartialForm) >= 45);
        Assert.True(neglected.State.Creatures.Average(c => c.MartialForm) <= 35);
        Assert.True(
            baselineEnd.State.Stocks.MealsProduced is >= 68 and <= 78 &&
            preparedEnd.State.Stocks.MealsProduced is >= 58 and <= 68 &&
            neglectedEnd.State.Stocks.MealsProduced is >= 0 and <= 6,
            $"end production baseline={baselineEnd.State.Stocks.MealsProduced}, " +
            $"prepared={preparedEnd.State.Stocks.MealsProduced}, " +
            $"neglected={neglectedEnd.State.Stocks.MealsProduced}");
    }

    [Fact]
    public void Replay_from_loaded_command_log_is_byte_identical()
    {
        var path = FixturePath("prepared");
        var first = PrototypeScenario.Run(
            PrototypeCommandDocument.Load(path),
            PrototypeTuning.RaidTick + 1);
        var replay = PrototypeScenario.Run(
            PrototypeCommandDocument.Load(path),
            PrototypeTuning.RaidTick + 1);

        Assert.Equal(first.CanonicalJson, replay.CanonicalJson);
    }

    [Fact]
    public void Performance_sanity_completes_three_raid_tick_runs()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        foreach (var scenario in new[] { "baseline", "prepared", "neglected" })
        {
            _ = PrototypeScenario.Run(
                LoadFixture(scenario),
                PrototypeTuning.RaidTick + 1);
        }

        started.Stop();
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(10),
            $"Three prototype scenarios took {started.Elapsed}.");
    }

    private static int AverageReadiness(PrototypeRunResult result)
    {
        return (int)result.State.Creatures.Average(creature => creature.ReadinessAtRaid!.Value);
    }

    private static string Describe(
        PrototypeRunResult baseline,
        PrototypeRunResult prepared,
        PrototypeRunResult neglected,
        (int Baseline, int Prepared, int Neglected) readiness)
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
            $"neglected[{One(neglected, readiness.Neglected)}]";
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
