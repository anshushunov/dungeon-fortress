using System.Text.Json;

using DungeonFortress.Simulation;

namespace DungeonFortress.Scenarios;

internal static class CommandFile
{
    public static IReadOnlyList<SimulationCommand> Load(string? path)
    {
        if (path is null)
        {
            return [];
        }

        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Command file root must be a JSON object.");
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
                throw new InvalidDataException($"Unknown command file property: {property.Name}");
            }
        }

        var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        if (schemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported command schema version: {schemaVersion}");
        }

        var commandsElement = root.GetProperty("commands");
        if (commandsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The commands property must be an array.");
        }

        var commands = new List<SimulationCommand>();
        foreach (var commandElement in commandsElement.EnumerateArray())
        {
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
