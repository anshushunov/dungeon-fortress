namespace DungeonFortress.Simulation;

// Player commands: applying one, and putting right what applying it
// invalidated — zones, designations and the jobs that stood on them.
public sealed partial class PrototypeWorld
{
    private void ApplyCommands()
    {
        while (_nextCommandIndex < _commands.Length &&
               _commands[_nextCommandIndex].Tick == CurrentTick)
        {
            ApplyCommand(_commands[_nextCommandIndex]);
            _nextCommandIndex++;
            CommandsApplied++;
        }
    }

    private void ApplyCommand(PrototypeCommand command)
    {
        switch (command)
        {
            case ZonePaintCommand paint:
                ValidateZoneTiles(paint.ZoneKind, paint.Tiles, painting: true);
                foreach (var tile in paint.Tiles)
                {
                    _zones[paint.ZoneKind].Add(tile);
                }

                break;
            case ZoneEraseCommand erase:
                ValidateZoneTiles(erase.ZoneKind, erase.Tiles, painting: false);
                if (erase.ZoneKind == ZoneKind.Larder)
                {
                    var remaining = new SortedSet<GridPoint>(_zones[ZoneKind.Larder]);
                    remaining.ExceptWith(erase.Tiles);
                    if (!remaining.Any(tile => _map[tile] == TileKind.Larder))
                    {
                        throw new InvalidDataException(
                            "zone_erase would remove the final larder feature from Larder.");
                    }
                }

                foreach (var tile in erase.Tiles)
                {
                    if (!_zones[erase.ZoneKind].Remove(tile))
                    {
                        continue;
                    }

                    if (erase.ZoneKind == ZoneKind.MaterialStockpile)
                    {
                        SpillStoredStone(tile);
                    }
                }

                break;
            case DigDesignateCommand designate:
                ApplyDigDesignate(designate);
                break;
            case DigCancelCommand cancel:
                ApplyDigCancel(cancel);
                break;
            case BuildDesignateCommand build:
                ApplyBuildDesignate(build);
                break;
            case BuildCancelCommand unbuild:
                ApplyBuildCancel(unbuild);
                break;
            case SetPriorityCommand priority:
                _priorities[priority.JobKind] = priority.Value;
                break;
            case SetRuleCommand rule:
                _rules[rule.RuleId] = rule.Value;
                break;
            default:
                throw new InvalidDataException($"Unsupported prototype command: {command.GetType().Name}");
        }
    }

    /// <summary>
    /// Strict and atomic: every tile is checked against the live map before the
    /// first designation is recorded, so a rejected command mutates nothing.
    /// Designating an already designated tile is a no-op, matching zone_paint.
    /// </summary>
    private void ApplyDigDesignate(DigDesignateCommand command)
    {
        foreach (var tile in command.Tiles)
        {
            if (!_map.IsDiggable(tile))
            {
                throw new InvalidDataException(
                    $"Dig tile ({tile.X},{tile.Y}) is not diggable rock. " +
                    "Floor, features, the gate and the map boundary cannot be designated.");
            }
        }

        var added = command.Tiles.Count(tile => !_digDesignations.Contains(tile));
        if (_digDesignations.Count + added > PrototypeTuning.MaximumDigDesignations)
        {
            throw new InvalidDataException(
                $"A session cannot hold more than {PrototypeTuning.MaximumDigDesignations} " +
                "dig designations.");
        }

        foreach (var tile in command.Tiles)
        {
            _digDesignations.Add(tile);
        }
    }

    /// <summary>
    /// Tolerant, like zone_erase: tiles without a designation are simply skipped.
    /// Releasing the owning job in the same step keeps the world from holding a
    /// reservation or half-finished progress for an intent the player withdrew.
    /// </summary>
    private void ApplyDigCancel(DigCancelCommand command)
    {
        foreach (var tile in command.Tiles)
        {
            if (!_digDesignations.Remove(tile))
            {
                continue;
            }

            var job = _jobs.FirstOrDefault(
                item => item.Kind == JobKind.Dig && item.Origin == tile);
            if (job is null)
            {
                continue;
            }

            var worker = _creatures.FirstOrDefault(creature => creature.CurrentJob == job);
            if (worker is not null)
            {
                CancelJob(worker, "dig_cancelled");
            }

            _jobs.Remove(job);
        }
    }

