using System.Diagnostics;
using System.Text;
using System.Text.Json;

using DungeonFortress.Simulation;

namespace DungeonFortress.Scenarios;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = ScenarioOptions.Parse(args);
            var commands = options.CommandsPath is null
                ? []
                : SimulationCommandDocument.Load(options.CommandsPath);
            var config = new SimulationConfig(options.Seed, options.AgentCount);

            var stopwatch = Stopwatch.StartNew();
            var result = SimulationScenario.Run(config, options.TickCount, commands);
            stopwatch.Stop();

            if (options.SnapshotPath is not null)
            {
                var fullPath = Path.GetFullPath(options.SnapshotPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllBytes(fullPath, result.CanonicalJson);
            }

            if (options.PrintSnapshot)
            {
                Console.WriteLine(Encoding.UTF8.GetString(result.CanonicalJson));
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                @event = "scenario_result",
                status = "ok",
                seed = options.Seed,
                agentCount = options.AgentCount,
                ticks = result.Tick,
                commandsApplied = result.CommandsApplied,
                snapshotBytes = result.CanonicalJson.Length,
                checksum = result.Checksum,
                elapsedMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
            }));

            return 0;
        }
        catch (HelpRequestedException)
        {
            Console.WriteLine(ScenarioOptions.Usage);
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or FormatException
            or IOException
            or JsonException
            or OverflowException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                @event = "scenario_error",
                status = "error",
                errorType = exception.GetType().Name,
                message = exception.Message,
            }));
            Console.Error.WriteLine(ScenarioOptions.Usage);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                @event = "scenario_error",
                status = "error",
                errorType = exception.GetType().Name,
                message = exception.Message,
            }));
            return 1;
        }
    }
}
