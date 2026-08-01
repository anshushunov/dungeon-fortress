using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// The map as the player is entitled to see it right now: canonical state plus
/// the marking that has already been accepted for the tick the world is sitting
/// on and that the tick has not applied yet.
///
/// A command carrying tick <c>T</c> is applied at the <em>start</em> of tick
/// <c>T</c>, so a world stopped at <c>T</c> still holds it in
/// <see cref="PrototypeSnapshot.PendingCommands"/> rather than in
/// <see cref="PrototypeSnapshot.DigDesignations"/>, <c>BuildSites</c> or
/// <c>Zones</c>. While time runs, that gap lasts a sixth of a second and nobody
/// sees it. Paused it never closes at all: the player marks rock, the log
/// records the intent, and the map keeps showing bare rock until time moves.
/// Pause is the planning mode, so the one place the feedback was missing is the
/// one place marking is actually done — Issue #58.
///
/// This type closes that gap in the projection and nowhere else:
///
/// <list type="bullet">
/// <item>it is a pure function of one snapshot. Nothing is written back, the
/// simulation is not consulted and no rule is copied to this side of the
/// seam;</item>
/// <item>the order of operations inside a tick, the command vocabulary and the
/// shape of the canonical snapshot are untouched, which is what keeps the
/// checksum and replay independent of whether the player was paused;</item>
/// <item>a mark waiting for its tick is projected as the mark it is about to
/// become, not as a third kind of thing. Unpausing therefore refines a status —
/// reserved, unreachable, in progress — instead of drawing the mark for the
/// first time.</item>
/// </list>
///
/// Everything that reads the map goes through here rather than through the
/// snapshot directly, so the drawn mark, the brush that would mark it again, the
/// cell count during a drag and the inspector cannot disagree with each other.
/// </summary>
public sealed class MapProjection
{
    private static readonly GridPoint[] NoTiles = [];

    private readonly HashSet<GridPoint> _digMarks;
    private readonly HashSet<GridPoint> _digWithdrawals;
    private readonly HashSet<GridPoint> _buildMarks;
    private readonly HashSet<GridPoint> _buildWithdrawals;
    private readonly Dictionary<ZoneKind, HashSet<GridPoint>> _zonePaints;
    private readonly Dictionary<ZoneKind, HashSet<GridPoint>> _zoneErasures;
    private readonly Dictionary<JobKind, int> _priorities;

    private MapProjection(
        PrototypeSnapshot state,
        int pendingCommandCount,
        HashSet<GridPoint> digMarks,
        HashSet<GridPoint> digWithdrawals,
        HashSet<GridPoint> buildMarks,
        HashSet<GridPoint> buildWithdrawals,
        Dictionary<ZoneKind, HashSet<GridPoint>> zonePaints,
        Dictionary<ZoneKind, HashSet<GridPoint>> zoneErasures,
        Dictionary<JobKind, int> priorities)
    {
        State = state;
        PendingCommandCount = pendingCommandCount;
        _digMarks = digMarks;
        _digWithdrawals = digWithdrawals;
        _buildMarks = buildMarks;
        _buildWithdrawals = buildWithdrawals;
        _zonePaints = zonePaints;
        _zoneErasures = zoneErasures;
        _priorities = priorities;
    }

    /// <summary>
    /// The projection of one snapshot. With nothing waiting — every tick while
    /// time runs freely, and every frame of a session in which the player has not
    /// just marked something — the canonical lists are handed back unchanged and
    /// unwrapped.
    /// </summary>
    public static MapProjection Of(PrototypeSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);

        HashSet<GridPoint> digMarks = [];
        HashSet<GridPoint> digWithdrawals = [];
        HashSet<GridPoint> buildMarks = [];
        HashSet<GridPoint> buildWithdrawals = [];
        Dictionary<ZoneKind, HashSet<GridPoint>> zonePaints = new();
        Dictionary<ZoneKind, HashSet<GridPoint>> zoneErasures = new();
        Dictionary<JobKind, int> priorities = new();
        var digTiles = state.DigDesignations.Select(item => item.Tile).ToHashSet();
        var siteTiles = state.BuildSites.Select(site => site.Tile).ToHashSet();
        var count = 0;

