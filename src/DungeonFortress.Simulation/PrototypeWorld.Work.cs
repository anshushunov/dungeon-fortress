namespace DungeonFortress.Simulation;

// Carrying the work out: mustering, eating, working a job, stone in
// hand, finishing or cancelling a job, and moving a body one step.
public sealed partial class PrototypeWorld
{
    private static int Manhattan(GridPoint left, GridPoint right) => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private void CountLaborForTick()
    {
        _totalCreatureTicks += _creatures.Count;
        foreach (var creature in _creatures)
        {
            if (creature.Mode == CreatureMode.Eating ||
                (creature.IsMustering && creature.MusterNeedsRation))
            {
                _eatTicks++;
            }
            else if (creature.IsMustering)
            {
                _musterTicks++;
            }
            else
            {
                // Stone logistics is construction labour, not food labour: folding
                // it into foodWorkTicks would silently inflate the food share the
                // contract corridors are measured against.
                if (creature.CurrentJob is { Kind: JobKind.Haul, Resource: ResourceKind.Stone })
                {
                    _stoneHaulTicks++;
                    continue;
                }

                switch (creature.CurrentJob?.Kind)
                {
                    case JobKind.Harvest:
                    case JobKind.Haul:
                    case JobKind.Cook:
                        _foodWorkTicks++;
                        break;
                    case JobKind.Rest:
                        _restTicks++;
                        break;
                    case JobKind.Drill:
                        _drillTicks++;
                        break;
                    case JobKind.Watch:
                        _watchTicks++;
                        break;
                    case JobKind.Dig:
                        _digTicks++;
                        break;
                    case JobKind.Build:
                        _buildTicks++;
                        break;
                    default:
                        _idleTicks++;
                        break;
                }
            }
        }
    }

    private void CountPostOccupancyForTick()
    {
        foreach (var station in _stationOccupiedTicks.Keys.ToArray())
        {
            if (_creatures.Any(creature => IsUsingStation(creature, station)))
            {
                _stationOccupiedTicks[station]++;
            }
        }

        // Training is no longer something that only happens before the one raid,
        // so post capacity is counted for as long as the party runs.
        var activePosts = _map.PostTiles()
            .Where(point => _zones[ZoneKind.TrainingGround].Contains(point))
            .ToArray();
        _postCapacityTicks += activePosts.Length;
    }

    private bool IsUsingStation(CreatureState creature, GridPoint point)
    {
        return creature.Position == point &&
            (creature.CurrentJob?.Kind, _map[point]) switch
            {
                (JobKind.Cook, TileKind.Kitchen) => true,
                (JobKind.Drill, TileKind.Post) => true,
                _ => false,
            };
    }

    private void ActMuster(CreatureState creature)
    {
        if (creature.MusterNeedsRation)
        {
            if (!CanAdvanceMealQueue(creature, out var larder))
            {
                return;
            }

            if (creature.Position != larder)
            {
                _ = Move(creature, larder);
                return;
            }

            creature.Mode = CreatureMode.Eating;
            if (creature.SpecialTicks == 0)
            {
                creature.SpecialTicks = PrototypeTuning.EatTicks;
            }

            creature.SpecialTicks--;
            if (creature.SpecialTicks == 0)
            {
                ConsumeReservedMeal(creature);
                creature.MusterNeedsRation = false;
                creature.Mode = CreatureMode.Mustering;
            }

            return;
        }

        creature.Mode = CreatureMode.Mustering;
        if (creature.MusterTarget is { } target && creature.Position != target)
        {
            _ = Move(creature, target);
        }
    }

    private void ActEating(CreatureState creature)
    {
        if (!CanAdvanceMealQueue(creature, out var queueTarget))
        {
            return;
        }

        creature.SpecialTarget = queueTarget;
        if (creature.SpecialTarget is not { } target)
        {
            CancelMealReservation(creature);
            creature.Mode = CreatureMode.Waiting;
            return;
        }

        if (creature.Position != target)
        {
            _ = Move(creature, target);
            return;
        }

        creature.SpecialTicks--;
        if (creature.SpecialTicks <= 0)
        {
            ConsumeReservedMeal(creature);
            creature.Mode = CreatureMode.Waiting;
            creature.SpecialTarget = null;
        }
    }

