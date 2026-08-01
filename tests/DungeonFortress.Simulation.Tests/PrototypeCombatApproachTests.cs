using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// How a defender walks up to a raider, measured rather than described.
///
/// Issue #129. The owner watched a raid on 2026-08-01 and said two things about
/// it: fighters pile onto each other, and only one of them seems able to strike
/// even when a raider is surrounded on two sides. Both are about geometry, and
/// geometry is the one thing the canonical snapshot publishes on every tick, so
/// both are counted here instead of being argued about.
///
/// The measurement is written so that it says something on either side of the
/// edit. Before it, <c>ActCombatant</c> sent every fighter to the raider's own
/// tile: one destination for everybody who shares a nearest enemy, so the BFS
/// hands them a single corridor and they arrive in a column. After it, the
/// destination is a free tile beside the raider, chosen by each fighter for
/// itself.
///
/// Three numbers carry the claim, and all three are per (fixture, seed) over a
/// whole party:
///
/// <list type="bullet">
/// <item><description><c>contactShare</c> — of all the ticks a fighter spends
/// in <see cref="CreatureMode.Fighting"/> while a raider is on the map, the
/// share it spends within striking distance of one. A column is fighters not
/// touching anything.</description></item>
/// <item><description><c>queuedInTheScrum</c> — fighter-ticks where the fighter
/// is within <see cref="ScrumRadius"/> steps of its own target by the map, is
/// **not** touching it, and a free tile beside that target existed anyway. This
/// is the column stated as a count: it is not "far away and walking", it is
/// "here, with a place to stand, standing somewhere else".</description></item>
/// <item><description><c>meanTouchingPerEngagedRaider</c> — averaged over every
/// (tick, raider) pair where at least one defender is in reach, how many
/// defenders are in reach. This is the owner's second sentence turned into a
/// number.</description></item>
/// </list>
///
/// Where the numbers come from is deliberately not the event log. Canonical
/// events coalesce a repeated identical decision into one entry with a
/// <see cref="PrototypeEvent.Repeats"/> count and no per-tick record, so
/// counting attacks by scanning entries for a tick undercounts a fighter that
/// rolled the same damage twice in a row. Positions do not coalesce. Attacks are
/// read from <see cref="PrototypeCreatureSnapshot.LastDecision"/>, which carries
/// its own tick, and the positional count is reported next to it so the two can
/// be compared.
/// </summary>
public sealed class PrototypeCombatApproachTests(ITestOutputHelper output)
{
    /// <summary>
    /// How close by the map a fighter has to be to its target before standing
    /// still counts as queueing rather than walking. Three steps is the scrum:
    /// one tile is contact, two is the tile behind contact, three is the tile
    /// behind that. Beyond it a fighter is simply on its way and says nothing
    /// about the shape of the fight.
    /// </summary>
    private const int ScrumRadius = 3;

    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    private static readonly string[] Fixtures = ["baseline", "prepared"];

