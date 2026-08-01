using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #147: a room's border must not be drawn inside the pixels a wall next
/// to it already occupies — on any side, for any room of the shipped map, at any
/// tile size ADR 0008 allows.
///
/// <para>
/// Issue #139 asked the same question of one side and one mechanism: a facade
/// hanging south over the room below it. The owner's complaint had named two
/// rooms, and <c>quarters@19,2</c> has no wall to its north at all — so half the
/// complaint was never in that issue's reach. What it had instead was a wall to
/// its west and one to its east, whose dark side seams
/// <c>Main.DrawWall</c> centres exactly on the boundary the two cells share, so
/// half of <see cref="WallRenderGeometry.EdgeReferenceWidth"/> is painted inside
/// the room's own cell. The plain ladder left the border 0.375 reference pixels
/// clear of that — 0.55 screen pixels at the smallest tile size, which is a
/// gap two antialiased strokes close on their own.
/// </para>
///
/// <para>
/// So this does not measure a mechanism. It measures the wall as drawn —
/// <see cref="WallRenderGeometry.DrawnBands"/>, every rectangle the adapter puts
/// on screen for a rock tile, seams widened into the bands <c>DrawLine</c>
/// actually paints — against the border as drawn, and it does it for every edge
/// of every room. A mechanism nobody has thought of yet fails it the same way a
/// named one does.
/// </para>
/// </summary>
public sealed class RoomWallClearanceTests
{
    /// <summary>
    /// The ends of ADR 0008's range and the default in the middle. Every quantity
    /// involved is a reference-pixel number multiplied by one scale, so the three
    /// answers agree once converted — but the smallest tile is where the gap is
    /// physically smallest in the pixels a player sees, and Issue #147's own
    /// acceptance criterion asks for the ends and not only the default.
    /// </summary>
    public static TheoryData<int> TileSizes => new()
    {
        CameraView.MinimumTileSize,
        CameraView.DefaultTileSize,
        CameraView.MaximumTileSize,
    };

    /// <summary>
    /// The closest a room's border comes to a wall on one side of it.
    /// </summary>
    /// <param name="Room">The room's published id.</param>
    /// <param name="Purpose">Its purpose, which picks the rung of the ladder.</param>
    /// <param name="Side">Which side of the cell this edge faces.</param>
    /// <param name="Inset">The reference-pixel inset the border was drawn at.</param>
    /// <param name="Cell">The room cell whose edge this is.</param>
    /// <param name="Wall">The rock tile whose drawn band comes closest.</param>
    /// <param name="ScreenPx">The gap in screen pixels at this tile size.</param>
    /// <param name="ReferencePx">The same gap in reference pixels.</param>
    private sealed record Clearance(
        string Room,
        string Purpose,
        string Side,
        double Inset,
        string Cell,
        string Wall,
        double ScreenPx,
        double ReferencePx);

    /// <summary>
    /// The check itself. No border stroke of any room is drawn closer to any wall
    /// band than <see cref="RoomGeometry.WallVisibleGap"/>, on every side, at
    /// every tile size — with one declared exception, whose legitimacy
    /// <see cref="A_wall_in_front_of_a_room_cannot_be_cleared_by_any_inset"/>
    /// establishes rather than assumes.
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void The_border_of_every_room_clears_every_wall_band_on_every_side(int tileSize)
    {
        var measured = Measure(tileSize, WallAwareInset);
        var required = RoomGeometry.WallVisibleGap * CameraView.WorldVisualScale(tileSize);
        var violations = measured
            .Where(row => row.ScreenPx < required - Tolerance)
            .ToArray();

        Assert.True(violations.Length == 0, Payload(tileSize, required, measured, violations));
    }

