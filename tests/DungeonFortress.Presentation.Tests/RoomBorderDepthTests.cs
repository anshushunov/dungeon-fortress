using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #156: a creature standing on a room's border must be drawn over the line,
/// not under it.
///
/// <para>
/// The owner reported it from playtest — «наверно существо должно быть над
/// границей комнаты, а не под ней» — with the goblins on the bottom row of the
/// kitchen and the larder struck through by their own room's outline. It is the
/// rule Issue #83 wrote down («a mark that can share a cell with a body must not
/// hide it») meeting the one case that rule's own manifest let through: the border
/// is <see cref="OverlayMarkPolicy.StrokeOnly"/>, and the reason recorded next to
/// that policy was that a line with no fill hides nothing. A stroke two reference
/// pixels wide drawn across the middle of a twenty-two pixel cell does.
/// </para>
///
/// <para>
/// The fix is draw order and it has two halves, because the two halves pull
/// opposite ways:
/// </para>
///
/// <list type="number">
/// <item>the border is drawn <b>before</b> the depth pass, so the body walks over
/// it — <see cref="No_stroke_above_the_depth_pass_lands_on_a_body_that_is_visible"/>
/// is what fails if that is undone;</item>
/// <item><b>except</b> the pieces a wall standing directly in front of the room
/// paints over completely, which stay above the depth pass because the wall would
/// otherwise erase them outright and no inset buys them back (Issues #139, #147) —
/// <see cref="A_wall_in_front_keeps_the_piece_it_swallows_above_the_depth_pass"/>
/// is what fails if <em>that</em> is undone.</item>
/// </list>
///
/// <para>
/// Both halves are one expression, <see cref="RoomGeometry.LayerOf"/>, so each half
/// has a one-line mutant: hardwire it to <c>OverWallInFront</c> and the first check
/// reddens, to <c>UnderBodies</c> and the second does.
/// </para>
///
/// <para>
/// <b>Pieces, not edges.</b> The first round of this issue classified a whole
/// boundary edge at a time and independent review found the corner that costs:
/// <see cref="An_outline_closes_at_the_corner_a_wall_in_front_reaches"/> measures
/// it, and <see cref="The_first_round_of_156_opened_the_corner_at_a_wall_in_front"/>
/// keeps the "before" of that column reproducible too.
/// </para>
/// </summary>
public sealed class RoomBorderDepthTests
{
    /// <summary>
    /// The ends of ADR 0008's range and the default in the middle, the same three
    /// <see cref="RoomWallClearanceTests"/> measures at: every quantity here is a
    /// reference-pixel number times one scale, but a claim about what a player sees
    /// is worth making at the tile sizes a player can actually choose.
    /// </summary>
    public static TheoryData<int> TileSizes => new()
    {
        CameraView.MinimumTileSize,
        CameraView.DefaultTileSize,
        CameraView.MaximumTileSize,
    };

    // ------------------------------------------------------- what the passes are

    /// <summary>
    /// The manifest, which everything below reads rather than assumes: the border
    /// proper is declared below the depth pass and carries no informational mark,
    /// and the over-wall half is declared above it and carries
    /// <see cref="OverlayMark.RoomBorder"/>.
    ///
    /// Moving either routine back in <see cref="WorldDrawOrder"/> is one of the
    /// mutations Issue #90 lists as "nothing catches"; this is the check that does,
    /// alongside <c>WorldDrawPassGuardTests</c> holding the adapter to the same
    /// declaration.
    /// </summary>
    [Fact]
    public void The_two_halves_of_the_border_are_declared_in_the_two_passes()
    {
        foreach (var name in new[] { "DrawRoomBorders", "DrawRoomBorder" })
        {
            var routine = WorldDrawOrder.Find(name);
            Assert.NotNull(routine);
            Assert.True(
                routine!.Pass < WorldDrawPass.Depth,
                $"'{name}' is declared {routine.Pass}. A room's border is a line on " +
                "the floor a body stands on: drawn anywhere but below the depth " +
                "pass, it is drawn over that body (Issue #156).");
            Assert.Null(routine.Mark);
        }

        foreach (var name in new[] { "DrawRoomBordersOverWalls", "DrawRoomBorderOverWall" })
        {
            var routine = WorldDrawOrder.Find(name);
            Assert.NotNull(routine);
            Assert.True(
                routine!.Pass > WorldDrawPass.Depth,
                $"'{name}' is declared {routine.Pass}. The piece a wall in front " +
                "swallows is only kept by being drawn after the wall.");
            Assert.Equal(OverlayMark.RoomBorder, routine.Mark);
        }

        // The border proper is the last thing on the floor, so a stockpile
        // silhouette or a bed cannot break the outline — that would be this same
        // defect with a different thing on top of the line.
        var steps = WorldDrawOrder.Steps.ToArray();
        Assert.Equal(
            Array.IndexOf(steps, "DrawElevatedWorld") - 1,
            Array.IndexOf(steps, "DrawRoomBorders"));
    }

