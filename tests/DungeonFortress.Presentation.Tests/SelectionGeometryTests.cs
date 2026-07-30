using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #99: the frame a drag stretches and the highlight under the cursor are
/// one shape, derived from one function.
///
/// The two tests that matter are <see cref="Every_selected_cell_is_inside_the_frame"/>
/// and <see cref="The_frame_is_tight_against_the_cells_column_by_column"/>, and
/// they are a pair on purpose. Containment alone is satisfied by any large enough
/// rectangle; tightness alone is satisfied by a shape that misses cells. Together
/// they say the frame is the union of the interaction rectangles and nothing else,
/// which is the claim the issue asks to be provable — substituting either geometry
/// for the other fails one of them.
/// </summary>
public sealed class SelectionGeometryTests
{
    private const int Tile = CameraView.DefaultTileSize;

    /// <summary>
    /// A vertical rock column with floor on both sides. Its south end is exposed,
    /// so the bottom cell carries a facade; the cells above it do not.
    /// </summary>
    private static IReadOnlySet<GridPoint> RockColumn() =>
        new HashSet<GridPoint>
        {
            new(5, 2), new(5, 3), new(5, 4),
        };

    private static IReadOnlySet<GridPoint> NoRock() => new HashSet<GridPoint>();

    [Fact]
    public void A_floor_cell_is_highlighted_by_its_footprint()
    {
        Assert.Equal(
            new ViewRect(120, 80, Tile - 1, Tile - 1),
            SelectionGeometry.CellInteractionRect(new GridPoint(3, 2), NoRock(), Tile));
    }

    /// <summary>
    /// The owner's observation, as an assertion: hovering rock outlines its whole
    /// visible mass, and a single click has to be the same rectangle rather than
    /// the flat cell. A 1x1 drag is an ordinary selection with one cell in it, so
    /// this is the frame reduced to one column.
    /// </summary>
    [Fact]
    public void A_single_rock_cell_selection_is_exactly_the_shape_the_hover_highlights()
    {
        var cell = new GridPoint(5, 4);
        var rock = RockColumn();
        var highlight = SelectionGeometry.CellInteractionRect(cell, rock, Tile);

        var frame = SelectionGeometry.Bounds(cell, cell, rock, Tile);

        Assert.Equal(highlight.X, frame.X);
        Assert.Equal(highlight.Y, frame.Y);
        Assert.Equal(highlight.Width, frame.Width);
        Assert.Equal(highlight.Height, frame.Height);

        // The raised top really is above the cell's own footprint, so the flat
        // grid rectangle this replaces was measurably a different shape.
        Assert.True(highlight.Y < CameraView.CellTopLeft(cell, Tile).Y);

        // A single click goes through the same path as a drag, so the degenerate
        // one-column outline has to be the highlight rectangle itself.
        Assert.Equal(
            [
                new ViewPoint(highlight.X, highlight.Y),
                new ViewPoint(highlight.X + highlight.Width, highlight.Y),
                new ViewPoint(highlight.X + highlight.Width, highlight.Y + highlight.Height),
                new ViewPoint(highlight.X, highlight.Y + highlight.Height),
                new ViewPoint(highlight.X, highlight.Y),
            ],
            SelectionGeometry.Outline(cell, cell, rock, Tile, 0));
    }

    /// <summary>
    /// The mutation the issue names: build the frame in grid coordinates and a
    /// rock cell's raised top and overhanging facade fall outside it.
    /// </summary>
    [Fact]
    public void Every_selected_cell_is_inside_the_frame()
    {
        var rock = RockColumn();
        var from = new GridPoint(4, 2);
        var to = new GridPoint(6, 4);

        var columns = SelectionGeometry.Columns(from, to, rock, Tile);

        foreach (var cell in BrushSelection.Rectangle(from, to))
        {
            var rect = SelectionGeometry.CellInteractionRect(cell, rock, Tile);
            var column = columns.Single(item => item.Cell == cell.X);
            Assert.InRange(rect.X, column.Left, column.Right);
            Assert.InRange(rect.X + rect.Width, column.Left, column.Right);
            Assert.InRange(rect.Y, column.Top, column.Bottom);
            Assert.InRange(rect.Y + rect.Height, column.Top, column.Bottom);
        }
    }

    /// <summary>
    /// The other half. A bounding box of the raised rectangles also contains every
    /// cell, and it is what the second review round of Issue #83 objected to: it
    /// lifts the top of the frame over floor columns that were never raised. The
    /// frame is a profile, so each column ends exactly where its own cells do.
    /// </summary>
    [Fact]
    public void The_frame_is_tight_against_the_cells_column_by_column()
    {
        var rock = RockColumn();
        var from = new GridPoint(4, 2);
        var to = new GridPoint(6, 4);

        var columns = SelectionGeometry.Columns(from, to, rock, Tile);

        Assert.Equal(3, columns.Count);
        foreach (var column in columns)
        {
            var head = SelectionGeometry.CellInteractionRect(
                new GridPoint(column.Cell, 2),
                rock,
                Tile);
            var tail = SelectionGeometry.CellInteractionRect(
                new GridPoint(column.Cell, 4),
                rock,
                Tile);
            Assert.Equal(head.Y, column.Top);
            Assert.Equal(tail.Y + tail.Height, column.Bottom);
        }

        // Stated as numbers as well, so a shape change is visible in the diff:
        // only the middle column is rock, and only it is raised.
        Assert.Equal(80, columns[0].Top);
        Assert.NotEqual(columns[0].Top, columns[1].Top);
        Assert.Equal(columns[0].Top, columns[2].Top);
        Assert.True(columns[1].Top < columns[0].Top);
        Assert.True(columns[1].Bottom > columns[0].Bottom);
    }

