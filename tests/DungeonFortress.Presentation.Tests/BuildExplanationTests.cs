using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #48: the construction chain has to be readable without the log. Every
/// branch is named here together with the wording it produces, and none of it
/// needs an engine — the boundary ADR 0011 draws.
/// </summary>
public sealed class BuildExplanationTests
{
    private const string Result =
        "\nresult → a training post; Drill work needs a TrainingGround zone here";

    // ------------------------------------------------ blueprint, every statusCode

    [Fact]
    public void A_blueprint_with_no_stone_anywhere_points_at_the_dig_brush()
    {
        Assert.Equal(
            "training post blueprint · stone 0/2. There is no stone in the world yet.\n" +
            "Press [D] and mark rock; a finished dig leaves one block." + Result,
            InspectorText.BuildBlueprintExplanation(
                PresentationFixtures.Baseline(1),
                Site("build_no_stone")));
    }

    [Fact]
    public void A_blueprint_whose_stone_is_spoken_for_says_so_instead_of_no_stone()
    {
        Assert.Equal(
            "training post blueprint · stone 0/2. The stone that exists is already " +
            "booked by another job.\nDig more rock, or wait for a carrier to free up." + Result,
            InspectorText.BuildBlueprintExplanation(
                PresentationFixtures.FullChain(700),
                Site("build_stone_reserved")));
    }

    [Fact]
    public void A_blueprint_waiting_for_a_volunteer_says_the_stone_is_free()
    {
        Assert.Equal(
            "training post blueprint · stone 0/2. Stone is available and free; " +
            "waiting for a creature to choose the Haul job." + Result,
            InspectorText.BuildBlueprintExplanation(
                PresentationFixtures.FullChain(700),
                Site("build_waiting_carrier")));
    }

    [Fact]
    public void A_booked_delivery_is_reported_on_the_first_line_and_in_the_sentence()
    {
        Assert.Equal(
            "training post blueprint · stone 1/2, 1 booked. A carrier is walking here " +
            "with the rest of the stone." + Result,
            InspectorText.BuildBlueprintExplanation(
                PresentationFixtures.FullChain(700),
                Site("build_carrier_on_the_way", delivered: 1, incomingReserved: 1)));
    }

    [Fact]
    public void A_supplied_blueprint_says_the_crew_decides_who_builds()
    {
        Assert.Equal(
            "training post blueprint · stone 2/2. Material complete; waiting for a " +
            "creature to be free.\nYou mark intent, the crew decides who builds." + Result,
            InspectorText.BuildBlueprintExplanation(
                PresentationFixtures.FullChain(700),
                Site("build_ready", delivered: 2)));
    }

    [Fact]
    public void A_reserved_blueprint_names_the_creature_that_volunteered()
    {
        var state = PresentationFixtures.FullChain(700);
        var builder = state.Creatures[4];

        Assert.Equal(
            $"training post blueprint · stone 2/2. {builder.Name} chose this job and " +
            "is walking here." + Result,
            InspectorText.BuildBlueprintExplanation(
                state,
                Site("build_reserved", delivered: 2, reservedBy: builder.Id)));
    }

    [Fact]
    public void Work_in_progress_reports_who_is_building_and_how_far_along_it_is()
    {
        var state = PresentationFixtures.FullChain(700);
        var builder = state.Creatures[7];

        Assert.Equal(
            $"training post blueprint · stone 2/2. Building 11/30 ticks by {builder.Name}." +
            Result,
            InspectorText.BuildBlueprintExplanation(
                state,
                Site(
                    "build_in_progress",
                    delivered: 2,
                    reservedBy: builder.Id,
                    progressTicks: 11,
                    requiredTicks: 30)));
    }

    [Fact]
    public void A_blueprint_blocked_by_priority_reports_the_priority_it_is_blocked_by()
    {
        var state = WithPriority(PresentationFixtures.FullChain(700), JobKind.Build, 0);

        Assert.Equal(
            "training post blueprint · stone 0/2. Build priority is 0.\n" +
            "Raise it with [J] and +/- to let creatures take the job." + Result,
            InspectorText.BuildBlueprintExplanation(state, Site("build_blocked_priority")));
    }

    [Fact]
    public void A_blueprint_blocked_by_haul_priority_names_the_other_lever()
    {
        var state = WithPriority(PresentationFixtures.FullChain(700), JobKind.Haul, 0);

        Assert.Equal(
            "training post blueprint · stone 0/2. Haul priority is 0: nothing is being " +
            "carried anywhere.\nRaise it with [J] and +/-." + Result,
            InspectorText.BuildBlueprintExplanation(state, Site("build_haul_blocked")));
    }

