using System.Text;
using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Issue #24: the player designates internal rock and creatures excavate it on
/// their own. Every assertion here reads canonical simulation state, never a
/// direct order, so the tests cannot fake the autonomy they are checking.
/// </summary>
public sealed class PrototypeDigTests
{
    // The dig pocket of PrototypeMap. (26,2) is fully enclosed by rock and the
    // map boundary, which makes it the natural unreachable-designation witness.
    private static readonly GridPoint PocketTopLeft = new(25, 1);
    private static readonly GridPoint PocketEnclosed = new(26, 2);
    private static readonly GridPoint PocketBottomLeft = new(25, 3);

    [Fact]
    public void Dig_commands_parse_strictly_and_round_trip_through_pending_commands()
    {
        const string json =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[
              {"tick":0,"kind":"dig_designate","tiles":[[26,1],[25,1]]},
              {"tick":5,"kind":"dig_cancel","tiles":[[26,1]]}
            ]}
            """;

        var log = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json));

        var designate = Assert.IsType<DigDesignateCommand>(log.Commands[0]);
        var cancel = Assert.IsType<DigCancelCommand>(log.Commands[1]);
        // The parser normalises tile order so two spellings of one stroke are
        // the same command.
        Assert.Equal([new GridPoint(25, 1), new GridPoint(26, 1)], designate.Tiles);
        Assert.Equal([new GridPoint(26, 1)], cancel.Tiles);

        var pending = new PrototypeWorld(log).GetSnapshot().PendingCommands;
        Assert.Equal(["dig_designate", "dig_cancel"], pending.Select(item => item.Kind));
        Assert.Equal(
            [new GridPoint(25, 1), new GridPoint(26, 1)],
            pending[0].Tiles);
        Assert.All(pending, item =>
        {
            Assert.Null(item.ZoneKind);
            Assert.Null(item.JobKind);
            Assert.Null(item.RuleId);
            Assert.Null(item.Value);
        });
    }

    [Fact]
    public void Tile_order_does_not_change_the_canonical_state_of_a_dig_stroke()
    {
        const string template =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[
              {"tick":0,"kind":"dig_designate","tiles":[TILES]}
            ]}
            """;
        var first = PrototypeCommandDocument.Parse(
            Encoding.UTF8.GetBytes(template.Replace("TILES", "[25,1],[26,1],[25,2]")));
        var second = PrototypeCommandDocument.Parse(
            Encoding.UTF8.GetBytes(template.Replace("TILES", "[25,2],[25,1],[26,1]")));

