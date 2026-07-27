using System.Text.Json;

using DungeonFortress.Simulation;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DungeonFortress.DomainMcp;

public sealed class DomainMcpTool : McpServerTool
{
    private static readonly ToolAnnotations SafeAnnotations = new()
    {
        ReadOnlyHint = true,
        DestructiveHint = false,
        IdempotentHint = true,
        OpenWorldHint = false,
    };

    private readonly Func<IDictionary<string, JsonElement>?, CallToolResult> handler;

    private DomainMcpTool(
        string name,
        string description,
        JsonElement inputSchema,
        JsonElement outputSchema,
        Func<IDictionary<string, JsonElement>?, CallToolResult> handler)
    {
        this.handler = handler;
        ProtocolTool = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema,
            OutputSchema = outputSchema,
            Annotations = SafeAnnotations,
        };
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata => [];

    public static DomainMcpTool CreateBridgeStatus(DomainTools tools)
    {
        return new DomainMcpTool(
            "bridge_status",
            "Reports the validated project-owned domain bridge contract. " +
            "This tool does not inspect arbitrary files or external systems.",
            JsonElement.Parse(
                """
                {
                  "type": "object",
                  "properties": {},
                  "additionalProperties": false
                }
                """),
            JsonElement.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "event": { "type": "string" },
                    "status": { "type": "string" },
                    "bridgeVersion": { "type": "string" },
                    "canonicalSchemaVersion": { "type": "integer" },
                    "commandSchemaVersion": { "type": "integer" },
                    "prototypeCommandSchemaVersion": { "type": "integer" },
                    "validatedSentinels": {
                      "type": "array",
                      "items": { "type": "string" }
                    },
                    "tools": {
                      "type": "array",
                      "items": { "type": "string" }
                    }
                  },
                  "required": [
                    "event",
                    "status",
                    "bridgeVersion",
                    "canonicalSchemaVersion",
                    "commandSchemaVersion",
                    "prototypeCommandSchemaVersion",
                    "validatedSentinels",
                    "tools"
                  ],
                  "additionalProperties": false
                }
                """),
            tools.BridgeStatus);
    }

    public static DomainMcpTool CreatePrototypeRun(DomainTools tools)
    {
        return new DomainMcpTool(
            "prototype_run",
            "Runs Prototype 1 through the closed gameplay command schema v2. " +
            "Returns canonical state, event log, checksum, and structured precombat observations.",
            JsonElement.Parse(
                $$"""
                {
                  "type": "object",
                  "properties": {
                    "commandsPath": {
                      "type": "string",
                      "description": "Repository-relative gameplay-v2 command fixture."
                    },
                    "ticks": {
                      "type": "integer",
                      "minimum": 0,
                      "maximum": {{PrototypeTuning.SessionTicks}},
                      "description": "Fixed ticks from 0 through {{PrototypeTuning.SessionTicks}}."
                    }
                  },
                  "required": ["commandsPath", "ticks"],
                  "additionalProperties": false
                }
                """),
            JsonElement.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "event": { "type": "string" },
                    "status": { "type": "string" },
                    "scenario": { "type": "string" },
                    "seed": { "type": "integer", "minimum": 0 },
                    "ticks": { "type": "integer" },
                    "commandsApplied": { "type": "integer" },
                    "checksum": { "type": "string" },
                    "canonicalJson": { "type": "string" },
                    "canonicalEventLog": { "type": "string" },
                    "mealsProduced": { "type": "integer" },
                    "mealsEaten": { "type": "integer" },
                    "meals": { "type": "integer" },
                    "rawMushroom": { "type": "integer" },
                    "averageSatiety": { "type": "integer" },
                    "averageFatigue": { "type": "integer" },
                    "averageMartialForm": { "type": "integer" },
                    "averageReadinessAtRaid": { "type": ["integer", "null"] },
                    "creatureCount": { "type": "integer" },
                    "jobCount": { "type": "integer" },
                    "eventCount": { "type": "integer" }
                  },
                  "required": [
                    "event",
                    "status",
                    "scenario",
                    "seed",
                    "ticks",
                    "commandsApplied",
                    "checksum",
                    "canonicalJson",
                    "canonicalEventLog",
                    "mealsProduced",
                    "mealsEaten",
                    "meals",
                    "rawMushroom",
                    "averageSatiety",
                    "averageFatigue",
                    "averageMartialForm",
                    "averageReadinessAtRaid",
                    "creatureCount",
                    "jobCount",
                    "eventCount"
                  ],
                  "additionalProperties": false
                }
                """),
            tools.PrototypeRun);
    }

    public static DomainMcpTool CreateSimulationRun(DomainTools tools)
    {
        return new DomainMcpTool(
            "simulation_run",
            "Runs the project-owned deterministic simulation and returns its canonical UTF-8 JSON " +
            "and SHA-256 checksum. It accepts only bounded values and an optional repository-relative " +
            "JSON command document.",
            JsonElement.Parse(
                $$"""
                {
                  "type": "object",
                  "properties": {
                    "seed": {
                      "type": "integer",
                      "minimum": 0,
                      "maximum": 18446744073709551615,
                      "description": "Deterministic unsigned 64-bit seed."
                    },
                    "agentCount": {
                      "type": "integer",
                      "minimum": 1,
                      "maximum": {{DomainBridgeInfo.MaximumAgentCount}},
                      "description": "Agent count from 1 through {{DomainBridgeInfo.MaximumAgentCount}}."
                    },
                    "ticks": {
                      "type": "integer",
                      "minimum": 0,
                      "maximum": {{DomainBridgeInfo.MaximumTickCount}},
                      "description": "Fixed tick count from 0 through {{DomainBridgeInfo.MaximumTickCount}}."
                    },
                    "commandsPath": {
                      "type": ["string", "null"],
                      "description": "Optional repository-relative .json command document."
                    }
                  },
                  "required": ["seed", "agentCount", "ticks"],
                  "additionalProperties": false
                }
                """),
            JsonElement.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "event": { "type": "string" },
                    "status": { "type": "string" },
                    "seed": { "type": "integer", "minimum": 0 },
                    "agentCount": { "type": "integer" },
                    "ticks": { "type": "integer" },
                    "commandsApplied": { "type": "integer" },
                    "snapshotBytes": { "type": "integer" },
                    "checksum": { "type": "string" },
                    "canonicalJson": { "type": "string" }
                  },
                  "required": [
                    "event",
                    "status",
                    "seed",
                    "agentCount",
                    "ticks",
                    "commandsApplied",
                    "snapshotBytes",
                    "checksum",
                    "canonicalJson"
                  ],
                  "additionalProperties": false
                }
                """),
            tools.SimulationRun);
    }

    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(handler(request.Params.Arguments));
    }
}