    private void ActJob(CreatureState creature, JobState job)
    {
        // The larder retry below uses "target == origin" to mean "no lane found".
        // A stone haul never borrows that sentinel: its destination lives in
        // StoreCell and may legitimately be the pile's own tile.
        if (job.Kind == JobKind.Haul &&
            job.Resource != ResourceKind.Stone &&
            job.PickedUp &&
            job.Target == job.Origin)
        {
            if (!TryFindLarderTarget(
                    creature,
                    out var retryTarget,
                    out var retryAvailability))
            {
                RecordDecision(
                    creature,
                    "refused_zone_unreachable",
                    new Dictionary<string, int>
                    {
                        ["jobId"] = checked((int)job.Id),
                    },
                    job.Kind,
                    job.Origin);
                return;
            }

            if (retryAvailability == LarderAvailability.Occupied)
            {
                RecordMovementBlocked(creature, retryTarget);
                return;
            }

            job.Target = retryTarget;
        }

        if (creature.Position != job.Target)
        {
            creature.Mode = CreatureMode.Moving;
            _ = Move(creature, job.Target);
            return;
        }

        creature.Mode = job.Kind switch
        {
            JobKind.Rest => CreatureMode.Resting,
            _ => CreatureMode.Working,
        };

        if (job.Kind == JobKind.Haul && !job.PickedUp &&
            job.Resource == ResourceKind.Stone)
        {
            PickUpStone(creature, job);
            return;
        }

        if (job.Kind == JobKind.Haul && !job.PickedUp)
        {
            var available = LooseAt(job.Origin, job.Resource!.Value);
            var quantity = Math.Min(job.Quantity, available);
            RemoveLoose(job.Origin, job.Resource.Value, quantity);
            if (quantity == 0)
            {
                FinishJob(creature, job);
                return;
            }

            creature.Carrying = job.Resource;
            creature.CarryAmount = quantity;
            job.Quantity = quantity;
            job.PickedUp = true;
            if (!TryFindLarderTarget(
                    creature,
                    out var larder,
                    out var availability))
            {
                job.Target = job.Origin;
                RecordDecision(
                    creature,
                    "refused_zone_unreachable",
                    new Dictionary<string, int>
                    {
                        ["jobId"] = checked((int)job.Id),
                    },
                    job.Kind,
                    job.Origin);
                return;
            }

            if (availability == LarderAvailability.Occupied)
            {
                job.Target = job.Origin;
                RecordMovementBlocked(creature, larder);
                return;
            }

            job.Target = larder;
            return;
        }

        if (job.Kind == JobKind.Cook && !job.PickedUp)
        {
            if (_stockRaw < PrototypeTuning.CookInput)
            {
                CancelJob(creature, "waiting_input_missing");
                return;
            }

            _stockRaw -= PrototypeTuning.CookInput;
            creature.Carrying = ResourceKind.RawMushroom;
            creature.CarryAmount = PrototypeTuning.CookInput;
            job.PickedUp = true;
            job.Target = job.Origin;
            return;
        }

        if (job.Kind == JobKind.Rest)
        {
            job.ProgressTicks++;
            if (job.ProgressTicks % PrototypeTuning.RestRecoveryPeriod == 0)
            {
                creature.Fatigue = Math.Max(0, creature.Fatigue - 1);
            }

            // A wound keeps its owner in the bunk past the fatigue target: the
            // mending in ApplyPassiveProcesses only counts ticks spent resting.
            if (creature.Fatigue <= PrototypeTuning.RestTarget &&
                creature.Injury == InjuryKind.None)
            {
                FinishJob(creature, job);
            }

            return;
        }

        if (job.Kind == JobKind.Watch)
        {
            creature.WatchTicks++;
            if (creature.WatchTicks % PrototypeTuning.WatchFatiguePeriod == 0)
            {
                creature.Fatigue = Math.Min(100, creature.Fatigue + 1);
            }

            return;
        }

        if (job.Kind is JobKind.Dig or JobKind.Build)
        {
            if (job.ProgressTicks == 0)
            {
                RecordDecision(
                    creature,
                    job.Kind == JobKind.Dig ? "dig_started" : "build_started",
                    new Dictionary<string, int>
                    {
                        ["tileX"] = job.Origin.X,
                        ["tileY"] = job.Origin.Y,
                        ["requiredTicks"] = job.RemainingTicks,
                    },
                    job.Kind,
                    job.Origin);
            }

            job.ProgressTicks++;
        }

        creature.WorkTicks++;
        if (creature.WorkTicks % PrototypeTuning.FatigueGainPeriod == 0)
        {
            creature.Fatigue = Math.Min(100, creature.Fatigue + 1);
        }

        job.RemainingTicks--;
        if (job.RemainingTicks > 0)
        {
            return;
        }

        CompleteJob(creature, job);
    }