    /// <summary>
    /// The same measurement against the inset policy that shipped before this
    /// issue — Issue #139's north-only ladder — so the check is known to be able
    /// to fail, and so the "before" column of the evidence is produced by a
    /// command that stays runnable rather than by a number typed into a file.
    ///
    /// <para>
    /// What it pins is the finding Issue #147 was opened on: under that policy
    /// <c>quarters@19,2</c> is the tightest room on the map on both of its walled
    /// sides, at 0.375 reference pixels — tighter than any of the rooms #139
    /// itself moved, which is why the owner kept seeing it after #139 shipped.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void The_pre_147_ladder_really_did_draw_the_border_inside_the_wall(int tileSize)
    {
        var measured = Measure(tileSize, PreIssue147Inset);
        var required = RoomGeometry.WallVisibleGap * CameraView.WorldVisualScale(tileSize);
        var violations = measured
            .Where(row => row.ScreenPx < required - Tolerance)
            .ToArray();
        var payload = Payload(tileSize, required, measured, violations);

        Assert.True(violations.Length > 0, payload);
        Assert.True(
            violations.Any(row => row.Room == "quarters@19,2" && row.Side == "West"),
            payload);
        Assert.True(
            violations.Any(row => row.Room == "quarters@19,2" && row.Side == "East"),
            payload);

        // 0.375 reference px: the wall's own half-seam (0.625) plus the border's
        // half-stroke (1.0) subtracted from the plain ladder's 2.0. Quarters is
        // the shallowest rung, so it is the whole of the margin the owner saw.
        var quarters = violations
            .Where(row => row.Room == "quarters@19,2")
            .Select(row => Math.Round(row.ReferencePx, 6))
            .Distinct()
            .ToArray();
        Assert.Equal([0.375], quarters);
    }

    /// <summary>
    /// The declared exception, and why it is one rather than an omission.
    ///
    /// <para>
    /// A wall standing directly south of a room cell is drawn <em>in front of</em>
    /// it: <see cref="WallRenderGeometry"/> lifts a wall's top mass
    /// <see cref="WallRenderGeometry.FacadeReferenceHeight"/> reference pixels
    /// above its own footprint, so it covers the bottom of the cell to its north
    /// outright — not a seam's worth, the whole band, and the bright seam along
    /// the top of that mass reaches half its own width higher still. Clearing
    /// that would need an inset past <see cref="RoomGeometry.MaximumBorderInset"/>,
    /// at which point the stroke bands of the two opposite sides of a one-cell
    /// room meet and there is no border left to draw. This measures that
    /// impossibility instead of asserting it.
    /// </para>
    ///
    /// <para>
    /// The answer the project already gave is not geometry but draw order, taken
    /// in Issue #83 and written down in <see cref="WorldDrawOrder"/>: the segment
    /// a wall in front would swallow is drawn after the depth pass, so «zone
    /// borders remain complete instead of losing their south edge under a wall».
    /// The second half of this test is that the declaration still says so — an
    /// exception excused by a policy is only excused while the policy holds.
    /// </para>
    ///
    /// <para>
    /// Issue #156 narrowed which routine carries that policy without weakening it.
    /// The whole border used to be drawn after the depth pass, and the price was
    /// paid by every creature standing on a line anywhere on the map; now
    /// <c>DrawRoomBordersOverWalls</c> draws exactly the segments this exception is
    /// about, and <see cref="RoomBorderDepthTests"/> is what holds the split.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TileSizes))]
    public void A_wall_in_front_of_a_room_cannot_be_cleared_by_any_inset(int tileSize)
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.RockTiles.ToHashSet();
        var inFront = state.Rooms
            .SelectMany(room => room.Perimeter)
            .Count(cell => rock.Contains(new GridPoint(cell.X, cell.Y + 1)));

        // The map really does have rooms with a wall in front of them, or the
        // exception would be excusing nothing and this test would be decoration.
        Assert.True(inFront > 0, "no room of the shipped map has a wall to its south");