    /// <summary>
    /// Strict and atomic, exactly like <see cref="ApplyDigDesignate"/>: every tile
    /// is checked against the live map before the first blueprint is recorded.
    /// Designating an already designated tile is a no-op, matching zone_paint.
    /// </summary>
    private void ApplyBuildDesignate(BuildDesignateCommand command)
    {
        foreach (var tile in command.Tiles)
        {
            if (!_map.IsBuildableFloor(tile))
            {
                throw new InvalidDataException(
                    $"Build tile ({tile.X},{tile.Y}) is not plain floor. " +
                    "Rock, map features, the gate, the map boundary and an existing " +
                    "post cannot hold a blueprint.");
            }

            if (_zones[ZoneKind.MaterialStockpile].Contains(tile))
            {
                throw new InvalidDataException(
                    $"Build tile ({tile.X},{tile.Y}) is a material stockpile cell. " +
                    "Erase the cell first; a building site is not a warehouse.");
            }
        }

        var added = command.Tiles.Count(tile => !_buildSites.ContainsKey(tile));
        if (_buildSites.Count + added > PrototypeTuning.MaximumBuildDesignations)
        {
            throw new InvalidDataException(
                $"A session cannot hold more than {PrototypeTuning.MaximumBuildDesignations} " +
                "build designations.");
        }

        foreach (var tile in command.Tiles)
        {
            if (!_buildSites.ContainsKey(tile))
            {
                _buildSites[tile] = new BuildSiteState(tile);
            }
        }
    }

    /// <summary>
    /// Tolerant, like zone_erase and dig_cancel. Stone already delivered to the
    /// site becomes a loose pile on the very same tile, so withdrawing an intent
    /// never destroys material and never teleports it.
    /// </summary>
    private void ApplyBuildCancel(BuildCancelCommand command)
    {
        foreach (var tile in command.Tiles)
        {
            if (!_buildSites.TryGetValue(tile, out var site))
            {
                continue;
            }

            _buildSites.Remove(tile);
            if (site.Delivered > 0)
            {
                AddLoose(tile, ResourceKind.Stone, site.Delivered);
                _stoneSpilled += site.Delivered;
            }

            var job = _jobs.FirstOrDefault(
                item => item.Kind == JobKind.Build && item.Origin == tile);
            if (job is not null)
            {
                var worker = _creatures.FirstOrDefault(creature => creature.CurrentJob == job);
                if (worker is not null)
                {
                    CancelJob(worker, "build_cancelled");
                }

                _jobs.Remove(job);
            }
        }
    }

    /// <summary>
    /// A designation is workable when at least one orthogonal neighbour is a
    /// passable, non-forbidden tile that is not the gate. Reachability is checked
    /// here rather than at command time because digging changes it.
    /// </summary>
    private IEnumerable<GridPoint> DigApproachTiles(GridPoint rock)
    {
        return PrototypeMap.Neighbors(rock)
            .Where(neighbor =>
                _map.IsPassable(neighbor) &&
                neighbor != PrototypeMap.Gate &&
                !_zones[ZoneKind.Forbidden].Contains(neighbor))
            .Order();
    }

    private bool IsDigReachable(GridPoint rock)
    {
        return DigApproachTiles(rock).Any();
    }

    private bool TryFindDigApproach(
        CreatureState creature,
        GridPoint rock,
        out GridPoint target)
    {
        var candidate = DigApproachTiles(rock)
            .Select(tile => new
            {
                Tile = tile,
                Distance = _map.Distance(
                    creature.Position,
                    tile,
                    _zones[ZoneKind.Forbidden]),
            })
            .Where(item => item.Distance is not null)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Tile)
            .FirstOrDefault();
        if (candidate is null)
        {
            target = default;
            return false;
        }

