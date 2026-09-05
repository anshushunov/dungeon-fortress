using System.Text;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// The observable claims of slice 5 of the pitch's order of proof (section 6.8):
/// a raider has a name, the one who left alive comes back two waves later under
/// that name, he takes a place in the wave instead of adding one, he comes back
/// stronger with the scar the domain actually gave him, and he walks round the
/// tile where it was given.
///
/// Design contract: <c>docs/design/SLICE_05_RETURNING_HERO.md</c>.
/// </summary>
public sealed class PrototypeReturningRaiderTests(ITestOutputHelper output)
{
    /// <summary>
    /// The shipped journal the slice is read on. Its second wave leaves six
    /// raiders alive, and its fourth wave is where they come back.
    /// </summary>
    private const string ShippedFixture = "baseline";

    /// <summary>
    /// A party on which the memory of place changes a route. It is a seed and not
    /// a fixture because the shipped one does not reach the case: on
    /// <c>baseline</c> the only survivor with a scar was hit on the larder tile
    /// itself, which is where he is walking to, so there is nothing to walk round
    /// (see <see cref="A_memory_takes_away_a_road_and_never_the_objective"/>).
    /// </summary>
    private const ulong RouteSeed = 20_260_729UL;

    /// <summary>
    /// The seeds a scene may be looked for in when the shipped journal's own does
    /// not contain it. The range is the one the scan of Issue #361 used and it is
    /// stated here rather than in a docstring, so that a search over it is a
    /// reproducible procedure rather than a number somebody once wrote down.
    /// </summary>
    private static readonly ulong[] SearchSeeds =
        [.. Enumerable.Range(0, 30).Select(offset => 20_260_726UL + (ulong)offset)];

    /// <summary>
    /// A party on which a returning raider's memory of place <b>is</b> the
    /// objective — the tile he is walking to — so the rule has to let him walk
    /// onto it. The shipped journal's own seed does not reach the case (see
    /// <see cref="A_memory_takes_away_a_road_and_never_the_objective"/>).
    ///
    /// <para><b>Found by its shape and not written down.</b> It was the literal
    /// 20260747, obtained by scanning <see cref="SearchSeeds"/> once, by hand,
    /// during Issue #361 — and a literal obtained that way is a scene pinned to a
    /// party. The party has since changed twice (the health scale of Issue #336
    /// and cell occupancy of Issue #76) and 20260747 stopped containing the
    /// scene, which is how a check that states a rule came to fail for want of a
    /// subject. The scan is therefore the code, run over the same range, taking
    /// the first seed that holds the scene; if none does, the failure says so
    /// instead of asserting over an empty set.</para>
    ///
    /// <para><b>A downed-on-the-way-in candidate is not a scene (trophy slice,
    /// Task 5), and the exclusion is cut by cause, not by <c>Mode</c> alone
    /// (review finding 1 of fix round 3, 2026-09-05).</b> Freeing a weapon off
    /// a fallen or fled holder moves who is standing where and how hard the
    /// line hits, which moves every raider's wounds and every returning
    /// raider's road in turn. On the tree fix round 3 measured this on, the
    /// first seed of the range (20260726) brought back raider #18 remembering
    /// the larder tile but put down at (23,4) before it ever got near it,
    /// walking the direct approach the whole way — combat, not memory, ended
    /// it, the same shape
    /// <see cref="A_returning_raider_walks_round_the_place_it_was_hit_hardest"/>
    /// already tracks apart from the rule under a name of its own
    /// (<c>putDownOnTheWayIn</c>). <see cref="ObjectiveWitnesses"/> is the one
    /// filter both this search and the Fact below read, so a seed accepted
    /// here can never disagree with what the Fact then asserts (review
    /// finding 2) — the search asks for at least one witness surviving that
    /// filter, and the Fact quantifies over exactly that same, already-culled
    /// population rather than the raw, unfiltered one.</para>
    ///
    /// <para><b>The doorstep, not the direct road, is what a raider fearing
    /// its own objective can be diverted from (review finding of fix round 4,
    /// 2026-09-05).</b> <see cref="RoadNotMemoryEndedIt"/>'s road-shape
    /// comparison says nothing when the feared tile is the objective itself —
    /// nothing can be routed round its own destination, so a raider cut down
    /// approaching normally and one refused the very last step onto the
    /// objective by memory of it, then left lingering next to it until
    /// combat found it there, looked identical to that comparison. The second
    /// is exactly the violation this bound exists to catch, so the exclusion
    /// for this one case reads whether the raider ever reached a tile
    /// adjacent to the objective (Manhattan distance 1) instead: one that
    /// never did was stopped by something with no business at the doorstep at
    /// all, and is excluded; one that reached the doorstep and still failed
    /// to step in is kept.</para>
    ///
    /// <para><b>Where the search lands, after both fixes and after finding 3's
    /// own production change (paying a circulating blade's debt only once,
    /// same fix round).</b> That change moved combat pacing again, and on the
    /// current tree the first seed of the range (20260726) no longer needs
    /// either exclusion at all: raider #18 (Сиплый) now reaches the larder and
    /// escapes cleanly (<c>Mode.Escaped</c>, larder tile visited), so
    /// <see cref="ObjectiveSeedSearch"/> returns 20260726 itself. The doorstep
    /// rule is not exercised by this particular witness — it exists for
    /// whichever future shift next produces the shape it was written for,
    /// the same way the cause-based exclusion above sat unexercised on this
    /// exact seed until Task 5 gave it a subject.</para>
    /// </summary>
    private static ulong ObjectiveSeed => ObjectiveSeedSearch.Value;

