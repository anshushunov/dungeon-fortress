using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// One column of a selection: the horizontal band it owns and the vertical span
/// its cells actually occupy on screen. Rock raises the top of its column and
/// hangs its facade below the footprint, so two columns of the same selection
/// legitimately have different spans.
/// </summary>
public readonly record struct SelectionColumn(
    int Cell,
    double Left,
    double Right,
    double Top,
    double Bottom);

/// <summary>
/// The shape of everything the pointer selects, derived from one function.
///
/// Issue #83 gave rock volume — a raised top and an observer-facing facade — and
/// taught the hover highlight, the selected cell and the dig marks about it,
/// because all of them ask <see cref="CellInteractionRect"/>. The rectangle a
/// drag stretches did not ask: it was built in grid coordinates and knew nothing
/// about volume. The owner saw exactly that on playtest — hovering rock outlines
/// its whole shape, clicking it snaps back to a flat square — and it happens at
/// the moment the player moves from looking to acting.
///
/// So the frame is built from the same per-cell rectangle the highlight uses. It
/// is a rectilinear profile rather than a bounding box: on a drag across floor
/// and rock the top rises only over the columns whose first cell is rock, which
/// is what the second review round of Issue #83 objected to when the frame was
/// briefly the bounding box of the raised rectangles.
/// </summary>
public static class SelectionGeometry
{
    /// <summary>
    /// The rectangle a cell answers a pointer with and is highlighted by. Floor
    /// is its footprint; rock is its visible mass, top and facade included.
    ///
    /// The one-pixel inset is the grid hairline: two neighbouring cells must not
    /// paint the same pixel column, or a wall of outlined cells reads as a solid
    /// block.
    /// </summary>
    public static ViewRect CellInteractionRect(
        GridPoint cell,
        IReadOnlySet<GridPoint> rockTiles,
        int tileSize)
    {
        ArgumentNullException.ThrowIfNull(rockTiles);
        var topLeft = CameraView.CellTopLeft(cell, tileSize);
        if (!rockTiles.Contains(cell))
        {
            return new ViewRect(topLeft.X, topLeft.Y, tileSize - 1, tileSize - 1);
        }

        var bounds = WallRenderGeometry
            .ForCell(cell, WallTopology.SelectVariant(cell, rockTiles), tileSize)
            .Bounds;
        return new ViewRect(
            bounds.X,
            bounds.Y,
            bounds.Width - 1,
            bounds.Height - 1);
    }

    /// <summary>
    /// The selection column by column. Each column spans from the top of its
    /// first cell's interaction rectangle to the bottom of its last one, which
    /// is what the union of those rectangles occupies.
    ///
    /// Only the two ends of a column are measured, and that is exact rather than
    /// approximate because of one invariant: a wall reaches less than a whole
    /// tile past its own footprint. Its raised top rises
    /// <see cref="WallRenderGeometry.FacadeReferenceHeight"/> scaled pixels and
    /// its facade hangs <see cref="WallRenderGeometry.FacadeReferenceOverhang"/>
    /// below, and their sum stays under the tile size across the whole 32–48 px
    /// range ADR 0008 allows — 16 px at 32 and 24 px at 48. So rock in the middle
    /// of a column can never poke out of a span whose ends are one tile further
    /// away, whatever those ends are made of.
    /// <c>SelectionGeometryTests.A_wall_reaches_less_than_a_tile_past_its_footprint</c>
    /// pins the invariant and the floor–rock–floor column that depends on it.
    /// </summary>
    public static IReadOnlyList<SelectionColumn> Columns(
        GridPoint from,
        GridPoint to,
        IReadOnlySet<GridPoint> rockTiles,
        int tileSize)
    {
        ArgumentNullException.ThrowIfNull(rockTiles);
        var cells = BrushSelection.Rectangle(from, to);
        if (cells.Count == 0)
        {
            return [];
        }

        var minX = cells.Min(cell => cell.X);
        var maxX = cells.Max(cell => cell.X);
        var minY = cells.Min(cell => cell.Y);
        var maxY = cells.Max(cell => cell.Y);

        var columns = new List<SelectionColumn>(maxX - minX + 1);
        for (var x = minX; x <= maxX; x++)
        {
            var head = CellInteractionRect(new GridPoint(x, minY), rockTiles, tileSize);
            var tail = CellInteractionRect(new GridPoint(x, maxY), rockTiles, tileSize);

            // Interior columns meet at the exact grid boundary so the frame stays
            // continuous; the last one keeps the hairline the cell rectangles use.
            var right = x == maxX
                ? (x * (double)tileSize) + tileSize - 1
                : (x + 1) * (double)tileSize;
            columns.Add(new SelectionColumn(
                x,
                x * (double)tileSize,
                right,
                head.Y,
                tail.Y + tail.Height));
        }

        return columns;
    }