    /// <summary>
    /// The moment loose stone becomes carried stone. The booking shrinks to the
    /// amount actually lifted, so a pile that shrank in the meantime does not keep
    /// holding stockpile space it will never use.
    /// </summary>
    private void PickUpStone(CreatureState creature, JobState job)
    {
        if (job.StoreCell is not { } cell)
        {
            FinishJob(creature, job);
            return;
        }

        // Two sources, one lifting rule: a loose pile on the tile, or the stone
        // already put away in the stockpile cell the job names as its source.
        var available = job.SourceCell is { } source
            ? StoredStoneAt(source)
            : LooseAt(job.Origin, ResourceKind.Stone);
        // Never lift more than the destination is holding room for. A replan can
        // shrink the booking below the quantity the job was created with, and
        // lifting the old quantity would let this job over-book the new cell.
        var quantity = Math.Min(Math.Min(job.Quantity, available), job.StoreReserved);
        if (quantity <= 0)
        {
            ReleaseStoreReservation(job);
            FinishJob(creature, job);
            return;
        }

        if (job.SourceCell is { } stockpile)
        {
            var remaining = StoredStoneAt(stockpile) - quantity;
            if (remaining <= 0)
            {
                _storedStone.Remove(stockpile);
            }
            else
            {
                _storedStone[stockpile] = remaining;
            }
        }
        else
        {
            RemoveLoose(job.Origin, ResourceKind.Stone, quantity);
        }

        creature.Carrying = ResourceKind.Stone;
        creature.CarryAmount = quantity;
        job.Quantity = quantity;
        job.StoreReserved = quantity;
        job.PickedUp = true;
        job.Target = cell;
        RecordDecision(
            creature,
            "stone_picked_up",
            new Dictionary<string, int>
            {
                ["quantity"] = quantity,
                ["cellX"] = cell.X,
                ["cellY"] = cell.Y,
                ["fromStockpile"] = job.SourceCell is null ? 0 : 1,
            },
            JobKind.Haul,
            cell);
    }

    /// <summary>
    /// The moment carried stone becomes stored stone. Anything the cell cannot
    /// take is put down as a loose pile instead of vanishing, which is what keeps
    /// produced stone equal to loose plus carried plus stored at every tick.
    /// </summary>
    private void StoreCarriedStone(CreatureState creature, JobState job)
    {
        if (job.StoreCell is { } site && _buildSites.ContainsKey(site))
        {
            DeliverCarriedStone(creature, job, site);
            return;
        }

        var carried = creature.CarryAmount;
        var free = job.StoreCell is { } target &&
            creature.Position == target &&
            _zones[ZoneKind.MaterialStockpile].Contains(target)
                ? Math.Max(0, PrototypeTuning.StockpileCellCapacity - StoredStoneAt(target))
                : 0;
        // Bounded by the booking as well as by the room: a carrier whose booking
        // was shrunk by a replan must not spend the slot another job is holding.
        // Whatever it cannot put away is set down here, so nothing is lost.
        var delivered = Math.Min(Math.Min(free, carried), job.StoreReserved);
        if (delivered > 0)
        {
            var cell = job.StoreCell!.Value;
            _storedStone[cell] = StoredStoneAt(cell) + delivered;
            _stoneStored += delivered;
            _stoneHaulsCompleted++;
            RecordDecision(
                creature,
                "stone_stored",
                new Dictionary<string, int>
                {
                    ["quantity"] = delivered,
                    ["cellX"] = cell.X,
                    ["cellY"] = cell.Y,
                    ["stored"] = StoredStoneAt(cell),
                    ["capacity"] = PrototypeTuning.StockpileCellCapacity,
                },
                JobKind.Haul,
                cell);
        }

        var spilled = carried - delivered;
        if (spilled > 0)
        {
            AddLoose(creature.Position, ResourceKind.Stone, spilled);
            _stoneSpilled += spilled;
            RecordDecision(
                creature,
                "stone_spilled",
                new Dictionary<string, int>
                {
                    ["quantity"] = spilled,
                    ["tileX"] = creature.Position.X,
                    ["tileY"] = creature.Position.Y,
                },
                JobKind.Haul,
                creature.Position);
        }

        ReleaseStoreReservation(job);
        creature.Carrying = null;
        creature.CarryAmount = 0;
    }

