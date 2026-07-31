using System.Text;
using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Issue #48: excavated stone stops being a number in a stockpile. The player
/// marks a blueprint, creatures deliver stone to it on their own, the work
/// consumes the stone and the tile becomes a training post that produces real
/// <see cref="JobKind.Drill"/> work. Every assertion reads canonical simulation
/// state, and none of them can address a creature or a job, so the tests cannot
/// fake the autonomy or the conservation they are checking.
/// </summary>
public sealed class PrototypeBuildTests
{
    // The wall between the hearth and the spine, east of the larder's stair. It
    // moved here from the quarry in the far corner with the dungeon of Issue
    // #117: every tile of it is rock at tick 0, so digging it still creates
    // ground that did not exist, and it is where a domain would actually widen
    // itself. At the corner the whole chain stopped being demonstrable — the
    // stone lost every scoring comparison to a food haul fifteen tiles nearer,
    // and a post raised out there was never trained at.
    private static readonly GridPoint[] Pocket =
    [
        new(17, 9), new(18, 9), new(19, 9), new(20, 9),
    ];

    private static readonly GridPoint StockLeft = new(16, 8);
    private static readonly GridPoint StockRight = new(17, 8);

    // The quarry at the back of the dungeon, and the stockpile in the quarters
    // next to it. Two of the ten build statuses are only reachable from a long
    // walk — `build_reserved` is the window between a builder volunteering and
    // arriving, and `build_stone_reserved` needs every block to be booked while
    // a site waits — and next to the hearth that window is one tick wide or
    // shut. This is where the shipped demo fixtures dig.
    private static readonly GridPoint[] FarPocket =
    [
        new(25, 1), new(25, 2), new(25, 3), new(26, 1),
    ];

    private static readonly GridPoint FarStockLeft = new(22, 1);
    private static readonly GridPoint FarStockRight = new(23, 1);
    private static readonly GridPoint FarSite = new(25, 2);

    // The site is inside the dug wall on purpose: it is ground that does not
    // exist at tick 0, so a post standing there is a room the player created.
    private static readonly GridPoint Site = new(18, 9);

    // Late enough that every block the pocket yields is already in the stockpile,
    // which makes the stockpile the only possible source of the build material.
    private const int BlueprintTick = 1_000;

    // ---------------------------------------------------------------- 1. command

