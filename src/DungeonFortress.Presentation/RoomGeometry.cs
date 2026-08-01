using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>One straight piece of a drawn outline.</summary>
public readonly record struct ViewSegment(ViewPoint From, ViewPoint To);

/// <summary>
/// The shape of a room on screen: its border, where its caption sits, and how far
/// in the border is drawn.
///
/// It lives here rather than in the simulation for the same reason
/// <see cref="WallTopology"/> does: an outline is topology over a set of tiles, it
/// follows from the tiles the snapshot publishes, and it needs no tick to run
/// (<see href="../../docs/decisions/0011-presentation-layer-without-engine.md">ADR
/// 0011</see>). The simulation publishes the patch; this file turns the patch into
/// a line.
///
/// <b>Why a border at all.</b>
/// <see href="../../docs/decisions/0013-what-is-a-room.md">ADR 0013</see> makes it
/// mandatory rather than optional, and names the game it is avoiding: in Dwarf
/// Fortress a room exists as an entity and its boundary is never shown, which is
/// «одна из самых устойчивых жалоб на игру». A per-cell box around every tile —
/// what the zone outline drew before — is the same failure wearing the opposite
/// costume: twenty-one boxes are not a room, they are texture. One line around the
/// patch is.
/// </summary>
public static class RoomGeometry
{
    /// <summary>
    /// How far inside its own cells a room's border is drawn, in reference pixels
    /// before the world scale is applied.
    ///
    /// It depends on the purpose because rooms overlap: a <c>Forbidden</c> paint
    /// over a gym is two rooms on the same tiles, and two borders on the same
    /// pixels are one border. Purposes three apart in the enum share a value, and
    /// that is deliberate rather than an oversight — the inset exists so that a
    /// second border is not swallowed whole, and colour does the rest of the
    /// telling apart.
    ///
    /// This ladder itself stays shallow — 2.0 to 5.0 — on purpose: widening it
    /// would push the innermost border a third of the way into a one-tile room
    /// at the smallest tile size ADR 0008 allows.
    /// <see cref="WallAdjacentBorderInset"/> spends exactly that cost anyway,
    /// but only for a room that actually borders a wall (Issue #139): the
    /// alternative was a border sitting inside the wall's own facade, and that
    /// reads worse than a deep inset does. Naming the trade rather than paying
    /// it silently: it does not stop at the border. The wall-adjacent ladder's
    /// own reach pushed a second, smaller cost into view — it can overlap
    /// <see cref="LabelDefaultTop"/>'s old fixed caption position, which is why
    /// that position moves for a room that pays for it (<see cref="LabelTop"/>).
    /// </summary>
    public static double BorderInset(ZoneKind purpose) => 2.0 + PurposeLadderStep(purpose);

    /// <summary>
    /// <see cref="BorderInset"/> for a room whose border sits under a wall — one
    /// of its cells has rock directly to the north (<see cref="BordersWallToNorth"/>).
    ///
    /// <see cref="WallRenderGeometry"/> draws a wall as a volume: its front facade
    /// overhangs <see cref="WallClearance"/>-worth of ground past the wall's own
    /// footprint, into whichever cell sits right below it. The plain
    /// <see cref="BorderInset"/> ladder knows nothing about a neighbour and was
    /// tuned only so two overlapping purposes stay apart from each other
    /// (Issue #52) — nothing in it keeps a purpose apart from a wall, so at the
    /// low end of the ladder the border was drawn inside the facade's own
    /// overhang (Issue #139). This uses the same per-purpose step, so two
    /// overlapping purposes pushed by the same wall are exactly as apart from
    /// each other as they were before the push, just both moved past the facade.
    /// </summary>
    public static double WallAdjacentBorderInset(ZoneKind purpose) =>
        WallClearance + PurposeLadderStep(purpose);

    /// <summary>
    /// Whether any cell of the room has a wall directly to its north — the one
    /// direction a wall's facade can hang over a room's own footprint, since
    /// <see cref="WallRenderGeometry"/> only ever draws a facade on a wall's
    /// south-facing (observer-facing) side.
    /// </summary>
    public static bool BordersWallToNorth(
        IReadOnlyCollection<GridPoint> tiles,
        IReadOnlySet<GridPoint> wallTiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(wallTiles);
        return tiles.Any(cell => wallTiles.Contains(new GridPoint(cell.X, cell.Y - 1)));
    }

    /// <summary>
    /// Half the width <c>Main.DrawRoomBorder</c> strokes a border line with: a
    /// line "at" some coordinate actually covers a band a half-stroke to either
    /// side of it, and it is the near edge of that band — not the coordinate
    /// itself — that must clear the facade.
    /// </summary>
    private const double BorderStrokeHalfWidth = 1.0;

