using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// Engine-free construction of depth items. These anchors are presentation
/// policy: walls use the south edge of their footprint, usable structures use
/// the cell centre, and moving bodies use their interpolated render centre.
/// </summary>
public static class WorldRenderGeometry
{
    public static WorldRenderItem ForCell(
        WorldRenderKind kind,
        int stableId,
        GridPoint cell,
        int tileSize)
    {
        var topLeft = CameraView.CellTopLeft(cell, tileSize);
        var center = CameraView.CellCenter(cell, tileSize);
        return kind switch
        {
            WorldRenderKind.Wall =>
                new WorldRenderItem(kind, stableId, center.X, topLeft.Y + tileSize),
            WorldRenderKind.Structure =>
                new WorldRenderItem(kind, stableId, center.X, center.Y),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Only cell-anchored world geometry can use a grid cell."),
        };
    }

    public static WorldRenderItem ForBody(
        WorldRenderKind kind,
        int stableId,
        ViewPoint interpolatedCenter) =>
        kind switch
        {
            WorldRenderKind.Creature or WorldRenderKind.Raider =>
                new WorldRenderItem(
                    kind,
                    stableId,
                    interpolatedCenter.X,
                    interpolatedCenter.Y),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Only moving bodies can use an interpolated centre."),
        };
}

/// <summary>
/// Semantic wall strokes. The adapter chooses colours and widths, while this
/// engine-free value fixes which exposed side maps to which visible segment.
/// </summary>
public enum WallStrokeKind
{
    BrightEdge,
    DarkEdge,
    FacadeLip,
    FacadeBottom,
}

public readonly record struct WallStroke(
    ViewPoint From,
    ViewPoint To,
    WallStrokeKind Kind);

/// <summary>
/// All geometry shared by wall drawing and interaction overlays.
/// </summary>
public sealed record WallVisualMass(
    ViewRect Bounds,
    ViewRect Top,
    ViewRect? Facade,
    IReadOnlyList<WallStroke> Strokes);

/// <summary>
/// Pure graybox wall geometry. Reference dimensions are scaled with the same
/// tile-size policy as every other world primitive.
/// </summary>
public static class WallRenderGeometry
{
    public const double FacadeReferenceHeight = 8.0;
    public const double FacadeReferenceOverhang = 3.0;

    /// <summary>
    /// The reference-pixel width <c>Main.DrawWall</c> strokes a wall seam with —
    /// its bright top edge, its dark side edges and the bottom of its facade.
    ///
    /// It lives here rather than as a literal in the adapter because a stroke is
    /// not a line: <c>DrawLine</c> centres a band of this width on the segment,
    /// so half of it lands on whichever side of the seam the wall does not own.
    /// A dark side edge sits exactly on the boundary between the wall's cell and
    /// its neighbour's, which makes that half a real intrusion into a floor cell
    /// somebody else's border is drawn in (Issue #147). Nothing can derive that
    /// intrusion from a number the engine assembly keeps to itself.
    /// </summary>
    public const double EdgeReferenceWidth = 1.25;

    /// <summary>
    /// The reference-pixel width of the facade's top lip, drawn heavier than the
    /// seams above so the change of plane reads at a glance.
    /// </summary>
    public const double FacadeLipReferenceWidth = 2.0;

