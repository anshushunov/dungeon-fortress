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