    /// <summary>
    /// A further reference pixel kept clear once the overhang and the stroke's
    /// own half-width are both accounted for, so the border reads as apart from
    /// the wall rather than merely not touching it.
    /// </summary>
    private const double WallVisibleGap = 1.0;

    /// <summary>
    /// The reference-pixel depth a wall-adjacent border needs before its own
    /// per-purpose step: the facade's downward overhang past the wall's own
    /// footprint, the half-width of the stroke that draws the border, and a
    /// visible gap. Derived from <see cref="WallRenderGeometry.FacadeReferenceOverhang"/>
    /// rather than restated, so a change to the wall's geometry cannot silently
    /// leave this stale.
    /// </summary>
    public const double WallClearance =
        WallRenderGeometry.FacadeReferenceOverhang + BorderStrokeHalfWidth + WallVisibleGap;

    /// <summary>
    /// The step <see cref="BorderInset"/> and <see cref="WallAdjacentBorderInset"/>
    /// both climb by purpose, so that whichever base either starts from, two
    /// overlapping purposes stay exactly as far apart from each other.
    /// </summary>
    private static double PurposeLadderStep(ZoneKind purpose) => ((int)purpose % 3) * 1.5;

    /// <summary>
    /// The reference-pixel size <c>Main.DrawRoomIcon</c> draws a room's purpose
    /// glyph at. <see cref="LabelTop"/> also uses it as the vertical span
    /// between the icon's own top and the caption baseline directly under it,
    /// because that is what <c>Main.DrawRoomLabel</c> draws: the two shared one
    /// unnamed literal before Issue #139 F1, and a border cutting through both
    /// of them at once is what independent review found by naming the two
    /// separately at the same coordinate.
    /// </summary>
    public const double LabelIconSize = 8.0;

    /// <summary>
    /// Where a room's caption and icon start, in reference pixels below the
    /// anchor cell's top edge, absent any reason to move them — <c>Main.cs</c>'s
    /// original, unconditional position from before Issue #139 gave the border
    /// underneath a reason to move.
    /// </summary>
    public const double LabelDefaultTop = 2.0;

    /// <summary>
    /// A reference pixel of daylight kept between a room's border stroke and
    /// its caption/icon block, past the stroke's own half-width — the same
    /// shape of margin <see cref="WallClearance"/> keeps against the wall's
    /// facade — so a border pushed deep by <see cref="WallAdjacentBorderInset"/>
    /// cannot read as cutting through the glyphs sitting next to it.
    /// </summary>
    private const double LabelClearanceGap = 1.0;

    /// <summary>
    /// How far below the anchor cell's top edge a room's caption and icon may
    /// start, in reference pixels, given the inset the room's own border is
    /// actually drawn at — whichever of <see cref="BorderInset"/> or
    /// <see cref="WallAdjacentBorderInset"/> <c>Main.DrawRoomBorder</c> picked
    /// for the same room, passed in as <paramref name="borderInset"/> rather
    /// than recomputed here, so the two can never read a different inset for
    /// one room than the other.
    ///
    /// Below <see cref="LabelDefaultTop"/> only when the border's own stroke
    /// band — <paramref name="borderInset"/> ± the stroke's half-width — would
    /// otherwise reach into it. Issue #139 F1: independent review found the
    /// deepened wall-adjacent ladder cutting straight through the caption and
    /// icon of every room checkpoint 2's own evidence screenshotted, because
    /// the label was still drawn at the fixed pre-#139 position and knew
    /// nothing of how far the border underneath it had moved.
    /// </summary>
    public static double LabelTop(double borderInset) =>
        Math.Max(LabelDefaultTop, borderInset + BorderStrokeHalfWidth + LabelClearanceGap);

    /// <summary>
    /// The closed outline of a room, as the boundary edges of its patch — one
    /// segment per cell side whose neighbour is outside the room, moved inward by
    /// <paramref name="inset"/> and trimmed or extended at its ends so the corners
    /// meet exactly.
    ///
    /// Holes are handled by construction: an enclosed cell that is not part of the
    /// room produces four inner edges, and they are drawn like any other boundary.
    /// A room made of two touching cells therefore has six segments and not eight.
    ///
    /// Flat cell geometry is correct here and nowhere else in this file needs
    /// saying twice: a zone may only be painted on passable tiles (contract 4.4),
    /// so no cell of a room is ever rock, and rock is the only thing on this map
    /// with volume to reach out of its footprint.
    /// </summary>
    public static IReadOnlyList<ViewSegment> Border(
        IReadOnlyCollection<GridPoint> tiles,
        int tileSize,
        double inset)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var room = tiles as IReadOnlySet<GridPoint> ?? tiles.ToHashSet();
        var segments = new List<ViewSegment>();
        foreach (var cell in tiles)
        {
            foreach (var side in Sides)
            {
                if (room.Contains(Step(cell, side.Normal)))
                {
                    continue;
                }

                segments.Add(Edge(room, cell, side, tileSize, inset));
            }
        }

