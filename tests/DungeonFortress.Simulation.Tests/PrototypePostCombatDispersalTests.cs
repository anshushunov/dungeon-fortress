using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// What happens to a group of defenders in the ticks after the fight it was in
/// has ended, measured rather than described.
///
/// Issue #186. The owner watched a party on 2026-08-02 and said: «Когда все
/// сгрудились после боя — потом не могут некоторое время разойтись и блокируют
/// друг друга». Nobody had taken the number that sentence corresponds to, so the
/// first thing this file does is name one.
///
/// <para><b>The quantity.</b> A wave ends on the tick <c>PrototypeWaveSnapshot.EndTick</c>
/// records; on that tick <c>ResolveWave</c> turns everybody still in
/// <see cref="CreatureMode.Fighting"/> back into <see cref="CreatureMode.Waiting"/>.
/// The **cohort** is exactly that set, read from the snapshot of the tick before,
/// which is the last tick on which the fight still existed. The **window** is the
/// <see cref="DispersalWindow"/> ticks that follow. Inside it:</para>
///
/// <list type="bullet">
/// <item><description><c>clinchTicks</c> — ticks of the window on which at least
/// two members of the cohort were refused a step
/// (<c>waiting_blocked_by_other</c>). Two, because one creature waiting for one
/// tile is ordinary traffic; two or more at once is the group blocking
/// itself.</description></item>
/// <item><description><c>maxBlockedTogether</c> — the most cohort members
/// refused a step on one tick. This is "сколько существ" in the owner's
/// sentence.</description></item>
/// <item><description><c>dispersalDelay</c> — per cohort member, the ticks
/// between the end of the wave and the first tick on which it stands somewhere
/// else. The maximum over the cohort is how long the last of them took to get
/// out.</description></item>
/// </list>
///
/// <para><b>What it is compared against.</b> The same cohort, the same number of
/// ticks, taken immediately **before** the wave arrived — a stretch of the same
/// party with the same creatures doing ordinary work in the same dungeon. That
/// is what makes "толчея" a claim rather than an impression: the post-combat
/// window is only interesting if it differs from the peacetime one, and the ratio
/// is reported instead of asserted because it is a property of six runs (13.4).</para>
///
/// <para><b>Where the numbers come from.</b> Blocked steps are read from the
/// canonical event log by <c>LastTick</c>, the way
/// <see cref="PrototypeTrafficTests"/> reads them: a repeated identical decision
/// coalesces into one entry whose <c>LastTick</c> advances every tick it repeats,
/// so scanning for <c>LastTick == acted</c> sees it on each of those ticks.
/// Positions and modes are read from the snapshot, which does not coalesce.</para>
///
/// <para><b>Why a detour is counted.</b> Three mechanisms could produce the
/// clinch, and only one of them can be told from the others by a number that the
/// snapshot already publishes. <c>PrototypeMap.NextStep</c> is a BFS that does
/// not see bodies, so a creature whose next tile is taken waits instead of
/// walking around. For every refused step this file therefore asks whether a
/// route to the same destination existed with every other creature treated as a
/// wall. If it did, the tick was spent because the pathfinder is blind, which is
/// <see href="https://github.com/anshushunov/dungeon-fortress/issues/76">Issue #76</see>;
/// if it did not, the creature was walled in by bodies and only the yield
/// arbitration could have freed it.</para>
/// </summary>
public sealed class PrototypePostCombatDispersalTests(ITestOutputHelper output)
{
    /// <summary>
    /// How long after the end of a wave the group is watched. Sixty ticks: the
    /// dungeon is 28 tiles wide, so this is long enough to walk its length twice
    /// over; a harvest is 12 ticks and a cook batch 24, so it covers several
    /// whole pieces of work; and it is a sixth of
    /// <see cref="PrototypeTuning.WaveIntervalTicks"/>, so it never reaches into
    /// the next fight.
    /// </summary>
    private const int DispersalWindow = 60;

