using DungeonFortress.DomainMcp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

var projectRoot = ProjectRoot.Resolve(args);
var builder = Host.CreateApplicationBuilder([]);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton(projectRoot);
var domainTools = new DomainTools(projectRoot);
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "dungeon-fortress-domain",
            Version = DomainBridgeInfo.Version,
        };
    })
    .WithStdioServerTransport()
    .WithTools(
    [
        DomainMcpTool.CreateBridgeStatus(domainTools),
        DomainMcpTool.CreatePrototypeRun(domainTools),
        DomainMcpTool.CreateSimulationRun(domainTools),
    ]);

await builder.Build().RunAsync();
