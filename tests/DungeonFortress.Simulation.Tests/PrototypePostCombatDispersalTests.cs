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
/// ticks, taken before the wave arrived **and before the domain stops working for
/// it** — see <see cref="Background"/> for why the second half of that sentence
/// is load-bearing. That is what makes "толчея" a claim rather than an
/// impression: the post-combat window is only interesting if it differs from the
/// peacetime one, and the ratio is reported instead of asserted because it is a
/// property of six runs (13.4).</para>
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
    /// The control window has to end before the domain stops working, and this is
    /// the check that says so.
    ///
    /// The first version of this file compared the ticks after a fight against
    /// the <see cref="DispersalWindow"/> ticks ending at the wave's arrival. On
    /// <c>prepared</c> that interval is the muster to the tile — its journal sets
    /// <c>muster_lead_ticks</c> to 60 on tick 880 and the window is 60 — so the
    /// "peacetime" half of the comparison was the one moment of the party when
    /// every creature drops its work and walks into a Watch zone of ten tiles.
    /// It carried 532 of the 737 refused steps of that control and reversed the
    /// answer. Independent review of Issue #186 found it by measurement, and this
    /// check is what keeps it found: the control now ends where
    /// <c>PrototypeWorld.IsMusterActive</c> starts, and losing that subtraction
    /// reddens here rather than quietly returning the wrong ratio.
    ///
    /// The second assertion is the sample: a matrix in which nobody ever musters
    /// would satisfy the first one without exercising it at all.
    /// </summary>
    [Fact]
    public void The_control_window_ends_before_the_domain_stops_working()
    {
        foreach (var party in Matrix)
        {
            foreach (var wave in party.Measured)
            {
                Assert.True(
                    wave.ControlWindowEndTick <= wave.ArriveTick - wave.MusterLeadTicks,
                    $"{party.Fixture}/{party.Seed} wave {wave.Number}: the control window ends on " +
                    $"tick {wave.ControlWindowEndTick}, and the muster for that wave starts on " +
                    $"{wave.ArriveTick - wave.MusterLeadTicks}. The ticks a fight is being " +
                    "compared against are the ticks the whole domain spends walking into the " +
                    $"Watch zone, which is not peacetime.{Environment.NewLine}{Detail()}");
            }
        }

        Assert.True(
            Matrix.Any(party => party.Measured.Any(wave => wave.MusterLeadTicks > 0)),
            "No wave of the matrix has a muster lead at all, so the rule above was never " +
            $"exercised.{Environment.NewLine}{Detail()}");
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
        var longer = Matrix.Sum(
            party => party.Measured.Sum(wave => wave.BlockedWithAStrictlyLongerWayRound));
        var share = (double)detoured / blocked;

        Assert.True(
            share >= 0.5,
            $"On {detoured} of {blocked} refused steps after a fight ({share:P1}) a route to the " +
            $"same destination existed with at most {DetourSlack} extra steps once every other " +
            "creature was treated as a wall. Below a half the clinch is bodies enclosing bodies " +
            "rather than a pathfinder that cannot see them, and the mechanism named in Issue #186 " +
            $"is the wrong one.{Environment.NewLine}{Detail()}");

        // The second half of the same claim, and the one that keeps the first
        // from passing by accident. If the search for a way round did not treat
        // bodies as walls it would re-derive the route the simulation already
        // took: every "detour" would be exactly as long as the blind one and this
        // count would be zero — a hundred per cent detour share measuring nothing.
        Assert.True(
            longer * 5 >= blocked,
            $"Only {longer} of {blocked} ways round were strictly longer than the route the " +
            "simulation walks with its eyes shut, where a fifth is the floor. Below it the second " +
            "search is not seeing the bodies it is supposed to see, and the detour share above is " +
            $"measuring nothing.{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// The three candidate mechanisms of Issue #186, decided between by the one
    /// comparison that can decide between them: how many of the refused steps
    /// each could have prevented.
    ///
    /// Walking round reaches a refused step when a route to the same destination
    /// exists once bodies count as walls. A yield reaches one when the creature
    /// standing in the way is one <c>PrototypeWorld.CanYield</c> would allow to
    /// step aside **and** has an empty tile to step onto — anything less and the
    /// order cannot be given or cannot be obeyed. The approach rule of Issue #129
    /// is not in this comparison because it is not a way of clearing a refused
    /// step at all: it decides how many creatures end the fight in one place, and
    /// that is measured in <c>evidence/186-before-after-129.json</c> instead.
    ///
    /// <para><b>The two are compared on the refused steps a body was standing in
    /// the way of, and only on those.</b> A step refused with its next tile empty
    /// at the end of the tick was refused by the arbitration itself — the mover
    /// lost the contest for the tile, or the tile was booked for somebody else's
    /// yield — so no yield of anybody's could have cleared it, and putting it on
    /// the other side of the comparison compares unlike sets. The first version
    /// of this check did exactly that: a way round counted over all 782 refused
    /// steps against a yield counted only over the ones with a blocker, which
    /// flattered the first side by 89. Found by independent review of Issue #186,
    /// and the correction is why the floor below is one and a half rather than
    /// two.</para>
    /// </summary>
    [Fact]
    public void Walking_round_reaches_more_of_the_clinch_than_a_yield_could()
    {
        var detoured = Matrix.Sum(
            party => party.Measured.Sum(wave => wave.BlockedWithABodyInTheWayAndAWayRound));
        var yieldable = Matrix.Sum(
            party => party.Measured.Sum(wave => wave.BlockedAYieldCouldHaveCleared));

        Assert.True(
            detoured > 0 && detoured * 2 >= yieldable * 3,
            $"Of the refused steps a body stood in the way of, a way round reached {detoured} and " +
            $"a yield could have cleared {yieldable} — a ratio of " +
            $"{(yieldable == 0 ? double.PositiveInfinity : (double)detoured / yieldable):F2}, " +
            "where one and a half is the floor. Issue #186 named the pathfinder as the mechanism " +
            "on the strength of this ratio; below the floor the naming has to be taken again." +
            $"{Environment.NewLine}{Detail()}");
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
            $"byNoDestination={waves.Sum(wave => wave.BlockedByACreatureWithNoDestination)} " +
            $"byEligible={waves.Sum(wave => wave.BlockedByACreatureEligibleToYield)} " +
            $"standingStill={waves.Sum(wave => wave.StandingStillCreatureTicks)} " +
            $"idleAndStill={waves.Sum(wave => wave.IdleAndStillCreatureTicks)} " +
            $"cohortTicks={waves.Sum(wave => wave.CohortSize * wave.ObservedTicks)} " +
            $"longerWayRound={waves.Sum(wave => wave.BlockedWithAStrictlyLongerWayRound)} " +
            $"yieldCouldClear={waves.Sum(wave => wave.BlockedAYieldCouldHaveCleared)} " +
            $"detourWithABody={waves.Sum(wave => wave.BlockedWithABodyInTheWayAndAWayRound)}");
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
        var musterLeadPerTick = new Dictionary<int, int>();
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
            musterLeadPerTick[acted] = current.Rules.GetValueOrDefault("muster_lead_ticks");

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
                    Background(blockedPerTick, musterLeadPerTick, cohort, wave.ArriveTick)));
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
    /// ending where the domain stops working.
    ///
    /// The window is pushed back by <c>muster_lead_ticks</c>, and that is a
    /// correction rather than a nicety. <c>PrototypeWorld.IsMusterActive</c> is
    /// true from <c>arriveTick - muster_lead_ticks</c> onwards, and this control
    /// used to be the <see cref="DispersalWindow"/> ticks ending at
    /// <c>arriveTick</c>. On <c>prepared</c>, whose journal sets the lead to 60
    /// on tick 880, and with a window of exactly 60, the two intervals coincided
    /// tick for tick: the "peacetime" the fight was being compared against was
    /// the muster, when the whole domain drops its work and walks into a Watch
    /// zone of ten tiles. It carried 532 of the 737 refused steps of the old
    /// control and turned the answer round — 782 against 737 became 782 against
    /// 526. Found by independent review of Issue #186; the numbers are in the
    /// pull request and in <c>evidence/186-measure-now.json</c>.
    ///
    /// Only the lead is subtracted, and no more: <c>baseline</c> sets no lead, so
    /// its control does not move at all, and <c>prepared</c> is compared against
    /// the quiet stretch that ends exactly where its muster begins.
    /// </summary>
    private static (int ClinchTicks, int BlockedCreatureTicks, int End, int Lead) Background(
        IReadOnlyDictionary<int, List<int>> blockedPerTick,
        IReadOnlyDictionary<int, int> musterLeadPerTick,
        IReadOnlyCollection<int> cohort,
        int arriveTick)
    {
        var clinch = 0;
        var blocked = 0;
        var lead = musterLeadPerTick.GetValueOrDefault(arriveTick - 1);
        for (var tick = arriveTick - lead - DispersalWindow; tick < arriveTick - lead; tick++)
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

        return (clinch, blocked, arriveTick - lead, lead);
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
        (int ClinchTicks, int BlockedCreatureTicks, int End, int Lead) background)
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
        private int _blockerWithNoDestination;
        private int _shortDetourWithABodyInTheWay;
        private int _blockerEligibleToYield;
        private int _standingStill;
        private int _detourStrictlyLonger;
        private int _couldHaveBeenYielded;
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
                var nextTileWasFree = false;
                var next = NextStep(creature.Position, destination, walls);
                if (next is not { } step || step == creature.Position)
                {
                    _nextStepFree++;
                    nextTileWasFree = true;
                }
                else if (!occupants.TryGetValue(step, out var blocker))
                {
                    // Nobody is standing there at the end of the tick, so the step
                    // was refused by the arbitration itself: the creature lost the
                    // contest for the tile or the tile was booked for a yield.
                    _nextStepFree++;
                    nextTileWasFree = true;
                }
                else if (blocker.Mode is CreatureMode.Fighting or CreatureMode.Downed)
                {
                    _blockerCannotAct++;
                }
                else if (blocker.MealReserved || blocker.IsMustering)
                {
                    _blockerUrgent++;
                }
                else if (Destination(snapshot, blocker) is { } blockerTarget &&
                         blockerTarget == blocker.Position)
                {
                    _blockerAtItsDestination++;
                }
                else
                {
                    // Everything left may be told to step aside, and that
                    // includes a creature with no destination at all. The last
                    // clause of PrototypeWorld.CanYield is
                    // `PrimaryDestination(creature) != creature.Position`, and
                    // PrimaryDestination hands back a `GridPoint?` over a
                    // `readonly record struct`: for a creature with no job, no
                    // meal and no muster it is null, and `null != position` lifts
                    // to true. The first version of this harness read that clause
                    // as "no destination means it has arrived" and put 17 refused
                    // steps in the bucket a yield may not touch, which moved the
                    // reach of a yield down rather than up. Found by independent
                    // review of Issue #186 and counted here rather than corrected
                    // in silence.
                    if (Destination(snapshot, blocker) is null)
                    {
                        _blockerWithNoDestination++;
                    }
                    else
                    {
                        _blockerEligibleToYield++;
                    }

                    if (Neighbors(blocker.Position).Any(tile =>
                            InBounds(tile) && !walls.Contains(tile) && !bodies.Contains(tile)))
                    {
                        // The blocker may be told to step aside and has somewhere
                        // to step. This is the whole of what the yield
                        // arbitration could ever buy on this tick.
                        _couldHaveBeenYielded++;
                    }
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
                    if (!nextTileWasFree)
                    {
                        // The same count over the refused steps a yield can even
                        // be compared on: the ones where somebody was standing on
                        // the tile. A step refused with the tile empty was
                        // refused by the arbitration itself, and counting it as
                        // reachable by a way round while it belongs to nobody's
                        // yield compares unlike sets.
                        _shortDetourWithABodyInTheWay++;
                    }
                }

                if (blind is { } direct && seeing.Value > direct)
                {
                    // The route round is a different route and costs more than
                    // the blind one. Counting these apart is what keeps the
                    // detour number honest: if bodies were left out of the second
                    // search it would re-derive the first and this count would be
                    // zero.
                    _detourStrictlyLonger++;
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
                background.End,
                background.Lead,
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
                _blockerWithNoDestination,
                _blockerEligibleToYield,
                _standingStill,
                _idleAndStill,
                _detourStrictlyLonger,
                _couldHaveBeenYielded,
                _shortDetourWithABodyInTheWay);
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

        /// <summary>
        /// Modes are counted over everybody standing still; last decisions only
        /// over the ones in <see cref="CreatureMode.Waiting"/>, because that is
        /// the group the conclusion about them is drawn about. Mixing the two let
        /// a histogram over 1989 creature-ticks be read as an explanation of 1121
        /// of them — found by independent review of Issue #186.
        ///
        /// <c>LastDecision</c> is the last decision this creature took, not a
        /// decision it took on this tick: for a creature that decided nothing
        /// here the reason below is several ticks old. It is still the right
        /// thing to count — a creature that has stopped deciding is one whose
        /// last decision is still standing — but it reads the past, and that is
        /// named rather than left to be discovered.
        /// </summary>
        public void CountStandingStill(PrototypeCreatureSnapshot creature)
        {
            _standingStillModes[creature.Mode] = _standingStillModes.GetValueOrDefault(creature.Mode) + 1;
            if (creature.Mode != CreatureMode.Waiting)
            {
                return;
            }

            var reason = creature.LastDecision.ReasonCode;
            _standingStillReasons[reason] = _standingStillReasons.GetValueOrDefault(reason) + 1;
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

            report.AppendLine(
                "what an ex-combatant that neither walked nor was refused was doing (modes over " +
                "all of them, last decisions over the ones in Waiting only):");
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
        int ControlWindowEndTick,
        int MusterLeadTicks,
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
        int BlockedByACreatureWithNoDestination,
        int BlockedByACreatureEligibleToYield,
        int StandingStillCreatureTicks,
        int IdleAndStillCreatureTicks,
        int BlockedWithAStrictlyLongerWayRound,
        int BlockedAYieldCouldHaveCleared,
        int BlockedWithABodyInTheWayAndAWayRound)
    {
        public override string ToString() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"wave={Number} arrive={ArriveTick} end={EndTick} outcome={Outcome} " +
                $"cohort={CohortSize} observed={ObservedTicks} " +
                $"clinchTicks={ClinchTicks} background={BackgroundClinchTicks} " +
                $"blocked={BlockedCreatureTicks} backgroundBlocked={BackgroundBlockedCreatureTicks} " +
                $"controlEnds={ControlWindowEndTick} musterLead={MusterLeadTicks} " +
                $"maxTogether={MaxBlockedTogether} " +
                $"dispersalMax={MaxDispersalDelay} dispersalMean={MeanDispersalDelay} " +
                $"neverMoved={NeverMovedInTheWindow} standingStill={StandingStillCreatureTicks} " +
                $"idleAndStill={IdleAndStillCreatureTicks} " +
                $"longerWayRound={BlockedWithAStrictlyLongerWayRound} " +
                $"yieldCouldClear={BlockedAYieldCouldHaveCleared} " +
                $"detourWithABody={BlockedWithABodyInTheWayAndAWayRound} " +
                $"withDestination={BlockedWithDestination} shortDetour={BlockedWithAShortDetour} " +
                $"walledIn={BlockedWithNoRouteAtAll} noYield={BlockedWithNoYieldOnTheTick} " +
                $"sharing={BlockedSharingADestination} yieldsToCohort={YieldsToTheCohort} " +
                $"mealReserved={BlockedWhileMealReserved} toLarder={BlockedTowardsTheLarder} " +
                $"nextTileFree={BlockedWithTheNextTileFree} " +
                $"byCannotAct={BlockedByACreatureThatCannotAct} " +
                $"byUrgent={BlockedByAnUrgentCreature} " +
                $"byArrived={BlockedByACreatureAtItsDestination} " +
                $"byNoDestination={BlockedByACreatureWithNoDestination} " +
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
