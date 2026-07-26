namespace DungeonFortress.Simulation;

internal sealed class PrototypeMap
{
    private static readonly GridPoint[] NeighborOffsets =
    [
        new(0, -1),
        new(1, 0),
        new(0, 1),
        new(-1, 0),
    ];

    private readonly TileKind[,] _tiles =
        new TileKind[PrototypeTuning.MapWidth, PrototypeTuning.MapHeight];

    public PrototypeMap()
    {
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                _tiles[x, y] =
                    x == 0 || y == 0 ||
                    x == PrototypeTuning.MapWidth - 1 ||
                    y == PrototypeTuning.MapHeight - 1
                        ? TileKind.Rock
                        : TileKind.Floor;
            }
        }

        Set(TileKind.Bed, BedTiles);
        Set(TileKind.Kitchen, KitchenTiles);
        Set(TileKind.Larder, LarderTiles);
        Set(TileKind.Bunk, BunkTiles);
        Set(TileKind.Post, PostTiles);
        Set(TileKind.Rock, InternalRockTiles);
        _tiles[Gate.X, Gate.Y] = TileKind.Gate;
    }

    public static GridPoint[] BedTiles =>
    [
        new(2, 1), new(5, 1), new(2, 3), new(5, 3),
        new(2, 5), new(5, 5), new(2, 7), new(5, 7),
    ];

    public static GridPoint[] KitchenTiles => [new(10, 7), new(11, 7)];

    public static GridPoint[] LarderTiles => [new(14, 7), new(15, 7)];

    public static GridPoint[] BunkTiles => [new(20, 3), new(21, 3), new(21, 4), new(22, 4)];

    public static GridPoint[] PostTiles => [new(8, 12), new(9, 12), new(8, 13), new(9, 13)];

    public static GridPoint[] InternalRockTiles =>
    [
        new(9, 4), new(9, 5), new(18, 4),
        new(18, 5), new(9, 10), new(18, 10),
    ];

    public static GridPoint Gate => new(27, 13);

    public TileKind this[GridPoint point] => _tiles[point.X, point.Y];

    public bool IsPassable(GridPoint point)
    {
        return IsInside(point) && this[point] != TileKind.Rock;
    }

    public static bool IsInside(GridPoint point)
    {
        return point.X is >= 0 and < PrototypeTuning.MapWidth &&
            point.Y is >= 0 and < PrototypeTuning.MapHeight;
    }

    public int? Distance(
        GridPoint start,
        GridPoint target,
        IReadOnlySet<GridPoint> forbidden)
    {
        if (start == target)
        {
            return 0;
        }

        var queue = new Queue<GridPoint>();
        var distances = new int[PrototypeTuning.MapWidth, PrototypeTuning.MapHeight];
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                distances[x, y] = -1;
            }
        }
        distances[start.X, start.Y] = 0;
        queue.Enqueue(start);

        while (queue.TryDequeue(out var current))
        {
            foreach (var next in Neighbors(current))
            {
                if (!IsPassable(next) ||
                    (forbidden.Contains(next) && next != start) ||
                    distances[next.X, next.Y] >= 0)
                {
                    continue;
                }

                var distance = distances[current.X, current.Y] + 1;
                if (next == target)
                {
                    return distance;
                }

                distances[next.X, next.Y] = distance;
                queue.Enqueue(next);
            }
        }

        return null;
    }

    public GridPoint? NextStep(
        GridPoint start,
        GridPoint target,
        IReadOnlySet<GridPoint> forbidden)
    {
        if (start == target)
        {
            return start;
        }

        var queue = new Queue<GridPoint>();
        var visited = new bool[PrototypeTuning.MapWidth, PrototypeTuning.MapHeight];
        var previous = new GridPoint?[PrototypeTuning.MapWidth, PrototypeTuning.MapHeight];
        visited[start.X, start.Y] = true;
        queue.Enqueue(start);

        while (queue.TryDequeue(out var current))
        {
            foreach (var next in Neighbors(current))
            {
                if (!IsPassable(next) ||
                    (forbidden.Contains(next) && next != start) ||
                    visited[next.X, next.Y])
                {
                    continue;
                }

                visited[next.X, next.Y] = true;
                previous[next.X, next.Y] = current;
                if (next == target)
                {
                    var step = target;
                    while (previous[step.X, step.Y] is { } predecessor &&
                           predecessor != start)
                    {
                        step = predecessor;
                    }

                    return step;
                }

                queue.Enqueue(next);
            }
        }

        return null;
    }

    public IEnumerable<GridPoint> PassableTiles()
    {
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                var point = new GridPoint(x, y);
                if (IsPassable(point))
                {
                    yield return point;
                }
            }
        }
    }

    public static Dictionary<ZoneKind, SortedSet<GridPoint>> CreateDefaultZones(
        PrototypeMap map)
    {
        var zones = Enum.GetValues<ZoneKind>()
            .ToDictionary(kind => kind, _ => new SortedSet<GridPoint>());
        PaintRectangle(map, zones[ZoneKind.Farm], new(1, 1), new(6, 7));
        PaintRectangle(map, zones[ZoneKind.Kitchen], new(9, 6), new(12, 8));
        PaintRectangle(map, zones[ZoneKind.Larder], new(13, 6), new(16, 8));
        PaintRectangle(map, zones[ZoneKind.Quarters], new(19, 2), new(23, 5));
        return zones;
    }

    public static IEnumerable<GridPoint> Neighbors(GridPoint point)
    {
        foreach (var offset in NeighborOffsets)
        {
            yield return new GridPoint(point.X + offset.X, point.Y + offset.Y);
        }
    }

    private static void PaintRectangle(
        PrototypeMap map,
        SortedSet<GridPoint> zone,
        GridPoint start,
        GridPoint end)
    {
        for (var y = start.Y; y <= end.Y; y++)
        {
            for (var x = start.X; x <= end.X; x++)
            {
                var point = new GridPoint(x, y);
                if (map.IsPassable(point) && map[point] != TileKind.Gate)
                {
                    zone.Add(point);
                }
            }
        }
    }

    private void Set(TileKind kind, IEnumerable<GridPoint> points)
    {
        foreach (var point in points)
        {
            _tiles[point.X, point.Y] = kind;
        }
    }
}
