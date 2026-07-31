using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #39: the inspector explanations used to be checked by exactly one branch
/// per function — whichever one happened to land in a captured frame. Here every
/// branch is named, together with the wording it produces, and none of it needs
/// an engine.
/// </summary>
public sealed class InspectorExplanationTests
{
    // ------------------------------------------------ stockpile, every statusCode

    [Fact]
    public void Stockpile_unreachable_says_what_is_stored_stays()
    {
        var state = PresentationFixtures.FullChain(700);

        Assert.Equal(
            "1/2 stored. Forbidden: nobody may step here. What is stored stays; " +
            "nothing new arrives until you erase the Forbidden paint.",
            InspectorText.BuildStockpileExplanation(
                state,
                PresentationFixtures.Cell("stockpile_unreachable", stored: 1, reachable: false)));
    }

    [Fact]
    public void Stockpile_full_names_the_loose_stone_that_is_still_waiting()
    {
        var state = WithLooseStone(PresentationFixtures.FullChain(700), 3);

        Assert.Equal(
            "2/2 stored. Full. Loose 3 waits until you paint another cell with [M].",
            InspectorText.BuildStockpileExplanation(
                state,
                PresentationFixtures.Cell("stockpile_full", stored: 2)));
    }

    [Fact]
    public void Stockpile_incoming_reports_the_booked_slots_on_the_first_line()
    {
        var state = PresentationFixtures.FullChain(700);

        Assert.Equal(
            "1/2 stored, 1 booked. Every remaining slot is promised; a carrier is walking here.",
            InspectorText.BuildStockpileExplanation(
                state,
                PresentationFixtures.Cell("stockpile_incoming", stored: 1, incomingReserved: 1)));
    }

    [Fact]
    public void Stockpile_partial_promises_the_stone_back_if_the_cell_is_erased()
    {
        var state = PresentationFixtures.FullChain(700);

        Assert.Equal(
            "1/2 stored. Room left. Erasing this cell drops the stored stone back " +
            "here as a loose pile — it is never destroyed.",
            InspectorText.BuildStockpileExplanation(
                state,
                PresentationFixtures.Cell("stockpile_partial", stored: 1)));
    }

    [Fact]
    public void Empty_stockpile_tells_the_player_to_dig_when_no_stone_exists_yet()
    {
        var state = WithLooseStone(PresentationFixtures.FullChain(700), 0);

        Assert.Equal(
            "0/2 stored. Empty and ready. Dig rock and the stone will be brought here.",
            InspectorText.BuildStockpileExplanation(
                state,
                PresentationFixtures.Cell("stockpile_empty")));
    }

    [Fact]
    public void Empty_stockpile_points_at_the_loose_stone_when_some_already_exists()
    {
        var state = WithLooseStone(PresentationFixtures.FullChain(700), 4);

        Assert.Equal(
            "0/2 stored. Empty and ready. Loose stone exists; a free creature will choose the Haul job.",
            InspectorText.BuildStockpileExplanation(
                state,
                PresentationFixtures.Cell("stockpile_empty")));
    }

    /// <summary>
    /// A status code the panel has never seen must still produce the neutral
    /// reading rather than an empty section.
    /// </summary>
    [Fact]
    public void An_unknown_stockpile_status_falls_back_to_the_neutral_reading()
    {
        var state = WithLooseStone(PresentationFixtures.FullChain(700), 0);

        Assert.Equal(
            InspectorText.BuildStockpileExplanation(
                state,
                PresentationFixtures.Cell("stockpile_empty")),
            InspectorText.BuildStockpileExplanation(
                state,
                PresentationFixtures.Cell("stockpile_invented_by_a_later_step")));
    }

