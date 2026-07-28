using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// The side panel: what the player is looking at and why the crew is or is not
/// acting on it. Every branch reports simulation state, not a UI guess.
///
/// Before Issue #39 the only way to reach any of these branches was to start
/// Godot, drive a demo to the right tick and read the branch that happened to
/// land in the captured frame. They are ordinary functions of a snapshot, so
/// they now live where a unit test can call them one by one.
/// </summary>
public static class InspectorText
{
    /// <summary>
    /// The whole panel for one selection: a creature if one is selected, else the
    /// selected cell, else the idle text.
    /// </summary>
    public static string Build(
        PrototypeSnapshot state,
        int? selectedCreatureId,
        GridPoint? selectedCell)
    {
        if (selectedCreatureId is { } creatureId)
        {
            var creature = state.Creatures.Single(item => item.Id == creatureId);
            creature = creature with
            {
                Name = $"{creature.Name} — {HudText.CreatureLifeState(creature)} HP {creature.Hp}/{creature.MaxHp}",
            };
            var job = creature.CurrentJobId is { } jobId
                ? state.Jobs.SingleOrDefault(item => item.JobId == jobId)
                : null;
            var details = creature.LastDecision.Details.Count == 0
                ? "none"
                : string.Join(", ", creature.LastDecision.Details.Select(pair => $"{pair.Key}={pair.Value}"));
            details = $"STATUS {HudText.CreatureLifeState(creature)} • HP {creature.Hp}/{creature.MaxHp}\n" + details;
            return
                $"CREATURE #{creature.Id} · {creature.Name}\n\n" +
                $"satiety {creature.Satiety}   fatigue {creature.Fatigue}\n" +
                $"martial form {creature.MartialForm}   readiness {creature.Readiness}\n" +
                $"mode {creature.Mode}\n" +
                $"job {(job is null ? "none" : $"#{job.JobId} {job.Kind}")}\n" +
                $"carrying {(creature.Carrying is null ? "nothing" : $"{creature.CarryAmount} {creature.Carrying}")}\n" +
                $"{DescribeCarrierRoute(creature, job)}\n" +
                $"WHY\nt{creature.LastDecision.Tick} · {creature.LastDecision.ReasonCode}\n" +
                $"{details}";
        }

        if (selectedCell is { } cell)
        {
            var zones = state.Zones
                .Where(pair => pair.Value.Contains(cell))
                .Select(pair => pair.Key.ToString())
                .ToArray();
            if (zones.Contains(nameof(ZoneKind.Quarters), StringComparer.Ordinal))
            {
                zones = zones.Append("QUARTERS: rest only at fatigue 50+, free bunk").ToArray();
            }
            var jobs = state.Jobs
                .Where(job => job.Origin == cell || job.Target == cell || job.StoreCell == cell)
                .ToArray();
            var stockpile = state.StockpileCells.FirstOrDefault(item => item.Position == cell);
            var stockpileSection = stockpile is null
                ? string.Empty
                : $"STOCKPILE\n{BuildStockpileExplanation(state, stockpile)}\n\n";
            var looseStone = state.LooseItems.FirstOrDefault(
                item => item.Position == cell && item.Resource == ResourceKind.Stone);
            var looseSection = looseStone is null
                ? string.Empty
                : $"LOOSE STONE\n{BuildLooseStoneExplanation(state, looseStone, jobs)}\n\n";
            return
                $"CELL ({cell.X}, {cell.Y})\n\n" +
                $"tile {TileDescription(state, cell)}\n" +
                $"zones {(zones.Length == 0 ? "none" : string.Join(", ", zones))}\n" +
                $"jobs {(jobs.Length == 0 ? "none" : string.Join(", ", jobs.Select(job => $"#{job.JobId} {job.Kind}")))}\n\n" +
                looseSection +
                stockpileSection +
                $"DIG\n{BuildDigExplanation(state, cell)}";
        }

        return
            "INSPECTOR\n\nClick a creature or map cell.\n\n" +
            "The world is a read-only projection of PrototypeWorld; Godot owns only selection, UI tempo and drawing.";
    }

