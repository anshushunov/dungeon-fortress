using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The rectangle brush.
///
/// The claim the whole step exists for is arithmetic, so it is checked as
/// arithmetic: a drag over N cells produces exactly one command carrying N
/// tiles, and a drag that is cancelled produces none. Neither needs an engine,
/// a picture or a running world — which is why the acceptance criterion
/// "a 4x3 pocket costs one gesture, not twelve clicks" is a unit test here
/// rather than a thing somebody times by hand.
/// </summary>
public sealed class BrushSelectionTests
{
    /// <summary>
    /// A 4x3 block of plain floor away from every authored feature, zone and rock
    /// tile. Twelve cells: exactly the pocket the Issue counts clicks for.
    /// </summary>
    private static readonly GridPoint PlainFloorFrom = new(11, 10);
    private static readonly GridPoint PlainFloorTo = new(14, 12);

    [Fact]
    public void A_rectangle_covers_every_cell_between_its_corners()
    {
        var tiles = BrushSelection.Rectangle(PlainFloorFrom, PlainFloorTo);
        Assert.Equal(12, tiles.Count);
        Assert.Equal(tiles.Count, tiles.Distinct().Count());
        Assert.Contains(new GridPoint(11, 10), tiles);
        Assert.Contains(new GridPoint(14, 12), tiles);
    }

    /// <summary>
    /// Dragging up-left has to mean the same rectangle as dragging down-right.
    /// The anchor is where the button went down, not a corner with a privilege.
    /// </summary>
    [Fact]
    public void A_rectangle_does_not_care_which_corner_the_drag_started_from()
    {
        Assert.Equal(
            BrushSelection.Rectangle(PlainFloorFrom, PlainFloorTo),
            BrushSelection.Rectangle(PlainFloorTo, PlainFloorFrom));
        Assert.Equal(
            BrushSelection.Rectangle(PlainFloorFrom, PlainFloorTo),
            BrushSelection.Rectangle(
                new GridPoint(PlainFloorFrom.X, PlainFloorTo.Y),
                new GridPoint(PlainFloorTo.X, PlainFloorFrom.Y)));
    }

    [Fact]
    public void A_rectangle_is_clipped_to_the_map()
    {
        var tiles = BrushSelection.Rectangle(new GridPoint(-4, -4), new GridPoint(1, 1));
        Assert.All(tiles, tile => Assert.True(MapBounds.Contains(tile)));
        Assert.Equal(4, tiles.Count);
    }

    /// <summary>
    /// The criterion the step is measured by: one gesture, one command, twelve
    /// tiles. Before this, the same intent was twelve separate commands.
    /// </summary>
    [Fact]
    public void A_four_by_three_drag_is_one_command_carrying_twelve_tiles()
    {
        var state = PresentationFixtures.Baseline(1);
        var stroke = BrushSelection.Resolve(
            state.Shown(), BrushMode.Paint, ZoneKind.TrainingGround, PlainFloorFrom, PlainFloorTo);

        Assert.Null(stroke.Refusal);
        Assert.Equal(12, stroke.Tiles.Count);
        Assert.Equal(12, stroke.RectangleTiles);

        var command = Assert.IsType<ZonePaintCommand>(BrushSelection.ToCommand(stroke, state.Tick));
        Assert.Equal(ZoneKind.TrainingGround, command.ZoneKind);
        Assert.Equal(12, command.Tiles.Count);
        Assert.Equal(BrushSelection.Rectangle(PlainFloorFrom, PlainFloorTo), command.Tiles);
    }

    /// <summary>
    /// The excavation pocket, which is what the owner playtest was actually
    /// digging when it reported the brushes as unusable. Six cells, one command.
    /// </summary>
    [Fact]
    public void A_drag_over_the_rock_pocket_designates_it_in_one_command()
    {
        var state = PresentationFixtures.Baseline(1);
        var stroke = BrushSelection.Resolve(
            state.Shown(), BrushMode.Dig, ZoneKind.Farm, new GridPoint(25, 1), new GridPoint(26, 3));

        var command = Assert.IsType<DigDesignateCommand>(BrushSelection.ToCommand(stroke, state.Tick));
        Assert.Equal(6, command.Tiles.Count);
        Assert.All(command.Tiles, tile => Assert.Contains(tile, state.Map.DiggableTiles));
    }

