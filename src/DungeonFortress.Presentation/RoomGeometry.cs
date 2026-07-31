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
    /// telling apart. Widening the ladder would push the innermost border a third
    /// of the way into a one-tile room at the smallest tile size ADR 0008 allows.
    /// </summary>
    public static double BorderInset(ZoneKind purpose) => 2.0 + ((int)purpose % 3) * 1.5;

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