    /// <summary>
    /// The returning raiders of <paramref name="state"/> that remember the
    /// objective tile and survive <see cref="RoadNotMemoryEndedIt"/> — read by
    /// both <see cref="ObjectiveSeedSearch"/> and
    /// <see cref="A_memory_takes_away_a_road_and_never_the_objective"/>, so the
    /// two can never quantify over different populations again (review
    /// finding 2).
    /// </summary>
    private static PrototypeRaiderSnapshot[] ObjectiveWitnesses(
        PrototypeSnapshot state,
        Dictionary<int, HashSet<GridPoint>> visits) =>
        state.Raiders
            .Where(raider =>
                raider.ReturnedFromWave is not null &&
                raider.RememberedPlace?.Place == FirstLarderTile &&
                !RoadNotMemoryEndedIt(raider.Mode, FirstLarderTile, FirstLarderTile, visits[raider.Id]))
            .ToArray();

    private static readonly Lazy<ulong> ObjectiveSeedSearch = new(() =>
    {
        foreach (var seed in SearchSeeds)
        {
            var (visits, state) = RaiderRoutes(ShippedFixture, seed);
            if (ObjectiveWitnesses(state, visits).Length > 0)
            {
                return seed;
            }
        }

        throw new InvalidOperationException(
            $"No seed of {SearchSeeds[0]}..{SearchSeeds[^1]} brings a returning raider back " +
            $"remembering the larder tile {FirstLarderTile} without having been put down on the " +
            "way in, so the bound «a memory takes away a road and never the objective» has no " +
            "scene to be read on. That is a finding about the world rather than a broken test: " +
            "either the objective stopped being the tile raiders are hit on, or returning " +
            "raiders stopped carrying a memory at all, or every one that does is cut down before " +
            "the road it walked could say anything about memory.");
    });

    /// <summary>
    /// Where every raider is walking to. It is read out of the authored layout
    /// rather than out of <c>PrototypeMap</c>, which is internal: the raiders go
    /// to the first larder tile in reading order, which is what
    /// <c>PrototypeLayout.Read</c>'s own docstring says the order is for.
    /// </summary>
    private static GridPoint FirstLarderTile => PrototypeLayout.Read('L')[0];

    /// <summary>
    /// Where every raider walks from. Read out of the authored layout for the
    /// same reason <see cref="FirstLarderTile"/> is.
    /// </summary>
    private static GridPoint Gate => PrototypeLayout.Read('G')[0];

    private static readonly GridPoint[] StepOffsets =
        [new(0, -1), new(1, 0), new(0, 1), new(-1, 0)];

    /// <summary>
    /// A tile of the <b>authored</b> layout a body can stand on — everything
    /// but rock (<c>#</c>) and the quarry face it is spelled differently for
    /// (<c>d</c>). This is not quite <c>PrototypeMap.IsPassable</c>'s own rule
    /// over the live map: the live map also treats a dug-out tile as passable,
    /// and this reads the fixed picture in <see cref="PrototypeLayout"/>
    /// instead of the internal map, so a tile excavated mid-party never joins
    /// the approach here. That divergence is fail-loud rather than silent: a
    /// raider that actually walks onto newly-dug rock falls outside every
    /// shortest-approach set this file computes and is reported as stranded
    /// (or, for the objective's own doorstep branch, as combat-ended) rather
    /// than correctly excused — a false positive a reader would see and could
    /// investigate, not a false negative that hides a real stranding. It is
    /// acceptable for what this file measures because raiders do not path
    /// through a domain's own dig sites in the shipped fixtures the returning-raider
    /// slice is read on; if that ever changes, this rule needs the live map's
    /// excavated tiles added to it, not silent trust that it already has them.
    /// </summary>
    private static bool IsPassableTile(GridPoint point)
    {
        if (point.Y < 0 || point.Y >= PrototypeLayout.Rows.Count)
        {
            return false;
        }

        var row = PrototypeLayout.Rows[point.Y];
        return point.X >= 0 && point.X < row.Length && row[point.X] is not ('#' or 'd');
    }

