using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The border of a room, which ADR 0013 makes mandatory rather than optional:
/// «явная граница по периметру — вариант C обязан рисовать её, иначе повторяется
/// ошибка Dwarf Fortress».
///
/// What is checked here is the difference between a border and what the map drew
/// before: a box around every cell of the zone. Twenty-one boxes are not a room,
/// and the test that says so is <see cref="A_room_is_one_outline_and_not_a_box_per_cell"/>.
/// </summary>
public sealed class RoomGeometryTests
{
    // The middle of the 32–48 px range ADR 0008 allows; CameraView refuses
    // anything outside it, so the numbers below are real screen coordinates and
    // not a convenient decimal fiction.
    private const int Tile = 40;
    private const double Inset = 4;

    private static IReadOnlyList<ViewSegment> Border(params GridPoint[] cells) =>
        RoomGeometry.Border(cells, Tile, Inset);

    /// <summary>
    /// One cell is a closed square, inset on all four sides. Every corner meets:
    /// the ends of the four segments are the same four points.
    /// </summary>
    [Fact]
    public void A_single_cell_is_a_closed_inset_square()
    {
        var border = Border(new GridPoint(0, 0));

        Assert.Equal(4, border.Count);
        Assert.Equal(
            [
                new ViewSegment(new ViewPoint(4, 4), new ViewPoint(36, 4)),
                new ViewSegment(new ViewPoint(36, 4), new ViewPoint(36, 36)),
                new ViewSegment(new ViewPoint(4, 36), new ViewPoint(36, 36)),
                new ViewSegment(new ViewPoint(4, 4), new ViewPoint(4, 36)),
            ],
            border);
    }

    /// <summary>
    /// The point of the whole change, as a number. Two touching cells produce six
    /// segments and not eight, and the shared edge is not drawn at all: the two
    /// north edges butt together into one continuous line rather than each ending
    /// short of the other.
    /// </summary>
    [Fact]
    public void A_room_is_one_outline_and_not_a_box_per_cell()
    {
        var border = Border(new GridPoint(0, 0), new GridPoint(1, 0));

        Assert.Equal(6, border.Count);

        var north = border
            .Where(segment => segment.From.Y == 4 && segment.To.Y == 4)
            .OrderBy(segment => segment.From.X)
            .ToArray();
        Assert.Equal(2, north.Length);
        Assert.Equal(4, north[0].From.X);
        Assert.Equal(north[0].To.X, north[1].From.X);
        Assert.Equal(76, north[1].To.X);

        // Nothing is drawn on the wall the two cells share.
        Assert.DoesNotContain(border, segment => segment.From.X == 40 && segment.To.X == 40);
    }

    /// <summary>
    /// An inner corner is pushed out rather than pulled back, so the outline closes
    /// there too. In an L the west edge of the foot has to reach up past the grid
    /// line to meet the south edge of the cell above-left, and the two meet at the
    /// same point.
    /// </summary>
    [Fact]
    public void An_inner_corner_closes_instead_of_leaving_a_gap()
    {
        var border = Border(new GridPoint(0, 0), new GridPoint(1, 0), new GridPoint(1, 1));

        var westOfTheFoot = Assert.Single(
            border.Where(segment => segment.From.X == 44 && segment.To.X == 44));
        var southOfTheCorner = Assert.Single(
            border.Where(segment => segment.From.Y == 36 && segment.To.Y == 36));

        Assert.Equal(36, westOfTheFoot.From.Y);
        Assert.Equal(new ViewPoint(4, 36), southOfTheCorner.From);
        Assert.Equal(new ViewPoint(44, 36), southOfTheCorner.To);
        Assert.Equal(southOfTheCorner.To, new ViewPoint(westOfTheFoot.From.X, westOfTheFoot.From.Y));
    }

