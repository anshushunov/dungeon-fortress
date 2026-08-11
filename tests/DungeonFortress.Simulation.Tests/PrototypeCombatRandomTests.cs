using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// The combat stream has to <b>move</b>. Issue #361: <c>DeterministicRandom</c>
/// is a mutable struct, and <c>PrototypeWorld._combatRandom</c> was declared
/// <c>readonly</c>. C# answers a mutating call on a readonly field of a struct
/// type with a defensive copy, so every draw advanced a throwaway and the field
/// itself never moved: <c>CombatJitter</c> returned one and the same number for
/// the whole party, and <c>T.raider_might_jitter</c> and <c>T.damage_jitter</c>
/// were dead settings.
///
/// <para><b>What these two checks are careful not to be.</b> «The jitter takes
/// more than one value somewhere» is green on a generator that is only partly
/// broken, and it is green for reasons that have nothing to do with the
/// generator — two blows differ because two fighters differ. So neither check
/// below looks at jitter values. The first looks at the <b>state of the field
/// itself</b> and says when it is obliged to have moved; the second says that
/// no field in the assembly is declared in the shape that stopped it moving.
/// Both are statements about immobility, which is what broke.</para>
/// </summary>
public sealed class PrototypeCombatRandomTests(ITestOutputHelper output)
{
    /// <summary>
    /// The combat stream advances on <b>every tick a blow lands</b>.
    ///
    /// <para>A raider's hit points fall in exactly one place —
    /// <c>PrototypeWorld.Combat.cs:193</c>, where the damage is
    /// <c>might + readiness/25 + CombatJitter(...)</c> — so «some raider has less
    /// hp than it had last tick» is an observable, snapshot-only witness that a
    /// draw was taken during that tick. The check pairs that witness with the
    /// private state of <c>_combatRandom</c> and demands that the state moved.
    /// It fails on the first such tick if the field is frozen, and it fails just
    /// as loudly for any future way of freezing it — a defensive copy taken by
    /// passing the field by value, a local snapshot of it, a re-seed.</para>
    ///
    /// <para>The final assertion is the guard against the check going hollow: a
    /// party in which nobody is ever hit would satisfy the implication
    /// vacuously, so the number of witnessing ticks is asserted to be positive
    /// and printed.</para>
    /// </summary>
    [Theory]
    [InlineData("baseline", 20_260_726UL)]
    [InlineData("baseline", 20_260_727UL)]
    [InlineData("prepared", 20_260_726UL)]
    public void The_combat_stream_advances_on_every_tick_a_blow_lands(string fixtureName, ulong seed)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var stateAtStart = CombatStreamState(world);
        var previousState = stateAtStart;
        var previousRaiderHp = RaiderHitPoints(world);
        var blowTicks = 0;
        var frozenTicks = new List<int>();

        for (var step = 0; step < PrototypeTuning.SessionTicks && !world.IsComplete; step++)
        {
            world.Step();
            var state = CombatStreamState(world);
            var raiderHp = RaiderHitPoints(world);

            var struck = raiderHp.Any(entry =>
                previousRaiderHp.TryGetValue(entry.Key, out var before) && entry.Value < before);
            if (struck)
            {
                blowTicks++;
                if (state == previousState)
                {
                    frozenTicks.Add(world.CurrentTick);
                }
            }

            previousState = state;
            previousRaiderHp = raiderHp;
        }

        Assert.True(
            frozenTicks.Count == 0,
            $"{fixtureName}/{seed}: a raider lost hit points on {frozenTicks.Count} tick(s) " +
            $"without the combat stream advancing — first at tick " +
            $"{(frozenTicks.Count == 0 ? -1 : frozenTicks[0])}. The state of _combatRandom stood " +
            $"at 0x{previousState:X16} while a damage roll was being taken, which is the whole of " +
            "Issue #361: the draw is served from a defensive copy and the field never moves.");