    /// <summary>
    /// Every tile's distance from <paramref name="start"/> by a plain grid walk
    /// of the authored layout, optionally with <paramref name="blocked"/> made
    /// impassable too. A breadth-first search over a sixteen-by-twenty-eight
    /// board costs nothing worth avoiding, so it is not cached.
    /// </summary>
    private static Dictionary<GridPoint, int> DistancesFrom(GridPoint start, GridPoint? blocked)
    {
        var distances = new Dictionary<GridPoint, int> { [start] = 0 };
        var queue = new Queue<GridPoint>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out var current))
        {
            foreach (var offset in StepOffsets)
            {
                var next = new GridPoint(current.X + offset.X, current.Y + offset.Y);
                if (!IsPassableTile(next) || next == blocked || distances.ContainsKey(next))
                {
                    continue;
                }

                distances[next] = distances[current] + 1;
                queue.Enqueue(next);
            }
        }

        return distances;
    }

    /// <summary>
    /// Every tile that lies on at least one shortest walk from
    /// <paramref name="start"/> to <paramref name="target"/>, optionally with
    /// <paramref name="obstacle"/> made impassable — "the shortest approach the
    /// map gives you", read by a breadth-first search from both ends and kept
    /// where the two halves add up to the shortest total. Empty when no such
    /// walk exists (the obstacle cuts every road).
    /// </summary>
    private static HashSet<GridPoint> ShortestApproachTiles(
        GridPoint start,
        GridPoint target,
        GridPoint? obstacle)
    {
        var fromStart = DistancesFrom(start, obstacle);
        if (!fromStart.TryGetValue(target, out var shortest))
        {
            return [];
        }

        var fromTarget = DistancesFrom(target, obstacle);
        var tiles = new HashSet<GridPoint>();
        foreach (var (tile, distance) in fromStart)
        {
            if (fromTarget.TryGetValue(tile, out var back) && distance + back == shortest)
            {
                tiles.Add(tile);
            }
        }

        return tiles;
    }

    /// <summary>
    /// Cause, not outcome (review finding 1 of the coordinator's fix round 3,
    /// 2026-09-05): a downed raider that never reached <paramref
    /// name="objective"/> is excluded from a stranding claim only when its own
    /// route shows the road, not its memory, is what ended it there. That road
    /// is "the shortest approach the map gives you, with the remembered tile
    /// as the only obstacle" — computed once by <see cref="ShortestApproachTiles"/>
    /// with <paramref name="feared"/> blocked. A raider whose visited tiles
    /// never leave that approach was walking it correctly when something else
    /// — combat, almost always — cut it down; one whose visited tiles leave it
    /// was not, and stays counted wherever this returns <c>false</c>.
    ///
    /// <para><b>When <paramref name="feared"/> equals <paramref
    /// name="objective"/> itself (review finding of fix round 4, 2026-09-05),
    /// the road-shape comparison above says nothing at all.</b> Nothing can be
    /// routed round its own destination, so the "obstacle blocked" approach and
    /// the plain direct one are the same set — a raider cut down while
    /// approaching normally and a raider refused the very last step onto the
    /// objective by memory of that objective, then left lingering next to it
    /// until combat found it there, walk identical routes by that comparison,
    /// and the second is exactly the violation
    /// <see cref="A_memory_takes_away_a_road_and_never_the_objective"/> exists
    /// to catch. The doorstep is the evidence instead: memory's only way to
    /// divert a raider from its own objective is to stop it crossing the
    /// threshold, so a raider that never even reached a tile adjacent to the
    /// objective (Manhattan distance 1) was stopped by something with no
    /// business at the objective's doorstep at all — usually combat, well
    /// short of it — and is excluded; one that reached the doorstep and still
    /// failed to step in is kept, whatever the direct-route comparison would
    /// have said.</para>
    ///
    /// <para>The same helper backs three call sites — the `stranded` bucket
    /// below, <see cref="ObjectiveSeedSearch"/>'s candidate filter and the
    /// `atTheObjective` filter of the Fact that search feeds — so a seed the
    /// search accepts can never disagree with what the Fact then asserts
    /// (review finding 2): both read the same cause on the same route. Only
    /// the two call sites that ever pass <paramref name="feared"/> equal to
    /// <paramref name="objective"/> (the search and its Fact) reach the
    /// doorstep branch; `stranded` never does, because its own <c>avoiders</c>
    /// population is filtered to remembered tiles other than the objective.</para>
    /// </summary>
    private static bool RoadNotMemoryEndedIt(
        RaiderMode mode,
        GridPoint objective,
        GridPoint feared,
        IReadOnlySet<GridPoint> visited)
    {
        if (mode != RaiderMode.Downed || visited.Contains(objective))
        {
            return false;
        }

        if (feared == objective)
        {
            return !visited.Any(tile => Manhattan(tile, objective) == 1);
        }

        var direct = ShortestApproachTiles(Gate, objective, null);
        var obstacle = direct.Contains(feared) ? feared : (GridPoint?)null;
        var approach = ShortestApproachTiles(Gate, objective, obstacle);
        return visited.All(approach.Contains);
    }

    private static int Manhattan(GridPoint a, GridPoint b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    [Fact]
    public void Every_name_a_party_can_need_fits_in_the_pool()
    {
        var largestPossibleParty = PrototypeTuning.WaveMaxRaiders * PrototypeTuning.WaveCount;
        Assert.True(
            PrototypeRaiderNames.Capacity > largestPossibleParty,
            $"the pool yields {PrototypeRaiderNames.Capacity} distinct names and the largest " +
            $"party this prototype can field brings {largestPossibleParty} raiders. " +
            "DrawRaiderName throws rather than repeating a name, so this is not a " +
            "cosmetic bound.");
    }

    /// <summary>
    /// Criterion 1 of Issue #358, in the half a test can hold: the same seed gives
    /// the same names, and no two raiders of one party share one. The other half —
    /// that the only source of the spread is <see cref="DeterministicRandom"/> — is
    /// a property of the source and is measured by <c>rg</c> over the changed
    /// files, in <c>evidence/358-determinism.json</c>.
    /// </summary>
    [Fact]
    public void A_name_is_deterministic_and_no_two_raiders_of_one_party_share_one()
    {
        var first = FullParty(ShippedFixture, PrototypeTuning.DefaultSeed);
        var second = FullParty(ShippedFixture, PrototypeTuning.DefaultSeed);

        Assert.Equal(first.Checksum, second.Checksum);
        Assert.Equal(first.CanonicalJson, second.CanonicalJson);

        var names = first.State.Raiders.Select(raider => raider.Name).ToArray();
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(
            names.Length,
            names.Where((_, index) =>
                    // A returning raider carries the name it already had, so the
                    // uniqueness claim is about bodies that are different people:
                    // one name may appear twice only when the second body is the
                    // return of the first.
                    first.State.Raiders[index].ReturnedFromWave is null)
                .Distinct(StringComparer.Ordinal)
                .Count() +
            first.State.Raiders.Count(raider => raider.ReturnedFromWave is not null));

        // Same names in the same order, run twice, stated separately from the
        // checksum so a failure says which of the two moved.
        Assert.Equal(names, second.State.Raiders.Select(raider => raider.Name).ToArray());
    }

    /// <summary>
    /// Criterion 2: he leaves in wave N, is not in wave N+1, and is in wave N+2
    /// under the same name.
    /// </summary>
    [Fact]
    public void A_raider_who_left_alive_comes_back_two_waves_later_under_the_same_name()
    {
        var state = FullParty(ShippedFixture, PrototypeTuning.DefaultSeed).State;

        var returned = state.Raiders.Where(raider => raider.ReturnedFromWave is not null).ToArray();
        Assert.NotEmpty(returned);
        Assert.All(returned, raider => Assert.Equal(
            raider.Wave,
            raider.ReturnedFromWave!.Value + PrototypeTuning.ReturningRaiderWaveGap));

        foreach (var raider in returned)
        {
            var escapedAs = state.Raiders.Single(other =>
                other.Id != raider.Id &&
                other.Name == raider.Name &&
                other.Wave == raider.ReturnedFromWave!.Value);
            Assert.Equal(RaiderMode.Escaped, escapedAs.Mode);

            // Absent from the wave in between: the whole point of a gap of two is
            // that there is a wave the domain does not see him in.
            Assert.DoesNotContain(
                state.Raiders,
                other => other.Name == raider.Name &&
                    other.Wave == raider.ReturnedFromWave!.Value + 1);

            var survivor = state.Survivors.Single(item =>
                item.Name == raider.Name && item.EscapedWave == raider.ReturnedFromWave!.Value);
            Assert.Equal("returned", survivor.Status);
            Assert.Equal(raider.Id, survivor.ReturnedAsRaiderId);
            Assert.Equal(raider.Wave, survivor.ReturnWave);
        }
    }

    /// <summary>
    /// Criterion 3: the wave did not grow. Its size is what renown bought at the
    /// announce tick, and a returning raider stands in one of those places.
    /// </summary>
    [Fact]
    public void A_returning_raider_takes_a_place_in_the_wave_instead_of_adding_one()
    {
        var state = FullParty(ShippedFixture, PrototypeTuning.DefaultSeed).State;

        Assert.Contains(state.Raiders, raider => raider.ReturnedFromWave is not null);
        foreach (var wave in state.Waves.Where(wave => wave.Arrived))
        {
            var fromRenown = Math.Min(
                PrototypeTuning.WaveMaxRaiders,
                PrototypeTuning.WaveBaseRaiders +
                wave.RenownAtAnnounce / PrototypeTuning.RenownPerExtraRaider);
            Assert.Equal(fromRenown, wave.RaiderCount);
            Assert.Equal(
                wave.RaiderCount,
                state.Raiders.Count(raider => raider.Wave == wave.Number));
        }
    }

    /// <summary>
    /// A return the party has no wave left for is written down, with the reason,
    /// rather than dropped. Issue #358 asks for exactly this: «возвращаться некуда,
    /// и это должно быть видно в снапшоте, а не молча потеряно».
    /// </summary>
    [Fact]
    public void A_return_with_no_wave_left_is_written_down_rather_than_dropped()
    {
        var state = FullParty(ShippedFixture, PrototypeTuning.DefaultSeed).State;

        Assert.All(state.Survivors, survivor => Assert.Equal(
            survivor.EscapedWave + PrototypeTuning.ReturningRaiderWaveGap,
            survivor.ReturnWave));
        Assert.All(state.Survivors, survivor => Assert.Contains(
            survivor.Status,
            new[] { "awaiting", "returned", "no_wave_left", "no_room_in_wave" }));

        var lost = state.Survivors.Where(survivor => survivor.Status == "no_wave_left").ToArray();
        Assert.NotEmpty(lost);
        Assert.All(lost, survivor => Assert.True(
            survivor.ReturnWave > state.SessionResult.WaveCount,
            $"{survivor.Name} is recorded as having no wave left, but wave " +
            $"{survivor.ReturnWave} is inside a party of {state.SessionResult.WaveCount}"));
        Assert.All(lost, survivor => Assert.Null(survivor.ReturnedAsRaiderId));

        // And a party that ended leaves nobody merely waiting: every debt has an
        // answer by the last tick.
        Assert.DoesNotContain(state.Survivors, survivor => survivor.Status == "awaiting");
    }

    /// <summary>
    /// Criterion 4, in the half a test can hold: he is measurably stronger than the
    /// strangers he walks in with. How much that changes the wave is a measurement
    /// and lives in <c>evidence/358-strengthening.json</c>.
    /// </summary>
    [Fact]
    public void A_returning_raider_is_stronger_than_the_wave_it_arrives_with()
    {
        var state = FullParty(ShippedFixture, PrototypeTuning.DefaultSeed).State;

        foreach (var raider in state.Raiders.Where(item => item.ReturnedFromWave is not null))
        {
            var wave = state.Waves.Single(item => item.Number == raider.Wave);
            Assert.Equal(
                wave.RaiderMight + PrototypeTuning.ReturningRaiderMightBonus,
                raider.Might - MightJitterOf(state, raider));
        }

        // And stronger in exactly one way. He walks in with the health every
        // raider walks in with: the health bonus that used to be here was removed
        // by measurement, and asserting its absence is what keeps "the
        // strengthening is one knob" a checked statement rather than a comment.
        // Health is read at entry, so it is taken on the tick the wave lands
        // rather than at the end of the party, when everybody has been hit.
        var atEntry = PartyUntil(
            ShippedFixture,
            PrototypeTuning.DefaultSeed,
            world => world.GetSnapshot().Raiders.Any(raider => raider.ReturnedFromWave is not null));
        var returning = atEntry.Raiders.Single(raider => raider.ReturnedFromWave is not null);
        Assert.Equal(PrototypeTuning.RaiderHp, returning.Hp);
    }

    /// <summary>
    /// The scar is read off the damage that landed. A raider nobody reached walks
    /// back with none, and — because a memory of place is the tile of the hardest
    /// blow — with nothing to remember either.
    /// </summary>
    [Fact]
    public void A_scar_is_read_off_the_damage_that_landed_and_never_assigned()
    {
        var state = FullParty(ShippedFixture, PrototypeTuning.DefaultSeed).State;

        foreach (var survivor in state.Survivors)
        {
            var body = state.Raiders.Single(raider =>
                raider.Name == survivor.Name && raider.Wave == survivor.EscapedWave);
            const int startingHp = PrototypeTuning.RaiderHp;
            var expected = body.Hp >= startingHp
                ? InjuryKind.None
                : body.Hp * 100 > startingHp * PrototypeTuning.LightInjuryShare
                    ? InjuryKind.Light
                    : InjuryKind.Heavy;
            Assert.Equal(expected, survivor.Scar);
            Assert.Equal(survivor.Scar == InjuryKind.None, survivor.RememberedPlace is null);
        }

        Assert.Contains(state.Survivors, survivor => survivor.Scar == InjuryKind.None);
        Assert.Contains(state.Survivors, survivor => survivor.Scar != InjuryKind.None);
    }

    /// <summary>
    /// Criterion 5: the route of the returning raider does not pass through the
    /// tile he remembers, and the route of the same raider before he remembered it
    /// does.
    ///
    /// <para><b>Read over twelve parties and as a floor, and both of those were
    /// put here by a measurement rather than by taste (Issue #405).</b> It used to
    /// be read on <see cref="RouteSeed"/> alone and to forbid the step outright,
    /// and that claim is stronger than the mechanic it is about:
    /// <c>RaiderStep</c> walks round the remembered tile <b>when there is a way
    /// round</b> and straight through when there is not, which is its own
    /// documented bound and the reason the rule cannot strand anybody. Scanned
    /// over seeds 20260726..20260755 on the tree the check was green on
    /// (<c>main</c> at 8977b0d, no edit of this branch applied): of 54 returning
    /// raiders carrying a memory off the objective, four walked over it and three
    /// were stranded. So the absolute form was already false on the shipped world
    /// — it passed because it was pinned to one seed on which it happened to
    /// hold.</para>
    ///
    /// <para>What replaces it is the same claim read on a wider subject and stated
    /// as what the mechanic promises: a memory takes a road away from nearly
    /// everybody who carries one, and takes the raid away from nobody. On the
    /// twelve parties below, on that same tree, 2 of 25 avoiders stepped on their
    /// tile and none was stranded; with the admission fix of Issue #405 applied,
    /// the same 25 avoiders give the same 2 and the same 0. The floor is one fifth
    /// and the stranding claim stays absolute — it is the half a raider actually
    /// loses the raid to, and the half the admission fix carries from three
    /// stranded to none over the full thirty.</para>
    /// </summary>
    [Fact]
    public void A_returning_raider_walks_round_the_place_it_was_hit_hardest()
    {
        var avoiderCount = 0;
        var walkedOverIt = new List<string>();
        var stranded = new List<string>();
        var putDownOnTheWayIn = new List<string>();

        foreach (var seed in SearchSeeds.Take(12))
        {
            var (visits, state) = RaiderRoutes(ShippedFixture, seed);
            var avoiders = state.Raiders
                .Where(raider =>
                    raider.ReturnedFromWave is not null &&
                    raider.RememberedPlace is { } place &&
                    place.Place != FirstLarderTile)
                .ToArray();

            foreach (var raider in avoiders)
            {
                avoiderCount++;
                var remembered = raider.RememberedPlace!.Place;

                // Before: the body that carried this name last time stood there. It
                // has to have — the tile is where it was hit — and asserting it is
                // what makes the "after" half a change rather than an absence. This
                // one stays absolute, because it is a fact about how the memory was
                // written and not about how a route is walked.
                var previous = state.Raiders.Single(other =>
                    other.Name == raider.Name && other.Wave == raider.ReturnedFromWave!.Value);
                Assert.Contains(remembered, visits[previous.Id]);

                if (visits[raider.Id].Contains(remembered))
                {
                    walkedOverIt.Add($"{seed}/{raider.Name}@({remembered.X},{remembered.Y})");
                }

                // Not arriving has two shapes (Issue #409), and which one a
                // downed raider is counted as is cut by cause, not by `Mode`
                // alone (review finding 1 of fix round 3, 2026-09-05, reverting
                // an outcome-based `Mode != Downed` this file briefly carried).
                // A memory walling the raider out is what this clause is named
                // for; the domain putting it down on a route memory never
                // touched is the domain doing its job and has nothing to do
                // with what the raider remembers — but a raider memory *did*
                // wall off, that wandered, and was *then* cut down, is still a
                // stranding: outcome (Downed) cannot tell the two apart, only
                // the route can. `RoadNotMemoryEndedIt` reads that route: a
                // downed raider is excluded from `stranded` only when its
                // visited tiles never leave the shortest approach the map
                // gives it with the remembered tile as the one obstacle to
                // route around (or the plain direct approach, when the
                // remembered tile was never on the road to begin with). The
                // two buckets below therefore may now disagree where they used
                // to agree by accident (PR #417 measured `stranded=0` and
                // `putDownOnTheWayIn=0` together on every seed this file had
                // ever walked; the trophy slice's own trajectory shifts are
                // the first to separate them). Found on baseline/20260735:
                // Бурый Младший remembers (16,7), died at (15,7) one tile short
                // of the larder (14,7) — its full eighteen-tile route
                // ((26,13)…(26,8), west along y=8 to (15,8), then the single
                // step to (15,7)) is exactly the shortest walk from the gate
                // to the larder and never touches (16,7) at all, so
                // `RoadNotMemoryEndedIt` is true: the road, not the memory,
                // ended it, and it is excluded from `stranded` and counted
                // only in `putDownOnTheWayIn`. See the report for the full
                // trace and the general seeds/candidates this was checked
                // against.
                if (!visits[raider.Id].Contains(FirstLarderTile) &&
                    raider.Mode != RaiderMode.Escaped &&
                    !RoadNotMemoryEndedIt(raider.Mode, FirstLarderTile, remembered, visits[raider.Id]))
                {
                    stranded.Add($"{seed}/{raider.Name}");
                }

                // Counted beside the clause above and asserted on by nothing
                // (Issue #409): the domain putting a raider down on the way in,
                // named apart from a true stranding rather than folded into it.
                // Outcome-based on purpose — this bucket is a diagnostic print,
                // not the assertion, so it does not need the cause-based cut
                // `stranded` does.
                if (raider.Mode == RaiderMode.Downed &&
                    !visits[raider.Id].Contains(FirstLarderTile))
                {
                    putDownOnTheWayIn.Add($"{seed}/{raider.Name}");
                }
            }
        }

        Assert.True(
            avoiderCount > 0,
            "No returning raider of the twelve parties carries a memory off the objective, so " +
            "criterion 5 has no subject at all. That is a finding about the world rather than a " +
            "broken check: either raiders stopped being hit anywhere but the larder tile, or they " +
            "stopped coming back remembering it.");

        Assert.True(
            walkedOverIt.Count * 5 <= avoiderCount,
            $"{walkedOverIt.Count} of {avoiderCount} returning raiders walked over the tile they " +
            $"remember, which is more than the one fifth the way-round-when-there-is-a-way-round " +
            $"rule leaves room for: {string.Join(' ', walkedOverIt)}");

        // <b>The clause is cut by cause again, not by an outcome-based
        // exclusion (fix round 3, 2026-09-05, reverting the interim state of
        // fix round 1).</b> Issue #409 relaxed it once by outcome — a raider
        // put down on the way in, and a raider of a wave the session fuse cut
        // short, were both excluded from «stranded» regardless of why — and
        // independent review of PR #417 measured that neither exclusion was
        // needed on that tree: the population they would have removed was
        // empty (`putDownOnTheWayIn=0`, `stranded=0` over the twelve parties),
        // so an outcome-based relaxation that bought nothing was taken back
        // out, and the two buckets had agreed by accident ever since. The
        // trophy slice's own trajectory shifts ended that accident: they
        // produced the first downed raider this file has ever measured whose
        // own route shows combat, not memory, ended it (Бурый Младший,
        // baseline/20260735, traced above). Re-adding an unconditional `Mode
        // != Downed` exclusion here would repeat exactly the outcome-based
        // relaxation PR #417 rejected — it would also silently clear a raider
        // memory genuinely walled off and wandering, if combat happened to
        // finish it afterwards. `RoadNotMemoryEndedIt` is the cause-based
        // difference: it excludes a downed raider from `stranded` only when
        // its own route never left the shortest approach the map gives it, so
        // the two buckets below are now allowed to disagree — `stranded` can
        // stay 0 while `putDownOnTheWayIn` is not, exactly the case this seed
        // is — where before that would have meant one of them was wrong.
        Assert.True(
            stranded.Count == 0,
            $"{stranded.Count} returning raiders neither reached the larder nor left: avoidance " +
            $"must not be able to strand a raider. {string.Join(' ', stranded)}");

        // <b>What Issue #409 leaves behind is a measurement and not a change.</b>
        // A wave resolves only when none of its raiders is still raiding, so an
        // avoider of a resolved wave is Escaped, Downed, or the defect the clause
        // names — and this line prints how many fall in each, so that «the clause
        // is green» can be told apart from «the clause has no subject». On the
        // shipped journals it currently prints zero on both counts, which is the
        // evidence behind the vacuity finding raised against Issue #358 itself.
        output.WriteLine(
            $"ROUTES avoiders={avoiderCount} walkedOverIt={walkedOverIt.Count} " +
            $"stranded={stranded.Count} putDownOnTheWayIn={putDownOnTheWayIn.Count} " +
            $"[{string.Join(' ', putDownOnTheWayIn)}]");
    }

    /// <summary>
    /// The bound on the rule: when the remembered place <b>is</b> the objective
    /// there is nothing to walk round, so the raider walks onto it — and that is
    /// the raider side of the bound Issue #171 put on a creature's memory.
    /// </summary>
    /// <para><b>Why it has its own seed.</b> It used to be read on the shipped
    /// journal's own seed, where the one survivor of <c>baseline</c> with a scar
    /// happened to have been hit on the larder tile itself. Issue #361 made the
    /// damage jitter live, which changes who is hit where, and on
    /// <c>PrototypeTuning.DefaultSeed</c> no returning raider remembers the
    /// larder tile any more — the scene the bound is about is simply not in that
    /// party. Scanned over <c>baseline</c> and <c>prepared</c> at seeds
    /// 20260726–20260755, it is in eight parties; this one has two raiders in it
    /// rather than one, which is why it and not the first hit was taken. The other
    /// half of the rule — walking round a memory that is not the objective — was
    /// pinned to <see cref="RouteSeed"/> in the same way and for the same reason,
    /// and Issue #405 replaced that pin with a floor read over twelve parties; see
    /// <see cref="A_returning_raider_walks_round_the_place_it_was_hit_hardest"/>.</para>
    [Fact]
    public void A_memory_takes_away_a_road_and_never_the_objective()
    {
        var (visits, state) = RaiderRoutes(ShippedFixture, ObjectiveSeed);

        // The identical filter ObjectiveSeedSearch accepted the seed on
        // (review finding 2): a seed this search picks can never leave the
        // Fact quantifying over a raider the search itself would have called
        // a non-witness.
        var atTheObjective = ObjectiveWitnesses(state, visits);
        Assert.NotEmpty(atTheObjective);
        Assert.All(atTheObjective, raider => Assert.Contains(
            FirstLarderTile,
            visits[raider.Id]));
    }

    /// <summary>
    /// The numbers of <c>evidence/358-*.json</c>, printed rather than asserted:
    /// what each party's survivors are, when they come back, what they come back
    /// with, and where they will not go.
    /// </summary>
    [Fact]
    public void Report_the_returning_raiders_of_the_shipped_journals()
    {
        var report = new StringBuilder();
        foreach (var (fixture, seed) in new[]
                 {
                     (ShippedFixture, PrototypeTuning.DefaultSeed),
                     ("prepared", PrototypeTuning.DefaultSeed),
                     (ShippedFixture, RouteSeed),
                 })
        {
            var run = FullParty(fixture, seed);
            var state = run.State;
            report.AppendLine($"== {fixture} / seed {seed} checksum {run.Checksum}");
            foreach (var wave in state.Waves)
            {
                report.AppendLine(
                    $"   wave {wave.Number}: raiderCount {wave.RaiderCount} " +
                    $"(renown {wave.RenownAtAnnounce}) might {wave.RaiderMight} " +
                    $"outcome {wave.Outcome} endTick {wave.EndTick} " +
                    $"raidersDowned {wave.RaidersDowned} " +
                    $"returning {state.Raiders.Count(raider => raider.Wave == wave.Number && raider.ReturnedFromWave is not null)}");
            }

            foreach (var survivor in state.Survivors)
            {
                report.AppendLine(
                    $"   survivor {survivor.Name}: escapedWave {survivor.EscapedWave} " +
                    $"escapedTick {survivor.EscapedTick} returnWave {survivor.ReturnWave} " +
                    $"status {survivor.Status} scar {survivor.Scar} " +
                    $"remembers {(survivor.RememberedPlace is { } place ? $"({place.Place.X},{place.Place.Y}) @ {place.Tick}" : "nothing")} " +
                    $"asRaiderId {survivor.ReturnedAsRaiderId?.ToString() ?? "-"}");
            }

            report.AppendLine(
                $"   session {state.SessionResult.Outcome} endTick {state.SessionResult.EndTick} " +
                $"score {state.SessionResult.Score} mealsStolen {state.SessionResult.MealsStolen} " +
                $"raidersDowned {state.SessionResult.RaidersDowned} " +
                $"defendersDowned {state.SessionResult.DefendersDowned} " +
                $"defendersFled {state.SessionResult.DefendersFled}");
        }

        output.WriteLine(report.ToString());
        Assert.NotEmpty(report.ToString());
    }

    /// <summary>
    /// The jitter this raider's might was rolled with, recovered from the wave it
    /// belongs to. It is bounded by <c>T.raider_might_jitter</c>, so a claim about
    /// the bonus can be made without reproducing the combat stream.
    /// </summary>
    private static int MightJitterOf(PrototypeSnapshot state, PrototypeRaiderSnapshot raider)
    {
        var wave = state.Waves.Single(item => item.Number == raider.Wave);
        var jitter = raider.Might - wave.RaiderMight - PrototypeTuning.ReturningRaiderMightBonus;
        Assert.InRange(
            jitter,
            -PrototypeTuning.RaiderMightJitter,
            PrototypeTuning.RaiderMightJitter);
        return jitter;
    }

    private static PrototypeRunResult FullParty(string fixture, ulong seed) =>
        PrototypeScenario.Run(LoadFixture(fixture, seed), PrototypeTuning.SessionTicks);

    /// <summary>
    /// A shipped journal, optionally replayed under another seed. The commands are
    /// read off the file rather than reconstructed here, so a change to a fixture
    /// reaches these checks; the seed is the one thing overridden, because the
    /// slice is read on a party the shipped seed does not produce
    /// (<see cref="RouteSeed"/>).
    /// </summary>
    private static PrototypeCommandLog LoadFixture(string name, ulong seed)
    {
        var document = PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "scenarios",
            "prototype1",
            $"{name}.commands.v2.json"));
        return document.Seed == seed ? document : document with { Seed = seed };
    }

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

        throw new InvalidOperationException("The repository root was not found.");
    }

    private static PrototypeSnapshot PartyUntil(
        string fixture,
        ulong seed,
        Func<PrototypeWorld, bool> stop)
    {
        var world = new PrototypeWorld(LoadFixture(fixture, seed));
        while (!world.IsComplete)
        {
            world.Step();
            if (stop(world))
            {
                break;
            }
        }

        return world.GetSnapshot();
    }

    /// <summary>
    /// Every tile every raider of one party ever stood on, plus the party's final
    /// state. The walk starts at the first wave, because no raider exists before
    /// it and a snapshot a tick is a snapshot too many otherwise.
    /// </summary>
    private static (Dictionary<int, HashSet<GridPoint>> Visits, PrototypeSnapshot State) RaiderRoutes(
        string fixture,
        ulong seed)
    {
        var world = new PrototypeWorld(LoadFixture(fixture, seed));
        var visits = new Dictionary<int, HashSet<GridPoint>>();
        while (!world.IsComplete && world.CurrentTick < PrototypeTuning.FirstRaidTick)
        {
            world.Step();
        }

        while (!world.IsComplete)
        {
            world.Step();
            foreach (var raider in world.GetSnapshot().Raiders)
            {
                if (!visits.TryGetValue(raider.Id, out var tiles))
                {
                    tiles = [];
                    visits[raider.Id] = tiles;
                }

                tiles.Add(raider.Position);
            }
        }

        return (visits, world.GetSnapshot());
    }
}