    /// <summary>
    /// A single click is a 1x1 rectangle and nothing else. It matters because the
    /// demos, the smokes and every existing command log go through this path: if a
    /// click stopped producing a one-tile command, three golden frames would move.
    /// </summary>
    [Fact]
    public void A_single_click_is_a_one_by_one_rectangle()
    {
        var state = PresentationFixtures.Baseline(1);
        var tile = new GridPoint(25, 1);
        var stroke = BrushSelection.Resolve(state.Shown(), BrushMode.Dig, ZoneKind.Farm, tile, tile);

        var command = Assert.IsType<DigDesignateCommand>(BrushSelection.ToCommand(stroke, 7));
        Assert.Equal([tile], command.Tiles);
        Assert.Equal(7, command.Tick);
    }

    /// <summary>
    /// A stroke that crosses what the brush cannot take never becomes a rejected
    /// command: the illegal cells are dropped and the rest is applied. The count
    /// the player sees while dragging is this filtered count, not the area.
    /// </summary>
    [Fact]
    public void A_stroke_across_floor_and_rock_carries_only_the_cells_the_brush_can_take()
    {
        var state = PresentationFixtures.Baseline(1);
        // (24,1)..(26,3) is the pocket plus a column of plain floor beside it.
        var stroke = BrushSelection.Resolve(
            state.Shown(), BrushMode.Dig, ZoneKind.Farm, new GridPoint(24, 1), new GridPoint(26, 3));

        Assert.Equal(9, stroke.RectangleTiles);
        Assert.Equal(6, stroke.Tiles.Count);
        Assert.DoesNotContain(new GridPoint(24, 1), stroke.Tiles);
    }

    /// <summary>
    /// A designation that already exists is not counted twice, so dragging over
    /// the pocket a second time states that there is nothing left to do.
    /// </summary>
    [Fact]
    public void A_second_stroke_over_the_same_pocket_marks_nothing()
    {
        var state = PresentationFixtures.DigOnly(3);
        var stroke = BrushSelection.Resolve(
            state.Shown(), BrushMode.Dig, ZoneKind.Farm, new GridPoint(25, 1), new GridPoint(26, 3));

        Assert.Equal(2, stroke.Tiles.Count); // (26,2) and (26,3) were never designated
        Assert.DoesNotContain(new GridPoint(25, 1), stroke.Tiles);
    }

