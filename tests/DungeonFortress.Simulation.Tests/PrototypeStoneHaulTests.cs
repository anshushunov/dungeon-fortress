using System.Text;
using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Issue #26: excavated stone stops being a counter on the floor. The player
/// paints a material stockpile and creatures create and run Haul jobs on their
/// own. Every assertion reads canonical simulation state, and none of them can
/// address a creature or a job, so the tests cannot fake the autonomy or the
/// conservation they are checking.
/// </summary>
public sealed class PrototypeStoneHaulTests
{
    private static readonly GridPoint[] Pocket =
    [
        new(25, 1), new(25, 2), new(25, 3), new(26, 1),
    ];

    // Plain pre-existing floor just west of the dig pocket, outside every default
    // zone. Two cells hold exactly the four blocks the pocket yields.
    private static readonly GridPoint StockLeft = new(22, 1);
    private static readonly GridPoint StockRight = new(23, 1);

    // ---------------------------------------------------------------- 1. command

    [Theory]
    // Rock, the map boundary and the gate are not floor.
    [InlineData("[[25,1]]")]
    [InlineData("[[0,0]]")]
    [InlineData("[[27,13]]")]
    // Map features are floor-adjacent but are not plain floor.
    [InlineData("[[2,1]]")]
    [InlineData("[[10,7]]")]
    [InlineData("[[14,7]]")]
    [InlineData("[[20,3]]")]
    [InlineData("[[8,12]]")]
    // Atomicity: one bad tile rejects the whole stroke.
    [InlineData("[[22,1],[14,7]]")]
    public void MaterialStockpile_paint_is_rejected_outside_plain_floor(string tiles)
    {
        var json =
            $$"""
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"zone_paint","zoneKind":"MaterialStockpile","tiles":{{tiles}}}
            ]}
            """;

        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// Zoning runtime-excavated floor is step 3, not this one. Unlike a dig
    /// designation, this rule is decided entirely by the initial layout — a tile
    /// that will only become floor by digging is rock at tick 0 — so the whole
    /// document is rejected before any world exists, however late the command is.
    /// </summary>
    [Fact]
    public void MaterialStockpile_paint_is_rejected_on_ground_that_only_digging_creates()
    {
        var excavated = new GridPoint(25, 3);
        var late = new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            [
                new DigDesignateCommand(0, [excavated]),
                new ZonePaintCommand(400, ZoneKind.MaterialStockpile, [excavated]),
            ]);

        var rejection = Assert.Throws<InvalidDataException>(() => new PrototypeWorld(late));
        Assert.Contains("not plain floor", rejection.Message, StringComparison.Ordinal);

        // The tile really does become floor during that session, which is what
        // makes the pre-flight rejection a rule rather than an accident.
        var dug = PrototypeScenario.Run(
            new PrototypeCommandLog(
                "custom",
                PrototypeTuning.DefaultSeed,
                [new DigDesignateCommand(0, [excavated])]),
            400).State;
        Assert.Contains(excavated, dug.Map.ExcavatedTiles);
        Assert.Empty(dug.StockpileCells);
    }