    /// <summary>
    /// Nothing moves on a drag with no rock in it. The frame over floor is exactly
    /// the grid rectangle it always was, which is what keeps this a fix for the
    /// rock case rather than a new look for every selection.
    /// </summary>
    [Fact]
    public void A_selection_with_no_rock_is_the_grid_rectangle_it_always_was()
    {
        var from = new GridPoint(2, 3);
        var to = new GridPoint(5, 6);

        var outline = SelectionGeometry.Outline(from, to, NoRock(), Tile, 0);

        Assert.Equal(
            [
                new ViewPoint(80, 120),
                new ViewPoint((5 * Tile) + Tile - 1, 120),
                new ViewPoint((5 * Tile) + Tile - 1, (6 * Tile) + Tile - 1),
                new ViewPoint(80, (6 * Tile) + Tile - 1),
                new ViewPoint(80, 120),
            ],
            outline);
    }

    /// <summary>
    /// The frame is a closed rectilinear loop: every segment is horizontal or
    /// vertical, and the walk comes back to where it started. A frame drawn as a
    /// polyline has to close itself, and a diagonal would mean the profile walk
    /// lost a step.
    /// </summary>
    [Fact]
    public void The_outline_is_a_closed_rectilinear_loop()
    {
        var outline = SelectionGeometry.Outline(
            new GridPoint(4, 2),
            new GridPoint(6, 4),
            RockColumn(),
            Tile,
            1);

        Assert.Equal(outline[0], outline[^1]);
        Assert.True(outline.Count > 5, "a mixed floor/rock drag steps, so it has corners");
        for (var index = 1; index < outline.Count; index++)
        {
            var previous = outline[index - 1];
            var current = outline[index];
            Assert.True(
                previous.X == current.X || previous.Y == current.Y,
                $"segment {index} runs diagonally from {previous} to {current}");
        }
    }

    /// <summary>
    /// The inset is what the drawn frame is actually stroked at, so it has to
    /// shrink the shape rather than move it: every point of the inset outline
    /// stays inside the un-inset one.
    /// </summary>
    [Fact]
    public void The_inset_shrinks_the_frame_rather_than_shifting_it()
    {
        var rock = RockColumn();
        var from = new GridPoint(4, 2);
        var to = new GridPoint(6, 4);
        var bounds = SelectionGeometry.Bounds(from, to, rock, Tile);

        var inset = SelectionGeometry.Outline(from, to, rock, Tile, 2);

        Assert.All(inset, point =>
        {
            Assert.InRange(point.X, bounds.X, bounds.X + bounds.Width);
            Assert.InRange(point.Y, bounds.Y, bounds.Y + bounds.Height);
        });
    }

    /// <summary>
    /// Criterion 2 of Issue #99. The caption is anchored to the frame, and a
    /// selection whose first row is rock on row 0 has a negative anchor, because
    /// the raised top of a wall genuinely sticks out above the map. The clamp is
    /// what keeps the number on screen: HUD masks cover every canvas pixel outside
    /// the world viewport, so an overhanging caption is a hidden caption.
    /// </summary>
    [Theory]
    [InlineData(CameraView.MinimumTileSize)]
    [InlineData(CameraView.DefaultTileSize)]
    [InlineData(CameraView.MaximumTileSize)]
    public void The_cell_count_stays_inside_the_map_at_every_tile_size(int tileSize)
    {
        var map = CameraView.MapSize(tileSize);
        var caption = new ViewSize(58, 14);
        var rock = new HashSet<GridPoint>();
        for (var x = 0; x < PrototypeTuning.MapWidth; x++)
        {
            for (var y = 0; y < PrototypeTuning.MapHeight; y++)
            {
                rock.Add(new GridPoint(x, y));
            }
        }

        foreach (var corner in EveryCorner())
        {
            foreach (var tiles in new[] { NoRock(), (IReadOnlySet<GridPoint>)rock })
            {
                var bounds = SelectionGeometry.Bounds(corner.From, corner.To, tiles, tileSize);

                var box = SelectionGeometry.CaptionBox(
                    new ViewPoint(bounds.X, bounds.Y),
                    caption,
                    3,
                    tileSize);

                Assert.InRange(box.X, 0, map.Width - caption.Width);
                Assert.InRange(box.Y, 0, map.Height - caption.Height);
                Assert.Equal(caption.Width, box.Width);
                Assert.Equal(caption.Height, box.Height);
            }
        }
    }

    /// <summary>
    /// The caption prefers the space above the selection and only drops inside it
    /// when there is none, which is the behaviour the flat frame had. A raised
    /// rock top on row 0 is exactly the case that used to have room and now does
    /// not.
    /// </summary>
    [Fact]
    public void The_cell_count_sits_above_the_selection_when_there_is_room()
    {
        var caption = new ViewSize(58, 14);

        var roomy = SelectionGeometry.CaptionBox(new ViewPoint(120, 200), caption, 3, Tile);
        var cramped = SelectionGeometry.CaptionBox(new ViewPoint(120, -14), caption, 3, Tile);

        Assert.Equal(200 - 14 - 3, roomy.Y);
        Assert.Equal(0, cramped.Y);
    }

    private static IEnumerable<(GridPoint From, GridPoint To)> EveryCorner()
    {
        var last = new GridPoint(PrototypeTuning.MapWidth - 1, PrototypeTuning.MapHeight - 1);
        yield return (new GridPoint(0, 0), new GridPoint(0, 0));
        yield return (new GridPoint(0, 0), last);
        yield return (last, last);
        yield return (new GridPoint(last.X, 0), new GridPoint(last.X, 0));
        yield return (new GridPoint(0, last.Y), new GridPoint(0, last.Y));
        yield return (new GridPoint(3, 0), new GridPoint(9, 2));
    }
}