    [Theory]
    // The map boundary and the gate can never be plain floor. Internal rock is
    // deliberately absent from this list: digging can turn it into floor, so it is
    // the two-level case checked further down, not a rejection.
    [InlineData("[[0,0]]")]
    [InlineData("[[27,13]]")]
    // Map features are floor-adjacent but are not plain floor.
    [InlineData("[[2,1]]")]
    [InlineData("[[10,7]]")]
    [InlineData("[[14,7]]")]
    [InlineData("[[20,3]]")]
    [InlineData("[[10,2]]")]
    // Atomicity: one bad tile rejects the whole stroke.
    [InlineData("[[12,12],[14,7]]")]
    public void Build_designate_is_rejected_outside_plain_floor(string tiles)
    {
        var json =
            $$"""
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"build_designate","tiles":{{tiles}}}
            ]}
            """;

        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// ADR 0005 and ADR 0007 are enforced by the parser, not by convention: the
    /// closed field set of a designation command has no room for a creature, a job
    /// or a building type.
    /// </summary>
    [Theory]
    [InlineData("""{"tick":0,"kind":"build_designate","tiles":[[12,12]],"creatureId":3}""")]
    [InlineData("""{"tick":0,"kind":"build_designate","tiles":[[12,12]],"jobId":7}""")]
    [InlineData("""{"tick":0,"kind":"build_designate","tiles":[[12,12]],"building":"Post"}""")]
    [InlineData("""{"tick":0,"kind":"build_designate","tiles":[[12,12]],"zoneKind":"TrainingGround"}""")]
    [InlineData("""{"tick":0,"kind":"build_designate"}""")]
    [InlineData("""{"tick":0,"kind":"build_cancel","tiles":[[12,12]],"creatureId":3}""")]
    public void A_designation_command_cannot_carry_an_address(string command)
    {
        var json =
            $$"""
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[{{command}}]}
            """;

        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// Two-level validation, the mirror of the dig rule in ADR 0007. Ground that
    /// only digging creates passes the static pre-flight, because it can become
    /// plain floor; the live map rejects the command on its own tick while the
    /// tile is still rock, and accepts it once the tile has been excavated.
    /// </summary>
    [Fact]
    public void A_blueprint_on_excavated_ground_passes_preflight_and_waits_for_the_dig()
    {
        var tooEarly = new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            [
                new DigDesignateCommand(0, Pocket),
                new BuildDesignateCommand(1, [Site]),
            ]);

        // The pre-flight accepts it: the tile can become plain floor.
        _ = new PrototypeWorld(tooEarly);
        var tooEarlyFailure = Assert.Throws<InvalidDataException>(
            () => PrototypeScenario.Run(tooEarly, 2));
        Assert.Contains("not plain floor", tooEarlyFailure.Message, StringComparison.Ordinal);

        var state = PrototypeScenario.Run(BuildChain(), BlueprintTick + 1).State;
        Assert.Contains(Site, state.Map.ExcavatedTiles);
        Assert.Contains(state.BuildSites, site => site.Tile == Site);
    }

    /// <summary>
    /// A building site is not a warehouse. The rule is checked in both directions
    /// and in both validators, so no ordering of commands can produce a tile that
    /// is a stockpile cell and a blueprint at the same time.
    /// </summary>
    [Fact]
    public void A_blueprint_and_a_material_stockpile_cannot_share_a_tile()
    {
        var blueprintFirst = new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            [
                new BuildDesignateCommand(0, [StockLeft]),
                new ZonePaintCommand(1, ZoneKind.MaterialStockpile, [StockLeft]),
            ]);
        var stockpileFirst = new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            [
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft]),
                new BuildDesignateCommand(1, [StockLeft]),
            ]);

        Assert.Throws<InvalidDataException>(() => new PrototypeWorld(blueprintFirst));
        Assert.Throws<InvalidDataException>(() => new PrototypeWorld(stockpileFirst));

        // The published lists agree with the rule, so the brush cannot offer a
        // target the simulation would refuse.
        var state = PrototypeScenario.Run(
            Log(new BuildDesignateCommand(0, [StockLeft])),
            2).State;
        Assert.DoesNotContain(StockLeft, state.Map.StockpileFloorTiles);
        Assert.DoesNotContain(StockLeft, state.Map.BuildFloorTiles);
    }

    /// <summary>
    /// The brush must not re-derive "where may a post go?"; the simulation
    /// publishes it, exactly as it publishes which rock may be dug and which floor
    /// may store material.
    /// </summary>
    [Fact]
    public void The_snapshot_publishes_exactly_where_a_blueprint_may_be_placed()
    {
        var state = PrototypeScenario.Run(
            Log(
                new DigDesignateCommand(0, Pocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight])),
            700).State;
        var floor = state.Map.BuildFloorTiles.ToHashSet();

        Assert.Contains(new GridPoint(12, 12), floor);
        // Unlike a stockpile, freshly excavated ground is a legal building site:
        // a room made out of carved space is the point of this step.
        Assert.NotEmpty(state.Map.ExcavatedTiles);
        Assert.All(state.Map.ExcavatedTiles, tile => Assert.Contains(tile, floor));
        Assert.DoesNotContain(new GridPoint(27, 13), floor);  // gate
        Assert.DoesNotContain(new GridPoint(0, 0), floor);    // map boundary
        Assert.DoesNotContain(new GridPoint(2, 1), floor);    // mushroom bed
        Assert.DoesNotContain(new GridPoint(10, 7), floor);   // kitchen station
        Assert.DoesNotContain(new GridPoint(14, 7), floor);   // larder
        Assert.DoesNotContain(new GridPoint(20, 3), floor);   // bunk
        Assert.DoesNotContain(new GridPoint(10, 2), floor);   // authored post
        Assert.DoesNotContain(StockLeft, floor);              // stockpile cell
        Assert.All(state.Map.RockTiles, tile => Assert.DoesNotContain(tile, floor));

        // The published list is exactly the accepted list, in both directions.
        Assert.All(
            floor.Where(tile => !state.Map.ExcavatedTiles.Contains(tile)),
            tile => PrototypeScenario.Run(Log(new BuildDesignateCommand(0, [tile])), 2));
        foreach (var rejected in new GridPoint[] { new(14, 7), new(2, 1), new(10, 2), new(20, 3) })
        {
            Assert.Throws<InvalidDataException>(() => new PrototypeWorld(
                Log(new BuildDesignateCommand(0, [rejected]))));
        }
    }

    // ------------------------------------------------------------- 2. the chain

    /// <summary>
    /// The whole slice in one session and without a single addressed order:
    /// designate rock, dig it, store the stone, mark a blueprint on the ground the
    /// digging created, watch the crew fetch the stone back out of the stockpile,
    /// build the post, and see Drill work appear where there was none.
    /// </summary>
    [Fact]
    public void Designate_dig_store_blueprint_deliver_build_then_drill_runs_in_one_session()
    {
        var world = new PrototypeWorld(BuildChain());
        var sawWithdrawal = false;
        var sawDelivery = false;
        var sawBuildStart = false;
        var drillBeforeBuild = 0;
        var drillAfterBuild = 0;

        while (!world.IsComplete)
        {
            world.Step();
            var state = world.GetSnapshot();
            var built = state.Map.BuiltPostTiles.Contains(Site);

            sawWithdrawal |= state.Jobs.Any(job => job.SourceCell is not null && job.PickedUp);
            sawDelivery |= state.Events.Any(@event => @event.ReasonCode == "stone_delivered");
            sawBuildStart |= state.Events.Any(@event => @event.ReasonCode == "build_started");

            var drillHere = state.Jobs.Count(
                job => job.Kind == JobKind.Drill && job.Origin == Site);
            if (built)
            {
                drillAfterBuild = Math.Max(drillAfterBuild, drillHere);
            }
            else
            {
                drillBeforeBuild = Math.Max(drillBeforeBuild, drillHere);
            }
        }

        var final = world.GetSnapshot();

        Assert.True(sawWithdrawal, "No creature ever fetched stone back out of the stockpile.");
        Assert.True(sawDelivery, "No stone was ever delivered to the construction site.");
        Assert.True(sawBuildStart, "Construction never started.");
        Assert.Equal(1, final.Economy.BuildsCompleted);
        Assert.Equal(PrototypeTuning.BuildStoneCost, final.Economy.StoneConsumed);
        Assert.Contains(Site, final.Map.BuiltPostTiles);
        Assert.Empty(final.BuildSites);

        // The point of the step: the built post changes the simulation.
        Assert.Equal(0, drillBeforeBuild);
        Assert.True(drillAfterBuild > 0, "The built post never produced a Drill job.");
        Assert.Contains(
            final.Stations,
            station => station.Position == Site && station.Kind == TileKind.Post);
        Assert.Contains(
            final.Stations,
            station => station.Position == Site && station.OccupiedTicks > 0);
        Assert.True(final.Labor.DrillTicks > 0);
        Assert.Contains(final.Creatures, creature => creature.MartialForm > 0);

        // And no command in the log addressed anybody.
        Assert.All(
            BuildChain().Commands,
            command => Assert.True(
                command is ZonePaintCommand or DigDesignateCommand or
                    BuildDesignateCommand or SetPriorityCommand,
                command.GetType().Name));
    }

    /// <summary>
    /// Without a stockpile in the way the same chain still runs: a loose pile is
    /// a legal source for a construction site, and the crew prefers the site over
    /// putting the stone away, so material is never carried twice.
    /// </summary>
    [Fact]
    public void A_blueprint_outranks_a_stockpile_as_a_destination_for_loose_stone()
    {
        // Run at the far quarry, and the tick is the one on which the site's own
        // tile becomes floor rather than the first block anywhere: a blueprint on
        // rock is refused before the world runs.
        //
        // The far quarry is what makes the claim observable at all. Next to the
        // hearth the stockpile is two tiles from the rock, so every block is put
        // away before a blueprint can be marked and the race this test is about
        // never happens — measured, all four stored instead of two.
        var digTick = FindTick(
            Log(new DigDesignateCommand(0, FarPocket)),
            state => state.Map.ExcavatedTiles.Contains(FarSite));
        var state = PrototypeScenario.Run(
            Log(
                new DigDesignateCommand(0, FarPocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [FarStockLeft, FarStockRight]),
                new BuildDesignateCommand(digTick, [FarSite])),
            1_250).State;

        Assert.Equal(1, state.Economy.BuildsCompleted);
        Assert.Contains(FarSite, state.Map.BuiltPostTiles);
        Assert.Equal(PrototypeTuning.BuildStoneCost, state.Economy.StoneConsumed);
        // The two blocks the post ate never entered the stockpile. Stated as a
        // bound rather than as an equality: how many of the remaining blocks have
        // been put away by this tick is a question about walking, and on the
        // dungeon one of them is still in transit here. What the test is about is
        // that no block was stored and then fetched back out — that is what a
        // count above this bound would mean.
        Assert.True(
            state.Economy.StoneStored <= FarPocket.Length - PrototypeTuning.BuildStoneCost,
            $"stoneStored={state.Economy.StoneStored} of {FarPocket.Length} dug, with " +
            $"{PrototypeTuning.BuildStoneCost} eaten by the post: the blueprint stopped " +
            "outranking the stockpile as a destination.");
    }

    // ------------------------------------------------------- 3. conservation

    /// <summary>
    /// The invariant the whole step rests on, checked on every single tick of a
    /// session that digs, stores, delivers, builds, cancels a second blueprint
    /// with stone already on it and then runs into a raid.
    /// </summary>
    [Fact]
    public void Stone_is_conserved_on_every_tick_including_construction_and_cancellation()
    {
        var second = new GridPoint(19, 9);
        var world = new PrototypeWorld(new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            [
                new DigDesignateCommand(0, Pocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight]),
                new BuildDesignateCommand(BlueprintTick, [Site]),
                new ZonePaintCommand(BlueprintTick, ZoneKind.TrainingGround, [Site]),
                new SetPriorityCommand(BlueprintTick, JobKind.Drill, 4),
                // A second site that never gets built: construction is switched
                // off once the first post is up, so this one collects its stone
                // and then has the intention withdrawn from under it.
                new SetPriorityCommand(BlueprintTick + 400, JobKind.Build, 0),
                new BuildDesignateCommand(BlueprintTick + 400, [second]),
                new BuildCancelCommand(BlueprintTick + 900, [second]),
            ]));
        var sawSiteStone = false;
        var sawConsumed = false;
        var sawCancelledWithStone = 0;

        while (!world.IsComplete)
        {
            world.Step();
            var state = world.GetSnapshot();
            var looseByTile = state.LooseItems
                .Where(item => item.Resource == ResourceKind.Stone)
                .Sum(item => item.Quantity);
            var storedByCell = state.StockpileCells.Sum(cell => cell.Stored);
            var carriedByCreature = state.Creatures
                .Where(creature => creature.Carrying == ResourceKind.Stone)
                .Sum(creature => creature.CarryAmount);
            var siteByBlueprint = state.BuildSites.Sum(site => site.Delivered);

            Assert.Equal(looseByTile, state.Stocks.LooseStone);
            Assert.Equal(storedByCell, state.Stocks.StoredStone);
            Assert.Equal(carriedByCreature, state.Stocks.CarriedStone);
            Assert.Equal(siteByBlueprint, state.Stocks.SiteStone);
            Assert.Equal(
                state.Economy.StoneProduced,
                looseByTile + storedByCell + carriedByCreature + siteByBlueprint +
                state.Economy.StoneConsumed);
            Assert.Equal(
                state.Economy.BuildsCompleted * PrototypeTuning.BuildStoneCost,
                state.Economy.StoneConsumed);
            Assert.All(
                state.BuildSites,
                site => Assert.InRange(site.Delivered, 0, site.Required));

            sawSiteStone |= siteByBlueprint > 0;
            sawConsumed |= state.Economy.StoneConsumed > 0;
            if (state.Tick == BlueprintTick + 900)
            {
                sawCancelledWithStone = state.BuildSites
                    .Where(site => site.Tile == second)
                    .Sum(site => site.Delivered);
            }
        }

        Assert.True(sawSiteStone, "No tick ever showed stone waiting on a construction site.");
        Assert.True(sawConsumed, "No stone was ever spent on a post.");
        Assert.True(
            sawCancelledWithStone > 0,
            "The withdrawn blueprint never held stone, so the cancel path was not exercised.");
        Assert.Equal(Pocket.Length, world.GetSnapshot().Economy.StoneProduced);
    }

    /// <summary>
    /// The player withdraws an intention the crew already invested in. Everything
    /// that arrived comes back to the floor of that same tile; nothing teleports
    /// and nothing is deleted.
    /// </summary>
    [Fact]
    public void Cancelling_a_partly_supplied_blueprint_drops_its_stone_on_the_same_tile()
    {
        var chain = Log(
            new DigDesignateCommand(0, Pocket),
            new BuildDesignateCommand(200, [Site]));
        var deliveredTick = FindTick(chain, state => state.Stocks.SiteStone > 0);

        var world = new PrototypeWorld(Log(
            new DigDesignateCommand(0, Pocket),
            new BuildDesignateCommand(200, [Site]),
            new BuildCancelCommand(deliveredTick, [Site])));
        world.RunTicks(deliveredTick);
        var before = world.GetSnapshot();
        var onSite = before.Stocks.SiteStone;
        Assert.True(onSite > 0);

        world.Step();
        var after = world.GetSnapshot();

        Assert.Empty(after.BuildSites);
        Assert.Equal(0, after.Stocks.SiteStone);
        Assert.Contains(
            after.LooseItems,
            item => item.Resource == ResourceKind.Stone &&
                item.Position == Site &&
                item.Quantity >= onSite);
        Assert.Equal(
            after.Economy.StoneProduced,
            after.Stocks.LooseStone + after.Stocks.CarriedStone +
            after.Stocks.StoredStone + after.Stocks.SiteStone + after.Economy.StoneConsumed);
        Assert.DoesNotContain(after.Jobs, job => job.Kind == JobKind.Build);
        Assert.DoesNotContain(Site, after.Map.BuiltPostTiles);
    }

    // ---------------------------------------------------- 4. every explanation

    /// <summary>
    /// Every stop of the chain has to be answerable from the snapshot alone. The
    /// codes are read from the live simulation rather than from this test's list,
    /// so a new one cannot be added without this failing.
    /// </summary>
    [Fact]
    public void Every_build_status_the_simulation_publishes_is_reachable_and_named()
    {
        // Scouted rather than guessed: a blueprint marked while every block is
        // already booked towards the stockpile is the only way to reach the
        // "the stone exists but it is spoken for" reading.
        var bookedTick = FindTick(
            Log(
                new DigDesignateCommand(0, FarPocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [FarStockLeft, FarStockRight])),
            state => state.Stocks.CarriedStone > 0);

        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var log in new[]
                 {
                     BuildChain(),
                     // No stone at all: the blueprint states that plainly.
                     Log(new BuildDesignateCommand(0, [new GridPoint(12, 12)])),
                     // Reachable and supplied, but construction is switched off.
                     Log(
                         new DigDesignateCommand(0, Pocket),
                         new SetPriorityCommand(0, JobKind.Build, 0),
                         new BuildDesignateCommand(200, [new GridPoint(19, 9)])),
                     // Nobody may step on the site any more.
                     Log(
                         new DigDesignateCommand(0, Pocket),
                         new BuildDesignateCommand(200, [new GridPoint(19, 9)]),
                         new ZonePaintCommand(201, ZoneKind.Forbidden, [new GridPoint(19, 9)])),
                     // Carrying is switched off while a blueprint waits.
                     Log(
                         new DigDesignateCommand(0, Pocket),
                         new BuildDesignateCommand(200, [new GridPoint(19, 9)]),
                         new SetPriorityCommand(201, JobKind.Haul, 0)),
                     // Every block is already booked somewhere else. Dug at the
                     // far quarry, because the booking has to still be open when
                     // the blueprint lands and next to the hearth it is not.
                     Log(
                         new DigDesignateCommand(0, FarPocket),
                         new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [FarStockLeft, FarStockRight]),
                         new BuildDesignateCommand(bookedTick, [new GridPoint(12, 12)])),
                     // A builder chosen and still walking: the same chain run at
                     // the far quarry, where the walk lasts long enough to see.
                     Log(
                         new DigDesignateCommand(0, FarPocket),
                         new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [FarStockLeft, FarStockRight]),
                         new BuildDesignateCommand(BlueprintTick, [FarSite]),
                         new ZonePaintCommand(BlueprintTick, ZoneKind.TrainingGround, [FarSite]),
                         new SetPriorityCommand(BlueprintTick, JobKind.Drill, 4)),
                 })
        {
            var world = new PrototypeWorld(log);
            while (!world.IsComplete)
            {
                world.Step();
                foreach (var site in world.GetSnapshot().BuildSites)
                {
                    observed.Add(site.StatusCode);
                }
            }
        }

        Assert.Equal(
            new[]
            {
                "build_blocked_priority", "build_carrier_on_the_way", "build_haul_blocked",
                "build_in_progress", "build_no_stone", "build_ready", "build_reserved",
                "build_stone_reserved", "build_unreachable", "build_waiting_carrier",
            },
            observed.OrderBy(code => code, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_blueprint_with_no_stone_in_the_world_explains_itself_without_a_job()
    {
        var state = PrototypeScenario.Run(
            Log(new BuildDesignateCommand(0, [new GridPoint(12, 12)])),
            400).State;

        var site = Assert.Single(state.BuildSites);
        Assert.Equal("build_no_stone", site.StatusCode);
        Assert.Equal(0, site.Delivered);
        Assert.Equal(PrototypeTuning.BuildStoneCost, site.Required);
        Assert.True(site.Reachable);
        Assert.DoesNotContain(state.Jobs, job => job.Kind == JobKind.Build);
    }

    /// <summary>
    /// Build is last in enum order, so in a normal economy an idle creature
    /// reports a food reason rather than a construction one — that is the same
    /// tie-break rule Dig follows. Raise the priority above the food chain and the
    /// construction ladder becomes the reported diagnostic, with the numbers the
    /// reason follows from.
    /// </summary>
    [Fact]
    public void An_idle_creature_reports_the_construction_ladder_once_building_outranks_food()
    {
        var noStone = PrototypeScenario.Run(
            Log(
                new BuildDesignateCommand(0, [new GridPoint(12, 12)]),
                new SetPriorityCommand(0, JobKind.Build, 4)),
            300).State;
        Assert.Contains(
            noStone.Events,
            @event => @event.ReasonCode == "build_no_stone" &&
                @event.JobKind == JobKind.Build &&
                @event.Details["blueprints"] == 1);

        var noBlueprint = PrototypeScenario.Run(
            Log(new SetPriorityCommand(0, JobKind.Build, 4)),
            300).State;
        Assert.Contains(
            noBlueprint.Events,
            @event => @event.ReasonCode == "waiting_no_blueprint" &&
                @event.JobKind == JobKind.Build);

        var unreachable = PrototypeScenario.Run(
            Log(
                new BuildDesignateCommand(0, [new GridPoint(12, 12)]),
                new ZonePaintCommand(0, ZoneKind.Forbidden, [new GridPoint(12, 12)]),
                new SetPriorityCommand(0, JobKind.Build, 4)),
            300).State;
        Assert.Contains(
            unreachable.Events,
            @event => @event.ReasonCode == "build_unreachable" &&
                @event.JobKind == JobKind.Build);
    }

    [Fact]
    public void A_site_nobody_may_step_on_keeps_its_stone_and_says_it_is_unreachable()
    {
        var target = new GridPoint(19, 9);
        var state = PrototypeScenario.Run(
            Log(
                new DigDesignateCommand(0, Pocket),
                new BuildDesignateCommand(200, [target]),
                new ZonePaintCommand(201, ZoneKind.Forbidden, [target])),
            900).State;

        var site = Assert.Single(state.BuildSites);
        Assert.Equal("build_unreachable", site.StatusCode);
        Assert.False(site.Reachable);
        Assert.Equal(0, state.Economy.BuildsCompleted);
        Assert.DoesNotContain(target, state.Map.BuiltPostTiles);
        Assert.Equal(
            state.Economy.StoneProduced,
            state.Stocks.LooseStone + state.Stocks.CarriedStone +
            state.Stocks.StoredStone + state.Stocks.SiteStone + state.Economy.StoneConsumed);
    }

    [Fact]
    public void Build_priority_zero_keeps_the_blueprint_and_names_the_priority()
    {
        var state = PrototypeScenario.Run(
            Log(
                new DigDesignateCommand(0, Pocket),
                new BuildDesignateCommand(200, [new GridPoint(19, 9)]),
                new SetPriorityCommand(200, JobKind.Build, 0)),
            900).State;

        var site = Assert.Single(state.BuildSites);
        Assert.Equal("build_blocked_priority", site.StatusCode);
        Assert.Equal(0, state.Priorities[JobKind.Build]);
        Assert.Equal(0, state.Economy.BuildsCompleted);
        Assert.DoesNotContain(state.Jobs, job => job.Kind == JobKind.Build);
    }

    // ------------------------------------------------------- 5. determinism

    [Fact]
    public void The_whole_chain_replays_byte_for_byte_and_is_visible_in_canonical_json()
    {
        var first = PrototypeScenario.Run(BuildChain(), PrototypeTuning.SessionTicks);
        var second = PrototypeScenario.Run(BuildChain(), PrototypeTuning.SessionTicks);

        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.CanonicalEventLog, second.CanonicalEventLog);
        Assert.Equal(first.Checksum, second.Checksum);

        using var document = JsonDocument.Parse(first.CanonicalJson);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("buildSites", out _));
        var map = root.GetProperty("map");
        Assert.NotEmpty(map.GetProperty("buildFloorTiles").EnumerateArray());
        Assert.NotEmpty(map.GetProperty("builtPostTiles").EnumerateArray());
        var stocks = root.GetProperty("stocks");
        Assert.True(stocks.TryGetProperty("siteStone", out _));
        var economy = root.GetProperty("economy");
        Assert.True(economy.GetProperty("buildsCompleted").GetInt32() > 0);
        Assert.True(economy.GetProperty("stoneConsumed").GetInt32() > 0);
        Assert.True(economy.GetProperty("stoneDelivered").GetInt32() > 0);
        Assert.True(root.GetProperty("labor").GetProperty("buildTicks").GetInt32() > 0);
    }

    [Fact]
    public void A_different_blueprint_position_changes_the_canonical_checksum()
    {
        Assert.NotEqual(
            PrototypeScenario.Run(BuildChain(), PrototypeTuning.SessionTicks).Checksum,
            PrototypeScenario.Run(
                Log(
                    new DigDesignateCommand(0, Pocket),
                    new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight]),
                    new BuildDesignateCommand(BlueprintTick, [new GridPoint(20, 9)]),
                    new ZonePaintCommand(BlueprintTick, ZoneKind.TrainingGround, [new GridPoint(20, 9)]),
                    new SetPriorityCommand(BlueprintTick, JobKind.Drill, 3)),
                PrototypeTuning.SessionTicks).Checksum);
    }

    [Fact]
    public void Source_destination_and_reservation_are_a_deterministic_function_of_the_log()
    {
        var bookedTick = FindTick(
            BuildChain(),
            state => state.BuildSites.Any(site => site.IncomingReserved > 0));

        var first = PrototypeScenario.Run(BuildChain(), bookedTick).State;
        var second = PrototypeScenario.Run(BuildChain(), bookedTick).State;

        var firstStone = StoneJobs(first);
        Assert.NotEmpty(firstStone);
        Assert.Equal(
            firstStone.Select(job =>
                (job.Origin, job.SourceCell, job.StoreCell, job.ReservedBy, job.StoreReserved)),
            StoneJobs(second).Select(job =>
                (job.Origin, job.SourceCell, job.StoreCell, job.ReservedBy, job.StoreReserved)));
    }

    /// <summary>
    /// A site is booked exactly like a stockpile cell, so two carriers can never
    /// bring a third block to a post that only needs two.
    /// </summary>
    [Fact]
    public void A_construction_site_is_never_oversubscribed()
    {
        var world = new PrototypeWorld(BuildChain());
        while (!world.IsComplete)
        {
            world.Step();
            var state = world.GetSnapshot();
            foreach (var site in state.BuildSites)
            {
                var incoming = state.Jobs
                    .Where(job => job.StoreCell == site.Tile)
                    .Sum(job => job.StoreReserved);
                Assert.Equal(incoming, site.IncomingReserved);
                Assert.True(
                    site.Delivered + site.IncomingReserved <= site.Required,
                    $"t{state.Tick} site ({site.Tile.X},{site.Tile.Y}) " +
                    $"delivered={site.Delivered} incoming={site.IncomingReserved}");
            }

            // One Build job per site, one creature per job.
            var buildJobs = state.Jobs.Where(job => job.Kind == JobKind.Build).ToArray();
            Assert.Equal(buildJobs.Length, buildJobs.Select(job => job.Origin).Distinct().Count());
            var builders = buildJobs
                .Where(job => job.ReservedBy is not null)
                .Select(job => job.ReservedBy!.Value)
                .ToArray();
            Assert.Equal(builders.Length, builders.Distinct().Count());
        }
    }

    // ------------------------------------------------------- 6. no regression

    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    [InlineData("neglected")]
    public void Shipped_fixtures_have_no_construction_state_at_all(string fixtureName)
    {
        var state = PrototypeScenario.Run(
            LoadFixture(fixtureName),
            PrototypeTuning.SessionTicks).State;

        Assert.Empty(state.BuildSites);
        Assert.Empty(state.Map.BuiltPostTiles);
        Assert.Equal(0, state.Stocks.SiteStone);
        Assert.Equal(0, state.Economy.StoneDelivered);
        Assert.Equal(0, state.Economy.StoneConsumed);
        Assert.Equal(0, state.Economy.BuildsCompleted);
        Assert.Equal(0, state.Labor.BuildTicks);
        Assert.DoesNotContain(state.Jobs, job => job.Kind == JobKind.Build);
        Assert.DoesNotContain(state.Jobs, job => job.SourceCell is not null);
        Assert.DoesNotContain(
            state.Events,
            @event => @event.ReasonCode.StartsWith("build_", StringComparison.Ordinal) ||
                @event.ReasonCode is "stone_delivered" or "waiting_no_blueprint");
    }

    /// <summary>
    /// The strongest available regression guard: a session that marks a blueprint
    /// but never produces a single stone must behave exactly like the same session
    /// without it — same positions, same needs, same economy, same raid outcome.
    /// </summary>
    [Fact]
    public void A_blueprint_without_stone_changes_nothing_about_the_food_and_raid_session()
    {
        var plain = PrototypeScenario.Run(
            new PrototypeCommandLog("baseline", PrototypeTuning.DefaultSeed, []),
            PrototypeTuning.SessionTicks).State;
        var marked = PrototypeScenario.Run(
            Log(new BuildDesignateCommand(0, [new GridPoint(12, 12)])),
            PrototypeTuning.SessionTicks).State;

        Assert.Equal(plain.Economy, marked.Economy);
        Assert.Equal(plain.Labor, marked.Labor);
        Assert.Equal(plain.SessionResult, marked.SessionResult);
        Assert.Equal(plain.Stocks, marked.Stocks);
        Assert.Equal(
            plain.Creatures.Select(creature =>
                (creature.Id, creature.Position, creature.Satiety, creature.Fatigue,
                 creature.MartialForm, creature.Hp, creature.Mode, creature.MoveCount)),
            marked.Creatures.Select(creature =>
                (creature.Id, creature.Position, creature.Satiety, creature.Fatigue,
                 creature.MartialForm, creature.Hp, creature.Mode, creature.MoveCount)));
        Assert.Single(marked.BuildSites);
    }

    /// <summary>
    /// The authored gym still works. Removing the free posts is a different
    /// product decision; this step only makes new ones cost something.
    /// </summary>
    [Fact]
    public void The_authored_posts_are_untouched_by_the_construction_step()
    {
        var state = PrototypeScenario.Run(
            LoadFixture("prepared"),
            PrototypeTuning.FirstRaidTick + 1).State;

        Assert.Equal(
            4,
            state.Stations.Count(station => station.Kind == TileKind.Post));
        Assert.Empty(state.Map.BuiltPostTiles);
        Assert.True(state.Labor.PostOccupiedTicks > 0);
        Assert.True(state.Labor.DrillTicks > 0);
    }

    // ------------------------------------------------ documented walkthrough

    /// <summary>
    /// docs/engineering/PROTOTYPE_HEADLESS.md quotes concrete numbers from this
    /// shipped fixture. Without this test the document and the fixture could drift
    /// apart silently.
    /// </summary>
    [Fact]
    public void Build_demo_fixture_matches_the_documented_headless_walkthrough()
    {
        // The shipped fixture digs the quarry in the far corner and builds there.
        // It keeps its own coordinates rather than the ones this class uses,
        // which moved next to the hearth with the dungeon of Issue #117: the
        // walkthrough is about what the shipped journal does, and the document it
        // guards quotes those tiles.
        var demoSite = new GridPoint(25, 2);
        const int demoPocketSize = 4;
        var log = LoadFixture("build-demo");

        var beforeBlueprint = PrototypeScenario.Run(log, BlueprintTick).State;
        Assert.Equal(4, beforeBlueprint.Economy.DigsCompleted);
        Assert.Equal(4, beforeBlueprint.Stocks.StoredStone);
        Assert.Empty(beforeBlueprint.BuildSites);

        var afterBlueprint = PrototypeScenario.Run(log, BlueprintTick + 1).State;
        var site = Assert.Single(afterBlueprint.BuildSites);
        Assert.Equal(demoSite, site.Tile);
        Assert.Equal(0, site.Delivered);
        Assert.Equal(PrototypeTuning.BuildStoneCost, site.Required);

        var settled = PrototypeScenario.Run(log, BlueprintTick + 700).State;
        Assert.Empty(settled.BuildSites);
        Assert.Equal([demoSite], settled.Map.BuiltPostTiles);
        Assert.Equal(1, settled.Economy.BuildsCompleted);
        Assert.Equal(PrototypeTuning.BuildStoneCost, settled.Economy.StoneConsumed);
        Assert.Equal(
            demoPocketSize - PrototypeTuning.BuildStoneCost,
            settled.Stocks.StoredStone);
        Assert.Contains(settled.Jobs, job => job.Kind == JobKind.Drill && job.Origin == demoSite);
    }

    // ------------------------------------------------------------- helpers

    private static PrototypeCommandLog BuildChain()
    {
        return Log(
            new DigDesignateCommand(0, Pocket),
            new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight]),
            new BuildDesignateCommand(BlueprintTick, [Site]),
            new ZonePaintCommand(BlueprintTick, ZoneKind.TrainingGround, [Site]),
            // Four rather than three. The post this chain raises stands in the
            // quarry at the back of the dungeon of Issue #117, and on the default
            // priority a training job that far away loses every comparison to
            // work in the hearth, so the built post never gets used and the step
            // it demonstrates — that a post the player built produces real work —
            // cannot be seen. Saying "this matters more" is the lever the player
            // has.
            new SetPriorityCommand(BlueprintTick, JobKind.Drill, 4));
    }

    private static PrototypeCommandLog Log(params PrototypeCommand[] commands)
    {
        return new PrototypeCommandLog("custom", PrototypeTuning.DefaultSeed, commands);
    }

    private static PrototypeJobSnapshot[] StoneJobs(PrototypeSnapshot state)
    {
        return state.Jobs
            .Where(job => job.Kind == JobKind.Haul && job.Resource == ResourceKind.Stone)
            .OrderBy(job => job.JobId)
            .ToArray();
    }

    private static int FindTick(PrototypeCommandLog log, Func<PrototypeSnapshot, bool> predicate)
    {
        var world = new PrototypeWorld(log);
        while (!world.IsComplete)
        {
            world.Step();
            if (predicate(world.GetSnapshot()))
            {
                return world.CurrentTick;
            }
        }

        throw new Xunit.Sdk.XunitException("The scouted condition never happened.");
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
