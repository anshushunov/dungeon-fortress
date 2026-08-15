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
                // Issue #409, criterion 7. The panel used to print Hp, mode and
                // readiness and say nothing at all about where the creature was
                // hurt, which made the slice's own question unanswerable: the
                // player cannot recognise a creature by its wound if the wound
                // cannot be read. Every hurt part is named here — the mark over
                // the head shows only the worst — and «цел» is printed rather
                // than the line being dropped, because "this one is whole" is an
                // answer and a missing line is not.
                $"wounds {HudText.CreatureInjuryLong(creature)}\n" +
                $"{DescribeWoundIntent(creature)}" +
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

    /// <summary>
    /// The panel of one raider (Issue #364).
    ///
    /// <para><b>Why it exists.</b> The owner's second finding on the playtest of
    /// 2026-08-10 was «Враги, кстати вообще не выбираются и при наведении ничего
    /// нет». A body the player can watch and cannot ask about is the same hole as
    /// a caption that has drifted off its owner: in both cases the frame will not
    /// say who that is.</para>
    ///
    /// <para><b>Why these fields and no others.</b> Nothing here is invented —
    /// every line is a field of <c>PrototypeRaiderSnapshot</c> the domain already
    /// publishes. Who he is and what he is doing now (name, wave, mode, health,
    /// what he is carrying) is what the player asks of any body; the past
    /// encounter is what he asks of <em>this</em> one, and it is the whole subject
    /// of slice 5. The creature panel's <c>WHY</c> block has no counterpart here on
    /// purpose: a raider carries no <c>LastDecision</c> in the snapshot, and a
    /// panel that guessed one would be the screen disagreeing with the canonical
    /// document.</para>
    ///
    /// <para><b>The past encounter is not written twice.</b> The two lines about it
    /// are <see cref="ReturningHeroLabel.Name"/> and
    /// <see cref="ReturningHeroLabel.Story"/> — the same calls the caption over his
    /// head makes — so the panel and the caption cannot drift apart by anybody
    /// editing one of them. Criterion 10 of Issue #364 asks for exactly that, and
    /// <c>WorldLabelInspectorTests</c> compares the two rather than checking each
    /// alone.</para>
    /// </summary>
    public static string Raider(PrototypeSnapshot state, PrototypeRaiderSnapshot raider)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(raider);
        var past = ReturningHeroLabel.Story(raider) is { } story
            ? $"\nLAST TIME\nволна {raider.ReturnedFromWave} · он уже был здесь\n{story}\n"
            : raider.ReturnedFromWave is { } wave
                ? $"\nLAST TIME\nволна {wave} · он уже был здесь, и никто до него не дотянулся\n"
                : "\nfirst visit: the domain has not met this one before\n";
        return
            $"RAIDER #{raider.Id} · {ReturningHeroLabel.Name(raider)} — " +
            $"{raider.Mode} HP {raider.Hp}/{PrototypeTuning.RaiderHp}\n\n" +
            $"arrived with wave {raider.Wave} of {state.Threat.WaveCount}\n" +
            $"might {raider.Might}   at ({raider.Position.X}, {raider.Position.Y})\n" +
            $"carrying {(raider.CarryingMeals == 0 ? "nothing" : $"{raider.CarryingMeals} Meal")}" +
            $"{(raider.ReturningToGate ? ", heading for the gate" : string.Empty)}\n" +
            past;
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
    /// This line replaced the <c>zones</c> line rather than joining it, and the
    /// budget is why. The panel is measured by the HUD overflow guard at every
    /// viewport the game supports, and the two lines together overflowed the
    /// frame of the demo blueprint. The guard is right — text that does not fit
    /// is dropped or drawn over the panel below — and the cheapest honest answer
    /// was that the two lines said the same thing.
    /// Every painted zone is a room, so <c>zones TrainingGround</c> and
    /// <c>room TRAIN · no post [trainingGround@25,2]</c> name the same fact, and
    /// only the second one says how it is doing.
    ///
    /// The one thing the zones line had that the room line has to keep is the
    /// rule note for the quarters: resting has a condition the player cannot see
    /// from the map.
    ///
    /// Empty for the great majority of cells, which are in no room at all — the
    /// panel then simply has no room line, exactly as it had no zones to list.
    ///
    /// <b>The pending-intent window (Issue #130).</b> The room line reads the
    /// canonical rooms and the orphan line reads the folded zone membership, and
    /// the two only agree while nothing is waiting. A zone paint or erase
    /// accepted for this tick and not applied yet is exactly the moment they
    /// stop agreeing — a paint has no room yet, an erase leaves the room holding
    /// the cell — so in that window the panel names the player's intent instead
    /// and does not print the room line or the orphan line that would contradict
    /// it. This is the same sentence a pending dig mark, blueprint or stockpile
    /// cell gets, so the pause window reads like every other accepted mark.
    /// </summary>
    public static string DescribeRooms(MapProjection view, GridPoint cell)
    {
        ArgumentNullException.ThrowIfNull(view);
        var lines = string.Empty;

        // The pending-intent window, answered with the intent itself. A room the
        // erase is about to remove is not held by this cell any more, and an
        // orphan whose room the erase is about to create is not an answer — the
        // player asked to remove the room, not to hear it needs repainting.
        var pendingErasures = Enum.GetValues<ZoneKind>()
            .Where(zone => PendingZoneMarks.IsErasing(view, zone, cell))
            .ToArray();

        var rooms = view.State.Rooms
            .Where(room => room.Perimeter.Contains(cell))
            .Where(room => !pendingErasures.Contains(room.Purpose))
            .ToArray();
        if (rooms.Length > 0)
        {
            var described = rooms
                .Select(room => $"{RoomLabels.Caption(room, view)} [{room.Id}]")
                .ToList();
            if (rooms.Any(room => room.Purpose == ZoneKind.Quarters))
            {
                described.Add("QUARTERS: rest only at fatigue 50+, free bunk");
            }

            lines += "room " + string.Join(" · ", described) + "\n";
        }

        foreach (var zone in Enum.GetValues<ZoneKind>())
        {
            if (view.IsPendingZonePaint(zone, cell))
            {
                lines += PendingMarkLine(zone.ToString()) + "\n";
            }
            else if (PendingZoneMarks.IsErasing(view, zone, cell))
            {
                lines += PendingMarkLine($"erasing {zone}") + "\n";
            }
        }

        // The other half of the silence: a post nobody has zoned is in no room, so
        // no room's caption can mention it. The map marks it; the panel says what
        // to do about it. Suppressed while an erase accepted on this tick is
        // about to take this very cell out of the zone it needs — the erase
        // intent line above already answers.
        var orphan = RoomObjects.Unroomed(view).FirstOrDefault(item => item.Position == cell);
        if (orphan is not null && !pendingErasures.Contains(orphan.Needs))
        {
            lines += $"no room: this {RoomLabels.FeatureName(orphan.Kind)} needs " +
                $"{orphan.Needs} painted over it\n";
        }

        return lines;
    }

    /// <summary>
    /// What this creature will not go back to, and why (Issue #117).
    ///
    /// It is on the panel and not only in the feed because the feed is a digest
    /// (Issue #145), not a ticker: a player who asks "why is this one standing
    /// about" a hundred ticks after the wave needs the answer where they are
    /// looking. Empty for a creature that
    /// has been through nothing, which is most of them for most of a party.
    ///
    /// One line, and the newest place first. It was three lines with a heading
    /// first, and the HUD overflow guard refused the frame: a creature carrying
    /// several remembered places overflowed the panel. The guard is right — text
    /// that does not fit is dropped or drawn over the panel below — so the block
    /// is compact rather than the panel taller.
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
    /// What a wounded creature decided at the roll call, and by how much
    /// (Issue #431, §4, criterion 8).
    ///
    /// <para><b>The source is <c>woundIntent</c> and never
    /// <c>LastDecision</c>.</b> The roll call runs before <c>GenerateJobs</c> and
    /// <c>MatchJobs</c> (<c>PrototypeWorld.cs</c>), and every following
    /// <c>RecordDecision</c> overwrites the last one
    /// (<c>PrototypeWorld.Stores.cs</c>), which is what the <c>WHY</c> block
    /// below is built from. A decision to spare itself would therefore be gone
    /// from the panel on the very tick it was taken — the player would be looking
    /// at a creature walking to a bunk and reading that it took a Rest job, which
    /// is the <em>consequence</em> of the decision and not the decision. The
    /// simulation publishes the intent as a field of the creature for exactly
    /// this reason, and it lives until the next re-check overturns it or the wave
    /// it was about is over (§3.6).</para>
    ///
    /// <para><b>Both outcomes get the line, not only the refusal.</b> The
    /// question the playtest asks is whether a verdict changed how a creature
    /// behaved with a wound, and «полез с разбитой ногой» is half of that answer.
    /// The feed says so only where the fear of the domain was demonstrably the
    /// cause (<c>combat_pressed_wound</c>); the panel says so about every wounded
    /// creature that took the field, because here the player has asked about this
    /// one creature and the numbers of its own contest are the answer.</para>
    ///
    /// <para><b>The verdict is named as the cause only under the rule of §3.5</b>
    /// — the published <c>verdictDecided</c>, which is true only where replaying
    /// the contest without the terms a verdict wrote flips the outcome. A domain
    /// that feeds and tends its people spares its wounded with no verdict spoken,
    /// and the panel may not credit the player with that.</para>
    ///
    /// <para>Empty for every whole creature, which is most of the crew for most
    /// of a party, and for a wounded one before the first roll call that asked
    /// it. One line, for the reason <see cref="DescribeMemory"/> is one line: the
    /// panel is measured by the HUD overflow guard at every viewport the game
    /// supports.</para>
    /// </summary>
    public static string DescribeWoundIntent(PrototypeCreatureSnapshot creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.WoundIntent is not { } intent)
        {
            return string.Empty;
        }

        var sparing = intent.Code == "spared";
        var decided = intent.VerdictDecided ? " · your verdict decided it" : string.Empty;
        var weighed = sparing
            ? $"{intent.Spare} v {intent.Press}"
            : $"{intent.Press} v {intent.Spare}";
        return
            $"INTENT t{intent.Tick} wave {intent.Wave} · " +
            $"{(sparing ? "sparing" : "standing on")} {SeverityWord(intent.Severity)} " +
            $"{PartWord(intent.Part)} · {weighed}{decided}\n";
    }

    /// <summary>
    /// The part in the player's words. It switches over
    /// <see cref="BodyPart"/> itself rather than over an index, so a fifth part
    /// cannot exist in the simulation and be missing here.
    /// </summary>
    private static string PartWord(BodyPart part) => part switch
    {
        BodyPart.Head => "head",
        BodyPart.Torso => "body",
        BodyPart.Arm => "arm",
        BodyPart.Leg => "leg",
        _ => "body",
    };

    /// <summary>
    /// How badly, in the two words the mark over the head already uses. A whole
    /// part never reaches this method — a whole creature does not enter the
    /// contest at all (§3.1) — and is reported as such rather than guessed at.
    /// </summary>
    private static string SeverityWord(InjuryKind severity) => severity switch
    {
        InjuryKind.Light => "a hurt",
        InjuryKind.Heavy => "a ruined",
        _ => "a whole",
    };

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
