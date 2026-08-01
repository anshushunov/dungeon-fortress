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

        // And no inset eats a whole tile, or a one-cell room would have no inside
        // left. The ceiling is RoomGeometry's own, in the reference pixels the
        // inset is measured in; it used to be `32 / 4.0` here, which compared a
        // reference-pixel quantity against the smallest *screen* tile ADR 0008
        // allows — the units mistake the debt ledger recorded until PR #151 closed it,
        // and the reason
        // Issue #147 had to answer what the ceiling actually is before it could
        // move the ladder past the old one.
        foreach (var purpose in purposes)
        {
            Assert.InRange(
                RoomGeometry.BorderInset(purpose),
                1.0,
                RoomGeometry.MaximumBorderInset);
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
    /// Issue #139: a room's border must not sit inside the band a neighbouring
    /// wall's front facade occupies. The wall is drawn as a volume — a top plus a
    /// facade that overhangs downward past the wall's own footprint, per
    /// <see cref="WallRenderGeometry"/> — and when a room cell's north neighbour is
    /// rock, that facade hangs over part of the room's own cell. If the room's
    /// north border line is drawn inside that overhang, the border reads as
    /// sitting on the wall rather than around the room.
    ///
    /// This walks every room of the shipped starting map — not one screenshot of
    /// two rooms — and measures the signed gap between the drawn line (its near
    /// edge, since <c>DrawLine</c> gives the stroke width and the eye reads the
    /// whole stroke) and the facade's bottom edge, in scaled screen pixels at the
    /// same tile size <see cref="RoomGeometryTests"/> uses everywhere else. It
    /// picks the inset the same way <c>Main.DrawRoomBorder</c> does — through
    /// <see cref="RoomGeometry.BorderInsetFor"/> — so the test exercises the real
    /// decision and not a second copy of it. A positive overlap means the two are
    /// not yet apart.
    ///
    /// Kept alongside the wider all-sides sweep Issue #147 added
    /// (<see cref="RoomWallClearanceTests"/>) rather than folded into it: this is
    /// the property #139 bought, written the way #139 wrote it, and a narrower
    /// check that names one mechanism is worth keeping next to a general one.
    /// </summary>
    [Fact]
    public void The_border_of_every_room_clears_a_neighbouring_walls_facade()
    {
        var state = PresentationFixtures.Baseline(1);
        var rockTiles = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(Tile);
        // Main.DrawRoomBorder strokes the line RoomGeometry.BorderStrokeWidth
        // wide; a line drawn "at" y actually covers y ± half that width.
        var halfStrokeWidth = scale * RoomGeometry.BorderStrokeHalfWidth;

        var overlaps = new List<object>();
        foreach (var room in state.Rooms)
        {
            var purposeInset =
                RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rockTiles);
            var insetScaled = purposeInset * scale;
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

    /// <summary>
    /// Issue #139 F1 (independent review, round 2): the border must not sit
    /// inside the band a room's own caption and icon occupy either. Checkpoint
    /// 2 fixed the border-vs-facade overlap above and, in doing so, pushed
    /// some rooms' border deep enough to cut through the caption and icon
    /// <c>Main.DrawRoomLabel</c> draws on the same anchor cell — review found
    /// it on the very frames checkpoint 2's own evidence captured, for
    /// farm@1,1, kitchen@9,6 and larder@13,6, the three rooms whose border
    /// this issue actually deepens on the shipped map.
    ///
    /// The anchor cell always has a drawn north border edge at the room's
    /// picked inset: it is the topmost cell of the room by construction
    /// (<see cref="RoomGeometry.LabelAnchor"/>), so no room cell can sit north
    /// of it. The border and the label therefore always compete for the same
    /// row, on every room, whether or not that room borders a wall.
    ///
    /// The check is written so it can fail: it first shows that the label's
    /// old, unconditional position — <see cref="RoomGeometry.LabelDefaultTop"/>,
    /// what <c>Main.DrawRoomLabel</c> drew at before this round — really would
    /// still overlap the border's stroke band for the rooms review named, and
    /// only then checks that <see cref="RoomGeometry.LabelTop"/> — the
    /// position it draws at now, per the wiring tests below — does not, for
    /// every room of the map.
    /// </summary>
    [Fact]
    public void The_border_of_every_room_clears_its_own_caption_and_icon()
    {
        var state = PresentationFixtures.Baseline(1);
        var rockTiles = state.Map.RockTiles.ToHashSet();

        var oldPositionStillConflicts = new List<string>();
        var newPositionOverlaps = new List<object>();
        foreach (var room in state.Rooms)
        {
            var purposeInset =
                RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rockTiles);
            var borderFarEdge = purposeInset + RoomGeometry.BorderStrokeHalfWidth;

            if (RoomGeometry.LabelDefaultTop < borderFarEdge)
            {
                oldPositionStillConflicts.Add(room.Id);
            }

            var labelTop = RoomGeometry.LabelTop(purposeInset);
            if (labelTop < borderFarEdge)
            {
                newPositionOverlaps.Add(new
                {
                    room = room.Id,
                    purpose = room.Purpose.ToString(),
                    purposeInset,
                    borderFarEdge,
                    labelTop,
                });
            }
        }

        Assert.True(
            oldPositionStillConflicts.Count >= 3 &&
            new[] { "farm@1,1", "kitchen@9,6", "larder@13,6" }.All(oldPositionStillConflicts.Contains),
            "expected the pre-round-2 fixed label position to still conflict on at least " +
            "farm@1,1, kitchen@9,6 and larder@13,6 (the rooms review named) — got: " +
            string.Join(", ", oldPositionStillConflicts));

        var payload = JsonSerializer.Serialize(
            new { overlapCount = newPositionOverlaps.Count, overlaps = newPositionOverlaps },
            new JsonSerializerOptions { WriteIndented = true });
        Assert.True(newPositionOverlaps.Count == 0, payload);
    }

    /// <summary>
    /// Every wall-aware ladder keeps the same property
    /// <see cref="Two_purposes_next_to_each_other_do_not_share_a_border_line"/>
    /// proves for the plain one, on every one of the sixteen side profiles
    /// <see cref="RoomGeometry.WallSides"/> can return and not only on the one
    /// Issue #139 had: every purpose still gets a distinct depth, none of them
    /// eats a whole one-tile room, and a room pushed by a wall is never drawn
    /// shallower than the same room would be with nothing against it.
    /// </summary>
    [Fact]
    public void Wall_aware_insets_are_still_distinct_and_bounded()
    {
        var purposes = Enum.GetValues<ZoneKind>();
        foreach (var sides in AllSideProfiles())
        {
            for (var index = 1; index < purposes.Length; index++)
            {
                Assert.NotEqual(
                    RoomGeometry.BorderInset(purposes[index - 1], sides),
                    RoomGeometry.BorderInset(purposes[index], sides));
            }

            foreach (var purpose in purposes)
            {
                Assert.InRange(
                    RoomGeometry.BorderInset(purpose, sides),
                    1.0,
                    RoomGeometry.MaximumBorderInset);
                Assert.True(
                    RoomGeometry.BorderInset(purpose, sides) >=
                    RoomGeometry.BorderInset(purpose),
                    $"{purpose} at {sides} is drawn shallower than with no wall at all");
            }
        }
    }

    /// <summary>
    /// A wall to the south is the one side that buys no inset at all, and the one
    /// that must not: <see cref="RoomGeometry.WallSides"/> reports it, and
    /// <see cref="RoomGeometry.BorderInset(ZoneKind, WallNeighbors)"/> ignores it
    /// on purpose. <see cref="RoomWallClearanceTests"/> is where the reason is
    /// measured rather than asserted; this only pins that the deliberate choice
    /// is still the one the code makes, because "south does nothing" is exactly
    /// what a careless generalisation of this ladder would quietly change.
    /// </summary>
    [Fact]
    public void A_wall_to_the_south_alone_does_not_move_the_border()
    {
        foreach (var purpose in Enum.GetValues<ZoneKind>())
        {
            Assert.Equal(
                RoomGeometry.BorderInset(purpose),
                RoomGeometry.BorderInset(purpose, WallNeighbors.South));
        }
    }

    /// <summary>
    /// The debt the ledger recorded against Issue #139, re-measured rather than
    /// re-argued: two rooms painted over each other, of which only one touches a
    /// wall, can be handed the same inset and draw one line where #52 bought two.
    ///
    /// The ledger's own example was <c>2.0 + 3.0 == 5.0 + 0.0</c>. Issue #147
    /// moved every base, so the question "did that get worse" has to be answered
    /// with a number and not with a shrug: this counts the colliding pairs across
    /// the whole cross-product of side profile and purpose, and pins the count.
    /// It is not zero and cannot be made zero by choosing better numbers here —
    /// the collision is <see cref="WallRenderGeometry.FacadeReferenceOverhang"/>
    /// (3.0, owned by Issue #83) landing exactly on the widest step of the
    /// purpose ladder (3.0, owned by Issue #52), and neither constant belongs to
    /// this issue. What is checked is that #147 did not add to it.
    /// </summary>
    [Fact]
    public void Overlapping_rooms_with_different_wall_profiles_collide_no_more_than_before()
    {
        var purposes = Enum.GetValues<ZoneKind>();
        var collisions = new List<(double Shallower, double Deeper, double Inset)>();

        // Distinct *bases*, since two profiles that produce the same base are the
        // same ladder and cannot collide with each other in a way #52 cares about.
        var bases = AllSideProfiles()
            .Select(RoomGeometry.WallClearance)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        Assert.Equal(
            [
                RoomGeometry.PlainBorderBase,
                RoomGeometry.SideWallClearance,
                RoomGeometry.NorthWallClearance,
            ],
            bases);

        foreach (var left in bases)
        {
            foreach (var right in bases)
            {
                if (left >= right)
                {
                    continue;
                }

                foreach (var first in purposes)
                {
                    foreach (var second in purposes)
                    {
                        var shallow = left + PurposeStep(first);
                        if (Math.Abs(shallow - (right + PurposeStep(second))) < 1e-9)
                        {
                            collisions.Add((left, right, shallow));
                        }
                    }
                }
            }
        }

        // Before #147 the bases were {2.0, 5.0} and the one colliding rung was
        // 2.0 + 3.0 == 5.0 + 0.0, over the two purposes with step 3.0 against the
        // three with step 0.0 — six ordered pairs. After #147 the bases are
        // {2.0, 2.625, 5.625} and the surviving rung is 2.625 + 3.0 == 5.625 + 0,
        // the same six pairs. The plain-against-north collision the ledger named
        // is gone; a room with nothing against it can no longer be mistaken for a
        // room under a wall.
        var payload = JsonSerializer.Serialize(
            collisions.Select(item => new
            {
                shallowerBase = item.Shallower,
                deeperBase = item.Deeper,
                inset = item.Inset,
            }),
            new JsonSerializerOptions { WriteIndented = true });
        Assert.True(collisions.Count == 6, payload);
        Assert.All(
            collisions,
            collision =>
            {
                Assert.Equal(RoomGeometry.SideWallClearance, collision.Shallower);
                Assert.Equal(RoomGeometry.NorthWallClearance, collision.Deeper);
            });
    }

    /// <summary>
    /// The purpose step, restated from <c>RoomGeometry</c>'s private
    /// <c>PurposeLadderStep</c> — a test about how two ladders line up needs the
    /// rungs, and the alternative is making the ladder public for one caller.
    /// <see cref="Two_purposes_next_to_each_other_do_not_share_a_border_line"/>
    /// is what keeps this honest: it reads the real method.
    /// </summary>
    private static double PurposeStep(ZoneKind purpose) =>
        RoomGeometry.BorderInset(purpose) - RoomGeometry.PlainBorderBase;

    /// <summary>Every combination of sides a room can have rock against.</summary>
    private static IEnumerable<WallNeighbors> AllSideProfiles() =>
        Enumerable.Range(0, 16).Select(bits => (WallNeighbors)bits);

    /// <summary>
    /// Review round 2's F7: the bound above passes with zero margin at its own
    /// ceiling (8.0 against 32 / 4.0). This is the number that matters more
    /// directly — whether the caption/icon block <see cref="RoomGeometry.LabelTop"/>
    /// pushes down, plus its own <see cref="RoomGeometry.LabelIconSize"/>,
    /// still fits inside one cell at the deepest the ladder ever reaches, which
    /// after Issue #147 is 8.625 rather than 8.0.
    ///
    /// Not parametrised over 32 and 48, the real tile sizes ADR 0008 allows,
    /// and that is not a shortcut: every quantity involved is reference-pixel
    /// and pre-scale, and the real tile size and the scale it produces cancel
    /// in this comparison exactly the way they do in
    /// <see cref="Wall_aware_insets_are_still_distinct_and_bounded"/> above
    /// — one reference-domain assertion already covers every tile size the
    /// engine can be asked to run at, not only the two ends of the range.
    ///
    /// Review round 3's N3: that argument is the reason the ceiling has to be
    /// the reference tile size and not the real screen-pixel minimum — comparing
    /// a reference-pixel quantity against 32 is a units mismatch. Issue #147
    /// moved the restated 22.0 out of this file and into
    /// <see cref="RoomGeometry.ReferenceCell"/>, where
    /// <see cref="ReferenceCell_is_the_one_CameraView_scales_by"/> pins it to
    /// <c>CameraView</c>'s private copy executably instead of by comment.
    /// </summary>
    [Fact]
    public void The_label_block_still_fits_one_cell_at_the_deepest_wall_aware_inset()
    {
        foreach (var sides in AllSideProfiles())
        {
            foreach (var purpose in Enum.GetValues<ZoneKind>())
            {
                var labelBottom =
                    RoomGeometry.LabelTop(RoomGeometry.BorderInset(purpose, sides)) +
                    RoomGeometry.LabelIconSize;

                Assert.True(
                    labelBottom < RoomGeometry.ReferenceCell,
                    $"{purpose} at {sides}: label block reaches {labelBottom} reference px " +
                    $"of a {RoomGeometry.ReferenceCell} reference-px cell — no longer " +
                    "inside one cell");
            }
        }
    }

    /// <summary>
    /// <see cref="RoomGeometry.ReferenceCell"/> restates a constant
    /// <c>CameraView</c> keeps private, and a restated constant is the shape of
    /// debt the ledger already records twice against this corner of the code. So
    /// it is not left restated: <see cref="CameraView.WorldVisualScale"/> divides
    /// by exactly that constant, so asking it for the scale of a real tile size
    /// and comparing with the ratio computed here pins the two together without
    /// widening anybody's API.
    ///
    /// 44 rather than the reference cell itself, because
    /// <see cref="CameraView.ValidateTileSize"/> refuses anything outside ADR
    /// 0008's 32–48 range and 22 is outside it.
    /// </summary>
    [Fact]
    public void ReferenceCell_is_the_one_CameraView_scales_by()
    {
        const int tile = 44;

        Assert.Equal(tile / RoomGeometry.ReferenceCell, CameraView.WorldVisualScale(tile));
    }

    /// <summary>
    /// The predicate that picks a ladder. North and south read the straight
    /// neighbour; east and west read the whole column beside the cell, because a
    /// wall's side seam runs the height of a mass that is lifted above its own
    /// row and hangs below it, so a diagonal neighbour paints into the cell at
    /// the same depth as a straight one (Issue #147).
    /// </summary>
    [Fact]
    public void WallSides_reads_straight_neighbours_north_and_south_and_columns_beside()
    {
        GridPoint[] room = [new(0, 0)];

        Assert.Equal(
            WallNeighbors.North,
            RoomGeometry.WallSides(room, new HashSet<GridPoint> { new(0, -1) }));
        Assert.Equal(
            WallNeighbors.South,
            RoomGeometry.WallSides(room, new HashSet<GridPoint> { new(0, 1) }));
        Assert.Equal(
            WallNeighbors.West,
            RoomGeometry.WallSides(room, new HashSet<GridPoint> { new(-1, 0) }));
        Assert.Equal(
            WallNeighbors.East,
            RoomGeometry.WallSides(room, new HashSet<GridPoint> { new(1, 0) }));

        // The diagonals: a wall one row up and one column across is a west or an
        // east neighbour for this purpose and nothing else. quarters@19,2 has
        // exactly this at (18,1) and it alone would earn the room its inset.
        Assert.Equal(
            WallNeighbors.West,
            RoomGeometry.WallSides(room, new HashSet<GridPoint> { new(-1, -1) }));
        Assert.Equal(
            WallNeighbors.East,
            RoomGeometry.WallSides(room, new HashSet<GridPoint> { new(1, 1) }));

        // And a diagonal is not a north or a south neighbour: a facade's overhang
        // and a lifted top mass stay inside the wall's own column.
        Assert.Equal(
            WallNeighbors.None,
            RoomGeometry.WallSides(room, new HashSet<GridPoint>()));
        Assert.False(
            RoomGeometry.WallSides(room, new HashSet<GridPoint> { new(-1, -1) })
                .HasFlag(WallNeighbors.North));
    }

    // -------------------------------------------------- the adapter's wiring
    //
    // RoomGeometry is pure and covered above; nothing in this project can build
    // Main.cs, which needs the engine (ADR 0011). AdapterSource reads it as text
    // instead — the same technique HudReadabilityTests uses for LayoutHud and
    // MakeCustomTooltip — so the two ends of the wiring that connect the pure
    // fix to the drawn frame have their own checked, mutable failure mode rather
    // than depending on the geometry tests above by accident.

    /// <summary>
    /// The first half of the wiring: <c>Main.DrawMap</c> must hand
    /// <c>DrawRoomBorders</c> the real rock set it already computed for the
    /// wall/floor passes, not a stand-in. Without this,
    /// <see cref="RoomGeometry.WallSides"/> would always see an empty wall set
    /// and the fix in RoomGeometry.cs would never fire, however correct it is on
    /// its own.
    /// </summary>
    [Fact]
    public void Main_DrawMap_passes_the_real_rock_tiles_into_DrawRoomBorders()
    {
        var body = AdapterSource.Body("DrawMap");
        var calls = AdapterSource.CallsTo(body, "DrawRoomBorders");
        var call = Assert.Single(calls);
        Assert.Equal(["rockTiles"], call.Arguments);
    }

    /// <summary>
    /// The second half: <c>Main.DrawRoomBorder</c> must take its inset from
    /// <see cref="RoomGeometry.BorderInsetFor"/>, handing it the room's real
    /// purpose, perimeter and rock set — not from the plain purpose-only ladder,
    /// which is what the defect looked like.
    ///
    /// Issue #139 pinned a ternary written out here and copied into
    /// <c>DrawRoomLabel</c>, and spent two review rounds proving the two copies
    /// still agreed. Issue #147 moved the decision behind one method, so the two
    /// bodies now name the same call and there is no copy left to diverge.
    ///
    /// Compares against <see cref="Whitespace"/>-normalised text rather than
    /// the raw multi-line literal <c>AdapterSource.Body</c> returns.
    /// <c>AdapterSource</c>'s own contract (its header comment) promises that
    /// what it lets a test check "survives reformatting"; a test that pins
    /// twelve columns of indentation is the one place in this file that would
    /// not have, and review round 2's F3 is exactly that finding.
    /// </summary>
    [Fact]
    public void Main_DrawRoomBorder_takes_its_inset_from_BorderInsetFor()
    {
        var body = Whitespace(AdapterSource.Body("DrawRoomBorder"));
        Assert.Contains(
            "RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rockTiles);",
            body,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And it strokes the line at the width <see cref="RoomGeometry"/> declares,
    /// not at a literal of its own. The debt ledger recorded, until PR #151 closed
    /// the entry, that <c>BorderStrokeHalfWidth</c> was a silent
    /// copy of half a <c>ScaleWorld(2.0f)</c> in this method, with no executable
    /// link between them — and every clearance Issue #147 derives is measured
    /// from that half-width, so the copy stopped being harmless.
    /// </summary>
    [Fact]
    public void Main_DrawRoomBorder_strokes_the_width_RoomGeometry_declares()
    {
        var body = AdapterSource.Body("DrawRoomBorder");
        var scaleWorldCalls = AdapterSource.CallsTo(body, "ScaleWorld");
        Assert.Contains(
            scaleWorldCalls,
            call => call.Arguments.Any(argument =>
                argument.Contains("RoomGeometry.BorderStrokeWidth", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The same, for the other side of the same measurement: <c>Main.DrawWall</c>
    /// must stroke each seam at the width
    /// <see cref="WallRenderGeometry.ReferenceStrokeWidth"/> declares for its
    /// kind. Half of that width is what a side seam paints into the neighbouring
    /// floor cell, which is the whole of Issue #147's arithmetic; a literal here
    /// would leave <see cref="RoomGeometry.SideWallClearance"/> guessing.
    /// </summary>
    [Fact]
    public void Main_DrawWall_strokes_the_width_WallRenderGeometry_declares()
    {
        var body = AdapterSource.Body("DrawWall");
        var scaleWorldCalls = AdapterSource.CallsTo(body, "ScaleWorld");
        Assert.Contains(
            scaleWorldCalls,
            call => call.Arguments.Any(argument => argument.Contains(
                "WallRenderGeometry.ReferenceStrokeWidth(stroke.Kind)",
                StringComparison.Ordinal)));
    }

    /// <summary>
    /// The third link, missing until review round 2's F2: neither of the two
    /// tests above can tell whether <c>purposeInset</c> — the ternary's own
    /// result — ever reaches the line actually drawn. Review reproduced the
    /// gap with a real mutant: <c>var inset = ScaleWorld((float)purposeInset);</c>
    /// rewritten to <c>var inset = ScaleWorld((float)RoomGeometry.BorderInset(room.Purpose));</c>
    /// leaves both structural tests above green — the ternary's text is
    /// untouched, it is just computed and thrown away — while every room's
    /// border silently goes back to the plain, pre-#139 ladder. This closes
    /// that: some call the method makes to <c>ScaleWorld</c> must be handed
    /// <c>purposeInset</c> by name, not a value that merely happens to equal
    /// it once.
    /// </summary>
    [Fact]
    public void Main_DrawRoomBorder_scales_the_picked_inset_it_computed()
    {
        var body = AdapterSource.Body("DrawRoomBorder");
        var scaleWorldCalls = AdapterSource.CallsTo(body, "ScaleWorld");
        Assert.Contains(
            scaleWorldCalls,
            call => call.Arguments.Any(argument =>
                argument.Contains("purposeInset", StringComparison.Ordinal)));
    }

    /// <summary>
    /// F1's own wiring, guarded the same three ways as the border's above so
    /// the exact gap F1 and F2 each found cannot reopen here unnoticed: first,
    /// that <c>DrawMap</c> hands <c>DrawRoomLabels</c> the real rock set.
    /// </summary>
    [Fact]
    public void Main_DrawMap_passes_the_real_rock_tiles_into_DrawRoomLabels()
    {
        var body = AdapterSource.Body("DrawMap");
        var calls = AdapterSource.CallsTo(body, "DrawRoomLabels");
        var call = Assert.Single(calls);
        Assert.Equal(["rockTiles"], call.Arguments);
    }

    /// <summary>
    /// Second and third: that the icon and the caption are each actually drawn
    /// at a point built from <c>labelTop</c> — the computed, wall-aware
    /// position — rather than at a value that merely happens to equal it once
    /// or a position that quietly reverts to the pre-#139-F1 constant. Checked
    /// through <c>CallsTo</c>'s own argument split, the same technique
    /// <see cref="Main_DrawRoomBorder_scales_the_picked_inset_it_computed"/>
    /// uses for the border, so this survives reformatting the same way that
    /// one does and the raw multi-line literal F3 replaced does not.
    /// </summary>
    [Fact]
    public void Main_DrawRoomLabel_draws_the_icon_and_caption_at_the_computed_label_top()
    {
        var body = AdapterSource.Body("DrawRoomLabel");

        var iconCalls = AdapterSource.CallsTo(body, "DrawRoomIcon");
        var iconCall = Assert.Single(iconCalls);
        Assert.Contains(
            iconCall.Arguments,
            argument => argument.Contains("labelTop", StringComparison.Ordinal));

        var captionCalls = AdapterSource.CallsTo(body, "DrawString");
        var captionCall = Assert.Single(captionCalls);
        Assert.Contains(
            captionCall.Arguments,
            argument => argument.Contains("labelTop", StringComparison.Ordinal));
    }

    /// <summary>
    /// Review round 3's N1: the test above proves the icon and caption are
    /// drawn from <c>labelTop</c> by name, but nothing proved where
    /// <c>labelTop</c> itself came from — a mutant that reads
    /// <c>RoomGeometry.LabelDefaultTop</c> straight, with <c>purposeInset</c>
    /// computed and then thrown away, still assigns the result to a variable
    /// named <c>labelTop</c> and passes to both draw calls by that name. That
    /// mutant is L-1 in review's own numbering, and it reopens F1 verbatim
    /// while leaving the test above green.
    ///
    /// <c>AdapterSource.CallsTo</c> cannot see this call directly —
    /// <c>RoomGeometry.LabelTop(...)</c> is qualified on a different type, and
    /// <c>CallsTo</c> only finds a call the adapter makes on itself (its own
    /// header comment; <see cref="Main_DrawRoomBorder_scales_the_picked_inset_it_computed"/>
    /// only works because <c>ScaleWorld</c> is unqualified). The same
    /// <see cref="Whitespace"/> normalisation
    /// <see cref="Main_DrawRoomBorder_takes_its_inset_from_BorderInsetFor"/>
    /// already uses for exactly this reason stands in for it here.
    /// </summary>
    [Fact]
    public void Main_DrawRoomLabel_computes_labelTop_from_the_picked_inset()
    {
        var body = Whitespace(AdapterSource.Body("DrawRoomLabel"));
        Assert.Contains("RoomGeometry.LabelTop(purposeInset)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review round 3's N1, second mutant (L-2): <c>DrawRoomLabel</c> used to
    /// compute its own <c>purposeInset</c> from an independent copy of
    /// <c>DrawRoomBorder</c>'s ternary, and nothing proved the copy still
    /// branched the same way. Issue #147 deleted the copy — both bodies now call
    /// <see cref="RoomGeometry.BorderInsetFor"/> — so what this pins is that the
    /// label reads the same one decision the border does, against
    /// <c>DrawRoomLabel</c>'s body instead of <c>DrawRoomBorder</c>'s. A label
    /// silently back on the plain ladder reopens F1 verbatim.
    /// </summary>
    [Fact]
    public void Main_DrawRoomLabel_takes_its_inset_from_BorderInsetFor()
    {
        var body = Whitespace(AdapterSource.Body("DrawRoomLabel"));
        Assert.Contains(
            "RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rockTiles);",
            body,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Collapses every run of whitespace — spaces, tabs, line breaks, any
    /// indentation a formatter chooses — to a single space, the same
    /// normalisation <c>AdapterSource</c> already applies internally to a
    /// call's own argument text (its private <c>Compact</c>), so a check
    /// against <c>Body</c>'s raw, un-compacted text can have the same
    /// property without adding a public method to a shared test helper for
    /// one caller.
    /// </summary>
    private static string Whitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