    /// <summary>
    /// The brush must not re-derive "where may material go?"; the simulation
    /// publishes it, exactly as it publishes which rock may be dug. This test is
    /// what stops the two answers from drifting apart.
    /// </summary>
    [Fact]
    public void The_snapshot_publishes_exactly_where_a_stockpile_may_be_painted()
    {
        var state = PrototypeScenario.Run(DigOnly(), 400).State;
        var floor = state.Map.StockpileFloorTiles.ToHashSet();

        Assert.Contains(StockLeft, floor);
        Assert.Contains(StockRight, floor);
        Assert.DoesNotContain(new GridPoint(27, 13), floor);  // gate
        Assert.DoesNotContain(new GridPoint(0, 0), floor);    // map boundary
        Assert.DoesNotContain(new GridPoint(2, 1), floor);    // mushroom bed
        Assert.DoesNotContain(new GridPoint(10, 7), floor);   // kitchen station
        Assert.DoesNotContain(new GridPoint(14, 7), floor);   // larder
        Assert.DoesNotContain(new GridPoint(20, 3), floor);   // bunk
        Assert.DoesNotContain(new GridPoint(8, 12), floor);   // training post
        Assert.All(state.Map.RockTiles, tile => Assert.DoesNotContain(tile, floor));
        Assert.NotEmpty(state.Map.ExcavatedTiles);
        Assert.All(state.Map.ExcavatedTiles, tile => Assert.DoesNotContain(tile, floor));

        // The published list is exactly the accepted list, in both directions.
        Assert.All(floor, tile => PrototypeScenario.Run(
            Log(new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [tile])),
            1));
        foreach (var rejected in new GridPoint[] { new(14, 7), new(2, 1), new(8, 12), new(20, 3) })
        {
            Assert.Throws<InvalidDataException>(() => new PrototypeWorld(
                Log(new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [rejected]))));
        }
    }

    [Fact]
    public void MaterialStockpile_uses_the_existing_zone_commands_and_replays_identically()
    {
        const string json =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[
              {"tick":0,"kind":"zone_paint","zoneKind":"MaterialStockpile","tiles":[[23,1],[22,1]]},
              {"tick":5,"kind":"zone_erase","zoneKind":"MaterialStockpile","tiles":[[23,1]]}
            ]}
            """;

        var log = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json));
        var paint = Assert.IsType<ZonePaintCommand>(log.Commands[0]);
        Assert.Equal(ZoneKind.MaterialStockpile, paint.ZoneKind);
        // The parser normalises tile order, so two spellings of one stroke are the
        // same command — the property the whole replay guarantee rests on.
        Assert.Equal([StockLeft, StockRight], paint.Tiles);

        var early = PrototypeScenario.Run(log, 3).State;
        Assert.Equal(
            [StockLeft, StockRight],
            early.StockpileCells.Select(cell => cell.Position));
        Assert.All(early.StockpileCells, cell =>
        {
            Assert.Equal(0, cell.Stored);
            Assert.Equal(PrototypeTuning.StockpileCellCapacity, cell.Capacity);
            Assert.True(cell.Reachable);
            Assert.Equal("stockpile_empty", cell.StatusCode);
        });

        var late = PrototypeScenario.Run(log, 30);
        Assert.Equal([StockLeft], late.State.StockpileCells.Select(cell => cell.Position));
        Assert.Equal(late.Checksum, PrototypeScenario.Run(log, 30).Checksum);
    }

    // ------------------------------------------------- 2. no stockpile, no haul

    [Fact]
    public void Without_a_material_stockpile_loose_stone_stays_where_it_was_dug()
    {
        var state = PrototypeScenario.Run(DigOnly(), 700).State;

        Assert.Equal(Pocket.Length, state.Economy.DigsCompleted);
        Assert.Equal(Pocket.Length, state.Stocks.LooseStone);
        Assert.Equal(0, state.Stocks.StoredStone);
        Assert.Equal(0, state.Stocks.CarriedStone);
        Assert.Empty(state.StockpileCells);
        Assert.DoesNotContain(
            state.Jobs,
            job => job.Kind == JobKind.Haul && job.Resource == ResourceKind.Stone);
        Assert.DoesNotContain(
            state.Creatures,
            creature => creature.Carrying == ResourceKind.Stone);

        // The idleness must be explainable without reading the source.
        Assert.Contains(
            state.Events,
            @event => @event.ReasonCode == "waiting_no_stockpile" &&
                @event.Details["stockpileCells"] == 0 &&
                @event.Details["looseStone"] > 0);
    }

    [Fact]
    public void Haul_priority_zero_from_the_start_creates_no_stone_haul_at_all()
    {
        var blocked = PrototypeScenario.Run(
            Log(
                new SetPriorityCommand(0, JobKind.Haul, 0),
                new DigDesignateCommand(0, Pocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight])),
            700).State;

        Assert.Equal(Pocket.Length, blocked.Stocks.LooseStone);
        Assert.Equal(0, blocked.Stocks.StoredStone);
        Assert.Equal(0, blocked.Stocks.CarriedStone);
        Assert.DoesNotContain(
            blocked.Jobs,
            job => job.Kind == JobKind.Haul && job.Resource == ResourceKind.Stone);
        Assert.All(blocked.StockpileCells, cell => Assert.Equal("stockpile_empty", cell.StatusCode));
    }

    /// <summary>
    /// The lever the owner playtest pulls: freeze transport mid-flow, watch the
    /// stone sit still, hand the priority back and watch logistics restart with no
    /// further input. The pause is deliberately short — a long one lets the farm
    /// build a food backlog that legitimately outranks stone for a long time,
    /// which is a property of the shared Haul priority, not of this test.
    /// </summary>
    [Fact]
    public void Stopping_and_restoring_haul_priority_freezes_and_then_resumes_stone_transport()
    {
        const int pause = 60;
        var flowTick = FindTick(FullChain(), state => state.Stocks.StoredStone > 0);
        var world = new PrototypeWorld(
            Log(
                new DigDesignateCommand(0, Pocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight]),
                new SetPriorityCommand(flowTick, JobKind.Haul, 0),
                new SetPriorityCommand(
                    flowTick + pause,
                    JobKind.Haul,
                    PrototypeTuning.DefaultHaulPriority)));

        // A command scheduled for tick T is applied at the start of tick T, so the
        // world must run one tick past it before its effect is observable.
        world.RunTicks(flowTick + 1);
        var frozen = world.GetSnapshot();
        Assert.Equal(0, frozen.Priorities[JobKind.Haul]);
        Assert.True(frozen.Stocks.StoredStone > 0);
        Assert.True(frozen.Stocks.LooseStone > 0);

        world.RunTicks(flowTick + pause - world.CurrentTick);
        var stillFrozen = world.GetSnapshot();
        Assert.Equal(frozen.Stocks.StoredStone, stillFrozen.Stocks.StoredStone);
        Assert.Equal(0, stillFrozen.Stocks.CarriedStone);
        Assert.DoesNotContain(
            stillFrozen.Jobs,
            job => job.Kind == JobKind.Haul && job.Resource == ResourceKind.Stone);
        // Nothing was lost while the lever was down.
        Assert.Equal(
            stillFrozen.Economy.StoneProduced,
            stillFrozen.Stocks.LooseStone + stillFrozen.Stocks.StoredStone);

        world.RunTicks(PrototypeTuning.RaidTick - world.CurrentTick);
        var resumed = world.GetSnapshot();
        Assert.Equal(PrototypeTuning.DefaultHaulPriority, resumed.Priorities[JobKind.Haul]);
        Assert.True(
            resumed.Stocks.StoredStone > frozen.Stocks.StoredStone,
            $"frozen={frozen.Stocks.StoredStone}, resumed={resumed.Stocks.StoredStone}");
        Assert.Equal(0, resumed.Stocks.LooseStone);
        Assert.Equal(
            resumed.Economy.StoneProduced,
            resumed.Stocks.LooseStone + resumed.Stocks.CarriedStone + resumed.Stocks.StoredStone);
    }

    /// <summary>
    /// The player pulls one global lever mid-flight. A carrier that is already
    /// holding stone must put it down, not evaporate with it.
    /// </summary>
    [Fact]
    public void Priority_zero_while_carrying_drops_the_stone_instead_of_deleting_it()
    {
        var carryTick = FindTick(
            FullChain(),
            state => state.Stocks.CarriedStone > 0);
        var world = new PrototypeWorld(
            Log(
                new DigDesignateCommand(0, Pocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight]),
                new SetPriorityCommand(carryTick, JobKind.Haul, 0)));
        world.RunTicks(carryTick);
        var before = world.GetSnapshot();
        Assert.True(before.Stocks.CarriedStone > 0);

        world.Step();
        var after = world.GetSnapshot();

        Assert.Equal(0, after.Stocks.CarriedStone);
        Assert.Equal(
            before.Stocks.LooseStone + before.Stocks.CarriedStone,
            after.Stocks.LooseStone);
        Assert.Equal(before.Stocks.StoredStone, after.Stocks.StoredStone);
        Assert.All(after.Jobs, job => Assert.Null(job.StoreCell));
    }

    // ----------------------------------------------------- 3. determinism

    [Fact]
    public void Source_target_and_reservation_are_a_deterministic_function_of_the_log()
    {
        var reservedTick = FindTick(
            FullChain(),
            state => state.Jobs.Any(job =>
                job.Kind == JobKind.Haul &&
                job.Resource == ResourceKind.Stone &&
                job.ReservedBy is not null));

        var first = PrototypeScenario.Run(FullChain(), reservedTick).State;
        var second = PrototypeScenario.Run(FullChain(), reservedTick).State;

        var firstStone = StoneJobs(first);
        Assert.NotEmpty(firstStone);
        Assert.Equal(
            firstStone.Select(job => (job.Origin, job.StoreCell, job.ReservedBy, job.StoreReserved)),
            StoneJobs(second)
                .Select(job => (job.Origin, job.StoreCell, job.ReservedBy, job.StoreReserved)));

        // Each reserved job names exactly one creature, and no creature is on two.
        var carriers = firstStone
            .Where(job => job.ReservedBy is not null)
            .Select(job => job.ReservedBy!.Value)
            .ToArray();
        Assert.Equal(carriers.Length, carriers.Distinct().Count());
        Assert.All(carriers, id =>
            Assert.Single(first.Creatures.Where(creature =>
                creature.Id == id &&
                creature.CurrentJobId == firstStone
                    .Single(job => job.ReservedBy == id).JobId)));

        Assert.Equal(
            PrototypeScenario.Run(FullChain(), 700).Checksum,
            PrototypeScenario.Run(FullChain(), 700).Checksum);
    }

    [Fact]
    public void A_different_stockpile_position_changes_the_canonical_checksum()
    {
        Assert.NotEqual(
            PrototypeScenario.Run(FullChain(), 500).Checksum,
            PrototypeScenario.Run(
                Log(
                    new DigDesignateCommand(0, Pocket),
                    new ZonePaintCommand(
                        0,
                        ZoneKind.MaterialStockpile,
                        [new GridPoint(22, 1), new GridPoint(22, 2)])),
                500).Checksum);
    }

    [Fact]
    public void Stored_stone_survives_replay_byte_for_byte_and_is_visible_in_canonical_json()
    {
        var first = PrototypeScenario.Run(FullChain(), 700);
        var second = PrototypeScenario.Run(FullChain(), 700);

        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.CanonicalEventLog, second.CanonicalEventLog);
        Assert.Equal(first.Checksum, second.Checksum);

        using var document = JsonDocument.Parse(first.CanonicalJson);
        var root = document.RootElement;
        var cells = root.GetProperty("materialStockpile").EnumerateArray().ToArray();
        Assert.Equal(2, cells.Length);
        Assert.All(cells, cell =>
        {
            Assert.True(cell.TryGetProperty("stored", out _));
            Assert.True(cell.TryGetProperty("capacity", out _));
            Assert.True(cell.TryGetProperty("incomingReserved", out _));
            Assert.True(cell.TryGetProperty("statusCode", out _));
        });
        Assert.Equal(
            first.State.Stocks.StoredStone,
            cells.Sum(cell => cell.GetProperty("stored").GetInt32()));

        var stocks = root.GetProperty("stocks");
        foreach (var property in new[]
                 {
                     "looseStone", "carriedStone", "storedStone",
                     "reservedStone", "stockpileCapacity",
                 })
        {
            Assert.True(stocks.TryGetProperty(property, out _), property);
        }

        Assert.NotEmpty(root.GetProperty("zones").GetProperty("materialStockpile").EnumerateArray());
        Assert.True(root.GetProperty("economy").GetProperty("stoneStored").GetInt32() > 0);
        Assert.True(root.GetProperty("labor").GetProperty("stoneHaulTicks").GetInt32() > 0);
    }

    // ------------------------------------------------------- 4. conservation

    /// <summary>
    /// The invariant this whole step rests on, checked on every single tick of a
    /// session that digs, hauls, fills a stockpile, erases part of it and runs
    /// into a raid: stone is never created, destroyed or teleported.
    /// </summary>
    [Fact]
    public void Stone_is_conserved_on_every_tick_including_erase_and_raid()
    {
        var world = new PrototypeWorld(
            Log(
                new DigDesignateCommand(0, Pocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight]),
                new ZoneEraseCommand(900, ZoneKind.MaterialStockpile, [StockRight]),
                new ZonePaintCommand(1_000, ZoneKind.MaterialStockpile, [StockRight])));
        var sawCarried = false;
        var sawStored = false;
        var sawSpill = false;

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

            // Aggregates and per-entity state must agree, so neither can drift.
            Assert.Equal(looseByTile, state.Stocks.LooseStone);
            Assert.Equal(storedByCell, state.Stocks.StoredStone);
            Assert.Equal(carriedByCreature, state.Stocks.CarriedStone);
            Assert.Equal(
                state.Economy.StoneProduced,
                looseByTile + storedByCell + carriedByCreature);
            Assert.Equal(
                state.Economy.DigsCompleted * PrototypeTuning.DigStoneYield,
                state.Economy.StoneProduced);
            Assert.All(
                state.StockpileCells,
                cell => Assert.InRange(cell.Stored, 0, cell.Capacity));

            sawCarried |= carriedByCreature > 0;
            sawStored |= storedByCell > 0;
            sawSpill |= state.Economy.StoneSpilled > 0;
        }

        Assert.True(sawCarried, "No tick ever showed stone on a creature's back.");
        Assert.True(sawStored, "No tick ever showed stone inside the stockpile.");
        Assert.True(sawSpill, "The erase never spilled stored stone.");
        var final = world.GetSnapshot();
        Assert.Equal(Pocket.Length, final.Economy.StoneProduced);
    }

    // ------------------------------------------- 5. capacity and reservations

    [Fact]
    public void Capacity_is_never_oversubscribed_and_no_pile_is_picked_up_twice()
    {
        var world = new PrototypeWorld(FullChain());
        while (!world.IsComplete)
        {
            world.Step();
            var state = world.GetSnapshot();
            var stoneJobs = StoneJobs(state);

            foreach (var cell in state.StockpileCells)
            {
                var incoming = stoneJobs
                    .Where(job => job.StoreCell == cell.Position)
                    .Sum(job => job.StoreReserved);
                Assert.Equal(incoming, cell.IncomingReserved);
                Assert.True(
                    cell.Stored + cell.IncomingReserved <= cell.Capacity,
                    $"t{state.Tick} cell ({cell.Position.X},{cell.Position.Y}) " +
                    $"stored={cell.Stored} incoming={cell.IncomingReserved}");
            }

            // One job per pile, one creature per job, one booking per job.
            Assert.Equal(
                stoneJobs.Length,
                stoneJobs.Select(job => job.Origin).Distinct().Count());
            Assert.All(stoneJobs, job =>
            {
                Assert.InRange(
                    job.StoreReserved,
                    0,
                    PrototypeTuning.StoneCarryCapacity);
                if (job.ReservedBy is null)
                {
                    Assert.Null(job.StoreCell);
                    Assert.Equal(0, job.StoreReserved);
                }
                else
                {
                    Assert.NotNull(job.StoreCell);
                }
            });
            var reserved = stoneJobs
                .Where(job => job.ReservedBy is not null)
                .Select(job => job.ReservedBy!.Value)
                .ToArray();
            Assert.Equal(reserved.Length, reserved.Distinct().Count());
        }

        var final = world.GetSnapshot();
        Assert.Equal(Pocket.Length, final.Stocks.StoredStone);
        Assert.Equal(0, final.Stocks.LooseStone);
        Assert.All(final.StockpileCells, cell => Assert.Equal("stockpile_full", cell.StatusCode));
    }

    /// <summary>
    /// The capacity test above never loses a destination, so it cannot reach the
    /// replan path. This one churns the zone — erase a full cell, repaint it, then
    /// take a booked destination away under a live carrier and give it back — and
    /// holds the booking invariants on every tick of a full session.
    ///
    /// It guards the replan path found in review, where a shrunken booking used to
    /// let a job lift or deposit its original quantity and over-book a cell. The
    /// shrink itself needs a two-stone load meeting a cell with exactly one free
    /// slot; that combination is not reachable from any command log this step can
    /// write, so the clamps in PickUpStone/StoreCarriedStone stay defensive and
    /// only their consequence — the capacity invariant — is asserted here.
    /// </summary>
    [Fact]
    public void Bookings_survive_zone_churn_without_oversubscribing_a_cell()
    {
        // Erasing a filled cell spills a pile bigger than any single dig produces,
        // which is the only way a job's quantity can exceed one cell's capacity.
        var fullTick = -1;
        var filled = default(GridPoint);
        var fillScout = new PrototypeWorld(FullChain());
        while (!fillScout.IsComplete && fullTick < 0)
        {
            fillScout.Step();
            var cell = fillScout.GetSnapshot().StockpileCells.FirstOrDefault(
                item => item.Stored >= PrototypeTuning.StockpileCellCapacity);
            if (cell is not null)
            {
                fullTick = fillScout.CurrentTick;
                filled = cell.Position;
            }
        }

        Assert.True(fullTick > 0, "No stockpile cell ever filled up.");
        var churn = Log(
            new DigDesignateCommand(0, Pocket),
            new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight]),
            new ZoneEraseCommand(fullTick, ZoneKind.MaterialStockpile, [filled]),
            new ZonePaintCommand(fullTick + 8, ZoneKind.MaterialStockpile, [filled]));

        // Scouted rather than guessed: taking the destination away at a tick where
        // nothing is booked would leave the replan path untested.
        var bookedTick = -1;
        var doomed = default(GridPoint);
        var scout = new PrototypeWorld(churn);
        while (!scout.IsComplete && bookedTick < 0)
        {
            scout.Step();
            var state = scout.GetSnapshot();
            if (state.Tick <= fullTick + 8)
            {
                continue;
            }

            var booking = StoneJobs(state).FirstOrDefault(job => job.StoreCell is not null);
            if (booking is not null)
            {
                bookedTick = state.Tick;
                doomed = booking.StoreCell!.Value;
            }
        }

        Assert.True(bookedTick > 0, "No stone haul ever booked a cell after the churn.");

        var world = new PrototypeWorld(
            Log(
                new DigDesignateCommand(0, Pocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight]),
                new ZoneEraseCommand(fullTick, ZoneKind.MaterialStockpile, [filled]),
                new ZonePaintCommand(fullTick + 8, ZoneKind.MaterialStockpile, [filled]),
                new ZonePaintCommand(bookedTick, ZoneKind.Forbidden, [doomed]),
                new ZoneEraseCommand(bookedTick + 40, ZoneKind.Forbidden, [doomed])));
        var sawReplan = false;
        var sawBooking = false;

        while (!world.IsComplete)
        {
            world.Step();
            var state = world.GetSnapshot();

            foreach (var cell in state.StockpileCells)
            {
                Assert.True(
                    cell.Stored + cell.IncomingReserved <= cell.Capacity,
                    $"t{state.Tick} cell ({cell.Position.X},{cell.Position.Y}) " +
                    $"stored={cell.Stored} incoming={cell.IncomingReserved} " +
                    $"capacity={cell.Capacity}");
            }

            foreach (var job in StoneJobs(state).Where(job => job.PickedUp))
            {
                var carrier = state.Creatures.Single(
                    creature => creature.Id == job.ReservedBy);
                // A carrier may hold more than it can still put away after a
                // replan, but it must never hold a booking bigger than its load.
                Assert.True(
                    job.StoreReserved <= carrier.CarryAmount,
                    $"t{state.Tick} job #{job.JobId} booked {job.StoreReserved} " +
                    $"while carrying {carrier.CarryAmount}");
            }

            Assert.Equal(
                state.Economy.StoneProduced,
                state.Stocks.LooseStone + state.Stocks.CarriedStone + state.Stocks.StoredStone);

            sawBooking |= StoneJobs(state).Any(job => job.StoreReserved > 0);
            sawReplan |= state.Events.Any(
                @event => @event.ReasonCode == "stone_target_replanned");
        }

        // Without these the test would pass while protecting nothing.
        Assert.True(sawBooking, "The churn never produced a booked stockpile slot.");
        Assert.True(sawReplan, "The churn never forced a haul to replan its destination.");
        Assert.Equal(Pocket.Length, world.GetSnapshot().Economy.StoneProduced);
    }

    [Fact]
    public void A_stockpile_smaller_than_the_stone_leaves_the_remainder_loose_and_explains_why()
    {
        var state = PrototypeScenario.Run(
            Log(
                new DigDesignateCommand(0, Pocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft])),
            1_200).State;

        Assert.Equal(PrototypeTuning.StockpileCellCapacity, state.Stocks.StoredStone);
        Assert.Equal(
            Pocket.Length - PrototypeTuning.StockpileCellCapacity,
            state.Stocks.LooseStone);
        Assert.Equal("stockpile_full", Assert.Single(state.StockpileCells).StatusCode);
        Assert.Contains(
            state.Events,
            @event => @event.ReasonCode == "waiting_stockpile_full" &&
                @event.Details["stockpileFree"] == 0);
    }

    // ------------------------------------ 6. lost, forbidden and unreachable target

    [Fact]
    public void A_forbidden_destination_is_replanned_and_the_carried_stone_arrives_elsewhere()
    {
        var carryTick = FindTick(FullChain(), state => state.Stocks.CarriedStone > 0);
        var scout = PrototypeScenario.Run(FullChain(), carryTick).State;
        var doomed = StoneJobs(scout).First(job => job.PickedUp).StoreCell!.Value;
        var survivor = doomed == StockLeft ? StockRight : StockLeft;

        var world = new PrototypeWorld(
            Log(
                new DigDesignateCommand(0, Pocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight]),
                new ZonePaintCommand(carryTick, ZoneKind.Forbidden, [doomed])));
        world.RunTicks(carryTick);
        var before = world.GetSnapshot();
        var carried = before.Stocks.CarriedStone;
        Assert.True(carried > 0);

        world.Step();
        var after = world.GetSnapshot();

        Assert.Equal(carried, after.Stocks.CarriedStone);
        Assert.Equal(
            "stockpile_unreachable",
            after.StockpileCells.Single(cell => cell.Position == doomed).StatusCode);
        Assert.All(
            StoneJobs(after).Where(job => job.PickedUp),
            job => Assert.Equal(survivor, job.StoreCell));
        Assert.Contains(
            after.Events,
            @event => @event.ReasonCode == "stone_target_replanned" &&
                @event.Details["toX"] == survivor.X &&
                @event.Details["toY"] == survivor.Y);

        world.RunTicks(600);
        var settled = world.GetSnapshot();
        Assert.Equal(
            settled.Economy.StoneProduced,
            settled.Stocks.LooseStone + settled.Stocks.CarriedStone + settled.Stocks.StoredStone);
        Assert.Equal(0, settled.StockpileCells.Single(cell => cell.Position == doomed).Stored);
    }

    [Fact]
    public void Losing_the_only_destination_mid_transit_puts_the_stone_down_where_the_carrier_stands()
    {
        var single = Log(
            new DigDesignateCommand(0, Pocket),
            new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft]));
        var carryTick = FindTick(single, state => state.Stocks.CarriedStone > 0);

        var world = new PrototypeWorld(
            Log(
                new DigDesignateCommand(0, Pocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft]),
                new ZoneEraseCommand(carryTick, ZoneKind.MaterialStockpile, [StockLeft])));
        world.RunTicks(carryTick);
        var before = world.GetSnapshot();
        var carrier = before.Creatures.Single(creature => creature.Carrying == ResourceKind.Stone);
        var carried = carrier.CarryAmount;
        var storedBefore = before.Stocks.StoredStone;
        // The drop happens where the carrier stood when it lost its destination.
        // It is freed in the same tick and may walk on, so the position must be
        // read before the step, not after it.
        var dropTile = carrier.Position;

        world.Step();
        var after = world.GetSnapshot();

        Assert.Empty(after.StockpileCells);
        Assert.Equal(0, after.Stocks.CarriedStone);
        Assert.Equal(0, after.Stocks.StoredStone);
        // Everything that was stored plus everything that was carried is now loose.
        Assert.Equal(
            before.Stocks.LooseStone + carried + storedBefore,
            after.Stocks.LooseStone);
        Assert.Contains(
            after.LooseItems,
            item => item.Resource == ResourceKind.Stone && item.Position == dropTile);
        Assert.Contains(
            after.Events,
            @event => @event.ReasonCode == "stone_haul_cancelled" &&
                @event.Details["dropped"] == carried);
        Assert.DoesNotContain(
            after.Jobs,
            job => job.Kind == JobKind.Haul && job.Resource == ResourceKind.Stone);
    }

    [Fact]
    public void A_stockpile_walled_off_by_forbidden_is_an_observable_wait_not_a_lost_stone()
    {
        // (22,1) keeps its four orthogonal neighbours; making them Forbidden cuts
        // every route to it while the cell itself stays a legal stockpile.
        var state = PrototypeScenario.Run(
            Log(
                new DigDesignateCommand(0, Pocket),
                new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft]),
                new ZonePaintCommand(
                    0,
                    ZoneKind.Forbidden,
                    [new GridPoint(21, 1), new GridPoint(23, 1), new GridPoint(22, 2)])),
            900).State;

        Assert.Equal(Pocket.Length, state.Stocks.LooseStone);
        Assert.Equal(0, state.Stocks.StoredStone);
        Assert.Equal(0, state.Stocks.CarriedStone);
        Assert.Contains(state.Events, @event => @event.ReasonCode == "stone_unreachable");
        Assert.All(
            state.Creatures,
            creature => Assert.NotEqual(StockLeft, creature.Position));
    }

    // --------------------------------------------------------- 8. coexistence

    [Fact]
    public void Stone_and_food_share_one_haul_priority_without_starving_the_food_chain()
    {
        var withStone = PrototypeScenario.Run(FullChain(), PrototypeTuning.RaidTick + 1).State;
        var withoutStone = PrototypeScenario.Run(
            new PrototypeCommandLog("baseline", PrototypeTuning.DefaultSeed, []),
            PrototypeTuning.RaidTick + 1).State;

        // Both resource kinds really did move in the same session.
        Assert.True(withStone.Economy.StoneHaulsCompleted > 0);
        Assert.True(withStone.Economy.RawHaulsCompleted > 0);
        Assert.True(withStone.Economy.MealHaulsCompleted > 0);
        Assert.Contains(
            withStone.Events,
            @event => @event.ReasonCode == "stone_stored");

        // Stone carries no urgency bonus, so the food vertical stays healthy.
        Assert.True(
            withStone.Stocks.MealsProduced >= withoutStone.Stocks.MealsProduced - 6,
            $"withStone={withStone.Stocks.MealsProduced}, withoutStone={withoutStone.Stocks.MealsProduced}");
        Assert.Equal(0, PrototypeTuning.UrgencyHaulStone);

        // Stone labour is counted separately and the budget still adds up.
        Assert.True(withStone.Labor.StoneHaulTicks > 0);
        Assert.Equal(
            withStone.Tick * withStone.Creatures.Count,
            withStone.Labor.FoodWorkTicks +
            withStone.Labor.RestTicks +
            withStone.Labor.EatTicks +
            withStone.Labor.DrillTicks +
            withStone.Labor.WatchTicks +
            withStone.Labor.DigTicks +
            withStone.Labor.StoneHaulTicks +
            withStone.Labor.MusterTicks +
            withStone.Labor.IdleTicks);
    }

    // ------------------------------------------------------- 9. no regression

    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    [InlineData("neglected")]
    public void Shipped_fixtures_have_no_stone_logistics_state_at_all(string fixtureName)
    {
        var state = PrototypeScenario.Run(
            LoadFixture(fixtureName),
            PrototypeTuning.SessionTicks).State;

        Assert.Empty(state.StockpileCells);
        Assert.Equal(0, state.Stocks.StoredStone);
        Assert.Equal(0, state.Stocks.CarriedStone);
        Assert.Equal(0, state.Stocks.ReservedStone);
        Assert.Equal(0, state.Stocks.StockpileCapacity);
        Assert.Equal(0, state.Economy.StoneHaulsCompleted);
        Assert.Equal(0, state.Economy.StoneStored);
        Assert.Equal(0, state.Economy.StoneSpilled);
        Assert.Equal(0, state.Labor.StoneHaulTicks);
        Assert.DoesNotContain(
            state.Jobs,
            job => job.Kind == JobKind.Haul && job.Resource == ResourceKind.Stone);
        Assert.DoesNotContain(
            state.Events,
            @event => @event.ReasonCode.StartsWith("stone_", StringComparison.Ordinal) ||
                @event.ReasonCode is "waiting_no_stockpile" or "waiting_stockpile_full");
    }

    /// <summary>
    /// The strongest available regression guard: a session that paints a material
    /// stockpile but never produces a single stone must behave exactly like the
    /// same session without the zone — same positions, same needs, same economy,
    /// same labour budget, same raid outcome.
    /// </summary>
    [Fact]
    public void A_stockpile_without_stone_changes_nothing_about_the_food_and_raid_session()
    {
        var plain = PrototypeScenario.Run(
            new PrototypeCommandLog("baseline", PrototypeTuning.DefaultSeed, []),
            PrototypeTuning.SessionTicks).State;
        var zoned = PrototypeScenario.Run(
            Log(new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight])),
            PrototypeTuning.SessionTicks).State;

        Assert.Equal(plain.Economy, zoned.Economy);
        Assert.Equal(plain.Labor, zoned.Labor);
        Assert.Equal(plain.SessionResult, zoned.SessionResult);
        // The painted zone announces its own capacity; nothing else about the
        // session's resources may differ.
        Assert.Equal(0, plain.Stocks.StockpileCapacity);
        Assert.Equal(
            PrototypeTuning.StockpileCellCapacity * 2,
            zoned.Stocks.StockpileCapacity);
        Assert.Equal(plain.Stocks, zoned.Stocks with { StockpileCapacity = 0 });
        Assert.Equal(
            plain.Creatures.Select(creature =>
                (creature.Id, creature.Position, creature.Satiety, creature.Fatigue,
                 creature.MartialForm, creature.Hp, creature.Mode, creature.MoveCount)),
            zoned.Creatures.Select(creature =>
                (creature.Id, creature.Position, creature.Satiety, creature.Fatigue,
                 creature.MartialForm, creature.Hp, creature.Mode, creature.MoveCount)));
        Assert.Equal(
            plain.Raiders.Select(raider => (raider.Id, raider.Position, raider.Hp, raider.Mode)),
            zoned.Raiders.Select(raider => (raider.Id, raider.Position, raider.Hp, raider.Mode)));
    }

    // ------------------------------------------------ documented walkthrough

    /// <summary>
    /// docs/engineering/PROTOTYPE_HEADLESS.md quotes concrete numbers from this
    /// shipped fixture. Without this test the document and the fixture could drift
    /// apart silently.
    /// </summary>
    [Fact]
    public void Stone_haul_demo_fixture_matches_the_documented_headless_walkthrough()
    {
        var log = LoadFixture("stone-haul-demo");

        var beforeZone = PrototypeScenario.Run(log, 200).State;
        Assert.Equal(4, beforeZone.Economy.DigsCompleted);
        Assert.Equal(4, beforeZone.Stocks.LooseStone);
        Assert.Empty(beforeZone.StockpileCells);
        Assert.Contains(
            beforeZone.Events,
            @event => @event.ReasonCode == "waiting_no_stockpile");

        var afterZone = PrototypeScenario.Run(log, 210).State;
        Assert.Equal(
            [StockLeft, StockRight],
            afterZone.StockpileCells.Select(cell => cell.Position));
        Assert.Equal(4, afterZone.Stocks.StockpileCapacity);
        Assert.Equal(4, afterZone.Stocks.LooseStone);

        var settled = PrototypeScenario.Run(log, 700).State;
        Assert.Equal(0, settled.Stocks.LooseStone);
        Assert.Equal(4, settled.Stocks.StoredStone);
        Assert.Equal(4, settled.Economy.StoneHaulsCompleted);
        Assert.All(
            settled.StockpileCells,
            cell => Assert.Equal(PrototypeTuning.StockpileCellCapacity, cell.Stored));
    }

    // ------------------------------------------------------------- helpers

    private static PrototypeCommandLog FullChain()
    {
        return Log(
            new DigDesignateCommand(0, Pocket),
            new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight]));
    }

    private static PrototypeCommandLog DigOnly()
    {
        return Log(new DigDesignateCommand(0, Pocket));
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

    /// <summary>
    /// Finds the first tick at which a condition holds, so a later command can be
    /// scheduled exactly there. Scouting keeps the command log the only input and
    /// avoids poking the world from the outside.
    /// </summary>
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
