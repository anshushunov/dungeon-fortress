using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using DungeonFortress.Simulation;

using ModelContextProtocol.Protocol;
namespace DungeonFortress.DomainMcp;

public sealed class DomainTools(ProjectRoot projectRoot)
{
    private static readonly HashSet<string> SimulationRunArgumentNames =
    [
        "seed",
        "agentCount",
        "ticks",
        "commandsPath",
    ];

    private static readonly HashSet<string> PrototypeRunArgumentNames =
    [
        "commandsPath",
        "ticks",
    ];

    public CallToolResult BridgeStatus(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is { Count: > 0 })
        {
            return Error("bridge_status does not accept arguments.");
        }

        var response = new BridgeStatusResponse(
            "bridge_status",
            "ok",
            DomainBridgeInfo.Version,
            CanonicalSnapshot.SchemaVersion,
            SimulationCommandDocument.SchemaVersion,
            PrototypeCommandDocument.SchemaVersion,
            projectRoot.Sentinels,
            ["bridge_status", "prototype_run", "simulation_run"]);

        return Success(response);
    }

    public CallToolResult PrototypeRun(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return Error("prototype_run requires commandsPath and ticks.");
        }

        var unknownArguments = arguments.Keys
            .Where(name => !PrototypeRunArgumentNames.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unknownArguments.Length > 0)
        {
            return Error(
                $"prototype_run rejected unknown argument(s): {string.Join(", ", unknownArguments)}.");
        }

        if (!arguments.TryGetValue("commandsPath", out var commandsPathElement) ||
            commandsPathElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(commandsPathElement.GetString()) ||
            !TryReadInt32(arguments, "ticks", out var ticks))
        {
            return Error("prototype_run requires string commandsPath and integer ticks.");
        }

        if (ticks is < 0 or > PrototypeTuning.SessionTicks)
        {
            return Error($"ticks must be between 0 and {PrototypeTuning.SessionTicks}.");
        }

        try
        {
            var commandLog = PrototypeCommandDocument.Load(
                projectRoot.ResolveCommandDocument(commandsPathElement.GetString()!));
            var result = PrototypeScenario.Run(commandLog, ticks);
            var creatures = result.State.Creatures;
            var readiness = creatures.All(creature => creature.ReadinessAtRaid is not null)
                ? (int?)creatures.Average(creature => creature.ReadinessAtRaid!.Value)
                : null;
            var response = new PrototypeRunResponse(
                "prototype_result",
                "ok",
                commandLog.Scenario,
                commandLog.Seed,
                result.Tick,
                result.CommandsApplied,
                result.Checksum,
                Encoding.UTF8.GetString(result.CanonicalJson),
                Encoding.UTF8.GetString(result.CanonicalEventLog),
                result.State.Stocks.MealsProduced,
                result.State.Stocks.MealsEaten,
                result.State.Stocks.Meals,
                result.State.Stocks.RawMushroom,
                (int)creatures.Average(creature => creature.Satiety),
                (int)creatures.Average(creature => creature.Fatigue),
                (int)creatures.Average(creature => creature.MartialForm),
                readiness,
                creatures.Count,
                result.State.Jobs.Count,
                result.State.Events.Count);
            return Success(response);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or FormatException
            or InvalidDataException
            or IOException
            or JsonException
            or OverflowException)
        {
            return Error($"Prototype request rejected: {exception.Message}");
        }
    }

    public CallToolResult SimulationRun(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return Error("simulation_run requires seed, agentCount, and ticks.");
        }

        var unknownArguments = arguments.Keys
            .Where(name => !SimulationRunArgumentNames.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unknownArguments.Length > 0)
        {
            return Error(
                $"simulation_run rejected unknown argument(s): {string.Join(", ", unknownArguments)}.");
        }

        if (!TryReadUInt64(arguments, "seed", out var seed) ||
            !TryReadInt32(arguments, "agentCount", out var agentCount) ||
            !TryReadInt32(arguments, "ticks", out var ticks))
        {
            return Error(
                "simulation_run requires integer seed, agentCount, and ticks values.");
        }

        string? commandsPath = null;
        if (arguments.TryGetValue("commandsPath", out var commandsPathValue) &&
            commandsPathValue.ValueKind != JsonValueKind.Null)
        {
            if (commandsPathValue.ValueKind != JsonValueKind.String)
            {
                return Error("commandsPath must be a string or null.");
            }

            commandsPath = commandsPathValue.GetString();
        }

        if (agentCount is < 1 or > DomainBridgeInfo.MaximumAgentCount)
        {
            return Error(
                $"agentCount must be between 1 and {DomainBridgeInfo.MaximumAgentCount}.");
        }

        if (ticks is < 0 or > DomainBridgeInfo.MaximumTickCount)
        {
            return Error(
                $"ticks must be between 0 and {DomainBridgeInfo.MaximumTickCount}.");
        }

        try
        {
            var commands = commandsPath is null
                ? []
                : SimulationCommandDocument.Load(
                    projectRoot.ResolveCommandDocument(commandsPath));
            var result = SimulationScenario.Run(
                new SimulationConfig(seed, agentCount),
                ticks,
                commands);

            var response = new SimulationRunResponse(
                "simulation_result",
                "ok",
                seed,
                agentCount,
                result.Tick,
                result.CommandsApplied,
                result.CanonicalJson.Length,
                result.Checksum,
                Encoding.UTF8.GetString(result.CanonicalJson));

            return Success(response);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or FormatException
            or InvalidDataException
            or IOException
            or JsonException
            or OverflowException)
        {
            return Error($"Simulation request rejected: {exception.Message}");
        }
    }

    private static bool TryReadUInt64(
        IDictionary<string, JsonElement> arguments,
        string name,
        out ulong value)
    {
        value = default;
        return arguments.TryGetValue(name, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetUInt64(out value);
    }

    private static bool TryReadInt32(
        IDictionary<string, JsonElement> arguments,
        string name,
        out int value)
    {
        value = default;
        return arguments.TryGetValue(name, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out value);
    }

    private static CallToolResult Success<T>(T response)
    {
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(response),
                },
            ],
            StructuredContent = JsonSerializer.SerializeToElement(response),
        };
    }

    private static CallToolResult Error(string message)
    {
        return new CallToolResult
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = message,
                },
            ],
        };
    }
}

