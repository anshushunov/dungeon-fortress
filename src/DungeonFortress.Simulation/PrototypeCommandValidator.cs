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
                    ValidateZoneCommand(map, paint.ZoneKind, paint.Tiles, painting: true);
                    foreach (var tile in paint.Tiles)
                    {
                        zones[paint.ZoneKind].Add(tile);
                    }

                    break;
                case ZoneEraseCommand erase:
                    ValidateZoneCommand(map, erase.ZoneKind, erase.Tiles, painting: false);
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

            if (!PrototypeMap.IsInside(tile) || !map.IsPassable(tile))
            {
                throw new InvalidDataException(
                    $"Zone tile ({tile.X},{tile.Y}) is outside the map or not passable.");
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
