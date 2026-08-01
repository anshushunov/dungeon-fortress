using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>How a dig mark reads on the map.</summary>
public enum DigMarkAccent
{
    /// <summary>Marked; nobody is on it yet, or somebody is walking to it.</summary>
    Waiting,
    InProgress,
    BlockedByPriority,
    Unreachable,
}

/// <summary>How a training-post blueprint reads on the map.</summary>
public enum BlueprintAccent
{
    /// <summary>Material exists and is free; the site is waiting for a carrier.</summary>
    WaitingForCarrier,
    /// <summary>No stone the site could be given, or all of it is booked elsewhere.</summary>
    WaitingForMaterial,
    InProgress,
    BlockedByPriority,
    Unreachable,
}

/// <summary>
/// How a room reads on the map: the four states of
/// <see href="../../docs/decisions/0013-what-is-a-room.md">ADR 0013</see>, in the
/// same shape as the three ladders above it.
/// </summary>
public enum RoomAccent
{
    /// <summary>It has what it needs and its work is switched on.</summary>
    Ready,

    /// <summary>The zone is painted and the object it needs is not inside it.</summary>
    Unfinished,

    /// <summary>Complete, and the priority of the work it enables is 0.</summary>
    BlockedByPriority,

    /// <summary>Every one of its tiles is Forbidden, so nobody may set foot in it.</summary>
    Unreachable,
}

/// <summary>How a material stockpile cell reads on the map.</summary>
public enum StockpileCellAccent
{
    Room,
    Full,
    Incoming,
    Unreachable,
}