    [Fact]
    public void A_stroke_that_can_take_nothing_produces_no_command_and_says_why()
    {
        var state = PresentationFixtures.Baseline(1);
        var stroke = BrushSelection.Resolve(
            state.Shown(), BrushMode.Dig, ZoneKind.Farm, PlainFloorFrom, PlainFloorTo);

        Assert.Empty(stroke.Tiles);
        Assert.False(stroke.Applies);
        Assert.Null(BrushSelection.ToCommand(stroke, state.Tick));
        Assert.Contains("None of the 12 cells", stroke.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A single cell keeps the sentence it had before rectangles existed: it names
    /// the rule the player broke instead of counting an area of one.
    /// </summary>
    [Theory]
    [InlineData(12, 12, "(12,12) cannot be dug:")]
    [InlineData(0, 0, "(0,0) cannot be dug:")]
    public void A_refused_single_cell_names_the_rule(int x, int y, string expected)
    {
        var stroke = BrushSelection.Resolve(
            PresentationFixtures.Baseline(1).Shown(),
            BrushMode.Dig,
            ZoneKind.Farm,
            new GridPoint(x, y),
            new GridPoint(x, y));

        Assert.StartsWith(expected, stroke.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_already_designated_single_cell_says_so()
    {
        var stroke = BrushSelection.Resolve(
            PresentationFixtures.DigOnly(3).Shown(),
            BrushMode.Dig,
            ZoneKind.Farm,
            new GridPoint(25, 1),
            new GridPoint(25, 1));

        Assert.Equal("(25,1) is already designated for digging.", stroke.Refusal);
    }

    /// <summary>
    /// Splitting an oversized selection into several commands would put back
    /// exactly the partially applied marking the rectangle exists to remove, so it
    /// is refused whole and the player is told to make two strokes.
    /// </summary>
    [Fact]
    public void A_selection_larger_than_one_command_is_refused_rather_than_split()
    {
        var state = PresentationFixtures.Baseline(1);
        var stroke = BrushSelection.Resolve(
            state.Shown(),
            BrushMode.Paint,
            ZoneKind.TrainingGround,
            new GridPoint(0, 0),
            new GridPoint(PrototypeTuning.MapWidth - 1, PrototypeTuning.MapHeight - 1));

        Assert.True(stroke.RectangleTiles > PrototypeTuning.MaximumTilesPerCommand);
        Assert.Empty(stroke.Tiles);
        Assert.Null(BrushSelection.ToCommand(stroke, state.Tick));
        Assert.Contains("Mark it in two strokes", stroke.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every brush produces its own command kind and no brush produces two. The
    /// vocabulary is unchanged: these are the same six commands single cells
    /// always emitted, with a longer tile list.
    /// </summary>
    [Theory]
    [InlineData(BrushMode.Paint, typeof(ZonePaintCommand))]
    [InlineData(BrushMode.Erase, typeof(ZoneEraseCommand))]
    [InlineData(BrushMode.Dig, typeof(DigDesignateCommand))]
    [InlineData(BrushMode.CancelDig, typeof(DigCancelCommand))]
    [InlineData(BrushMode.Build, typeof(BuildDesignateCommand))]
    [InlineData(BrushMode.CancelBuild, typeof(BuildCancelCommand))]
    public void Each_brush_maps_to_one_command_kind(BrushMode mode, Type expected)
    {
        var stroke = new BrushStroke(mode, ZoneKind.Farm, [new GridPoint(11, 10)], 1, null);
        Assert.IsType(expected, BrushSelection.ToCommand(stroke, 0));
    }

    [Fact]
    public void Inspect_never_marks_the_map()
    {
        var stroke = BrushSelection.Resolve(
            PresentationFixtures.Baseline(1).Shown(),
            BrushMode.Inspect,
            ZoneKind.Farm,
            PlainFloorFrom,
            PlainFloorTo);

        Assert.Empty(stroke.Tiles);
        Assert.Null(BrushSelection.ToCommand(stroke, 0));
    }

    /// <summary>
    /// The erase brush only carries cells that are in the zone, so the count is
    /// how many cells the command will actually clear.
    /// </summary>
    [Fact]
    public void Erase_carries_only_cells_that_are_in_the_zone()
    {
        var state = PresentationFixtures.Baseline(1);
        // The authored Farm zone is (1,1)..(6,7); this rectangle overhangs it.
        var stroke = BrushSelection.Resolve(
            state.Shown(), BrushMode.Erase, ZoneKind.Farm, new GridPoint(5, 6), new GridPoint(8, 8));

        Assert.Equal(12, stroke.RectangleTiles);
        Assert.All(stroke.Tiles, tile => Assert.Contains(tile, state.Zones[ZoneKind.Farm]));
        Assert.DoesNotContain(new GridPoint(8, 8), stroke.Tiles);
    }

    /// <summary>
    /// The whole point of the atomic command: a rectangle the world accepts marks
    /// every cell of it, and there is no state in which half of it is marked. The
    /// command is run through the real simulation rather than asserted about.
    /// </summary>
    [Fact]
    public void An_accepted_rectangle_marks_every_cell_it_carried()
    {
        var state = PresentationFixtures.Baseline(1);
        var stroke = BrushSelection.Resolve(
            state.Shown(), BrushMode.Dig, ZoneKind.Farm, new GridPoint(25, 1), new GridPoint(26, 3));
        var command = BrushSelection.ToCommand(stroke, 0)!;

        var applied = PrototypeScenario.Run(PresentationFixtures.Log(command), 2).State;
        Assert.Equal(6, applied.DigDesignations.Count);
        Assert.All(
            stroke.Tiles,
            tile => Assert.Contains(applied.DigDesignations, item => item.Tile == tile));
    }
}
