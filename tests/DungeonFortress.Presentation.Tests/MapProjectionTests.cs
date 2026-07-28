using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #58: marking done while time is stopped was invisible until time moved.
///
/// The defect is a gap between two moments that only pause keeps apart. A command
/// carrying tick <c>T</c> is applied at the <em>start</em> of tick <c>T</c>, so a
/// world stopped at <c>T</c> holds the player's intent in the log and not yet in
/// its designations. Running, that gap is a sixth of a second; paused it is
/// forever, and pause is when marking is actually done.
///
/// A world stopped exactly on the tick its commands carry is therefore the whole
/// fixture of this file — <c>PrototypeScenario.Run(log, T)</c> with commands at
/// <c>T</c> — and it is the same state the Godot adapter is in the instant a
/// brush stroke is accepted.
///
/// What is checked here is the projection, not the simulation: canonical state,
/// the order of operations inside a tick and the snapshot schema are untouched,
/// which is what the "the tick changes nothing that is drawn" cases below state
/// from the other side.
/// </summary>
public sealed class MapProjectionTests
{
    private const int MarkTick = 40;

    private static readonly GridPoint Rock = new(25, 1);
    private static readonly GridPoint SecondRock = new(25, 2);
    private static readonly GridPoint Floor = new(12, 10);

    /// <summary>
    /// A world stopped on the very tick its commands carry: accepted, logged, not
    /// applied. This is the frame the player is looking at right after a stroke.
    /// </summary>
    private static PrototypeSnapshot Stopped(params PrototypeCommand[] commands) =>
        PrototypeScenario.Run(PresentationFixtures.Log(commands), MarkTick).State;

    private static PrototypeSnapshot Applied(params PrototypeCommand[] commands) =>
        PrototypeScenario.Run(PresentationFixtures.Log(commands), MarkTick + 1).State;

    [Fact]
    public void A_designation_accepted_on_this_tick_is_on_the_map_before_the_tick_runs()
    {
        var state = Stopped(new DigDesignateCommand(MarkTick, [Rock]));
        var view = MapProjection.Of(state);

        // The bug, stated as the two halves it is made of.
        Assert.DoesNotContain(state.DigDesignations, item => item.Tile == Rock);
        Assert.True(view.IsDesignatedForDigging(Rock));
        Assert.Equal([Rock], view.PendingDigMarks);
        Assert.True(view.HasPendingMarking);
        Assert.Equal(1, view.PendingCommandCount);
    }

    /// <summary>
    /// The acceptance criterion the player actually feels: unpausing must not
    /// redraw the marking. The set of cells that read as designated is compared
    /// across the very tick that records the command, and it has to be the same
    /// set — the tick adds a status, not a mark.
    /// </summary>
    [Fact]
    public void The_tick_that_applies_a_mark_does_not_change_which_cells_are_marked()
    {
        var command = new DigDesignateCommand(MarkTick, [Rock, SecondRock]);
        var before = MapProjection.Of(Stopped(command));
        var after = MapProjection.Of(Applied(command));

        Assert.Equal(Marked(before), Marked(after));
        Assert.Empty(after.PendingDigMarks);
        Assert.False(after.HasPendingMarking);
    }

    [Fact]
    public void A_withdrawal_accepted_on_this_tick_leaves_the_map_before_the_tick_runs()
    {
        var designate = new DigDesignateCommand(0, [Rock, SecondRock]);
        var state = Stopped(designate, new DigCancelCommand(MarkTick, [Rock]));
        var view = MapProjection.Of(state);

        // The world still holds it: only the projection has taken it away.
        Assert.Contains(state.DigDesignations, item => item.Tile == Rock);
        Assert.False(view.IsDesignatedForDigging(Rock));
        Assert.DoesNotContain(view.DigDesignations, item => item.Tile == Rock);
        Assert.Equal([Rock], view.PendingDigWithdrawals);

        // ...and the other mark is untouched, so a withdrawal is not a clear-all.
        Assert.True(view.IsDesignatedForDigging(SecondRock));
    }

    [Fact]
    public void The_tick_that_applies_a_withdrawal_does_not_change_which_cells_are_marked()
    {
        var designate = new DigDesignateCommand(0, [Rock, SecondRock]);
        var cancel = new DigCancelCommand(MarkTick, [Rock]);

        Assert.Equal(
            Marked(MapProjection.Of(Stopped(designate, cancel))),
            Marked(MapProjection.Of(Applied(designate, cancel))));
    }

    /// <summary>
    /// Marking and taking it back inside one paused moment nets out exactly as the
    /// tick would net it out, because the waiting commands are folded in log order.
    /// </summary>
    [Fact]
    public void A_mark_taken_back_in_the_same_paused_moment_leaves_no_trace()
    {
        var view = MapProjection.Of(Stopped(
            new DigDesignateCommand(MarkTick, [Rock]),
            new DigCancelCommand(MarkTick, [Rock])));

        Assert.False(view.IsDesignatedForDigging(Rock));
        Assert.Empty(view.PendingDigMarks);
    }

