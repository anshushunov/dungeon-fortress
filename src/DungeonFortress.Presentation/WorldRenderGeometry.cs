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