        target = candidate.Tile;
        return true;
    }

    private void ValidateZoneTiles(
        ZoneKind zoneKind,
        IReadOnlyList<GridPoint> tiles,
        bool painting)
    {
        foreach (var tile in tiles)
        {
            if (!_map.IsPassable(tile))
            {
                throw new InvalidDataException($"Zone tile ({tile.X},{tile.Y}) is not passable.");
            }

            if (painting && _map[tile] == TileKind.Gate)
            {
                throw new InvalidDataException("The gate cannot belong to a zone.");
            }

            if (painting && zoneKind == ZoneKind.Forbidden && _map[tile] == TileKind.Larder)
            {
                throw new InvalidDataException("A larder feature cannot be Forbidden.");
            }

            // Material may only be stored on floor that was already floor when the
            // session began; zoning freshly excavated ground is step 3. Unlike a
            // dig designation, the initial layout answers this completely, so the
            // pre-flight is already sufficient and this stays a guard: it keeps the
            // rule attached to the mutation it protects.
            if (painting && zoneKind == ZoneKind.MaterialStockpile &&
                !_map.StockpileFloorTiles().Contains(tile))
            {
                throw new InvalidDataException(
                    $"MaterialStockpile tile ({tile.X},{tile.Y}) is not pre-existing plain floor. " +
                    "Map features and freshly excavated ground cannot store material yet.");
            }

            if (painting && zoneKind == ZoneKind.MaterialStockpile &&
                _buildSites.ContainsKey(tile))
            {
                throw new InvalidDataException(
                    $"MaterialStockpile tile ({tile.X},{tile.Y}) carries a construction " +
                    "blueprint. A building site is not a warehouse.");
            }
        }
    }

    /// <summary>
    /// Erasing a stockpile cell must never destroy what it held. The stone stops
    /// being stored and becomes a loose pile on the very same tile, so the total
    /// amount of stone in the world is unchanged by the command.
    /// </summary>
    private void SpillStoredStone(GridPoint tile)
    {
        var stored = _storedStone.GetValueOrDefault(tile);
        if (stored <= 0)
        {
            return;
        }

        _storedStone.Remove(tile);
        AddLoose(tile, ResourceKind.Stone, stored);
        _stoneSpilled += stored;
    }

    private void CancelInvalidJobs()
    {
        foreach (var creature in _creatures)
        {
            var job = creature.CurrentJob;
            if (job is null)
            {
                continue;
            }

            var invalid = _priorities[job.Kind] == 0 ||
                !JobStillSupported(job) ||
                (job.Kind == JobKind.Drill &&
                 creature.Satiety < _rules["drill_min_satiety"]);
            if (!invalid)
            {
                continue;
            }

            var reason = _priorities[job.Kind] == 0
                ? "refused_priority_zero"
                : job.Kind == JobKind.Drill
                    ? "refused_rule_min_satiety"
                    : job.Kind == JobKind.Dig
                        ? "dig_cancelled"
                        : job.Kind == JobKind.Build
                            ? "build_cancelled"
                            : "refused_zone_not_designated";
            CancelJob(creature, reason);
        }
    }

    private bool JobStillSupported(JobState job)
    {
        return job.Kind switch
        {
            JobKind.Harvest => _zones[ZoneKind.Farm].Contains(job.Origin),
            JobKind.Haul => job.PickedUp ||
                (job.SourceCell is { } source
                    ? StoredStoneAt(source) > 0 &&
                      _zones[ZoneKind.MaterialStockpile].Contains(source)
                    : HasLoose(job.Origin, job.Resource!.Value)),
            JobKind.Cook => _zones[ZoneKind.Kitchen].Contains(job.Origin),
            JobKind.Rest => _zones[ZoneKind.Quarters].Contains(job.Origin),
            JobKind.Drill => _zones[ZoneKind.TrainingGround].Contains(job.Origin),
            JobKind.Watch => _zones[ZoneKind.Watch].Contains(job.Origin),
            JobKind.Dig => _digDesignations.Contains(job.Origin) && _map.IsDiggable(job.Origin),
            JobKind.Build => _buildSites.TryGetValue(job.Origin, out var site) &&
                site.Delivered >= PrototypeTuning.BuildStoneCost &&
                _map.IsBuildableFloor(job.Origin),
            _ => false,
        };
    }
}