    [Fact]
    public void A_blueprint_accepted_on_this_tick_is_on_the_map_before_the_tick_runs()
    {
        var command = new BuildDesignateCommand(MarkTick, [Floor]);
        var view = MapProjection.Of(Stopped(command));

        Assert.Empty(view.State.BuildSites);
        Assert.True(view.CarriesBlueprint(Floor));
        Assert.Equal([Floor], view.PendingBuildMarks);
        Assert.Equal("floor (blueprint)", InspectorText.TileDescription(view, Floor));

        var after = MapProjection.Of(Applied(command));
        Assert.Contains(after.BuildSites, site => site.Tile == Floor);
        Assert.Empty(after.PendingBuildMarks);
    }

    [Fact]
    public void A_withdrawn_blueprint_leaves_the_map_before_the_tick_runs()
    {
        var view = MapProjection.Of(Stopped(
            new BuildDesignateCommand(0, [Floor]),
            new BuildCancelCommand(MarkTick, [Floor])));

        Assert.Contains(view.State.BuildSites, site => site.Tile == Floor);
        Assert.False(view.CarriesBlueprint(Floor));
        Assert.Empty(view.BuildSites);
    }

    [Fact]
    public void A_zone_painted_on_this_tick_is_on_the_map_before_the_tick_runs()
    {
        var view = MapProjection.Of(Stopped(
            new ZonePaintCommand(MarkTick, ZoneKind.TrainingGround, [Floor])));

        Assert.DoesNotContain(Floor, view.State.Zones[ZoneKind.TrainingGround]);
        Assert.True(view.IsInZone(ZoneKind.TrainingGround, Floor));
        Assert.Contains(Floor, view.Zone(ZoneKind.TrainingGround));
        Assert.Contains(ZoneKind.TrainingGround, view.ZonesAt(Floor));
    }

    [Fact]
    public void A_zone_erased_on_this_tick_leaves_the_map_before_the_tick_runs()
    {
        var farmTile = PresentationFixtures.Baseline(1).Zones[ZoneKind.Farm][0];
        var view = MapProjection.Of(Stopped(
            new ZoneEraseCommand(MarkTick, ZoneKind.Farm, [farmTile])));

        Assert.Contains(farmTile, view.State.Zones[ZoneKind.Farm]);
        Assert.False(view.IsInZone(ZoneKind.Farm, farmTile));
        Assert.DoesNotContain(farmTile, view.Zone(ZoneKind.Farm));
    }

    /// <summary>
    /// A material stockpile is one tile of a zone, so painting it while paused has
    /// to produce the storage square the tick would produce.
    /// </summary>
    [Fact]
    public void A_stockpile_cell_painted_on_this_tick_is_on_the_map_before_the_tick_runs()
    {
        var command = new ZonePaintCommand(
            MarkTick,
            ZoneKind.MaterialStockpile,
            [PresentationFixtures.StockLeft]);
        var view = MapProjection.Of(Stopped(command));

        Assert.Empty(view.State.StockpileCells);
        Assert.True(view.IsStockpileCell(PresentationFixtures.StockLeft));
        Assert.Equal([PresentationFixtures.StockLeft], view.PendingStockpileCells);

        var after = MapProjection.Of(Applied(command));
        Assert.Contains(after.StockpileCells, cell => cell.Position == PresentationFixtures.StockLeft);
        Assert.Empty(after.PendingStockpileCells);
    }

    [Fact]
    public void An_erased_stockpile_cell_leaves_the_map_before_the_tick_runs()
    {
        var view = MapProjection.Of(Stopped(
            new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [PresentationFixtures.StockLeft]),
            new ZoneEraseCommand(MarkTick, ZoneKind.MaterialStockpile, [PresentationFixtures.StockLeft])));

