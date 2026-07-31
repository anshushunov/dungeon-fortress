namespace DungeonFortress.Simulation;

/// <summary>
/// Where a room comes from, in the sense
/// <see href="../../docs/decisions/0013-what-is-a-room.md">ADR 0013</see> decided
/// it: variant C, "the player marks intent".
///
/// A room is <b>one connected patch of one zone</b>, plus whatever map features
/// stand inside it. Nothing new is asked of the player and nothing new is asked
/// of the map: the two things that already existed — a painted zone and a built
/// object — are joined into an entity that has an identity, a purpose, an extent,
/// contents, a state and a verdict on whether it is finished.
///
/// The entity is derived, not stored. It is recomputed from the zones and the map
/// every time a snapshot is taken, so it cannot drift out of step with them, and
/// no command creates or destroys a room directly. That is the whole of what
/// variant C costs, and it is why the id below is derived too — see
/// <see cref="Identify"/>.
///
/// The point of it is stated in the ADR and is worth repeating where the code is:
/// «Если игрок забыл покрасить зону, столб стоит, работы <c>Drill</c> не
/// появляются, <b>и игра об этом молчит</b>». After this type the game does not
/// stay silent — an unfinished room says so in
/// <see cref="PrototypeRoomSnapshot.StatusCode"/>, and the presentation layer
/// draws that reading rather than a shade of floor.
///
/// Connectivity is 4-connected, the same neighbourhood everything else in the
/// simulation walks on. Two separate patches of the same zone are therefore two
/// rooms, which is exactly the complaint Issue #52 opens with: zones stopped
/// being readable «как только зон стало больше одной».
/// </summary>
public static class PrototypeRooms
{
    /// <summary>
    /// The feature a room of this purpose must cover to work at all, straight off
    /// contract table 12.3. <c>null</c> for the three zones that require nothing:
    /// <see cref="ZoneKind.Watch"/>, <see cref="ZoneKind.Forbidden"/> and
    /// <see cref="ZoneKind.MaterialStockpile"/>.
    /// </summary>
    public static TileKind? RequiredFeature(ZoneKind purpose) => purpose switch
    {
        ZoneKind.Farm => TileKind.Bed,
        ZoneKind.Kitchen => TileKind.Kitchen,
        ZoneKind.Larder => TileKind.Larder,
        ZoneKind.Quarters => TileKind.Bunk,
        ZoneKind.TrainingGround => TileKind.Post,
        _ => null,
    };

    /// <summary>
    /// The work a finished room of this purpose lets exist, again off table 12.3.
    /// It is asked for one reason: a room that has everything and still produces
    /// nothing because the priority of its work is 0 is the second half of "the
    /// game says nothing", and the player deserves the same answer there.
    ///
    /// <see cref="ZoneKind.Larder"/> and <see cref="ZoneKind.Forbidden"/> enable
    /// no single job — the larder is storage and a place to eat, the forbidden
    /// paint is a refusal — so neither can be blocked by a priority.
    /// </summary>
    public static JobKind? EnabledWork(ZoneKind purpose) => purpose switch
    {
        ZoneKind.Farm => JobKind.Harvest,
        ZoneKind.Kitchen => JobKind.Cook,
        ZoneKind.Quarters => JobKind.Rest,
        ZoneKind.TrainingGround => JobKind.Drill,
        ZoneKind.Watch => JobKind.Watch,
        ZoneKind.MaterialStockpile => JobKind.Haul,
        _ => null,
    };

    /// <summary>
    /// The map features a room counts as its contents. The gate is deliberately
    /// absent: no zone may cover it (contract 4.4), so a gate can never be inside
    /// a room. Plain floor is not an object.
    /// </summary>
    private static readonly TileKind[] Furniture =
    [
        TileKind.Bed,
        TileKind.Kitchen,
        TileKind.Larder,
        TileKind.Bunk,
        TileKind.Post,
    ];

    /// <summary>
    /// Every room the current zones and map add up to, ordered by purpose and then
    /// by anchor so the canonical document never depends on the order the patches
    /// happened to be walked in.
    /// </summary>
    internal static IReadOnlyList<PrototypeRoomSnapshot> Derive(
        PrototypeMap map,
        IReadOnlyDictionary<ZoneKind, SortedSet<GridPoint>> zones,
        IReadOnlyDictionary<JobKind, int> priorities)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(priorities);

        var forbidden = zones[ZoneKind.Forbidden];
        var rooms = new List<PrototypeRoomSnapshot>();
        foreach (var purpose in Enum.GetValues<ZoneKind>().Order())
        {
            foreach (var patch in Patches(zones[purpose]))
            {
                rooms.Add(Describe(map, purpose, patch, forbidden, priorities));
            }
        }

