namespace DungeonFortress.Simulation;

// What the tick decides before anybody moves: which jobs exist, what
// each creature needs, and how bodies get out of each other's way.
public sealed partial class PrototypeWorld
{
    private void GenerateJobs()
    {
        var desired = new HashSet<string>(StringComparer.Ordinal);
        if (_priorities[JobKind.Harvest] > 0 &&
            ZoneCoversFeature(ZoneKind.Farm, TileKind.Bed) &&
            _stockRaw < PrototypeTuning.RawTarget)
        {
            foreach (var bed in _beds.Values.OrderBy(bed => bed.Position))
            {
                if (bed.IsRipe)
                {
                    EnsureJob(
                        $"harvest:{bed.Position.X}:{bed.Position.Y}",
                        JobKind.Harvest,
                        bed.Position,
                        null,
                        0,
                        desired);
                }
            }
        }

        if (_priorities[JobKind.Haul] > 0)
        {
            // One Haul kind, two destinations. Food goes to the larder and needs a
            // larder feature in the zone; stone goes to a material stockpile cell
            // and needs free capacity there. Neither gate affects the other.
            var larderReady = ZoneCoversFeature(ZoneKind.Larder, TileKind.Larder);
            // Two destinations now compete for the same material: a stockpile cell
            // with free capacity and a construction site that still needs stone.
            // A pile is worth a job if either of them can take it.
            var buildDemand = AvailableBuildDemand();
            var stoneCapacity = AvailableStoneCapacity() + buildDemand;
            foreach (var entry in _loose
                         .Where(pair => pair.Value > 0)
                         .OrderBy(pair => pair.Key.Point)
                         .ThenBy(pair => pair.Key.Resource))
            {
                if (entry.Key.Resource == ResourceKind.Stone)
                {
                    if (stoneCapacity <= 0)
                    {
                        continue;
                    }

                    EnsureJob(
                        $"haul:{entry.Key.Point.X}:{entry.Key.Point.Y}:{entry.Key.Resource}",
                        JobKind.Haul,
                        entry.Key.Point,
                        entry.Key.Resource,
                        Math.Min(PrototypeTuning.StoneCarryCapacity, entry.Value),
                        desired);
                    continue;
                }

                if (!larderReady)
                {
                    continue;
                }

                EnsureJob(
                    $"haul:{entry.Key.Point.X}:{entry.Key.Point.Y}:{entry.Key.Resource}",
                    JobKind.Haul,
                    entry.Key.Point,
                    entry.Key.Resource,
                    Math.Min(PrototypeTuning.CarryCapacity, entry.Value),
                    desired);
            }

            // Stored stone becomes movable again only when a construction site is
            // asking for it. Without that gate a stockpile would shuffle its own
            // contents between cells forever.
            if (buildDemand > 0)
            {
                foreach (var cell in UsableStockpileCells().Where(tile => StoredStoneAt(tile) > 0))
                {
                    EnsureJob(
                        $"supply:{cell.X}:{cell.Y}:{ResourceKind.Stone}",
                        JobKind.Haul,
                        cell,
                        ResourceKind.Stone,
                        Math.Min(PrototypeTuning.StoneCarryCapacity, StoredStoneAt(cell)),
                        desired,
                        sourceCell: cell);
                }
            }
        }

        if (_priorities[JobKind.Cook] > 0 &&
            ZoneCoversFeature(ZoneKind.Kitchen, TileKind.Kitchen) &&
            _stockMeals < PrototypeTuning.MealTarget)
        {
            var reservedRaw = _jobs
                .Where(job => job.Kind == JobKind.Cook && job.ReservedBy is not null && !job.PickedUp)
                .Sum(job => job.Quantity);
            var availableBatches = Math.Max(0, _stockRaw - reservedRaw) / PrototypeTuning.CookInput;
            foreach (var station in PrototypeMap.KitchenTiles
                         .Where(tile => _zones[ZoneKind.Kitchen].Contains(tile))
                         .Order())
            {
                if (availableBatches-- <= 0)
                {
                    break;
                }

                EnsureJob(
                    $"cook:{station.X}:{station.Y}",
                    JobKind.Cook,
                    station,
                    ResourceKind.RawMushroom,
                    PrototypeTuning.CookInput,
                    desired);
            }
        }

        if (_priorities[JobKind.Rest] > 0 &&
            ZoneCoversFeature(ZoneKind.Quarters, TileKind.Bunk))
        {
            var claimed = _jobs
                .Where(job => job.Kind == JobKind.Rest && job.ReservedBy is not null)
                .Select(job => job.Origin)
                .ToHashSet();
            foreach (var creature in _creatures
                         .Where(creature =>
                             (creature.Fatigue >= PrototypeTuning.RestSeekThreshold ||
                              creature.Injury != InjuryKind.None) &&
                             !creature.IsMustering &&
                             creature.Mode is not (CreatureMode.Eating or CreatureMode.Downed
                                 or CreatureMode.Fled or CreatureMode.Fighting) &&
                             creature.Satiety >= PrototypeTuning.CollapseThreshold)
                         .OrderBy(creature => creature.Id))
            {
                if (!TryNearestRestTarget(creature, claimed, out var bunk))
                {
                    continue;
                }

                claimed.Add(bunk);
                EnsureJob(
                    $"rest:{creature.Id}",
                    JobKind.Rest,
                    bunk,
                    null,
                    0,
                    desired,
                    creature.Id);
            }
        }

        if (_priorities[JobKind.Drill] > 0 &&
            ZoneCoversFeature(ZoneKind.TrainingGround, TileKind.Post))
        {
            foreach (var post in _map.PostTiles()
                         .Where(tile => _zones[ZoneKind.TrainingGround].Contains(tile))
                         .Order())
            {
                EnsureJob(
                    $"drill:{post.X}:{post.Y}",
                    JobKind.Drill,
                    post,
                    null,
                    0,
                    desired);
            }
        }

        if (_priorities[JobKind.Watch] > 0 && _zones[ZoneKind.Watch].Count > 0)
        {
            foreach (var tile in _zones[ZoneKind.Watch].Take(PrototypeTuning.WatchSlots))
            {
                EnsureJob(
                    $"watch:{tile.X}:{tile.Y}",
                    JobKind.Watch,
                    tile,
                    null,
                    0,
                    desired);
            }
        }

        if (_priorities[JobKind.Dig] > 0)
        {
            foreach (var tile in _digDesignations)
            {
                if (!_map.IsDiggable(tile) || !IsDigReachable(tile))
                {
                    continue;
                }

                EnsureJob(
                    $"dig:{tile.X}:{tile.Y}",
                    JobKind.Dig,
                    tile,
                    null,
                    0,
                    desired);
            }
        }

        // Construction is the last kind on purpose: a blueprint whose material has
        // not arrived yet creates no work at all, so "waiting for stone" and
        // "waiting for a builder" stay different, separately explained states.
        if (_priorities[JobKind.Build] > 0)
        {
            foreach (var site in _buildSites.Values
                         .Where(item =>
                             item.Delivered >= PrototypeTuning.BuildStoneCost &&
                             IsBuildSiteWorkable(item.Tile)))
            {
                EnsureJob(
                    $"build:{site.Tile.X}:{site.Tile.Y}",
                    JobKind.Build,
                    site.Tile,
                    null,
                    0,
                    desired);
            }
        }

        _jobs.RemoveAll(job => job.ReservedBy is null && !desired.Contains(job.Key));
    }

