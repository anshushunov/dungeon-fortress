using System.Text.Json;

namespace DungeonFortress.Simulation;

public static class PrototypeCommandDocument
{
    public const int SchemaVersion = 2;
    public const int MaximumDocumentBytes = 1_048_576;
    public const int MaximumCommandCount = 10_000;

    private static readonly HashSet<string> Scenarios =
    [
        "baseline",
        "prepared",
        "neglected",
        "custom",
    ];

    public static PrototypeCommandLog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The prototype command document does not exist.", path);
        }

        if (file.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"The prototype command document exceeds {MaximumDocumentBytes} bytes.");
        }

        return Parse(File.ReadAllBytes(file.FullName));
    }

    public static PrototypeCommandLog Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"The prototype command document exceeds {MaximumDocumentBytes} bytes.");
        }

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireObject(root, "Command document root");
        RequireExactProperties(root, ["schemaVersion", "scenario", "seed", "commands"]);

        var schemaVersion = ReadInt32(root, "schemaVersion");
        if (schemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Gameplay accepts only command schema version {SchemaVersion}.");
        }

        var scenario = ReadString(root, "scenario");
        if (!Scenarios.Contains(scenario))
        {
            throw new InvalidDataException($"Unknown scenario label: {scenario}");
        }

        if (!root.TryGetProperty("seed", out var seedElement) ||
            !seedElement.TryGetUInt64(out var seed))
        {
            throw new InvalidDataException("seed must be an unsigned 64-bit integer.");
        }

        var commandsElement = root.GetProperty("commands");
        if (commandsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("commands must be an array.");
        }

        var commands = new List<PrototypeCommand>();
        var previousTick = -1;
        foreach (var commandElement in commandsElement.EnumerateArray())
        {
            if (commands.Count == MaximumCommandCount)
            {
                throw new InvalidDataException(
                    $"The command document exceeds {MaximumCommandCount} commands.");
            }

            var command = ParseCommand(commandElement);
            if (command.Tick < previousTick)
            {
                throw new InvalidDataException("Commands must be ordered by non-decreasing tick.");
            }

            previousTick = command.Tick;
            commands.Add(command);
        }

        var commandLog = new PrototypeCommandLog(scenario, seed, commands);
        PrototypeCommandValidator.Validate(commandLog);
        return commandLog;
    }

    private static PrototypeCommand ParseCommand(JsonElement element)
    {
        RequireObject(element, "Every command");
        if (!element.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Every command requires string property kind.");
        }

        var tick = ReadInt32(element, "tick");
        if (tick is < 0 or >= PrototypeTuning.SessionTicks)
        {
            throw new InvalidDataException(
                $"Command tick must be between 0 and {PrototypeTuning.SessionTicks - 1}.");
        }

        return kindElement.GetString() switch
        {
            "zone_paint" => ParseZoneCommand(element, tick, paint: true),
            "zone_erase" => ParseZoneCommand(element, tick, paint: false),
            "dig_designate" => ParseDigCommand(element, tick, designate: true),
            "dig_cancel" => ParseDigCommand(element, tick, designate: false),
            "build_designate" => ParseBuildCommand(element, tick, designate: true),
            "build_cancel" => ParseBuildCommand(element, tick, designate: false),
            "set_priority" => ParsePriorityCommand(element, tick),
            "set_rule" => ParseRuleCommand(element, tick),
            "verdict" => ParseVerdictCommand(element, tick),
            string value => throw new InvalidDataException($"Unknown command kind: {value}"),
            null => throw new InvalidDataException("Command kind cannot be null."),
        };
    }

    private static PrototypeCommand ParseZoneCommand(
        JsonElement element,
        int tick,
        bool paint)
    {
        RequireExactProperties(element, ["tick", "kind", "zoneKind", "tiles"]);
        var zoneKind = ReadEnum<ZoneKind>(element, "zoneKind");
        var tiles = ReadTiles(element);

        return paint
            ? new ZonePaintCommand(tick, zoneKind, tiles)
            : new ZoneEraseCommand(tick, zoneKind, tiles);
    }

    private static PrototypeCommand ParseDigCommand(
        JsonElement element,
        int tick,
        bool designate)
    {
        // No zoneKind, no jobKind, no value: excavation intent is purely spatial.
        RequireExactProperties(element, ["tick", "kind", "tiles"]);
        var tiles = ReadTiles(element);

        return designate
            ? new DigDesignateCommand(tick, tiles)
            : new DigCancelCommand(tick, tiles);
    }

    private static PrototypeCommand ParseBuildCommand(
        JsonElement element,
        int tick,
        bool designate)
    {
        // No building type, no zoneKind, no creature: the post is the only thing
        // this step can build, and construction intent is purely spatial.
        RequireExactProperties(element, ["tick", "kind", "tiles"]);
        var tiles = ReadTiles(element);

        return designate
            ? new BuildDesignateCommand(tick, tiles)
            : new BuildCancelCommand(tick, tiles);
    }

    private static GridPoint[] ReadTiles(JsonElement element)
    {
        var tilesElement = element.GetProperty("tiles");
        if (tilesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("tiles must be an array.");
        }

        var tiles = new SortedSet<GridPoint>();
        var sourceCount = 0;
        foreach (var tileElement in tilesElement.EnumerateArray())
        {
            sourceCount++;
            if (sourceCount > PrototypeTuning.MaximumTilesPerCommand)
            {
                throw new InvalidDataException(
                    $"tiles cannot exceed {PrototypeTuning.MaximumTilesPerCommand} entries.");
            }

            if (tileElement.ValueKind != JsonValueKind.Array ||
                tileElement.GetArrayLength() != 2)
            {
                throw new InvalidDataException("Each tile must be [x,y].");
            }

            var values = tileElement.EnumerateArray().ToArray();
            if (!values[0].TryGetInt32(out var x) || !values[1].TryGetInt32(out var y))
            {
                throw new InvalidDataException("Tile coordinates must be integers.");
            }

            if (x is < 0 or >= PrototypeTuning.MapWidth ||
                y is < 0 or >= PrototypeTuning.MapHeight)
            {
                throw new InvalidDataException($"Tile ({x},{y}) is outside the map.");
            }

            if (!tiles.Add(new GridPoint(x, y)))
            {
                throw new InvalidDataException($"Duplicate tile ({x},{y}) is not allowed.");
            }
        }

        if (tiles.Count == 0)
        {
            throw new InvalidDataException("tiles cannot be empty.");
        }

        return [.. tiles];
    }

    private static PrototypeCommand ParsePriorityCommand(JsonElement element, int tick)
    {
        RequireExactProperties(element, ["tick", "kind", "jobKind", "value"]);
        var jobKind = ReadEnum<JobKind>(element, "jobKind");
        var value = ReadInt32(element, "value");
        if (value is < PrototypeTuning.PriorityMinimum or > PrototypeTuning.PriorityMaximum)
        {
            throw new InvalidDataException(
                $"Priority must be between {PrototypeTuning.PriorityMinimum} and " +
                $"{PrototypeTuning.PriorityMaximum}.");
        }

        return new SetPriorityCommand(tick, jobKind, value);
    }

    private static PrototypeCommand ParseRuleCommand(JsonElement element, int tick)
    {
        RequireExactProperties(element, ["tick", "kind", "ruleId", "value"]);
        var ruleId = ReadString(element, "ruleId");
        var value = ReadInt32(element, "value");
        var maximum = ruleId switch
        {
            "ration_reserve" => PrototypeTuning.RationReserveMaximum,
            "drill_min_satiety" => PrototypeTuning.DrillMinimumSatietyMaximum,
            "muster_lead_ticks" => PrototypeTuning.MusterLeadMaximum,
            _ => throw new InvalidDataException($"Unknown ruleId: {ruleId}"),
        };

        if (value is < 0 || value > maximum)
        {
            throw new InvalidDataException($"{ruleId} must be between 0 and {maximum}.");
        }

        return new SetRuleCommand(tick, ruleId, value);
    }

    /// <summary>
    /// The static half of a verdict: the closed set of properties, the closed
    /// enumeration of values and the bounds of <c>creatureId</c>. Everything
    /// else — is the window open, was there a card about this one — is a fact
    /// about a world that does not exist yet and is checked on the tick of the
    /// command (ADR 0019, «Форма команд вердикта»).
    ///
    /// <para><see cref="RequireExactProperties"/> is what makes "любое поле
    /// сверх <c>{tick, kind, creatureId, verdict}</c> выводит команду из
    /// вердиктов" structural rather than a promise: a fifth property throws
    /// <c>Unknown property</c> and takes the whole document with it.</para>
    /// </summary>
    private static PrototypeCommand ParseVerdictCommand(JsonElement element, int tick)
    {
        RequireExactProperties(element, ["tick", "kind", "creatureId", "verdict"]);
        var creatureId = ReadInt32(element, "creatureId");
        var verdict = ReadString(element, "verdict");
        return new VerdictCommand(tick, creatureId, ParseVerdict(verdict));
    }

    /// <summary>
    /// The single place a string becomes a <see cref="VerdictKind"/>. The names
    /// are spelled out instead of being derived from the enum, so a value cannot
    /// enter the game dictionary by being added to a C# enum: it has to be
    /// written here, and the design contract's walkthrough of the five
    /// conditions is what has to be written beside it.
    /// </summary>
    internal static VerdictKind ParseVerdict(string verdict) => verdict switch
    {
        "reward" => VerdictKind.Reward,
        "punish" => VerdictKind.Punish,
        _ => throw new InvalidDataException($"Unknown verdict: {verdict}"),
    };

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{name} must be a JSON object.");
        }
    }

    private static void RequireExactProperties(JsonElement element, string[] expected)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        var unknown = actual.Except(expected, StringComparer.Ordinal).Order().ToArray();
        var missing = expected.Except(actual, StringComparer.Ordinal).Order().ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidDataException($"Unknown property: {unknown[0]}");
        }

        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Missing required property: {missing[0]}");
        }

        if (actual.Length != actual.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("Duplicate JSON properties are not allowed.");
        }
    }

    private static int ReadInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"{name} must be a 32-bit integer.");
        }

        return value;
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } value)
        {
            throw new InvalidDataException($"{name} must be a string.");
        }

        return value;
    }

    private static T ReadEnum<T>(JsonElement element, string name)
        where T : struct, Enum
    {
        var value = ReadString(element, name);
        if (!Enum.TryParse<T>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw new InvalidDataException($"Unknown {name}: {value}");
        }

        return parsed;
    }
}
