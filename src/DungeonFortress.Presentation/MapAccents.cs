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
/// <item><see cref="Dig(string)"/>, <see cref="Blueprint(string)"/> and
/// <see cref="Stockpile(string)"/> read the world's own <c>statusCode</c>. This
/// is the source of truth for anything the world already holds;</item>
/// <item><see cref="PendingDig"/>, <see cref="PendingBlueprint"/> and
/// <see cref="PendingStockpile"/> answer the same question for a mark whose tick
/// has not run, from published snapshot facts only.</item>
/// </list>
///
/// The second half repeats a decision the world also makes, which is a cost worth
/// naming. It is bounded — the gates are read from <c>priorities</c>, the
/// <c>Forbidden</c> zone and the published stock and job lists, never re-derived
/// from map topology — and it is pinned: if the world's ladder changes and this
/// one does not, the comparison test fails in CI on the pull request.
/// </summary>
public static class MapAccents
{
    public static DigMarkAccent Dig(string statusCode) => statusCode switch
    {
        "dig_in_progress" => DigMarkAccent.InProgress,
        "dig_unreachable" => DigMarkAccent.Unreachable,
        "dig_blocked_priority" => DigMarkAccent.BlockedByPriority,
        // dig_waiting and dig_reserved read the same: the mark is placed and the
        // crew is deciding. Who walks where is drawn as a line, not as a colour.
        _ => DigMarkAccent.Waiting,
    };

    /// <summary>
    /// A dig mark whose tick has not run yet.
    ///
    /// <c>dig_blocked_priority</c> is the world's first branch and is a pure
    /// function of <c>priorities[Dig]</c> — so a mark made with digging switched
    /// off is grey immediately instead of turning grey the moment time moves.
    ///
    /// The priority is read from the projection and not from the snapshot,
    /// because switching digging off and marking rock in the same paused moment
    /// is one gesture to the player. Reading the canonical value made the mark
    /// blink in both directions, which is the same defect one level down.
    ///
    /// <c>dig_unreachable</c> is the one reading this side of the seam may not
    /// have. It asks whether any orthogonal neighbour of the rock is passable,
    /// not the gate and not <c>Forbidden</c>, which is map topology; copying it
    /// here would put a rule on both sides of the boundary ADR 0011 draws. A tile
    /// nobody can reach is therefore drawn as an ordinary waiting mark until the
    /// tick answers, and that is the one place where applying the command changes
    /// a colour. <c>MapAccentTests</c> names the affected tiles rather than
    /// leaving the exception implicit.
    /// </summary>
    public static DigMarkAccent PendingDig(MapProjection view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Priority(JobKind.Dig) == 0
            ? DigMarkAccent.BlockedByPriority
            : DigMarkAccent.Waiting;
    }

    public static BlueprintAccent Blueprint(string statusCode) => statusCode switch
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
    /// A blueprint whose tick has not run yet. A fresh site always has nothing
    /// delivered and nothing on the way — the adapter already draws it that way —
    /// so the reading follows the world's ladder for exactly that case, in the
    /// world's order.
    ///
    /// Every gate is a published fact: the two priorities and membership of the
    /// <c>Forbidden</c> zone, both taken from the projection so that a priority
    /// change or a <c>Forbidden</c> paint accepted in the same paused moment
    /// counts, and the stone the crew could still give a site. Nothing here
    /// re-derives map topology.
    /// </summary>
    public static BlueprintAccent PendingBlueprint(MapProjection view, GridPoint tile)
    {
        ArgumentNullException.ThrowIfNull(view);
        var state = view.State;
        if (view.Priority(JobKind.Build) == 0)
        {
            return BlueprintAccent.BlockedByPriority;
        }

        // A site is workable when it is buildable floor nobody has forbidden. The
        // floor half is guaranteed: a blueprint the world would refuse never
        // becomes a command, so only the zone can still take the site away.
        if (view.IsInZone(ZoneKind.Forbidden, tile))
        {
            return BlueprintAccent.Unreachable;
        }

        if (view.Priority(JobKind.Haul) == 0)
        {
            return BlueprintAccent.BlockedByPriority;
        }

        return FreeStoneForSites(state) > 0
            ? BlueprintAccent.WaitingForCarrier
            : BlueprintAccent.WaitingForMaterial;
    }

    public static StockpileCellAccent Stockpile(string statusCode) => statusCode switch
    {
        "stockpile_unreachable" => StockpileCellAccent.Unreachable,
        "stockpile_full" => StockpileCellAccent.Full,
        "stockpile_incoming" => StockpileCellAccent.Incoming,
        // stockpile_empty and stockpile_partial read the same: there is room. How
        // much is in it is drawn as pips.
        _ => StockpileCellAccent.Room,
    };

    /// <summary>
    /// A stockpile cell whose tick has not run yet. It is empty and nothing is
    /// booked for it, so the only question left is whether anybody may step on
    /// it — a single zone lookup, not topology.
    /// </summary>
    public static StockpileCellAccent PendingStockpile(MapProjection view, GridPoint tile)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.IsInZone(ZoneKind.Forbidden, tile)
            ? StockpileCellAccent.Unreachable
            : StockpileCellAccent.Room;
    }

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
