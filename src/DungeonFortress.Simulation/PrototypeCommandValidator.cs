namespace DungeonFortress.Simulation;

public static class PrototypeCommandValidator
{
    public static void Validate(PrototypeCommandLog commandLog)
    {
        ArgumentNullException.ThrowIfNull(commandLog);
        if (commandLog.Scenario is not ("baseline" or "prepared" or "neglected" or "custom"))
        {
            throw new InvalidDataException(
                $"Unknown scenario label: {commandLog.Scenario}");
        }
        ArgumentNullException.ThrowIfNull(commandLog.Commands);
        if (commandLog.Commands.Count > PrototypeCommandDocument.MaximumCommandCount)
        {
            throw new InvalidDataException(
                $"The command document exceeds {PrototypeCommandDocument.MaximumCommandCount} commands.");
        }

        var map = new PrototypeMap();
        var zones = PrototypeMap.CreateDefaultZones(map);
        // Tracked exactly like the zones above: a blueprint and a material
        // stockpile cannot share a tile, and the document must be rejected before
        // any world exists when it asks for both.
        var blueprints = new SortedSet<GridPoint>();
        var previousTick = -1;
        foreach (var command in commandLog.Commands)
        {
            if (command is null)
            {
                throw new InvalidDataException("Command entries cannot be null.");
            }

            if (command.Tick is < 0 or >= PrototypeTuning.SessionTicks)
            {
                throw new InvalidDataException(
                    $"Command tick must be between 0 and {PrototypeTuning.SessionTicks - 1}.");
            }

            if (command.Tick < previousTick)
            {
                throw new InvalidDataException("Commands must be ordered by non-decreasing tick.");
            }

            previousTick = command.Tick;
            switch (command)
            {
                case ZonePaintCommand paint:
                    ValidateZoneCommand(map, blueprints, paint.ZoneKind, paint.Tiles, painting: true);
                    foreach (var tile in paint.Tiles)
                    {
                        zones[paint.ZoneKind].Add(tile);
                    }

                    break;
                case ZoneEraseCommand erase:
                    ValidateZoneCommand(map, blueprints, erase.ZoneKind, erase.Tiles, painting: false);
                    var remaining = new SortedSet<GridPoint>(zones[erase.ZoneKind]);
                    remaining.ExceptWith(erase.Tiles);
                    if (erase.ZoneKind == ZoneKind.Larder &&
                        !remaining.Any(tile => map[tile] == TileKind.Larder))
                    {
                        throw new InvalidDataException(
                            "zone_erase would remove the final larder feature from Larder.");
                    }

                    zones[erase.ZoneKind] = remaining;
                    break;
                case DigDesignateCommand designate:
                    ValidateDigTiles(designate.Tiles, requireDiggable: true);
                    break;
                case DigCancelCommand cancel:
                    ValidateDigTiles(cancel.Tiles, requireDiggable: false);
                    break;
                case BuildDesignateCommand build:
                    ValidateBuildTiles(map, zones, build.Tiles, requireBuildable: true);
                    foreach (var tile in build.Tiles)
                    {
                        blueprints.Add(tile);
                    }

                    break;
                case BuildCancelCommand unbuild:
                    ValidateBuildTiles(map, zones, unbuild.Tiles, requireBuildable: false);
                    blueprints.ExceptWith(unbuild.Tiles);
                    break;
                case SetPriorityCommand priority:
                    if (!Enum.IsDefined(priority.JobKind) ||
                        priority.Value is < PrototypeTuning.PriorityMinimum or
                            > PrototypeTuning.PriorityMaximum)
                    {
                        throw new InvalidDataException(
                            $"Priority must be between {PrototypeTuning.PriorityMinimum} and " +
                            $"{PrototypeTuning.PriorityMaximum} for a known job kind.");
                    }

                    break;
                case SetRuleCommand rule:
                    ValidateRule(rule);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported prototype command: {command.GetType().Name}");
            }
        }
    }

    private static void ValidateZoneCommand(
        PrototypeMap map,
        IReadOnlySet<GridPoint> blueprints,
        ZoneKind zoneKind,
        IReadOnlyList<GridPoint> tiles,
        bool painting)
    {
        if (!Enum.IsDefined(zoneKind))
        {
            throw new InvalidDataException($"Unknown zoneKind: {zoneKind}");
        }

        if (tiles is null)
        {
            throw new InvalidDataException("tiles must be an array.");
        }
        if (tiles.Count is < 1 or > PrototypeTuning.MaximumTilesPerCommand)
        {
            throw new InvalidDataException(
                $"tiles must contain between 1 and {PrototypeTuning.MaximumTilesPerCommand} entries.");
        }

        var distinct = new HashSet<GridPoint>();
        foreach (var tile in tiles)
        {
            if (!distinct.Add(tile))
            {
                throw new InvalidDataException(
                    $"Duplicate tile ({tile.X},{tile.Y}) is not allowed.");
            }

            // Checked before passability so that "stockpile on rock" reports the
            // rule the player broke instead of a generic pathfinding message.
            if (painting && zoneKind == ZoneKind.MaterialStockpile &&
                (!PrototypeMap.IsInside(tile) || !map.StockpileFloorTiles().Contains(tile)))
            {
                throw new InvalidDataException(
                    $"MaterialStockpile tile ({tile.X},{tile.Y}) is not plain floor. " +
                    "Rock, map features, the gate and ground that only becomes floor " +
                    "by excavation cannot store material yet.");
            }

            if (painting && zoneKind == ZoneKind.MaterialStockpile && blueprints.Contains(tile))
            {
                throw new InvalidDataException(
                    $"MaterialStockpile tile ({tile.X},{tile.Y}) carries a construction " +
                    "blueprint. A building site is not a warehouse.");
            }

            // Rock that digging can turn into floor is accepted here on purpose:
            // a room built out of excavated space has to be zonable. The live map
            // in PrototypeWorld stays the authority and rejects the command on its
            // tick if the tile is still rock then.
            if (!PrototypeMap.IsInside(tile) ||
                (!map.IsPassable(tile) && !map.IsDiggable(tile)))
            {
                throw new InvalidDataException(
                    $"Zone tile ({tile.X},{tile.Y}) is outside the map or can never be passable.");
            }

            if (painting && map[tile] == TileKind.Gate)
            {
                throw new InvalidDataException("The gate cannot belong to a zone.");
            }

            if (painting && zoneKind == ZoneKind.Forbidden && map[tile] == TileKind.Larder)
            {
                throw new InvalidDataException("A larder feature cannot be Forbidden.");
            }

        }
    }