    [Fact]
    public void An_unreachable_site_says_nothing_can_arrive_and_nothing_can_be_built()
    {
        Assert.Equal(
            "training post blueprint · stone 1/2. Nobody may step on this tile, so " +
            "nothing can be brought here and nothing can be built.\n" +
            "Erase the Forbidden paint." + Result,
            InspectorText.BuildBlueprintExplanation(
                PresentationFixtures.FullChain(700),
                Site("build_unreachable", delivered: 1, reachable: false)));
    }

    /// <summary>
    /// A status code the panel has never seen must still produce the neutral
    /// reading rather than an empty section.
    /// </summary>
    [Fact]
    public void An_unknown_build_status_falls_back_to_the_neutral_reading()
    {
        var state = PresentationFixtures.FullChain(700);

        Assert.Equal(
            InspectorText.BuildBlueprintExplanation(state, Site("build_waiting_carrier")),
            InspectorText.BuildBlueprintExplanation(
                state,
                Site("build_invented_by_a_later_step")));
    }

    /// <summary>
    /// The codes the simulation actually publishes, checked against the live
    /// snapshot rather than against this test's own list, so a new code cannot be
    /// added to <c>PrototypeWorld</c> without this failing.
    /// </summary>
    [Fact]
    public void Every_build_status_the_simulation_publishes_is_covered_here()
    {
        var covered = new[]
        {
            "build_blocked_priority", "build_carrier_on_the_way", "build_haul_blocked",
            "build_in_progress", "build_no_stone", "build_ready", "build_reserved",
            "build_stone_reserved", "build_unreachable", "build_waiting_carrier",
        };
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var world = new PrototypeWorld(PresentationFixtures.BuildChain());
        while (!world.IsComplete)
        {
            world.Step();
            foreach (var site in world.GetSnapshot().BuildSites)
            {
                observed.Add(site.StatusCode);
            }
        }

        Assert.NotEmpty(observed);
        Assert.All(observed, code => Assert.Contains(code, covered));
    }

    // --------------------------------------------------------- the built post

    [Fact]
    public void A_built_post_outside_a_training_ground_names_the_missing_zone()
    {
        var state = PresentationFixtures.BuiltPost(PresentationFixtures.BlueprintTick + 300);
        var post = Assert.Single(state.Map.BuiltPostTiles);
        state = state with
        {
            Zones = state.Zones.ToDictionary(
                pair => pair.Key,
                pair => pair.Key == ZoneKind.TrainingGround
                    ? (IReadOnlyList<GridPoint>)[]
                    : pair.Value),
        };

        Assert.Equal(
            "built training post; it cost 2 stone.\n" +
            "No TrainingGround zone here yet: press [Z] to select it and [B] to paint, " +
            "and Drill work appears.",
            InspectorText.BuildPostExplanation(state, post));
    }

    [Fact]
    public void A_built_post_inside_a_training_ground_says_it_now_produces_work()
    {
        var state = PresentationFixtures.BuiltPost(PresentationFixtures.BlueprintTick + 300);
        var post = Assert.Single(state.Map.BuiltPostTiles);

        Assert.Equal(
            "built training post; it cost 2 stone.\n" +
            "Inside TrainingGround: this post now produces Drill work like any other.",
            InspectorText.BuildPostExplanation(state, post));
    }

    [Fact]
    public void A_built_post_with_drill_switched_off_names_that_lever_instead()
    {
        var state = WithPriority(
            PresentationFixtures.BuiltPost(PresentationFixtures.BlueprintTick + 300),
            JobKind.Drill,
            0);
        var post = Assert.Single(state.Map.BuiltPostTiles);

        Assert.Equal(
            "built training post; it cost 2 stone.\n" +
            "Inside TrainingGround, but the Drill priority is 0. Raise it with [J] and +/-.",
            InspectorText.BuildPostExplanation(state, post));
    }

    // ------------------------------------------------------------- refusals

    [Fact]
    public void The_build_brush_refusals_read_as_full_sentences()
    {
        var fresh = PresentationFixtures.Baseline(1);
        var chain = PresentationFixtures.BuiltPost(PresentationFixtures.BlueprintTick + 300);

        Assert.Equal(
            "it is still rock — dig it first, then build on the floor it leaves",
            InspectorText.UnbuildableReason(fresh, new GridPoint(25, 1)));
        Assert.Equal(
            "the map boundary holds the dungeon in",
            InspectorText.UnbuildableReason(fresh, new GridPoint(0, 0)));
        Assert.Equal(
            "it is a bed, a station, the larder, a bunk, an existing post or the gate — not plain floor",
            InspectorText.UnbuildableReason(fresh, new GridPoint(14, 7)));
        Assert.Equal(
            "a training post already stands here",
            InspectorText.UnbuildableReason(chain, Assert.Single(chain.Map.BuiltPostTiles)));
        Assert.Equal(
            "it is a material stockpile cell — erase it first, a building site is not a warehouse",
            InspectorText.UnbuildableReason(chain, PresentationFixtures.StockLeft));
    }

