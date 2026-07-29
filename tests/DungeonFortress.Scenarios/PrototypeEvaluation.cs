using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using DungeonFortress.Simulation;

namespace DungeonFortress.Scenarios;

internal static class PrototypeEvaluation
{
    // A party ends on its own tick now, so the runner asks for the fuse and gets
    // whatever the party actually took.
    private static readonly int Ticks = PrototypeTuning.SessionTicks;
    private static readonly ulong[] Seeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];
    private static readonly string[] MatrixScenarios = ["baseline", "prepared", "neglected"];
    private static readonly string[] PairScenarios = ["prepared-ration-zero", "prepared-watch-zero"];

    public static int Run(string[] args)
    {
        try
        {
            var options = EvaluationOptions.Parse(args);
            var results = BuildReport(options.RepositoryRoot);
            var destination = options.Verify
                ? Path.Combine(Path.GetTempPath(), $"dungeon-fortress-evaluation-{Guid.NewGuid():N}.json")
                : options.OutputPath;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, JsonSerializer.Serialize(results), new UTF8Encoding(false));
                if (options.Verify && !File.ReadAllBytes(options.OutputPath).SequenceEqual(File.ReadAllBytes(destination)))
                {
                    throw new InvalidDataException("Evaluation replay differs from the committed compact evidence.");
                }
            }
            finally
            {
                if (options.Verify && File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                @event = "prototype_evaluation_result",
                status = "ok",
                verify = options.Verify,
                output = options.OutputPath,
                runCount = results.Runs.Count,
                pairCount = results.CausalPairs.Count,
            }));
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                @event = "prototype_evaluation_error",
                status = "error",
                errorType = exception.GetType().Name,
                message = exception.Message,
            }));
            return 2;
        }
    }

    private static EvaluationReport BuildReport(string repositoryRoot)
    {
        var runs = new List<EvaluationRun>();
        var lookup = new Dictionary<(string Scenario, ulong Seed), EvaluationRun>();
        foreach (var scenario in MatrixScenarios.Concat(PairScenarios))
        {
            foreach (var seed in Seeds)
            {
                var first = RunOnce(repositoryRoot, scenario, seed);
                var second = RunOnce(repositoryRoot, scenario, seed);
                if (first.Checksum != second.Checksum)
                {
                    throw new InvalidDataException($"Non-deterministic checksum for {scenario}/{seed}.");
                }

                var run = new EvaluationRun(
                    scenario,
                    seed,
                    first.Checksum,
                    first.CommandsApplied,
                    second.Checksum,
                    true,
                    first.Metrics);
                runs.Add(run);
                lookup.Add((scenario, seed), run);
            }
        }

        var pairs = new List<CausalPairResult>();
        foreach (var definition in new[]
        {
            ("CP1", "prepared-ration-zero", "ration_reserve: 6 -> 0 at tick 320"),
            ("CP2", "prepared-watch-zero", "Watch priority: 3 -> 0 at tick 900"),
        })
        {
            foreach (var seed in Seeds)
            {
                var control = lookup[("prepared", seed)];
                var treatment = lookup[(definition.Item2, seed)];
                pairs.Add(new CausalPairResult(
                    definition.Item1,
                    seed,
                    "prepared",
                    definition.Item2,
                    definition.Item3,
                    control.Checksum,
                    treatment.Checksum,
                    Delta(control.Metrics, treatment.Metrics)));
            }
        }

        return new EvaluationReport(
            1,
            "tests/DungeonFortress.Scenarios --evaluate-prototype",
            "fc2a9bd",
            Ticks,
            Seeds,
            MatrixScenarios,
            ["prepared", "prepared-ration-zero", "prepared-watch-zero"],
            runs,
            pairs);
    }

    private static RunCapture RunOnce(string repositoryRoot, string scenario, ulong seed)
    {
        var path = Path.Combine(repositoryRoot, "scenarios", "prototype1", $"{scenario}.commands.v2.json");
        var document = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
        document["seed"] = seed;
        if (scenario.StartsWith("prepared-", StringComparison.Ordinal))
        {
            document["scenario"] = "prepared";
        }

        var log = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(document.ToJsonString()));
        var result = PrototypeScenario.Run(log, Ticks);
        return new RunCapture(result.Checksum, result.CommandsApplied, CaptureMetrics(result.State));
    }

    private static EvaluationMetrics CaptureMetrics(PrototypeSnapshot state)
    {
        var creatures = state.Creatures;
        var reasonCoverage = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var @event in state.Events)
        {
            reasonCoverage.TryGetValue(@event.ReasonCode, out var repeats);
            reasonCoverage[@event.ReasonCode] = repeats + @event.Repeats;
        }

        return new EvaluationMetrics(
            new EconomyMetrics(
                state.Economy.HarvestsCompleted,
                state.Economy.RawHaulsCompleted,
                state.Economy.CookBatchesCompleted,
                state.Economy.MealHaulsCompleted,
                state.Stocks.MealsProduced,
                state.Stocks.MealsEaten,
                state.Stocks.Meals),
            new LaborMetrics(
                state.Labor.FoodWorkTicks,
                state.Labor.RestTicks,
                state.Labor.EatTicks,
                state.Labor.DrillTicks,
                state.Labor.WatchTicks,
                state.Labor.MusterTicks,
                state.Labor.IdleTicks,
                state.Labor.PostOccupancyPercent),
            new CreatureMetrics(
                creatures.Count,
                creatures.Sum(creature => creature.Satiety) / creatures.Count,
                creatures.Sum(creature => creature.Fatigue) / creatures.Count,
                creatures.Sum(creature => creature.MartialForm) / creatures.Count,
                creatures.All(creature => creature.ReadinessAtRaid is not null)
                    ? creatures.Sum(creature => creature.ReadinessAtRaid!.Value) / creatures.Count
                    : null,
                creatures.GroupBy(creature => creature.Mode.ToString())
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                creatures.Count(creature => creature.Injury != InjuryKind.None),
                creatures.Count(creature => creature.Mode == CreatureMode.Downed),
                creatures.Count(creature => creature.Mode == CreatureMode.Fled),
                creatures.Select(creature => creature.Name).ToArray()),
            new SessionMetrics(
                state.SessionResult.Outcome,
                state.SessionResult.EndTick,
                state.SessionResult.Unresolved,
                state.SessionResult.DefendersDowned,
                state.SessionResult.DefendersFled,
                state.SessionResult.RaidersDowned,
                state.SessionResult.MealsStolen,
                state.SessionResult.MealsLeft,
                // The two numbers the party is scored by, and the shape of the
                // pressure that produced them. Without these the evidence would
                // still be measuring a single raid.
                state.Domain.Renown,
                state.Domain.Strength,
                state.Domain.LivingCreatures,
                state.SessionResult.WavesResolved,
                state.SessionResult.WaveCount,
                state.Waves
                    .Select(wave => new WaveMetrics(
                        wave.Number,
                        wave.ArriveTick,
                        wave.RaiderCount,
                        wave.RaiderMight,
                        wave.RenownAtAnnounce,
                        wave.Outcome,
                        wave.EndTick,
                        wave.RaidersDowned,
                        wave.DefendersDowned,
                        wave.DefendersFled,
                        wave.MealsStolen))
                    .ToArray()),
            new ExplainabilityMetrics(state.Events.Count, reasonCoverage.Count, reasonCoverage));
    }

    private static CausalDelta Delta(EvaluationMetrics control, EvaluationMetrics treatment) => new(
        NullableDelta(treatment.Creatures.AverageReadinessAtRaid, control.Creatures.AverageReadinessAtRaid),
        treatment.Labor.WatchTicks - control.Labor.WatchTicks,
        treatment.Labor.PostOccupancyPercent - control.Labor.PostOccupancyPercent,
        treatment.Session.DefendersDowned - control.Session.DefendersDowned,
        treatment.Session.DefendersFled - control.Session.DefendersFled,
        treatment.Session.RaidersDowned - control.Session.RaidersDowned,
        treatment.Session.MealsStolen - control.Session.MealsStolen,
        treatment.Session.MealsLeft - control.Session.MealsLeft);

    private static int? NullableDelta(int? left, int? right) => left is not null && right is not null ? left - right : null;

    private sealed record EvaluationOptions(string RepositoryRoot, string OutputPath, bool Verify)
    {
        public static EvaluationOptions Parse(string[] args)
        {
            string? root = null;
            string? output = null;
            var verify = false;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--evaluate-prototype": break;
                    case "--repository-root": root = RequireValue(args, ref index); break;
                    case "--output": output = RequireValue(args, ref index); break;
                    case "--verify": verify = true; break;
                    default: throw new ArgumentException($"Unknown evaluation option: {args[index]}");
                }
            }

            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(output))
            {
                throw new ArgumentException("--evaluate-prototype requires --repository-root and --output.");
            }

            return new EvaluationOptions(Path.GetFullPath(root), Path.GetFullPath(output), verify);
        }

        private static string RequireValue(string[] args, ref int index)
        {
            if (++index >= args.Length) { throw new ArgumentException("Missing evaluation option value."); }
            return args[index];
        }
    }

    private sealed record RunCapture(string Checksum, int CommandsApplied, EvaluationMetrics Metrics);
    private sealed record EvaluationReport(int SchemaVersion, string Generator, string ImplementationBaseline, int Ticks, IReadOnlyList<ulong> Seeds, IReadOnlyList<string> MatrixScenarios, IReadOnlyList<string> CausalPairControls, IReadOnlyList<EvaluationRun> Runs, IReadOnlyList<CausalPairResult> CausalPairs);
    private sealed record EvaluationRun(string Scenario, ulong Seed, string Checksum, int CommandsApplied, string RepeatChecksum, bool RepeatIdentical, EvaluationMetrics Metrics);
    private sealed record CausalPairResult(string Id, ulong Seed, string Control, string Treatment, string ChangedIntent, string ControlChecksum, string TreatmentChecksum, CausalDelta Delta);
    private sealed record CausalDelta(int? ReadinessAtRaid, int WatchTicks, int PostOccupancyPercent, int DefendersDowned, int DefendersFled, int RaidersDowned, int MealsStolen, int MealsLeft);
    private sealed record EvaluationMetrics(EconomyMetrics Economy, LaborMetrics Labor, CreatureMetrics Creatures, SessionMetrics Session, ExplainabilityMetrics Explainability);
    private sealed record EconomyMetrics(int HarvestsCompleted, int RawHaulsCompleted, int CookBatchesCompleted, int MealHaulsCompleted, int MealsProduced, int MealsEaten, int MealsCurrent);
    private sealed record LaborMetrics(int FoodWorkTicks, int RestTicks, int EatTicks, int DrillTicks, int WatchTicks, int MusterTicks, int IdleTicks, int PostOccupancyPercent);
    private sealed record CreatureMetrics(int Count, int AverageSatiety, int AverageFatigue, int AverageMartialForm, int? AverageReadinessAtRaid, IReadOnlyDictionary<string, int> Modes, int Injured, int Downed, int Fled, IReadOnlyList<string> Names);
    private sealed record SessionMetrics(string? Outcome, int? EndTick, bool Unresolved, int DefendersDowned, int DefendersFled, int RaidersDowned, int MealsStolen, int MealsLeft, int Renown, int Strength, int LivingCreatures, int WavesResolved, int WaveCount, IReadOnlyList<WaveMetrics> Waves);
    private sealed record WaveMetrics(int Number, int ArriveTick, int RaiderCount, int RaiderMight, int RenownAtAnnounce, string? Outcome, int? EndTick, int RaidersDowned, int DefendersDowned, int DefendersFled, int MealsStolen);
    private sealed record ExplainabilityMetrics(int EventCount, int DistinctReasonCodes, IReadOnlyDictionary<string, int> ReasonCodeOccurrences);
}
