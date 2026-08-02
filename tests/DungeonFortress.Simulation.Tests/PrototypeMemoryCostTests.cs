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
        foreach (var cell in SixParties())
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

    /// <summary>
    /// The claim Issue #171 exists to make: <b>a party that wins its fights does
    /// not end it starving.</b>
    ///
    /// <para>
    /// It is asserted over all six parties rather than on the seed the defect
    /// was found on, because one seed cannot tell a fix from a coincidence. Both
    /// fixtures that reach a wave must come out of every seed alive; `neglected`
    /// is not here because it falls before the first wave, writes no memory at
    /// all and is the fixture whose falling is the point.
    /// </para>
    ///
    /// <para>
    /// Six parties and not the fifteen cells of the seed matrix of 13.4: the two
    /// causal pairs are outside this sample, and every count printed and asserted
    /// by this class is over the six. Named because one change set must not use
    /// the word `matrix` for two different denominators — found by the independent
    /// review of PR #178.
    /// </para>
    ///
    /// <para>
    /// On <c>main</c> this fails on baseline/20260728: the domain repels the wave
    /// at the larder, its defenders refuse to work there afterwards, and it falls
    /// at t2299 of hunger with a score of -223. It also fails with either half of
    /// the fix reverted on its own — see <c>evidence/171-mutations.json</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void A_party_that_wins_its_fights_does_not_end_it_starving()
    {
        foreach (var cell in SixParties())
        {
            Assert.True(
                cell.Outcome != "fallen",
                $"{cell.Fixture}/{cell.Seed} ended the party as `fallen` at t{cell.EndTick} with a " +
                $"score of {cell.Score}, having produced {cell.MealsProduced} meals and finished at " +
                $"an average satiety of {cell.AverageSatiety}. Memory of place refused work " +
                $"{cell.Refusals} times, {cell.RefusalsOnFoodChain} of them on a kitchen or larder " +
                "tile. A fixture that reaches its waves may lose them; it may not win them and then " +
                "starve because of what its defenders remember (Issue #171).");
        }
    }

    /// <summary>
    /// The shape of the price, and the sentence that separates a decision from a
    /// casualty: <b>a creature may choose differently because of what it
    /// remembers; it may not be removed from the domain by it.</b>
    ///
    /// <para>
    /// Measured as the longest unbroken run of ticks on which one creature
    /// refuses work. A creature that refuses on two hundred consecutive ticks is
    /// not making a choice — it is standing still for a tenth of the party, and
    /// the domain has lost a worker without losing a creature.
    /// </para>
    ///
    /// <para>
    /// The bound is 100 and the shipped run reaches 87, which is a margin of
    /// thirteen ticks and is named rather than hidden: this is a fitted bound,
    /// and it is worth having only because both halves of the fix cross it on
    /// their own. On <c>main</c> the same figure is 213; with the hunger bound
    /// reverted it is 157 and with the ageing reverted 114. The measurements are
    /// in <c>evidence/171-mutations.json</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void No_creature_is_taken_out_of_the_domain_by_what_it_remembers()
    {
        const int bound = 100;
        foreach (var cell in SixParties())
        {
            Assert.True(
                cell.LongestRefusalStreakOneCreature <= bound,
                $"{cell.Fixture}/{cell.Seed}: one creature refused work by memory on " +
                $"{cell.LongestRefusalStreakOneCreature} consecutive ticks, and the bound is " +
                $"{bound}. Over that party memory refused {cell.Refusals} times, " +
                $"{cell.RefusalsOnFoodChain} of them on a kitchen or larder tile, and the domain " +
                $"produced {cell.MealsProduced} meals. A refusal is a different choice; an unbroken " +
                "run of them is a worker the domain no longer has.");
        }
    }

    /// <summary>
    /// The first of the two bounds, as its own rule: <b>no refusal ever acts on a
    /// memory older than <see cref="PrototypeTuning.MemoryAvoidTicks"/>.</b>
    ///
    /// <para>
    /// This is an invariant of the rule rather than a measurement of the party —
    /// the same kind of check, and honest about it for the same reason, as the
    /// note in contract 5.1 about
    /// <c>A_refusal_by_memory_names_the_work_the_creature_would_have_taken</c>.
    /// Its worth is that deleting the ageing arm of <c>AvoidedPlace</c> cannot
    /// pass it: on <c>main</c> the oldest memory still refusing work is 420 ticks
    /// old, and with only this half reverted it is 694.
    /// </para>
    /// </summary>
    [Fact]
    public void A_memory_stops_refusing_work_before_the_next_wave_arrives()
    {
        Assert.True(
            PrototypeTuning.MemoryAvoidTicks < PrototypeTuning.WaveIntervalTicks,
            $"a memory refuses work for {PrototypeTuning.MemoryAvoidTicks} ticks and waves are " +
            $"{PrototypeTuning.WaveIntervalTicks} apart. Longer than the interval, a fright " +
            "outlives the quiet window the party has to feed itself in, and frights compound wave " +
            "over wave instead of healing between them.");
        foreach (var cell in SixParties())
        {
            Assert.True(
                cell.OldestMemoryAtRefusal <= PrototypeTuning.MemoryAvoidTicks,
                $"{cell.Fixture}/{cell.Seed}: a creature refused work because of a place it had " +
                $"remembered {cell.OldestMemoryAtRefusal} ticks earlier, and avoidance is supposed " +
                $"to run out after {PrototypeTuning.MemoryAvoidTicks}.");
        }
    }

    /// <summary>
    /// The second of the two bounds, as its own rule: <b>a creature below
    /// <see cref="PrototypeTuning.MemoryYieldsSatiety"/> never refuses work by
    /// memory</b>, and over the six parties that actually happens rather than being
    /// vacuously true.
    ///
    /// <para>
    /// The second half is what makes this a check instead of a tautology: the
    /// rule only matters if hungry creatures are ever offered work they remember
    /// a fright at, and the count says how often the domain was saved by it.
    /// </para>
    /// </summary>
    [Fact]
    public void A_creature_going_hungry_takes_the_work_it_would_otherwise_refuse()
    {
        var yielded = 0;
        var report = new StringBuilder();
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
                var here = 0;
                // Satiety is read off the snapshot taken *before* the step, because
                // that is the state the tick decided on: it decays inside the same
                // tick, so a creature that was at the threshold when it chose is
                // one point under it by the time the tick is over.
                var before = world.GetSnapshot();
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

                        var creature = before.Creatures.Single(item => item.Id == @event.CreatureId);
                        Assert.True(
                            creature.Satiety >= PrototypeTuning.MemoryYieldsSatiety,
                            $"{fixtureName}/{seed}: on tick {acted} {creature.Name} refused work at a " +
                            $"place it remembers while its satiety stood at {creature.Satiety}, under " +
                            $"the {PrototypeTuning.MemoryYieldsSatiety} at which hunger is supposed to " +
                            "outrank what a creature remembers.");
                    }

                    // A creature that is under the threshold, is holding a memory
                    // and took work anyway: the rule doing its job, counted where
                    // it happens rather than inferred from the outcome.
                    here += state.Creatures.Count(creature =>
                        before.Creatures.Single(item => item.Id == creature.Id).Satiety <
                            PrototypeTuning.MemoryYieldsSatiety &&
                        creature.RememberedPlaces.Count > 0 &&
                        creature.CurrentJobId is not null &&
                        creature.LastDecision.Tick == acted &&
                        creature.LastDecision.Target is { } target &&
                        creature.RememberedPlaces.Any(place =>
                            Manhattan(place.Place, target) <= PrototypeTuning.MemoryAvoidRadius &&
                            acted - place.Tick <= PrototypeTuning.MemoryAvoidTicks));
                    before = state;
                }

                yielded += here;
                report.AppendLine(CultureInfo.InvariantCulture, $"{fixtureName}/{seed}: {here}");
            }
        }

        Assert.True(
            yielded > 0,
            "over all six parties no hungry creature ever took work at a place it remembers, so " +
            $"the bound was never reached and this check tested nothing.{Environment.NewLine}{report}");
    }

    private static int Manhattan(GridPoint left, GridPoint right) =>
        Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private static string Format(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0
            ? "none"
            : string.Join(", ", counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

    /// <summary>
    /// The sample every figure of this class is taken on: the two fixtures that
    /// reach a wave, on the three seeds of the matrix — six parties. It is
    /// deliberately not called the matrix: that word names the fifteen cells of
    /// 13.4, and `neglected`, `prepared-ration-zero` and `prepared-watch-zero`
    /// are not walked here.
    /// </summary>
    private static List<Cell> SixParties()
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
