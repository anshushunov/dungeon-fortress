using System.Diagnostics;
using System.Text;
using System.Text.Json;

using DungeonFortress.Simulation;

namespace DungeonFortress.Scenarios;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--evaluate-prototype", StringComparer.Ordinal))
        {
            return PrototypeEvaluation.Run(args);
        }

        try
        {
            var options = ScenarioOptions.Parse(args);
            if (options.Prototype)
            {
                return RunPrototype(options);
            }

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

    private static int RunPrototype(ScenarioOptions options)
    {
        if (options.CommandsPath is null)
        {
            throw new ArgumentException("--prototype requires --commands with a gameplay-v2 document.");
        }

        if (options.SeedSpecified || options.AgentCountSpecified)
        {
            throw new ArgumentException(
                "--prototype reads seed and creature count from the gameplay-v2 document; " +
                "--seed and --agents are not accepted.");
        }

        var commandLog = PrototypeCommandDocument.Load(options.CommandsPath);
        var stopwatch = Stopwatch.StartNew();
        var result = PrototypeScenario.Run(commandLog, options.TickCount);
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

        var creatures = result.State.Creatures;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            @event = "prototype_result",
            status = "ok",
            scenario = commandLog.Scenario,
            seed = commandLog.Seed,
            ticks = result.Tick,
            commandsApplied = result.CommandsApplied,
            checksum = result.Checksum,
            mealsProduced = result.State.Stocks.MealsProduced,
            mealsEaten = result.State.Stocks.MealsEaten,
            meals = result.State.Stocks.Meals,
            rawMushroom = result.State.Stocks.RawMushroom,
            looseStone = result.State.Stocks.LooseStone,
            carriedStone = result.State.Stocks.CarriedStone,
            storedStone = result.State.Stocks.StoredStone,
            reservedStone = result.State.Stocks.ReservedStone,
            stockpileCapacity = result.State.Stocks.StockpileCapacity,
            materialStockpile = result.State.StockpileCells,
            digsCompleted = result.State.Economy.DigsCompleted,
            digDesignations = result.State.DigDesignations,
            excavatedTiles = result.State.Map.ExcavatedTiles,
            averageSatiety = (int)creatures.Average(creature => creature.Satiety),
            averageFatigue = (int)creatures.Average(creature => creature.Fatigue),
            averageMartialForm = (int)creatures.Average(creature => creature.MartialForm),
            averageReadinessAtRaid = creatures.All(creature => creature.ReadinessAtRaid is not null)
                ? (int?)creatures.Average(creature => creature.ReadinessAtRaid!.Value)
                : null,
            creatureCount = creatures.Count,
            jobCount = result.State.Jobs.Count,
            eventCount = result.State.Events.Count,
            economy = result.State.Economy,
            labor = result.State.Labor,
            stations = result.State.Stations,
            elapsedMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
        }));
        return 0;
    }
}