        return rooms;
    }

    /// <summary>
    /// One zone's tiles split into 4-connected patches, each patch in reading
    /// order and the patches themselves ordered by their anchor.
    /// </summary>
    private static IEnumerable<List<GridPoint>> Patches(SortedSet<GridPoint> tiles)
    {
        var remaining = new HashSet<GridPoint>(tiles);
        // `tiles` is a SortedSet in reading order, so the seed of each patch is
        // the first of its cells the reading order reaches. That makes the order
        // of the patches themselves reading order too, without a second sort.
        foreach (var seed in tiles)
        {
            if (!remaining.Remove(seed))
            {
                continue;
            }

            var patch = new SortedSet<GridPoint> { seed };
            var queue = new Queue<GridPoint>();
            queue.Enqueue(seed);
            while (queue.TryDequeue(out var current))
            {
                foreach (var next in PrototypeMap.Neighbors(current))
                {
                    if (remaining.Remove(next))
                    {
                        patch.Add(next);
                        queue.Enqueue(next);
                    }
                }
            }

            yield return [.. patch];
        }
    }

    private static PrototypeRoomSnapshot Describe(
        PrototypeMap map,
        ZoneKind purpose,
        List<GridPoint> patch,
        SortedSet<GridPoint> forbidden,
        IReadOnlyDictionary<JobKind, int> priorities)
    {
        var contents = patch
            .Where(tile => Furniture.Contains(map[tile]))
            .Select(tile => new PrototypeRoomObjectSnapshot(tile, map[tile]))
            .ToArray();
        var required = RequiredFeature(purpose);
        var complete = required is not { } feature ||
            contents.Any(item => item.Kind == feature);

        return new PrototypeRoomSnapshot(
            Identify(purpose, patch[0]),
            purpose,
            patch,
            contents,
            Status(purpose, patch, complete, forbidden, priorities),
            complete);
    }

    /// <summary>
    /// The state of a room, as a ladder in the same shape and the same spirit as
    /// the excavation, construction and stockpile ladders: the first rung that
    /// answers wins, and the code names the reason rather than a colour.
    ///
    /// <list type="number">
    /// <item><c>room_forbidden</c> — every one of its tiles is painted
    /// <see cref="ZoneKind.Forbidden"/>, so nobody of the domain may set foot in
    /// it and nothing it holds can be used. Asked first because it beats every
    /// other reason: a complete gym nobody may enter is not working either. A
    /// <see cref="ZoneKind.Forbidden"/> room is exempt for the obvious reason —
    /// it is the paint doing the forbidding.</item>
    /// <item><c>room_missing_feature</c> — the zone is painted and the object it
    /// needs is not inside it. This is the case ADR 0013 is about.</item>
    /// <item><c>room_blocked_priority</c> — it has everything and the priority of
    /// the work it enables is 0, so the work exists nowhere.</item>
    /// <item><c>room_ready</c> — it works.</item>
    /// </list>
    /// </summary>
    private static string Status(
        ZoneKind purpose,
        List<GridPoint> patch,
        bool complete,
        SortedSet<GridPoint> forbidden,
        IReadOnlyDictionary<JobKind, int> priorities)
    {
        if (purpose != ZoneKind.Forbidden && patch.All(forbidden.Contains))
        {
            return "room_forbidden";
        }

        if (!complete)
        {
            return "room_missing_feature";
        }

        if (EnabledWork(purpose) is { } work && priorities[work] == 0)
        {
            return "room_blocked_priority";
        }

        return "room_ready";
    }

    /// <summary>
    /// The identity of a derived entity, derived: the purpose plus the anchor —
    /// the first tile of the patch in reading order.
    ///
    /// A counter would look more like an identity and would be a worse one. A room
    /// is recomputed from the zones on every snapshot, so a counter would have to
    /// become canonical state, survive erasing and repainting, and answer "is this
    /// the same room?" for a patch the player split in two. None of those questions
    /// has an answer in variant C, where a room is exactly the patch that is
    /// painted right now.
    ///
    /// So the id says what it can honestly say: this purpose, anchored here. It is
    /// stable while the anchor stays painted, it is unique because two patches of
    /// one zone cannot share a tile, and nothing in the simulation carries state
    /// across a change of it.
    /// </summary>
    public static string Identify(ZoneKind purpose, GridPoint anchor)
    {
        var name = purpose.ToString();
        return $"{char.ToLowerInvariant(name[0])}{name[1..]}@{anchor.X},{anchor.Y}";
    }
}
