namespace DungeonFortress.Simulation;

// What the domain has put away and who has claimed it: meals, beds,
// stockpile and build-site stone, their reservations and revalidation.
public sealed partial class PrototypeWorld
{
    private bool CanAdvanceMealQueue(CreatureState creature, out GridPoint target)
    {
        var larderTiles = ActiveLarderTiles().ToArray();
        if (larderTiles.Length == 0)
        {
            target = default;
            RecordDecision(
                creature,
                "refused_zone_unreachable",
                new Dictionary<string, int>
                {
                    ["zoneKind"] = (int)ZoneKind.Larder,
                });
            return false;
        }

        var reserved = _creatures
            .Where(candidate => candidate.MealReserved)
            .ToArray();
        var musterQueue = reserved.Any(candidate => candidate.IsMustering);
        var queue = musterQueue
            ? reserved.OrderBy(candidate => candidate.Id).ToArray()
            : reserved
                .OrderBy(candidate =>
                    larderTiles.Contains(candidate.Position) ? 0 : 1)
                .ThenBy(candidate => candidate.Position)
                .ThenBy(candidate => candidate.Id)
                .ToArray();
        var index = Array.IndexOf(queue, creature);
        var activeCount = Math.Min(queue.Length, larderTiles.Length);
        if (index < 0 || index >= activeCount)
        {
            target = default;
            return false;
        }

        var active = queue.Take(activeCount).ToArray();
        var occupiedAssignment = active
            .Where(candidate => larderTiles.Contains(candidate.Position))
            .ToDictionary(candidate => candidate.Id, candidate => candidate.Position);
        if (occupiedAssignment.TryGetValue(creature.Id, out target))
        {
            return true;
        }

        var claimed = occupiedAssignment.Values.ToHashSet();
        foreach (var candidate in active
                     .Where(candidate => !occupiedAssignment.ContainsKey(candidate.Id)))
        {
            var assignment = larderTiles
                .Where(tile => !claimed.Contains(tile))
                .Select(tile => new
                {
                    Tile = tile,
                    Distance = _map.Distance(
                        candidate.Position,
                        tile,
                        _zones[ZoneKind.Forbidden]),
                })
                .Where(item => item.Distance is not null)
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Tile)
                .FirstOrDefault();
            if (assignment is null)
            {
                if (candidate == creature)
                {
                    target = default;
                    RecordDecision(
                        creature,
                        "refused_zone_unreachable",
                        new Dictionary<string, int>
                        {
                            ["zoneKind"] = (int)ZoneKind.Larder,
                        });
                    return false;
                }

                continue;
            }

            occupiedAssignment[candidate.Id] = assignment.Tile;
            claimed.Add(assignment.Tile);
        }

        if (!occupiedAssignment.TryGetValue(creature.Id, out target))
        {
            return false;
        }

        var assignedTarget = target;
        var laneOccupied = _creatures.Any(other =>
            other != creature &&
            other.CurrentJob is not null &&
            other.Position == assignedTarget);
        if (laneOccupied)
        {
            RecordMovementBlocked(creature, target);
            return false;
        }