    /// <summary>
    /// The closed outline of the selection, inset by <paramref name="inset"/> the
    /// way a drawn frame is. The first point is repeated last, so the caller
    /// draws one polyline instead of closing the loop itself.
    /// </summary>
    public static IReadOnlyList<ViewPoint> Outline(
        GridPoint from,
        GridPoint to,
        IReadOnlySet<GridPoint> rockTiles,
        int tileSize,
        double inset)
    {
        var columns = Columns(from, to, rockTiles, tileSize);
        if (columns.Count == 0)
        {
            return [];
        }

        var left = columns[0].Left + inset;
        var right = columns[^1].Right - inset;
        var points = new List<ViewPoint>();

        points.Add(new ViewPoint(left, columns[0].Top + inset));
        for (var index = 1; index < columns.Count; index++)
        {
            if (columns[index].Top == columns[index - 1].Top)
            {
                continue;
            }

            points.Add(new ViewPoint(columns[index].Left, columns[index - 1].Top + inset));
            points.Add(new ViewPoint(columns[index].Left, columns[index].Top + inset));
        }

        points.Add(new ViewPoint(right, columns[^1].Top + inset));
        points.Add(new ViewPoint(right, columns[^1].Bottom - inset));
        for (var index = columns.Count - 1; index > 0; index--)
        {
            if (columns[index].Bottom == columns[index - 1].Bottom)
            {
                continue;
            }

            points.Add(new ViewPoint(columns[index].Left, columns[index].Bottom - inset));
            points.Add(new ViewPoint(columns[index].Left, columns[index - 1].Bottom - inset));
        }

        points.Add(new ViewPoint(left, columns[0].Bottom - inset));
        points.Add(points[0]);
        return points;
    }

    /// <summary>
    /// The smallest rectangle holding the whole selection. It is deliberately not
    /// what the frame is drawn from — that is <see cref="Outline"/> — and is used
    /// only to anchor the cell count.
    /// </summary>
    public static ViewRect Bounds(
        GridPoint from,
        GridPoint to,
        IReadOnlySet<GridPoint> rockTiles,
        int tileSize)
    {
        var columns = Columns(from, to, rockTiles, tileSize);
        if (columns.Count == 0)
        {
            return new ViewRect(0, 0, 0, 0);
        }

        var left = columns.Min(column => column.Left);
        var right = columns.Max(column => column.Right);
        var top = columns.Min(column => column.Top);
        var bottom = columns.Max(column => column.Bottom);
        return new ViewRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Where the cell count is drawn. Above the selection when there is room and
    /// inside it when there is not, and always fully inside the map: HUD masks
    /// cover every canvas pixel outside the explicit world viewport, so a caption
    /// hanging over the edge would be hidden rather than appear in the HUD.
    ///
    /// A raised rock top makes this more than a formality — the top of a
    /// selection on row 0 is above the map, so the anchor itself can be negative.
    /// </summary>
    public static ViewRect CaptionBox(
        ViewPoint anchor,
        ViewSize caption,
        double gap,
        int tileSize)
    {
        var map = CameraView.MapSize(tileSize);
        var above = anchor.Y - caption.Height - gap;
        var y = above >= 0 ? above : anchor.Y + gap;
        return new ViewRect(
            Math.Clamp(anchor.X, 0, Math.Max(0, map.Width - caption.Width)),
            Math.Clamp(y, 0, Math.Max(0, map.Height - caption.Height)),
            caption.Width,
            caption.Height);
    }
}
