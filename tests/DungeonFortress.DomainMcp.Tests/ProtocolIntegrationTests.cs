using System.Diagnostics;
using System.Text;
using System.Text.Json;

using DungeonFortress.DomainMcp;
using DungeonFortress.Simulation;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using Xunit;

namespace DungeonFortress.DomainMcp.Tests;

public sealed class ProtocolIntegrationTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Official_client_lists_only_the_minimal_surface_and_matches_canonical_bytes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var transport = new StdioClientTransport(new()
        {
            Name = "Dungeon Fortress domain MCP integration test",
            Command = "dotnet",
            Arguments = [typeof(DomainTools).Assembly.Location, "--root", repositoryRoot],
        });

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();

        Assert.Equal(
            ["bridge_status", "prototype_run", "simulation_run"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());

        var result = await client.CallToolAsync(
            "simulation_run",
            new Dictionary<string, object?>
            {
                ["seed"] = 424_242UL,
                ["agentCount"] = 32,
                ["ticks"] = 256,
                ["commandsPath"] = "scenarios/smoke.commands.json",
            });

        Assert.NotEqual(true, result.IsError);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var canonicalJson = structured.GetProperty("canonicalJson").GetString();
        Assert.NotNull(canonicalJson);

        var expected = SimulationScenario.Run(
            new SimulationConfig(424_242, 32),
            256,
            SimulationCommandDocument.Load(
                Path.Combine(repositoryRoot, "scenarios", "smoke.commands.json")));
        Assert.Equal(expected.CanonicalJson, Encoding.UTF8.GetBytes(canonicalJson));
        Assert.Equal(
            expected.Checksum,
            structured.GetProperty("checksum").GetString());
    }

    [Fact]
    public async Task Raw_json_rpc_exposes_schemas_errors_and_clean_shutdown()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var process = StartServer(repositoryRoot);

        await WriteMessageAsync(
            process,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"domain-mcp-tests","version":"1.0"}}}
            """);
        var initialize = await ReadMessageAsync(process);
        Assert.Equal(1, initialize.RootElement.GetProperty("id").GetInt32());

        await WriteMessageAsync(
            process,
            """
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            """);
        await WriteMessageAsync(
            process,
            """
            {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
            """);

        var list = await ReadMessageAsync(process);
        var tools = list.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(3, tools.Length);
        Assert.All(tools, tool =>
        {
            Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            Assert.False(tool.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean());
            Assert.Equal(
                JsonValueKind.Object,
                tool.GetProperty("inputSchema").ValueKind);
            Assert.Equal(
                JsonValueKind.Object,
                tool.GetProperty("outputSchema").ValueKind);
        });

        var bridgeStatusTool = Assert.Single(
            tools,
            tool => tool.GetProperty("name").GetString() == "bridge_status");
        AssertClosedWorldSchema(
            bridgeStatusTool.GetProperty("inputSchema"),
            []);

        var simulationRunTool = Assert.Single(
            tools,
            tool => tool.GetProperty("name").GetString() == "simulation_run");
        var simulationSchema = simulationRunTool.GetProperty("inputSchema");
        AssertClosedWorldSchema(
            simulationSchema,
            ["agentCount", "commandsPath", "seed", "ticks"]);
        Assert.Equal(
            ["agentCount", "seed", "ticks"],
            simulationSchema
                .GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString())
                .Order(StringComparer.Ordinal)
                .ToArray());
        var properties = simulationSchema.GetProperty("properties");
        Assert.Equal(0UL, properties.GetProperty("seed").GetProperty("minimum").GetUInt64());
        Assert.Equal(
            ulong.MaxValue,
            properties.GetProperty("seed").GetProperty("maximum").GetUInt64());
        Assert.Equal(
            1,
            properties.GetProperty("agentCount").GetProperty("minimum").GetInt32());
        Assert.Equal(
            DomainBridgeInfo.MaximumAgentCount,
            properties.GetProperty("agentCount").GetProperty("maximum").GetInt32());
        Assert.Equal(
            0,
            properties.GetProperty("ticks").GetProperty("minimum").GetInt32());
        Assert.Equal(
            DomainBridgeInfo.MaximumTickCount,
            properties.GetProperty("ticks").GetProperty("maximum").GetInt32());

        var prototypeRunTool = Assert.Single(
            tools,
            tool => tool.GetProperty("name").GetString() == "prototype_run");
        var prototypeSchema = prototypeRunTool.GetProperty("inputSchema");
        AssertClosedWorldSchema(
            prototypeSchema,
            ["commandsPath", "ticks"]);
        Assert.Equal(
            ["commandsPath", "ticks"],
            prototypeSchema
                .GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString())
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            PrototypeTuning.SessionTicks,
            prototypeSchema
                .GetProperty("properties")
                .GetProperty("ticks")
                .GetProperty("maximum")
                .GetInt32());

        await WriteMessageAsync(
            process,
            """
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"simulation_run","arguments":{"seed":424242,"agentCount":32,"ticks":256,"commandsPath":"../outside.json"}}}
            """);
        var rejected = await ReadMessageAsync(process);
        var rejectedResult = rejected.RootElement.GetProperty("result");
        Assert.True(rejectedResult.GetProperty("isError").GetBoolean());
        Assert.Contains(
            "must remain inside",
            rejectedResult
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString());

        await WriteMessageAsync(
            process,
            """
            {"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"simulation_run","arguments":{"seed":424242,"agentCount":32,"ticks":256,"source":"arbitrary"}}}
            """);
        var unknownArgument = await ReadMessageAsync(process);
        var unknownArgumentResult = unknownArgument.RootElement.GetProperty("result");
        Assert.True(unknownArgumentResult.GetProperty("isError").GetBoolean());
        Assert.Contains(
            "unknown argument(s): source",
            unknownArgumentResult
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString());

        await WriteMessageAsync(
            process,
            """
            {"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"prototype_run","arguments":{"ticks":1501,"commandsPath":"scenarios/prototype1/prepared.commands.v2.json"}}}
            """);
        var prototype = await ReadMessageAsync(process);
        var prototypeResult = prototype.RootElement
            .GetProperty("result")
            .GetProperty("structuredContent");
        Assert.Equal("prototype_result", prototypeResult.GetProperty("event").GetString());
        Assert.Equal(9, prototypeResult.GetProperty("creatureCount").GetInt32());
        Assert.Equal(6, prototypeResult.GetProperty("commandsApplied").GetInt32());
        Assert.True(prototypeResult.GetProperty("averageReadinessAtRaid").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(
            prototypeResult.GetProperty("canonicalJson").GetString()));
        Assert.True(
            prototypeResult
                .GetProperty("economy")
                .GetProperty("cookBatchesCompleted")
                .GetInt32() > 0);
        Assert.InRange(
            prototypeResult
                .GetProperty("labor")
                .GetProperty("foodWorkPercent")
                .GetInt32(),
            30,
            70);
        Assert.Equal(
            6,
            prototypeResult.GetProperty("stations").GetArrayLength());

        await WriteMessageAsync(
            process,
            """
            {"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"prototype_run","arguments":{"ticks":0,"commandsPath":"scenarios/prototype1/invalid-semantic.commands.v2.json"}}}
            """);
        var invalidPrototype = await ReadMessageAsync(process);
        var invalidPrototypeResult = invalidPrototype.RootElement.GetProperty("result");
        Assert.True(invalidPrototypeResult.GetProperty("isError").GetBoolean());
        Assert.Contains(
            "final larder feature",
            invalidPrototypeResult
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString());

        await WriteMessageAsync(
            process,
            """
            {"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"bridge_status","arguments":{}}}
            """);
        var bridgeStatus = await ReadMessageAsync(process);
        var bridgeStatusResult = bridgeStatus.RootElement
            .GetProperty("result")
            .GetProperty("structuredContent");
        Assert.Equal("bridge_status", bridgeStatusResult.GetProperty("event").GetString());
        Assert.Equal("ok", bridgeStatusResult.GetProperty("status").GetString());
        Assert.Equal(
            ["bridge_status", "prototype_run", "simulation_run"],
            bridgeStatusResult
                .GetProperty("tools")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());

        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);

        var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
        Assert.DoesNotContain("stdout", stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertClosedWorldSchema(
        JsonElement schema,
        string[] expectedProperties)
    {
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            expectedProperties,
            schema
                .GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task Stdout_contains_protocol_json_only()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var process = StartServer(repositoryRoot);

        await WriteMessageAsync(
            process,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"domain-mcp-tests","version":"1.0"}}}
            """);
        _ = await ReadMessageAsync(process);
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(ProcessTimeout);
        await process.WaitForExitAsync(timeout.Token);
        var remaining = await process.StandardOutput.ReadToEndAsync(timeout.Token);

        foreach (var line in remaining.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            using var protocolMessage = JsonDocument.Parse(line);
            Assert.Equal(
                "2.0",
                protocolMessage.RootElement.GetProperty("jsonrpc").GetString());
        }
    }

    private static Process StartServer(string repositoryRoot)
    {
        var process = new Process
        {
            StartInfo = new()
            {
                FileName = "dotnet",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(typeof(DomainTools).Assembly.Location);
        process.StartInfo.ArgumentList.Add("--root");
        process.StartInfo.ArgumentList.Add(repositoryRoot);
        Assert.True(process.Start());
        return process;
    }

    private static async Task WriteMessageAsync(Process process, string json)
    {
        await process.StandardInput.WriteLineAsync(json);
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonDocument> ReadMessageAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
        Assert.False(string.IsNullOrWhiteSpace(line));
        return JsonDocument.Parse(line);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DungeonFortress.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find the repository root.");
    }
}
