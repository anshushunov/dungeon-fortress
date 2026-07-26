using System.Text.Json;

namespace DungeonFortress.Simulation;

public static class SimulationCommandDocument
{
    public const int SchemaVersion = 1;
    public const int MaximumDocumentBytes = 1_048_576;
    public const int MaximumCommandCount = 10_000;

    public static IReadOnlyList<SimulationCommand> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The simulation command document does not exist.", path);
        }

        if (file.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"The simulation command document exceeds {MaximumDocumentBytes} bytes.");
        }

        return Parse(File.ReadAllBytes(file.FullName));
    }

    public static IReadOnlyList<SimulationCommand> Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"The simulation command document exceeds {MaximumDocumentBytes} bytes.");
        }

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Command document root must be a JSON object.");
        }

        var allowedRootProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion",
            "commands",
        };

        foreach (var property in root.EnumerateObject())
        {
            if (!allowedRootProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"Unknown command document property: {property.Name}");
            }
        }

        var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        if (schemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported command schema version: {schemaVersion}");
        }

        var commandsElement = root.GetProperty("commands");
        if (commandsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The commands property must be an array.");
        }

        var commands = new List<SimulationCommand>();
        foreach (var commandElement in commandsElement.EnumerateArray())
        {
            if (commands.Count == MaximumCommandCount)
            {
                throw new InvalidDataException(
                    $"The command document exceeds {MaximumCommandCount} commands.");
            }

            commands.Add(ParseCommand(commandElement));
        }

        return commands;
    }

    private static SimulationCommand ParseCommand(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Every command must be a JSON object.");
        }

        var allowedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "tick",
            "agentId",
            "energyDelta",
        };

        foreach (var property in element.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name))
            {
                throw new InvalidDataException($"Unknown command property: {property.Name}");
            }
        }

        return new SimulationCommand(
            element.GetProperty("tick").GetInt32(),
            element.GetProperty("agentId").GetInt32(),
            element.GetProperty("energyDelta").GetInt32());
    }
}