        // Only the commands of *this* tick. A fixture that schedules a stockpile
        // for tick 200 is not an intent waiting for the player's next frame, and
        // showing it 199 ticks early would be a different defect.
        //
        // The commands are replayed in log order, so "mark it, then take it back"
        // inside one paused moment nets out exactly as the tick would net it out.
        foreach (var command in state.PendingCommands)
        {
            if (command.Tick != state.Tick)
            {
                continue;
            }

            var changed = command.Kind switch
            {
                "dig_designate" => Mark(command.Tiles, digMarks, digWithdrawals, digTiles.Contains),
                "dig_cancel" => Withdraw(command.Tiles, digMarks, digWithdrawals, digTiles.Contains),
                "build_designate" => Mark(command.Tiles, buildMarks, buildWithdrawals, siteTiles.Contains),
                "build_cancel" => Withdraw(command.Tiles, buildMarks, buildWithdrawals, siteTiles.Contains),
                "zone_paint" when command.ZoneKind is { } painted => Mark(
                    command.Tiles,
                    Bucket(zonePaints, painted),
                    Bucket(zoneErasures, painted),
                    state.Zones[painted].Contains),
                "zone_erase" when command.ZoneKind is { } erased => Withdraw(
                    command.Tiles,
                    Bucket(zonePaints, erased),
                    Bucket(zoneErasures, erased),
                    state.Zones[erased].Contains),
                // A priority does not put anything on the map, but it decides how
                // a mark reads — the world's own status ladder asks it first. A
                // player who switches digging off and then marks rock in the same
                // paused moment must not watch every mark change colour when time
                // moves, so the change is folded here rather than left canonical.
                // Applying it in the world is literally `_priorities[k] = v`; no
                // rule crosses the seam.
                "set_priority" when command.JobKind is { } job && command.Value is { } value =>
                    SetPriority(priorities, state.Priorities, job, value),
                // set_rule changes neither what is drawn nor how it reads.
                _ => false,
            };

            if (changed)
            {
                count++;
            }
        }