    /// <summary>
    /// The numbers themselves, printed rather than asserted. This is the "before
    /// and after" of Issue #129, and it is a fact rather than a requirement
    /// because the corridors it produces are a property of six runs, not of the
    /// design (13.4 of the contract).
    /// </summary>
    [Fact]
    public void Report_combat_approach_over_the_seed_matrix()
    {
        var report = new StringBuilder();
        foreach (var measurement in Matrix)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{measurement}");
        }

        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// Criterion 1 of Issue #129: with free tiles beside a raider, fighters do
    /// not line up behind one another.
    ///
    /// It is asserted over the matrix as a whole rather than seed by seed,
    /// following the rule of 13.4 that a corridor is a property of the runs and
    /// only the matrix carries requirements. What is asserted is a ratio and not
    /// a count: of the places around a raider that the fighters who came for it
    /// could have filled, the share they did.
    ///
    /// Measured before the edit the share was 0.418 over the six runs
    /// (0.354–0.471 individually), and after it 0.512 (0.475–0.555) —
    /// <c>evidence/129-before.json</c> and <c>evidence/129-matrix.json</c>. The
    /// threshold sits between the two and closer to the new floor than to the
    /// old ceiling, because the number it guards is the destination rule, not the
    /// jitter of a fight.
    /// </summary>
    [Fact]
    public void The_places_beside_a_raider_are_taken_rather_than_queued_for()
    {
        var places = Matrix.Sum(measurement => measurement.SurroundPlaces);
        var taken = Matrix.Sum(measurement => measurement.SurroundTaken);
        var share = (double)taken / places;

        Assert.True(
            share >= 0.48,
            $"Fighters filled {taken} of the {places} places around a raider that the fighters " +
            $"present for it could have filled — a share of {share:F3}, where 0.48 is the floor. " +
            "Below it they are queueing behind one another instead of taking the free tile " +
            $"beside the enemy.{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// The second half of the owner's sentence — «похоже только 1 может атаковать
    /// одновременно даже если с 2 сторон окружили» — asserted from the side the
    /// edit is allowed to touch.
    ///
    /// The rules of combat never capped this: <c>ActCombatant</c> runs once per
    /// fighter and counts nothing. Geometry did. Over the six matrix parties
    /// before the edit, three defenders were never once simultaneously adjacent
    /// to the same raider and never once struck it in the same tick; the ceiling
    /// was two. Reaching three is therefore a binary fact about the approach rule
    /// rather than a corridor, and it is asserted as one.
    /// </summary>
    [Fact]
    public void Three_defenders_can_reach_and_strike_one_raider_in_the_same_tick()
    {
        var touching = Matrix.Max(measurement => measurement.MaxTouchingOneRaider);
        var attackers = Matrix.Max(measurement => measurement.MaxAttackersOnOneRaider);

        Assert.True(
            touching >= 3,
            $"Over the whole matrix at most {touching} defender(s) were ever adjacent to one " +
            "raider at the same time, although a raider has four neighbouring tiles. That is the " +
            "column: only the head of it is ever in reach." +
            $"{Environment.NewLine}{Detail()}");
        Assert.True(
            attackers >= 3,
            $"Over the whole matrix at most {attackers} defender(s) ever struck the same raider " +
            "in the same tick, although the resolution of combat has never limited how many may. " +
            $"Standing room, not the rule, is what was short.{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// The six parties, measured once and shared. A party takes about a second to
    /// walk, and three assertions over the same six would otherwise pay for it
    /// three times.
    /// </summary>
    private static IReadOnlyList<ApproachMeasurement> Matrix => MatrixMeasurements.Value;

    private static readonly Lazy<IReadOnlyList<ApproachMeasurement>> MatrixMeasurements =
        new(() =>
        [
            .. Fixtures.SelectMany(
                _ => MatrixSeeds,
                (fixtureName, seed) => Measure(fixtureName, seed)),
        ]);

    private static string Detail() =>
        string.Join(Environment.NewLine, Matrix.Select(measurement => measurement.ToString()));

    /// <summary>
    /// One party, walked tick by tick, counting what the fight looked like from
    /// above.
    /// </summary>
    private static ApproachMeasurement Measure(string fixtureName, ulong seed)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var tally = new Tally();
        var last = world.GetSnapshot();

        while (!world.IsComplete)
        {
            world.Step();
            last = world.GetSnapshot();
            Observe(last, tally);
        }

        return tally.ToMeasurement(fixtureName, seed, last.Tick);
    }

    private static void Observe(PrototypeSnapshot snapshot, Tally tally)
    {
        var acted = snapshot.Tick - 1;
        var raiders = snapshot.Raiders
            .Where(raider => raider.Mode == RaiderMode.Raiding)
            .ToArray();
        if (raiders.Length == 0)
        {
            return;
        }

        foreach (var stack in raiders.GroupBy(raider => raider.Position))
        {
            tally.MaxRaidersOnOneTile = Math.Max(tally.MaxRaidersOnOneTile, stack.Count());
        }

        var fighters = snapshot.Creatures
            .Where(creature => creature.Mode == CreatureMode.Fighting)
            .ToArray();

        CountAttacks(snapshot, acted, tally);

        if (fighters.Length == 0)
        {
            return;
        }

        tally.CombatTicks++;
        tally.FighterTicks += fighters.Length;

        var blocked = Blocked(snapshot, raiders);
        var reach = new Dictionary<int, IReadOnlyDictionary<GridPoint, int>>();
        var wanted = new Dictionary<int, int>();

        foreach (var fighter in fighters)
        {
            // The same rule ActCombatant uses to pick a target: nearest raider by
            // Manhattan distance, lowest id on a tie. It is restated here rather
            // than exported, because a measurement that asks the code what it did
            // cannot disagree with it.
            var target = raiders
                .OrderBy(raider => Manhattan(fighter.Position, raider.Position))
                .ThenBy(raider => raider.Id)
                .First();
            if (fighter.LastDecision.Tick == acted &&
                fighter.LastDecision.ReasonCode == "waiting_blocked_by_other")
            {
                tally.BlockedWhileFighting++;
            }

            if (Manhattan(fighter.Position, target.Position) <= PrototypeTuning.MeleeAttackRange)
            {
                tally.InContactFighterTicks++;
                wanted[target.Id] = wanted.GetValueOrDefault(target.Id) + 1;
                continue;
            }

            if (!reach.TryGetValue(target.Id, out var distances))
            {
                distances = Distances(snapshot, target.Position);
                reach[target.Id] = distances;
            }

            if (!distances.TryGetValue(fighter.Position, out var steps) || steps > ScrumRadius)
            {
                continue;
            }

            wanted[target.Id] = wanted.GetValueOrDefault(target.Id) + 1;
            tally.QueuedInTheScrumFighterTicks++;
            var free = FreeTilesBeside(snapshot, target.Position, blocked);
            if (free > 0)
            {
                tally.QueuedInTheScrumWithAFreeTile++;
                tally.FreeTilesWhileQueued += free;
            }
        }

        var standing = fighters.Select(fighter => fighter.Position).ToHashSet();
        foreach (var raider in raiders)
        {
            var perimeter = Neighbors(raider.Position)
                .Where(tile => InBounds(tile) &&
                    !snapshot.Map.RockTiles.Contains(tile) &&
                    !snapshot.Zones[ZoneKind.Forbidden].Contains(tile) &&
                    !raiders.Any(other => other.Position == tile))
                .ToArray();
            var taken = perimeter.Count(standing.Contains);
            if (wanted.TryGetValue(raider.Id, out var candidates) && candidates > 0)
            {
                // How many of the places around this raider could have been
                // filled by the fighters that are actually here for it, and how
                // many are. A column is the gap between the two.
                tally.SurroundPlaces += Math.Min(perimeter.Length, candidates);
                tally.SurroundTaken += taken;
            }

            var touching = fighters.Count(fighter =>
                Manhattan(fighter.Position, raider.Position) <= PrototypeTuning.MeleeAttackRange);
            if (touching == 0)
            {
                continue;
            }

            tally.EngagedRaiderTicks++;
            tally.TouchingSum += touching;
            tally.MaxTouchingOneRaider = Math.Max(tally.MaxTouchingOneRaider, touching);
            tally.TouchingHistogram[Math.Min(touching, 4)]++;
        }
    }

    /// <summary>
    /// Who struck whom on the tick that just ran, read off each creature's own
    /// last decision. A killing blow writes <c>combat_raider_downed</c> over the
    /// <c>combat_attack</c> that caused it, so both codes count as a strike and
    /// both carry the raider they landed on.
    /// </summary>
    private static void CountAttacks(PrototypeSnapshot snapshot, int acted, Tally tally)
    {
        var byTarget = new Dictionary<int, int>();
        foreach (var creature in snapshot.Creatures)
        {
            var decision = creature.LastDecision;
            if (decision.Tick != acted ||
                decision.ReasonCode is not ("combat_attack" or "combat_raider_downed") ||
                !decision.Details.TryGetValue("raiderId", out var raiderId))
            {
                continue;
            }

            byTarget[raiderId] = byTarget.GetValueOrDefault(raiderId) + 1;
            tally.Strikes++;
        }

        foreach (var attacked in byTarget.Values)
        {
            tally.AttackedRaiderTicks++;
            tally.MaxAttackersOnOneRaider = Math.Max(tally.MaxAttackersOnOneRaider, attacked);
            tally.AttackerHistogram[Math.Min(attacked, 4)]++;
        }
    }

    /// <summary>
    /// Every tile no fighter may stand on: rock, a forbidden zone, a tile a
    /// creature of the domain already holds, and a tile a raider holds. The last
    /// one is the contract's occupancy rule (4.1) and not the implementation's:
    /// <c>Move</c> only checks creatures. It is counted as blocked here because
    /// the question being asked is "was there a place to stand", and standing on
    /// a raider is not one.
    /// </summary>
    private static HashSet<GridPoint> Blocked(
        PrototypeSnapshot snapshot,
        IReadOnlyList<PrototypeRaiderSnapshot> raiders)
    {
        var blocked = new HashSet<GridPoint>(snapshot.Map.RockTiles);
        blocked.UnionWith(snapshot.Zones[ZoneKind.Forbidden]);
        blocked.UnionWith(snapshot.Creatures.Select(creature => creature.Position));
        blocked.UnionWith(raiders.Select(raider => raider.Position));
        return blocked;
    }

    private static int FreeTilesBeside(
        PrototypeSnapshot snapshot,
        GridPoint tile,
        IReadOnlySet<GridPoint> blocked) =>
        Neighbors(tile).Count(neighbor => InBounds(neighbor) && !blocked.Contains(neighbor));

    /// <summary>
    /// Map distance from one tile to every tile reachable from it: rock and
    /// forbidden stop the flood, creatures do not. That is the same thing the
    /// simulation's own BFS sees, and it is what makes "three steps away and not
    /// striking" a statement about the fight rather than about the walls.
    /// </summary>
    private static IReadOnlyDictionary<GridPoint, int> Distances(
        PrototypeSnapshot snapshot,
        GridPoint start)
    {
        var walls = new HashSet<GridPoint>(snapshot.Map.RockTiles);
        walls.UnionWith(snapshot.Zones[ZoneKind.Forbidden]);
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

                distances[neighbor] = next;
                queue.Enqueue(neighbor);
            }
        }

        return distances;
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

    private static int Manhattan(GridPoint left, GridPoint right) =>
        Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private sealed class Tally
    {
        public int CombatTicks;
        public int FighterTicks;
        public int InContactFighterTicks;
        public int QueuedInTheScrumFighterTicks;
        public int QueuedInTheScrumWithAFreeTile;
        public int FreeTilesWhileQueued;
        public int EngagedRaiderTicks;
        public int TouchingSum;
        public int MaxTouchingOneRaider;
        public int AttackedRaiderTicks;
        public int MaxAttackersOnOneRaider;
        public int Strikes;
        public int MaxRaidersOnOneTile;
        public int BlockedWhileFighting;
        public int SurroundPlaces;
        public int SurroundTaken;
        public readonly int[] TouchingHistogram = new int[5];
        public readonly int[] AttackerHistogram = new int[5];

        public ApproachMeasurement ToMeasurement(string fixtureName, ulong seed, int ticks) =>
            new(
                fixtureName,
                seed,
                ticks,
                CombatTicks,
                FighterTicks,
                InContactFighterTicks,
                QueuedInTheScrumFighterTicks,
                QueuedInTheScrumWithAFreeTile,
                FreeTilesWhileQueued,
                EngagedRaiderTicks,
                TouchingSum,
                MaxTouchingOneRaider,
                AttackedRaiderTicks,
                MaxAttackersOnOneRaider,
                Strikes,
                MaxRaidersOnOneTile,
                BlockedWhileFighting,
                SurroundPlaces,
                SurroundTaken,
                [.. TouchingHistogram],
                [.. AttackerHistogram]);
    }

    private sealed record ApproachMeasurement(
        string Fixture,
        ulong Seed,
        int Ticks,
        int CombatTicks,
        int FighterTicks,
        int InContactFighterTicks,
        int QueuedInTheScrumFighterTicks,
        int QueuedInTheScrumWithAFreeTile,
        int FreeTilesWhileQueued,
        int EngagedRaiderTicks,
        int TouchingSum,
        int MaxTouchingOneRaider,
        int AttackedRaiderTicks,
        int MaxAttackersOnOneRaider,
        int Strikes,
        int MaxRaidersOnOneTile,
        int BlockedWhileFighting,
        int SurroundPlaces,
        int SurroundTaken,
        IReadOnlyList<int> TouchingHistogram,
        IReadOnlyList<int> AttackerHistogram)
    {
        /// <summary>
        /// Of the places around a raider that the fighters present for it could
        /// have filled, the share they did fill. One is a raider surrounded by
        /// everybody who came for it; a half is one fighter in place and one
        /// standing behind it. This is the "line" of Issue #129 as a number.
        /// </summary>
        public double SurroundShare =>
            SurroundPlaces == 0 ? 0 : (double)SurroundTaken / SurroundPlaces;

        public double ContactShare =>
            FighterTicks == 0 ? 0 : (double)InContactFighterTicks / FighterTicks;

        public double MeanTouchingPerEngagedRaider =>
            EngagedRaiderTicks == 0 ? 0 : (double)TouchingSum / EngagedRaiderTicks;

        public double MeanAttackersPerAttackedRaider =>
            AttackedRaiderTicks == 0 ? 0 : (double)Strikes / AttackedRaiderTicks;

        public override string ToString() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Fixture}/{Seed} ticks={Ticks} combatTicks={CombatTicks} " +
                $"fighterTicks={FighterTicks} inContact={InContactFighterTicks} " +
                $"contactShare={ContactShare:F3} " +
                $"queuedInScrum={QueuedInTheScrumFighterTicks} " +
                $"queuedWithFreeTile={QueuedInTheScrumWithAFreeTile} " +
                $"freeTilesWhileQueued={FreeTilesWhileQueued} " +
                $"engagedRaiderTicks={EngagedRaiderTicks} " +
                $"meanTouching={MeanTouchingPerEngagedRaider:F3} " +
                $"maxTouching={MaxTouchingOneRaider} " +
                $"touchingHistogram=[{string.Join(',', TouchingHistogram)}] " +
                $"strikes={Strikes} attackedRaiderTicks={AttackedRaiderTicks} " +
                $"meanAttackers={MeanAttackersPerAttackedRaider:F3} " +
                $"maxAttackers={MaxAttackersOnOneRaider} " +
                $"attackerHistogram=[{string.Join(',', AttackerHistogram)}] " +
                $"maxRaidersOnOneTile={MaxRaidersOnOneTile} " +
                $"blockedWhileFighting={BlockedWhileFighting} " +
                $"surroundPlaces={SurroundPlaces} surroundTaken={SurroundTaken} " +
                $"surroundShare={SurroundShare:F3}");
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
