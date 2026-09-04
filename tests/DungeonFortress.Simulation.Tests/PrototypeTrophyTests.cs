using DungeonFortress.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// docs/design/TROPHY_WEAPON.md. Every check here is a party played out on the
/// shipped fixtures, because the simulation has no seam for placing a weapon
/// by hand and is not going to get one: the rules are proved on what the
/// domain actually does.
/// </summary>
public sealed class PrototypeTrophyTests(ITestOutputHelper output)
{
    private const string Baseline = "baseline";
    private const string Prepared = "prepared";
    private static readonly ulong[] MatrixSeeds = [20_260_726, 20_260_727, 20_260_728];

    [Fact]
    public void A_fresh_world_carries_no_weapon_anywhere()
    {
        var state = new PrototypeWorld(LoadFixture(Baseline, PrototypeTuning.DefaultSeed)).GetSnapshot();
        output.WriteLine($"tick {state.Tick}: {state.Creatures.Count} creatures, {state.LooseWeapons.Count} loose weapons");

        Assert.Empty(state.LooseWeapons);
        Assert.All(state.Creatures, creature => Assert.Null(creature.Weapon));
    }

    // ---- helpers shared by every test of this file ----

    internal static PrototypeCommandLog LoadFixture(string name, ulong seed)
    {
        var document = PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "scenarios",
            "prototype1",
            $"{name}.commands.v2.json"));
        return document.Seed == seed ? document : document with { Seed = seed };
    }

    /// <summary>
    /// Plays one party tick by tick and hands every consecutive pair of
    /// snapshots to <paramref name="visit"/>. The pair is what a rule about a
    /// transition ("when X becomes Y, Z is true") needs; a single end state is
    /// not enough because weapons are picked up and dropped again.
    /// </summary>
    internal static void Walk(
        string fixture,
        ulong seed,
        Action<PrototypeSnapshot, PrototypeSnapshot> visit)
    {
        var world = new PrototypeWorld(LoadFixture(fixture, seed));
        var before = world.GetSnapshot();
        while (!world.IsComplete)
        {
            world.Step();
            var after = world.GetSnapshot();
            visit(before, after);
            before = after;
        }
    }

    internal static IEnumerable<(string Fixture, ulong Seed)> Matrix()
    {
        foreach (var fixture in new[] { Baseline, Prepared })
        {
            foreach (var seed in MatrixSeeds)
            {
                yield return (fixture, seed);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DungeonFortress.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("DungeonFortress.sln not found above " + AppContext.BaseDirectory);
    }
}