    private void EnsureJob(
        string key,
        JobKind kind,
        GridPoint target,
        ResourceKind? resource,
        int quantity,
        HashSet<string> desired,
        int? personalCreatureId = null,
        GridPoint? sourceCell = null)
    {
        desired.Add(key);
        if (_jobs.Any(job => job.Key == key))
        {
            return;
        }

        _jobs.Add(new JobState(
            _nextJobId++,
            key,
            kind,
            target,
            resource,
            quantity,
            personalCreatureId)
        {
            SourceCell = sourceCell,
        });
    }

    /// <summary>
    /// What each creature needs of the domain this tick — the muster before a
    /// wave, a meal, a bed — decided before any work is generated or matched.
    ///
    /// <para><b>It is asked of the people the domain still has the right to send
    /// somewhere</b>, and that qualification is the whole of Issue #333. Whether
    /// a creature is in the line is decided one phase earlier, by
    /// <see cref="UpdateCombatParticipation"/>, and it is carried by
    /// <see cref="CreatureState.Mode"/> and by nothing else. This loop used to
    /// walk every creature with no guard at all, so a need decided here
    /// overwrote a participation decided two phases before it: <see
    /// cref="TryStartEating"/> assigns <see cref="CreatureMode.Eating"/> to
    /// whoever is hungry, and hungry included the fighting, the fleeing and the
    /// people on the floor.</para>
    ///
    /// <para>The band it bites in is arithmetic rather than luck.
    /// <see cref="PrototypeTuning.EatThreshold"/> is 30 and
    /// <see cref="PrototypeTuning.CombatMinSatiety"/> is 20, so 20..29 is exactly
    /// the set combat admits and hunger then takes back on the same tick.
    /// Measured over the nine shipped runs before the guard existed
    /// (<c>evidence/333-before.json</c>): creatures joined the line and were out
    /// of it by the end of that very tick, left it mid-wave with nothing in the
    /// journal saying so and came back 15, 50 and 333 ticks later, stopped
    /// fleeing to walk to the larder, and — once, on <c>prepared/20260726</c> at
    /// t1720 — got up off the floor with one wound and no health at all. That
    /// last one costs more than a picture: <see cref="RaiseTheDowned"/> only
    /// raises creatures whose mode is <see cref="CreatureMode.Downed"/>, so a
    /// body that walked away is never carried off it.</para>
    ///
    /// <para><b>Why the guard and not a rule that ejects the starving from the
    /// line.</b> Ejecting would be a new rule of combat, and the two sentences
    /// this issue is about do not ask for one; a fighter that grows hungry stays
    /// in the line until the wave ends, and eats afterwards. The guard is also
    /// not invented here — it is the one <see cref="GenerateJobs"/> and
    /// <see cref="MatchJobs"/> already carry, minus <see
    /// cref="CreatureMode.Eating"/>, which this method must keep seeing because
    /// finishing a meal is one of the needs it decides.</para>
    /// </summary>
    private void DecideNeedsAndMuster()
    {
        var musterActive = IsMusterActive();
        foreach (var creature in _creatures.OrderBy(creature => creature.Id))
        {
            if (creature.Mode is CreatureMode.Fighting or CreatureMode.Fled or CreatureMode.Downed)
            {
                continue;
            }

            if (musterActive)
            {
                if (!creature.IsMustering)
                {
                    if (creature.CurrentJob is not null)
                    {
                        CancelJob(creature, "chosen_muster");
                    }

                    creature.IsMustering = true;
                    creature.MusterTarget = MusterTargetFor(creature.Id);
                    creature.MusterNeedsRation =
                        creature.Satiety < PrototypeTuning.RationSatietyGate &&
                        AvailableMealsForReservation() > 0;
                    if (creature.MusterNeedsRation)
                    {
                        creature.MealReserved = true;
                        RecordDecision(
                            creature,
                            "chosen_ration",
                            new Dictionary<string, int>
                            {
                                ["satiety"] = creature.Satiety,
                                ["gate"] = PrototypeTuning.RationSatietyGate,
                            });
                    }
                    else
                    {
                        RecordDecision(
                            creature,
                            "chosen_muster",
                            new Dictionary<string, int>
                            {
                                ["raidTick"] = CurrentWave()?.ArriveTick ?? 0,
                                ["leadTicks"] = _rules["muster_lead_ticks"],
                            });
                    }
                }

                if (!creature.MusterNeedsRation &&
                    !creature.MealReserved &&
                    creature.Satiety < PrototypeTuning.RationSatietyGate &&
                    AvailableMealsForReservation() > 0)
                {
                    creature.MusterNeedsRation = true;
                    creature.MealReserved = true;
                    RecordDecision(
                        creature,
                        "chosen_ration",
                        new Dictionary<string, int>
                        {
                            ["satiety"] = creature.Satiety,
                            ["gate"] = PrototypeTuning.RationSatietyGate,
                        });
                }

                continue;
            }

            if (creature.IsMustering)
            {
                continue;
            }

            if (creature.Satiety < PrototypeTuning.CollapseThreshold)
            {
                if (TryStartEating(creature, ignoreReserve: false, "chosen_need_hunger"))
                {
                    continue;
                }

                if (creature.CurrentJob is not null)
                {
                    CancelJob(creature, "refused_too_exhausted");
                }

                RecordDecision(
                    creature,
                    "refused_too_exhausted",
                    new Dictionary<string, int>
                    {
                        ["satiety"] = creature.Satiety,
                        ["threshold"] = PrototypeTuning.CollapseThreshold,
                    });
                continue;
            }

            if (creature.Satiety < PrototypeTuning.EatThreshold &&
                creature.Mode != CreatureMode.Eating)
            {
                if (TryStartEating(creature, ignoreReserve: false, "chosen_need_hunger"))
                {
                    continue;
                }

                var reason = _stockMeals - ReservedMeals() <= 0
                    ? "waiting_input_missing"
                    : "refused_rule_reserve";
                RecordDecision(
                    creature,
                    reason,
                    new Dictionary<string, int>
                    {
                        ["meals"] = _stockMeals,
                        ["reserve"] = _rules["ration_reserve"],
                    });
            }

            if ((creature.Fatigue > PrototypeTuning.RestThreshold ||
                 creature.Injury != InjuryKind.None) &&
                !creature.IsMustering)
            {
                creature.NeedsRest = TryNearestRestTarget(
                    creature,
                    new HashSet<GridPoint>(),
                    out _);
                if (creature.NeedsRest &&
                    creature.CurrentJob is { Kind: not JobKind.Rest })
                {
                    CancelJob(creature, "chosen_need_fatigue");
                }
                else if (!creature.NeedsRest && _priorities[JobKind.Rest] == 0)
                {
                    RecordDecision(
                        creature,
                        "refused_priority_zero",
                        new Dictionary<string, int>
                        {
                            ["fatigue"] = creature.Fatigue,
                            ["threshold"] = PrototypeTuning.RestThreshold,
                        },
                        JobKind.Rest);
                }
            }
            else
            {
                creature.NeedsRest = false;
            }
        }

        if (musterActive)
        {
            foreach (var creature in _creatures
                         .Where(creature => creature.MusterNeedsRation)
                         .OrderBy(creature => creature.Id))
            {
                creature.SpecialTarget = CanAdvanceMealQueue(creature, out var target)
                    ? target
                    : null;
            }
        }
    }

