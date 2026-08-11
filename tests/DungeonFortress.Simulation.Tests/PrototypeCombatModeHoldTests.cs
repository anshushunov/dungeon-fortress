using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Issue #333 — probe. Counts, over the shipped journals, how often the fact
/// "this creature is in the line" fails to survive the tick it was decided in.
/// </summary>
public sealed class PrototypeCombatModeHoldTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    private static readonly string[] Fixtures = ["baseline", "prepared", "neglected"];

    [Fact]
    public void Report_combat_mode_hold_over_the_seed_matrix()
    {
        var report = new StringBuilder();
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"{Measure(fixtureName, seed)}");
            }
        }

        output.WriteLine(report.ToString());
    }

    private static Measurement Measure(string fixtureName, ulong seed)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var tally = new Measurement(fixtureName, seed);
        var previous = world.GetSnapshot();
        var modeByTick = new Dictionary<int, Dictionary<int, CreatureMode>>();

        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            if (current.Tick == previous.Tick)
            {
                tally.StepsThatMovedNoTick++;

                // What the view will draw on this frame, by BlowReadout's own
                // rule — the fresh entries of the journal are the ones stamped
                // `Tick - 1`. The rule is restated rather than called, the same
                // way PrototypeCombatApproachTests restates ActCombatant's target
                // rule: a measurement that asks the code what it did cannot
                // disagree with it. The tick does not move while the window is
                // open, so `Tick - 1` keeps naming the tick the wave ended on and
                // the same blows are drawn for the whole window.
                var bodies = current.Raiders
                    .Where(raider => raider.Mode == RaiderMode.Downed)
                    .Select(raider => raider.Id)
                    .ToHashSet();
                var drawn = current.Events
                    .Where(entry =>
                        entry.LastTick == current.Tick - 1 &&
                        entry.ReasonCode is "combat_attack" or "combat_raider_downed")
                    .ToArray();
                var onBodies = drawn.Count(entry =>
                    entry.Details.TryGetValue("raiderId", out var id) && bodies.Contains(id));
                tally.BlowsDrawnWhilePaused += drawn.Length;
                tally.BlowsDrawnOnABodyWhilePaused += onBodies;
                if (!tally.WindowOpen)
                {
                    tally.WindowOpen = true;
                    tally.Windows.Add($"t{current.Tick - 1}:{onBodies}/{drawn.Length}");
                }

                previous = current;
                // A step spent waiting for a verdict runs no phase and moves no
                // tick (Issue #312). Counting it would read the same decision of
                // the same tick again against a world that has already moved on
                // — the whole of the 39 phantom "strikes on a downed raider"
                // this probe reported before the guard existed.
                continue;
            }

            tally.WindowOpen = false;
            var acted = current.Tick - 1;
            modeByTick[acted] = current.Creatures.ToDictionary(c => c.Id, c => c.Mode);
            Observe(previous, current, acted, tally);
            previous = current;
        }

        tally.Ticks = previous.Tick;
        ScanJoins(previous, modeByTick, tally);
        return tally;
    }

    /// <summary>
    /// Everything that can be read by comparing the snapshot after tick T-1 with
    /// the snapshot after tick T.
    /// </summary>
    private static void Observe(
        PrototypeSnapshot previous,
        PrototypeSnapshot current,
        int acted,
        Measurement tally)
    {
        var raidersBefore = previous.Raiders.ToDictionary(raider => raider.Id, raider => raider.Mode);
        var standing = current.Raiders.Count(raider => raider.Mode == RaiderMode.Raiding);
        var bodies = current.Raiders.Count(raider => raider.Mode == RaiderMode.Downed);
        tally.MaxRaiderBodiesOnTheMap = Math.Max(tally.MaxRaiderBodiesOnTheMap, bodies);
        tally.RaiderBodyTicks += bodies;
        if (standing == 0)
        {
            tally.FightingTicksWithNothingToFight +=
                current.Creatures.Count(creature => creature.Mode == CreatureMode.Fighting);
        }

        foreach (var creature in current.Creatures)
        {
            var decision = creature.LastDecision;
            if (decision.Tick == acted &&
                decision.ReasonCode is "combat_attack" or "combat_raider_downed" &&
                decision.Details.TryGetValue("raiderId", out var raiderId) &&
                raidersBefore.TryGetValue(raiderId, out var modeBefore) &&
                modeBefore != RaiderMode.Raiding)
            {
                tally.StrikesOnARaiderAlreadyOut++;
                tally.FirstStrikeOnARaiderAlreadyOut ??= $"t{acted} creature#{creature.Id} raider#{raiderId} was {modeBefore}";
            }
        }

        tally.YieldsBookedByAFighter += current.Creatures.Count(creature =>
            creature.Mode == CreatureMode.Fighting &&
            creature.LastDecision.Tick == acted &&
            creature.LastDecision.ReasonCode == "chosen_traffic_yield");

        var before = previous.Creatures.ToDictionary(creature => creature.Id);
        var waveResolvedThisTick = current.Waves.Any(wave =>
            wave.Outcome is not null && wave.EndTick == acted);

        foreach (var creature in current.Creatures)
        {
            var was = before[creature.Id].Mode;
            var now = creature.Mode;
            if (was == now)
            {
                continue;
            }

            if (was == CreatureMode.Fighting)
            {
                if (now is CreatureMode.Fled or CreatureMode.Downed)
                {
                    continue;
                }

                if (waveResolvedThisTick && now == CreatureMode.Waiting)
                {
                    continue;
                }

                tally.LeftTheLineWithoutLeavingTheFight++;
                tally.LeftInto[now] = tally.LeftInto.GetValueOrDefault(now) + 1;
                tally.LeftBecause[creature.LastDecision.ReasonCode] =
                    tally.LeftBecause.GetValueOrDefault(creature.LastDecision.ReasonCode) + 1;
                tally.FirstSilentLeave ??=
                    $"t{acted} creature#{creature.Id} Fighting->{now} ({creature.LastDecision.ReasonCode}@t{creature.LastDecision.Tick}, satiety={creature.Satiety})";
                tally.OutOfTheLineSince[creature.Id] = acted;
                continue;
            }

            if (was == CreatureMode.Downed && now != CreatureMode.Waiting)
            {
                tally.DownedGotUpWithoutBeingRaised++;
                tally.DownedGotUpInto[now] = tally.DownedGotUpInto.GetValueOrDefault(now) + 1;
                tally.FirstDownedGotUp ??=
                    $"t{acted} creature#{creature.Id} Downed->{now} ({creature.LastDecision.ReasonCode}@t{creature.LastDecision.Tick}, hp={creature.Hp}, injury={creature.Injury})";
            }

            if (was == CreatureMode.Fled && now is not (CreatureMode.Waiting or CreatureMode.Downed))
            {
                tally.FledStoppedFleeing++;
                tally.FledStoppedInto[now] = tally.FledStoppedInto.GetValueOrDefault(now) + 1;
                tally.FirstFledStopped ??=
                    $"t{acted} creature#{creature.Id} Fled->{now} ({creature.LastDecision.ReasonCode}@t{creature.LastDecision.Tick})";
            }

            if (now == CreatureMode.Fighting &&
                tally.OutOfTheLineSince.TryGetValue(creature.Id, out var leftAt))
            {
                tally.BackInTheLineAfterASilentLeave++;
                tally.FirstBackInTheLine ??=
                    $"t{acted} creature#{creature.Id} back in the line, gap={acted - leftAt} ticks";
                tally.OutOfTheLineSince.Remove(creature.Id);
            }
        }
    }

    /// <summary>
    /// The same-tick half of the claim, read off the canonical event log: a
    /// `combat_joined` that stands alone on one tick, and the creature is not in
    /// the line at the end of that very tick.
    /// </summary>
    private static void ScanJoins(
        PrototypeSnapshot final,
        Dictionary<int, Dictionary<int, CreatureMode>> modeByTick,
        Measurement tally)
    {
        foreach (var entry in final.Events.Where(e => e.ReasonCode == "combat_joined" && e.Repeats == 1))
        {
            if (!modeByTick.TryGetValue(entry.FirstTick, out var modes) ||
                !modes.TryGetValue(entry.CreatureId, out var mode))
            {
                continue;
            }

            tally.JoinsSeen++;
            if (mode == CreatureMode.Fighting)
            {
                continue;
            }

            tally.JoinedAndOutInTheSameTick++;
            tally.JoinedIntoNothing[mode] = tally.JoinedIntoNothing.GetValueOrDefault(mode) + 1;
            tally.FirstJoinedAndOut ??=
                $"t{entry.FirstTick} creature#{entry.CreatureId} combat_joined, and ended the same tick as {mode}";
        }
    }

    private sealed class Measurement(string fixtureName, ulong seed)
    {
        public int Ticks { get; set; }

        public int StrikesOnARaiderAlreadyOut { get; set; }

        public string? FirstStrikeOnARaiderAlreadyOut { get; set; }

        public int LeftTheLineWithoutLeavingTheFight { get; set; }

        public Dictionary<CreatureMode, int> LeftInto { get; } = [];

        public Dictionary<string, int> LeftBecause { get; } = [];

        public string? FirstSilentLeave { get; set; }

        public int BackInTheLineAfterASilentLeave { get; set; }

        public string? FirstBackInTheLine { get; set; }

        public Dictionary<int, int> OutOfTheLineSince { get; } = [];

        public int DownedGotUpWithoutBeingRaised { get; set; }

        public Dictionary<CreatureMode, int> DownedGotUpInto { get; } = [];

        public string? FirstDownedGotUp { get; set; }

        public int FledStoppedFleeing { get; set; }

        public Dictionary<CreatureMode, int> FledStoppedInto { get; } = [];

        public string? FirstFledStopped { get; set; }

        public int JoinsSeen { get; set; }

        public int JoinedAndOutInTheSameTick { get; set; }

        public Dictionary<CreatureMode, int> JoinedIntoNothing { get; } = [];

        public string? FirstJoinedAndOut { get; set; }

        public int StepsThatMovedNoTick { get; set; }

        public int BlowsDrawnWhilePaused { get; set; }

        public int BlowsDrawnOnABodyWhilePaused { get; set; }

        public bool WindowOpen { get; set; }

        public List<string> Windows { get; } = [];

        public int FightingTicksWithNothingToFight { get; set; }

        public int RaiderBodyTicks { get; set; }

        public int MaxRaiderBodiesOnTheMap { get; set; }

        public int YieldsBookedByAFighter { get; set; }

        public override string ToString()
        {
            static string Map<T>(Dictionary<T, int> value)
                where T : notnull =>
                value.Count == 0
                    ? "-"
                    : string.Join(',', value.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}={pair.Value}"));

            return string.Join(Environment.NewLine,
                $"{fixtureName}/{seed} ticks={Ticks}",
                $"  strikesOnARaiderAlreadyOut={StrikesOnARaiderAlreadyOut} first={FirstStrikeOnARaiderAlreadyOut ?? "-"}",
                $"  leftTheLineSilently={LeftTheLineWithoutLeavingTheFight} into=[{Map(LeftInto)}] because=[{Map(LeftBecause)}]",
                $"    first={FirstSilentLeave ?? "-"}",
                $"  backInTheLineAfterASilentLeave={BackInTheLineAfterASilentLeave} first={FirstBackInTheLine ?? "-"}",
                $"  downedGotUpWithoutBeingRaised={DownedGotUpWithoutBeingRaised} into=[{Map(DownedGotUpInto)}]",
                $"    first={FirstDownedGotUp ?? "-"}",
                $"  fledStoppedFleeing={FledStoppedFleeing} into=[{Map(FledStoppedInto)}] first={FirstFledStopped ?? "-"}",
                $"  joinsSeen={JoinsSeen} joinedAndOutInTheSameTick={JoinedAndOutInTheSameTick} into=[{Map(JoinedIntoNothing)}]",
                $"    first={FirstJoinedAndOut ?? "-"}",
                $"  stepsThatMovedNoTick={StepsThatMovedNoTick} blowsDrawnWhilePaused={BlowsDrawnWhilePaused} ofThemOnABody={BlowsDrawnOnABodyWhilePaused}",
                $"    windowsOpened=[{(Windows.Count == 0 ? "-" : string.Join(' ', Windows))}] (tick:blowsOnBodies/blowsDrawn on the frame that opens it)",
                $"  fightingTicksWithNothingToFight={FightingTicksWithNothingToFight} raiderBodyTicks={RaiderBodyTicks} maxRaiderBodiesOnTheMap={MaxRaiderBodiesOnTheMap} yieldsBookedByAFighter={YieldsBookedByAFighter}");
        }
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
