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
    ///
    /// It reads a <see cref="MapProjection"/> rather than a snapshot, because a
    /// mark accepted for this tick is already drawn on the map and the panel has
    /// to agree with it: a cell that visibly carries a designation must not be
    /// described as bare rock waiting to be marked.
    /// </summary>
    public static string Build(
        MapProjection view,
        int? selectedCreatureId,
        GridPoint? selectedCell)
    {
        ArgumentNullException.ThrowIfNull(view);
        var state = view.State;
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
            return
                $"CREATURE #{creature.Id} · {creature.Name}\n\n" +
                $"satiety {creature.Satiety}   fatigue {creature.Fatigue}\n" +
                $"martial form {creature.MartialForm}   readiness {creature.Readiness}\n" +
                $"mode {creature.Mode}\n" +
                $"job {(job is null ? "none" : $"#{job.JobId} {job.Kind}")}\n" +
                $"carrying {(creature.Carrying is null ? "nothing" : $"{creature.CarryAmount} {creature.Carrying}")}\n" +
                $"{DescribeCarrierRoute(creature, job, state.BuildSites)}" +
                $"{DescribeMemory(creature)}" +
                $"WHY t{creature.LastDecision.Tick} · {creature.LastDecision.ReasonCode}\n" +
                $"{EventNarration.Sentence(creature.LastDecision.ReasonCode, creature.LastDecision.Details, creature.LastDecision.JobKind, creature.LastDecision.Target)}\n" +
                $"{details}";
        }

        if (selectedCell is { } cell)
        {
            var zones = view.ZonesAt(cell)
                .Select(zone => zone.ToString())
                .ToArray();
            if (zones.Contains(nameof(ZoneKind.Quarters), StringComparer.Ordinal))
            {
                zones = zones.Append("QUARTERS: rest only at fatigue 50+, free bunk").ToArray();
            }
            var jobs = state.Jobs
                .Where(job => job.Origin == cell || job.Target == cell || job.StoreCell == cell)
                .ToArray();
            var stockpile = view.StockpileCells.FirstOrDefault(item => item.Position == cell);
            var stockpileSection = stockpile is not null
                ? $"STOCKPILE\n{BuildStockpileExplanation(state, stockpile)}\n\n"
                : view.IsPendingStockpileCell(cell)
                    ? $"STOCKPILE\n{PendingMarkLine("a stockpile cell")}\n\n"
                    : string.Empty;
            var looseStone = state.LooseItems.FirstOrDefault(
                item => item.Position == cell && item.Resource == ResourceKind.Stone);
            var looseSection = looseStone is null
                ? string.Empty
                : $"LOOSE STONE\n{BuildLooseStoneExplanation(state, looseStone, jobs)}\n\n";
            // Only a cell that is part of the construction chain carries this
            // section. A tile that is neither a blueprint nor a built post reads
            // exactly as it did before the chain existed.
            var site = view.BuildSites.FirstOrDefault(item => item.Tile == cell);
            var buildSection = site is not null
                ? $"BUILD\n{BuildBlueprintExplanation(state, site)}\n\n"
                : view.IsPendingBuildMark(cell)
                    ? $"BUILD\n{PendingMarkLine("a training-post blueprint")}\n\n"
                    : state.Map.BuiltPostTiles.Contains(cell)
                        ? $"BUILD\n{BuildPostExplanation(state, cell)}\n\n"
                        : string.Empty;
            return
                $"CELL ({cell.X}, {cell.Y})\n\n" +
                $"tile {TileDescription(view, cell)}\n" +
                $"zones {(zones.Length == 0 ? "none" : string.Join(", ", zones))}\n" +
                DescribeRooms(view, cell) +
                $"jobs {(jobs.Length == 0 ? "none" : string.Join(", ", jobs.Select(job => $"#{job.JobId} {job.Kind}")))}\n\n" +
                looseSection +
                buildSection +
                stockpileSection +
                $"DIG\n{BuildDigExplanation(view, cell)}";
        }

        return
            "INSPECTOR\n\nClick a creature or map cell.\n\n" +
            "The world is a read-only projection of PrototypeWorld; Godot owns only selection, UI tempo and drawing.";
    }

    public static string TileDescription(MapProjection view, GridPoint cell)
    {
        ArgumentNullException.ThrowIfNull(view);
        var state = view.State;
        if (state.Map.RockTiles.Contains(cell))
        {
            return state.Map.DiggableTiles.Contains(cell)
                ? "rock (internal)"
                : "rock (map boundary)";
        }

        if (state.Map.BuiltPostTiles.Contains(cell)) return "Post (built)";
        if (view.CarriesBlueprint(cell)) return "floor (blueprint)";
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
    public static string BuildDigExplanation(MapProjection view, GridPoint cell)
    {
        ArgumentNullException.ThrowIfNull(view);
        var state = view.State;
        // A mark accepted for this tick has no status yet — the world assigns one
        // when the tick runs. Saying so is honest and is the only wording here
        // that is not read straight out of the simulation.
        if (view.IsPendingDigMark(cell))
        {
            return
                $"{PendingMarkLine("designated for excavation")}\n" +
                $"result → floor + {PrototypeTuning.DigStoneYield} loose stone";
        }

        if (view.DigDesignations.FirstOrDefault(item => item.Tile == cell) is { } designation)
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
    /// Which rooms this cell belongs to, and what is standing on it with no room
    /// around it (Issue #52).
    ///
    /// One line each and no heading, for the reason
    /// <see cref="DescribeMemory"/> already had to learn: the panel fits sixteen
    /// lines at 1280x720 and the HUD overflow guard refuses a frame that does not,
    /// so a block with a heading and a blank line costs three lines for one fact.
    /// The room's own caption already carries its state — <c>TRAIN · no post</c> —
    /// so the line is the caption plus the id the map draws the caption under.
    ///
    /// Empty for the great majority of cells, which are in no room at all.
    /// </summary>
    public static string DescribeRooms(MapProjection view, GridPoint cell)
    {
        ArgumentNullException.ThrowIfNull(view);
        var lines = string.Empty;
        var rooms = view.State.Rooms
            .Where(room => room.Perimeter.Contains(cell))
            .ToArray();
        if (rooms.Length > 0)
        {
            lines += "room " + string.Join(
                " · ",
                rooms.Select(room => $"{RoomLabels.Caption(room)} [{room.Id}]")) + "\n";
        }

        // The other half of the silence: a post nobody has zoned is in no room, so
        // no room's caption can mention it. The map marks it; the panel says what
        // to do about it.
        var orphan = RoomObjects.Unroomed(view).FirstOrDefault(item => item.Position == cell);
        if (orphan is not null)
        {
            lines += $"no room: this {RoomLabels.FeatureName(orphan.Kind)} needs " +
                $"{orphan.Needs} painted over it\n";
        }

        return lines;
    }

    /// <summary>
    /// What this creature will not go back to, and why (Issue #117).
    ///
    /// It is on the panel and not only in the feed because the feed scrolls: a
    /// player who asks "why is this one standing about" a hundred ticks after the
    /// wave needs the answer where they are looking. Empty for a creature that
    /// has been through nothing, which is most of them for most of a party.
    ///
    /// One line, and the newest place first. It was three lines with a heading
    /// first, and the HUD overflow guard refused the frame: the panel fits
    /// sixteen lines at 1280x720 and a creature carrying three memories needed
    /// eighteen. The guard is right — text that does not fit is dropped or drawn
    /// over the panel below — so the block is compact rather than the panel
    /// taller.
    /// </summary>
    public static string DescribeMemory(PrototypeCreatureSnapshot creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.RememberedPlaces.Count == 0)
        {
            return string.Empty;
        }

        var places = creature.RememberedPlaces
            .OrderByDescending(place => place.Tick)
            .Select(place => $"({place.Place.X},{place.Place.Y}) t{place.Tick} {place.Cause}");
        return "AVOIDS " + string.Join(" · ", places) + "\n";
    }

    /// <summary>
    /// The carrier half of the chain: where this creature is taking the stone and
    /// why. Read straight from the job's booking, so the panel cannot claim a
    /// destination the simulation is not holding.
    /// </summary>
    public static string DescribeCarrierRoute(
        PrototypeCreatureSnapshot creature,
        PrototypeJobSnapshot? job,
        IReadOnlyList<PrototypeBuildSiteSnapshot>? buildSites = null)
    {
        if (job is not { Kind: JobKind.Haul, Resource: ResourceKind.Stone })
        {
            return creature.Carrying is ResourceKind.Stone
                ? "stone in hand, no haul job: it will be put down here\n"
                : string.Empty;
        }

        var cell = job.StoreCell;
        var toSite = cell is { } destination &&
            buildSites is not null &&
            buildSites.Any(site => site.Tile == destination);
        var where = cell is null
            ? "no stockpile cell booked"
            : toSite
                ? $"booked for the site at ({cell.Value.X},{cell.Value.Y}) x{job.StoreReserved}"
                : $"booked ({cell.Value.X},{cell.Value.Y}) x{job.StoreReserved}";
        var source = job.SourceCell is { } stockpile
            ? $"taking it out of the stockpile ({stockpile.X},{stockpile.Y})"
            : $"walking to pile ({job.Origin.X},{job.Origin.Y})";
        var stage = job.PickedUp
            ? $"carrying to ({cell?.X},{cell?.Y})"
            : source;
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
    /// The player must be able to answer "why is nothing happening on my
    /// blueprint?" from the cell alone. The branches follow the order the
    /// simulation itself decides them in, so the panel and the status codes can
    /// never disagree.
    /// </summary>
    public static string BuildBlueprintExplanation(
        PrototypeSnapshot state,
        PrototypeBuildSiteSnapshot site)
    {
        var head = $"training post blueprint · stone {site.Delivered}/{site.Required}";
        if (site.IncomingReserved > 0)
        {
            head += $", {site.IncomingReserved} booked";
        }

        var result = "\nresult → a training post; Drill work needs a TrainingGround zone here";
        return site.StatusCode switch
        {
            "build_blocked_priority" =>
                $"{head}. Build priority is {state.Priorities[JobKind.Build]}.\n" +
                "Raise it with [J] and +/- to let creatures take the job." + result,
            "build_unreachable" =>
                $"{head}. Nobody may step on this tile, so nothing can be brought " +
                "here and nothing can be built.\nErase the Forbidden paint." + result,
            "build_in_progress" =>
                $"{head}. Building {site.ProgressTicks}/{site.RequiredTicks} ticks by " +
                $"{HudText.CreatureName(state, site.ReservedBy!.Value)}." + result,
            "build_reserved" =>
                $"{head}. {HudText.CreatureName(state, site.ReservedBy!.Value)} chose " +
                "this job and is walking here." + result,
            "build_ready" =>
                $"{head}. Material complete; waiting for a creature to be free.\n" +
                "You mark intent, the crew decides who builds." + result,
            "build_carrier_on_the_way" =>
                $"{head}. A carrier is walking here with the rest of the stone." + result,
            "build_haul_blocked" =>
                $"{head}. Haul priority is 0: nothing is being carried anywhere.\n" +
                "Raise it with [J] and +/-." + result,
            "build_stone_reserved" =>
                $"{head}. The stone that exists is already booked by another job.\n" +
                "Dig more rock, or wait for a carrier to free up." + result,
            "build_no_stone" =>
                $"{head}. There is no stone in the world yet.\n" +
                "Press [D] and mark rock; a finished dig leaves one block." + result,
            _ =>
                $"{head}. Stone is available and free; waiting for a creature to " +
                "choose the Haul job." + result,
        };
    }

    /// <summary>
    /// The end of the chain, read from the tile the player built. It states the
    /// one condition that still stands between a post and actual training.
    /// </summary>
    public static string BuildPostExplanation(PrototypeSnapshot state, GridPoint cell)
    {
        var zoned = state.Zones[ZoneKind.TrainingGround].Contains(cell);
        var head = $"built training post; it cost {PrototypeTuning.BuildStoneCost} stone.";
        if (!zoned)
        {
            return $"{head}\nNo TrainingGround zone here yet: press [Z] to select it and " +
                "[B] to paint, and Drill work appears.";
        }

        return state.Priorities[JobKind.Drill] == 0
            ? $"{head}\nInside TrainingGround, but the Drill priority is 0. " +
                "Raise it with [J] and +/-."
            : $"{head}\nInside TrainingGround: this post now produces Drill work like any other.";
    }

    /// <summary>
    /// Why a brush stroke over this tile produced no blueprint. Shown on the
    /// control feedback line, where there is room for the full sentence.
    /// </summary>
    public static string UnbuildableReason(PrototypeSnapshot state, GridPoint cell)
    {
        if (state.Map.RockTiles.Contains(cell))
        {
            return state.Map.DiggableTiles.Contains(cell)
                ? "it is still rock — dig it first, then build on the floor it leaves"
                : "the map boundary holds the dungeon in";
        }

        if (state.Map.BuiltPostTiles.Contains(cell))
        {
            return "a training post already stands here";
        }

        if (state.StockpileCells.Any(item => item.Position == cell))
        {
            return "it is a material stockpile cell — erase it first, a building site is not a warehouse";
        }

        return "it is a bed, a station, the larder, a bunk, an existing post or the gate — not plain floor";
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
    /// The one sentence a mark gets between being accepted and being applied.
    ///
    /// The map draws such a mark exactly as it will look once the tick runs, so
    /// that unpausing refines it instead of redrawing it. The panel is where the
    /// difference is stated, because "the log has it, the world does not have it
    /// yet" is a fact about time and the picture has no room for it.
    /// </summary>
    private static string PendingMarkLine(string what) =>
        $"marked as {what} on this tick; the world applies it when time advances.\n" +
        "Press [S] to step one tick, or unpause.";

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
            return "freshly excavated ground can hold a building, but not stored material";
        }

        if (state.BuildSites.Any(site => site.Tile == cell))
        {
            return "it carries a construction blueprint — a building site is not a warehouse";
        }

        return "it is a bed, a station, the larder, a bunk, a post or the gate — not plain floor";
    }
}