    private bool TryStartEating(CreatureState creature, bool ignoreReserve, string reason)
    {
        if (creature.Mode == CreatureMode.Eating)
        {
            return true;
        }

        if (!ignoreReserve &&
            ReservedMeals() >= ActiveLarderTiles().Count())
        {
            return false;
        }

        var available = AvailableMealsForReservation();
        if (available <= 0)
        {
            return false;
        }

        if (!ignoreReserve &&
            _stockMeals - ReservedMeals() - 1 < _rules["ration_reserve"])
        {
            return false;
        }

        if (!TryFindLarderTarget(
                creature,
                out var larder,
                out _))
        {
            RecordDecision(
                creature,
                "refused_zone_unreachable",
                new Dictionary<string, int>
                {
                    ["zoneKind"] = (int)ZoneKind.Larder,
                });
            return false;
        }

        if (creature.CurrentJob is not null)
        {
            CancelJob(creature, reason);
        }

        creature.MealReserved = true;
        creature.Mode = CreatureMode.Eating;
        creature.SpecialTarget = larder;
        creature.SpecialTicks = PrototypeTuning.EatTicks;
        RecordDecision(
            creature,
            reason,
            new Dictionary<string, int>
            {
                ["satiety"] = creature.Satiety,
                ["threshold"] = PrototypeTuning.EatThreshold,
            },
            target: creature.SpecialTarget);
        return true;
    }

