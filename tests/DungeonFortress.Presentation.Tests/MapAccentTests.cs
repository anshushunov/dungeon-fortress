using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The half of Issue #58 that the set-of-cells tests cannot see.
///
/// "Unpausing must not redraw the marking" is not only about which cells carry a
/// mark — it is about how each one reads. The first attempt drew a waiting mark
/// with the colour of a designation waiting for a worker and a waiting blueprint
/// with the colour of a site whose material is on the way. Both are wrong in the
/// most ordinary session: mark rock with <c>Dig</c> priority 0 and every mark
/// turns grey the moment time moves; mark a training post before any stone has
/// been dug and it turns from teal to amber. The cells were right in both cases.
///
/// So the comparison is made where it belongs: the reading a mark has while it
/// waits, against the reading the world gives it, <em>across the very tick that
/// applies the command</em>. The world is the real one — the same log run to
/// <c>T</c> and to <c>T + 1</c> — so this also pins the copy: if the simulation's
/// status ladder changes and <see cref="MapAccents"/> does not, this fails.
/// </summary>
public sealed class MapAccentTests
{
    private static readonly GridPoint Rock = new(25, 1);
    private static readonly GridPoint Floor = new(12, 10);

    /// <summary>
    /// The same log stopped on the tick its last command carries, and one tick
    /// later. The first is the frame the player sees while paused; the second is
    /// what unpausing produces.
    /// </summary>
    private static (MapProjection Waiting, MapProjection Applied) Across(
        int tick,
        params PrototypeCommand[] commands)
    {
        var log = PresentationFixtures.Log(commands);
        return (
            MapProjection.Of(PrototypeScenario.Run(log, tick).State),
            MapProjection.Of(PrototypeScenario.Run(log, tick + 1).State));
    }

    private static DigMarkAccent AppliedDig(MapProjection applied, GridPoint tile) =>
        MapAccents.Dig(applied.DigDesignations.Single(item => item.Tile == tile).StatusCode);

    private static BlueprintAccent AppliedBlueprint(MapProjection applied, GridPoint tile) =>
        MapAccents.Blueprint(applied.BuildSites.Single(site => site.Tile == tile).StatusCode);

    private static StockpileCellAccent AppliedStockpile(MapProjection applied, GridPoint tile) =>
        MapAccents.Stockpile(applied.StockpileCells.Single(cell => cell.Position == tile).StatusCode);

    [Fact]
    public void A_waiting_dig_mark_reads_the_same_as_the_designation_it_becomes()
    {
        var (waiting, applied) = Across(40, new DigDesignateCommand(40, [Rock]));

        Assert.Equal(DigMarkAccent.Waiting, MapAccents.PendingDig(waiting));
        Assert.Equal(MapAccents.PendingDig(waiting), AppliedDig(applied, Rock));
    }

    /// <summary>
    /// The defect this test exists for: with digging switched off the world calls
    /// every designation <c>dig_blocked_priority</c> on the first branch of its
    /// ladder, so a waiting mark drawn as an ordinary one made <em>every</em> mark
    /// change colour on unpause. The priority is a published snapshot field, not
    /// map topology.
    /// </summary>
    [Fact]
    public void A_waiting_dig_mark_is_already_grey_when_digging_is_switched_off()
    {
        var (waiting, applied) = Across(
            40,
            new SetPriorityCommand(0, JobKind.Dig, 0),
            new DigDesignateCommand(40, [Rock]));

        Assert.Equal(DigMarkAccent.BlockedByPriority, MapAccents.PendingDig(waiting));
        Assert.Equal(MapAccents.PendingDig(waiting), AppliedDig(applied, Rock));
    }

    /// <summary>
    /// The defect on the construction side: a fresh site has nothing delivered, so
    /// in the first session anybody plays — mark a post, then go dig for it — the
    /// applying tick deterministically answers <c>build_no_stone</c>. Drawing the
    /// waiting blueprint with the "a carrier is coming" colour meant the most
    /// ordinary blueprint in the game flipped colour on unpause.
    /// </summary>
    [Fact]
    public void A_waiting_blueprint_with_no_stone_in_the_world_reads_as_waiting_for_material()
    {
        var (waiting, applied) = Across(40, new BuildDesignateCommand(40, [Floor]));

        Assert.Equal(0, waiting.State.Stocks.LooseStone + waiting.State.Stocks.StoredStone);
        Assert.Equal(BlueprintAccent.WaitingForMaterial, MapAccents.PendingBlueprint(waiting, Floor));
        Assert.Equal(MapAccents.PendingBlueprint(waiting, Floor), AppliedBlueprint(applied, Floor));
    }

    /// <summary>
    /// And the other side of the same gate: with stone lying about, the site really
    /// is only waiting for somebody to pick it up, and the waiting blueprint has to
    /// say so from the start.
    /// </summary>
    [Fact]
    public void A_waiting_blueprint_with_free_stone_reads_as_waiting_for_a_carrier()
    {
        var (waiting, applied) = Across(
            700,
            new DigDesignateCommand(0, PresentationFixtures.Pocket),
            new BuildDesignateCommand(700, [Floor]));

        Assert.True(waiting.State.Stocks.LooseStone > 0);
        Assert.Equal(BlueprintAccent.WaitingForCarrier, MapAccents.PendingBlueprint(waiting, Floor));
        Assert.Equal(MapAccents.PendingBlueprint(waiting, Floor), AppliedBlueprint(applied, Floor));
    }