    /// <summary>
    /// How much longer a detour may be than the blind route before it stops
    /// counting as one. A creature that could reach its destination by walking
    /// four extra tiles lost the tick to the pathfinder; one that would have to
    /// cross the dungeon did not.
    /// </summary>
    private const int DetourSlack = 4;

    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    private static readonly string[] Fixtures = ["baseline", "prepared"];

    /// <summary>
    /// The numbers themselves, printed rather than asserted, one line per wave
    /// and one summary line per party. This is the measurement Issue #186 asks
    /// for in its first two criteria.
    /// </summary>
    [Fact]
    public void Report_post_combat_dispersal_over_the_seed_matrix()
    {
        var report = new StringBuilder();
        var diagnostics = new MatrixDiagnostics();
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                var party = Measure(fixtureName, seed, diagnostics);
                report.AppendLine(CultureInfo.InvariantCulture, $"{party}");
                foreach (var wave in party.Waves)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"    {wave}");
                }
            }
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"MATRIX {Summary(Matrix)}");
        report.Append(diagnostics);
        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// The whole point of the measurement, asserted so that it cannot quietly
    /// stop being taken: the matrix has to contain post-combat windows at all,
    /// and they have to contain refused steps. A layout or balance change that
    /// stopped producing either would leave every number above zero for the
    /// wrong reason and every conclusion drawn from them unfounded.
    /// </summary>
    [Fact]
    public void The_matrix_still_produces_post_combat_windows_to_measure()
    {
        var windows = Matrix.Sum(party => party.Measured.Count);
        var blocked = Matrix.Sum(party => party.Measured.Sum(wave => wave.BlockedCreatureTicks));

        Assert.True(
            windows >= 6,
            $"Only {windows} wave(s) over the matrix ended while the party was still alive, " +
            "which is too few for the shape of the ticks after a fight to have been sampled at " +
            $"all.{Environment.NewLine}{Detail()}");
        Assert.True(
            blocked >= 100,
            $"Only {blocked} refused step(s) by ex-combatants over all post-combat windows of " +
            "the matrix. The clinch this file exists to measure is not in the sample." +
            $"{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// Criterion 3 of Issue #186 as a check rather than a paragraph: of the ticks
    /// an ex-combatant spends refusing a step after the fight, the share on which
    /// a route to the same destination existed with every other body treated as a
    /// wall.
    ///
    /// This is the number that separates the three candidate mechanisms. A high
    /// share means the group is not walled in at all — the tiles to walk around
    /// by are there and <c>PrototypeMap.NextStep</c> cannot see them, which is
    /// Issue #76 and not something the yield arbitration was ever going to fix. A
    /// low share would mean the opposite: bodies really do enclose the creature
    /// and only a yield could open the way out.
    ///
    /// It is asserted as a floor and not as a corridor because the conclusion
    /// drawn from it in the pull request of #186 is one-sided: the mechanism is
    /// named as the pathfinder. If this share ever falls below a half the naming
    /// stops being true and the reader has to be told, which is what the failure
    /// message says.
    /// </summary>
    [Fact]
    public void A_refused_step_after_a_fight_usually_had_a_way_round_it()
    {
        var blocked = Matrix.Sum(party => party.Measured.Sum(wave => wave.BlockedWithDestination));
        var detoured = Matrix.Sum(party => party.Measured.Sum(wave => wave.BlockedWithAShortDetour));
        var share = (double)detoured / blocked;

        Assert.True(
            share >= 0.5,
            $"On {detoured} of {blocked} refused steps after a fight ({share:P1}) a route to the " +
            $"same destination existed with at most {DetourSlack} extra steps once every other " +
            "creature was treated as a wall. Below a half the clinch is bodies enclosing bodies " +
            "rather than a pathfinder that cannot see them, and the mechanism named in Issue #186 " +
            $"is the wrong one.{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// The six parties, measured once and shared: a party takes about a second to
    /// walk and three assertions over the same six would otherwise pay for it
    /// three times.
    /// </summary>
    private static IReadOnlyList<PartyMeasurement> Matrix => MatrixMeasurements.Value;

    private static readonly Lazy<IReadOnlyList<PartyMeasurement>> MatrixMeasurements =
        new(() =>
        [
            .. Fixtures.SelectMany(
                _ => MatrixSeeds,
                (fixtureName, seed) => Measure(fixtureName, seed)),
        ]);

    private static string Detail() =>
        string.Join(
            Environment.NewLine,
            Matrix.SelectMany(party => party.Measured.Select(wave => $"{party.Fixture}/{party.Seed} {wave}")));

    internal static string Summary(IReadOnlyList<PartyMeasurement> matrix)
    {
        var waves = matrix.SelectMany(party => party.Measured).ToArray();
        if (waves.Length == 0)
        {
            return "windows=0";
        }

        var clinch = waves.Sum(wave => wave.ClinchTicks);
        var background = waves.Sum(wave => wave.BackgroundClinchTicks);
        var blocked = waves.Sum(wave => wave.BlockedCreatureTicks);
        var backgroundBlocked = waves.Sum(wave => wave.BackgroundBlockedCreatureTicks);
        var withDestination = waves.Sum(wave => wave.BlockedWithDestination);
        var detoured = waves.Sum(wave => wave.BlockedWithAShortDetour);
        var walledIn = waves.Sum(wave => wave.BlockedWithNoRouteAtAll);
        var noYield = waves.Sum(wave => wave.BlockedWithNoYieldOnTheTick);
        var shared = waves.Sum(wave => wave.BlockedSharingADestination);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"windows={waves.Length} clinchTicks={clinch} backgroundClinchTicks={background} " +
            $"clinchRatio={Ratio(clinch, background):F2} " +
            $"blocked={blocked} backgroundBlocked={backgroundBlocked} " +
            $"blockedRatio={Ratio(blocked, backgroundBlocked):F2} " +
            $"maxBlockedTogether={waves.Max(wave => wave.MaxBlockedTogether)} " +
            $"maxDispersalDelay={waves.Max(wave => wave.MaxDispersalDelay)} " +
            $"blockedWithDestination={withDestination} shortDetour={detoured} " +
            $"detourShare={(withDestination == 0 ? 0 : (double)detoured / withDestination):F3} " +
            $"walledIn={walledIn} noYieldOnTheTick={noYield} sharingADestination={shared} " +
            $"mealReserved={waves.Sum(wave => wave.BlockedWhileMealReserved)} " +
            $"toLarder={waves.Sum(wave => wave.BlockedTowardsTheLarder)} " +
            $"nextTileFree={waves.Sum(wave => wave.BlockedWithTheNextTileFree)} " +
            $"byCannotAct={waves.Sum(wave => wave.BlockedByACreatureThatCannotAct)} " +
            $"byUrgent={waves.Sum(wave => wave.BlockedByAnUrgentCreature)} " +
            $"byArrived={waves.Sum(wave => wave.BlockedByACreatureAtItsDestination)} " +
            $"byEligible={waves.Sum(wave => wave.BlockedByACreatureEligibleToYield)} " +
            $"standingStill={waves.Sum(wave => wave.StandingStillCreatureTicks)} " +
            $"idleAndStill={waves.Sum(wave => wave.IdleAndStillCreatureTicks)} " +
            $"cohortTicks={waves.Sum(wave => wave.CohortSize * wave.ObservedTicks)}");
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? double.PositiveInfinity : (double)numerator / denominator;

    /// <summary>
    /// One party, walked tick by tick. Everything except the peacetime control
    /// window is measured online; the control window needs the refused steps of
    /// ticks that have already gone by, so those are kept per tick and nothing
    /// else is.
    /// </summary>
    internal static PartyMeasurement Measure(
        string fixtureName,
        ulong seed,
        MatrixDiagnostics? diagnostics = null)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var blockedPerTick = new Dictionary<int, List<int>>();
        var waves = new List<WaveMeasurement>();
        var open = new List<WaveWindow>();
        var closed = new List<WaveWindow>();
        var previous = world.GetSnapshot();
        var last = previous;

        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            last = current;
            var acted = current.Tick - 1;

            var blockedNow = new List<int>();
            var blockedTargets = new Dictionary<int, GridPoint>();
            var yieldedNow = new HashSet<int>();
            foreach (var @event in current.Events)
            {
                if (@event.LastTick != acted)
                {
                    continue;
                }

                switch (@event.ReasonCode)
                {
                    case "waiting_blocked_by_other" when !blockedNow.Contains(@event.CreatureId):
                        blockedNow.Add(@event.CreatureId);
                        if (@event.Target is { } target)
                        {
                            blockedTargets[@event.CreatureId] = target;
                        }

                        break;
                    case "chosen_traffic_yield":
                        yieldedNow.Add(@event.CreatureId);
                        break;
                }
            }

            blockedPerTick[acted] = blockedNow;

            // A wave that resolved on this tick opens a window. Its cohort is
            // read from the previous snapshot, because ResolveWave has already
            // turned everybody in it back into Waiting by the time this one is
            // taken.
            foreach (var wave in current.Waves)
            {
                if (wave.EndTick != acted)
                {
                    continue;
                }

                var cohort = previous.Creatures
                    .Where(creature => creature.Mode == CreatureMode.Fighting)
                    .Select(creature => creature.Id)
                    .ToArray();
                if (cohort.Length == 0)
                {
                    continue;
                }

                open.Add(new WaveWindow(
                    wave.Number,
                    wave.ArriveTick,
                    acted,
                    wave.Outcome ?? "unresolved",
                    cohort,
                    current.Creatures
                        .Where(creature => cohort.Contains(creature.Id))
                        .ToDictionary(creature => creature.Id, creature => creature.Position),
                    Background(blockedPerTick, cohort, wave.ArriveTick)));
            }

            foreach (var window in open)
            {
                if (acted <= window.EndTick)
                {
                    continue;
                }

                window.Observe(current, blockedNow, blockedTargets, yieldedNow, acted, diagnostics);
            }

            foreach (var window in open.Where(item => acted >= item.EndTick + DispersalWindow).ToArray())
            {
                closed.Add(window);
                open.Remove(window);
            }

            previous = current;
        }

        foreach (var window in open)
        {
            closed.Add(window);
        }

        foreach (var window in closed.OrderBy(item => item.Number))
        {
            waves.Add(window.ToMeasurement());
        }

        return new PartyMeasurement(fixtureName, seed, last.Tick, waves);
    }

    /// <summary>
    /// The peacetime control: the same creatures over the same number of ticks,
    /// ending on the tick before the wave walked in.
    /// </summary>
    private static (int ClinchTicks, int BlockedCreatureTicks) Background(
        IReadOnlyDictionary<int, List<int>> blockedPerTick,
        IReadOnlyCollection<int> cohort,
        int arriveTick)
    {
        var clinch = 0;
        var blocked = 0;
        for (var tick = arriveTick - DispersalWindow; tick < arriveTick; tick++)
        {
            if (!blockedPerTick.TryGetValue(tick, out var blockedThen))
            {
                continue;
            }

            var count = blockedThen.Count(cohort.Contains);
            blocked += count;
            if (count >= 2)
            {
                clinch++;
            }
        }

        return (clinch, blocked);
    }

    /// <summary>
    /// One post-combat window, accumulating while the party keeps running.
    /// </summary>
    private sealed class WaveWindow(
        int number,
        int arriveTick,
        int endTick,
        string outcome,
        IReadOnlyList<int> cohort,
        IReadOnlyDictionary<int, GridPoint> positionsAtEnd,
        (int ClinchTicks, int BlockedCreatureTicks) background)
    {
        private readonly Dictionary<int, int> _firstMove = [];
        private int _observedTicks;
        private int _clinchTicks;
        private int _blocked;
        private int _maxTogether;
        private int _withDestination;
        private int _shortDetour;
        private int _noRoute;
        private int _noYield;
        private int _sharing;
        private int _yieldsToCohort;
        private int _mealReserved;
        private int _towardsTheLarder;
        private int _nextStepFree;
        private int _blockerCannotAct;
        private int _blockerUrgent;
        private int _blockerAtItsDestination;
        private int _blockerEligibleToYield;
        private int _standingStill;
        private int _idleAndStill;

        public int Number => number;

        public int EndTick => endTick;

        public void Observe(
            PrototypeSnapshot snapshot,
            IReadOnlyList<int> blockedNow,
            IReadOnlyDictionary<int, GridPoint> blockedTargets,
            IReadOnlySet<int> yieldedNow,
            int acted,
            MatrixDiagnostics? diagnostics)
        {
            _observedTicks++;
            var blockedCohort = blockedNow.Where(cohort.Contains).ToArray();
            _blocked += blockedCohort.Length;
            _maxTogether = Math.Max(_maxTogether, blockedCohort.Length);
            if (blockedCohort.Length >= 2)
            {
                _clinchTicks++;
            }

            _yieldsToCohort += cohort.Count(yieldedNow.Contains);

            foreach (var id in cohort)
            {
                var creature = snapshot.Creatures.FirstOrDefault(item => item.Id == id);
                if (creature is null)
                {
                    continue;
                }

                if (creature.LastMoveTick != acted && !blockedCohort.Contains(id))
                {
                    // Neither walked nor was refused a step: the creature is
                    // standing where it is because nothing asked it to move. That
                    // is a different picture from a clinch and has to be counted
                    // apart from one — and split again, because a creature
                    // standing on a mushroom bed harvesting it has dispersed and
                    // one standing in the middle of the fight it just left has
                    // not.
                    _standingStill++;
                    if (creature.Mode == CreatureMode.Waiting)
                    {
                        _idleAndStill++;
                    }

                    diagnostics?.CountStandingStill(creature);
                }

                if (!_firstMove.ContainsKey(id) && creature.Position != positionsAtEnd[id])
                {
                    _firstMove[id] = acted - endTick;
                }
            }

            if (blockedCohort.Length == 0)
            {
                return;
            }

            var occupants = snapshot.Creatures.ToDictionary(creature => creature.Position);
            var bodies = occupants.Keys.ToHashSet();
            var walls = new HashSet<GridPoint>(snapshot.Map.RockTiles);
            walls.UnionWith(snapshot.Zones[ZoneKind.Forbidden]);
            var larder = PrototypeLayout.Read('L').ToHashSet();
            foreach (var id in blockedCohort)
            {
                if (!blockedTargets.TryGetValue(id, out var destination))
                {
                    continue;
                }

                var creature = snapshot.Creatures.First(item => item.Id == id);
                _withDestination++;
                if (yieldedNow.Count == 0)
                {
                    _noYield++;
                }

                if (creature.MealReserved)
                {
                    _mealReserved++;
                }

                if (larder.Contains(destination))
                {
                    _towardsTheLarder++;
                }

                if (blockedCohort.Any(other => other != id &&
                        blockedTargets.TryGetValue(other, out var otherTarget) &&
                        otherTarget == destination))
                {
                    _sharing++;
                }

                diagnostics?.CountBlocked(creature.Position, destination);

                // Who is standing on the tile the refused step was for. The step
                // is recomputed by the simulation's own rule — the same BFS over
                // the same map with the same (north, east, south, west) tie-break
                // — because the snapshot publishes the destination of a refused
                // step but not the tile it was refused onto.
                var next = NextStep(creature.Position, destination, walls);
                if (next is not { } step || step == creature.Position)
                {
                    _nextStepFree++;
                }
                else if (!occupants.TryGetValue(step, out var blocker))
                {
                    // Nobody is standing there at the end of the tick, so the step
                    // was refused by the arbitration itself: the creature lost the
                    // contest for the tile or the tile was booked for a yield.
                    _nextStepFree++;
                }
                else if (blocker.Mode is CreatureMode.Fighting or CreatureMode.Downed)
                {
                    _blockerCannotAct++;
                }
                else if (blocker.MealReserved || blocker.IsMustering)
                {
                    _blockerUrgent++;
                }
                else if (Destination(snapshot, blocker) is not { } blockerTarget ||
                         blockerTarget == blocker.Position)
                {
                    _blockerAtItsDestination++;
                }
                else
                {
                    _blockerEligibleToYield++;
                }

                var blind = Distance(creature.Position, destination, walls, bodies: null);
                var seeing = Distance(creature.Position, destination, walls, bodies);
                if (seeing is null)
                {
                    _noRoute++;
                    continue;
                }

                if (blind is null || seeing.Value <= blind.Value + DetourSlack)
                {
                    _shortDetour++;
                }
            }
        }

        public WaveMeasurement ToMeasurement()
        {
            var delays = cohort
                .Select(id => _firstMove.TryGetValue(id, out var delay) ? delay : DispersalWindow)
                .ToArray();
            return new WaveMeasurement(
                number,
                arriveTick,
                endTick,
                outcome,
                cohort.Count,
                _observedTicks,
                _clinchTicks,
                _blocked,
                _maxTogether,
                background.ClinchTicks,
                background.BlockedCreatureTicks,
                delays.Length == 0 ? 0 : delays.Max(),
                delays.Length == 0 ? 0 : delays.Sum() / delays.Length,
                cohort.Count(id => !_firstMove.ContainsKey(id)),
                _withDestination,
                _shortDetour,
                _noRoute,
                _noYield,
                _sharing,
                _yieldsToCohort,
                _mealReserved,
                _towardsTheLarder,
                _nextStepFree,
                _blockerCannotAct,
                _blockerUrgent,
                _blockerAtItsDestination,
                _blockerEligibleToYield,
                _standingStill,
                _idleAndStill);
        }
    }

    /// <summary>
    /// Where a creature is trying to get to, as
    /// <c>PrototypeWorld.PrimaryDestination</c> answers it, rebuilt from the
    /// snapshot. A creature that fled is out of scope here: no wave is on the map
    /// inside a post-combat window, so nobody is in that mode.
    /// </summary>
    private static GridPoint? Destination(
        PrototypeSnapshot snapshot,
        PrototypeCreatureSnapshot creature)
    {
        if (creature.IsMustering)
        {
            return creature.MusterNeedsRation ? creature.MealTarget : creature.MusterTarget;
        }

        if (creature.MealReserved)
        {
            return creature.MealTarget;
        }

        return creature.CurrentJobId is { } jobId
            ? snapshot.Jobs.FirstOrDefault(job => job.JobId == jobId)?.Target
            : null;
    }

    /// <summary>
    /// The step <c>PrototypeMap.NextStep</c> would return: the same BFS over the
    /// same passable tiles, visiting neighbours in the same order and returning
    /// the first tile of the first shortest path found. It is restated rather
    /// than exported because a measurement that asks the code what it did cannot
    /// disagree with it, and this one has to be able to.
    /// </summary>
    private static GridPoint? NextStep(GridPoint start, GridPoint target, IReadOnlySet<GridPoint> walls)
    {
        if (start == target)
        {
            return start;
        }

        var previous = new Dictionary<GridPoint, GridPoint>();
        var visited = new HashSet<GridPoint> { start };
        var queue = new Queue<GridPoint>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out var current))
        {
            foreach (var next in Neighbors(current))
            {
                if (!InBounds(next) ||
                    walls.Contains(next) ||
                    !visited.Add(next))
                {
                    continue;
                }

                previous[next] = current;
                if (next == target)
                {
                    var step = target;
                    while (previous.TryGetValue(step, out var predecessor) && predecessor != start)
                    {
                        step = predecessor;
                    }

                    return step;
                }

                queue.Enqueue(next);
            }
        }

        return null;
    }

    /// <summary>
    /// What the whole matrix looked like, kept apart from the per-wave counters
    /// because it answers "where" rather than "how much".
    /// </summary>
    internal sealed class MatrixDiagnostics
    {
        private readonly Dictionary<GridPoint, int> _blockedAt = [];
        private readonly Dictionary<GridPoint, int> _headingFor = [];
        private readonly Dictionary<string, int> _standingStillReasons = [];
        private readonly Dictionary<CreatureMode, int> _standingStillModes = [];

        public void CountBlocked(GridPoint standing, GridPoint destination)
        {
            _blockedAt[standing] = _blockedAt.GetValueOrDefault(standing) + 1;
            _headingFor[destination] = _headingFor.GetValueOrDefault(destination) + 1;
        }

        public void CountStandingStill(PrototypeCreatureSnapshot creature)
        {
            var reason = creature.LastDecision.ReasonCode;
            _standingStillReasons[reason] = _standingStillReasons.GetValueOrDefault(reason) + 1;
            _standingStillModes[creature.Mode] = _standingStillModes.GetValueOrDefault(creature.Mode) + 1;
        }

        public override string ToString()
        {
            var report = new StringBuilder();
            report.AppendLine("tiles an ex-combatant was refused a step on, over the whole matrix:");
            foreach (var tile in _blockedAt.OrderByDescending(pair => pair.Value).Take(10))
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"  ({tile.Key.X},{tile.Key.Y}) {tile.Value}");
            }

            report.AppendLine("destinations of those refused steps:");
            foreach (var tile in _headingFor.OrderByDescending(pair => pair.Value).Take(10))
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"  ({tile.Key.X},{tile.Key.Y}) {tile.Value}");
            }

            report.AppendLine("what an ex-combatant that neither walked nor was refused was doing:");
            foreach (var mode in _standingStillModes.OrderByDescending(pair => pair.Value))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {mode.Key} {mode.Value}");
            }

            foreach (var reason in _standingStillReasons.OrderByDescending(pair => pair.Value).Take(10))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {reason.Key} {reason.Value}");
            }

            return report.ToString();
        }
    }

    /// <summary>
    /// Distance by the map, optionally with every creature of the domain treated
    /// as a wall. With <paramref name="bodies"/> null this is what
    /// <c>PrototypeMap.NextStep</c> sees; with it, it is what the same route
    /// would cost if the pathfinder could see who is standing in it.
    /// </summary>
    private static int? Distance(
        GridPoint start,
        GridPoint target,
        IReadOnlySet<GridPoint> walls,
        IReadOnlySet<GridPoint>? bodies)
    {
        if (start == target)
        {
            return 0;
        }

        var distances = new Dictionary<GridPoint, int> { [start] = 0 };
        var queue = new Queue<GridPoint>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out var current))
        {
            var next = distances[current] + 1;
            foreach (var neighbor in Neighbors(current))
            {
                if (!InBounds(neighbor) ||
                    walls.Contains(neighbor) ||
                    distances.ContainsKey(neighbor))
                {
                    continue;
                }

                if (neighbor == target)
                {
                    return next;
                }

                if (bodies is not null && bodies.Contains(neighbor))
                {
                    continue;
                }

                distances[neighbor] = next;
                queue.Enqueue(neighbor);
            }
        }

        return null;
    }

    private static IEnumerable<GridPoint> Neighbors(GridPoint point)
    {
        yield return new GridPoint(point.X, point.Y - 1);
        yield return new GridPoint(point.X + 1, point.Y);
        yield return new GridPoint(point.X, point.Y + 1);
        yield return new GridPoint(point.X - 1, point.Y);
    }

    private static bool InBounds(GridPoint point) =>
        point.X >= 0 && point.X < PrototypeTuning.MapWidth &&
        point.Y >= 0 && point.Y < PrototypeTuning.MapHeight;

    internal sealed record PartyMeasurement(
        string Fixture,
        ulong Seed,
        int Ticks,
        IReadOnlyList<WaveMeasurement> Waves)
    {
        /// <summary>
        /// The windows that were actually watched. A wave whose end tick is also
        /// the tick the party ended on opens a window of zero ticks: it is
        /// printed, because "the party did not outlive the wave" is a fact, and
        /// it is left out of every aggregate, because a window nobody looked
        /// through cannot say how long a group took to disperse.
        /// </summary>
        public IReadOnlyList<WaveMeasurement> Measured =>
            [.. Waves.Where(wave => wave.ObservedTicks > 0)];

        public override string ToString() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Fixture}/{Seed} ticks={Ticks} windows={Waves.Count} measured={Measured.Count} " +
                $"clinchTicks={Measured.Sum(wave => wave.ClinchTicks)} " +
                $"backgroundClinchTicks={Measured.Sum(wave => wave.BackgroundClinchTicks)} " +
                $"blocked={Measured.Sum(wave => wave.BlockedCreatureTicks)} " +
                $"backgroundBlocked={Measured.Sum(wave => wave.BackgroundBlockedCreatureTicks)}");
    }

    internal sealed record WaveMeasurement(
        int Number,
        int ArriveTick,
        int EndTick,
        string Outcome,
        int CohortSize,
        int ObservedTicks,
        int ClinchTicks,
        int BlockedCreatureTicks,
        int MaxBlockedTogether,
        int BackgroundClinchTicks,
        int BackgroundBlockedCreatureTicks,
        int MaxDispersalDelay,
        int MeanDispersalDelay,
        int NeverMovedInTheWindow,
        int BlockedWithDestination,
        int BlockedWithAShortDetour,
        int BlockedWithNoRouteAtAll,
        int BlockedWithNoYieldOnTheTick,
        int BlockedSharingADestination,
        int YieldsToTheCohort,
        int BlockedWhileMealReserved,
        int BlockedTowardsTheLarder,
        int BlockedWithTheNextTileFree,
        int BlockedByACreatureThatCannotAct,
        int BlockedByAnUrgentCreature,
        int BlockedByACreatureAtItsDestination,
        int BlockedByACreatureEligibleToYield,
        int StandingStillCreatureTicks,
        int IdleAndStillCreatureTicks)
    {
        public override string ToString() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"wave={Number} arrive={ArriveTick} end={EndTick} outcome={Outcome} " +
                $"cohort={CohortSize} observed={ObservedTicks} " +
                $"clinchTicks={ClinchTicks} background={BackgroundClinchTicks} " +
                $"blocked={BlockedCreatureTicks} backgroundBlocked={BackgroundBlockedCreatureTicks} " +
                $"maxTogether={MaxBlockedTogether} " +
                $"dispersalMax={MaxDispersalDelay} dispersalMean={MeanDispersalDelay} " +
                $"neverMoved={NeverMovedInTheWindow} standingStill={StandingStillCreatureTicks} " +
                $"idleAndStill={IdleAndStillCreatureTicks} " +
                $"withDestination={BlockedWithDestination} shortDetour={BlockedWithAShortDetour} " +
                $"walledIn={BlockedWithNoRouteAtAll} noYield={BlockedWithNoYieldOnTheTick} " +
                $"sharing={BlockedSharingADestination} yieldsToCohort={YieldsToTheCohort} " +
                $"mealReserved={BlockedWhileMealReserved} toLarder={BlockedTowardsTheLarder} " +
                $"nextTileFree={BlockedWithTheNextTileFree} " +
                $"byCannotAct={BlockedByACreatureThatCannotAct} " +
                $"byUrgent={BlockedByAnUrgentCreature} " +
                $"byArrived={BlockedByACreatureAtItsDestination} " +
                $"byEligible={BlockedByACreatureEligibleToYield}");
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
