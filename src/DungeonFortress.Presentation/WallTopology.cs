using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// Orthogonal rock connections around one wall tile. The numeric values are the
/// tile-variant contract: every possible four-neighbour state has one stable
/// value, independent of Godot or an eventual atlas.
/// </summary>
[Flags]
public enum WallNeighbors : byte
{
    None = 0,
    North = 1 << 0,
    East = 1 << 1,
    South = 1 << 2,
    West = 1 << 3,
}

/// <summary>
/// The sixteen graybox wall variants. Names describe connected sides, not drawn
/// borders: an absent side is an exposed edge that the adapter must articulate.
/// </summary>
public enum WallTileVariant : byte
{
    Isolated = WallNeighbors.None,
    North = WallNeighbors.North,
    East = WallNeighbors.East,
    NorthEast = WallNeighbors.North | WallNeighbors.East,
    South = WallNeighbors.South,
    NorthSouth = WallNeighbors.North | WallNeighbors.South,
    EastSouth = WallNeighbors.East | WallNeighbors.South,
    NorthEastSouth = WallNeighbors.North | WallNeighbors.East | WallNeighbors.South,
    West = WallNeighbors.West,
    NorthWest = WallNeighbors.North | WallNeighbors.West,
    EastWest = WallNeighbors.East | WallNeighbors.West,
    NorthEastWest = WallNeighbors.North | WallNeighbors.East | WallNeighbors.West,
    SouthWest = WallNeighbors.South | WallNeighbors.West,
    NorthSouthWest = WallNeighbors.North | WallNeighbors.South | WallNeighbors.West,
    EastSouthWest = WallNeighbors.East | WallNeighbors.South | WallNeighbors.West,
    Surrounded = WallNeighbors.North | WallNeighbors.East | WallNeighbors.South | WallNeighbors.West,
}

/// <summary>
/// Pure wall autotiling for ADR 0008. It reads only the published rock set, so a
/// frame after excavation gets a new variant without asking the simulation for
/// presentation-specific data.
/// </summary>
public static class WallTopology
{
    private static readonly (GridPoint Offset, WallNeighbors Side)[] CardinalNeighbors =
    [
        (new GridPoint(0, -1), WallNeighbors.North),
        (new GridPoint(1, 0), WallNeighbors.East),
        (new GridPoint(0, 1), WallNeighbors.South),
        (new GridPoint(-1, 0), WallNeighbors.West),
    ];

    public static WallTileVariant SelectVariant(
        GridPoint cell,
        IReadOnlySet<GridPoint> rockTiles)
    {
        ArgumentNullException.ThrowIfNull(rockTiles);
        if (!rockTiles.Contains(cell))
        {
            throw new ArgumentException(
                $"Cell ({cell.X},{cell.Y}) is not a rock tile.",
                nameof(cell));
        }

        var neighbors = WallNeighbors.None;
        foreach (var (offset, side) in CardinalNeighbors)
        {
            if (rockTiles.Contains(new GridPoint(cell.X + offset.X, cell.Y + offset.Y)))
            {
                neighbors |= side;
            }
        }

        return (WallTileVariant)neighbors;
    }

    public static bool Connects(WallTileVariant variant, WallNeighbors side)
    {
        if (side is WallNeighbors.None || !IsSingleSide(side))
        {
            throw new ArgumentOutOfRangeException(
                nameof(side),
                side,
                "A single cardinal side is required.");
        }

        return (((WallNeighbors)variant) & side) != 0;
    }

    /// <summary>
    /// The observer-facing wall face is exposed only where rock does not continue
    /// towards screen-bottom.
    /// </summary>
    public static bool HasFrontFacade(WallTileVariant variant) =>
        !Connects(variant, WallNeighbors.South);

    private static bool IsSingleSide(WallNeighbors side) =>
        side is WallNeighbors.North or
            WallNeighbors.East or
            WallNeighbors.South or
            WallNeighbors.West;
}
