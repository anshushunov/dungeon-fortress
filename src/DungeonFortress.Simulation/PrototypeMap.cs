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

    private static readonly GridPoint[] AuthoredBeds = PrototypeLayout.Read('m');
    private static readonly GridPoint[] AuthoredKitchens = PrototypeLayout.Read('K');
    private static readonly GridPoint[] AuthoredLarders = PrototypeLayout.Read('L');
    private static readonly GridPoint[] AuthoredBunks = PrototypeLayout.Read('q');
    private static readonly GridPoint[] AuthoredPosts = PrototypeLayout.Read('T');
    private static readonly GridPoint[] AuthoredPocket = PrototypeLayout.Read('d');
    private static readonly GridPoint[] AuthoredInternalRock =
    [
        .. PrototypeLayout.Read('#')
            .Concat(AuthoredPocket)
            .Where(point => !IsBoundary(point))
            .Order(),
    ];

    private static readonly HashSet<GridPoint> InitiallyDiggable = [.. AuthoredInternalRock];
    private static readonly GridPoint AuthoredGate = PrototypeLayout.Read('G').Single();

    /// <summary>
    /// The terrain of tick 0, read straight off <see cref="PrototypeLayout"/>.
    /// Nothing here decides anything: every question about which tile is what is
    /// answered by the picture, so the picture is the only thing a reader has to
    /// check.
    /// </summary>
    public PrototypeMap()
    {
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                _tiles[x, y] = PrototypeLayout.Rows[y][x] switch
                {
                    '#' or 'd' => TileKind.Rock,
                    'm' => TileKind.Bed,
                    'K' => TileKind.Kitchen,
                    'L' => TileKind.Larder,
                    'q' => TileKind.Bunk,
                    'T' => TileKind.Post,
                    'G' => TileKind.Gate,
                    _ => TileKind.Floor,
                };
            }
        }
    }

    public static GridPoint[] BedTiles => [.. AuthoredBeds];

    public static GridPoint[] KitchenTiles => [.. AuthoredKitchens];

    public static GridPoint[] LarderTiles => [.. AuthoredLarders];

    public static GridPoint[] BunkTiles => [.. AuthoredBunks];

    /// <summary>
    /// The training posts the map fixture authors. It is the starting gym, not
    /// the set of posts that can exist: <see cref="PostTiles"/> is the runtime
    /// authority once the player starts building.
    /// </summary>
    public static GridPoint[] AuthoredPostTiles => [.. AuthoredPosts];

    /// <summary>
    /// Every rock tile inside the border. Since Issue #117 that is the whole
    /// masonry of the dungeon rather than six pillars and a pocket, so the
    /// player can now open a wall between two chambers instead of only quarrying
    /// a corner. Digging the border is still refused
    /// (<see cref="IsBoundary"/>): it holds the dungeon in.
    /// </summary>
    public static GridPoint[] InternalRockTiles => [.. AuthoredInternalRock];

    /// <summary>
    /// The excavation playground of Issue #24, in the top-right corner behind
    /// the quarters. The shipped demo fixtures name these six tiles by
    /// coordinate, which is why the pocket survived the layout change unmoved
    /// together with the niche at <c>x = 24</c> that a digger works from.
    ///
    /// It is no longer the only diggable rock: see
    /// <see cref="InternalRockTiles"/>. The measured claim it used to carry —
    /// that the three shipped scenarios walk identically with and without the
    /// pocket — was a property of a map made of open floor and is not restated.
    /// </summary>
    public static GridPoint[] DigPocketTiles => [.. AuthoredPocket];

    public static GridPoint Gate => AuthoredGate;

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
            InitiallyDiggable.Contains(point);
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
}