        Assert.Equal(
            PrototypeScenario.Run(first, 120).Checksum,
            PrototypeScenario.Run(second, 120).Checksum);
    }

    [Theory]
    // Addressing a creature, a job or an attack target stays inexpressible.
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[25,1]],"creatureId":4}""")]
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[25,1]],"jobId":7}""")]
    [InlineData("""{"tick":0,"kind":"dig_cancel","tiles":[[25,1]],"creatureId":4}""")]
    // Borrowing a field from another command kind is still an unknown field.
    [InlineData("""{"tick":0,"kind":"dig_designate","zoneKind":"Farm","tiles":[[25,1]]}""")]
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[25,1]],"value":3}""")]
    // Structural rules.
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[]}""")]
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[25,1],[25,1]]}""")]
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[28,1]]}""")]
    [InlineData("""{"tick":0,"kind":"dig_designate"}""")]
    // Only internal rock can be designated.
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[12,12]]}""")]
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[0,0]]}""")]
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[27,13]]}""")]
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[2,1]]}""")]
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[10,7]]}""")]
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[14,7]]}""")]
    // Atomicity at parse time: one bad tile rejects the whole stroke.
    [InlineData("""{"tick":0,"kind":"dig_designate","tiles":[[25,1],[12,12]]}""")]
    public void Invalid_dig_commands_are_rejected_before_a_world_exists(string command)
    {
        var json =
            $$"""
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[{{command}}]}
            """;

        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void A_runtime_rejected_designation_mutates_nothing()
    {
        // Both tiles are rock in the initial layout, so the static pre-flight
        // accepts the log. At tick 400 the first tile is already floor, which the
        // live map rejects — and the healthy tile of the same command must not be
        // designated by the failed attempt.
        var log = new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            [
                new DigDesignateCommand(0, [PocketBottomLeft]),
                new DigDesignateCommand(400, [PocketTopLeft, PocketBottomLeft]),
            ]);
        var world = new PrototypeWorld(log);
        world.RunTicks(400);
        var before = world.GetSnapshot();
        Assert.Contains(PocketBottomLeft, before.Map.ExcavatedTiles);

        Assert.Throws<InvalidDataException>(world.Step);

        var after = world.GetSnapshot();
        Assert.Empty(after.DigDesignations);
        Assert.Equal(before.Map.ExcavatedTiles, after.Map.ExcavatedTiles);
    }

    [Fact]
    public void A_designation_is_reserved_by_exactly_one_creature_that_works_from_a_neighbour()
    {
        var world = new PrototypeWorld(DesignateLog(PocketBottomLeft));
        PrototypeSnapshot? reservedState = null;
        for (var tick = 0; tick < 200 && reservedState is null; tick++)
        {
            world.Step();
            var state = world.GetSnapshot();
            if (state.DigDesignations.Single().ReservedBy is not null)
            {
                reservedState = state;
            }
        }

        Assert.NotNull(reservedState);
        var designation = reservedState.DigDesignations.Single();
        var job = reservedState.Jobs.Single(item => item.Kind == JobKind.Dig);
        Assert.Equal(PocketBottomLeft, job.Origin);
        Assert.Equal(designation.ReservedBy, job.ReservedBy);
        Assert.Equal(1, Manhattan(job.Origin, job.Target));
        Assert.DoesNotContain(job.Target, reservedState.Map.RockTiles);
        Assert.Single(
            reservedState.Creatures.Where(creature => creature.CurrentJobId == job.JobId));

        // Approach and reservation are a deterministic function of the log alone.
        var replay = new PrototypeWorld(DesignateLog(PocketBottomLeft));
        replay.RunTicks(reservedState.Tick);
        var replayJob = replay.GetSnapshot().Jobs.Single(item => item.Kind == JobKind.Dig);
        Assert.Equal(job.ReservedBy, replayJob.ReservedBy);
        Assert.Equal(job.Target, replayJob.Target);
    }

    [Fact]
    public void The_worker_never_stands_inside_rock_while_digging()
    {
        var world = new PrototypeWorld(DesignateLog(PocketBottomLeft));
        for (var tick = 0; tick < 200; tick++)
        {
            world.Step();
            var state = world.GetSnapshot();
            Assert.All(
                state.Creatures,
                creature => Assert.DoesNotContain(creature.Position, state.Map.RockTiles));
        }
    }

    [Fact]
    public void A_completed_dig_turns_rock_into_floor_and_leaves_one_loose_stone()
    {
        var result = PrototypeScenario.Run(DesignateLog(PocketBottomLeft), 200);
        var state = result.State;

        Assert.DoesNotContain(PocketBottomLeft, state.Map.RockTiles);
        Assert.Equal([PocketBottomLeft], state.Map.ExcavatedTiles);
        Assert.Empty(state.DigDesignations);
        Assert.DoesNotContain(state.Jobs, job => job.Kind == JobKind.Dig);

        var stone = Assert.Single(
            state.LooseItems.Where(item => item.Resource == ResourceKind.Stone));
        Assert.Equal(PocketBottomLeft, stone.Position);
        Assert.Equal(PrototypeTuning.DigStoneYield, stone.Quantity);
        Assert.Equal(PrototypeTuning.DigStoneYield, state.Stocks.LooseStone);
        Assert.Equal(1, state.Economy.DigsCompleted);
        Assert.Equal(PrototypeTuning.DigStoneYield, state.Economy.StoneProduced);
        Assert.True(state.Labor.DigTicks > 0);

        Assert.Contains(state.Events, @event => @event.ReasonCode == "dig_started");
        Assert.Contains(
            state.Events,
            @event => @event.ReasonCode == "dig_completed" &&
                @event.JobKind == JobKind.Dig &&
                @event.Target == PocketBottomLeft &&
                @event.Details["stone"] == PrototypeTuning.DigStoneYield);
    }

    /// <summary>
    /// Digging alone still produces nothing but a loose pile. Issue #26 added the
    /// material stockpile that turns that pile into a delivery; without one, the
    /// result of an excavation is exactly what step 1 promised.
    /// </summary>
    [Fact]
    public void Loose_stone_stays_on_its_tile_until_a_material_stockpile_exists()
    {
        var world = new PrototypeWorld(DesignateLog(PocketBottomLeft));
        world.RunTicks(200);
        Assert.Equal(PrototypeTuning.DigStoneYield, world.GetSnapshot().Stocks.LooseStone);

        world.RunTicks(600);
        var state = world.GetSnapshot();

        Assert.Equal(PrototypeTuning.DigStoneYield, state.Stocks.LooseStone);
        Assert.Equal(0, state.Stocks.StoredStone);
        Assert.Empty(state.StockpileCells);
        Assert.DoesNotContain(
            state.Jobs,
            job => job.Kind == JobKind.Haul && job.Resource == ResourceKind.Stone);
        Assert.DoesNotContain(
            state.Creatures,
            creature => creature.Carrying == ResourceKind.Stone);
    }

    [Fact]
    public void Cancel_before_work_starts_removes_the_intent_without_touching_the_map()
    {
        var log = new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            [
                new DigDesignateCommand(0, [PocketBottomLeft]),
                new DigCancelCommand(1, [PocketBottomLeft]),
            ]);
        var state = PrototypeScenario.Run(log, 200).State;

        Assert.Empty(state.DigDesignations);
        Assert.Empty(state.Map.ExcavatedTiles);
        Assert.Contains(PocketBottomLeft, state.Map.RockTiles);
        Assert.DoesNotContain(state.Jobs, job => job.Kind == JobKind.Dig);
        Assert.DoesNotContain(state.LooseItems, item => item.Resource == ResourceKind.Stone);
        Assert.Equal(0, state.Economy.DigsCompleted);
    }

    [Fact]
    public void Cancel_during_progress_releases_the_worker_and_discards_partial_progress()
    {
        var scout = new PrototypeWorld(DesignateLog(PocketBottomLeft));
        var progressTick = -1;
        while (scout.CurrentTick < 200 && progressTick < 0)
        {
            scout.Step();
            if (scout.GetSnapshot().DigDesignations.SingleOrDefault()?.ProgressTicks > 0)
            {
                progressTick = scout.CurrentTick;
            }
        }

        Assert.True(progressTick > 0, "The designation never reached visible progress.");

        var log = new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            [
                new DigDesignateCommand(0, [PocketBottomLeft]),
                new DigCancelCommand(progressTick, [PocketBottomLeft]),
            ]);
        var world = new PrototypeWorld(log);
        world.RunTicks(progressTick);
        var before = world.GetSnapshot();
        var worker = before.DigDesignations.Single().ReservedBy;
        Assert.NotNull(worker);

        world.Step();
        var after = world.GetSnapshot();

        Assert.Empty(after.DigDesignations);
        Assert.DoesNotContain(after.Jobs, job => job.Kind == JobKind.Dig);
        Assert.Contains(PocketBottomLeft, after.Map.RockTiles);
        Assert.Empty(after.Map.ExcavatedTiles);
        Assert.DoesNotContain(after.LooseItems, item => item.Resource == ResourceKind.Stone);
        Assert.Contains(after.Events, @event => @event.ReasonCode == "dig_cancelled");

        // The released worker is available again rather than stuck on a dead job.
        var released = after.Creatures.Single(creature => creature.Id == worker);
        Assert.True(
            released.CurrentJobId is null ||
            after.Jobs.Single(job => job.JobId == released.CurrentJobId).Kind != JobKind.Dig);

        world.RunTicks(200);
        Assert.Empty(world.GetSnapshot().Map.ExcavatedTiles);
    }

    [Fact]
    public void An_enclosed_designation_is_an_observable_wait_and_never_teleports_a_worker()
    {
        var world = new PrototypeWorld(DesignateLog(PocketEnclosed));
        world.RunTicks(200);
        var state = world.GetSnapshot();

        var designation = state.DigDesignations.Single();
        Assert.Equal(PocketEnclosed, designation.Tile);
        Assert.False(designation.Reachable);
        Assert.Equal("dig_unreachable", designation.StatusCode);
        Assert.Null(designation.ReservedBy);
        Assert.DoesNotContain(state.Jobs, job => job.Kind == JobKind.Dig);
        Assert.Empty(state.Map.ExcavatedTiles);
        Assert.All(
            state.Creatures,
            creature => Assert.DoesNotContain(creature.Position, state.Map.RockTiles));
    }

    [Fact]
    public void Excavating_a_neighbour_makes_an_enclosed_designation_reachable()
    {
        // (26,2) is walled in by (25,2), (26,1), (26,3) and the map boundary.
        // Opening (25,2) is the only way in, and it must happen through work.
        var log = new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            [new DigDesignateCommand(0, [new GridPoint(25, 2), PocketEnclosed])]);
        var world = new PrototypeWorld(log);
        world.RunTicks(10);
        Assert.Equal(
            "dig_unreachable",
            world.GetSnapshot().DigDesignations
                .Single(item => item.Tile == PocketEnclosed).StatusCode);

        world.RunTicks(290);
        var state = world.GetSnapshot();

        Assert.Empty(state.DigDesignations);
        Assert.Equal(
            [new GridPoint(25, 2), PocketEnclosed],
            state.Map.ExcavatedTiles);
        Assert.Equal(2, state.Economy.DigsCompleted);
        Assert.Equal(2, state.Stocks.LooseStone);
    }

    [Fact]
    public void Dig_priority_zero_keeps_the_intent_and_explains_the_refusal()
    {
        var log = new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            [
                new SetPriorityCommand(0, JobKind.Dig, 0),
                new DigDesignateCommand(0, [PocketBottomLeft]),
            ]);
        var state = PrototypeScenario.Run(log, 200).State;

        var designation = state.DigDesignations.Single();
        Assert.Equal("dig_blocked_priority", designation.StatusCode);
        Assert.True(designation.Reachable);
        Assert.DoesNotContain(state.Jobs, job => job.Kind == JobKind.Dig);
        Assert.Empty(state.Map.ExcavatedTiles);
    }

    [Fact]
    public void The_same_seed_and_log_reproduce_map_designations_jobs_and_stone_byte_for_byte()
    {
        var log = DesignateLog(PocketTopLeft, new GridPoint(26, 1), PocketBottomLeft);
        var first = PrototypeScenario.Run(log, 300);
        var second = PrototypeScenario.Run(log, 300);

        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.CanonicalEventLog, second.CanonicalEventLog);
        Assert.Equal(first.Checksum, second.Checksum);

        using var document = JsonDocument.Parse(first.CanonicalJson);
        var root = document.RootElement;
        Assert.NotEmpty(root.GetProperty("map").GetProperty("rockTiles").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("map").GetProperty("excavatedTiles").EnumerateArray());
        Assert.True(root.TryGetProperty("digDesignations", out _));
        Assert.True(root.GetProperty("economy").GetProperty("digsCompleted").GetInt32() > 0);
        Assert.True(root.GetProperty("labor").GetProperty("digTicks").GetInt32() > 0);
        Assert.Contains(
            root.GetProperty("looseItems").EnumerateArray(),
            item => item.GetProperty("resource").GetString() == "stone");
    }

    [Fact]
    public void A_different_designation_changes_the_canonical_checksum()
    {
        Assert.NotEqual(
            PrototypeScenario.Run(DesignateLog(PocketBottomLeft), 120).Checksum,
            PrototypeScenario.Run(DesignateLog(PocketTopLeft), 120).Checksum);
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    [InlineData("neglected")]
    public void Existing_fixtures_never_touch_the_map_or_produce_stone(string fixtureName)
    {
        var state = PrototypeScenario.Run(
            LoadFixture(fixtureName),
            PrototypeTuning.FirstRaidTick + 1).State;

        Assert.Empty(state.Map.ExcavatedTiles);
        Assert.Empty(state.DigDesignations);
        Assert.Equal(0, state.Economy.DigsCompleted);
        Assert.Equal(0, state.Economy.StoneProduced);
        Assert.Equal(0, state.Labor.DigTicks);
        Assert.Equal(0, state.Stocks.LooseStone);
        Assert.DoesNotContain(state.Jobs, job => job.Kind == JobKind.Dig);
        Assert.Contains(PocketBottomLeft, state.Map.RockTiles);
    }

    /// <summary>
    /// docs/engineering/PROTOTYPE_HEADLESS.md walks a reader through this shipped
    /// fixture and quotes concrete numbers. Without this test the document and the
    /// fixture could drift apart silently.
    /// </summary>
    [Fact]
    public void Dig_demo_fixture_matches_the_documented_headless_walkthrough()
    {
        var log = LoadFixture("dig-demo");

        var early = PrototypeScenario.Run(log, 5).State;
        Assert.Equal(
            [
                new GridPoint(25, 1), new GridPoint(26, 1),
                new GridPoint(25, 2), new GridPoint(26, 2),
                new GridPoint(25, 3),
            ],
            early.DigDesignations.Select(item => item.Tile));
        Assert.All(
            early.DigDesignations.Where(item => item.Tile.X == 26),
            item =>
            {
                Assert.False(item.Reachable);
                Assert.Equal("dig_unreachable", item.StatusCode);
                Assert.Null(item.ReservedBy);
            });
        Assert.All(
            early.DigDesignations.Where(item => item.Tile.X == 25),
            item =>
            {
                Assert.True(item.Reachable);
                Assert.Equal("dig_reserved", item.StatusCode);
                Assert.Equal(24, item.WorkTile!.Value.X);
            });
        // Three reachable designations, three different volunteers.
        Assert.Equal(
            3,
            early.DigDesignations
                .Where(item => item.ReservedBy is not null)
                .Select(item => item.ReservedBy!.Value)
                .Distinct()
                .Count());

        var late = PrototypeScenario.Run(log, 200).State;
        Assert.Equal(5, late.Economy.DigsCompleted);
        Assert.Equal(5, late.Stocks.LooseStone);
        Assert.Equal(
            [
                new GridPoint(25, 1), new GridPoint(26, 1),
                new GridPoint(25, 2), new GridPoint(26, 2),
                new GridPoint(25, 3),
            ],
            late.Map.ExcavatedTiles);
        Assert.Empty(late.DigDesignations);
        Assert.Contains(new GridPoint(26, 3), late.Map.RockTiles);
    }

    /// <summary>
    /// Zoning a room follows the same two-level rule the dig designation does, and
    /// since Issue #48 the rule is stated on the tile's future rather than on its
    /// present: internal rock passes the static pre-flight because digging can
    /// turn it into floor, and the live map rejects the command on its own tick
    /// while the tile is still rock. Only the map boundary is refused outright —
    /// it can never become anything.
    /// </summary>
    [Fact]
    public void Zoning_a_room_on_rock_is_refused_by_the_live_map_not_by_the_preflight()
    {
        const string tooEarly =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"zone_paint","zoneKind":"Quarters","tiles":[[25,1]]}
            ]}
            """;

        var log = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(tooEarly));
        var failure = Assert.Throws<InvalidDataException>(() => PrototypeScenario.Run(log, 1));
        Assert.Contains("not passable", failure.Message, StringComparison.Ordinal);

        const string boundary =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"zone_paint","zoneKind":"Quarters","tiles":[[0,0]]}
            ]}
            """;
        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(boundary)));
    }

    /// <summary>
    /// The other half of the same rule: once the tile really is floor, the room
    /// may be zoned over it. This is what makes a functional room out of carved
    /// space possible at all.
    /// </summary>
    [Fact]
    public void Zoning_a_room_on_excavated_ground_is_accepted_once_the_tile_is_floor()
    {
        var excavated = new GridPoint(25, 1);
        var state = PrototypeScenario.Run(
            new PrototypeCommandLog(
                "custom",
                PrototypeTuning.DefaultSeed,
                [
                    new DigDesignateCommand(0, [excavated]),
                    new ZonePaintCommand(400, ZoneKind.TrainingGround, [excavated]),
                ]),
            401).State;

        Assert.Contains(excavated, state.Map.ExcavatedTiles);
        Assert.Contains(excavated, state.Zones[ZoneKind.TrainingGround]);
    }

    private static int Manhattan(GridPoint left, GridPoint right)
    {
        return Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);
    }

    private static PrototypeCommandLog DesignateLog(params GridPoint[] tiles)
    {
        return new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            [new DigDesignateCommand(0, tiles)]);
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