    private void PlanTrafficActions()
    {
        _yieldReservations.Clear();
        foreach (var creature in _creatures)
        {
            creature.TrafficTarget = null;
            creature.WaitThisTick = false;
        }

        var intents = _creatures
            .Select(CreateMovementIntent)
            .Where(intent => intent is not null)
            .Cast<MovementIntent>()
            .ToArray();
        foreach (var contenders in intents.GroupBy(intent => intent.Next))
        {
            var ordered = contenders
                .OrderByDescending(intent => IsUrgentMover(intent.Creature))
                .ThenByDescending(intent => intent.Creature.BlockedTicks)
                .ThenBy(intent => FairnessKey(intent.Creature))
                .ThenBy(intent => intent.Creature.Id)
                .ToArray();
            foreach (var loser in ordered.Skip(1))
            {
                loser.Creature.WaitThisTick = true;
            }
        }

        var active = intents
            .Where(intent => !intent.Creature.WaitThisTick)
            .ToDictionary(intent => intent.Creature);
        var occupants = _creatures.ToDictionary(creature => creature.Position);
        var requested = active.Values.Select(intent => intent.Next).ToHashSet();
        var handledCycles = new HashSet<int>();

        foreach (var root in active.Keys.OrderBy(creature => creature.Id))
        {
            var chain = new List<CreatureState>();
            var indices = new Dictionary<CreatureState, int>();
            var current = root;
            while (active.TryGetValue(current, out var intent) &&
                   occupants.TryGetValue(intent.Next, out var occupant) &&
                   occupant != current)
            {
                indices[current] = chain.Count;
                chain.Add(current);
                if (!active.ContainsKey(occupant))
                {
                    _ = TryPlanYield(
                        occupant,
                        root,
                        intent.Destination,
                        requested,
                        dependencyCycle: false);
                    break;
                }

                if (indices.TryGetValue(occupant, out var cycleStart))
                {
                    var cycle = chain.Skip(cycleStart).ToArray();
                    var cycleKey = cycle.Min(creature => creature.Id);
                    if (handledCycles.Add(cycleKey))
                    {
                        var yielder = cycle
                            .OrderBy(creature => IsUrgentMover(creature))
                            .ThenBy(creature => creature.YieldCount)
                            .ThenBy(creature => FairnessKey(creature))
                            .ThenBy(creature => creature.Id)
                            .First();
                        _ = TryPlanYield(
                            yielder,
                            root,
                            intent.Destination,
                            requested,
                            dependencyCycle: true);
                    }

                    break;
                }

                current = occupant;
            }
        }
    }