/// <summary>
/// Which of a handful of readings a mark on the map has. It is the thing the
/// colour is chosen from, and it lives here rather than in the Godot adapter for
/// one reason: <c>Main.cs</c> is not built by the "Pure .NET" CI job, so a
/// reading decided there is decided where nothing can check it.
///
/// That is not hypothetical. Issue #58 drew a mark accepted while paused with the
/// colour of a designation waiting for a worker, and a blueprint accepted while
/// paused with the colour of a site whose material is on the way. Both were wrong
/// in the most ordinary session — a mark made with <c>Dig</c> priority 0, a
/// blueprint marked before any stone had been dug — so unpausing changed the
/// colour of every one of them. The set-of-cells tests could not see it, because
/// the set of cells was right and only the accent was wrong.
///
/// The two halves are therefore stated separately and compared against each
/// other by <c>MapAccentTests</c>, which runs the real simulation across the very
/// tick that applies the command:
///
/// <list type="bullet">
/// <item><see cref="Dig(MapProjection, PrototypeDigDesignationSnapshot)"/>,
/// <see cref="Blueprint"/> and <see cref="Stockpile"/> read the world's own
/// <c>statusCode</c> for a mark the world already holds;</item>
/// <item><see cref="PendingDig"/>, <see cref="PendingBlueprint"/> and
/// <see cref="PendingStockpile"/> answer the same question for a mark whose tick
/// has not run.</item>
/// </list>
///
/// Both halves take the projection, and that is the whole boundary rule:
///
/// <para><b>The projection answers what follows from published facts folded
/// through it; the world answers what needs a tick to run.</b></para>
///
/// The rule is stated as a rule on purpose. It was first written as a list of
/// exceptions, and the list turned out to be incomplete four times running: the
/// colour of a fresh blueprint, <c>dig_blocked_priority</c>, a priority waiting
/// over a mark that was also waiting, the same priority over marks the world
/// already held, and a <c>Forbidden</c> paint over them. Each was one more
/// instance of one class.
///
/// So instead of a list of exceptions, here is the list of <em>inputs</em>. Every
/// fact the three ladders in <c>PrototypeWorld</c> ask about appears below, and
/// each one is folded, impossible to have waiting, or the world's to answer.
///
/// <list type="table">
/// <listheader><term>Fact (where the world asks it)</term><description>Verdict</description></listheader>
///
/// <item><term><c>priorities[Dig]</c>, <c>[Build]</c>, <c>[Haul]</c> — first rung
/// of the dig ladder, first and next-to-last of the construction one</term>
/// <description><b>Folded.</b> <c>set_priority</c> is folded by
/// <c>MapProjection.Of</c> and read through <c>MapProjection.Priority</c>. Pinned
/// by <c>A_dig_mark_reads_the_priority_the_same_moment_accepted</c>,
/// <c>A_blueprint_reads_...</c> and the two
/// <c>An_old_..._and_a_new_one_read_the_same_when_a_priority_is_waiting</c>
/// cases.</description></item>
///
/// <item><term><c>Forbidden</c> over a construction site or a stockpile cell —
/// <c>IsBuildSiteWorkable</c>, <c>ToStockpileSnapshot</c></term>
/// <description><b>Folded.</b> <c>zone_paint</c> and <c>zone_erase</c> are folded
/// like any other marking and read through <c>MapProjection.IsInZone</c>, which
/// is why neither <c>PredictBlueprint</c> nor <see cref="Stockpile"/> uses the
/// <c>Reachable</c> field of the snapshot. Pinned by the two
/// <c>..._read_the_same_when_forbidden_is_waiting</c> cases.</description></item>
///
/// <item><term><c>Forbidden</c> over a tile marked for digging</term>
/// <description><b>Impossible while waiting.</b> A zone command over rock is
/// rejected before any world exists: <c>PrototypeCommandValidator</c> and
/// <c>PrototypeWorld.ValidateZoneTiles</c> refuse a tile that is neither passable
/// nor diggable-into-passable, and the live map refuses it again on its
/// tick.</description></item>
///
/// <item><term>buildable floor under a site — <c>IsBuildableFloor</c>; passable
/// ground under a stockpile cell — <c>IsPassable</c></term>
/// <description><b>Impossible while waiting.</b> The only mutations of the map are
/// rock → floor and floor → post, and both need a tick; no command moves them. A
/// site or a cell that exists therefore stands on ground that stays legal for as
/// long as it exists.</description></item>
///
/// <item><term>reachability of rock — <c>IsDigReachable</c>: has the tile any
/// orthogonal neighbour that is passable, not the gate and not
/// <c>Forbidden</c></term>
/// <description><b>The world's.</b> It is a question about the neighbours of a
/// tile, and answering it here would put map topology on both sides of the seam
/// ADR 0011 draws. A <c>Forbidden</c> paint over a <em>neighbouring floor tile</em>
/// can therefore change a dig mark's reading on the applying tick, and that is
/// deliberate.</description></item>
///
/// <item><term>who volunteered and whether work started — <c>job.ReservedBy</c>,
/// <c>job.ProgressTicks</c></term>
/// <description><b>The world's.</b> Job generation and matching happen inside the
/// tick. Read from the published record for the frame being drawn; the tick may
/// change them, and when it does, the world has done something.</description></item>
///
/// <item><term>material on a site — <c>Delivered</c>, <c>IncomingReserved</c> —
/// and stone in the world — <c>AvailableStoneForSites</c>, over
/// <c>stocks.looseStone</c>, <c>stocks.storedStone</c> and the booked part of
/// <c>jobs</c></term>
/// <description><b>The world's.</b> No command delivers, picks up or books stone.
/// Two commands move material as a <em>side effect</em> — <c>zone_erase</c> spills
/// a stockpile cell, <c>build_cancel</c> spills a site — and the projection
/// deliberately does not model side effects, only geometry: predicting where the
/// world puts material and which reservations survive is the rule this layer must
/// not own. So withdrawing a blueprint that holds stone can change another site's
/// material reading on the applying tick.</description></item>
///
/// <item><term><c>StoneAnywhere</c> — the world's split between
/// <c>build_no_stone</c> and <c>build_stone_reserved</c></term>
/// <description><b>Never reaches a reading.</b> Both statuses are the same accent,
/// so the fact cannot change what is drawn.</description></item>
///
/// <item><term>tuning constants — <c>BuildStoneCost</c>,
/// <c>StockpileCellCapacity</c></term>
/// <description><b>Impossible while waiting.</b> No command changes tuning; they
/// are compile-time values, and <c>capacity</c> is published on the cell
/// anyway.</description></item>
/// </list>
///
/// Repeating the world's ladders is a cost worth naming, and it is pinned:
/// <c>MapAccentTests</c> sweeps a real session comparing every prediction against
/// the world's own <c>statusCode</c> on every tick where nothing is waiting, so a
/// rung that stops matching fails in CI on the pull request.
/// </summary>
public static class MapAccents
{
    /// <summary>
    /// How a dig mark the world already holds reads.
    ///
    /// The world's <c>statusCode</c> is the answer, with one correction: it was
    /// computed under the priority the world holds, and the player may have
    /// changed that priority in this same paused moment. The priority is the
    /// first rung of the world's ladder, so it overrides everything below it and
    /// the correction is exact in both directions.
    ///
    /// Without it, a frame could hold two dig marks of different colours making
    /// opposite claims about the same fact: the mark accepted a second ago
    /// already knew digging was off, the one from a minute ago did not.
    /// </summary>
    public static DigMarkAccent Dig(MapProjection view, PrototypeDigDesignationSnapshot designation)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(designation);
        if (view.Priority(JobKind.Dig) == 0)
        {
            return DigMarkAccent.BlockedByPriority;
        }