        return true;
    }

    private void ApplyPassiveProcesses()
    {
        foreach (var bed in _beds.Values)
        {
            if (!bed.IsRipe)
            {
                bed.Growth++;
            }
        }

        if ((CurrentTick + 1) % PrototypeTuning.SatietyDecayPeriod == 0)
        {
            foreach (var creature in _creatures)
            {
                creature.Satiety = Math.Max(0, creature.Satiety - 1);
            }
        }

        _peakMeals = Math.Max(_peakMeals, _stockMeals);
        MendTheWounded();
    }

    /// <summary>
    /// A wound closes over time in a creature that rests and eats. It is the one
    /// thing the window between two waves needed to become a decision: the
    /// domain spends a bunk and a portion on getting somebody back on their
    /// feet, or it meets the next wave short-handed.
    ///
    /// Healing is read off the same health share that set the wound in the first
    /// place, so there is one rule and not two: above the light-injury share the
    /// wound stops being heavy, and at full health it is gone. Nothing here
    /// mends a creature that is not lying down and fed.
    ///
    /// <para>How much one period returns is <see cref="PrototypeTuning.HpRecoveryStep"/>
    /// and not the literal 1 it used to be. That 1 was a number denominated in
    /// the health units of before Issue #336, and leaving it alone while health
    /// grew eight times would have made every wound take eight times as long to
    /// close — a change to what the window between two waves is worth, decided by
    /// nobody. This is scope item 4 of #336, «пересмотреть числа, привязанные к
    /// длине боя», in the one place where the number is not about the fight at
    /// all but about what the fight leaves behind.</para>
    /// </summary>
    private void MendTheWounded()
    {
        foreach (var creature in _creatures.OrderBy(creature => creature.Id))
        {
            if (creature.Injury == InjuryKind.None ||
                creature.Mode != CreatureMode.Resting ||
                creature.Satiety < PrototypeTuning.RecoveryMinSatiety)
            {
                continue;
            }

            creature.RecoveryTicks++;
            if (creature.RecoveryTicks % PrototypeTuning.HpRecoveryPeriod != 0)
            {
                continue;
            }

            creature.Hp = Math.Min(
                creature.MaxHp, creature.Hp + PrototypeTuning.HpRecoveryStep);
            var before = creature.Injury;
            // The one rule, applied to each part rather than to one scalar. The
            // health share that set a wound is still the health share that
            // closes it, so the worst part after this loop is exactly the value
            // the scalar would have taken — which is what makes localisation a
            // change of what is recorded and not yet a change of what happens.
            foreach (var part in BodyParts.All)
            {
                var mendedPart = creature.PartInjury(part) switch
                {
                    InjuryKind.Heavy when creature.Hp * 100 >
                        creature.MaxHp * PrototypeTuning.LightInjuryShare => InjuryKind.Light,
                    InjuryKind.Light when creature.Hp >= creature.MaxHp => InjuryKind.None,
                    var unchanged => unchanged,
                };
                creature.SetPartInjury(part, mendedPart);
            }

            var mended = creature.Injury;
            if (mended == before)
            {
                continue;
            }

            creature.RecoveryTicks = 0;
            RecordDecision(
                creature,
                mended == InjuryKind.None ? "injury_healed" : "injury_mending",
                new Dictionary<string, int>
                {
                    ["hp"] = creature.Hp,
                    ["maxHp"] = creature.MaxHp,
                },
                JobKind.Rest);
        }
    }

    private void ConsumeReservedMeal(CreatureState creature)
    {
        if (!creature.MealReserved)
        {
            return;
        }

        if (_stockMeals <= 0)
        {
            creature.MealReserved = false;
            return;
        }

        _stockMeals--;
        MealsEaten++;
        creature.Satiety = Math.Min(100, creature.Satiety + PrototypeTuning.MealSatiety);
        creature.MealReserved = false;
    }

    private void CancelMealReservation(CreatureState creature)
    {
        creature.MealReserved = false;
        creature.SpecialTarget = null;
        creature.SpecialTicks = 0;
    }

    private int AvailableMealsForReservation()
    {
        return Math.Max(0, _stockMeals - ReservedMeals());
    }

    private int ReservedMeals()
    {
        return _creatures.Count(creature => creature.MealReserved);
    }

    /// <summary>
    /// The muster rule is now a standing order rather than a one-off: it fires
    /// ahead of every wave that has not arrived yet. That is what makes it a
    /// real trade — thirty per cent of each gap spent standing in the gathering
    /// zone instead of working.
    /// </summary>
    private bool IsMusterActive()
    {
        var lead = _rules["muster_lead_ticks"];
        if (lead <= 0 || CurrentWave() is not { } wave || wave.ArriveTick <= CurrentTick)
        {
            return false;
        }

        return CurrentTick >= wave.ArriveTick - lead;
    }

    private GridPoint MusterTargetFor(int creatureId)
    {
        var zone = _zones[ZoneKind.Watch].Count > 0
            ? _zones[ZoneKind.Watch]
            : _zones[ZoneKind.Larder];
        if (creatureId < zone.Count)
        {
            return zone.ElementAt(creatureId);
        }

        return _map.PassableTiles()
            .Where(tile => !zone.Contains(tile) && tile != PrototypeMap.Gate)
            .Select(tile => new
            {
                Tile = tile,
                Distance = zone
                    .Select(target => _map.Distance(tile, target, _zones[ZoneKind.Forbidden]))
                    .Where(distance => distance is not null)
                    .Min() ?? int.MaxValue,
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Tile)
            .ElementAt(creatureId - zone.Count)
            .Tile;
    }

    private bool TryInitialTarget(
        CreatureState creature,
        JobState job,
        out GridPoint target)
    {
        if (job.Kind == JobKind.Dig)
        {
            // The worker never enters the rock: it stands on a neighbouring tile.
            return TryFindDigApproach(creature, job.Origin, out target);
        }

        if (job.Kind != JobKind.Cook)
        {
            target = job.Origin;
            return true;
        }

        return TryFindLarderTarget(creature, out target, out var availability) &&
            availability == LarderAvailability.Available;
    }

    private bool TryFindLarderTarget(
        CreatureState creature,
        out GridPoint target,
        out LarderAvailability availability)
    {
        var reachable = ActiveLarderTiles()
            .Select(candidate => new
            {
                Target = candidate,
                Distance = _map.Distance(
                    creature.Position,
                    candidate,
                    _zones[ZoneKind.Forbidden]),
                Claims = _creatures.Count(other =>
                    other != creature &&
                    (other.CurrentJob?.Target == candidate ||
                     (other.MealReserved &&
                      other.SpecialTarget == candidate))),
            })
            .Where(item => item.Distance is not null)
            .OrderBy(item => item.Claims)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Target)
            .ToArray();
        if (reachable.Length == 0)
        {
            target = default;
            availability = LarderAvailability.Unreachable;
            return false;
        }

        var available = reachable.FirstOrDefault(item =>
            !_creatures.Any(other =>
                other != creature &&
                other.Position == item.Target));
        if (available is not null)
        {
            target = available.Target;
            availability = LarderAvailability.Available;
            return true;
        }

        target = reachable[0].Target;
        availability = LarderAvailability.Occupied;
        return true;
    }

    private IEnumerable<GridPoint> ActiveLarderTiles()
    {
        return PrototypeMap.LarderTiles
            .Where(tile => _zones[ZoneKind.Larder].Contains(tile))
            .Order();
    }

    /// <summary>
    /// Stockpile cells a creature could actually use. A cell inside
    /// <see cref="ZoneKind.Forbidden"/> keeps whatever it already stores but stops
    /// being a destination, because nobody is allowed to walk onto it.
    /// </summary>
    private IEnumerable<GridPoint> UsableStockpileCells()
    {
        return _zones[ZoneKind.MaterialStockpile]
            .Where(tile =>
                _map.IsPassable(tile) &&
                !_zones[ZoneKind.Forbidden].Contains(tile))
            .Order();
    }

    private int StoredStoneAt(GridPoint tile) => _storedStone.GetValueOrDefault(tile);

    /// <summary>
    /// Stone already booked by a live Haul job for this cell. Counting it is what
    /// prevents two creatures from filling the same last free slot.
    /// </summary>
    private int IncomingStoneAt(GridPoint tile)
    {
        return _jobs
            .Where(job => job.StoreCell == tile)
            .Sum(job => job.StoreReserved);
    }

    private int FreeStoneCapacityAt(GridPoint tile)
    {
        return Math.Max(
            0,
            PrototypeTuning.StockpileCellCapacity - StoredStoneAt(tile) - IncomingStoneAt(tile));
    }

    private int AvailableStoneCapacity()
    {
        return UsableStockpileCells().Sum(FreeStoneCapacityAt);
    }

    private int StoredStoneTotal() => _storedStone.Values.Sum();

    private int CarriedStoneTotal()
    {
        return _creatures
            .Where(creature => creature.Carrying == ResourceKind.Stone)
            .Sum(creature => creature.CarryAmount);
    }

    private int ReservedStoneTotal()
    {
        return _jobs.Sum(job => job.StoreReserved);
    }

    /// <summary>
    /// A construction site the crew may actually serve. Like a stockpile cell, a
    /// site inside <see cref="ZoneKind.Forbidden"/> keeps whatever was delivered
    /// but stops being a destination, because nobody may walk onto it.
    /// </summary>
    private bool IsBuildSiteWorkable(GridPoint tile)
    {
        return _map.IsBuildableFloor(tile) && !_zones[ZoneKind.Forbidden].Contains(tile);
    }

    private int FreeBuildDemandAt(GridPoint tile)
    {
        return _buildSites.TryGetValue(tile, out var site)
            ? Math.Max(0, PrototypeTuning.BuildStoneCost - site.Delivered - IncomingStoneAt(tile))
            : 0;
    }

    private int AvailableBuildDemand()
    {
        return _buildSites.Keys.Where(IsBuildSiteWorkable).Sum(FreeBuildDemandAt);
    }

    private int SiteStoneTotal() => _buildSites.Values.Sum(site => site.Delivered);

    /// <summary>
    /// Free room at a stone destination, whichever kind it is. Stockpile cells and
    /// construction sites can never share a tile, so one tile has exactly one
    /// meaning here and one <see cref="IncomingStoneAt"/> booking.
    /// </summary>
    private int FreeStoneRoomAt(GridPoint tile)
    {
        return _buildSites.ContainsKey(tile)
            ? FreeBuildDemandAt(tile)
            : FreeStoneCapacityAt(tile);
    }

    /// <summary>
    /// Picks where a load of stone should go: nearest to the job's origin, ties
    /// broken by tile order. Construction sites outrank stockpile cells, because a
    /// blueprint is an explicit player intention and putting the stone away first
    /// would make the crew carry it twice. A load withdrawn from the stockpile may
    /// only go to a site, which is what stops material from circling.
    /// It is a pure function of canonical state, so the same log always produces
    /// the same destination.
    /// </summary>
    private bool TryPlanStoneDestination(
        JobState job,
        GridPoint from,
        int quantity,
        out GridPoint cell,
        out int amount)
    {
        var candidates = _buildSites.Keys
            .Where(tile => IsBuildSiteWorkable(tile) && FreeBuildDemandAt(tile) > 0)
            .Select(tile => new { Tier = 0, Tile = tile })
            .ToList();
        if (job.SourceCell is null)
        {
            candidates.AddRange(UsableStockpileCells()
                .Where(tile => FreeStoneCapacityAt(tile) > 0)
                .Select(tile => new { Tier = 1, Tile = tile }));
        }

        var candidate = candidates
            .Select(item => new
            {
                item.Tier,
                item.Tile,
                Distance = _map.Distance(from, item.Tile, _zones[ZoneKind.Forbidden]),
            })
            .Where(item => item.Distance is not null)
            .OrderBy(item => item.Tier)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Tile)
            .FirstOrDefault();
        if (candidate is null)
        {
            cell = default;
            amount = 0;
            return false;
        }

        cell = candidate.Tile;
        amount = Math.Min(quantity, FreeStoneRoomAt(cell));
        return true;
    }

    private void ReleaseStoreReservation(JobState job)
    {
        job.StoreCell = null;
        job.StoreReserved = 0;
    }

    /// <summary>
    /// A destination stays valid only while it is still a usable stockpile cell
    /// with room for the booked load. Erase, Forbidden and a full cell all
    /// invalidate it, and the carrier is re-planned rather than left walking to a
    /// place that can no longer accept the stone.
    /// </summary>
    private bool IsStoreCellStillValid(JobState job, GridPoint from)
    {
        if (job.StoreCell is not { } cell)
        {
            return false;
        }

        if (_map.Distance(from, cell, _zones[ZoneKind.Forbidden]) is null)
        {
            return false;
        }

        // A construction site is a destination on exactly the same terms: it must
        // still exist, still be reachable, and still have room for the booking.
        if (_buildSites.TryGetValue(cell, out var site))
        {
            return IsBuildSiteWorkable(cell) &&
                site.Delivered + job.StoreReserved <= PrototypeTuning.BuildStoneCost;
        }

        return _zones[ZoneKind.MaterialStockpile].Contains(cell) &&
            _map.IsPassable(cell) &&
            !_zones[ZoneKind.Forbidden].Contains(cell) &&
            StoredStoneAt(cell) + job.StoreReserved <= PrototypeTuning.StockpileCellCapacity;
    }

    /// <summary>
    /// Runs before job cancellation each tick. Stone that is already on a
    /// creature's back is never deleted here: it is either re-routed to another
    /// cell or put down as a loose pile on the tile the carrier stands on.
    /// </summary>
    private void RevalidateStoneHauls()
    {
        foreach (var job in _jobs
                     .Where(item =>
                         item.Kind == JobKind.Haul &&
                         item.Resource == ResourceKind.Stone &&
                         item.StoreCell is not null)
                     .OrderBy(item => item.Id)
                     .ToArray())
        {
            var carrier = _creatures.FirstOrDefault(creature => creature.CurrentJob == job);
            var from = carrier?.Position ?? job.Origin;
            if (IsStoreCellStillValid(job, from))
            {
                continue;
            }

            var previous = job.StoreCell!.Value;
            var wanted = job.PickedUp && carrier is not null
                ? carrier.CarryAmount
                : job.Quantity;
            ReleaseStoreReservation(job);
            if (TryPlanStoneDestination(job, from, wanted, out var replacement, out var amount) &&
                amount > 0)
            {
                job.StoreCell = replacement;
                job.StoreReserved = amount;
                if (job.PickedUp)
                {
                    job.Target = replacement;
                }
                else
                {
                    // Keep the job's intent equal to what it may actually deliver,
                    // so the pile is not half-lifted and then put straight back.
                    job.Quantity = amount;
                }

                if (carrier is not null)
                {
                    RecordDecision(
                        carrier,
                        "stone_target_replanned",
                        new Dictionary<string, int>
                        {
                            ["fromX"] = previous.X,
                            ["fromY"] = previous.Y,
                            ["toX"] = replacement.X,
                            ["toY"] = replacement.Y,
                            ["quantity"] = amount,
                        },
                        JobKind.Haul,
                        replacement);
                }

                continue;
            }

            // Nowhere left to put it. Dropping the load where the carrier stands is
            // the only conserving answer; teleporting it back would be a lie.
            if (carrier is null)
            {
                _jobs.Remove(job);
                continue;
            }

            var dropped = carrier.CarryAmount;
            if (job.PickedUp && dropped > 0)
            {
                AddLoose(carrier.Position, ResourceKind.Stone, dropped);
                carrier.Carrying = null;
                carrier.CarryAmount = 0;
                _stoneSpilled += dropped;
            }

            RecordDecision(
                carrier,
                "stone_haul_cancelled",
                new Dictionary<string, int>
                {
                    ["jobId"] = checked((int)job.Id),
                    ["dropped"] = job.PickedUp ? dropped : 0,
                    ["cellX"] = previous.X,
                    ["cellY"] = previous.Y,
                },
                JobKind.Haul,
                carrier.Position);
            carrier.CurrentJob = null;
            carrier.Mode = CreatureMode.Waiting;
            _jobs.Remove(job);
        }
    }

    private bool TryNearestRestTarget(
        CreatureState creature,
        IReadOnlySet<GridPoint> claimed,
        out GridPoint target)
    {
        if (_priorities[JobKind.Rest] == 0)
        {
            target = default;
            return false;
        }

        var candidate = PrototypeMap.BunkTiles
            .Where(tile =>
                _zones[ZoneKind.Quarters].Contains(tile) &&
                !_zones[ZoneKind.Forbidden].Contains(tile) &&
                !claimed.Contains(tile) &&
                !_creatures.Any(other =>
                    other != creature && other.Position == tile) &&
                !_jobs.Any(job =>
                    job.Kind == JobKind.Rest &&
                    job.Origin == tile &&
                    job.PersonalCreatureId != creature.Id))
            .Select(tile => new
            {
                Target = tile,
                Distance = _map.Distance(
                    creature.Position,
                    tile,
                    _zones[ZoneKind.Forbidden]),
            })
            .Where(item => item.Distance is not null)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Target)
            .FirstOrDefault();
        if (candidate is null)
        {
            target = default;
            return false;
        }

        target = candidate.Target;
        return true;
    }

    private int WorkDuration(CreatureState creature, JobKind kind)
    {
        var baseDuration = kind switch
        {
            JobKind.Harvest => PrototypeTuning.HarvestTicks,
            JobKind.Haul => PrototypeTuning.HaulTransferTicks,
            JobKind.Cook => PrototypeTuning.CookTicks,
            JobKind.Drill => PrototypeTuning.DrillTicks,
            JobKind.Dig => PrototypeTuning.DigTicks,
            JobKind.Build => PrototypeTuning.BuildTicks,
            JobKind.Rest or JobKind.Watch => int.MaxValue,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (baseDuration == int.MaxValue)
        {
            return baseDuration;
        }

        var exhausted = creature.Fatigue > PrototypeTuning.RestThreshold &&
            !TryNearestRestTarget(
                creature,
                new HashSet<GridPoint>(),
                out _);
        var multiplier = exhausted ? PrototypeTuning.ExhaustedSpeedMultiplier : 1;
        return Math.Max(
            1,
            baseDuration * multiplier * PrototypeTuning.AffinitySpeedDenominator /
            (PrototypeTuning.AffinitySpeedDenominator + creature.Affinity(kind)));
    }

    private int Urgency(JobKind kind, ResourceKind? resource)
    {
        return kind switch
        {
            JobKind.Cook when _stockMeals <= PrototypeTuning.LowMealsThreshold =>
                PrototypeTuning.UrgencyLowMeals,
            JobKind.Haul when resource == ResourceKind.Meal =>
                PrototypeTuning.UrgencyHaulMeal,
            JobKind.Haul when resource == ResourceKind.RawMushroom =>
                PrototypeTuning.UrgencyHaulRaw,
            JobKind.Haul when resource == ResourceKind.Stone =>
                PrototypeTuning.UrgencyHaulStone,
            JobKind.Harvest when _beds.Values.Count(bed => bed.IsRipe) >=
                PrototypeTuning.RipeBacklogThreshold =>
                PrototypeTuning.UrgencyRipeBacklog,
            _ => 0,
        };
    }

    private int DiagnosticUrgency(JobKind kind)
    {
        return _jobs
            .Where(job => job.Kind == kind && job.ReservedBy is null)
            .Select(job => Urgency(job.Kind, job.Resource))
            .DefaultIfEmpty(0)
            .Max();
    }

    private bool AnyReachableTarget(CreatureState creature, JobKind kind)
    {
        bool Reachable(GridPoint target)
        {
            return _map.Distance(
                creature.Position,
                target,
                _zones[ZoneKind.Forbidden]) is not null;
        }

        return kind switch
        {
            JobKind.Harvest => PrototypeMap.BedTiles.Any(tile =>
                _zones[ZoneKind.Farm].Contains(tile) && Reachable(tile)),
            // A haul is reachable when some unreserved job has both a reachable
            // pile and a reachable destination — the larder for food, a usable
            // stockpile cell for stone.
            JobKind.Haul => _jobs.Any(job =>
                job.Kind == JobKind.Haul &&
                job.ReservedBy is null &&
                Reachable(job.Origin) &&
                (job.Resource == ResourceKind.Stone
                    ? UsableStockpileCells().Any(Reachable)
                    : ActiveLarderTiles().Any(Reachable))),
            JobKind.Cook => PrototypeMap.KitchenTiles.Any(tile =>
                    _zones[ZoneKind.Kitchen].Contains(tile) && Reachable(tile)) &&
                ActiveLarderTiles().Any(Reachable),
            JobKind.Rest => PrototypeMap.BunkTiles.Any(tile =>
                _zones[ZoneKind.Quarters].Contains(tile) && Reachable(tile)),
            JobKind.Drill => _map.PostTiles().Any(tile =>
                _zones[ZoneKind.TrainingGround].Contains(tile) && Reachable(tile)),
            JobKind.Watch => _zones[ZoneKind.Watch].Any(Reachable),
            JobKind.Dig => _digDesignations.Any(tile =>
                _map.IsDiggable(tile) && DigApproachTiles(tile).Any(Reachable)),
            // A blueprint is only workable once its stone has arrived: "no
            // material yet" and "cannot get there" must stay different answers.
            JobKind.Build => _buildSites.Values.Any(site =>
                site.Delivered >= PrototypeTuning.BuildStoneCost &&
                IsBuildSiteWorkable(site.Tile) &&
                Reachable(site.Tile)),
            _ => false,
        };
    }

    private static int Percentage(int numerator, int denominator)
    {
        return denominator == 0
            ? 0
            : numerator * 100 / denominator;
    }

    private bool ZoneCoversFeature(ZoneKind zone, TileKind feature)
    {
        return _zones[zone].Any(tile => _map[tile] == feature);
    }

    private bool HasLoose(GridPoint point, ResourceKind resource)
    {
        return LooseAt(point, resource) > 0;
    }

    private int LooseAt(GridPoint point, ResourceKind resource)
    {
        return _loose.GetValueOrDefault((point, resource));
    }

    private int LooseCount(ResourceKind resource)
    {
        return _loose
            .Where(pair => pair.Key.Resource == resource)
            .Sum(pair => pair.Value);
    }

    private void AddLoose(GridPoint point, ResourceKind resource, int quantity)
    {
        if (quantity <= 0)
        {
            return;
        }

        _loose[(point, resource)] = LooseAt(point, resource) + quantity;
    }

    private void RemoveLoose(GridPoint point, ResourceKind resource, int quantity)
    {
        var remaining = LooseAt(point, resource) - quantity;
        if (remaining <= 0)
        {
            _loose.Remove((point, resource));
        }
        else
        {
            _loose[(point, resource)] = remaining;
        }
    }

    private void RecordDecision(
        CreatureState creature,
        string reason,
        Dictionary<string, int> details,
        JobKind? kind = null,
        GridPoint? target = null)
    {
        creature.LastDecision = new PrototypeDecision(CurrentTick, reason, details, kind, target);
        var previous = _events.LastOrDefault(@event => @event.CreatureId == creature.Id);
        if (previous is not null &&
            previous.ReasonCode == reason &&
            previous.JobKind == kind &&
            previous.Target == target &&
            DetailsEqual(previous.Details, details))
        {
            previous.LastTick = CurrentTick;
            previous.Repeats++;
            return;
        }

        _events.Add(new EventState(
            CurrentTick,
            creature.Id,
            reason,
            details,
            kind,
            target));
    }

    private static bool DetailsEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        return left.Count == right.Count &&
            left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }
}