    /// <summary>
    /// How wide <c>Main.DrawWall</c> strokes each kind of seam, in reference
    /// pixels before the world scale is applied.
    /// </summary>
    public static double ReferenceStrokeWidth(WallStrokeKind kind) => kind switch
    {
        WallStrokeKind.BrightEdge or
        WallStrokeKind.DarkEdge or
        WallStrokeKind.FacadeBottom => EdgeReferenceWidth,
        WallStrokeKind.FacadeLip => FacadeLipReferenceWidth,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "Every wall stroke kind must declare the width it is drawn at."),
    };

    public static WallVisualMass ForCell(
        GridPoint cell,
        WallTileVariant variant,
        int tileSize)
    {
        var topLeft = CameraView.CellTopLeft(cell, tileSize);
        var scale = CameraView.WorldVisualScale(tileSize);
        var facadeHeight = FacadeReferenceHeight * scale;
        var facadeOverhang = FacadeReferenceOverhang * scale;
        var visualTopLeft = new ViewPoint(topLeft.X, topLeft.Y - facadeHeight);
        var top = new ViewRect(visualTopLeft.X, visualTopLeft.Y, tileSize, tileSize);
        var exposed = WallTopology.ExposedSides(variant);
        var strokes = new List<WallStroke>();

        if (exposed.HasFlag(WallNeighbors.North))
        {
            strokes.Add(new WallStroke(
                visualTopLeft,
                new ViewPoint(visualTopLeft.X + tileSize, visualTopLeft.Y),
                WallStrokeKind.BrightEdge));
        }

        if (exposed.HasFlag(WallNeighbors.West))
        {
            strokes.Add(new WallStroke(
                visualTopLeft,
                new ViewPoint(topLeft.X, visualTopLeft.Y + tileSize),
                WallStrokeKind.DarkEdge));
        }

        if (exposed.HasFlag(WallNeighbors.East))
        {
            strokes.Add(new WallStroke(
                new ViewPoint(visualTopLeft.X + tileSize, visualTopLeft.Y),
                new ViewPoint(
                    topLeft.X + tileSize,
                    visualTopLeft.Y + tileSize),
                WallStrokeKind.DarkEdge));
        }

        if (!WallTopology.HasFrontFacade(variant))
        {
            return new WallVisualMass(top, top, null, strokes);
        }

        var facadeTop = topLeft.Y + tileSize - facadeHeight;
        var facade = new ViewRect(
            topLeft.X,
            facadeTop,
            tileSize,
            facadeHeight + facadeOverhang);
        strokes.Add(new WallStroke(
            new ViewPoint(topLeft.X, facadeTop),
            new ViewPoint(topLeft.X + tileSize, facadeTop),
            WallStrokeKind.FacadeLip));

        if (exposed.HasFlag(WallNeighbors.West))
        {
            strokes.Add(new WallStroke(
                new ViewPoint(topLeft.X, facade.Y),
                new ViewPoint(topLeft.X, facade.Y + facade.Height),
                WallStrokeKind.DarkEdge));
        }

        if (exposed.HasFlag(WallNeighbors.East))
        {
            strokes.Add(new WallStroke(
                new ViewPoint(topLeft.X + tileSize, facade.Y),
                new ViewPoint(
                    topLeft.X + tileSize,
                    facade.Y + facade.Height),
                WallStrokeKind.DarkEdge));
        }

        strokes.Add(new WallStroke(
            new ViewPoint(topLeft.X, topLeft.Y + tileSize + facadeOverhang),
            new ViewPoint(
                topLeft.X + tileSize,
                topLeft.Y + tileSize + facadeOverhang),
            WallStrokeKind.FacadeBottom));

        var bounds = new ViewRect(
            visualTopLeft.X,
            visualTopLeft.Y,
            tileSize,
            tileSize + facadeHeight + facadeOverhang);
        return new WallVisualMass(bounds, top, facade, strokes);
    }

    /// <summary>
    /// Every rectangle of screen a wall actually covers: its top mass, its facade
    /// if it has one, and each seam widened into the band <c>DrawLine</c> paints
    /// it as — <see cref="ReferenceStrokeWidth"/> scaled, half to either side of
    /// the segment.
    ///
    /// <see cref="ForCell"/> answers "where are the wall's lines", which is what
    /// drawing needs. This answers "which pixels does the wall end up on", which
    /// is what anything drawn <em>next to</em> a wall needs — and the difference
    /// between the two is not decoration. A dark side edge is a segment lying
    /// exactly on a cell boundary, so as a line it intrudes into the neighbouring
    /// floor cell by nothing at all and as a band it intrudes by half its width.
    /// Issue #147 is that whole distinction: a room border kept clear of the
    /// segment still lands inside the band.
    ///
    /// Nothing draws from this. It exists so a check can measure the wall's real
    /// extent instead of restating it, the same reason
    /// <see cref="WorldDrawOrder"/> exists.
    /// </summary>
    public static IReadOnlyList<ViewRect> DrawnBands(
        GridPoint cell,
        WallTileVariant variant,
        int tileSize)
    {
        var mass = ForCell(cell, variant, tileSize);
        var scale = CameraView.WorldVisualScale(tileSize);
        var bands = new List<ViewRect> { mass.Top };
        if (mass.Facade is { } facade)
        {
            bands.Add(facade);
        }

        foreach (var stroke in mass.Strokes)
        {
            var half = ReferenceStrokeWidth(stroke.Kind) * scale / 2.0;
            var left = Math.Min(stroke.From.X, stroke.To.X);
            var right = Math.Max(stroke.From.X, stroke.To.X);
            var top = Math.Min(stroke.From.Y, stroke.To.Y);
            var bottom = Math.Max(stroke.From.Y, stroke.To.Y);

            // A stroke is widened across itself and not along itself: Godot's
            // DrawLine draws the band between the two ends without a cap, so a
            // seam does not reach past the corner it stops at.
            bands.Add(right - left >= bottom - top
                ? new ViewRect(left, top - half, right - left, (bottom - top) + (2.0 * half))
                : new ViewRect(left - half, top, (right - left) + (2.0 * half), bottom - top));
        }

        return bands;
    }
}

/// <summary>
/// Reversible row-major IDs used only to reconnect sorted presentation items
/// with adapter-owned drawing data.
/// </summary>
public static class GridCellId
{
    public static int Encode(GridPoint cell, int width)
    {
        ValidateWidth(width);
        if (cell.X < 0 || cell.X >= width || cell.Y < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cell),
                "Cell coordinates must be non-negative and X must fit the grid width.");
        }

        return checked((cell.Y * width) + cell.X);
    }

    public static GridPoint Decode(int stableId, int width)
    {
        ValidateWidth(width);
        if (stableId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stableId),
                stableId,
                "A stable cell ID cannot be negative.");
        }

        return new GridPoint(stableId % width, stableId / width);
    }

    private static void ValidateWidth(int width)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                "Grid width must be positive.");
        }
    }
}