    [Theory]
    [InlineData(nameof(JobKind.Build))]
    [InlineData(nameof(JobKind.Haul))]
    public void A_waiting_blueprint_is_already_grey_when_the_work_it_needs_is_switched_off(string job)
    {
        var (waiting, applied) = Across(
            40,
            new SetPriorityCommand(0, Enum.Parse<JobKind>(job), 0),
            new BuildDesignateCommand(40, [Floor]));

        Assert.Equal(BlueprintAccent.BlockedByPriority, MapAccents.PendingBlueprint(waiting, Floor));
        Assert.Equal(MapAccents.PendingBlueprint(waiting, Floor), AppliedBlueprint(applied, Floor));
    }

    /// <summary>
    /// Forbidden is a zone, so the projection knows about it — including a
    /// Forbidden paint accepted in the same paused moment. A site nobody may step
    /// on therefore reads as unreachable straight away.
    /// </summary>
    [Fact]
    public void A_waiting_blueprint_on_forbidden_ground_reads_as_unreachable()
    {
        var (waiting, applied) = Across(
            40,
            new ZonePaintCommand(0, ZoneKind.Forbidden, [Floor]),
            new BuildDesignateCommand(40, [Floor]));

        Assert.Equal(BlueprintAccent.Unreachable, MapAccents.PendingBlueprint(waiting, Floor));
        Assert.Equal(MapAccents.PendingBlueprint(waiting, Floor), AppliedBlueprint(applied, Floor));
    }

    [Fact]
    public void A_waiting_stockpile_cell_reads_as_the_empty_cell_it_becomes()
    {
        var cell = PresentationFixtures.StockLeft;
        var (waiting, applied) = Across(
            40,
            new ZonePaintCommand(40, ZoneKind.MaterialStockpile, [cell]));

        Assert.Equal(StockpileCellAccent.Room, MapAccents.PendingStockpile(waiting, cell));
        Assert.Equal(MapAccents.PendingStockpile(waiting, cell), AppliedStockpile(applied, cell));
    }

    [Fact]
    public void A_waiting_stockpile_cell_on_forbidden_ground_reads_as_unreachable()
    {
        var cell = PresentationFixtures.StockLeft;
        var (waiting, applied) = Across(
            40,
            new ZonePaintCommand(0, ZoneKind.Forbidden, [cell]),
            new ZonePaintCommand(40, ZoneKind.MaterialStockpile, [cell]));

        Assert.Equal(StockpileCellAccent.Unreachable, MapAccents.PendingStockpile(waiting, cell));
        Assert.Equal(MapAccents.PendingStockpile(waiting, cell), AppliedStockpile(applied, cell));
    }

    /// <summary>
    /// The residual boundary, named rather than implied.
    ///
    /// <c>dig_unreachable</c> asks whether any orthogonal neighbour of the rock is
    /// passable, not the gate and not <c>Forbidden</c>. That is map topology, and
    /// copying it into the presentation layer would put the same rule on both
    /// sides of the seam ADR 0011 draws. So this is the one reading that can still
    /// change when the tick runs, and it is not a rounding error: on the shipped
    /// baseline map two of the twelve diggable tiles are walled in until a
    /// neighbour is dug.
    ///
    /// The test designates every diggable tile at once and sorts the readings the
    /// applying tick produces into three piles. Two are allowed:
    ///
    /// <list type="bullet">
    /// <item><see cref="DigMarkAccent.Unreachable"/> — the boundary above, and it
    /// must be exactly those two tiles;</item>
    /// <item><see cref="DigMarkAccent.InProgress"/> — somebody was already
    /// standing next to the rock and started digging on that tick. That is the
    /// world answering the mark, which is what the player asked for; it is not the
    /// mark being drawn a second time.</item>
    /// </list>
    ///
    /// Everything else must read exactly as it did while it waited. Widening the
    /// exception silently is not possible while this test exists.
    /// </summary>
    [Fact]
    public void Only_reachability_and_work_starting_change_a_reading_when_the_tick_runs()
    {
        var diggable = PresentationFixtures.Baseline(1).Map.DiggableTiles;
        var (waiting, applied) = Across(1, new DigDesignateCommand(1, [.. diggable]));
        var wasWaiting = MapAccents.PendingDig(waiting);

        var readings = diggable.ToDictionary(tile => tile, tile => AppliedDig(applied, tile));
        var unreachable = Tiles(readings, DigMarkAccent.Unreachable);
        var started = Tiles(readings, DigMarkAccent.InProgress);
        var unchanged = readings.Count(pair => pair.Value == wasWaiting);

        Assert.Equal(12, diggable.Count);
        Assert.Equal([new GridPoint(26, 1), new GridPoint(26, 2)], unreachable);
        Assert.Equal(diggable.Count, unreachable.Length + started.Length + unchanged);
    }

    private static GridPoint[] Tiles(
        IReadOnlyDictionary<GridPoint, DigMarkAccent> readings,
        DigMarkAccent accent) =>
        [.. readings.Where(pair => pair.Value == accent).Select(pair => pair.Key).Order()];
}