    /// <summary>
    /// The moment carried stone becomes site stone. The rules are the stockpile's,
    /// with the site's own demand as the capacity: anything the site cannot take is
    /// put down as a loose pile rather than vanishing, so produced stone still
    /// equals loose plus carried plus stored plus delivered plus consumed.
    /// </summary>
    private void DeliverCarriedStone(CreatureState creature, JobState job, GridPoint tile)
    {
        var carried = creature.CarryAmount;
        var site = _buildSites[tile];
        var free = creature.Position == tile
            ? Math.Max(0, PrototypeTuning.BuildStoneCost - site.Delivered)
            : 0;
        var delivered = Math.Min(Math.Min(free, carried), job.StoreReserved);
        if (delivered > 0)
        {
            site.Delivered += delivered;
            _stoneDelivered += delivered;
            RecordDecision(
                creature,
                "stone_delivered",
                new Dictionary<string, int>
                {
                    ["quantity"] = delivered,
                    ["tileX"] = tile.X,
                    ["tileY"] = tile.Y,
                    ["delivered"] = site.Delivered,
                    ["required"] = PrototypeTuning.BuildStoneCost,
                },
                JobKind.Haul,
                tile);
        }

        var spilled = carried - delivered;
        if (spilled > 0)
        {
            AddLoose(creature.Position, ResourceKind.Stone, spilled);
            _stoneSpilled += spilled;
            RecordDecision(
                creature,
                "stone_spilled",
                new Dictionary<string, int>
                {
                    ["quantity"] = spilled,
                    ["tileX"] = creature.Position.X,
                    ["tileY"] = creature.Position.Y,
                },
                JobKind.Haul,
                creature.Position);
        }

        ReleaseStoreReservation(job);
        creature.Carrying = null;
        creature.CarryAmount = 0;
    }