        Assert.True(
            blowTicks > 0,
            $"{fixtureName}/{seed}: no raider lost hit points in the whole party, so this check " +
            "proved nothing. Either the fixture no longer reaches a raid or combat no longer " +
            "damages raiders; in both cases the witness has to be rebuilt before the assertion " +
            "above means anything again.");

        var jitters = RaiderMightJitters(world);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{fixtureName}/{seed}: blowTicks={blowTicks} frozenTicks={frozenTicks.Count} " +
            $"stateAtStart=0x{stateAtStart:X16} stateAtEnd=0x{previousState:X16} " +
            $"raiders={jitters.Count} distinctMightJitters=" +
            $"[{string.Join(", ", jitters.Distinct().Order())}]"));
    }

    /// <summary>
    /// Two runs of one seed give one canonical document, byte for byte, over a
    /// <b>whole party</b> — criterion 1 of the merged slice #333+#336, and the
    /// first thing it asks for.
    ///
    /// <para><b>Why it is not the check that already existed.</b>
    /// <c>PrototypeScenarioTests.Replay_from_loaded_command_log_is_byte_identical</c>
    /// runs to <c>FirstRaidTick + 1</c>, which is one tick into the first wave's
    /// arrival: no blow has landed, no jitter has been drawn, and the combat
    /// stream has never moved. It is a statement about the economy and it has
    /// never been able to say anything about the fight. Widening the spread of
    /// damage is exactly the change that would break reproducibility if the draw
    /// ever came from outside the seed, so the claim has to be made where the
    /// draws are.</para>
    ///
    /// <para>Three seeds and the fixture that fights hardest, compared on
    /// <see cref="PrototypeRunResult.CanonicalJson"/> rather than on the checksum:
    /// a checksum equal by accident is a thing that can happen and a document
    /// equal by accident is not.</para>
    /// </summary>
    [Theory]
    [InlineData("baseline", 20_260_726UL)]
    [InlineData("prepared", 20_260_727UL)]
    [InlineData("prepared", 20_260_728UL)]
    public void A_whole_party_of_one_seed_replays_byte_for_byte(string fixtureName, ulong seed)
    {
        var first = PrototypeScenario.Run(
            LoadFixture(fixtureName) with { Seed = seed }, PrototypeTuning.SessionTicks);
        var again = PrototypeScenario.Run(
            LoadFixture(fixtureName) with { Seed = seed }, PrototypeTuning.SessionTicks);

        // The guard against the check going hollow: a party that never reached a
        // fight would replay identically for reasons that have nothing to do with
        // the combat stream.
        Assert.True(
            first.State.SessionResult.RaidersDowned > 0,
            $"{fixtureName}/{seed} put down no raider at all, so this replay proves nothing " +
            "about the combat stream. Point it at a fixture that fights before trusting it.");

        Assert.Equal(first.CanonicalJson, again.CanonicalJson);
        Assert.Equal(first.Checksum, again.Checksum);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{fixtureName}/{seed}: {first.CanonicalJson.Length} bytes identical, " +
            $"checksum {first.Checksum}, raidersDowned={first.State.SessionResult.RaidersDowned}"));
    }

    /// <summary>
    /// The jitter every raider of the party was rolled with, read back off the
    /// published snapshot rather than off the generator: a raider's might is
    /// <c>wave.RaiderMight + jitter</c>, plus
    /// <see cref="PrototypeTuning.ReturningRaiderMightBonus"/> for one that has
    /// been here before (<c>PrototypeWorld.ReturningRaiders.cs:95-117</c>), and
    /// all three terms are in the snapshot. This is criterion 2 of Issue #361,
    /// and it is deliberately <b>reported</b> and not asserted: «the values
    /// differ» is green on a generator that is only partly stuck, so the
    /// assertions of this test are the ones above, about the state itself.
    /// </summary>
    private static IReadOnlyList<int> RaiderMightJitters(PrototypeWorld world)
    {
        var state = world.GetSnapshot();
        var mightOfWave = state.Waves.ToDictionary(wave => wave.Number, wave => wave.RaiderMight);
        return state.Raiders
            .Where(raider => mightOfWave.ContainsKey(raider.Wave))
            .Select(raider => raider.Might - mightOfWave[raider.Wave] -
                (raider.ReturnedFromWave is null ? 0 : PrototypeTuning.ReturningRaiderMightBonus))
            .ToList();
    }

    /// <summary>
    /// The shape that caused Issue #361 is not present anywhere in the
    /// simulation assembly: no field is both <c>readonly</c> and of a value type
    /// that has mutable state.
    ///
    /// <para>This is the half of the fix that survives the next field. Removing
    /// <c>readonly</c> from one declaration repairs one stream; this says the
    /// class of error cannot come back silently, and it is stated over metadata
    /// so it covers fields nobody thought to write a behavioural check for. A
    /// value type counts as immutable when the compiler marked it
    /// <c>IsReadOnlyAttribute</c> — which is what <c>readonly record struct</c>
    /// and <c>readonly struct</c> emit — or when every instance field of it is
    /// itself init-only.</para>
    ///
    /// <para>Compiler-generated types are skipped on purpose: closures, iterator
    /// and async state machines are the compiler's shapes rather than ours, and
    /// nobody can fix a finding in them.</para>
    /// </summary>
    [Fact]
    public void No_readonly_field_of_the_simulation_holds_a_mutable_struct()
    {
        var assembly = typeof(PrototypeWorld).Assembly;
        var inspected = 0;
        var findings = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                continue;
            }

            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly))
            {
                if (field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                {
                    continue;
                }

                var fieldType = field.FieldType;
                if (!fieldType.IsValueType || fieldType.IsEnum || fieldType.IsPrimitive)
                {
                    continue;
                }

                inspected++;
                if (field.IsInitOnly && IsMutableValueType(fieldType))
                {
                    findings.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{type.FullName}.{field.Name} : {fieldType.FullName}"));
                }
            }
        }

        Assert.True(inspected > 0, "no value-typed field was inspected, so this check proved nothing.");

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"inspected {inspected} value-typed field(s) in {assembly.GetName().Name}");
        foreach (var finding in findings)
        {
            report.AppendLine(finding);
        }

        output.WriteLine(report.ToString());

        Assert.True(
            findings.Count == 0,
            "a readonly field of a mutable value type freezes that value: every mutating call on " +
            "it is served from a defensive copy and the field itself never changes. Issue #361 is " +
            "one of these. Found:\n" + string.Join("\n", findings));
    }

    /// <summary>
    /// The private state word of <c>PrototypeWorld._combatRandom</c>. Read
    /// through reflection on purpose: the point of the check is that the field
    /// stays a private implementation detail and is still obliged to move, and
    /// opening it up for the test would change the very declaration under
    /// examination.
    /// </summary>
    private static ulong CombatStreamState(PrototypeWorld world)
    {
        var field = typeof(PrototypeWorld).GetField(
            "_combatRandom",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "PrototypeWorld no longer has a _combatRandom field; this check has to be pointed " +
                "at whatever replaced it before it can be trusted again.");
        var random = field.GetValue(world)
            ?? throw new InvalidOperationException("_combatRandom read back as null.");
        var stateField = random.GetType().GetField(
            "_state",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "DeterministicRandom no longer keeps its state in a _state field.");
        return (ulong)(stateField.GetValue(random)
            ?? throw new InvalidOperationException("_state read back as null."));
    }

    private static Dictionary<int, int> RaiderHitPoints(PrototypeWorld world) =>
        world.GetSnapshot().Raiders.ToDictionary(raider => raider.Id, raider => raider.Hp);

    private static bool IsMutableValueType(Type type)
    {
        if (type.IsDefined(typeof(IsReadOnlyAttribute), inherit: false))
        {
            return false;
        }

        return type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => !field.IsInitOnly);
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
