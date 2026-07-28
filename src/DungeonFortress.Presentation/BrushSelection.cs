using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// What a rectangle the player dragged would do, before anything is done.
///
/// The value is what the preview draws, what the count above the cursor reads,
/// and what the command is built from — one description rather than three, so the
/// highlighted area, the number of cells and the command that lands cannot
/// disagree with each other.
/// </summary>
/// <param name="Mode">The brush the rectangle was dragged with.</param>
/// <param name="Zone">The zone the paint and erase brushes act on.</param>
/// <param name="Tiles">
/// The cells the command will carry: every cell of the rectangle the simulation
/// would accept, in row-major order. Cells the brush cannot act on are filtered
/// out here rather than becoming a rejected command.
/// </param>
/// <param name="RectangleTiles">How many cells the dragged rectangle covered.</param>
/// <param name="Refusal">
/// Why the stroke produces no command, or <c>null</c> when it produces one.
/// </param>
public sealed record BrushStroke(
    BrushMode Mode,
    ZoneKind Zone,
    IReadOnlyList<GridPoint> Tiles,
    int RectangleTiles,
    string? Refusal)
{
    /// <summary>Whether the stroke will produce a command.</summary>
    public bool Applies => Tiles.Count > 0 && Refusal is null;
}

/// <summary>
/// The rectangle brush, as a pure function of the snapshot.
///
/// A pocket of 4x3 used to cost twelve clicks, because every brush worked one
/// cell at a time. It now costs one drag: the rectangle collapses into a single
/// command carrying the whole tile list, which the v2 command vocabulary already
/// accepted — <c>dig_designate</c>, <c>zone_paint</c> and <c>build_designate</c>
/// have always taken a list. No new command and no ADR.
///
/// Two properties follow from being one command rather than N:
/// <list type="bullet">
/// <item>partially applied marking cannot exist. Either the command is accepted
/// and every tile in it is marked, or it is rejected and nothing is;</item>
/// <item>a cancelled drag leaves no trace at all, because nothing is emitted
/// until the button is released.</item>
/// </list>
///
/// Living here rather than in the adapter is what makes "a drag over N cells
/// produces exactly one command with N tiles" an ordinary unit test.
/// </summary>
public static class BrushSelection
{
    /// <summary>
    /// Every cell of the rectangle spanned by two corners, clipped to the map.
    /// Either corner may be the one the drag started from.
    /// </summary>
    public static IReadOnlyList<GridPoint> Rectangle(GridPoint from, GridPoint to)
    {
        var minX = Math.Max(0, Math.Min(from.X, to.X));
        var maxX = Math.Min(PrototypeTuning.MapWidth - 1, Math.Max(from.X, to.X));
        var minY = Math.Max(0, Math.Min(from.Y, to.Y));
        var maxY = Math.Min(PrototypeTuning.MapHeight - 1, Math.Max(from.Y, to.Y));

        var tiles = new List<GridPoint>();
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                tiles.Add(new GridPoint(x, y));
            }
        }

        return tiles;
    }

    /// <summary>
    /// What dragging <paramref name="mode"/> from one corner to the other would
    /// do. A single click is the same call with both corners equal, so a 1x1
    /// rectangle is not a special case anywhere below it.
    ///
    /// The question is asked of the map the player is actually looking at, which
    /// includes the marking accepted for this tick and not applied yet. A cell
    /// that already carries a waiting mark is not offered again: the drawn mark,
    /// the highlighted area and the emitted command have to be one answer, and
    /// paused they would otherwise be three.
    ///
    /// It takes a <see cref="MapProjection"/> and not a snapshot deliberately. A
    /// snapshot overload would have to build one, and this runs once per cell of
    /// a rectangle — and, through <see cref="Accepts"/>, once per cell of the map
    /// on every frame a brush is held.
    /// </summary>
    public static BrushStroke Resolve(
        MapProjection view,
        BrushMode mode,
        ZoneKind zone,
        GridPoint from,
        GridPoint to)
    {
        ArgumentNullException.ThrowIfNull(view);
        var rectangle = Rectangle(from, to);
        if (mode == BrushMode.Inspect)
        {
            return new BrushStroke(mode, zone, [], rectangle.Count, "Inspect does not mark the map.");
        }

        var tiles = rectangle.Where(tile => Accepts(view, mode, zone, tile)).ToArray();
        if (tiles.Length == 0)
        {
            return new BrushStroke(mode, zone, [], rectangle.Count, EmptyReason(view, mode, zone, rectangle));
        }

        // One command carries at most 256 tiles, so a very large drag has to be
        // refused rather than split: splitting would reintroduce exactly the
        // partially applied marking this brush exists to make impossible.
        if (tiles.Length > PrototypeTuning.MaximumTilesPerCommand)
        {
            return new BrushStroke(
                mode,
                zone,
                [],
                rectangle.Count,
                $"{tiles.Length} cells is more than the {PrototypeTuning.MaximumTilesPerCommand} " +
                "one command may carry. Mark it in two strokes.");
        }

        return new BrushStroke(mode, zone, tiles, rectangle.Count, null);
    }

    /// <summary>
    /// The one command a stroke becomes. <c>null</c> for a stroke that does not
    /// apply, so a refused or cancelled drag has nothing to append to the log.
    /// </summary>
    public static PrototypeCommand? ToCommand(BrushStroke stroke, int tick)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        if (!stroke.Applies)
        {
            return null;
        }

        return stroke.Mode switch
        {
            BrushMode.Paint => new ZonePaintCommand(tick, stroke.Zone, stroke.Tiles),
            BrushMode.Erase => new ZoneEraseCommand(tick, stroke.Zone, stroke.Tiles),
            BrushMode.Dig => new DigDesignateCommand(tick, stroke.Tiles),
            BrushMode.CancelDig => new DigCancelCommand(tick, stroke.Tiles),
            BrushMode.Build => new BuildDesignateCommand(tick, stroke.Tiles),
            BrushMode.CancelBuild => new BuildCancelCommand(tick, stroke.Tiles),
            _ => null,
        };
    }

    /// <summary>
    /// Whether the simulation would act on this cell with this brush. The legal
    /// targets come from the snapshot — <c>map.diggableTiles</c>,
    /// <c>map.stockpileFloorTiles</c>, <c>map.buildFloorTiles</c> — so the rule
    /// itself is never copied to this side of the seam.
    ///
    /// A cell the brush would not change is excluded too, not only a cell it may
    /// not touch: the number the player sees during a drag has to be the number of
    /// cells the command actually affects.
    ///
    /// "Would not change" is read from the projection, so a mark that is waiting
    /// for its tick counts exactly as a mark the world already holds. That is not
    /// a new rule about which cells a brush may take — the legal-target lists
    /// above are untouched — it is the same "already marked" test finally being
    /// told the truth while the world is paused.
    /// </summary>
    public static bool Accepts(MapProjection view, BrushMode mode, ZoneKind zone, GridPoint tile)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!MapBounds.Contains(tile))
        {
            return false;
        }

        var state = view.State;
        return mode switch
        {
            BrushMode.Paint when zone == ZoneKind.MaterialStockpile =>
                state.Map.StockpileFloorTiles.Contains(tile) && !view.IsStockpileCell(tile),
            BrushMode.Paint =>
                IsZonable(state, tile) && !view.IsInZone(zone, tile),
            BrushMode.Erase =>
                IsZonable(state, tile) && view.IsInZone(zone, tile),
            BrushMode.Dig =>
                state.Map.DiggableTiles.Contains(tile) && !view.IsDesignatedForDigging(tile),
            BrushMode.CancelDig =>
                view.IsDesignatedForDigging(tile),
            BrushMode.Build =>
                state.Map.BuildFloorTiles.Contains(tile) && !view.CarriesBlueprint(tile),
            BrushMode.CancelBuild =>
                view.CarriesBlueprint(tile),
            _ => false,
        };
    }

    /// <summary>
    /// A zone may cover any passable cell except the gate. Rock is excluded
    /// because the world rejects a zone on a tile that is not passable yet, and a
    /// stroke that would be rejected must never become a command.
    /// </summary>
    private static bool IsZonable(PrototypeSnapshot state, GridPoint tile) =>
        !state.Map.RockTiles.Contains(tile) && tile != MapBounds.Gate;

    /// <summary>
    /// Why nothing happened. A single cell keeps the wording it had before the
    /// rectangle existed — those sentences name the rule the player broke and are
    /// the reason a rejected command never has to be shown. A larger rectangle
    /// gets the same sentence about the area instead of one per cell.
    /// </summary>
    private static string EmptyReason(
        MapProjection view,
        BrushMode mode,
        ZoneKind zone,
        IReadOnlyList<GridPoint> rectangle)
    {
        if (rectangle.Count == 1)
        {
            return SingleCellReason(view, mode, zone, rectangle[0]);
        }

        var area = $"None of the {rectangle.Count} cells in the selection";
        return mode switch
        {
            BrushMode.Paint when zone == ZoneKind.MaterialStockpile =>
                $"{area} can store material: it must be floor that was already floor at tick 0.",
            BrushMode.Paint => $"{area} can take the {zone} zone.",
            BrushMode.Erase => $"{area} carries the {zone} zone.",
            BrushMode.Dig => $"{area} is rock that can be dug.",
            BrushMode.CancelDig => $"{area} carries a dig designation.",
            BrushMode.Build => $"{area} can hold a training post.",
            BrushMode.CancelBuild => $"{area} carries a blueprint.",
            _ => $"{area} can be marked.",
        };
    }

    private static string SingleCellReason(
        MapProjection view,
        BrushMode mode,
        ZoneKind zone,
        GridPoint tile)
    {
        var state = view.State;
        var at = $"({tile.X},{tile.Y})";
        return mode switch
        {
            BrushMode.Paint when zone == ZoneKind.MaterialStockpile && view.IsStockpileCell(tile) =>
                $"{at} is already a material stockpile cell.",
            BrushMode.Paint when zone == ZoneKind.MaterialStockpile =>
                $"{at} cannot store material: {InspectorText.UnstockpileableReason(state, tile)}.",
            BrushMode.Paint when view.IsInZone(zone, tile) =>
                $"{at} is already in the {zone} zone.",
            BrushMode.Paint => $"{at} cannot take a zone: it is not passable ground.",
            BrushMode.Erase => $"{at} is not in the {zone} zone.",
            BrushMode.Dig when view.IsDesignatedForDigging(tile) =>
                $"{at} is already designated for digging.",
            BrushMode.Dig =>
                $"{at} cannot be dug: {InspectorText.UndiggableReason(state, tile)}.",
            BrushMode.CancelDig => $"{at} carries no dig designation.",
            BrushMode.Build when view.CarriesBlueprint(tile) =>
                $"{at} already carries a blueprint.",
            BrushMode.Build =>
                $"{at} cannot hold a training post: {InspectorText.UnbuildableReason(state, tile)}.",
            BrushMode.CancelBuild => $"{at} carries no blueprint.",
            _ => $"{at} cannot be marked.",
        };
    }
}