    private void CompleteJob(CreatureState creature, JobState job)
    {
        switch (job.Kind)
        {
            case JobKind.Harvest:
                _beds[job.Origin].Growth = 0;
                AddLoose(job.Origin, ResourceKind.RawMushroom, PrototypeTuning.HarvestOutput);
                _harvestsCompleted++;
                break;
            case JobKind.Haul when job.Resource == ResourceKind.Stone:
                StoreCarriedStone(creature, job);
                break;
            case JobKind.Haul:
                var free = PrototypeTuning.LarderCapacity - _stockRaw - _stockMeals;
                var delivered = Math.Min(free, creature.CarryAmount);
                if (creature.Carrying == ResourceKind.RawMushroom)
                {
                    _stockRaw += delivered;
                    if (delivered > 0)
                    {
                        _rawHaulsCompleted++;
                    }
                }
                else
                {
                    _stockMeals += delivered;
                    if (delivered > 0)
                    {
                        _mealHaulsCompleted++;
                    }
                }

                if (delivered < creature.CarryAmount)
                {
                    AddLoose(
                        creature.Position,
                        creature.Carrying!.Value,
                        creature.CarryAmount - delivered);
                }

                creature.Carrying = null;
                creature.CarryAmount = 0;
                break;
            case JobKind.Cook:
                creature.Carrying = null;
                creature.CarryAmount = 0;
                AddLoose(job.Origin, ResourceKind.Meal, PrototypeTuning.CookOutput);
                MealsProduced += PrototypeTuning.CookOutput;
                _cookBatchesCompleted++;
                break;
            case JobKind.Dig:
                _map.Excavate(job.Origin);
                _digDesignations.Remove(job.Origin);
                AddLoose(job.Origin, ResourceKind.Stone, PrototypeTuning.DigStoneYield);
                _digsCompleted++;
                _stoneProduced += PrototypeTuning.DigStoneYield;
                RecordDecision(
                    creature,
                    "dig_completed",
                    new Dictionary<string, int>
                    {
                        ["tileX"] = job.Origin.X,
                        ["tileY"] = job.Origin.Y,
                        ["stone"] = PrototypeTuning.DigStoneYield,
                    },
                    job.Kind,
                    job.Origin);
                break;
            case JobKind.Build:
                CompleteBuild(creature, job);
                break;
            case JobKind.Drill:
                creature.MartialForm = Math.Min(
                    100,
                    creature.MartialForm + PrototypeTuning.DrillGain);
                creature.Fatigue = Math.Min(
                    100,
                    creature.Fatigue + PrototypeTuning.DrillFatigue);
                creature.Satiety = Math.Max(
                    0,
                    creature.Satiety - PrototypeTuning.DrillSatietyCost);
                break;
        }

        FinishJob(creature, job);
    }

    /// <summary>
    /// The only place stone leaves the world, and the second place the map
    /// changes. The cost is spent, the blueprint is gone, and the tile becomes a
    /// training post that the existing Drill rules cannot tell from an authored
    /// one — which is the whole point of the step.
    /// </summary>
    private void CompleteBuild(CreatureState creature, JobState job)
    {
        var site = _buildSites[job.Origin];
        _buildSites.Remove(job.Origin);
        _stoneConsumed += PrototypeTuning.BuildStoneCost;
        // Defensive: the site can only ever hold what the demand allowed, so a
        // surplus is impossible. If one ever appeared it must not be deleted.
        AddLoose(job.Origin, ResourceKind.Stone, site.Delivered - PrototypeTuning.BuildStoneCost);
        _map.BuildPost(job.Origin);
        _stationOccupiedTicks.TryAdd(job.Origin, 0);
        _buildsCompleted++;
        RecordDecision(
            creature,
            "build_completed",
            new Dictionary<string, int>
            {
                ["tileX"] = job.Origin.X,
                ["tileY"] = job.Origin.Y,
                ["stone"] = PrototypeTuning.BuildStoneCost,
            },
            job.Kind,
            job.Origin);
    }

    private void FinishJob(CreatureState creature, JobState job)
    {
        _jobs.Remove(job);
        creature.CurrentJob = null;
        creature.Mode = CreatureMode.Waiting;
    }

    private void CancelJob(CreatureState creature, string reason)
    {
        if (creature.CurrentJob is not { } job)
        {
            return;
        }

        if (creature.Carrying is { } resource && creature.CarryAmount > 0)
        {
            AddLoose(creature.Position, resource, creature.CarryAmount);
            if (resource == ResourceKind.Stone)
            {
                // Counted here too, so stoneSpilled means one thing everywhere:
                // stone that went back onto the floor after being picked up or
                // stored, whatever interrupted it.
                _stoneSpilled += creature.CarryAmount;
            }

            creature.Carrying = null;
            creature.CarryAmount = 0;
        }

        job.ReservedBy = null;
        job.PickedUp = false;
        job.Target = job.Origin;
        job.RemainingTicks = 0;
        // A cancelled carrier must not keep holding stockpile space; the stone it
        // was carrying has just been dropped as a loose pile above.
        ReleaseStoreReservation(job);
        if (job.Kind is JobKind.Dig or JobKind.Build)
        {
            // Neither excavation nor construction has a partial result: an
            // interrupted tile is untouched rock or an untouched blueprint again,
            // so its progress must not survive the cancellation. The stone already
            // delivered to a blueprint stays on the site; only the labour is lost.
            job.ProgressTicks = 0;
        }

        creature.CurrentJob = null;
        creature.Mode = CreatureMode.Waiting;
        RecordDecision(
            creature,
            reason,
            new Dictionary<string, int>
            {
                ["jobId"] = checked((int)job.Id),
            },
            job.Kind,
            job.Origin);
    }