public sealed record BridgeStatusResponse(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("bridgeVersion")] string BridgeVersion,
    [property: JsonPropertyName("canonicalSchemaVersion")] int CanonicalSchemaVersion,
    [property: JsonPropertyName("commandSchemaVersion")] int CommandSchemaVersion,
    [property: JsonPropertyName("prototypeCommandSchemaVersion")] int PrototypeCommandSchemaVersion,
    [property: JsonPropertyName("validatedSentinels")] string[] ValidatedSentinels,
    [property: JsonPropertyName("tools")] string[] Tools);

public sealed record PrototypeRunResponse(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("scenario")] string Scenario,
    [property: JsonPropertyName("seed")] ulong Seed,
    [property: JsonPropertyName("ticks")] int Ticks,
    [property: JsonPropertyName("commandsApplied")] int CommandsApplied,
    [property: JsonPropertyName("checksum")] string Checksum,
    [property: JsonPropertyName("canonicalJson")] string CanonicalJson,
    [property: JsonPropertyName("canonicalEventLog")] string CanonicalEventLog,
    [property: JsonPropertyName("mealsProduced")] int MealsProduced,
    [property: JsonPropertyName("mealsEaten")] int MealsEaten,
    [property: JsonPropertyName("meals")] int Meals,
    [property: JsonPropertyName("rawMushroom")] int RawMushroom,
    [property: JsonPropertyName("averageSatiety")] int AverageSatiety,
    [property: JsonPropertyName("averageFatigue")] int AverageFatigue,
    [property: JsonPropertyName("averageMartialForm")] int AverageMartialForm,
    [property: JsonPropertyName("averageReadinessAtRaid")] int? AverageReadinessAtRaid,
    [property: JsonPropertyName("creatureCount")] int CreatureCount,
    [property: JsonPropertyName("jobCount")] int JobCount,
    [property: JsonPropertyName("eventCount")] int EventCount);

public sealed record SimulationRunResponse(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("seed")] ulong Seed,
    [property: JsonPropertyName("agentCount")] int AgentCount,
    [property: JsonPropertyName("ticks")] int Ticks,
    [property: JsonPropertyName("commandsApplied")] int CommandsApplied,
    [property: JsonPropertyName("snapshotBytes")] int SnapshotBytes,
    [property: JsonPropertyName("checksum")] string Checksum,
    [property: JsonPropertyName("canonicalJson")] string CanonicalJson);