    /// <summary>
    /// The two layers partition the outline exactly: the pieces of one boundary
    /// edge run end to end, in order, from the edge's own start to its own end, and
    /// nothing is drawn twice. Cutting a line in two is how a line goes missing, so
    /// this is asked before anything is asked about where the halves land.
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void The_two_layers_partition_every_segment_of_every_outline(int tileSize)
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(tileSize);
        var checkedEdges = 0;

        foreach (var room in state.Rooms)
        {
            var inset = RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rock) * scale;
            var pieces = RoomGeometry.BorderPieces(room.Perimeter, tileSize, inset, rock);

            // Every piece is in exactly one layer, and the two the adapter asks for
            // are the same pieces split by that layer and nothing else.
            var under = RoomGeometry.Border(
                room.Perimeter, tileSize, inset, rock, RoomBorderLayer.UnderBodies);
            var over = RoomGeometry.Border(
                room.Perimeter, tileSize, inset, rock, RoomBorderLayer.OverWallInFront);
            Assert.Equal(pieces.Count, under.Count + over.Count);
            Assert.Equal(
                pieces.Select(piece => piece.Segment).OrderBy(Key, StringComparer.Ordinal),
                under.Concat(over).OrderBy(Key, StringComparer.Ordinal));

            foreach (var edge in RoomGeometry.BorderEdges(room.Perimeter, tileSize, inset))
            {
                var mine = pieces
                    .Where(piece => piece.Cell == edge.Cell && piece.Side == edge.Side)
                    .ToArray();
                Assert.NotEmpty(mine);

                var horizontal = Math.Abs(edge.Segment.To.X - edge.Segment.From.X) >=
                    Math.Abs(edge.Segment.To.Y - edge.Segment.From.Y);
                var low = horizontal ? edge.Segment.From.X : edge.Segment.From.Y;
                var high = horizontal ? edge.Segment.To.X : edge.Segment.To.Y;
                var walked = low;
                foreach (var piece in mine)
                {
                    var from = horizontal ? piece.Segment.From.X : piece.Segment.From.Y;
                    var to = horizontal ? piece.Segment.To.X : piece.Segment.To.Y;
                    Assert.Equal(walked, from, 9);
                    Assert.True(to > from, $"{room.Id} {edge.Side} has an empty piece");
                    walked = to;
                }

                Assert.Equal(high, walked, 9);
                checkedEdges++;
            }
        }

        Assert.True(checkedEdges > 0, "the shipped map draws no border at all");
    }

    // ---------------------------------------------------- the owner's complaint

    /// <summary>
    /// The complaint, as a measurement: on the shipped map, at every tile size, no
    /// border stroke drawn above the depth pass lands on a body the depth pass
    /// leaves visible.
    ///
    /// <para>
    /// "Visible" is not assumed — for every overlap between a stroke and a body's
    /// drawn rectangle, the check looks for rock tiles whose own drawn bands cover
    /// that overlap whole and whose depth anchor is behind the body's, i.e. walls
    /// this very frame draws in front of that body. An overlap with no such wall is
    /// a creature with a line through it, which is the defect.
    /// </para>
    ///
    /// <para>
    /// The sweep is over positions, not over one example: every cell of the map a
    /// body can stand on, plus the midpoint of every orthogonal step between two of
    /// them, because a body's render centre is interpolated. Three points on each
    /// step is a sample rather than a proof for the whole of it;
    /// <see cref="No_body_anywhere_can_reach_a_stroke_the_wall_in_front_does_not_cover"/>
    /// is the statement that holds for every position at once.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void No_stroke_above_the_depth_pass_lands_on_a_body_that_is_visible(int tileSize)
    {
        var crossings = Measure(tileSize, Arrangement.Today);
        Assert.True(crossings.Count == 0, Payload(tileSize, Arrangement.Today, crossings));
    }

    /// <summary>
    /// The same measurement against the arrangement that shipped before this issue
    /// — the whole border drawn after the depth pass — so the "before" column of
    /// <c>evidence/156-before.json</c> stays reproducible after the arrangement is
    /// gone, and so the check above is known to be able to fail.
    ///
    /// <para>
    /// The count is pinned and not merely required to be positive. It is the number
    /// the issue and the documentation both quote, and a number quoted in prose with
    /// only <c>&gt; 0</c> behind it is a number nothing holds — independent review
    /// found exactly that here, against
    /// <c>RoomWallClearanceTests.The_pre_147_ladder…</c>, which pins its own figure.
    /// The rooms are named as well as counted, because a count alone would stay
    /// green if the defect moved to two other rooms.
    /// </para>
    ///
    /// <para>
    /// It is swept with the body of its own time as well as the draw order of its
    /// own time — see <see cref="PreIssue77BodyReferenceSize"/>. Issue #77 took
    /// bodies to 170 %, and a bigger rectangle catches more of the same lines: the
    /// same sweep with today's body returns 622. Both numbers describe the pre-#156
    /// defect and neither is wrong, but only the one measured with the body of 2026
    /// -08-01 is the number the issue's evidence quotes.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void The_border_used_to_be_drawn_over_every_body_that_stood_on_it(int tileSize)
    {
        var crossings = Measure(tileSize, Arrangement.BeforeIssue156);
        var payload = Payload(tileSize, Arrangement.BeforeIssue156, crossings);

        Assert.True(crossings.Count == 226, payload);
        Assert.Equal(
            new[] { "farm@1,1", "kitchen@9,6", "larder@13,6", "quarters@19,2" },
            crossings.Select(row => row.Room).Distinct().OrderBy(
                name => name,
                StringComparer.Ordinal));

        // The frame the owner sent, by name: the goblins on the bottom row.
        Assert.True(crossings.Any(row => row.Room == "kitchen@9,6" && row.Body == "11,8"), payload);
        Assert.True(crossings.Any(row => row.Room == "larder@13,6" && row.Body == "16,8"), payload);

        // And the creatures named are really standing there at the tick the frame
        // was taken, rather than being cells the sweep merely visited.
        var standing = PresentationFixtures.Baseline(1)
            .Creatures
            .Select(creature => $"{creature.Position.X},{creature.Position.Y}")
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("11,8", standing);
        Assert.Contains("16,8", standing);
    }

    /// <summary>
    /// The claim the sweep above samples, made once for every body position there
    /// is: a piece drawn above the depth pass sits inside the drawn bands of walls
    /// that are drawn after any body whose sprite can reach it at all.
    ///
    /// <para>
    /// The reach is arithmetic rather than a sweep. A body's sprite reaches at most
    /// half of <see cref="CameraView.GoblinDrawSize"/> below its own render centre,
    /// so the southernmost centre from which a body can touch the stroke is the
    /// stroke's lower edge plus that half — and
    /// <see cref="InFrontOfEverybodyTouching"/> keeps only walls anchored south of
    /// that point, which are therefore drawn after every one of those bodies.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void No_body_anywhere_can_reach_a_stroke_the_wall_in_front_does_not_cover(int tileSize)
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(tileSize);
        var half = RoomGeometry.BorderStrokeHalfWidth * scale;
        var walls = Walls(rock, tileSize);
        var checkedStrokes = 0;

        foreach (var (room, piece) in Pieces(state, rock, tileSize, Arrangement.Today))
        {
            if (piece.Layer != RoomBorderLayer.OverWallInFront)
            {
                continue;
            }

            var stroke = RoomGeometry.StrokeBand(piece.Segment, half);
            var covering = InFrontOfEverybodyTouching(stroke, walls, tileSize);
            Assert.True(
                covering.Count > 0 && RoomGeometry.IsCoveredBy(stroke, covering),
                $"{room} {piece.Side} at {piece.Cell.X},{piece.Cell.Y} is drawn above " +
                "the depth pass, and the walls that this frame draws in front of every " +
                "body able to touch it do not paint over the whole stroke. Part of " +
                "that line lands on whoever is standing there, which is Issue #156.");
            checkedStrokes++;
        }

        Assert.True(
            checkedStrokes > 0,
            "no piece of the shipped map is drawn above the depth pass, so this " +
            "says nothing — see A_wall_in_front_keeps_the_piece_it_swallows_above_" +
            "the_depth_pass for why there have to be some.");
    }

    // ------------------------------------------------- what the other half buys

    /// <summary>
    /// The second half, and the reason the first one cannot simply be "draw the
    /// whole border under the depth pass": a wall standing directly in front of a
    /// room paints over the bottom of the room's cell outright, so a border drawn
    /// under the depth pass loses that piece completely — the failure
    /// <see cref="RoomWallClearanceTests.A_wall_in_front_of_a_room_cannot_be_cleared_by_any_inset"/>
    /// measures as impossible to fix with any inset.
    ///
    /// <para>
    /// The shipped map has such pieces, they are drawn above the depth pass, and
    /// each of them really is painted over whole — measured against the wall's own
    /// drawn bands, and against a wall set this check picks itself rather than the
    /// row the production predicate looks at.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void A_wall_in_front_keeps_the_piece_it_swallows_above_the_depth_pass(int tileSize)
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(tileSize);
        var half = RoomGeometry.BorderStrokeHalfWidth * scale;
        var walls = Walls(rock, tileSize);
        var swallowed = new List<string>();

        foreach (var (room, piece) in Pieces(state, rock, tileSize, Arrangement.Today))
        {
            var stroke = RoomGeometry.StrokeBand(piece.Segment, half);
            var covering = InFrontOfEverybodyTouching(stroke, walls, tileSize);
            if (covering.Count == 0 || !RoomGeometry.IsCoveredBy(stroke, covering))
            {
                continue;
            }

            swallowed.Add($"{room} {piece.Side} at {piece.Cell.X},{piece.Cell.Y}");
            Assert.Equal(RoomBorderLayer.OverWallInFront, piece.Layer);
        }

        Assert.True(
            swallowed.Count > 0,
            "no piece of the shipped map is painted over whole by a wall in front " +
            "of it, so the exception this layer exists for is excusing nothing. " +
            "Either the map changed or the layer has stopped selecting anything, " +
            "and the second one is a room silently losing its south edge.");
    }

    // ------------------------------------------------------------- the corner

    /// <summary>
    /// ADR 0013 and Issue #52 bought one property above all: a room is <b>one line
    /// around the whole patch</b>, not a frame per cell. Splitting that line between
    /// two passes must not open it.
    ///
    /// <para>
    /// The corner at risk is where a horizontal edge meets a vertical one on a cell
    /// with a wall in front. The horizontal edge is swallowed whole and is drawn
    /// after the wall; the vertical edge is swallowed only along its lower few
    /// pixels. Classifying whole edges therefore put one of them above the depth
    /// pass and the other below it, and the wall cut the second one off short of
    /// the first — see
    /// <see cref="The_first_round_of_156_opened_the_corner_at_a_wall_in_front"/> for
    /// what that measured.
    /// </para>
    ///
    /// <para>
    /// What is measured here is the line as a player sees it: the lowest point the
    /// vertical stroke is still visible at, with a piece below the depth pass cut
    /// off where the wall's paint starts, against the top of the horizontal stroke
    /// it has to reach. A positive gap is a hole in the outline.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void An_outline_closes_at_the_corner_a_wall_in_front_reaches(int tileSize)
    {
        var corners = Corners(tileSize, Arrangement.Today);

        Assert.NotEmpty(corners);
        Assert.All(
            corners,
            corner => Assert.True(
                corner.GapReferencePx <= 0,
                $"{corner.Room} {corner.Side} at {corner.Cell}: the vertical stroke " +
                $"stops {corner.GapReferencePx} reference px short of the horizontal " +
                "one it meets, so the room's outline has a hole at that corner. A " +
                "room is one line around the patch, not a frame per cell (ADR 0013, " +
                "Issue #52)."));
    }

    /// <summary>
    /// The "before" column of the corner, kept reproducible the same way every other
    /// "before" in this file is: the arrangement that produced it is restated rather
    /// than remembered.
    ///
    /// The gap is <c>8.625 − (inset + 1.0)</c> reference pixels — the wall's reach
    /// above its own footprint, less where the horizontal stroke's upper edge sits.
    /// It is pinned per room because that is what makes it a finding rather than an
    /// impression: 5.0 on the quarters, 0.5 on the kitchen, and none at all on the
    /// larder, whose ladder is deep enough that the two already meet.
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void The_first_round_of_156_opened_the_corner_at_a_wall_in_front(int tileSize)
    {
        var corners = Corners(tileSize, Arrangement.FirstRoundOf156);

        Assert.Equal(
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["kitchen@9,6"] = 0.5,
                ["larder@13,6"] = -1.0,
                ["quarters@19,2"] = 5.0,
            },
            corners
                .GroupBy(corner => corner.Room, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => Math.Round(group.Max(corner => corner.GapReferencePx), 6),
                    StringComparer.Ordinal));
    }

    // ---------------------------------------------------------- adapter wiring

    /// <summary>
    /// The two ends of the wiring, read out of <c>Main.cs</c> as text for the
    /// reason <see cref="AdapterSource"/> exists: <c>DrawMap</c> hands both halves
    /// the real rock set, and each half asks for its own layer by name. Without the
    /// second one, both routines could draw the whole border — every check above
    /// would stay green while the frame went back to the defect, because the pure
    /// side would still partition correctly and nothing would read the partition.
    /// </summary>
    [Fact]
    public void The_adapter_draws_each_layer_in_the_pass_it_is_declared_in()
    {
        var map = AdapterSource.Body("DrawMap");
        foreach (var routine in new[] { "DrawRoomBorders", "DrawRoomBordersOverWalls" })
        {
            var call = Assert.Single(AdapterSource.CallsTo(map, routine));
            Assert.Equal(["rockTiles"], call.Arguments);
        }

        Assert.Contains(
            "RoomBorderLayer.UnderBodies",
            Whitespace(AdapterSource.Body("DrawRoomBorder")),
            StringComparison.Ordinal);
        Assert.Contains(
            "RoomBorderLayer.OverWallInFront",
            Whitespace(AdapterSource.Body("DrawRoomBorderOverWall")),
            StringComparison.Ordinal);

        // And the half drawn after the depth pass takes the same inset and the same
        // stroke width as the half drawn before it, so the two cannot come apart
        // into two different-looking lines around one room.
        var overWall = Whitespace(AdapterSource.Body("DrawRoomBorderOverWall"));
        Assert.Contains(
            "RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rockTiles);",
            overWall,
            StringComparison.Ordinal);
        Assert.Contains(
            AdapterSource.CallsTo(AdapterSource.Body("DrawRoomBorderOverWall"), "ScaleWorld"),
            call => call.Arguments.Any(argument =>
                argument.Contains("RoomGeometry.BorderStrokeWidth", StringComparison.Ordinal)));
        Assert.Contains(
            AdapterSource.CallsTo(AdapterSource.Body("DrawRoomBorderOverWall"), "ScaleWorld"),
            call => call.Arguments.Any(argument =>
                argument.Contains("purposeInset", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Which pieces of the shipped map the exception actually buys, by name, and the
    /// one price this issue pays — stated here rather than left in a commit message,
    /// because it is an appearance change and the next person to look at the larder
    /// deserves to find the reason rather than a suspicion.
    ///
    /// <para>
    /// The larder's two front-wall cells keep their south stroke below the depth
    /// pass, because its ladder reaches 8.625 reference pixels and one of the two
    /// pixels of that stroke is drawn above everything the wall paints. Above the
    /// depth pass that one pixel lands on the goblin standing there, which is the
    /// frame the owner sent; below it the wall clips the other pixel, and the room
    /// keeps a line half as thick along those two cells.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void The_shipped_map_pays_for_the_exception_in_two_cells_of_the_larder(int tileSize)
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(tileSize);
        var half = RoomGeometry.BorderStrokeHalfWidth * scale;
        var above = new List<string>();
        var clipped = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (room, piece) in Pieces(state, rock, tileSize, Arrangement.Today))
        {
            var name = $"{room} {piece.Side} {piece.Cell.X},{piece.Cell.Y}";
            if (piece.Layer == RoomBorderLayer.OverWallInFront)
            {
                above.Add(name);
                continue;
            }

            // How much of this stroke survives the wall standing directly in front
            // of its cell — the height of it above everything that wall paints, in
            // reference pixels. Only that wall: a seam belonging to a wall one
            // column across paints a sliver of the same stroke without hiding the
            // rest of it, and a sliver is not what this is measuring.
            var front = new GridPoint(piece.Cell.X, piece.Cell.Y + 1);
            if (!rock.Contains(front))
            {
                continue;
            }

            var stroke = RoomGeometry.StrokeBand(piece.Segment, half);
            var covering = WallRenderGeometry.DrawnBands(
                front,
                WallTopology.SelectVariant(front, rock),
                tileSize);
            var survives = Math.Round((covering.Min(band => band.Y) - stroke.Y) / scale, 6);
            if (survives < RoomGeometry.BorderStrokeWidth)
            {
                clipped[name] = survives;
            }
        }

        Assert.Equal(
            new[]
            {
                // The south strokes a wall in front swallows whole, and the tails
                // of the vertical strokes meeting them, which is what closes the
                // corner. quarters@21,5's south stroke is here for a smaller
                // reason worth naming: only the first 0.625 reference px of it,
                // the sliver inside the side seam of the wall at 20,6, which is a
                // wall of the same row and therefore also drawn after any body on
                // 21,5. The rest of that stroke is below the depth pass.
                "kitchen@9,6 East 12,8",
                "kitchen@9,6 South 12,8",
                "kitchen@9,6 South 9,8",
                "kitchen@9,6 West 9,8",
                "quarters@19,2 South 19,5",
                "quarters@19,2 South 20,5",
                "quarters@19,2 South 21,5",
                "quarters@19,2 West 19,5",
            },
            above.OrderBy(name => name, StringComparer.Ordinal));

        Assert.Equal(
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["larder@13,6 South 13,8"] = 1.0,
                ["larder@13,6 South 14,8"] = 1.0,
            },
            clipped);
    }

    // ------------------------------------------------------------- the measure

    /// <summary>
    /// Which arrangement of the border a measurement is taken against.
    /// </summary>
    private enum Arrangement
    {
        /// <summary>What the adapter draws now: one piece per run of one answer.</summary>
        Today,

        /// <summary>
        /// What shipped before Issue #156: every segment of every outline drawn after
        /// the depth pass. Restated rather than remembered, because a "before" column
        /// has to stay reproducible once the "before" is gone — the same reason
        /// <c>RoomWallClearanceTests.PreIssue147Inset</c> exists.
        /// </summary>
        BeforeIssue156,

        /// <summary>
        /// Issue #156's own first round: the same decision taken per boundary edge
        /// rather than per piece of one. It is what opened the corner at a wall in
        /// front, and it is kept for the same reason as the one above.
        /// </summary>
        FirstRoundOf156,
    }

    /// <summary>
    /// The body a measurement sweeps with, in reference pixels before
    /// <see cref="CameraView.WorldVisualScale"/>. 20 is what
    /// <see cref="CameraView.GoblinDrawSize"/> was built from before Issue #77 took
    /// bodies to <see cref="CameraView.BodyVisualScale"/> — 170 % — and a "before"
    /// column is measured with the body of its own time, exactly as
    /// <see cref="Arrangement.BeforeIssue156"/> is measured with the draw order of
    /// its own time. Measured with today's body the same sweep returns 622 rather
    /// than the 226 the issue, the documentation and <c>evidence/156-before.json</c>
    /// all quote, and that number would then be a moving target for every future
    /// change of visual scale.
    /// </summary>
    private const double PreIssue77BodyReferenceSize = 20.0;

    /// <summary>
    /// The drawn body an arrangement is swept with: today's for today, and the one
    /// of its own time for a historical column.
    /// </summary>
    private static double BodyDrawSize(int tileSize, Arrangement arrangement) =>
        arrangement == Arrangement.Today
            ? CameraView.GoblinDrawSize(tileSize)
            : PreIssue77BodyReferenceSize * CameraView.WorldVisualScale(tileSize);

    /// <summary>
    /// The rectangle a body's sprite occupies at <paramref name="centre"/>. Today's
    /// comes from production geometry rather than a copy of it; a historical column
    /// is the square that geometry drew when the body was
    /// <see cref="PreIssue77BodyReferenceSize"/> — the same rule, because the foot
    /// line Issue #77 grows a body out of is half of that very body.
    /// </summary>
    private static ViewRect BodyRect(ViewPoint centre, int tileSize, Arrangement arrangement) =>
        arrangement == Arrangement.Today
            ? CameraView.GoblinDrawRect(centre, tileSize)
            : Square(centre, BodyDrawSize(tileSize, arrangement));

    /// <param name="Room">The room whose outline this stroke belongs to.</param>
    /// <param name="Side">Which side of its cell the stroke faces.</param>
    /// <param name="Cell">The room cell the stroke was drawn for.</param>
    /// <param name="Body">The cell a body stands on, or the step it is mid-way through.</param>
    /// <param name="OverlapPx">How much of the body's rectangle the stroke covers.</param>
    private sealed record Crossing(
        string Room,
        string Side,
        string Cell,
        string Body,
        double OverlapPx);

    /// <summary>Every room's outline as the pieces one arrangement draws.</summary>
    private static IReadOnlyList<(string Room, RoomBorderPiece Piece)> Pieces(
        PrototypeSnapshot state,
        IReadOnlySet<GridPoint> rock,
        int tileSize,
        Arrangement arrangement)
    {
        var scale = CameraView.WorldVisualScale(tileSize);
        var half = RoomGeometry.BorderStrokeHalfWidth * scale;
        var pieces = new List<(string, RoomBorderPiece)>();
        foreach (var room in state.Rooms)
        {
            var inset = RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rock) * scale;
            if (arrangement == Arrangement.Today)
            {
                foreach (var piece in RoomGeometry.BorderPieces(
                             room.Perimeter,
                             tileSize,
                             inset,
                             rock))
                {
                    pieces.Add((room.Id, piece));
                }

                continue;
            }

            foreach (var edge in RoomGeometry.BorderEdges(room.Perimeter, tileSize, inset))
            {
                var stroke = RoomGeometry.StrokeBand(edge.Segment, half);
                var layer = arrangement == Arrangement.BeforeIssue156
                    ? RoomBorderLayer.OverWallInFront
                    : RoomGeometry.LayerOf(
                        stroke,
                        RoomGeometry.WallBandsInFrontOf(edge.Cell, stroke, rock, tileSize));
                pieces.Add((
                    room.Id,
                    new RoomBorderPiece(edge.Cell, edge.Side, edge.Segment, layer)));
            }
        }

        return pieces;
    }

    /// <summary>
    /// Every place a stroke drawn above the depth pass lands on a body that no wall
    /// is drawing in front of.
    /// </summary>
    private static IReadOnlyList<Crossing> Measure(int tileSize, Arrangement arrangement)
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(tileSize);
        var half = RoomGeometry.BorderStrokeHalfWidth * scale;
        var bodies = BodyPositions(rock, tileSize, arrangement);
        var walls = Walls(rock, tileSize);

        var crossings = new List<Crossing>();
        foreach (var (room, piece) in Pieces(state, rock, tileSize, arrangement))
        {
            if (piece.Layer != RoomBorderLayer.OverWallInFront)
            {
                continue;
            }

            var stroke = RoomGeometry.StrokeBand(piece.Segment, half);
            foreach (var body in bodies)
            {
                if (Overlap(stroke, body.Rect) is not { } overlap)
                {
                    continue;
                }

                // Walls drawn after this body, painting over the whole overlap
                // between them, are the depth pass hiding the body there — the one
                // legitimate reason a mark may sit on top of a sprite. Together,
                // not one at a time: a wall's lifted top and the bright seam along
                // it are two rectangles and a stroke can straddle their boundary.
                var inFront = walls
                    .Where(wall => wall.Anchor > body.AnchorY)
                    .SelectMany(wall => wall.Bands)
                    .Where(band => Overlap(band, overlap) is not null)
                    .ToArray();
                if (!RoomGeometry.IsCoveredBy(overlap, inFront))
                {
                    crossings.Add(new Crossing(
                        room,
                        piece.Side.ToString(),
                        $"{piece.Cell.X},{piece.Cell.Y}",
                        body.Name,
                        Math.Round(overlap.Width * overlap.Height, 6)));
                }
            }
        }

        return crossings
            .OrderBy(row => row.Room, StringComparer.Ordinal)
            .ThenBy(row => row.Cell, StringComparer.Ordinal)
            .ThenBy(row => row.Side, StringComparer.Ordinal)
            .ThenBy(row => row.Body, StringComparer.Ordinal)
            .ToArray();
    }

    /// <param name="Room">The room whose outline this corner belongs to.</param>
    /// <param name="Cell">The cell with a wall in front of it.</param>
    /// <param name="Side">The vertical side whose stroke has to reach the horizontal one.</param>
    /// <param name="GapReferencePx">
    /// How far short of the horizontal stroke the vertical one stops, in reference
    /// pixels. Zero or less means they meet.
    /// </param>
    private sealed record Corner(string Room, string Cell, string Side, double GapReferencePx);

    /// <summary>
    /// Every corner where a wall in front can open the outline, and by how much.
    ///
    /// <para>
    /// The vertical stroke is followed piece by piece. A piece above the depth pass
    /// is drawn whole; a piece below it is cut off where the wall in front starts
    /// painting, because that is what the frame does to it. The lowest point still
    /// visible is compared with the top of the horizontal stroke of the same cell —
    /// the one the corner turns into.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Corner> Corners(int tileSize, Arrangement arrangement)
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(tileSize);
        var half = RoomGeometry.BorderStrokeHalfWidth * scale;
        var pieces = Pieces(state, rock, tileSize, arrangement);
        var corners = new List<Corner>();

        foreach (var group in pieces.GroupBy(item => (item.Room, item.Piece.Cell)))
        {
            var (room, cell) = group.Key;
            var front = new GridPoint(cell.X, cell.Y + 1);
            if (!rock.Contains(front))
            {
                continue;
            }

            var south = group
                .Where(item => item.Piece.Side == WallNeighbors.South)
                .Select(item => RoomGeometry.StrokeBand(item.Piece.Segment, half))
                .ToArray();
            if (south.Length == 0)
            {
                continue;
            }

            // Where the wall in front starts painting: above this the vertical
            // stroke survives, below it the wall covers the whole of the column.
            var wallTop = WallRenderGeometry
                .DrawnBands(front, WallTopology.SelectVariant(front, rock), tileSize)
                .Min(band => band.Y);
            var target = south.Min(band => band.Y);

            foreach (var side in new[] { WallNeighbors.West, WallNeighbors.East })
            {
                var vertical = group
                    .Where(item => item.Piece.Side == side)
                    .ToArray();
                if (vertical.Length == 0)
                {
                    continue;
                }

                var visible = vertical.Max(item =>
                {
                    var stroke = RoomGeometry.StrokeBand(item.Piece.Segment, half);
                    var bottom = stroke.Y + stroke.Height;
                    return item.Piece.Layer == RoomBorderLayer.OverWallInFront
                        ? bottom
                        : Math.Max(stroke.Y, Math.Min(bottom, wallTop));
                });

                corners.Add(new Corner(
                    room,
                    $"{cell.X},{cell.Y}",
                    side.ToString(),
                    Math.Round((target - visible) / scale, 6)));
            }
        }

        return corners;
    }

    /// <param name="Anchor">The Y the depth pass sorts this wall by.</param>
    /// <param name="Bands">Every rectangle of screen it actually paints.</param>
    private sealed record Wall(double Anchor, IReadOnlyList<ViewRect> Bands);

    private static IReadOnlyList<Wall> Walls(IReadOnlySet<GridPoint> rock, int tileSize) =>
        rock
            .Select(cell => new Wall(
                CameraView.CellTopLeft(cell, tileSize).Y + tileSize,
                WallRenderGeometry.DrawnBands(
                    cell,
                    WallTopology.SelectVariant(cell, rock),
                    tileSize)))
            .ToArray();

    /// <summary>
    /// The bands of every wall this frame draws in front of <em>every</em> body
    /// that could touch <paramref name="stroke"/> — not just of the bodies that
    /// happen to be standing somewhere today.
    ///
    /// A body's sprite reaches half of <see cref="CameraView.GoblinDrawSize"/>
    /// below its own render centre — <see cref="CameraView.GoblinDrawRect"/> is a
    /// square centred on that point, at 170 % as before it — so the southernmost
    /// centre from which anybody can touch the stroke is its lower edge plus that
    /// half. A wall anchored south of that point is drawn after every one of those
    /// bodies, whatever they are doing and wherever between two cells the
    /// interpolation has them.
    /// </summary>
    private static IReadOnlyList<ViewRect> InFrontOfEverybodyTouching(
        ViewRect stroke,
        IReadOnlyList<Wall> walls,
        int tileSize)
    {
        var southernmost = stroke.Y + stroke.Height + (CameraView.GoblinDrawSize(tileSize) / 2.0);
        return walls
            .Where(wall => wall.Anchor > southernmost)
            .SelectMany(wall => wall.Bands)
            .Where(band => Overlap(band, stroke) is not null)
            .ToArray();
    }

    /// <param name="Name">The cell, or the step, this position is.</param>
    /// <param name="Rect">The rectangle the sprite is drawn into.</param>
    /// <param name="AnchorY">The Y the depth pass sorts this body by.</param>
    private sealed record BodyPosition(string Name, ViewRect Rect, double AnchorY);

    /// <summary>
    /// Where a body can be. Every cell that is not rock, because rock is the only
    /// impassable thing on this map, plus the midpoint of every orthogonal step
    /// between two of them: a body's render centre is interpolated, so it spends
    /// most of its life between two cells rather than on one.
    ///
    /// The rectangle comes from production geometry for today's arrangement —
    /// <see cref="CameraView.GoblinDrawRect"/>, which is what <c>Main.DrawGoblin</c>
    /// draws the sprite into and is wider than the coloured disc underneath it —
    /// and from the body of its own time for a historical column
    /// (<see cref="BodyRect"/>). Using the drawn rectangle rather than the sprite's
    /// opaque pixels overstates the body, which is the safe direction for a check
    /// that has to find lines crossing it.
    /// </summary>
    private static IReadOnlyList<BodyPosition> BodyPositions(
        IReadOnlySet<GridPoint> rock,
        int tileSize,
        Arrangement arrangement)
    {
        var positions = new List<BodyPosition>();
        var steps = new[] { new GridPoint(1, 0), new GridPoint(0, 1) };
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                var cell = new GridPoint(x, y);
                if (rock.Contains(cell))
                {
                    continue;
                }

                var centre = CameraView.CellCenter(cell, tileSize);
                positions.Add(new BodyPosition(
                    $"{x},{y}",
                    BodyRect(centre, tileSize, arrangement),
                    centre.Y));
                foreach (var step in steps)
                {
                    var next = new GridPoint(x + step.X, y + step.Y);
                    if (next.X >= PrototypeTuning.MapWidth ||
                        next.Y >= PrototypeTuning.MapHeight ||
                        rock.Contains(next))
                    {
                        continue;
                    }

                    var far = CameraView.CellCenter(next, tileSize);
                    var middle = new ViewPoint((centre.X + far.X) / 2.0, (centre.Y + far.Y) / 2.0);
                    positions.Add(new BodyPosition(
                        $"{x},{y}->{next.X},{next.Y}",
                        BodyRect(middle, tileSize, arrangement),
                        middle.Y));
                }
            }
        }

        return positions;
    }

    private static ViewRect Square(ViewPoint centre, double size) =>
        new(centre.X - (size / 2.0), centre.Y - (size / 2.0), size, size);

    private static ViewRect? Overlap(ViewRect first, ViewRect second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        return right > left + Tolerance && bottom > top + Tolerance
            ? new ViewRect(left, top, right - left, bottom - top)
            : null;
    }

    private const double Tolerance = 1e-9;

    private static string Key(ViewSegment segment) =>
        $"{segment.From.X:F6},{segment.From.Y:F6}->{segment.To.X:F6},{segment.To.Y:F6}";

    private static string Whitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Payload(
        int tileSize,
        Arrangement arrangement,
        IReadOnlyList<Crossing> crossings) =>
        JsonSerializer.Serialize(
            new
            {
                tileSize,
                arrangement = arrangement.ToString(),
                scale = CameraView.WorldVisualScale(tileSize),
                bodyDrawSize = BodyDrawSize(tileSize, arrangement),
                crossingCount = crossings.Count,
                rooms = crossings.Select(row => row.Room).Distinct().OrderBy(
                    name => name,
                    StringComparer.Ordinal),
                crossings,
            },
            new JsonSerializerOptions { WriteIndented = true });
}
