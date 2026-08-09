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
    /// Where every raider is walking to. It is read out of the authored layout
    /// rather than out of <c>PrototypeMap</c>, which is internal: the raiders go
    /// to the first larder tile in reading order, which is what
    /// <c>PrototypeLayout.Read</c>'s own docstring says the order is for.
    /// </summary>
    private static GridPoint FirstLarderTile => PrototypeLayout.Read('L')[0];

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
    /// </summary>
    [Fact]
    public void A_returning_raider_walks_round_the_place_it_was_hit_hardest()
    {
        var (visits, state) = RaiderRoutes(ShippedFixture, RouteSeed);

        var avoiders = state.Raiders
            .Where(raider =>
                raider.ReturnedFromWave is not null &&
                raider.RememberedPlace is { } place &&
                place.Place != FirstLarderTile)
            .ToArray();
        Assert.NotEmpty(avoiders);

        foreach (var raider in avoiders)
        {
            var remembered = raider.RememberedPlace!.Place;

            // Before: the body that carried this name last time stood there. It has
            // to have — the tile is where it was hit — and asserting it is what
            // makes the "after" half a change rather than an absence.
            var previous = state.Raiders.Single(other =>
                other.Name == raider.Name && other.Wave == raider.ReturnedFromWave!.Value);
            Assert.Contains(remembered, visits[previous.Id]);

            // After: never again.
            Assert.DoesNotContain(remembered, visits[raider.Id]);

            // And he did get where he was going, so the memory took a road and not
            // the raid: he reached the larder or left through the gate.
            Assert.True(
                visits[raider.Id].Contains(FirstLarderTile) ||
                raider.Mode == RaiderMode.Escaped,
                $"{raider.Name} neither reached the larder nor left: avoidance must " +
                "not be able to strand a raider");
        }
    }

    /// <summary>
    /// The bound on the rule, and it is the shipped journal that needs it: the one
    /// survivor of <c>baseline</c> with a scar was hit on the larder tile itself.
    /// There is nothing to walk round when the remembered place is the objective,
    /// so the raider walks onto it — and that is the raider side of the bound
    /// Issue #171 put on a creature's memory.
    /// </summary>
    [Fact]
    public void A_memory_takes_away_a_road_and_never_the_objective()
    {
        var (visits, state) = RaiderRoutes(ShippedFixture, PrototypeTuning.DefaultSeed);

        var atTheObjective = state.Raiders
            .Where(raider =>
                raider.ReturnedFromWave is not null &&
                raider.RememberedPlace?.Place == FirstLarderTile)
            .ToArray();
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