        // Digging is being switched back on, so this mark is about to leave the
        // grey. Where it lands is the next rung down, and the world publishes it:
        // a designation carries its own reachability.
        if (designation.StatusCode == "dig_blocked_priority")
        {
            return designation.Reachable ? DigMarkAccent.Waiting : DigMarkAccent.Unreachable;
        }

        return DigFromStatus(designation.StatusCode);
    }

    /// <summary>
    /// A dig mark whose tick has not run yet.
    ///
    /// <c>dig_blocked_priority</c> is the world's first branch and is a pure
    /// function of <c>priorities[Dig]</c> — so a mark made with digging switched
    /// off is grey immediately instead of turning grey the moment time moves.
    ///
    /// The priority is read from the projection and not from the snapshot,
    /// because switching digging off and marking rock in the same paused moment
    /// is one gesture to the player.
    ///
    /// Below the priority the world asks whether anyone can reach the rock, and
    /// that this side of the seam may not answer — see the boundary note on the
    /// class.
    /// </summary>
    public static DigMarkAccent PendingDig(MapProjection view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Priority(JobKind.Dig) == 0
            ? DigMarkAccent.BlockedByPriority
            : DigMarkAccent.Waiting;
    }

    private static DigMarkAccent DigFromStatus(string statusCode) => statusCode switch
    {
        "dig_in_progress" => DigMarkAccent.InProgress,
        "dig_unreachable" => DigMarkAccent.Unreachable,
        "dig_blocked_priority" => DigMarkAccent.BlockedByPriority,
        // dig_waiting and dig_reserved read the same: the mark is placed and the
        // crew is deciding. Who walks where is drawn as a line, not as a colour.
        _ => DigMarkAccent.Waiting,
    };

    /// <summary>
    /// How a blueprint the world already holds reads.
    ///
    /// It always walks <see cref="Predict"/> rather than taking the world's
    /// <c>statusCode</c> when something looks like it is waiting. Deciding *when*
    /// to correct was itself a source of defects: a gate on "is a priority
    /// waiting" left a waiting <c>Forbidden</c> paint uncorrected, and any such
    /// gate has to be kept in step with the ladder by hand. Walking the ladder
    /// unconditionally cannot fall out of step, and
    /// <c>MapAccentTests</c> pins it against the world's own word on every tick
    /// of a whole session where nothing is waiting.
    /// </summary>
    public static BlueprintAccent Blueprint(MapProjection view, PrototypeBuildSiteSnapshot site) =>
        PredictBlueprint(view, site);

    /// <summary>
    /// What the ladder says about a site the world already holds, asked without
    /// the shortcut. With no priority waiting this must equal the world's own
    /// <c>statusCode</c>, and <c>MapAccentTests</c> sweeps a whole session
    /// checking exactly that — which is what makes repeating the ladder safe.
    /// </summary>
    public static BlueprintAccent PredictBlueprint(MapProjection view, PrototypeBuildSiteSnapshot site)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(site);
        // Not site.Reachable: that was computed under the zones the world holds,
        // and a Forbidden paint or erase accepted in this same paused moment has
        // not reached them yet. The floor half of the world's workability test is
        // guaranteed for a site that exists, so the zone is the whole question.
        return Predict(
            view,
            !view.IsInZone(ZoneKind.Forbidden, site.Tile),
            site.Delivered,
            site.IncomingReserved,
            site.ReservedBy is not null,
            site.ProgressTicks > 0);
    }

    /// <summary>
    /// A blueprint whose tick has not run yet. A fresh site always has nothing
    /// delivered, nothing on the way and nobody on it — the adapter already draws
    /// it that way — so it is <see cref="Predict"/> with those three facts fixed.
    /// </summary>
    public static BlueprintAccent PendingBlueprint(MapProjection view, GridPoint tile)
    {
        ArgumentNullException.ThrowIfNull(view);
        // A site is workable when it is buildable floor nobody has forbidden. The
        // floor half is guaranteed: a blueprint the world would refuse never
        // becomes a command, so only the zone can still take the site away, and
        // the zone is folded through the projection.
        return Predict(
            view,
            !view.IsInZone(ZoneKind.Forbidden, tile),
            delivered: 0,
            incomingReserved: 0,
            reserved: false,
            inProgress: false);
    }

    /// <summary>
    /// The world's construction ladder, rung for rung, over facts the snapshot
    /// publishes. It is the one place the ladder is repeated, and
    /// <c>MapAccentTests</c> sweeps a whole session comparing it against the
    /// world's own <c>statusCode</c>, so a rung that stops matching fails in CI.
    /// </summary>
    private static BlueprintAccent Predict(
        MapProjection view,
        bool workable,
        int delivered,
        int incomingReserved,
        bool reserved,
        bool inProgress)
    {
        if (view.Priority(JobKind.Build) == 0)
        {
            return BlueprintAccent.BlockedByPriority;
        }

        if (!workable)
        {
            return BlueprintAccent.Unreachable;
        }

        if (inProgress)
        {
            return BlueprintAccent.InProgress;
        }

        // Somebody is on it, the material is complete, or the rest is on its way:
        // three different sentences in the inspector, one reading on the map.
        if (reserved ||
            delivered >= PrototypeTuning.BuildStoneCost ||
            incomingReserved > 0)
        {
            return BlueprintAccent.WaitingForCarrier;
        }

        if (view.Priority(JobKind.Haul) == 0)
        {
            return BlueprintAccent.BlockedByPriority;
        }

        return FreeStoneForSites(view.State) > 0
            ? BlueprintAccent.WaitingForCarrier
            : BlueprintAccent.WaitingForMaterial;
    }

    /// <summary>
    /// The world's own word about a blueprint, as an accent. Nothing draws with
    /// it: it exists so that <c>MapAccentTests</c> can hold
    /// <see cref="Predict"/> against the simulation.
    /// </summary>
    public static BlueprintAccent BlueprintReadingOfStatus(string statusCode) => statusCode switch
    {
        "build_in_progress" => BlueprintAccent.InProgress,
        "build_unreachable" => BlueprintAccent.Unreachable,
        // "no work is being taken" reads the same whether it is building or
        // carrying that is switched off.
        "build_blocked_priority" or "build_haul_blocked" => BlueprintAccent.BlockedByPriority,
        "build_no_stone" or "build_stone_reserved" => BlueprintAccent.WaitingForMaterial,
        _ => BlueprintAccent.WaitingForCarrier,
    };

    /// <summary>
    /// How a stockpile cell the world already holds reads.
    ///
    /// The stockpile ladder asks about no priority, but it does ask about
    /// <c>Forbidden</c> — <c>reachable = IsPassable &amp;&amp; !Forbidden</c> —
    /// so a paint or an erase accepted in this same paused moment decides it, and
    /// the whole ladder is walked over the cell's published facts for the same
    /// reason the construction one is. The passability half is guaranteed: the
    /// world only lets the zone be painted on plain floor.
    /// </summary>
    public static StockpileCellAccent Stockpile(
        MapProjection view,
        PrototypeStockpileCellSnapshot cell)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(cell);
        return PredictStockpile(view, cell.Position, cell.Stored, cell.IncomingReserved, cell.Capacity);
    }

    private static StockpileCellAccent PredictStockpile(
        MapProjection view,
        GridPoint position,
        int stored,
        int incomingReserved,
        int capacity)
    {
        if (view.IsInZone(ZoneKind.Forbidden, position))
        {
            return StockpileCellAccent.Unreachable;
        }

        if (stored >= capacity)
        {
            return StockpileCellAccent.Full;
        }

        // stockpile_empty and stockpile_partial read the same: there is room. How
        // much is in it is drawn as pips.
        return stored + incomingReserved >= capacity
            ? StockpileCellAccent.Incoming
            : StockpileCellAccent.Room;
    }

    /// <summary>
    /// The world's own word about a stockpile cell, as an accent. Test-facing, in
    /// the same way as <see cref="BlueprintReadingOfStatus"/>.
    /// </summary>
    public static StockpileCellAccent StockpileReadingOfStatus(string statusCode) => statusCode switch
    {
        "stockpile_unreachable" => StockpileCellAccent.Unreachable,
        "stockpile_full" => StockpileCellAccent.Full,
        "stockpile_incoming" => StockpileCellAccent.Incoming,
        _ => StockpileCellAccent.Room,
    };

    /// <summary>
    /// A stockpile cell whose tick has not run yet: <see cref="PredictStockpile"/>
    /// with "empty and nothing booked" fixed, which is what the world creates.
    /// </summary>
    public static StockpileCellAccent PendingStockpile(MapProjection view, GridPoint tile)
    {
        ArgumentNullException.ThrowIfNull(view);
        return PredictStockpile(
            view,
            tile,
            stored: 0,
            incomingReserved: 0,
            capacity: PrototypeTuning.StockpileCellCapacity);
    }

    /// <summary>
    /// How a room the world already holds reads.
    ///
    /// Like <see cref="Blueprint"/> it always walks the ladder rather than taking
    /// the world's <c>statusCode</c>, and for the same reason: both facts the
    /// world's first two rungs ask about are folded by the projection, so a
    /// <c>Forbidden</c> paint or a priority accepted in this same paused moment
    /// decides the reading. Deciding *when* to correct was itself a source of
    /// defects on Issue #58; walking the ladder unconditionally cannot fall out of
    /// step, and <c>MapAccentTests</c> sweeps a whole session comparing it against
    /// the world's own word.
    ///
    /// The two rungs it cannot walk are the two it does not need to: whether the
    /// room exists at all and whether it is complete. Both need connectivity and
    /// contents to be recomputed, which is the simulation's rule and not a fold of
    /// published facts — so <see cref="PrototypeRoomSnapshot.Complete"/> is read
    /// from the room, and a zone painted in a paused moment has no room yet. That
    /// is why the adapter still draws a per-cell outline for a pending paint: the
    /// immediate feedback Issue #58 asked for is kept, and the room appears when
    /// the tick that creates it runs.
    /// </summary>
    public static RoomAccent Room(MapProjection view, PrototypeRoomSnapshot room)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(room);

        // Not the world's `room_forbidden`: that was computed under the zones the
        // world holds, and a Forbidden paint or erase accepted in this same paused
        // moment has not reached them yet.
        if (room.Purpose != ZoneKind.Forbidden &&
            room.Perimeter.All(tile => view.IsInZone(ZoneKind.Forbidden, tile)))
        {
            return RoomAccent.Unreachable;
        }

        if (!room.Complete)
        {
            return RoomAccent.Unfinished;
        }

        if (PrototypeRooms.EnabledWork(room.Purpose) is { } work && view.Priority(work) == 0)
        {
            return RoomAccent.BlockedByPriority;
        }

        return RoomAccent.Ready;
    }

    /// <summary>
    /// The world's own word about a room, as an accent. Nothing draws with it: it
    /// exists so that <c>MapAccentTests</c> can hold <see cref="Room"/> against the
    /// simulation, in the same way as <see cref="BlueprintReadingOfStatus"/>.
    /// </summary>
    public static RoomAccent RoomReadingOfStatus(string statusCode) => statusCode switch
    {
        "room_forbidden" => RoomAccent.Unreachable,
        "room_missing_feature" => RoomAccent.Unfinished,
        "room_blocked_priority" => RoomAccent.BlockedByPriority,
        _ => RoomAccent.Ready,
    };

    /// <summary>
    /// Stone a construction site could still be given: loose piles and stockpiled
    /// blocks that no live haul has already booked for somewhere else. Read from
    /// the published stock counters and job list, in the same terms the world
    /// states them.
    /// </summary>
    private static int FreeStoneForSites(PrototypeSnapshot state)
    {
        var booked = state.Jobs
            .Where(job =>
                job.Kind == JobKind.Haul &&
                job.Resource == ResourceKind.Stone &&
                job.ReservedBy is not null)
            .Sum(job => job.StoreReserved);
        return state.Stocks.LooseStone + state.Stocks.StoredStone - booked;
    }
}
