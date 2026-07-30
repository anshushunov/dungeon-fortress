using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            siteStone = result.State.Stocks.SiteStone,
            reservedStone = result.State.Stocks.ReservedStone,
            stockpileCapacity = result.State.Stocks.StockpileCapacity,
            materialStockpile = result.State.StockpileCells,
            digsCompleted = result.State.Economy.DigsCompleted,
            digDesignations = result.State.DigDesignations,
            excavatedTiles = result.State.Map.ExcavatedTiles,
            buildsCompleted = result.State.Economy.BuildsCompleted,
            stoneConsumed = result.State.Economy.StoneConsumed,
            buildSites = result.State.BuildSites,
            builtPostTiles = result.State.Map.BuiltPostTiles,
            averageSatiety = (int)creatures.Average(creature => creature.Satiety),
            averageFatigue = (int)creatures.Average(creature => creature.Fatigue),
            averageMartialForm = (int)creatures.Average(creature => creature.MartialForm),
            averageReadinessAtRaid = creatures.All(creature => creature.ReadinessAtRaid is not null)
                ? (int?)creatures.Average(creature => creature.ReadinessAtRaid!.Value)
                : null,
            creatureCount = creatures.Count,
            // The party is a sequence now, so a headless run has to be able to
            // read the whole sequence, the two numbers it is scored by and how
            // it ended, without opening a snapshot file or a picture.
            threat = result.State.Threat,
            waves = result.State.Waves,
            domain = result.State.Domain,
            sessionResult = SessionResultLine(result.State.SessionResult),
            injuries = creatures
                .Select(creature => new
                {
                    creature.Id,
                    creature.Name,
                    creature.Hp,
                    creature.MaxHp,
                    injury = creature.Injury.ToString(),
                    mode = creature.Mode.ToString(),
                    creature.RecoveryTicks,
                })
                .ToArray(),
            jobCount = result.State.Jobs.Count,
            eventCount = result.State.Events.Count,
            economy = result.State.Economy,
            labor = result.State.Labor,
            stations = result.State.Stations,
            elapsedMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
        }));
        return 0;
    }

    /// <summary>
    /// The session summary as the headline `prototype_result` line carries it.
    /// `prototype_result` is a derived report and may hold facts the canonical
    /// snapshot does not — but where it repeats a canonical fact it repeats its
    /// form, and the score is a fact whose form is its presence: a party that
    /// has not ended has no score at all rather than an empty one (ADR 0016,
    /// contract 12.1, and the versioning rule in
    /// docs/engineering/PROTOTYPE_HEADLESS.md).
    ///
    /// Reflection over the record cannot say that — an `int?` that is null comes
    /// out as `"Score": null`, which is the single form the decision rules out —
    /// so the one field is dropped after serialising rather than the whole
    /// summary being rewritten by hand. Written by hand it would silently lose
    /// every field added to the record later.
    /// </summary>
    private static JsonNode SessionResultLine(PrototypeSessionResultSnapshot sessionResult)
    {
        var line = JsonSerializer.SerializeToNode(sessionResult)!.AsObject();
        if (sessionResult.Score is null)
        {
            line.Remove(nameof(PrototypeSessionResultSnapshot.Score));
        }

        return line;
    }
}
