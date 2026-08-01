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
/// <item><b>except</b> the segments a wall standing directly in front of the room
/// paints over completely, which stay above the depth pass because the wall would
/// otherwise erase them outright and no inset buys them back (Issues #139, #147) —
/// <see cref="A_wall_in_front_keeps_the_segment_it_swallows_above_the_depth_pass"/>
/// is what fails if <em>that</em> is undone.</item>
/// </list>
///
/// <para>
/// Both halves are one predicate, <see cref="RoomGeometry.IsHiddenByWallInFront"/>,
/// so each half has a one-line mutant: hardwire it <c>true</c> and the first check
/// reddens, hardwire it <c>false</c> and the second does.
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
                $"'{name}' is declared {routine.Pass}. The segment a wall in front " +
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
    /// The two layers partition the outline: every segment is drawn in exactly one
    /// pass, none twice and none not at all. Splitting a line in two is how a line
    /// goes missing, so this is asked before anything is asked about where the
    /// halves land.
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void The_two_layers_partition_every_segment_of_every_outline(int tileSize)
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(tileSize);
        var total = 0;
        foreach (var room in state.Rooms)
        {
            var inset = RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rock) * scale;
            var all = RoomGeometry.BorderEdges(room.Perimeter, tileSize, inset)
                .Select(edge => edge.Segment)
                .ToArray();
            var under = RoomGeometry.Border(
                room.Perimeter, tileSize, inset, rock, RoomBorderLayer.UnderBodies);
            var over = RoomGeometry.Border(
                room.Perimeter, tileSize, inset, rock, RoomBorderLayer.OverWallInFront);

            Assert.Equal(all.Length, under.Count + over.Count);
            Assert.Equal(
                all.OrderBy(Key, StringComparer.Ordinal),
                under.Concat(over).OrderBy(Key, StringComparer.Ordinal));
            total += all.Length;
        }

        Assert.True(total > 0, "the shipped map draws no border at all");
    }

    // ---------------------------------------------------- the owner's complaint

    /// <summary>
    /// The complaint, as a measurement: on the shipped map, at every tile size, no
    /// border stroke drawn above the depth pass lands on a body the depth pass
    /// leaves visible.
    ///
    /// <para>
    /// "Visible" is not assumed — for every overlap between a stroke and a body's
    /// drawn rectangle, the check looks for a rock tile whose own drawn bands cover
    /// that overlap whole and whose depth anchor is behind the body's, i.e. a wall
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
        var crossings = Measure(tileSize, RoomGeometry.LayerOf);
        Assert.True(crossings.Count == 0, Payload(tileSize, crossings));
    }

    /// <summary>
    /// The same measurement against the arrangement that shipped before this issue
    /// — the whole border drawn after the depth pass — so the "before" column of
    /// <c>evidence/156-before.json</c> stays reproducible after the arrangement is
    /// gone, and so the check above is known to be able to fail.
    ///
    /// <para>
    /// What it pins is the frame the owner sent: the goblins on the bottom row of
    /// the kitchen and the larder, each with its own room's line across it. Both
    /// rooms are named here rather than counted, because a count would stay green
    /// if the defect moved to two other rooms.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void The_border_used_to_be_drawn_over_every_body_that_stood_on_it(int tileSize)
    {
        var crossings = Measure(tileSize, PreIssue156Layer);
        var payload = Payload(tileSize, crossings);

        Assert.True(crossings.Count > 0, payload);
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
    /// is: a stroke drawn above the depth pass sits inside a single drawn band of
    /// the wall directly in front of its own cell, and that wall is drawn after any
    /// body whose sprite can reach the stroke at all.
    ///
    /// <para>
    /// The second half is arithmetic rather than a sweep. A body's sprite reaches
    /// at most half of <see cref="CameraView.GoblinDrawSize"/> below its own render
    /// centre, so the southernmost centre from which a body can touch the stroke is
    /// the stroke's lower edge plus that half — and every such centre has to be
    /// north of the wall's depth anchor, which is the bottom of the wall's own
    /// footprint. North of it means drawn before it, which means covered by it.
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

        foreach (var (room, edge) in Edges(state, rock, tileSize))
        {
            if (RoomGeometry.LayerOf(edge, rock, tileSize) != RoomBorderLayer.OverWallInFront)
            {
                continue;
            }

            var stroke = RoomGeometry.StrokeBand(edge.Segment, half);
            var covering = InFrontOfEverybodyTouching(stroke, walls, tileSize);
            Assert.True(
                covering.Count > 0 && RoomGeometry.IsCoveredBy(stroke, covering),
                $"{room} {edge.Side} at {edge.Cell.X},{edge.Cell.Y} is drawn above the " +
                "depth pass, and the walls that this frame draws in front of every " +
                "body able to touch it do not paint over the whole stroke. Part of " +
                "that line lands on whoever is standing there, which is Issue #156.");
            checkedStrokes++;
        }

        Assert.True(
            checkedStrokes > 0,
            "no segment of the shipped map is drawn above the depth pass, so this " +
            "says nothing — see A_wall_in_front_keeps_the_segment_it_swallows_above_" +
            "the_depth_pass for why there have to be some.");
    }

    // ------------------------------------------------- what the other half buys

    /// <summary>
    /// The second half, and the reason the first one cannot simply be "draw the
    /// whole border under the depth pass": a wall standing directly in front of a
    /// room paints over the bottom of the room's cell outright, so a border drawn
    /// under the depth pass loses that segment completely — the failure
    /// <see cref="RoomWallClearanceTests.A_wall_in_front_of_a_room_cannot_be_cleared_by_any_inset"/>
    /// measures as impossible to fix with any inset.
    ///
    /// <para>
    /// The shipped map has such segments, they are drawn above the depth pass, and
    /// each of them really is painted over whole — measured against the wall's own
    /// drawn bands, so "erased" is a fact about the frame and not a worry.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void A_wall_in_front_keeps_the_segment_it_swallows_above_the_depth_pass(int tileSize)
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(tileSize);
        var half = RoomGeometry.BorderStrokeHalfWidth * scale;
        var walls = Walls(rock, tileSize);
        var swallowed = new List<string>();

        foreach (var (room, edge) in Edges(state, rock, tileSize))
        {
            var stroke = RoomGeometry.StrokeBand(edge.Segment, half);
            var covering = InFrontOfEverybodyTouching(stroke, walls, tileSize);
            if (covering.Count == 0 || !RoomGeometry.IsCoveredBy(stroke, covering))
            {
                continue;
            }

            swallowed.Add($"{room} {edge.Side} at {edge.Cell.X},{edge.Cell.Y}");
            Assert.Equal(
                RoomBorderLayer.OverWallInFront,
                RoomGeometry.LayerOf(edge, rock, tileSize));
        }

        Assert.True(
            swallowed.Count > 0,
            "no segment of the shipped map is painted over whole by a wall in front " +
            "of it, so the exception this layer exists for is excusing nothing. " +
            "Either the map changed or the layer has stopped selecting anything, " +
            "and the second one is a room silently losing its south edge.");
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
    /// Which segments of the shipped map the exception actually buys, by name, and
    /// the one price this issue pays — stated here rather than left in a commit
    /// message, because it is an appearance change and the next person to look at
    /// the larder deserves to find the reason rather than a suspicion.
    ///
    /// <para>
    /// Four segments of the shipped map are drawn above the depth pass: the
    /// kitchen's and the quarters' south edges where a wall stands in front of
    /// them. The larder's two are <em>not</em>, and that is the price: its ladder
    /// reaches 8.625 reference pixels, deep enough that one of the two reference
    /// pixels of its stroke is drawn above everything the wall paints. Drawn after
    /// the depth pass that one pixel would land on the goblin standing there, which
    /// is the frame the owner sent; drawn before it, the wall clips the other
    /// pixel and the room keeps a line half as thick along those two cells.
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
        var walls = Walls(rock, tileSize);
        var above = new List<string>();
        var clipped = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (room, edge) in Edges(state, rock, tileSize))
        {
            var name = $"{room} {edge.Side} {edge.Cell.X},{edge.Cell.Y}";
            if (RoomGeometry.LayerOf(edge, rock, tileSize) == RoomBorderLayer.OverWallInFront)
            {
                above.Add(name);
                continue;
            }

            // How much of this stroke survives the wall standing directly in front
            // of its cell — the height of it above everything that wall paints, in
            // reference pixels. Only that wall: a seam belonging to a wall one
            // column across paints a sliver of the same stroke without hiding the
            // rest of it, and a sliver is not what this is measuring.
            var front = new GridPoint(edge.Cell.X, edge.Cell.Y + 1);
            if (!rock.Contains(front))
            {
                continue;
            }

            var stroke = RoomGeometry.StrokeBand(edge.Segment, half);
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
                "kitchen@9,6 South 12,8",
                "kitchen@9,6 South 9,8",
                "quarters@19,2 South 19,5",
                "quarters@19,2 South 20,5",
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

    /// <summary>
    /// The arrangement this issue replaced: every segment of every outline drawn
    /// after the depth pass, which is what <c>WorldDrawOrder</c> declared until
    /// Issue #156. Restated here because a "before" column has to stay reproducible
    /// once the "before" is gone — the same reason
    /// <c>RoomWallClearanceTests.PreIssue147Inset</c> exists.
    /// </summary>
    private static RoomBorderLayer PreIssue156Layer(
        RoomBorderEdge edge,
        IReadOnlySet<GridPoint> wallTiles,
        int tileSize) =>
        RoomBorderLayer.OverWallInFront;

    /// <summary>
    /// Every place a stroke drawn above the depth pass lands on a body that no wall
    /// is drawing in front of.
    /// </summary>
    private static IReadOnlyList<Crossing> Measure(
        int tileSize,
        Func<RoomBorderEdge, IReadOnlySet<GridPoint>, int, RoomBorderLayer> layer)
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(tileSize);
        var half = RoomGeometry.BorderStrokeHalfWidth * scale;
        var bodies = BodyPositions(rock, tileSize);
        var walls = Walls(rock, tileSize);

        var crossings = new List<Crossing>();
        foreach (var (room, edge) in Edges(state, rock, tileSize))
        {
            if (layer(edge, rock, tileSize) != RoomBorderLayer.OverWallInFront)
            {
                continue;
            }

            var stroke = RoomGeometry.StrokeBand(edge.Segment, half);
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
                        edge.Side.ToString(),
                        $"{edge.Cell.X},{edge.Cell.Y}",
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
    /// below its own render centre, so the southernmost centre from which anybody
    /// can touch the stroke is its lower edge plus that half. A wall anchored south
    /// of that point is drawn after every one of those bodies, whatever they are
    /// doing and wherever between two cells the interpolation has them.
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

    /// <summary>Every room's outline, with the room's id kept alongside.</summary>
    private static IEnumerable<(string Room, RoomBorderEdge Edge)> Edges(
        PrototypeSnapshot state,
        IReadOnlySet<GridPoint> rock,
        int tileSize)
    {
        var scale = CameraView.WorldVisualScale(tileSize);
        foreach (var room in state.Rooms)
        {
            var inset = RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rock) * scale;
            foreach (var edge in RoomGeometry.BorderEdges(room.Perimeter, tileSize, inset))
            {
                yield return (room.Id, edge);
            }
        }
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
    /// The rectangle is <see cref="CameraView.GoblinDrawSize"/> square, which is
    /// what <c>Main.DrawGoblin</c> draws the sprite into and is wider than the
    /// coloured disc underneath it. Using the drawn rectangle rather than the
    /// sprite's opaque pixels overstates the body, which is the safe direction for
    /// a check that has to find lines crossing it.
    /// </summary>
    private static IReadOnlyList<BodyPosition> BodyPositions(
        IReadOnlySet<GridPoint> rock,
        int tileSize)
    {
        var size = CameraView.GoblinDrawSize(tileSize);
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
                positions.Add(new BodyPosition($"{x},{y}", Square(centre, size), centre.Y));
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
                        Square(middle, size),
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

    private static string Payload(int tileSize, IReadOnlyList<Crossing> crossings) =>
        JsonSerializer.Serialize(
            new
            {
                tileSize,
                scale = CameraView.WorldVisualScale(tileSize),
                bodyDrawSize = CameraView.GoblinDrawSize(tileSize),
                crossingCount = crossings.Count,
                rooms = crossings.Select(row => row.Room).Distinct().OrderBy(
                    name => name,
                    StringComparer.Ordinal),
                crossings,
            },
            new JsonSerializerOptions { WriteIndented = true });
}