    private MovementIntent? CreateMovementIntent(CreatureState creature)
    {
        var destination = PrimaryDestination(creature);
        if (destination is not { } target || creature.Position == target)
        {
            return null;
        }

        var next = _map.NextStep(
            creature.Position,
            target,
            _zones[ZoneKind.Forbidden]);
        return next is null || next == creature.Position
            ? null
            : new MovementIntent(creature, target, next.Value);
    }

    /// <summary>
    /// Where this creature is trying to get to, as traffic arbitration sees it.
    ///
    /// A creature that broke has one too, and saying so is the whole of the fix
    /// Issue #101 needed on this side. Flight stopped being a teleport and became
    /// a walk, but a walk whose destination nobody published: <c>PrimaryDestination</c>
    /// answered <c>null</c> for a runner, so it produced no
    /// <see cref="MovementIntent"/>, took no part in the arbitration, and was
    /// governed by nothing except "do not step onto an occupied tile". Nobody
    /// stepped aside for it and no dependency cycle containing it was resolved.
    /// Measured on the matrix: eleven of fifty-five flights on <c>prepared</c>
    /// never moved the creature a single tile, one of them for sixty-seven ticks
    /// — half a minute of a defender who announced panic and then stood in the
    /// middle of a fight, which reads as broken rather than as frightened.
    ///
    /// The refuge is the destination, published exactly the way a worker's target
    /// is, with no priority of its own: see <see cref="IsUrgentMover"/>, which a
    /// runner deliberately does not join. Panic does not entitle anybody to the
    /// corridor.
    /// </summary>
    private GridPoint? PrimaryDestination(CreatureState creature)
    {
        if (creature.Mode == CreatureMode.Fled)
        {
            return FleeTile(creature);
        }

        if (creature.IsMustering)
        {
            return creature.MusterNeedsRation
                ? creature.SpecialTarget
                : creature.MusterTarget;
        }

        if (creature.MealReserved)
        {
            return creature.SpecialTarget;
        }

        return creature.CurrentJob?.Target;
    }