    /// <summary>
    /// The five codes the simulation actually publishes, checked against the live
    /// snapshot rather than against this test's own list, so a new code cannot be
    /// added to <c>PrototypeWorld</c> without this failing.
    /// </summary>
    [Fact]
    public void Every_stockpile_status_the_simulation_publishes_is_covered_here()
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        // Three blocks into two cells of two, next to the hearth. Three rather
        // than four because a cell only reads `stockpile_partial` while it holds
        // an odd block, and next to the hearth rather than at the far quarry
        // because the third block has to actually arrive inside the session.
        var world = new PrototypeWorld(PresentationFixtures.Log(
            new DigDesignateCommand(0, [.. PresentationFixtures.NearWall.Take(3)]),
            new ZonePaintCommand(
                0,
                ZoneKind.MaterialStockpile,
                [PresentationFixtures.NearStockLeft, PresentationFixtures.NearStockRight]),
            new ZonePaintCommand(600, ZoneKind.Forbidden, [PresentationFixtures.NearStockLeft])));
        while (!world.IsComplete)
        {
            world.Step();
            foreach (var cell in world.GetSnapshot().StockpileCells)
            {
                observed.Add(cell.StatusCode);
            }
        }

        Assert.Equal(
            new[]
            {
                "stockpile_empty", "stockpile_full", "stockpile_incoming",
                "stockpile_partial", "stockpile_unreachable",
            },
            observed.OrderBy(code => code, StringComparer.Ordinal).ToArray());
    }

    // ------------------------------------------------ loose stone, every branch

    [Fact]
    public void A_claimed_pile_names_the_carrier_and_the_booked_cell()
    {
        var state = PresentationFixtures.FullChain(700);
        var carrier = state.Creatures[3];
        var loose = new PrototypeLooseItemSnapshot(new GridPoint(25, 2), ResourceKind.Stone, 1);

        Assert.Equal(
            $"1 loose here. {carrier.Name} chose this job, taking it to (23,1).",
            InspectorText.BuildLooseStoneExplanation(
                state,
                loose,
                [PresentationFixtures.StoneHaul(loose.Position, carrier.Id, PresentationFixtures.StockRight)]));
    }

    [Fact]
    public void A_claimed_pile_without_a_destination_yet_says_the_cell_is_being_chosen()
    {
        var state = PresentationFixtures.FullChain(700);
        var carrier = state.Creatures[0];
        var loose = new PrototypeLooseItemSnapshot(new GridPoint(25, 2), ResourceKind.Stone, 2);

        Assert.Equal(
            $"2 loose here. {carrier.Name} chose this job, taking it to a cell being chosen.",
            InspectorText.BuildLooseStoneExplanation(
                state,
                loose,
                [PresentationFixtures.StoneHaul(loose.Position, carrier.Id, storeCell: null, storeReserved: 0)]));
    }

    [Fact]
    public void Haul_priority_zero_is_reported_before_anything_about_stockpiles()
    {
        var state = PresentationFixtures.FullChain(700) is var full
            ? WithHaulPriority(full, 0)
            : throw new InvalidOperationException();
        var loose = new PrototypeLooseItemSnapshot(new GridPoint(25, 2), ResourceKind.Stone, 1);

        Assert.Equal(
            "1 loose here. Haul priority is 0: no carrying job exists. Raise it with [J] and +/-.",
            InspectorText.BuildLooseStoneExplanation(state, loose, []));
    }

    [Fact]
    public void Without_any_stockpile_the_pile_explains_which_key_creates_one()
    {
        var state = PresentationFixtures.DigOnly(700);
        var loose = Assert.Single(
            state.LooseItems.Where(item =>
                item.Resource == ResourceKind.Stone && item.Position == new GridPoint(25, 1)));

        Assert.Empty(state.StockpileCells);
        Assert.Equal(
            $"{loose.Quantity} loose here. No material stockpile yet. Press [M], paint plain floor.",
            InspectorText.BuildLooseStoneExplanation(state, loose, []));
    }

    [Fact]
    public void A_stockpile_nobody_may_step_on_is_reported_as_forbidden_rather_than_as_full()
    {
        var state = PresentationFixtures.FullChain(700);
        state = state with
        {
            StockpileCells =
            [
                PresentationFixtures.Cell("stockpile_unreachable", stored: 1, reachable: false),
            ],
        };
        var loose = new PrototypeLooseItemSnapshot(new GridPoint(25, 2), ResourceKind.Stone, 1);

        Assert.Equal(
            "1 loose here. Every stockpile cell is Forbidden: nobody may step on it.",
            InspectorText.BuildLooseStoneExplanation(state, loose, []));
    }

    [Fact]
    public void A_full_stockpile_shows_stored_plus_booked_against_capacity()
    {
        var state = PresentationFixtures.FullChain(700);
        state = state with
        {
            StockpileCells = [PresentationFixtures.Cell("stockpile_full", stored: 2)],
            Stocks = state.Stocks with
            {
                StoredStone = 3,
                ReservedStone = 1,
                StockpileCapacity = 4,
            },
        };
        var loose = new PrototypeLooseItemSnapshot(new GridPoint(25, 2), ResourceKind.Stone, 1);

        Assert.Equal(
            "1 loose here. Stockpile full: 3 stored + 1 booked of 4. Paint another cell with [M].",
            InspectorText.BuildLooseStoneExplanation(state, loose, []));
    }

    [Fact]
    public void Free_slots_are_counted_so_the_wait_is_readable_as_a_queue()
    {
        var state = PresentationFixtures.FullChain(700);
        state = state with
        {
            StockpileCells = [PresentationFixtures.Cell("stockpile_partial", stored: 1)],
            Stocks = state.Stocks with
            {
                StoredStone = 1,
                ReservedStone = 1,
                StockpileCapacity = 4,
            },
        };
        var loose = new PrototypeLooseItemSnapshot(new GridPoint(25, 2), ResourceKind.Stone, 1);

        Assert.Equal(
            "1 loose here. 2 slot(s) free; waiting for a creature to be free.",
            InspectorText.BuildLooseStoneExplanation(state, loose, []));
    }

    // ----------------------------------------------------- dig, every statusCode

    [Fact]
    public void An_unreachable_designation_tells_the_player_to_dig_a_neighbour_first()
    {
        var state = Designated(
            PresentationFixtures.Designation("dig_unreachable", new GridPoint(26, 1)));

        Assert.Equal(
            "designated, but no free neighbouring floor to work from.\n" +
            "Dig an adjacent tile first; nobody is teleported into rock.\n" +
            "result → floor + 1 loose stone",
            InspectorText.BuildDigExplanation(state.Shown(), new GridPoint(26, 1)));
    }

    [Fact]
    public void A_designation_blocked_by_priority_reports_the_priority_it_is_blocked_by()
    {
        var state = WithDigPriority(
            Designated(PresentationFixtures.Designation("dig_blocked_priority", new GridPoint(25, 1))),
            0);

        Assert.Equal(
            "designated, but the Dig priority is 0.\n" +
            "Raise it with [J] and +/- to let creatures take the job.\n" +
            "result → floor + 1 loose stone",
            InspectorText.BuildDigExplanation(state.Shown(), new GridPoint(25, 1)));
    }

    [Fact]
    public void Work_in_progress_reports_who_is_digging_and_how_far_along_it_is()
    {
        var state = PresentationFixtures.DigOnly(1);
        var digger = state.Creatures[2];
        state = Designated(
            state,
            PresentationFixtures.Designation(
                "dig_in_progress",
                new GridPoint(25, 1),
                reservedBy: digger.Id,
                workTile: new GridPoint(24, 1),
                progressTicks: 7,
                requiredTicks: 20));

        Assert.Equal(
            $"digging 7/20 ticks by {digger.Name} from (24,1).\n" +
            "result → floor + 1 loose stone",
            InspectorText.BuildDigExplanation(state.Shown(), new GridPoint(25, 1)));
    }

    [Fact]
    public void A_reserved_designation_reports_who_volunteered_and_where_they_walk()
    {
        var state = PresentationFixtures.DigOnly(1);
        var digger = state.Creatures[5];
        state = Designated(
            state,
            PresentationFixtures.Designation(
                "dig_reserved",
                new GridPoint(25, 1),
                reservedBy: digger.Id,
                workTile: new GridPoint(24, 1)));

        Assert.Equal(
            $"{digger.Name} chose this job and is walking to (24,1).\n" +
            "result → floor + 1 loose stone",
            InspectorText.BuildDigExplanation(state.Shown(), new GridPoint(25, 1)));
    }

    [Fact]
    public void A_waiting_designation_explains_that_the_crew_chooses_who_goes()
    {
        var state = Designated(
            PresentationFixtures.Designation("dig_waiting", new GridPoint(25, 1)));

        Assert.Equal(
            "designated and reachable; waiting for a creature to be free.\n" +
            "You mark intent, the crew decides who goes.\n" +
            "result → floor + 1 loose stone",
            InspectorText.BuildDigExplanation(state.Shown(), new GridPoint(25, 1)));
    }

    [Fact]
    public void Undesignated_diggable_rock_names_the_key_that_designates_it()
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.DiggableTiles[0];

        Assert.Equal(
            "diggable internal rock. Press [D] and click or drag to designate.\n" +
            "result → floor + 1 loose stone",
            InspectorText.BuildDigExplanation(state.Shown(), rock));
    }

    [Theory]
    [InlineData(0, 0, "map boundary")]
    [InlineData(12, 12, "floor, feature or gate")]
    [InlineData(27, 13, "floor, feature or gate")]
    public void Rock_that_cannot_be_dug_gets_the_terse_reason(int x, int y, string reason)
    {
        var state = PresentationFixtures.Baseline(1);

        Assert.Equal(
            $"not diggable: {reason}.",
            InspectorText.BuildDigExplanation(state.Shown(), new GridPoint(x, y)));
    }

    [Fact]
    public void Already_excavated_ground_is_reported_as_excavated_rather_than_as_floor()
    {
        var state = PresentationFixtures.DigOnly(700);
        var excavated = state.Map.ExcavatedTiles[0];

        Assert.Equal(
            "not diggable: already excavated.",
            InspectorText.BuildDigExplanation(state.Shown(), excavated));
        Assert.Equal("already excavated", InspectorText.ShortUndiggableReason(state, excavated));
        Assert.Equal("it has already been excavated", InspectorText.UndiggableReason(state, excavated));
    }

    // ------------------------------------------------------- refusal wording

    [Fact]
    public void The_dig_brush_refusals_read_as_full_sentences()
    {
        var state = PresentationFixtures.Baseline(1);

        Assert.Equal(
            "the map boundary holds the dungeon in",
            InspectorText.UndiggableReason(state, new GridPoint(0, 0)));
        Assert.Equal(
            "it is floor, a feature or the gate, not rock",
            InspectorText.UndiggableReason(state, new GridPoint(27, 13)));
    }

    [Fact]
    public void The_stockpile_brush_refusals_name_the_three_kinds_of_tile_it_rejects()
    {
        var fresh = PresentationFixtures.Baseline(1);
        var dug = PresentationFixtures.DigOnly(700);

        Assert.Equal(
            "it is still rock",
            InspectorText.UnstockpileableReason(fresh, new GridPoint(0, 0)));
        // Reworded by Issue #48: excavated ground stopped being "the next step"
        // and became buildable ground that still cannot store material.
        Assert.Equal(
            "freshly excavated ground can hold a building, but not stored material",
            InspectorText.UnstockpileableReason(dug, dug.Map.ExcavatedTiles[0]));
        Assert.Equal(
            "it is a bed, a station, the larder, a bunk, a post or the gate — not plain floor",
            InspectorText.UnstockpileableReason(fresh, new GridPoint(14, 7)));
    }

    // -------------------------------------------------------- carrier route

    [Fact]
    public void A_creature_with_no_stone_job_contributes_no_route_line_at_all()
    {
        var creature = PresentationFixtures.Baseline(1).Creatures[0];

        Assert.Equal(string.Empty, InspectorText.DescribeCarrierRoute(creature, null));
    }

    [Fact]
    public void Stone_in_hand_without_a_job_says_it_will_be_put_down_here()
    {
        var creature = PresentationFixtures.Baseline(1).Creatures[0] with
        {
            Carrying = ResourceKind.Stone,
            CarryAmount = 1,
        };

        Assert.Equal(
            "stone in hand, no haul job: it will be put down here\n",
            InspectorText.DescribeCarrierRoute(creature, null));
    }

    [Fact]
    public void A_carrier_on_the_way_to_the_pile_reports_the_pile_not_the_destination()
    {
        var creature = PresentationFixtures.Baseline(1).Creatures[0];

        Assert.Equal(
            "stone haul: walking to pile (25,2), booked (23,1) x1\n",
            InspectorText.DescribeCarrierRoute(
                creature,
                PresentationFixtures.StoneHaul(
                    new GridPoint(25, 2),
                    creature.Id,
                    PresentationFixtures.StockRight)));
    }

    [Fact]
    public void A_loaded_carrier_reports_the_cell_it_is_walking_to()
    {
        var creature = PresentationFixtures.Baseline(1).Creatures[0];

        Assert.Equal(
            "stone haul: carrying to (23,1), booked (23,1) x1\n",
            InspectorText.DescribeCarrierRoute(
                creature,
                PresentationFixtures.StoneHaul(
                    new GridPoint(25, 2),
                    creature.Id,
                    PresentationFixtures.StockRight,
                    pickedUp: true)));
    }

    [Fact]
    public void A_stone_haul_that_lost_its_booking_says_so_instead_of_inventing_a_cell()
    {
        var creature = PresentationFixtures.Baseline(1).Creatures[0];

        Assert.Equal(
            "stone haul: walking to pile (25,2), no stockpile cell booked\n",
            InspectorText.DescribeCarrierRoute(
                creature,
                PresentationFixtures.StoneHaul(
                    new GridPoint(25, 2),
                    creature.Id,
                    storeCell: null,
                    storeReserved: 0)));
    }

    /// <summary>
    /// Food shares the <c>Haul</c> kind with stone but not the stone wording.
    /// </summary>
    [Fact]
    public void A_food_haul_is_not_described_as_a_stone_route()
    {
        var creature = PresentationFixtures.Baseline(1).Creatures[0];
        var mealHaul = PresentationFixtures.StoneHaul(
            new GridPoint(2, 1),
            creature.Id,
            new GridPoint(14, 7)) with
        {
            Resource = ResourceKind.Meal,
        };

        Assert.Equal(string.Empty, InspectorText.DescribeCarrierRoute(creature, mealHaul));
    }

    // -------------------------------------------------------- tile description

    [Fact]
    public void Every_kind_of_tile_reads_differently()
    {
        var fresh = PresentationFixtures.Baseline(1);
        var dug = PresentationFixtures.DigOnly(700);

        Assert.Equal("rock (internal)", InspectorText.TileDescription(fresh.Shown(), new GridPoint(25, 1)));
        Assert.Equal("rock (map boundary)", InspectorText.TileDescription(fresh.Shown(), new GridPoint(0, 0)));
        Assert.Equal("floor (excavated)", InspectorText.TileDescription(dug.Shown(), dug.Map.ExcavatedTiles[0]));
        Assert.Equal("mushroom bed", InspectorText.TileDescription(fresh.Shown(), fresh.Beds[0].Position));
        Assert.Equal("gate", InspectorText.TileDescription(fresh.Shown(), new GridPoint(27, 13)));
        Assert.Equal("floor", InspectorText.TileDescription(fresh.Shown(), new GridPoint(12, 12)));
        var station = fresh.Stations[0];
        Assert.Equal(
            station.Kind.ToString(),
            InspectorText.TileDescription(fresh.Shown(), station.Position));
    }

    // ------------------------------------------------------------- whole panel

    [Fact]
    public void With_nothing_selected_the_panel_explains_who_owns_the_world()
    {
        Assert.Equal(
            "INSPECTOR\n\nClick a creature or map cell.\n\n" +
            "The world is a read-only projection of PrototypeWorld; Godot owns only selection, UI tempo and drawing.",
            InspectorText.Build(PresentationFixtures.Baseline(1).Shown(), null, null));
    }

    /// <summary>
    /// The QUARTERS rule is appended to the zone list rather than being a section
    /// of its own, which is easy to lose in a reflow.
    /// </summary>
    [Fact]
    public void A_quarters_cell_carries_its_rest_rule_in_the_zone_line()
    {
        var state = PresentationFixtures.Baseline(1);
        var quarters = state.Zones[ZoneKind.Quarters][0];

        Assert.Contains(
            "zones Quarters, QUARTERS: rest only at fatigue 50+, free bunk\n",
            InspectorText.Build(state.Shown(), null, quarters),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_selected_creature_leads_with_its_identity_status_and_hit_points()
    {
        var state = PresentationFixtures.Baseline(1);
        var creature = state.Creatures[0];

        var text = InspectorText.Build(state.Shown(), creature.Id, creature.Position);

        Assert.StartsWith(
            $"CREATURE #{creature.Id} · {creature.Name} — ALIVE HP {creature.Hp}/{creature.MaxHp}\n\n",
            text,
            StringComparison.Ordinal);
        // The status used to be repeated above the decision details as well. It
        // was dropped with Issue #117: the inspector gained a line for what the
        // creature will not go near and a line of plain English for its last
        // decision, and the HUD overflow guard refuses a panel that needs more
        // lines than it has. The header above already carries the same two facts.
        Assert.DoesNotContain("STATUS ", text, StringComparison.Ordinal);
        // A selected creature wins over a selected cell, so no CELL section appears.
        Assert.DoesNotContain("CELL (", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_creature_with_no_decision_details_says_none_rather_than_showing_a_blank()
    {
        var state = PresentationFixtures.Baseline(1);
        var creature = state.Creatures[0] with
        {
            CurrentJobId = null,
            LastDecision = new PrototypeDecision(0, "waiting_no_job_available", new Dictionary<string, int>()),
        };
        state = state with { Creatures = [creature, .. state.Creatures.Skip(1)] };

        var text = InspectorText.Build(state.Shown(), creature.Id, null);

        Assert.Contains("job none\n", text, StringComparison.Ordinal);
        Assert.EndsWith("is standing about: nothing to do.\nnone", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cell_with_both_a_pile_and_a_stockpile_shows_the_loose_section_first()
    {
        var state = PresentationFixtures.FullChain(700);
        var cell = PresentationFixtures.StockRight;
        state = state with
        {
            StockpileCells = [PresentationFixtures.Cell("stockpile_partial", stored: 1)],
            LooseItems = [new PrototypeLooseItemSnapshot(cell, ResourceKind.Stone, 1)],
        };

        var text = InspectorText.Build(state.Shown(), null, cell);

        Assert.True(
            text.IndexOf("LOOSE STONE", StringComparison.Ordinal) <
            text.IndexOf("STOCKPILE", StringComparison.Ordinal),
            text);
        Assert.Contains("\nDIG\n", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static PrototypeSnapshot WithLooseStone(PrototypeSnapshot state, int looseStone) =>
        state with { Stocks = state.Stocks with { LooseStone = looseStone } };

    private static PrototypeSnapshot WithHaulPriority(PrototypeSnapshot state, int priority) =>
        WithPriority(state, JobKind.Haul, priority);

    private static PrototypeSnapshot WithDigPriority(PrototypeSnapshot state, int priority) =>
        WithPriority(state, JobKind.Dig, priority);

    private static PrototypeSnapshot WithPriority(
        PrototypeSnapshot state,
        JobKind job,
        int priority)
    {
        var priorities = state.Priorities.ToDictionary(pair => pair.Key, pair => pair.Value);
        priorities[job] = priority;
        return state with { Priorities = priorities };
    }

    private static PrototypeSnapshot Designated(PrototypeDigDesignationSnapshot designation) =>
        Designated(PresentationFixtures.DigOnly(1), designation);

    private static PrototypeSnapshot Designated(
        PrototypeSnapshot state,
        PrototypeDigDesignationSnapshot designation) =>
        state with { DigDesignations = [designation] };
}