        return new MapProjection(
            state,
            count,
            digMarks,
            digWithdrawals,
            buildMarks,
            buildWithdrawals,
            zonePaints,
            zoneErasures,
            priorities);
    }

    /// <summary>The canonical snapshot this projection was taken from.</summary>
    public PrototypeSnapshot State { get; }

    /// <summary>
    /// How many accepted map commands are waiting for this tick to run and will
    /// actually change something. A command the world would fold away — a second
    /// mark on a marked tile, a withdrawal from a tile that carries nothing — is
    /// not counted, because it is not going to move the map either.
    /// </summary>
    public int PendingCommandCount { get; }

    /// <summary>
    /// The priority a job kind will have once this tick runs.
    ///
    /// It is deliberately used in one place only: predicting how a mark whose
    /// tick has not run will read. Everything that explains what the world is
    /// doing <em>now</em> — the inspector's "the Dig priority is 0", the reason a
    /// pile is not moving — keeps reading canonical priorities, because those
    /// sentences explain a status code the world produced under the old value.
    /// </summary>
    public int Priority(JobKind job) =>
        _priorities.TryGetValue(job, out var value) ? value : State.Priorities[job];

    /// <summary>
    /// The priority changes accepted for this tick and not applied yet. Empty on
    /// almost every frame.
    /// </summary>
    public IReadOnlyDictionary<JobKind, int> PendingPriorities => _priorities;

    /// <summary>
    /// Whether the projection differs from the snapshot at all; false for almost
    /// every frame.
    /// </summary>
    public bool HasPendingMarking =>
        _digMarks.Count > 0 || _digWithdrawals.Count > 0 ||
        _buildMarks.Count > 0 || _buildWithdrawals.Count > 0 ||
        _zonePaints.Any(pair => pair.Value.Count > 0) ||
        _zoneErasures.Any(pair => pair.Value.Count > 0);

    /// <summary>
    /// Whether anything at all is waiting — a mark, or a priority that decides how
    /// the marks read. This is what a structured run reports as <c>ui.pending</c>.
    /// </summary>
    public bool HasPendingIntent => HasPendingMarking || _priorities.Count > 0;

    /// <summary>
    /// The dig designations the world holds, minus the ones a withdrawal accepted
    /// on this tick is about to remove.
    /// </summary>
    public IReadOnlyList<PrototypeDigDesignationSnapshot> DigDesignations =>
        _digWithdrawals.Count == 0
            ? State.DigDesignations
            : [.. State.DigDesignations.Where(item => !_digWithdrawals.Contains(item.Tile))];

    /// <summary>
    /// Tiles that carry a mark the world has not recorded yet. They are drawn as
    /// an ordinary designation waiting for a worker, because that is what they
    /// become when the tick runs.
    /// </summary>
    public IReadOnlyList<GridPoint> PendingDigMarks => Ordered(_digMarks);

    /// <summary>Tiles whose designation is about to be withdrawn.</summary>
    public IReadOnlyList<GridPoint> PendingDigWithdrawals => Ordered(_digWithdrawals);

    /// <summary>The blueprints the world holds, minus the ones being withdrawn.</summary>
    public IReadOnlyList<PrototypeBuildSiteSnapshot> BuildSites =>
        _buildWithdrawals.Count == 0
            ? State.BuildSites
            : [.. State.BuildSites.Where(site => !_buildWithdrawals.Contains(site.Tile))];

    /// <summary>Tiles that carry a blueprint the world has not recorded yet.</summary>
    public IReadOnlyList<GridPoint> PendingBuildMarks => Ordered(_buildMarks);

    /// <summary>Tiles whose blueprint is about to be withdrawn.</summary>
    public IReadOnlyList<GridPoint> PendingBuildWithdrawals => Ordered(_buildWithdrawals);

    /// <summary>
    /// The stockpile cells the world holds, minus the ones an erase accepted on
    /// this tick is about to remove. A stockpile cell is one tile of the
    /// <see cref="ZoneKind.MaterialStockpile"/> zone, so painting and erasing that
    /// zone is what creates and destroys them.
    /// </summary>
    public IReadOnlyList<PrototypeStockpileCellSnapshot> StockpileCells =>
        Erased(ZoneKind.MaterialStockpile).Count == 0
            ? State.StockpileCells
            : [.. State.StockpileCells.Where(cell => !IsErased(ZoneKind.MaterialStockpile, cell.Position))];

    /// <summary>Tiles that become stockpile cells when this tick runs.</summary>
    public IReadOnlyList<GridPoint> PendingStockpileCells =>
        Ordered(Painted(ZoneKind.MaterialStockpile));

    /// <summary>
    /// How many tiles read as designated for digging, which is the number the HUD
    /// reports as <c>marks</c>. A mark the player just made counts: it is in the
    /// log, and telling the player it is not there is the defect.
    /// </summary>
    public int DigDesignationCount => DigDesignations.Count + PendingDigMarks.Count;

    /// <summary>
    /// The tiles of one zone, with this tick's paint and erase already folded in.
    /// </summary>
    public IReadOnlyList<GridPoint> Zone(ZoneKind zone)
    {
        var canonical = State.Zones[zone];
        var painted = Painted(zone);
        var erased = Erased(zone);
        if (painted.Count == 0 && erased.Count == 0)
        {
            return canonical;
        }

        var tiles = new SortedSet<GridPoint>(canonical);
        tiles.ExceptWith(erased);
        tiles.UnionWith(painted);
        return [.. tiles];
    }

    /// <summary>
    /// Which zones cover a tile, in the canonical zone order the snapshot uses so
    /// that overlapping outlines are drawn in a stable sequence.
    /// </summary>
    public IEnumerable<ZoneKind> ZonesAt(GridPoint tile)
    {
        foreach (var zone in State.Zones.Keys)
        {
            if (IsInZone(zone, tile))
            {
                yield return zone;
            }
        }
    }

    public bool IsInZone(ZoneKind zone, GridPoint tile) =>
        !IsErased(zone, tile) &&
        (State.Zones[zone].Contains(tile) || Painted(zone).Contains(tile));

    /// <summary>Whether the tile reads as marked for excavation.</summary>
    public bool IsDesignatedForDigging(GridPoint tile) =>
        !_digWithdrawals.Contains(tile) &&
        (_digMarks.Contains(tile) || State.DigDesignations.Any(item => item.Tile == tile));

    /// <summary>Whether the tile reads as carrying a training-post blueprint.</summary>
    public bool CarriesBlueprint(GridPoint tile) =>
        !_buildWithdrawals.Contains(tile) &&
        (_buildMarks.Contains(tile) || State.BuildSites.Any(site => site.Tile == tile));

    /// <summary>Whether the tile reads as a material stockpile cell.</summary>
    public bool IsStockpileCell(GridPoint tile) => IsInZone(ZoneKind.MaterialStockpile, tile);

    /// <summary>
    /// Whether this tile's mark is still waiting for its tick. The picture does
    /// not distinguish the two on purpose; the inspector does, because "accepted,
    /// the crew answers when time moves" is a different sentence from "nobody has
    /// taken the job yet".
    /// </summary>
    public bool IsPendingDigMark(GridPoint tile) => _digMarks.Contains(tile);

    public bool IsPendingBuildMark(GridPoint tile) => _buildMarks.Contains(tile);

    public bool IsPendingStockpileCell(GridPoint tile) => IsPendingZonePaint(ZoneKind.MaterialStockpile, tile);

    /// <summary>
    /// Whether this tile's zone paint is still waiting for its tick.
    ///
    /// Since Issue #52 a settled zone is drawn as a room — one border round the
    /// whole patch — and a room is created by the world, because which patch a
    /// tile joins and whether the patch is complete are questions connectivity and
    /// contents answer. A paint the player has just made therefore has no room
    /// yet, and this is what lets the adapter keep drawing it the way it always
    /// did until the tick that turns it into one.
    /// </summary>
    public bool IsPendingZonePaint(ZoneKind zone, GridPoint tile) =>
        Painted(zone).Contains(tile);

    /// <summary>
    /// Marking, folded the way the world folds it. Marking a tile that already
    /// carries the mark is a no-op in <c>PrototypeWorld</c> — "designating an
    /// already designated tile is a no-op, matching zone_paint" — so it is a no-op
    /// here, and undoing a withdrawal simply restores the canonical mark rather
    /// than inventing a fresh one.
    /// </summary>
    /// <returns>Whether the projection actually moved.</returns>
    private static bool Mark(
        IReadOnlyList<GridPoint> tiles,
        HashSet<GridPoint> fresh,
        HashSet<GridPoint> withdrawn,
        Func<GridPoint, bool> canonical)
    {
        var changed = false;
        foreach (var tile in tiles)
        {
            if (withdrawn.Remove(tile) || (!canonical(tile) && fresh.Add(tile)))
            {
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Withdrawal, folded the way the world folds it: <c>ApplyDigCancel</c>,
    /// <c>ApplyBuildCancel</c> and <c>zone_erase</c> are all tolerant and skip a
    /// tile that carries nothing. Adding such a tile here would make the map claim
    /// a withdrawal that is never going to happen — reachable through a fixture,
    /// even though the brush would never emit it.
    /// </summary>
    /// <returns>Whether the projection actually moved.</returns>
    private static bool Withdraw(
        IReadOnlyList<GridPoint> tiles,
        HashSet<GridPoint> fresh,
        HashSet<GridPoint> withdrawn,
        Func<GridPoint, bool> canonical)
    {
        var changed = false;
        foreach (var tile in tiles)
        {
            if (fresh.Remove(tile) || (canonical(tile) && withdrawn.Add(tile)))
            {
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// A priority waiting for its tick. Restating the value the world already has
    /// is not a change, and setting it back to the canonical value undoes an
    /// earlier waiting change rather than recording a second one.
    /// </summary>
    /// <returns>Whether the projection actually moved.</returns>
    private static bool SetPriority(
        Dictionary<JobKind, int> pending,
        IReadOnlyDictionary<JobKind, int> canonical,
        JobKind job,
        int value)
    {
        if (canonical[job] == value)
        {
            return pending.Remove(job);
        }

        if (pending.TryGetValue(job, out var current) && current == value)
        {
            return false;
        }

        pending[job] = value;
        return true;
    }

    private static HashSet<GridPoint> Bucket(
        Dictionary<ZoneKind, HashSet<GridPoint>> buckets,
        ZoneKind zone)
    {
        if (!buckets.TryGetValue(zone, out var bucket))
        {
            bucket = [];
            buckets[zone] = bucket;
        }

        return bucket;
    }

    private IReadOnlySet<GridPoint> Painted(ZoneKind zone) =>
        _zonePaints.TryGetValue(zone, out var tiles) ? tiles : FrozenEmpty;

    private IReadOnlySet<GridPoint> Erased(ZoneKind zone) =>
        _zoneErasures.TryGetValue(zone, out var tiles) ? tiles : FrozenEmpty;

    private bool IsErased(ZoneKind zone, GridPoint tile) => Erased(zone).Contains(tile);

    private static readonly HashSet<GridPoint> FrozenEmpty = [];

    private static IReadOnlyList<GridPoint> Ordered(IReadOnlySet<GridPoint> tiles) =>
        tiles.Count == 0 ? NoTiles : [.. tiles.Order()];
}