        Assert.NotEmpty(view.State.StockpileCells);
        Assert.Empty(view.StockpileCells);
        Assert.False(view.IsStockpileCell(PresentationFixtures.StockLeft));
    }

    /// <summary>
    /// A fixture that schedules something for a later tick is not an intent
    /// waiting for the player's next frame. Showing it early would be a different
    /// defect, and it is what would have moved the golden UI frames.
    /// </summary>
    [Fact]
    public void A_command_scheduled_for_a_later_tick_is_not_shown_early()
    {
        var state = PrototypeScenario.Run(
            PresentationFixtures.Log(new DigDesignateCommand(MarkTick + 100, [Rock])),
            MarkTick).State;
        var view = MapProjection.Of(state);

        Assert.NotEmpty(state.PendingCommands);
        Assert.False(view.HasPendingMarking);
        Assert.False(view.IsDesignatedForDigging(Rock));
    }

    /// <summary>
    /// With nothing waiting the projection is the snapshot, down to the very
    /// instances. That is what makes it safe to route every reader through it: the
    /// overwhelmingly common frame pays nothing and cannot differ.
    /// </summary>
    [Fact]
    public void With_nothing_waiting_the_projection_is_the_snapshot()
    {
        var state = PresentationFixtures.FullChain(700);
        var view = MapProjection.Of(state);

        Assert.False(view.HasPendingMarking);
        Assert.Same(state.DigDesignations, view.DigDesignations);
        Assert.Same(state.BuildSites, view.BuildSites);
        Assert.Same(state.StockpileCells, view.StockpileCells);
        Assert.Same(state.Zones[ZoneKind.Farm], view.Zone(ZoneKind.Farm));
        Assert.Empty(view.PendingDigMarks);
    }

    /// <summary>
    /// Time controls are not commands, so a priority or rule waiting for its tick
    /// changes nothing that is drawn and must not make the map claim otherwise.
    /// </summary>
    [Fact]
    public void A_waiting_priority_change_is_not_marking()
    {
        var view = MapProjection.Of(Stopped(
            new SetPriorityCommand(MarkTick, JobKind.Dig, 4)));

        Assert.False(view.HasPendingMarking);
        Assert.Equal(0, view.PendingCommandCount);
    }

    /// <summary>
    /// The brush reads the same map the player does. Without this, marking while
    /// paused would keep offering cells it had already marked and a second stroke
    /// would emit a command that changes nothing.
    /// </summary>
    [Fact]
    public void The_brush_does_not_offer_a_cell_that_already_carries_a_waiting_mark()
    {
        var view = MapProjection.Of(Stopped(new DigDesignateCommand(MarkTick, [Rock])));

        Assert.False(BrushSelection.Accepts(view, BrushMode.Dig, ZoneKind.Farm, Rock));
        Assert.True(BrushSelection.Accepts(view, BrushMode.CancelDig, ZoneKind.Farm, Rock));

        var stroke = BrushSelection.Resolve(view, BrushMode.Dig, ZoneKind.Farm, Rock, Rock);
        Assert.Empty(stroke.Tiles);
        Assert.Equal("(25,1) is already designated for digging.", stroke.Refusal);
    }

    /// <summary>
    /// The count above a drag is the count of cells the command will carry, so a
    /// rectangle dragged over the pocket a second time while paused says how much
    /// of it is genuinely left.
    /// </summary>
    [Fact]
    public void A_drag_over_a_partly_marked_pocket_counts_only_what_is_left()
    {
        var view = MapProjection.Of(Stopped(new DigDesignateCommand(MarkTick, [Rock, SecondRock])));
        var stroke = BrushSelection.Resolve(
            view, BrushMode.Dig, ZoneKind.Farm, new GridPoint(25, 1), new GridPoint(26, 3));

        Assert.Equal(6, stroke.RectangleTiles);
        Assert.Equal(4, stroke.Tiles.Count);
        Assert.DoesNotContain(Rock, stroke.Tiles);
        Assert.DoesNotContain(SecondRock, stroke.Tiles);
    }

    /// <summary>
    /// A cell that visibly carries a mark must not be described as bare rock
    /// waiting to be marked. The panel states what the picture cannot: the world
    /// has not applied it yet.
    /// </summary>
    [Fact]
    public void The_inspector_says_a_waiting_mark_is_waiting()
    {
        var view = MapProjection.Of(Stopped(new DigDesignateCommand(MarkTick, [Rock])));
        var dig = InspectorText.BuildDigExplanation(view, Rock);

        Assert.StartsWith("marked as designated for excavation on this tick", dig, StringComparison.Ordinal);
        Assert.Contains("when time advances", dig, StringComparison.Ordinal);
        Assert.DoesNotContain("Press [D]", dig, StringComparison.Ordinal);
    }

    [Fact]
    public void The_inspector_stops_describing_a_withdrawn_mark_as_designated()
    {
        var view = MapProjection.Of(Stopped(
            new DigDesignateCommand(0, [Rock]),
            new DigCancelCommand(MarkTick, [Rock])));

        Assert.StartsWith(
            "diggable internal rock",
            InspectorText.BuildDigExplanation(view, Rock),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// "marks" on the top line is the same question the map answers, so it counts
    /// the mark the player just made. Reporting 0 next to a marked cell was the
    /// text half of the same defect.
    /// </summary>
    [Fact]
    public void The_hud_counts_a_mark_that_is_waiting_for_its_tick()
    {
        var state = Stopped(new DigDesignateCommand(MarkTick, [Rock, SecondRock]));

        Assert.Empty(state.DigDesignations);
        Assert.Equal(2, MapProjection.Of(state).DigDesignationCount);
        Assert.Contains("marks 2", HudText.Summary(View(state)), StringComparison.Ordinal);
    }

    private static IReadOnlyList<GridPoint> Marked(MapProjection view) =>
    [
        .. view.DigDesignations
            .Select(item => item.Tile)
            .Concat(view.PendingDigMarks)
            .Order(),
    ];

    private static HudViewState View(PrototypeSnapshot state) => new(
        state,
        "baseline",
        new string('0', 8),
        Paused: true,
        Speed: 1.0,
        SelectedCreatureId: null,
        SelectedCell: null,
        ControlFeedback: "feedback",
        PlayerCommands: [],
        DiagnosticCount: 0);
}