        // What clearing it would cost, in the same reference pixels the ladder is
        // measured in: the whole lifted mass, the upper half of the bright seam
        // drawn along its top, the border's own half-stroke and a visible gap. It
        // lands past the ceiling — the base alone would eat more than half the
        // cell — and the ladder built on that base reaches half again as far,
        // because every purpose still climbs its own step above it.
        //
        // The half-seam term is the same one Issue #139 left out of the north
        // clearance and Issue #147 put back: a wall's mass is not the rectangle,
        // it is the rectangle plus the bands its seams are painted as.
        var neededBase =
            WallRenderGeometry.FacadeReferenceHeight +
            (WallRenderGeometry.EdgeReferenceWidth / 2.0) +
            RoomGeometry.BorderStrokeHalfWidth +
            RoomGeometry.WallVisibleGap;
        var widestStep = Enum.GetValues<ZoneKind>()
            .Max(purpose => RoomGeometry.BorderInset(purpose) - RoomGeometry.PlainBorderBase);
        Assert.True(
            neededBase > RoomGeometry.MaximumBorderInset,
            $"a wall in front would be cleared by a base inset of {neededBase} reference px, " +
            $"which is inside the {RoomGeometry.MaximumBorderInset} ceiling — the exception " +
            "below is no longer necessary and the border should simply clear it");
        Assert.True(
            neededBase + widestStep > RoomGeometry.MaximumBorderInset,
            $"the deepest rung of a ladder rooted at {neededBase} reaches " +
            $"{neededBase + widestStep} reference px of a {RoomGeometry.ReferenceCell} " +
            "reference-px cell, which is inside the ceiling after all");

        // And the policy that stands in for the inset is still declared.
        var borders = WorldDrawOrder.Find("DrawRoomBordersOverWalls");
        Assert.NotNull(borders);
        Assert.True(
            borders!.Pass > WorldDrawPass.Depth,
            "the segment a wall in front swallows is no longer drawn after the depth " +
            "pass, so a wall in front of a room now erases its edge instead of " +
            "merely standing over it");