    /// <summary>
    /// A static pre-flight over the <em>initial</em> layout. A tile that is not
    /// rock at the start can never become diggable, so rejecting it here is sound.
    /// The live map in <see cref="PrototypeWorld"/> stays the runtime authority for
    /// tiles that have already been excavated during the session.
    /// </summary>
    private static void ValidateDigTiles(
        IReadOnlyList<GridPoint> tiles,
        bool requireDiggable)
    {
        if (tiles is null)
        {
            throw new InvalidDataException("tiles must be an array.");
        }

        if (tiles.Count is < 1 or > PrototypeTuning.MaximumTilesPerCommand)
        {
            throw new InvalidDataException(
                $"tiles must contain between 1 and {PrototypeTuning.MaximumTilesPerCommand} entries.");
        }

        var distinct = new HashSet<GridPoint>();
        foreach (var tile in tiles)
        {
            if (!distinct.Add(tile))
            {
                throw new InvalidDataException(
                    $"Duplicate tile ({tile.X},{tile.Y}) is not allowed.");
            }

            if (!PrototypeMap.IsInside(tile))
            {
                throw new InvalidDataException(
                    $"Dig tile ({tile.X},{tile.Y}) is outside the map.");
            }

            if (requireDiggable && !PrototypeMap.IsDiggableInInitialLayout(tile))
            {
                throw new InvalidDataException(
                    $"Dig tile ({tile.X},{tile.Y}) is not internal rock. " +
                    "Floor, features, the gate and the map boundary cannot be designated.");
            }
        }
    }

    /// <summary>
    /// A static pre-flight over the <em>initial</em> layout, the mirror of
    /// <see cref="ValidateDigTiles"/>. A tile that is neither plain floor nor
    /// diggable rock at tick 0 can never become plain floor, so rejecting it here
    /// is sound. Whether it is floor <em>yet</em> is decided by the live map.
    /// </summary>
    private static void ValidateBuildTiles(
        PrototypeMap map,
        IReadOnlyDictionary<ZoneKind, SortedSet<GridPoint>> zones,
        IReadOnlyList<GridPoint> tiles,
        bool requireBuildable)
    {
        if (tiles is null)
        {
            throw new InvalidDataException("tiles must be an array.");
        }

        if (tiles.Count is < 1 or > PrototypeTuning.MaximumTilesPerCommand)
        {
            throw new InvalidDataException(
                $"tiles must contain between 1 and {PrototypeTuning.MaximumTilesPerCommand} entries.");
        }

        var distinct = new HashSet<GridPoint>();
        foreach (var tile in tiles)
        {
            if (!distinct.Add(tile))
            {
                throw new InvalidDataException(
                    $"Duplicate tile ({tile.X},{tile.Y}) is not allowed.");
            }

            if (!PrototypeMap.IsInside(tile))
            {
                throw new InvalidDataException(
                    $"Build tile ({tile.X},{tile.Y}) is outside the map.");
            }

            if (!requireBuildable)
            {
                continue;
            }

            if (!map.IsBuildableInInitialLayout(tile))
            {
                throw new InvalidDataException(
                    $"Build tile ({tile.X},{tile.Y}) is not plain floor. " +
                    "Map features, the gate, the map boundary and an existing post " +
                    "cannot hold a blueprint.");
            }

            if (zones[ZoneKind.MaterialStockpile].Contains(tile))
            {
                throw new InvalidDataException(
                    $"Build tile ({tile.X},{tile.Y}) is a material stockpile cell. " +
                    "Erase the cell first; a building site is not a warehouse.");
            }
        }
    }

    private static void ValidateRule(SetRuleCommand rule)
    {
        var maximum = rule.RuleId switch
        {
            "ration_reserve" => PrototypeTuning.RationReserveMaximum,
            "drill_min_satiety" => PrototypeTuning.DrillMinimumSatietyMaximum,
            "muster_lead_ticks" => PrototypeTuning.MusterLeadMaximum,
            _ => throw new InvalidDataException($"Unknown ruleId: {rule.RuleId}"),
        };
        if (rule.Value is < 0 || rule.Value > maximum)
        {
            throw new InvalidDataException($"{rule.RuleId} must be between 0 and {maximum}.");
        }
    }
}
