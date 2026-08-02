using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// What memory of place <b>costs the domain</b> when the tile it lands on is a
/// tile somebody works on (Issue #171).
///
/// <para>
/// <c>PrototypeMemoryTests</c> asks whether the memory is written, is personal
/// and is told truthfully. None of that is in question here. The question here
/// is the price: after Issue #129 defenders stand around a raider instead of
/// behind one another, a raider stands at the larder, and so the tiles a broken
/// nerve is written on are the tiles the food chain runs through. The reading
/// half of memory then takes that work away from the creature — and on
/// <c>baseline</c>/20260728 it took away enough of it that the domain won every
/// fight and starved.
/// </para>
///
/// <para>
/// The whole class is one measurement plus the bounds that measurement is held
/// to. The measurement is printed on every run, before and after the fix, with
/// the same command:
/// <c>dotnet test tests/DungeonFortress.Simulation.Tests -c Release --filter
/// "FullyQualifiedName~PrototypeMemoryCostTests" --logger
/// "console;verbosity=detailed"</c>
/// </para>
/// </summary>
public sealed class PrototypeMemoryCostTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];
    private static readonly string[] Fixtures = ["baseline", "prepared"];

    /// <summary>
    /// The kitchen and the larder as the map paints them
    /// (<c>PrototypeMap</c>: kitchen x9..12, larder x13..16, both y6..8).
    /// Work that starts on one of these tiles is the food chain, and it is the
    /// only work in Prototype 1 whose loss ends the party.
    /// </summary>
    private static bool IsFoodChain(GridPoint tile) =>
        tile.X >= 9 && tile.X <= 16 && tile.Y >= 6 && tile.Y <= 8;

    /// <summary>
    /// One party, read for the price of memory: what it produced, what it ended
    /// as, how often memory refused work, whose refusals those were, where they
    /// landed and how old the memory behind them was.
    /// </summary>
    private sealed record Cell(
        string Fixture,
        ulong Seed,
        string? Outcome,
        int? EndTick,
        int? Score,
        int MealsProduced,
        int AverageSatiety,
        int Refusals,
        int RefusingCreatures,
        int RefusalsOnFoodChain,
        int LongestRefusalStreakOneCreature,
        int OldestMemoryAtRefusal,
        IReadOnlyDictionary<string, int> RefusedKinds,
        IReadOnlyDictionary<string, int> RefusedTiles);

    /// <summary>
    /// The measurement. Printed rather than asserted: the bounds below say what
    /// of it must hold, and this says what it actually is, so that a number in
    /// evidence is never a copy of a number in a document.
    /// </summary>
    [Fact]
    public void Report_what_memory_of_place_costs_the_domain()
    {
        var report = new StringBuilder();
        foreach (var cell in Matrix())
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{cell.Fixture}/{cell.Seed}: {cell.Outcome} at t{cell.EndTick}, score {cell.Score}, " +
                $"mealsProduced {cell.MealsProduced}, averageSatiety {cell.AverageSatiety}, " +
                $"refusals {cell.Refusals} by {cell.RefusingCreatures} creature(s), " +
                $"{cell.RefusalsOnFoodChain} of them on kitchen/larder tiles, " +
                $"longest streak of one creature {cell.LongestRefusalStreakOneCreature} tick(s), " +
                $"oldest memory acted on {cell.OldestMemoryAtRefusal} tick(s)");
            if (cell.Refusals > 0)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"    kinds: {Format(cell.RefusedKinds)}; tiles: {Format(cell.RefusedTiles)}");
            }
        }

        output.WriteLine(report.ToString());
    }

    private static string Format(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0
            ? "none"
            : string.Join(", ", counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

    private static List<Cell> Matrix()
    {
        var cells = new List<Cell>();
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                cells.Add(Measure(fixtureName, seed));
            }
        }

        return cells;
    }

    /// <summary>
    /// One party walked tick by tick. The refusals are counted as they happen
    /// rather than read off the end, because the cap can push a memory out after
    /// the refusal it caused.
    /// </summary>
    private static Cell Measure(string fixtureName, ulong seed)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var perCreature = new Dictionary<int, int>();
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var tiles = new Dictionary<string, int>(StringComparer.Ordinal);
        var onFoodChain = 0;
        var oldest = 0;
        var streak = new Dictionary<int, (int Last, int Length, int Longest)>();

        while (!world.IsComplete)
        {
            world.Step();
            var state = world.GetSnapshot();
            var acted = state.Tick - 1;
            foreach (var @event in state.Events)
            {
                if (@event.LastTick != acted ||
                    @event.ReasonCode is not ("refused_place_of_panic" or "refused_place_of_wound"))
                {
                    continue;
                }

                perCreature[@event.CreatureId] = perCreature.GetValueOrDefault(@event.CreatureId) + 1;
                var kind = @event.JobKind?.ToString() ?? "none";
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                oldest = Math.Max(oldest, acted - @event.Details["sinceTick"]);
                if (@event.Target is { } target)
                {
                    var key = string.Create(CultureInfo.InvariantCulture, $"({target.X},{target.Y})");
                    tiles[key] = tiles.GetValueOrDefault(key) + 1;
                    if (IsFoodChain(target))
                    {
                        onFoodChain++;
                    }
                }

                // How long one creature goes on refusing without a break. This is
                // the shape of the cost the party actually pays: a creature that
                // refuses on two hundred consecutive ticks is a creature the
                // domain has lost, not a creature that made a different choice.
                var previous = streak.GetValueOrDefault(@event.CreatureId, (Last: int.MinValue, Length: 0, Longest: 0));
                var length = previous.Last == acted - 1 ? previous.Length + 1 : 1;
                streak[@event.CreatureId] =
                    (acted, length, Math.Max(previous.Longest, length));
            }
        }

        var final = world.GetSnapshot();
        return new Cell(
            fixtureName,
            seed,
            final.SessionResult.Outcome,
            final.SessionResult.EndTick,
            final.SessionResult.Score,
            final.Stocks.MealsProduced,
            final.Creatures.Sum(creature => creature.Satiety) / final.Creatures.Count,
            perCreature.Values.Sum(),
            perCreature.Count,
            onFoodChain,
            streak.Values.Count == 0 ? 0 : streak.Values.Max(item => item.Longest),
            oldest,
            kinds,
            tiles);
    }

    private static PrototypeCommandLog LoadFixture(string fixtureName) =>
        PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(), "scenarios", "prototype1", $"{fixtureName}.commands.v2.json"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DungeonFortress.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
