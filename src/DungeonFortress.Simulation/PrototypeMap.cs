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

    private readonly SortedSet<GridPoint> _excavated = [];
    private readonly SortedSet<GridPoint> _builtPosts = [];

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
        Set(TileKind.Post, AuthoredPostTiles);
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

    /// <summary>
    /// The training posts the map fixture authors. It is the starting gym, not
    /// the set of posts that can exist: <see cref="PostTiles"/> is the runtime
    /// authority once the player starts building.
    /// </summary>
    public static GridPoint[] AuthoredPostTiles => [new(8, 12), new(9, 12), new(8, 13), new(9, 13)];

    public static GridPoint[] InternalRockTiles =>
    [
        new(9, 4), new(9, 5), new(18, 4),
        new(18, 5), new(9, 10), new(18, 10),
        .. DigPocketTiles,
    ];

    /// <summary>
    /// The excavation playground of Issue #24, in the top-right corner. It was
    /// placed away from the tiles the shipped scenarios actually walk: creature
    /// positions, move counts and economy counters of baseline, prepared and
    /// neglected are identical over a full session with and without the pocket.
    /// That is a measured property of those three command logs, not a proof that
    /// no path anywhere changes — a log that sends a creature between the
    /// top-right corner tiles can pick a different equal-length route.
    /// </summary>
    public static GridPoint[] DigPocketTiles =>
    [
        new(25, 1), new(26, 1),
        new(25, 2), new(26, 2),
        new(25, 3), new(26, 3),
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

    /// <summary>
    /// The map border holds the dungeon in. Excavating it would open the fortress
    /// to the outside world, which is a product decision this step does not take.
    /// </summary>
    public static bool IsBoundary(GridPoint point)
    {
        return point.X == 0 || point.Y == 0 ||
            point.X == PrototypeTuning.MapWidth - 1 ||
            point.Y == PrototypeTuning.MapHeight - 1;
    }

    /// <summary>
    /// Diggability is a property of the tile alone. Whether a worker can actually
    /// reach it is a job condition, because reachability changes while digging.
    /// </summary>
    public bool IsDiggable(GridPoint point)
    {
        return IsInside(point) && !IsBoundary(point) && this[point] == TileKind.Rock;
    }

    /// <summary>
    /// Only the initial layout can tell whether a tile could <em>ever</em> be dug.
    /// The command pre-flight uses it; the live map stays the runtime authority.
    /// </summary>
    public static bool IsDiggableInInitialLayout(GridPoint point)
    {
        return IsInside(point) && !IsBoundary(point) &&
            InternalRockTiles.Contains(point);
    }

    /// <summary>
    /// A tile a training-post blueprint may occupy: plain floor inside the map,
    /// whether it was authored as floor or created by digging. Features, the gate,
    /// rock and an already built post are excluded because the tile kind is no
    /// longer <see cref="TileKind.Floor"/>.
    /// </summary>
    public bool IsBuildableFloor(GridPoint point)
    {
        return IsInside(point) && !IsBoundary(point) &&
            this[point] == TileKind.Floor && point != Gate;
    }

    /// <summary>
    /// Only the initial layout can tell whether a tile could <em>ever</em> hold a
    /// blueprint: it is already plain floor, or it is internal rock that digging
    /// can turn into plain floor. The command pre-flight uses it; the live map
    /// stays the runtime authority.
    /// </summary>
    public bool IsBuildableInInitialLayout(GridPoint point)
    {
        return IsBuildableFloor(point) || IsDiggable(point);
    }

    public void Excavate(GridPoint point)
    {
        if (!IsDiggable(point))
        {
            throw new InvalidOperationException(
                $"Tile ({point.X},{point.Y}) is not diggable rock.");
        }

        _tiles[point.X, point.Y] = TileKind.Floor;
        _excavated.Add(point);
    }

    /// <summary>
    /// The second mutation the map allows: plain floor becomes a training post.
    /// Like excavation it is recorded as a delta, so the fixed initial layout plus
    /// the two deltas reproduce the terrain exactly.
    /// </summary>
    public void BuildPost(GridPoint point)
    {
        if (!IsBuildableFloor(point))
        {
            throw new InvalidOperationException(
                $"Tile ({point.X},{point.Y}) is not plain floor.");
        }

        _tiles[point.X, point.Y] = TileKind.Post;
        _builtPosts.Add(point);
    }

    public IReadOnlyCollection<GridPoint> ExcavatedTiles => _excavated;

    public IReadOnlyCollection<GridPoint> BuiltPostTiles => _builtPosts;

    /// <summary>
    /// Every training post on the live map, authored or built. Reading it from
    /// the map rather than from a static list is what lets a built post create the
    /// same <see cref="JobKind.Drill"/> work an authored one does.
    /// </summary>
    public IEnumerable<GridPoint> PostTiles()
    {
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                if (_tiles[x, y] == TileKind.Post)
                {
                    yield return new GridPoint(x, y);
                }
            }
        }
    }

    /// <summary>
    /// Where a blueprint may be placed, published for the same reason
    /// <see cref="StockpileFloorTiles"/> is: the Godot brush filters against this
    /// list instead of re-deriving the rule.
    /// </summary>
    public IEnumerable<GridPoint> BuildFloorTiles()
    {
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                var point = new GridPoint(x, y);
                if (IsBuildableFloor(point))
                {
                    yield return point;
                }
            }
        }
    }

    /// <summary>
    /// Tiles a material stockpile may cover: plain floor that was already floor
    /// when the session began. Map features keep their own purpose, and ground
    /// created by excavation is deliberately excluded until the step that zones
    /// new rooms. This is the single authority the command validator, the world
    /// and the Godot brush all read.
    /// </summary>
    public IEnumerable<GridPoint> StockpileFloorTiles()
    {
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                var point = new GridPoint(x, y);
                if (_tiles[x, y] == TileKind.Floor && !_excavated.Contains(point))
                {
                    yield return point;
                }
            }
        }
    }

    public IEnumerable<GridPoint> RockTiles()
    {
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                var point = new GridPoint(x, y);
                if (_tiles[x, y] == TileKind.Rock)
                {
                    yield return point;
                }
            }
        }
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
