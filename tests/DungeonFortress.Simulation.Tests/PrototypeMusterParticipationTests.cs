using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// How much of the domain actually takes the field when a wave lands, and who
/// does not — Issue #405, raised from the owner's playtest of 2026-08-12: «ещё
/// когда начинается атака не все бегут защищаться — возможно так и задумано, но
/// это новое».
///
/// <para>The sentence carries two claims and this probe is built to answer both
/// separately. <b>«Не все»</b> is a share, and it is measured here per wave, per
/// party, over the shipped seed matrix. <b>«Новое»</b> is a comparison, and the
/// same file compiles and runs on the tree before the balance slice
/// (<c>8977b0d^</c>) — deliberately: it names no constant the slice renamed, and
/// reads the admission threshold out of the journal entry that carries it rather
/// than out of <see cref="PrototypeTuning"/>. The two runs and the command that
/// produced each are in <c>evidence/405-before-after.json</c>.</para>
///
/// <para><b>Everything is read from the canonical event log</b> and from
/// <c>GetSnapshot()</c>, never from the internals. That is not decoration: a
/// refusal to join is decided in phase 4 and the same creature goes on to eat,
/// take a job and move in the phases after it, so
/// <see cref="PrototypeCreatureSnapshot.LastDecision"/> no longer holds the
/// refusal by the end of the tick. The journal does — <c>RecordDecision</c>
/// appends there and only updates <c>LastTick</c>/<c>Repeats</c> when the very
/// same entry repeats — so an entry whose <c>LastTick</c> is the tick that just
/// acted is exactly what that tick recorded.</para>
///
/// <para><b>The denominator is the domain on its feet</b> — every creature whose
/// mode is not <see cref="CreatureMode.Downed"/> on the tick the wave arrives.
/// It is the honest one for the sentence being checked: a body on the floor is
/// visibly out of the fight and nobody expects it to run anywhere, while
/// everyone else is somebody the player watches either go or not go. The bodies
/// are counted and reported beside the share rather than folded into it.</para>
/// </summary>
public sealed class PrototypeMusterParticipationTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    private static readonly string[] Fixtures = ["baseline", "prepared", "neglected"];

    /// <summary>
    /// The seed the owner played on 2026-08-12, kept beside the shipped matrix
    /// because the report has to be able to answer «what did I see» with the
    /// party that was actually seen.
    /// </summary>
    private const ulong PlaytestSeed = 20_260_729UL;

    /// <summary>
    /// The five sentences <c>UpdateCombatParticipation</c> can write about one
    /// creature at one roll call. Exactly one of them is written per creature per
    /// roll call, which is what makes a count of them a partition rather than a
    /// tally of overlapping things.
    /// </summary>
    private static readonly string[] MusterCodes =
    [
        "combat_joined",
        "combat_refused_injured",
        "combat_refused_starving",
        "combat_refused_grudge",
        "combat_absent_unreachable",
    ];

    private static IReadOnlyList<Measurement> Matrix => MatrixMeasurements.Value;

    private static readonly Lazy<IReadOnlyList<Measurement>> MatrixMeasurements =
        new(() =>
        [
            .. Fixtures.SelectMany(_ => MatrixSeeds, (fixtureName, seed) => Measure(fixtureName, seed)),
            .. Fixtures.Select(fixtureName => Measure(fixtureName, PlaytestSeed)),
        ]);

    [Fact]
    public void Report_muster_participation_over_the_seed_matrix()
    {
        var report = new StringBuilder();
        foreach (var measurement in Matrix)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{measurement}");
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"{Pooled(Matrix)}");
        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// The floor the report is worth anything as: most of the domain that is on
    /// its feet when a wave lands does take the field.
    ///
    /// <para>Pooled over every wave of every party that saw one, because the unit
    /// of «a property of the world rather than of one party» in this suite is the
    /// matrix. The number is a floor and not the measurement: what was measured
    /// is in the report above and in <c>evidence/405-before-after.json</c>.</para>
    ///
    /// <para><b>This is the assertion the admission threshold moves</b>, and that
    /// is the point of it. Raising the admission threshold — <c>CombatJoinSatiety</c>
    /// after the balance slice, <c>CombatMinSatiety</c> before it —
    /// takes creatures out of the numerator through
    /// <c>combat_refused_starving</c> and nothing else; the mutants that show it
    /// in both directions are in <c>evidence/405-mutant.json</c>.</para>
    /// </summary>
    [Fact]
    public void Most_of_the_domain_on_its_feet_takes_the_field()
    {
        var waves = Matrix.SelectMany(run => run.Waves).Where(wave => wave.OnFeet > 0).ToArray();

        Assert.True(
            waves.Length > 0,
            $"Not one wave of the matrix was met by anybody at all, so nothing below was asked." +
            $"{Environment.NewLine}{Detail()}");

        var onFeet = waves.Sum(wave => wave.OnFeet);
        var joined = waves.Sum(wave => wave.Joined.Count);

        Assert.True(
            joined * 100 >= onFeet * 60,
            $"Only {joined} of {onFeet} creature-slots on their feet took the field over " +
            $"{waves.Length} waves ({joined * 100 / onFeet}%), which is below the floor of 60%." +
            $"{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// A refusal names the reason it actually refused for — the truthfulness rule
    /// of Issue #125, asked of the muster's own journal.
    ///
    /// <para>Every <c>combat_refused_starving</c> carries the satiety it refused
    /// at and the threshold it was compared against, and the first must be below
    /// the second. Without this the share above could fall for a reason the
    /// journal misnames, and the breakdown by reason code — the whole result of
    /// Issue #405 — would be a story rather than a measurement.</para>
    /// </summary>
    [Fact]
    public void A_refusal_to_join_names_a_reason_that_holds()
    {
        var lying = Matrix.SelectMany(run => run.StarvingRefusalsThatDoNotHold).ToArray();

        Assert.True(
            lying.Length == 0,
            $"{lying.Length} refusals to join said «starving» while the satiety they carry is not " +
            $"below the threshold they carry: {string.Join(' ', lying.Take(8))}" +
            $"{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// First half of the admission fix of Issue #405: the nearest-raider arm of
    /// contract 10.2 exists at all.
    ///
    /// <para>«Расстояние от самого существа до ближайшего налётчика <b>или</b> до
    /// ближайшего тайла кладовой не превышает T.engage_radius» — the code measured
    /// only the larder arm, so a creature with a raider two tiles from it and the
    /// larder twelve tiles from it was turned away. Over the twelve parties that
    /// is 20 roll calls on the tree this branch starts from and 30 on the tree
    /// before the balance slice; the worst of them is <c>baseline/20260729</c> at
    /// t2020, where five of nine were refused with raiders 2, 5, 5, 6 and 6 tiles
    /// away — the wave the owner is describing.</para>
    ///
    /// <para><b>Manhattan and therefore a floor of zero rather than a count.</b>
    /// Manhattan never exceeds the walk, so a reading of 6 does not prove the walk
    /// was 6; a refusal could in principle be honest with a raider close in a
    /// straight line and far round a wall. The check is nonetheless an equality to
    /// zero, because over these twelve parties the fixed world has none at all,
    /// and a floor that admitted «a few» would be a number nothing chose. If a
    /// legitimate one ever appears, the message says which party and tick to look
    /// at.</para>
    /// </summary>
    [Fact]
    public void A_creature_a_raider_is_already_near_is_not_turned_away_for_being_far_from_the_larder()
    {
        var turnedAway = Matrix.SelectMany(run => run.UnreachableWhileARaiderStoodNear).ToArray();

        Assert.True(
            turnedAway.Length == 0,
            $"{turnedAway.Length} roll calls refused a creature as unreachable while a raider stood " +
            $"within the engage radius of it, which contract 10.2 admits: " +
            $"{string.Join(' ', turnedAway.Take(10))}{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// Second half of the same fix: the larder arm is <b>kept</b> and not traded
    /// for the raider arm.
    ///
    /// <para>It is what lets the domain form a line before a raider is anywhere
    /// near it — the first roll call of a wave happens on the tick the wave lands,
    /// when the raiders are still walking in at the gate. This counts the joins
    /// where every raider on the map is <b>provably</b> further than the engage
    /// radius from the creature that joined: Manhattan is a lower bound on the
    /// walk, so a Manhattan greater than the radius proves the walk is greater
    /// too, and the arm that admitted such a creature can only have been the
    /// larder one.</para>
    ///
    /// <para>A floor of one join, and the unit is the matrix. Replacing
    /// <c>min(toLarder, toRaider)</c> with <c>toRaider</c> alone empties this
    /// entirely — that is mutant M5 of <c>evidence/405-mutant.json</c>.</para>
    /// </summary>
    [Fact]
    public void A_creature_within_reach_of_the_larder_is_admitted_before_any_raider_is_near()
    {
        var joins = Matrix.Sum(run => run.JoinedWhileEveryRaiderWasFarAway.Count);

        Assert.True(
            joins > 0,
            $"Not one creature of the matrix took the field while every raider on the map was " +
            $"provably further than the engage radius from it, so nothing admits a defender but " +
            $"the nearness of a raider and the larder arm of contract 10.2 is gone." +
            $"{Environment.NewLine}{Detail()}");
    }

    private static string Detail() =>
        string.Join(Environment.NewLine, Matrix.Select(measurement => measurement.ToString()))
        + Environment.NewLine + Pooled(Matrix);

    private static string Pooled(IReadOnlyList<Measurement> runs)
    {
        var waves = runs.SelectMany(run => run.Waves).Where(wave => wave.OnFeet > 0).ToArray();
        if (waves.Length == 0)
        {
            return "POOLED no wave was met by anybody";
        }

        var onFeet = waves.Sum(wave => wave.OnFeet);
        var joined = waves.Sum(wave => wave.Joined.Count);
        var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var wave in waves)
        {
            foreach (var reason in wave.AbsentByReason())
            {
                byReason[reason.Key] = byReason.GetValueOrDefault(reason.Key) + reason.Value;
            }
        }

        return $"POOLED waves={waves.Length} onFeet={onFeet} joined={joined} " +
            $"share={joined * 100 / onFeet}% absent={onFeet - joined} " +
            $"absentBy=[{string.Join(',', byReason.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))}] " +
            $"thresholdSeen={string.Join('/', waves.SelectMany(wave => wave.ThresholdsSeen).Distinct().Order())}";
    }

    /// <summary>
    /// What the roll call's own distance test walks over: rock, plus the tiles the
    /// player has forbidden. It is the same set <c>PrototypeWorld.DistanceToTheFight</c>
    /// hands to the map — bodies are deliberately not in it, because that method
    /// does not put them there either, and a harness that walked round bodies
    /// would be measuring a different rule from the one it is checking.
    /// </summary>
    private static HashSet<GridPoint> WallsOf(PrototypeSnapshot state)
    {
        var walls = new HashSet<GridPoint>(state.Map.RockTiles);
        walls.UnionWith(state.Zones[ZoneKind.Forbidden]);
        return walls;
    }

    /// <summary>
    /// Steps from one tile to another over <paramref name="walls"/>, or
    /// <c>null</c> when there is no way. Breadth-first, four-neighbour, in the
    /// map's own visiting order — the same shape as <c>PrototypeMap.Distance</c>,
    /// restated here because the map is internal to the simulation assembly.
    /// </summary>
    private static int? Walk(GridPoint start, GridPoint target, IReadOnlySet<GridPoint> walls)
    {
        if (start == target)
        {
            return 0;
        }

        var visited = new HashSet<GridPoint> { start };
        var queue = new Queue<(GridPoint Tile, int Steps)>();
        queue.Enqueue((start, 0));
        while (queue.TryDequeue(out var current))
        {
            foreach (var next in Neighbors(current.Tile))
            {
                if (next.X < 0 || next.X >= PrototypeTuning.MapWidth ||
                    next.Y < 0 || next.Y >= PrototypeTuning.MapHeight ||
                    walls.Contains(next) ||
                    !visited.Add(next))
                {
                    continue;
                }

                if (next == target)
                {
                    return current.Steps + 1;
                }

                queue.Enqueue((next, current.Steps + 1));
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

    private static Measurement Measure(string fixtureName, ulong seed)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var tally = new Measurement(fixtureName, seed);
        var previous = world.GetSnapshot();

        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            if (current.Tick == previous.Tick)
            {
                // A step spent waiting for a verdict runs no phase and moves no
                // tick (Issue #312). Reading the journal again against a world
                // that has not moved would count the same roll call twice.
                previous = current;
                continue;
            }

            var acted = current.Tick - 1;
            Observe(previous, current, acted, tally);
            previous = current;
        }

        foreach (var creature in previous.Creatures)
        {
            tally.NameOf[creature.Id] = creature.Name;
        }

        tally.Ticks = previous.Tick;
        tally.Outcome = previous.SessionResult.Outcome ?? "unfinished";
        return tally;
    }

    private static void Observe(
        PrototypeSnapshot previous,
        PrototypeSnapshot current,
        int acted,
        Measurement tally)
    {
        // The roster is taken on the tick the wave lands, after that tick has
        // acted — which is the first roll call, so a creature that answered at
        // once is already in the line here and is counted by its `combat_joined`
        // below rather than by its mode.
        foreach (var wave in current.Waves.Where(wave => wave.ArriveTick == acted))
        {
            var tallyForWave = tally.WaveOf(wave.Number);
            tallyForWave.ArriveTick = wave.ArriveTick;
            tallyForWave.OnFeet = current.Creatures.Count(creature => creature.Mode != CreatureMode.Downed);
            tallyForWave.OnTheFloor = current.Creatures.Count(creature => creature.Mode == CreatureMode.Downed);
            foreach (var creature in current.Creatures.Where(creature => creature.Mode != CreatureMode.Downed))
            {
                tallyForWave.Roster.Add(creature.Id);
            }
        }

        foreach (var wave in current.Waves.Where(wave => wave.EndTick == acted && wave.Outcome is not null))
        {
            var tallyForWave = tally.WaveOf(wave.Number);
            tallyForWave.EndTick = acted;
            tallyForWave.Outcome = wave.Outcome;
        }

        foreach (var entry in current.Events)
        {
            if (entry.LastTick != acted || !MusterCodes.Contains(entry.ReasonCode, StringComparer.Ordinal))
            {
                continue;
            }

            if (!entry.Details.TryGetValue("wave", out var waveNumber))
            {
                continue;
            }

            var tallyForWave = tally.WaveOf(waveNumber);
            tallyForWave.RollCallTicks.Add(acted);
            if (entry.ReasonCode == "combat_joined")
            {
                tallyForWave.Joined.Add(entry.CreatureId);
                tallyForWave.JoinTick.TryAdd(entry.CreatureId, acted);

                // A join that only the larder arm of contract 10.2 can account
                // for. Three conditions together, and each of them removes one
                // other thing that could have admitted this creature:
                //
                //  - it was NOT mustering at the start of the tick, so the
                //    `!creature.IsMustering` escape in the admission rule did not
                //    let it past the distance test. Read off the previous snapshot
                //    because joining clears the flag in the same tick;
                //  - at least one raider is standing on the map, so the distance
                //    test had something to measure to at all;
                //  - every one of them is further away in a straight line than the
                //    engage radius, and the walk is never shorter than the straight
                //    line — so the raider arm cannot have admitted it.
                var wasMustering = previous.Creatures
                    .FirstOrDefault(creature => creature.Id == entry.CreatureId)?.IsMustering ?? false;
                var raidersOnTheMap = current.Raiders
                    .Where(raider => raider.Mode == RaiderMode.Raiding)
                    .Select(raider => Math.Abs(raider.Position.X - PositionOf(current, entry.CreatureId).X) +
                        Math.Abs(raider.Position.Y - PositionOf(current, entry.CreatureId).Y))
                    .ToArray();
                if (!wasMustering &&
                    raidersOnTheMap.Length > 0 &&
                    raidersOnTheMap.Min() > PrototypeTuning.EngageRadius)
                {
                    tally.JoinedWhileEveryRaiderWasFarAway.Add(
                        $"t{acted}#{entry.CreatureId}(toNearestRaider={raidersOnTheMap.Min()})");
                }

                continue;
            }

            tallyForWave.Refusals.Add(new Refusal(entry.CreatureId, entry.ReasonCode, acted, entry.Details));
            if (entry.ReasonCode == "combat_absent_unreachable")
            {
                // The contract's distance test (10.2) admits a creature that is
                // within T.engage_radius of the nearest RAIDER **or** of the
                // nearest larder tile; the implementation measures only to
                // LarderTiles[0]. This counts the roll calls where the two could
                // disagree — the creature was turned away for being far from the
                // larder while a raider stood near it.
                //
                // <b>The walk and not Manhattan, and read off the board the roll
                // call itself saw.</b> Both halves are repairs Issue #409 made,
                // and both were forced by a false accusation rather than chosen.
                //
                // This used to compare Manhattan against the radius, and the
                // comment here said in as many words that Manhattan is «an
                // indicator and not a verdict» because it never exceeds the walk —
                // and then the check below asserted on it anyway. The rule of 10.2
                // is about the walk, so a raider six tiles away with a wall between
                // is a raider the rule does not admit, and Manhattan cannot tell
                // that from a raider six tiles away down a corridor. The
                // localised-injury slice moved the party into exactly that case:
                // baseline/20260726 at t2020, a creature refused with toLarder=11
                // while a raider stood at Manhattan 6 and more than
                // T.engage_radius of walking away.
                //
                // Positions come from `previous` for the second reason: the roll
                // call runs in phase 4, before anybody moves, so the board at the
                // start of the tick is the board it decided on. `current` is the
                // board after the raiders have taken their step.
                var seenByTheRollCall = PositionOf(previous, entry.CreatureId);
                var walls = WallsOf(previous);
                var nearestRaider = previous.Raiders
                    .Where(raider => raider.Mode == RaiderMode.Raiding)
                    .Select(raider => Walk(seenByTheRollCall, raider.Position, walls))
                    .Where(steps => steps is not null)
                    .Select(steps => steps!.Value)
                    .DefaultIfEmpty(int.MaxValue)
                    .Min();
                if (nearestRaider <= PrototypeTuning.EngageRadius)
                {
                    tally.UnreachableWhileARaiderStoodNear.Add(
                        $"t{acted}#{entry.CreatureId}(toLarder={entry.Details.GetValueOrDefault("distance", -1)},toRaider={nearestRaider})");
                }
            }

            if (entry.ReasonCode == "combat_refused_starving")
            {
                var satiety = entry.Details.GetValueOrDefault("satiety", -1);
                var threshold = entry.Details.GetValueOrDefault("threshold", -1);
                tallyForWave.ThresholdsSeen.Add(threshold);
                if (satiety >= threshold)
                {
                    tally.StarvingRefusalsThatDoNotHold.Add(
                        $"t{acted}#{entry.CreatureId}(satiety={satiety},threshold={threshold})");
                }
            }
        }
    }

    private static GridPoint PositionOf(PrototypeSnapshot snapshot, int creatureId) =>
        snapshot.Creatures.First(creature => creature.Id == creatureId).Position;

    private sealed record Refusal(int CreatureId, string ReasonCode, int Tick, IReadOnlyDictionary<string, int> Details);

    private sealed class WaveTally(int number)
    {
        public int Number { get; } = number;

        public int ArriveTick { get; set; }

        public int? EndTick { get; set; }

        public string? Outcome { get; set; }

        public int OnFeet { get; set; }

        public int OnTheFloor { get; set; }

        public HashSet<int> Roster { get; } = [];

        public HashSet<int> Joined { get; } = [];

        public Dictionary<int, int> JoinTick { get; } = [];

        public List<Refusal> Refusals { get; } = [];

        public SortedSet<int> RollCallTicks { get; } = [];

        public SortedSet<int> ThresholdsSeen { get; } = [];

        /// <summary>
        /// Who was on their feet when the wave landed and never took the field
        /// during it. A creature that joined at any roll call of the wave is not
        /// here, however late it joined — the question is participation, not
        /// punctuality, and lateness is reported separately by the join tick.
        /// </summary>
        public IEnumerable<int> Absent() => Roster.Where(id => !Joined.Contains(id)).Order();

        /// <summary>
        /// The reason each absentee is absent for, counted once per creature and
        /// not once per roll call: a creature refused three times for hunger is
        /// one creature the player did not see run, not three. The reason taken is
        /// the one of its <b>last</b> roll call of the wave, because that is the
        /// one that still held when the wave ended.
        /// </summary>
        public Dictionary<string, int> AbsentByReason()
        {
            var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in Absent())
            {
                var last = Refusals.Where(refusal => refusal.CreatureId == id)
                    .OrderBy(refusal => refusal.Tick)
                    .LastOrDefault();
                var reason = last?.ReasonCode ?? "never_asked";
                byReason[reason] = byReason.GetValueOrDefault(reason) + 1;
            }

            return byReason;
        }

        public string Share() => OnFeet == 0 ? "-" : $"{Joined.Count * 100 / OnFeet}%";
    }

    private sealed class Measurement(string fixtureName, ulong seed)
    {
        private readonly Dictionary<int, WaveTally> _waves = [];

        public int Ticks { get; set; }

        public string Outcome { get; set; } = "unfinished";

        public Dictionary<int, string> NameOf { get; } = [];

        public List<string> StarvingRefusalsThatDoNotHold { get; } = [];

        /// <summary>
        /// Roll calls where <c>combat_absent_unreachable</c> was written while a
        /// raider stood within the engage radius of the creature it turned away.
        /// Reported and not asserted on — see the note at the site that fills it.
        /// </summary>
        public List<string> UnreachableWhileARaiderStoodNear { get; } = [];

        /// <summary>
        /// Joins that only the larder arm of contract 10.2 can account for: every
        /// raider on the map was provably further than the engage radius away.
        /// </summary>
        public List<string> JoinedWhileEveryRaiderWasFarAway { get; } = [];

        public IEnumerable<WaveTally> Waves => _waves.Values.OrderBy(wave => wave.Number);

        public WaveTally WaveOf(int number)
        {
            if (!_waves.TryGetValue(number, out var wave))
            {
                wave = new WaveTally(number);
                _waves[number] = wave;
            }

            return wave;
        }

        private string Who(int id) => $"#{id}{(NameOf.TryGetValue(id, out var name) ? " " + name : string.Empty)}";

        public override string ToString()
        {
            var lines = new List<string>
            {
                $"{fixtureName}/{seed} ticks={Ticks} outcome={Outcome} waves={_waves.Count}",
            };

            var totalOnFeet = 0;
            var totalJoined = 0;
            foreach (var wave in Waves)
            {
                totalOnFeet += wave.OnFeet;
                totalJoined += wave.Joined.Count;
                lines.Add(
                    $"  MUSTER {fixtureName}/{seed} w{wave.Number} arrive=t{wave.ArriveTick} " +
                    $"end={(wave.EndTick is null ? "-" : "t" + wave.EndTick)} outcome={wave.Outcome ?? "-"} " +
                    $"onFeet={wave.OnFeet} onTheFloor={wave.OnTheFloor} joined={wave.Joined.Count} " +
                    $"share={wave.Share()} rollCalls={wave.RollCallTicks.Count} " +
                    $"absentBy=[{string.Join(',', wave.AbsentByReason().OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))}]");

                var absent = wave.Absent().ToArray();
                lines.Add(absent.Length == 0
                    ? $"    absent w{wave.Number}: nobody"
                    : $"    absent w{wave.Number}: " + string.Join("; ", absent.Select(id =>
                        {
                            var refusals = wave.Refusals.Where(refusal => refusal.CreatureId == id)
                                .GroupBy(refusal => refusal.ReasonCode, StringComparer.Ordinal)
                                .OrderBy(group => group.Key, StringComparer.Ordinal)
                                .Select(group =>
                                {
                                    var last = group.OrderBy(refusal => refusal.Tick).Last();
                                    var detail = string.Join(',', last.Details
                                        .Where(pair => pair.Key != "wave")
                                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                        .Select(pair => $"{pair.Key}={pair.Value}"));
                                    return $"{group.Key}x{group.Count()}({detail})";
                                });
                            var text = string.Join('|', refusals);
                            return $"{Who(id)} {(text.Length == 0 ? "never_asked" : text)}";
                        })));

                var late = wave.JoinTick
                    .Where(pair => pair.Value > wave.ArriveTick)
                    .OrderBy(pair => pair.Value)
                    .Select(pair => $"{Who(pair.Key)}@t{pair.Value}(+{pair.Value - wave.ArriveTick})")
                    .ToArray();
                lines.Add($"    joinedLate w{wave.Number}: {(late.Length == 0 ? "nobody" : string.Join(' ', late))}");
            }

            lines.Add(
                $"  CONTRACT {fixtureName}/{seed} unreachableWhileARaiderStoodNear=" +
                $"{UnreachableWhileARaiderStoodNear.Count} " +
                $"[{string.Join(' ', UnreachableWhileARaiderStoodNear.Take(6))}] " +
                $"joinedWhileEveryRaiderWasFarAway={JoinedWhileEveryRaiderWasFarAway.Count}");
            lines.Add(
                $"  RUNSHARE {fixtureName}/{seed} onFeet={totalOnFeet} joined={totalJoined} " +
                $"share={(totalOnFeet == 0 ? "-" : $"{totalJoined * 100 / totalOnFeet}%")}");
            return string.Join(Environment.NewLine, lines);
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