    private bool TryPlanYield(
        CreatureState blocker,
        CreatureState beneficiary,
        GridPoint beneficiaryTarget,
        IReadOnlySet<GridPoint> requested,
        bool dependencyCycle)
    {
        if (!CanYield(blocker, allowUrgent: dependencyCycle))
        {
            return false;
        }

        var occupants = _creatures.ToDictionary(creature => creature.Position);
        var beneficiaryUrgent = IsUrgentMover(beneficiary);
        var queue = new Queue<GridPoint>();
        var previous = new Dictionary<GridPoint, GridPoint?>();
        queue.Enqueue(blocker.Position);
        previous[blocker.Position] = null;
        GridPoint? openTarget = null;

        while (queue.TryDequeue(out var current) && openTarget is null)
        {
            foreach (var neighbor in PrototypeMap.Neighbors(current))
            {
                if (previous.ContainsKey(neighbor) ||
                    !_map.IsPassable(neighbor) ||
                    _map[neighbor] == TileKind.Larder ||
                    neighbor == PrototypeMap.Gate ||
                    _zones[ZoneKind.Forbidden].Contains(neighbor) ||
                    (!beneficiaryUrgent && requested.Contains(neighbor)) ||
                    _yieldReservations.Contains(neighbor))
                {
                    continue;
                }

                previous[neighbor] = current;
                if (!occupants.TryGetValue(neighbor, out var occupant))
                {
                    openTarget = neighbor;
                    break;
                }

                if (CanYield(occupant, allowUrgent: false))
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        if (openTarget is null)
        {
            return false;
        }

        var path = new List<GridPoint>();
        for (GridPoint? point = openTarget; point is { } value; point = previous[value])
        {
            path.Add(value);
        }

        path.Reverse();
        for (var index = path.Count - 2; index >= 0; index--)
        {
            var actor = occupants[path[index]];
            var target = path[index + 1];
            actor.TrafficTarget = target;
            actor.WaitThisTick = false;
            _yieldReservations.Add(target);
            RecordDecision(
                actor,
                "chosen_traffic_yield",
                new Dictionary<string, int>
                {
                    ["beneficiaryId"] = beneficiary.Id,
                    ["dependencyCycle"] = dependencyCycle ? 1 : 0,
                    ["targetX"] = target.X,
                    ["targetY"] = target.Y,
                },
                actor.CurrentJob?.Kind,
                target);
        }

        return true;
    }

    /// <summary>
    /// Whether this creature may be told to step aside.
    ///
    /// The order has to go to somebody who will actually take the step, and two
    /// modes will not (Issue #119). <see cref="ActCreatures"/> hands a
    /// <see cref="CreatureMode.Fighting"/> creature to
    /// <see cref="ActCombatant"/> before it ever reads
    /// <c>TrafficTarget</c>, and it skips a <see cref="CreatureMode.Downed"/>
    /// one outright. Choosing either as the yielder cost the tick twice: the
    /// booked tile stayed shut for everybody, including the creature the yield
    /// was made for, and <c>chosen_traffic_yield</c> went into the canonical log
    /// for a move that never happened.
    ///
    /// Measured on the hall layout of `main`, per party over the seed matrix:
    /// 21 to 109 such orders to a defender in a fight and 0 to 173 to a creature
    /// on the floor. The dungeon of Issue #117 is what made the cost visible —
    /// in a hall the traffic walks around a tile locked for nothing, in a
    /// doorway there is nothing to walk around — and the traffic measurement
    /// behind the decision to fix it here is
    /// <c>evidence/117-traffic.json</c>.
    ///
    /// <see cref="CreatureMode.Fled"/> is deliberately not in the list: a runner
    /// does read the booking and does take the step, which is the half of this
    /// Issue #101 already closed.
    /// </summary>
    private bool CanYield(CreatureState creature, bool allowUrgent)
    {
        return creature.TrafficTarget is null &&
            creature.Mode is not (CreatureMode.Fighting or CreatureMode.Downed) &&
            (allowUrgent ||
             (!creature.MealReserved && !creature.IsMustering)) &&
            PrimaryDestination(creature) != creature.Position;
    }

    private static bool IsUrgentMover(CreatureState creature)
    {
        return creature.MealReserved || creature.MusterNeedsRation;
    }

    private int FairnessKey(CreatureState creature)
    {
        var count = _creatures.Count;
        return (creature.Id - CurrentTick % count + count) % count;
    }
}
