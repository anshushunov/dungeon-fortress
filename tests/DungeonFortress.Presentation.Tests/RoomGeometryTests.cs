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
    /// picks the inset the same way <c>Main.DrawRoomBorder</c> does — plain
    /// <see cref="RoomGeometry.BorderInset"/> unless <see cref="RoomGeometry.BordersWallToNorth"/>
    /// says otherwise — so the test exercises the real decision and not a second
    /// copy of it. A positive overlap means the two are not yet apart.
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
            var purposeInset = RoomGeometry.BordersWallToNorth(room.Perimeter, rockTiles)
                ? RoomGeometry.WallAdjacentBorderInset(room.Purpose)
                : RoomGeometry.BorderInset(room.Purpose);
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
        // RoomGeometry's own (private) BorderStrokeHalfWidth: the reference
        // pixel half-width Main.DrawRoomBorder actually strokes its line with.
        const double halfStrokeWidth = 1.0;

        var oldPositionStillConflicts = new List<string>();
        var newPositionOverlaps = new List<object>();
        foreach (var room in state.Rooms)
        {
            var purposeInset = RoomGeometry.BordersWallToNorth(room.Perimeter, rockTiles)
                ? RoomGeometry.WallAdjacentBorderInset(room.Purpose)
                : RoomGeometry.BorderInset(room.Purpose);
            var borderFarEdge = purposeInset + halfStrokeWidth;

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
    /// The wall-adjacent ladder keeps the same property <see cref="Two_purposes_next_to_each_other_do_not_share_a_border_line"/>
    /// proves for the plain one: every purpose still gets a distinct depth, and
    /// none of them eats a whole one-tile room even at the smallest tile size
    /// ADR 0008 allows.
    /// </summary>
    [Fact]
    public void Wall_adjacent_insets_are_still_distinct_and_bounded()
    {
        var purposes = Enum.GetValues<ZoneKind>();
        for (var index = 1; index < purposes.Length; index++)
        {
            Assert.NotEqual(
                RoomGeometry.WallAdjacentBorderInset(purposes[index - 1]),
                RoomGeometry.WallAdjacentBorderInset(purposes[index]));
        }

        foreach (var purpose in purposes)
        {
            Assert.InRange(RoomGeometry.WallAdjacentBorderInset(purpose), 1.0, 32 / 4.0);

            // And it is always the deeper of the two ladders: a room pushed by a
            // wall never ends up drawn shallower than one that is not.
            Assert.True(
                RoomGeometry.WallAdjacentBorderInset(purpose) > RoomGeometry.BorderInset(purpose));
        }
    }

    /// <summary>
    /// Review round 2's F7: the bound above passes with zero margin at its own
    /// ceiling (8.0 against 32 / 4.0). This is the number that matters more
    /// directly — whether the caption/icon block <see cref="RoomGeometry.LabelTop"/>
    /// pushes down, plus its own <see cref="RoomGeometry.LabelIconSize"/>,
    /// still fits inside one cell at the deepest the wall-adjacent ladder ever
    /// reaches.
    ///
    /// Not parametrised over 32 and 48, the real tile sizes ADR 0008 allows,
    /// and that is not a shortcut: every quantity involved is reference-pixel
    /// and pre-scale, and the real tile size and the scale it produces cancel
    /// in this comparison exactly the way they do in
    /// <see cref="Wall_adjacent_insets_are_still_distinct_and_bounded"/> above
    /// — one reference-domain assertion already covers every tile size the
    /// engine can be asked to run at, not only the two ends of the range.
    /// </summary>
    [Fact]
    public void The_label_block_still_fits_one_cell_at_the_deepest_wall_adjacent_inset()
    {
        foreach (var purpose in Enum.GetValues<ZoneKind>())
        {
            var labelBottom =
                RoomGeometry.LabelTop(RoomGeometry.WallAdjacentBorderInset(purpose)) +
                RoomGeometry.LabelIconSize;

            Assert.True(
                labelBottom < 32,
                $"{purpose}: label block reaches {labelBottom} reference px of a " +
                "32 px minimum tile — no longer inside one cell");
        }
    }

    /// <summary>
    /// The predicate that switches ladders: true only when a cell of the room
    /// really does have rock to its north, false for a room with no such
    /// neighbour and false for a room whose only rock neighbour is on another
    /// side (Issue #139 is specifically about the north-facing facade overhang,
    /// per <see cref="WallTopology.HasFrontFacade"/>).
    /// </summary>
    [Fact]
    public void BordersWallToNorth_reads_only_the_north_neighbour()
    {
        var wallToNorth = new HashSet<GridPoint> { new(0, -1) };
        Assert.True(RoomGeometry.BordersWallToNorth([new GridPoint(0, 0)], wallToNorth));

        var wallToSouth = new HashSet<GridPoint> { new(0, 1) };
        Assert.False(RoomGeometry.BordersWallToNorth([new GridPoint(0, 0)], wallToSouth));

        Assert.False(RoomGeometry.BordersWallToNorth([new GridPoint(0, 0)], new HashSet<GridPoint>()));
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
    /// wall/floor passes, not a stand-in. Without this, <see cref="RoomGeometry.BordersWallToNorth"/>
    /// would always see an empty wall set and the fix in RoomGeometry.cs would
    /// never fire, however correct it is on its own.
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
    /// The second half: <c>Main.DrawRoomBorder</c> must actually pick
    /// <see cref="RoomGeometry.WallAdjacentBorderInset"/> when
    /// <see cref="RoomGeometry.BordersWallToNorth"/> says a wall is north, and
    /// the plain <see cref="RoomGeometry.BorderInset"/> otherwise — not always
    /// one or the other regardless of what the predicate says.
    ///
    /// Compares against <see cref="Whitespace"/>-normalised text rather than
    /// the raw multi-line literal <c>AdapterSource.Body</c> returns.
    /// <c>AdapterSource</c>'s own contract (its header comment) promises that
    /// what it lets a test check "survives reformatting"; a test that pins
    /// twelve columns of indentation is the one place in this file that would
    /// not have, and review round 2's F3 is exactly that finding.
    /// </summary>
    [Fact]
    public void Main_DrawRoomBorder_branches_on_BordersWallToNorth()
    {
        var body = Whitespace(AdapterSource.Body("DrawRoomBorder"));
        Assert.Contains(
            "RoomGeometry.BordersWallToNorth(room.Perimeter, rockTiles) " +
            "? RoomGeometry.WallAdjacentBorderInset(room.Purpose) " +
            ": RoomGeometry.BorderInset(room.Purpose);",
            body,
            StringComparison.Ordinal);
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