    public static string TileDescription(PrototypeSnapshot state, GridPoint cell)
    {
        if (state.Map.RockTiles.Contains(cell))
        {
            return state.Map.DiggableTiles.Contains(cell)
                ? "rock (internal)"
                : "rock (map boundary)";
        }

        if (state.Map.ExcavatedTiles.Contains(cell)) return "floor (excavated)";
        if (state.Beds.Any(bed => bed.Position == cell)) return "mushroom bed";
        if (state.Stations.Any(station => station.Position == cell)) return state.Stations.Single(station => station.Position == cell).Kind.ToString();
        if (cell == new GridPoint(27, 13)) return "gate";
        return "floor";
    }

    /// <summary>
    /// The player must be able to answer "why is nobody digging this?" from the
    /// inspector alone. Every branch reports simulation state, not a UI guess.
    /// </summary>
    public static string BuildDigExplanation(PrototypeSnapshot state, GridPoint cell)
    {
        if (state.DigDesignations.FirstOrDefault(item => item.Tile == cell) is { } designation)
        {
            var result =
                $"\nresult → floor + {PrototypeTuning.DigStoneYield} loose stone";
            return designation.StatusCode switch
            {
                "dig_unreachable" =>
                    "designated, but no free neighbouring floor to work from.\n" +
                    "Dig an adjacent tile first; nobody is teleported into rock." + result,
                "dig_blocked_priority" =>
                    $"designated, but the Dig priority is {state.Priorities[JobKind.Dig]}.\n" +
                    "Raise it with [J] and +/- to let creatures take the job." + result,
                "dig_in_progress" =>
                    $"digging {designation.ProgressTicks}/{designation.RequiredTicks} ticks by " +
                    $"{HudText.CreatureName(state, designation.ReservedBy!.Value)} from " +
                    $"({designation.WorkTile!.Value.X},{designation.WorkTile.Value.Y})." + result,
                "dig_reserved" =>
                    $"{HudText.CreatureName(state, designation.ReservedBy!.Value)} chose this job and is walking to " +
                    $"({designation.WorkTile!.Value.X},{designation.WorkTile.Value.Y})." + result,
                _ =>
                    "designated and reachable; waiting for a creature to be free.\n" +
                    "You mark intent, the crew decides who goes." + result,
            };
        }

        if (state.Map.DiggableTiles.Contains(cell))
        {
            return
                "diggable internal rock. Press [D] and click or drag to designate.\n" +
                $"result → floor + {PrototypeTuning.DigStoneYield} loose stone";
        }

        // Deliberately terse: on a stockpile cell this section is the least
        // important one on the panel and must not push the rest out of the box.
        return $"not diggable: {ShortUndiggableReason(state, cell)}.";
    }

    /// <summary>
    /// The carrier half of the chain: where this creature is taking the stone and
    /// why. Read straight from the job's booking, so the panel cannot claim a
    /// destination the simulation is not holding.
    /// </summary>
    public static string DescribeCarrierRoute(
        PrototypeCreatureSnapshot creature,
        PrototypeJobSnapshot? job)
    {
        if (job is not { Kind: JobKind.Haul, Resource: ResourceKind.Stone })
        {
            return creature.Carrying is ResourceKind.Stone
                ? "stone in hand, no haul job: it will be put down here\n"
                : string.Empty;
        }

        var cell = job.StoreCell;
        var where = cell is null
            ? "no stockpile cell booked"
            : $"booked ({cell.Value.X},{cell.Value.Y}) x{job.StoreReserved}";
        var stage = job.PickedUp
            ? $"carrying to ({cell?.X},{cell?.Y})"
            : $"walking to pile ({job.Origin.X},{job.Origin.Y})";
        return $"stone haul: {stage}, {where}\n";
    }