    private bool Move(CreatureState creature, GridPoint target)
    {
        if (creature.LastMoveTick == CurrentTick)
        {
            return false;
        }

        if (creature.WaitThisTick)
        {
            creature.BlockedTicks++;
            RecordMovementBlocked(creature, target);
            return false;
        }

        var next = StepAroundBodies(creature, target);
        if (next is null)
        {
            RecordDecision(
                creature,
                "refused_zone_unreachable",
                new Dictionary<string, int>
                {
                    ["targetX"] = target.X,
                    ["targetY"] = target.Y,
                },
                creature.CurrentJob?.Kind,
                target);
            return false;
        }

        if (next == creature.Position)
        {
            creature.BlockedTicks = 0;
            return true;
        }

        if (_yieldReservations.Contains(next.Value) &&
            creature.TrafficTarget != next)
        {
            creature.BlockedTicks++;
            RecordMovementBlocked(creature, target);
            return false;
        }

        if (_creatures.Any(other => other != creature && other.Position == next))
        {
            creature.BlockedTicks++;
            RecordMovementBlocked(creature, target);
            return false;
        }

        creature.Position = next.Value;
        creature.MoveCount++;
        creature.LastMoveTick = CurrentTick;
        creature.BlockedTicks = 0;
        return true;
    }

    /// <summary>
    /// One step towards <paramref name="target"/>, round the bodies in the way
    /// where there is a way round and straight at them where there is not.
    ///
    /// <para><b>Issue #76, the half of it this slice raised.</b> Occupancy already
    /// existed at the moment of stepping — <see cref="Move"/> has always refused
    /// to put a creature on a tile another one stands on — but the pathfinder knew
    /// nothing about it, so a creature walked into a body, was refused, and walked
    /// into it again next tick. Measured on this branch: one creature spent a whole
    /// sixty-tick window after a wave doing exactly that
    /// (<c>baseline/20260728</c>, <c>#6 at (23,6) mode=Moving
    /// last=waiting_blocked_by_other</c>), which is the jam the longer fight of
    /// Issue #336 created and the reason #76 was raised out of slice 6.</para>
    ///
    /// <para><b>A body takes away a road and never the objective</b>, which is the
    /// same bound <see cref="RaiderBlockedTiles"/> puts on a raider's memory and
    /// the same fallback: if there is no way round the bodies, the crowded path is
    /// used and <see cref="Move"/>'s own check does the waiting. So occupancy can
    /// make a corridor a throat; it cannot make a destination unreachable.</para>
    ///
    /// <para>The target tile itself is never treated as occupied. A creature is
    /// routinely sent to a tile somebody is standing on — a work tile, a bunk, the
    /// larder — and blocking it would refuse the journey rather than the last
    /// step.</para>
    /// </summary>
    private GridPoint? StepAroundBodies(CreatureState creature, GridPoint target)
    {
        var forbidden = _zones[ZoneKind.Forbidden];
        var blocked = new HashSet<GridPoint>(forbidden);
        foreach (var other in _creatures)
        {
            if (other != creature && other.Position != target)
            {
                blocked.Add(other.Position);
            }
        }

        foreach (var raider in _raiders)
        {
            if (raider.Mode == RaiderMode.Raiding && raider.Position != target)
            {
                blocked.Add(raider.Position);
            }
        }

        blocked.Remove(creature.Position);
        return _map.NextStep(creature.Position, target, blocked)
            ?? _map.NextStep(creature.Position, target, forbidden);
    }

    private void RecordMovementBlocked(CreatureState creature, GridPoint target)
    {
        RecordDecision(
            creature,
            "waiting_blocked_by_other",
            new Dictionary<string, int>
            {
                ["targetX"] = target.X,
                ["targetY"] = target.Y,
            },
            creature.CurrentJob?.Kind,
            target: target);
    }
}