        // The measurement is real at this tile size too: without the exception the
        // sweep above would report the front walls and nothing else.
        var excused = Measure(tileSize, WallAwareInset, excuseWallsInFront: false)
            .Where(row => row.ScreenPx <
                RoomGeometry.WallVisibleGap * CameraView.WorldVisualScale(tileSize) - Tolerance)
            .ToArray();
        Assert.All(
            excused,
            row => Assert.True(
                row.Side is "South" or "West" or "East",
                $"{row.Room} {row.Side} at {row.Cell} is reported against the wall in " +
                "front of it, which is not a side a wall in front can reach"));
    }

    /// <summary>
    /// A wall's drawn band is wider than its line. This is the difference the
    /// whole issue turns on, so it is measured directly rather than trusted:
    /// the dark side seam of a wall with an exposed east side is centred on the
    /// wall's own right-hand boundary, and the band it is painted as therefore
    /// reaches half a stroke into the next cell along.
    /// </summary>
    [Fact]
    public void A_walls_side_seam_paints_half_its_width_into_the_next_cell()
    {
        const int tile = CameraView.DefaultTileSize;
        var scale = CameraView.WorldVisualScale(tile);
        var cell = new GridPoint(3, 3);
        var boundary = CameraView.CellTopLeft(cell, tile).X + tile;

        var bands = WallRenderGeometry.DrawnBands(cell, WallTileVariant.Isolated, tile);
        var reach = bands.Max(band => band.X + band.Width) - boundary;

        Assert.Equal(
            WallRenderGeometry.EdgeReferenceWidth / 2.0 * scale,
            reach,
            10);
    }

    /// <summary>
    /// The same measurement across the other axis, and the one Issue #147
    /// actually turns on: how far below its own footprint a wall paints.
    ///
    /// <para>
    /// The facade rectangle overhangs
    /// <see cref="WallRenderGeometry.FacadeReferenceOverhang"/>, and the seam
    /// that closes it off is drawn <em>on</em> that lower edge, so half of
    /// <see cref="WallRenderGeometry.EdgeReferenceWidth"/> lands below the
    /// rectangle. Issue #139 cleared the rectangle and stopped there, which is
    /// why <c>farm@1,1</c> still had only 0.375 reference px of daylight after
    /// it shipped — the same figure as the west and east sides #139 never
    /// covered.
    /// </para>
    ///
    /// <para>
    /// It exists because independent review of this branch found the gap by
    /// running the targeted mutant: stop widening the <c>FacadeBottom</c> seam
    /// alone and the whole suite stayed green.
    /// <see cref="A_walls_side_seam_paints_half_its_width_into_the_next_cell"/>
    /// pinned the lateral half of <see cref="WallRenderGeometry.DrawnBands"/>
    /// and nothing pinned the downward half; the one mutant that did reach it
    /// zeroed every seam at once, which is a coarser edit than the sub-mechanism
    /// this issue exists for. Two measurements, one per axis, and neither stands
    /// in for the other.
    /// </para>
    /// </summary>
    [Fact]
    public void A_walls_facade_seam_paints_half_its_width_below_the_facade()
    {
        const int tile = CameraView.DefaultTileSize;
        var scale = CameraView.WorldVisualScale(tile);
        var cell = new GridPoint(3, 3);
        var footprintBottom = CameraView.CellTopLeft(cell, tile).Y + tile;

        var bands = WallRenderGeometry.DrawnBands(cell, WallTileVariant.Isolated, tile);
        var reach = bands.Max(band => band.Y + band.Height) - footprintBottom;

        Assert.Equal(
            (WallRenderGeometry.FacadeReferenceOverhang +
             (WallRenderGeometry.EdgeReferenceWidth / 2.0)) * scale,
            reach,
            10);

        // And the north clearance is that reach plus the border's own share, so
        // the constant the ladder is built from cannot drift away from the
        // measurement above.
        Assert.Equal(
            RoomGeometry.NorthWallClearance,
            (reach / scale) +
            RoomGeometry.BorderStrokeHalfWidth +
            RoomGeometry.WallVisibleGap,
            10);
    }

    /// <summary>
    /// The upward half of the same question, which is what makes a wall standing
    /// in front of a room impossible to clear rather than merely expensive: the
    /// bright seam along the top of the lifted mass is centred on it, so a wall
    /// reaches <see cref="WallRenderGeometry.FacadeReferenceHeight"/> plus half a
    /// seam above its own footprint.
    /// </summary>
    [Fact]
    public void A_walls_top_seam_paints_half_its_width_above_the_lifted_mass()
    {
        const int tile = CameraView.DefaultTileSize;
        var scale = CameraView.WorldVisualScale(tile);
        var cell = new GridPoint(3, 3);
        var footprintTop = CameraView.CellTopLeft(cell, tile).Y;

        var bands = WallRenderGeometry.DrawnBands(cell, WallTileVariant.Isolated, tile);
        var reach = footprintTop - bands.Min(band => band.Y);

        Assert.Equal(
            (WallRenderGeometry.FacadeReferenceHeight +
             (WallRenderGeometry.EdgeReferenceWidth / 2.0)) * scale,
            reach,
            10);
    }

    // ------------------------------------------------------------- the measure

    private const double Tolerance = 1e-9;

    /// <summary>
    /// The inset <c>Main.DrawRoomBorder</c> draws with today.
    /// </summary>
    private static double WallAwareInset(
        PrototypeRoomSnapshot room,
        IReadOnlySet<GridPoint> rock) =>
        RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rock);

    /// <summary>
    /// The inset it drew with between Issue #139 and Issue #147: the plain ladder
    /// unless some cell had rock directly north, and a base of 5.0 when it did.
    ///
    /// Restated here because the policy no longer exists in production code, and a
    /// "before" column has to be reproducible after the "before" is gone. 5.0 is
    /// #139's own derivation — facade overhang 3.0, border half-stroke 1.0,
    /// visible gap 1.0 — which is exactly the term that missed the half of the
    /// facade's closing seam that is drawn below the facade rectangle.
    /// </summary>
    private static double PreIssue147Inset(
        PrototypeRoomSnapshot room,
        IReadOnlySet<GridPoint> rock)
    {
        var step = RoomGeometry.BorderInset(room.Purpose) - RoomGeometry.PlainBorderBase;
        var wallToNorth = room.Perimeter.Any(cell =>
            rock.Contains(new GridPoint(cell.X, cell.Y - 1)));
        return wallToNorth
            ? WallRenderGeometry.FacadeReferenceOverhang +
              RoomGeometry.BorderStrokeHalfWidth +
              RoomGeometry.WallVisibleGap +
              step
            : RoomGeometry.PlainBorderBase + step;
    }

    /// <summary>
    /// For every room and every side of it, the smallest gap between the border
    /// stroke actually drawn there and any band any wall actually paints.
    /// </summary>
    private static IReadOnlyList<Clearance> Measure(
        int tileSize,
        Func<PrototypeRoomSnapshot, IReadOnlySet<GridPoint>, double> inset,
        bool excuseWallsInFront = true)
    {
        var state = PresentationFixtures.Baseline(1);
        var rock = state.Map.RockTiles.ToHashSet();
        var scale = CameraView.WorldVisualScale(tileSize);
        var halfStroke = RoomGeometry.BorderStrokeHalfWidth * scale;
        var bands = rock
            .SelectMany(wall => WallRenderGeometry
                .DrawnBands(wall, WallTopology.SelectVariant(wall, rock), tileSize)
                .Select(band => (Wall: wall, Band: band)))
            .ToArray();

        var closest = new Dictionary<(string Room, string Side), Clearance>();
        foreach (var room in state.Rooms)
        {
            var reference = inset(room, rock);
            // A wall standing directly south of a cell of this room is drawn in
            // front of it and is excused; see
            // A_wall_in_front_of_a_room_cannot_be_cleared_by_any_inset.
            var inFront = excuseWallsInFront
                ? room.Perimeter
                    .Select(cell => new GridPoint(cell.X, cell.Y + 1))
                    .Where(rock.Contains)
                    .ToHashSet()
                : [];

            foreach (var edge in RoomGeometry.BorderEdges(
                         room.Perimeter,
                         tileSize,
                         reference * scale))
            {
                var stroke = StrokeBand(edge.Segment, halfStroke);
                foreach (var (wall, band) in bands)
                {
                    if (inFront.Contains(wall))
                    {
                        continue;
                    }

                    var gap = Separation(stroke, band);
                    var key = (room.Id, edge.Side.ToString());
                    if (closest.TryGetValue(key, out var best) && best.ScreenPx <= gap)
                    {
                        continue;
                    }

                    closest[key] = new Clearance(
                        room.Id,
                        room.Purpose.ToString(),
                        edge.Side.ToString(),
                        reference,
                        $"{edge.Cell.X},{edge.Cell.Y}",
                        $"{wall.X},{wall.Y}",
                        gap,
                        gap / scale);
                }
            }
        }

        return closest.Values
            .OrderBy(row => row.Room, StringComparer.Ordinal)
            .ThenBy(row => row.Side, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The rectangle a border segment is painted as. It used to be a copy of the
    /// widening rule kept here; Issue #156 needed the same rectangle in production
    /// code, so <see cref="RoomGeometry.StrokeBand"/> is now the one that answers
    /// and this measurement reads it rather than restating it.
    /// </summary>
    private static ViewRect StrokeBand(ViewSegment segment, double halfStroke) =>
        RoomGeometry.StrokeBand(segment, halfStroke);

    /// <summary>
    /// How far apart two axis-aligned rectangles are: the larger of the two
    /// per-axis gaps, negative when they overlap. It understates a purely
    /// diagonal separation, which is the safe direction — a check built on it can
    /// only be stricter than the true distance, never looser.
    /// </summary>
    private static double Separation(ViewRect first, ViewRect second)
    {
        var gapX = Math.Max(
            first.X - (second.X + second.Width),
            second.X - (first.X + first.Width));
        var gapY = Math.Max(
            first.Y - (second.Y + second.Height),
            second.Y - (first.Y + first.Height));
        return Math.Max(gapX, gapY);
    }

    private static string Payload(
        int tileSize,
        double required,
        IReadOnlyList<Clearance> measured,
        IReadOnlyList<Clearance> violations) =>
        JsonSerializer.Serialize(
            new
            {
                tileSize,
                scale = CameraView.WorldVisualScale(tileSize),
                requiredScreenPx = required,
                requiredReferencePx = RoomGeometry.WallVisibleGap,
                violationCount = violations.Count,
                violations,
                closestPerRoomAndSide = measured,
            },
            new JsonSerializerOptions { WriteIndented = true });
}