    /// <summary>
    /// The stockpile refusal had to change with this step: excavated ground is no
    /// longer "the next step of the experiment", it is buildable ground that still
    /// cannot store material.
    /// </summary>
    [Fact]
    public void The_stockpile_brush_now_separates_buildable_ground_from_storable_ground()
    {
        var dug = PresentationFixtures.DigOnly(700);

        Assert.Equal(
            "freshly excavated ground can hold a building, but not stored material",
            InspectorText.UnstockpileableReason(dug, dug.Map.ExcavatedTiles[0]));
        Assert.Contains(dug.Map.ExcavatedTiles[0], dug.Map.BuildFloorTiles);
    }

    // -------------------------------------------------------- carrier route

    [Fact]
    public void A_carrier_bound_for_a_site_says_so_instead_of_naming_a_stockpile_cell()
    {
        var state = PresentationFixtures.BuildChainAt(PresentationFixtures.BlueprintTick + 40);
        var creature = state.Creatures[0];
        var site = new GridPoint(25, 2);

        Assert.Equal(
            "stone haul: taking it out of the stockpile (22,1), booked for the site at (25,2) x2\n",
            InspectorText.DescribeCarrierRoute(
                creature,
                PresentationFixtures.StoneHaul(
                    PresentationFixtures.StockLeft,
                    creature.Id,
                    site,
                    storeReserved: 2) with
                {
                    SourceCell = PresentationFixtures.StockLeft,
                },
                [PresentationFixtures.BuildSite(site)]));
    }

    /// <summary>
    /// Without the build-site list the wording is exactly the one Issue #26 shipped:
    /// the stockpile route did not change.
    /// </summary>
    [Fact]
    public void A_carrier_bound_for_a_stockpile_keeps_the_wording_it_already_had()
    {
        var creature = PresentationFixtures.Baseline(1).Creatures[0];

        Assert.Equal(
            "stone haul: walking to pile (25,2), booked (23,1) x1\n",
            InspectorText.DescribeCarrierRoute(
                creature,
                PresentationFixtures.StoneHaul(
                    new GridPoint(25, 2),
                    creature.Id,
                    PresentationFixtures.StockRight),
                []));
    }

    // ------------------------------------------------------------ whole panel

    [Fact]
    public void A_blueprint_cell_carries_a_build_section_and_a_blueprint_tile_line()
    {
        var state = PresentationFixtures.BuildChainAt(PresentationFixtures.BlueprintTick + 1);
        var site = Assert.Single(state.BuildSites);

        var text = InspectorText.Build(state.Shown(),null, site.Tile);

        Assert.Contains("tile floor (blueprint)\n", text, StringComparison.Ordinal);
        Assert.Contains("\nBUILD\ntraining post blueprint · stone ", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("BUILD", StringComparison.Ordinal) <
            text.IndexOf("DIG", StringComparison.Ordinal),
            text);
    }

    [Fact]
    public void A_built_post_cell_reads_as_a_post_rather_than_as_plain_floor()
    {
        var state = PresentationFixtures.BuiltPost(PresentationFixtures.BlueprintTick + 300);
        var post = Assert.Single(state.Map.BuiltPostTiles);

        var text = InspectorText.Build(state.Shown(),null, post);

        Assert.Contains("tile Post (built)\n", text, StringComparison.Ordinal);
        Assert.Contains("\nBUILD\nbuilt training post; it cost 2 stone.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cell that has nothing to do with construction must read exactly as it did
    /// before the chain existed. This is what keeps the golden UI frames honest.
    /// </summary>
    [Fact]
    public void A_cell_outside_the_chain_gains_no_build_section()
    {
        var state = PresentationFixtures.FullChain(700);

        Assert.DoesNotContain(
            "BUILD",
            InspectorText.Build(state.Shown(),null, PresentationFixtures.StockRight),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BUILD",
            InspectorText.Build(state.Shown(),null, new GridPoint(12, 12)),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static PrototypeBuildSiteSnapshot Site(
        string statusCode,
        int delivered = 0,
        int incomingReserved = 0,
        int? reservedBy = null,
        int progressTicks = 0,
        int requiredTicks = 30,
        bool reachable = true) =>
        new(
            new GridPoint(25, 2),
            delivered,
            PrototypeTuning.BuildStoneCost,
            incomingReserved,
            reservedBy is null ? null : 1,
            reservedBy,
            progressTicks,
            requiredTicks,
            reachable,
            statusCode);

    private static PrototypeSnapshot WithPriority(
        PrototypeSnapshot state,
        JobKind job,
        int priority)
    {
        var priorities = state.Priorities.ToDictionary(pair => pair.Key, pair => pair.Value);
        priorities[job] = priority;
        return state with { Priorities = priorities };
    }
}
