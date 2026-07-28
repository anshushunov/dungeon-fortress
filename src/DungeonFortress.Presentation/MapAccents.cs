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
/// A priority and a forbidden square are the player's intent exactly as a brush
/// stroke is, so they are folded and both halves read them — which is why a mark
/// the world already holds is corrected here for a priority change waiting in the
/// same frame. Reachability of rock, work starting, and which creature
/// volunteers are answers the world has to walk the map for; the projection does
/// not have them and does not guess.
///
/// The rule is stated as a rule on purpose. It was first written as a list of
/// exceptions, and the list turned out to be incomplete three times running.
///
/// Repeating the world's ladder is a cost worth naming. It is bounded — the gates
/// are read from <c>priorities</c>, the <c>Forbidden</c> zone and the published
/// stock, job and designation records, never re-derived from map topology — and
/// it is pinned: <c>MapAccentTests</c> sweeps a real session comparing the
/// prediction against the world's own <c>statusCode</c>, so a rung that stops
/// matching fails in CI on the pull request.
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
    /// How a blueprint the world already holds reads, corrected for a priority
    /// change waiting in the same frame — the same correction
    /// <see cref="Dig(MapProjection, PrototypeDigDesignationSnapshot)"/> makes,
    /// and for the same reason.
    ///
    /// Construction has two gates rather than one: the world asks about
    /// <c>Build</c> first of all, and about <c>Haul</c> far down, after it has
    /// already decided that nobody is on the site and nothing is on the way. With
    /// either of them waiting the reading is taken from
    /// <see cref="Predict"/>, which walks the world's ladder over the site's own
    /// published facts; with neither waiting the world's word stands unchanged.
    /// </summary>
    public static BlueprintAccent Blueprint(MapProjection view, PrototypeBuildSiteSnapshot site)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(site);
        return view.IsPriorityWaiting(JobKind.Build) || view.IsPriorityWaiting(JobKind.Haul)
            ? PredictBlueprint(view, site)
            : BlueprintFromStatus(site.StatusCode);
    }

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
        return Predict(
            view,
            site.Reachable,
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

    private static BlueprintAccent BlueprintFromStatus(string statusCode) => statusCode switch
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
    /// How a stockpile cell the world already holds reads. Nothing in the
    /// stockpile ladder asks about a priority, so there is nothing to correct —
    /// the projection is taken only so that every mark on the map is read the
    /// same way.
    /// </summary>
    public static StockpileCellAccent Stockpile(
        MapProjection view,
        PrototypeStockpileCellSnapshot cell)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(cell);
        return cell.StatusCode switch
        {
            "stockpile_unreachable" => StockpileCellAccent.Unreachable,
            "stockpile_full" => StockpileCellAccent.Full,
            "stockpile_incoming" => StockpileCellAccent.Incoming,
            // stockpile_empty and stockpile_partial read the same: there is room.
            // How much is in it is drawn as pips.
            _ => StockpileCellAccent.Room,
        };
    }

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