        return segments;
    }

    /// <summary>
    /// Where the caption and the icon of a room go: the top-left corner of the
    /// tile the room is named after — the first of its cells in reading order,
    /// which is the same tile <c>PrototypeRooms.Identify</c> builds the id from.
    ///
    /// The caption and the identity therefore point at one cell rather than two,
    /// which is what lets a player who reads <c>trainingGround@10,2</c> in a
    /// structured run find the caption on the map without a second lookup.
    /// </summary>
    public static ViewPoint LabelAnchor(IReadOnlyList<GridPoint> perimeter, int tileSize)
    {
        ArgumentNullException.ThrowIfNull(perimeter);
        if (perimeter.Count == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(perimeter),
                "A room with no cells cannot exist: it is derived from the cells.");
        }

        return CameraView.CellTopLeft(perimeter.Min(), tileSize);
    }

    /// <summary>
    /// The smallest rectangle covering the room, used to place things relative to
    /// the whole of it rather than to one cell.
    /// </summary>
    public static ViewRect Bounds(IReadOnlyCollection<GridPoint> tiles, int tileSize)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Count == 0)
        {
            return new ViewRect(0, 0, 0, 0);
        }

        var left = tiles.Min(cell => cell.X) * (double)tileSize;
        var top = tiles.Min(cell => cell.Y) * (double)tileSize;
        var right = (tiles.Max(cell => cell.X) + 1) * (double)tileSize;
        var bottom = (tiles.Max(cell => cell.Y) + 1) * (double)tileSize;
        return new ViewRect(left, top, right - left, bottom - top);
    }

    private readonly record struct Side(GridPoint Normal, GridPoint Tangent);

    /// <summary>
    /// The four sides of a cell as an outward normal and the direction the edge
    /// runs in. The tangent is the "second" end; the "first" end is its opposite.
    /// </summary>
    private static readonly Side[] Sides =
    [
        new(new GridPoint(0, -1), new GridPoint(1, 0)),
        new(new GridPoint(1, 0), new GridPoint(0, 1)),
        new(new GridPoint(0, 1), new GridPoint(1, 0)),
        new(new GridPoint(-1, 0), new GridPoint(0, 1)),
    ];

    /// <summary>
    /// One boundary edge, inset and with both ends resolved.
    ///
    /// An end meets one of three situations, and each has exactly one right
    /// answer if the outline is to close:
    ///
    /// <list type="bullet">
    /// <item>the cell along the edge is outside the room — the border turns here,
    /// so the end is pulled back by the inset to meet the edge it turns
    /// into;</item>
    /// <item>that cell is inside and its own neighbour across this edge is inside
    /// too — an inner corner, so the end is pushed out by the inset to meet the
    /// edge coming the other way;</item>
    /// <item>otherwise the edge simply continues into its neighbour's, and the end
    /// stays on the grid line.</item>
    /// </list>
    /// </summary>
    private static ViewSegment Edge(
        IReadOnlySet<GridPoint> room,
        GridPoint cell,
        Side side,
        int tileSize,
        double inset)
    {
        var origin = CameraView.CellTopLeft(cell, tileSize);
        var far = new ViewPoint(origin.X + tileSize, origin.Y + tileSize);

        // The line the edge sits on once it has been moved inward.
        var x = side.Normal.X switch
        {
            > 0 => far.X - inset,
            < 0 => origin.X + inset,
            _ => double.NaN,
        };
        var y = side.Normal.Y switch
        {
            > 0 => far.Y - inset,
            < 0 => origin.Y + inset,
            _ => double.NaN,
        };

        var first = Trim(room, cell, side, Negate(side.Tangent), inset);
        var second = Trim(room, cell, side, side.Tangent, inset);

        if (double.IsNaN(x))
        {
            // A horizontal edge: it spans the cell in x and sits at the computed y.
            return new ViewSegment(
                new ViewPoint(origin.X - first, y),
                new ViewPoint(far.X + second, y));
        }

        return new ViewSegment(
            new ViewPoint(x, origin.Y - first),
            new ViewPoint(x, far.Y + second));
    }

    private static double Trim(
        IReadOnlySet<GridPoint> room,
        GridPoint cell,
        Side side,
        GridPoint towards,
        double inset)
    {
        var along = Step(cell, towards);
        if (!room.Contains(along))
        {
            return -inset;
        }

        return room.Contains(Step(along, side.Normal)) ? inset : 0;
    }

    private static GridPoint Step(GridPoint cell, GridPoint offset) =>
        new(cell.X + offset.X, cell.Y + offset.Y);

    private static GridPoint Negate(GridPoint offset) => new(-offset.X, -offset.Y);
}