    /// <summary>
    /// "Why is that stone still lying here?" answered on the tile the player
    /// clicked. The order of the branches is the order the simulation itself
    /// checks them, so the panel and the reason codes never disagree.
    /// </summary>
    public static string BuildLooseStoneExplanation(
        PrototypeSnapshot state,
        PrototypeLooseItemSnapshot loose,
        PrototypeJobSnapshot[] jobs)
    {
        var claim = jobs.FirstOrDefault(job =>
            job.Origin == loose.Position &&
            job.Kind == JobKind.Haul &&
            job.Resource == ResourceKind.Stone);
        var head = $"{loose.Quantity} loose here.";
        if (claim is { ReservedBy: { } carrier })
        {
            var destination = claim.StoreCell is { } target
                ? $"({target.X},{target.Y})"
                : "a cell being chosen";
            return $"{head} {HudText.CreatureName(state, carrier)} chose this job, taking it to {destination}.";
        }

        if (state.Priorities[JobKind.Haul] == 0)
        {
            return $"{head} Haul priority is 0: no carrying job exists. Raise it with [J] and +/-.";
        }

        var stock = state.Stocks;
        if (state.StockpileCells.Count == 0)
        {
            return $"{head} No material stockpile yet. Press [M], paint plain floor.";
        }

        if (!state.StockpileCells.Any(item => item.Reachable))
        {
            return $"{head} Every stockpile cell is Forbidden: nobody may step on it.";
        }

        var free = stock.StockpileCapacity - stock.StoredStone - stock.ReservedStone;
        return free <= 0
            ? $"{head} Stockpile full: {stock.StoredStone} stored + {stock.ReservedStone} booked " +
                $"of {stock.StockpileCapacity}. Paint another cell with [M]."
            : $"{head} {free} slot(s) free; waiting for a creature to be free.";
    }

    /// <summary>
    /// The player must be able to answer "why is nothing arriving here?" from the
    /// cell alone. Every branch reports simulation state, not a UI guess.
    /// </summary>
    public static string BuildStockpileExplanation(
        PrototypeSnapshot state,
        PrototypeStockpileCellSnapshot cell)
    {
        var line = $"{cell.Stored}/{cell.Capacity} stored";
        if (cell.IncomingReserved > 0)
        {
            line += $", {cell.IncomingReserved} booked";
        }

        var stock = state.Stocks;
        return cell.StatusCode switch
        {
            "stockpile_unreachable" =>
                $"{line}. Forbidden: nobody may step here. What is stored stays; " +
                "nothing new arrives until you erase the Forbidden paint.",
            "stockpile_full" =>
                $"{line}. Full. Loose {stock.LooseStone} waits until you paint another cell with [M].",
            "stockpile_incoming" =>
                $"{line}. Every remaining slot is promised; a carrier is walking here.",
            "stockpile_partial" =>
                $"{line}. Room left. Erasing this cell drops the stored stone back " +
                "here as a loose pile — it is never destroyed.",
            _ =>
                $"{line}. Empty and ready. " +
                (stock.LooseStone > 0
                    ? "Loose stone exists; a free creature will choose the Haul job."
                    : "Dig rock and the stone will be brought here."),
        };
    }

    /// <summary>
    /// Why a brush stroke over this tile produced no dig designation. Shown on the
    /// control feedback line, where there is room for the full sentence.
    /// </summary>
    public static string UndiggableReason(PrototypeSnapshot state, GridPoint cell)
    {
        if (!state.Map.RockTiles.Contains(cell))
        {
            return state.Map.ExcavatedTiles.Contains(cell)
                ? "it has already been excavated"
                : "it is floor, a feature or the gate, not rock";
        }

        return "the map boundary holds the dungeon in";
    }

    /// <summary>
    /// The same reason inside the inspector's DIG section, where the panel is
    /// already close to overflowing.
    /// </summary>
    public static string ShortUndiggableReason(PrototypeSnapshot state, GridPoint cell)
    {
        if (state.Map.RockTiles.Contains(cell))
        {
            return "map boundary";
        }

        return state.Map.ExcavatedTiles.Contains(cell)
            ? "already excavated"
            : "floor, feature or gate";
    }

    /// <summary>
    /// Why a stockpile stroke over this tile produced no zone paint.
    /// </summary>
    public static string UnstockpileableReason(PrototypeSnapshot state, GridPoint cell)
    {
        if (state.Map.RockTiles.Contains(cell))
        {
            return "it is still rock";
        }

        if (state.Map.ExcavatedTiles.Contains(cell))
        {
            return "zoning freshly excavated ground is the next step of the experiment";
        }

        return "it is a bed, a station, the larder, a bunk, a post or the gate — not plain floor";
    }
}