    /// <summary>
    /// A room with a hole in it draws the hole. A ring of eight cells has twelve
    /// outer edges and four inner ones, and both are boundary in exactly the same
    /// sense — which is why nothing here special-cases them.
    /// </summary>
    [Fact]
    public void A_room_with_a_hole_draws_the_hole_as_well()
    {
        var ring = new List<GridPoint>();
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                if (x != 1 || y != 1)
                {
                    ring.Add(new GridPoint(x, y));
                }
            }
        }

        var border = RoomGeometry.Border(ring, Tile, Inset);

        Assert.Equal(16, border.Count);
        // The four edges facing the hole sit around the middle cell's footprint,
        // which spans 40..80 in both axes and is grown by the inset at each end.
        Assert.Equal(
            4,
            border.Count(segment =>
                segment.From.X is >= 36 and <= 84 &&
                segment.To.X is >= 36 and <= 84 &&
                segment.From.Y is >= 36 and <= 84 &&
                segment.To.Y is >= 36 and <= 84));
    }

    /// <summary>
    /// Nothing a room draws leaves the room. The inner-corner extension pushes an
    /// end past a grid line, and it must never push it past the room itself —
    /// otherwise a border would be drawn on a tile that belongs to somebody else.
    /// </summary>
    [Fact]
    public void No_segment_leaves_the_bounding_box_of_the_room()
    {
        GridPoint[] awkward =
        [
            new(2, 2), new(3, 2), new(4, 2),
            new(2, 3), new(4, 3),
            new(2, 4), new(3, 4), new(4, 4),
            new(6, 2),
        ];
        var bounds = RoomGeometry.Bounds(awkward, Tile);

        foreach (var segment in RoomGeometry.Border(awkward, Tile, Inset))
        {
            foreach (var point in new[] { segment.From, segment.To })
            {
                Assert.InRange(point.X, bounds.X, bounds.X + bounds.Width);
                Assert.InRange(point.Y, bounds.Y, bounds.Y + bounds.Height);
            }
        }
    }

    /// <summary>
    /// Rooms overlap — a <c>Forbidden</c> paint over a gym is the ordinary case —
    /// and two borders on the same pixels are one border. Neighbouring purposes
    /// are therefore drawn at different depths.
    /// </summary>
    [Fact]
    public void Two_purposes_next_to_each_other_do_not_share_a_border_line()
    {
        var purposes = Enum.GetValues<ZoneKind>();
        for (var index = 1; index < purposes.Length; index++)
        {
            Assert.NotEqual(
                RoomGeometry.BorderInset(purposes[index - 1]),
                RoomGeometry.BorderInset(purposes[index]));
        }

        // And no inset eats a whole tile even at the smallest tile size ADR 0008
        // allows, or a one-cell room would have no inside left.
        foreach (var purpose in purposes)
        {
            Assert.InRange(RoomGeometry.BorderInset(purpose), 1.0, 32 / 4.0);
        }
    }

    /// <summary>
    /// The caption sits on the tile the room is named after, so a player who reads
    /// <c>trainingGround@10,2</c> in a structured run finds the words at (10,2).
    /// </summary>
    [Fact]
    public void The_caption_is_anchored_on_the_tile_the_room_is_named_after()
    {
        IReadOnlyList<GridPoint> perimeter = [new(5, 3), new(4, 3), new(4, 2), new(9, 9)];

        Assert.Equal(
            CameraView.CellTopLeft(new GridPoint(4, 2), Tile),
            RoomGeometry.LabelAnchor(perimeter, Tile));
    }

    [Fact]
    public void A_room_with_no_cells_cannot_be_anchored()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RoomGeometry.LabelAnchor([], Tile));
    }

    [Fact]
    public void The_bounds_cover_every_cell_of_the_room()
    {
        var bounds = RoomGeometry.Bounds([new GridPoint(2, 1), new GridPoint(4, 5)], Tile);

        Assert.Equal(new ViewRect(80, 40, 120, 200), bounds);
    }

    /// <summary>
    /// The border is drawn from the rooms the simulation publishes, on the map the
    /// game actually ships: every default room closes, and the biggest of them
    /// draws far fewer segments than a box per cell would.
    /// </summary>
    [Fact]
    public void The_default_rooms_of_the_shipped_map_all_close()
    {
        var state = PresentationFixtures.Baseline(1);

        Assert.NotEmpty(state.Rooms);
        foreach (var room in state.Rooms)
        {
            var border = RoomGeometry.Border(
                room.Perimeter,
                Tile,
                RoomGeometry.BorderInset(room.Purpose));

            Assert.NotEmpty(border);
            Assert.True(
                border.Count < room.Perimeter.Count * 4,
                $"{room.Id} drew {border.Count} segments for {room.Perimeter.Count} cells, " +
                "which is a box per cell rather than an outline");

            // Every corner is shared: each endpoint of the outline is the endpoint
            // of exactly one other segment, which is what "closed" means for a set
            // of edges.
            var ends = border
                .SelectMany(segment => new[] { segment.From, segment.To })
                .GroupBy(point => point)
                .ToArray();
            Assert.All(ends, group => Assert.Equal(2, group.Count()));
        }
    }

    /// <summary>
    /// Issue #139, checkpoint 1: measures whether the hypothesis is real before
    /// touching any drawing code. A room's border must not sit inside the band a
    /// neighbouring wall's front facade occupies. The wall is drawn as a volume —
    /// a top plus a facade that overhangs downward past the wall's own footprint,
    /// per <see cref="WallRenderGeometry"/> — and when a room cell's north
    /// neighbour is rock, that facade hangs over part of the room's own cell.
    ///
    /// This walks every room of the shipped starting map — not one screenshot of
    /// two rooms — using the border inset exactly as <c>Main.DrawRoomBorder</c>
    /// computes it today (<see cref="RoomGeometry.BorderInset"/>, the only inset
    /// that exists before this issue's fix), and measures the signed gap between
    /// the drawn line's near edge (since <c>DrawLine</c> gives the stroke width
    /// and the eye reads the whole stroke) and the facade's bottom edge, in scaled
    /// screen pixels at the same tile size <see cref="RoomGeometryTests"/> uses
    /// everywhere else. A positive overlap means the two are not apart.
    /// </summary>
    [Fact]
    public void The_border_of_every_room_clears_a_neighbouring_walls_facade()
    {
        var state = PresentationFixtures.Baseline(1);
        var rockTiles = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(Tile);
        // Main.DrawRoomBorder strokes the line at ScaleWorld(2.0f) wide; a line
        // drawn "at" y actually covers y ± half that width.
        var halfStrokeWidth = scale * 1.0;

        var overlaps = new List<object>();
        foreach (var room in state.Rooms)
        {
            var insetScaled = RoomGeometry.BorderInset(room.Purpose) * scale;
            foreach (var cell in room.Perimeter)
            {
                var north = new GridPoint(cell.X, cell.Y - 1);
                if (!rockTiles.Contains(north))
                {
                    continue;
                }

                var variant = WallTopology.SelectVariant(north, rockTiles);
                var mass = WallRenderGeometry.ForCell(north, variant, Tile);
                if (mass.Facade is not { } facade)
                {
                    continue;
                }

                var roomTop = CameraView.CellTopLeft(cell, Tile).Y;
                var lineNearEdge = roomTop + insetScaled - halfStrokeWidth;
                var facadeBottom = facade.Y + facade.Height;
                var overlapPx = facadeBottom - lineNearEdge;
                if (overlapPx > 0)
                {
                    overlaps.Add(new
                    {
                        room = room.Id,
                        purpose = room.Purpose.ToString(),
                        cell = $"{cell.X},{cell.Y}",
                        insetScaled,
                        lineNearEdge,
                        facadeBottom,
                        overlapPx,
                    });
                }
            }
        }

        var payload = JsonSerializer.Serialize(
            new { tileSize = Tile, scale, overlapCount = overlaps.Count, overlaps },
            new JsonSerializerOptions { WriteIndented = true });

        Assert.True(overlaps.Count == 0, payload);
    }
}
