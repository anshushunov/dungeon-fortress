using System.Globalization;
using System.Text;
using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Where a creature is hurt, and not only how badly — Issue #409, the first half
/// of the promise section 6.13 of <c>docs/product/PITCH.md</c> makes: «попадание
/// даёт следствие вместо вычитания здоровья… главное здесь не бой, а то, что
/// травма переживает бой».
///
/// <para><b>What this file measures and what it does not.</b> It measures the
/// model: that the four parts exist, that blows reach all four, that the summary
/// the rest of the simulation reads is the worst of them and can never drift from
/// them, and that a wound outlives the wave it was taken in. The behavioural
/// consequence of each part — a weaker blow from a hurt arm, a slower walk on a
/// hurt leg, a lost tick from a hurt head, a thinner readiness from a hurt torso
/// — is measured where it happens, in the files named beside each check.</para>
///
/// <para><b>Everything is read from <c>GetSnapshot()</c> and from the canonical
/// event log</b>, for the same reason
/// <see cref="PrototypeMusterParticipationTests"/> gives: a wound is decided in
/// the raiders' phase and the creature goes on to act in the phases after it, so
/// <see cref="PrototypeCreatureSnapshot.LastDecision"/> no longer holds the wound
/// by the end of the tick. The journal does.</para>
/// </summary>
public sealed class PrototypeLocalisedInjuryTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    /// <summary>The seed the owner played on 2026-08-12, kept beside the matrix.</summary>
    private const ulong PlaytestSeed = 20_260_729UL;

    private static readonly string[] Fixtures = ["baseline", "prepared", "neglected"];

    private static IReadOnlyList<Measurement> Matrix => MatrixMeasurements.Value;

    private static readonly Lazy<IReadOnlyList<Measurement>> MatrixMeasurements =
        new(() =>
        [
            .. Fixtures.SelectMany(_ => MatrixSeeds, (fixtureName, seed) => Measure(fixtureName, seed)),
            .. Fixtures.Select(fixtureName => Measure(fixtureName, PlaytestSeed)),
        ]);

    /// <summary>
    /// The report criterion 1 of Issue #409 asks for: every part, how often it was
    /// hurt, at which severity, and on which parties. It is a report and not a
    /// threshold — the assertions are the checks below it.
    /// </summary>
    [Fact]
    public void Report_localised_injury_over_the_seed_matrix()
    {
        var report = new StringBuilder();
        foreach (var measurement in Matrix)
        {
            report.AppendLine(measurement.ToString());
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"{Pooled(Matrix)}");
        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// Criterion 1: all four parts are reachable, and none of them is a branch the
    /// shipped journals never take. A part that never happens is a part the player
    /// will never learn to read, which is the whole budget argument of the pitch
    /// turned into a check.
    /// </summary>
    [Fact]
    public void Every_one_of_the_four_parts_is_hurt_somewhere_on_the_shipped_journals()
    {
        var missing = BodyParts.All
            .Where(part => Matrix.Sum(run => run.WoundsByPart[(int)part]) == 0)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"Parts never hurt over the matrix: {string.Join(", ", missing)}." +
            Environment.NewLine + Detail());
    }

    /// <summary>
    /// The derivation invariant, checked on every creature of every published tick
    /// of every party: <c>injury</c> is the worst entry of <c>injuries</c>, and a
    /// creature with an empty list is whole.
    ///
    /// <para>This is the check that lets fifteen call sites keep reading the scalar
    /// without any of them being re-decided. If it ever reds, the summary and the
    /// localisation have drifted apart and every one of those call sites is
    /// reading something the player is not being shown.</para>
    /// </summary>
    [Fact]
    public void The_summary_injury_is_always_the_worst_of_the_localised_ones()
    {
        var wrong = Matrix.SelectMany(run => run.Drift).ToArray();
        Assert.True(wrong.Length == 0, string.Join(Environment.NewLine, wrong.Take(10)));
    }

    /// <summary>
    /// The same list, never repeating a part. Two entries for one part would make
    /// "which arm is it" a question the document answers twice and differently.
    /// </summary>
    [Fact]
    public void No_creature_carries_the_same_part_twice()
    {
        var duplicated = Matrix.SelectMany(run => run.Duplicates).ToArray();
        Assert.True(duplicated.Length == 0, string.Join(Environment.NewLine, duplicated.Take(10)));
    }

    /// <summary>
    /// Criterion 3 of Issue #409, in the form the criterion states: the share of
    /// creatures who walk into the next wave still carrying a localised wound. The
    /// criterion names zero on every party as a failure — a wound that does not
    /// outlive its wave is the mechanic not existing.
    /// </summary>
    [Fact]
    public void A_wound_outlives_the_wave_it_was_taken_in()
    {
        var carried = Matrix.Sum(run => run.CarriedIntoNextWave);
        var entries = Matrix.Sum(run => run.EntriesIntoALaterWave);

        output.WriteLine(
            $"CARRIED entries={entries} carrying={carried} " +
            $"share={(entries == 0 ? "-" : $"{carried * 100 / entries}%")}");

        Assert.True(entries > 0, "No party ran a second wave; the question cannot be asked.");
        Assert.True(
            carried > 0,
            "Nobody entered a later wave still carrying a localised wound: the injury does not survive the fight."
            + Environment.NewLine + Detail());
    }

    /// <summary>
    /// The canonical document carries the localisation, in part order, beside the
    /// summary. Read off the bytes rather than off the record, because the bytes
    /// are what a replay and a checksum are made of.
    /// </summary>
    [Fact]
    public void The_canonical_document_publishes_the_localisation_in_part_order()
    {
        var world = new PrototypeWorld(LoadFixture("baseline") with { Seed = PlaytestSeed });
        while (!world.IsComplete &&
               world.GetSnapshot().Creatures.All(creature => creature.Injuries.Count < 2))
        {
            world.Step();
        }

        var state = world.GetSnapshot();
        var wounded = state.Creatures.FirstOrDefault(creature => creature.Injuries.Count >= 2);
        Assert.True(wounded is not null, "No creature reached two hurt parts in a whole party.");

        using var document = JsonDocument.Parse(PrototypeCanonical.Serialize(state));
        var published = document.RootElement
            .GetProperty("creatures")
            .EnumerateArray()
            .Single(creature => creature.GetProperty("id").GetInt32() == wounded!.Id);
        var parts = published.GetProperty("injuries")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("part").GetString()!)
            .ToArray();

        Assert.Equal(
            wounded!.Injuries.OrderBy(injury => injury.Part)
                .Select(injury => Camel(injury.Part.ToString()))
                .ToArray(),
            parts);
        Assert.Equal(
            Camel(wounded.Injury.ToString()),
            published.GetProperty("injury").GetString());
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static string Detail() =>
        string.Join(Environment.NewLine, Matrix.Select(measurement => measurement.ToString()))
        + Environment.NewLine + Pooled(Matrix);

    private static string Pooled(IReadOnlyList<Measurement> runs)
    {
        var byPart = BodyParts.All
            .Select(part => $"{Camel(part.ToString())}={runs.Sum(run => run.WoundsByPart[(int)part])}");
        return
            $" POOLED wounds={runs.Sum(run => run.Wounds)} byPart=[{string.Join(',', byPart)}] " +
            $"light={runs.Sum(run => run.Light)} heavy={runs.Sum(run => run.Heavy)} " +
            $"entriesIntoALaterWave={runs.Sum(run => run.EntriesIntoALaterWave)} " +
            $"carrying={runs.Sum(run => run.CarriedIntoNextWave)}";
    }

    private static Measurement Measure(string fixtureName, ulong seed)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var tally = new Measurement(fixtureName, seed);
        var previous = world.GetSnapshot();
        Observe(previous, previous.Tick, tally);

        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            if (current.Tick == previous.Tick)
            {
                previous = current;
                continue;
            }

            Observe(current, current.Tick - 1, tally);
            previous = current;
        }

        return tally;
    }

    private static void Observe(PrototypeSnapshot state, int acted, Measurement tally)
    {
        foreach (var creature in state.Creatures)
        {
            var worst = creature.Injuries.Count == 0
                ? InjuryKind.None
                : creature.Injuries.Max(injury => injury.Severity);
            if (worst != creature.Injury)
            {
                tally.Drift.Add(
                    $"{tally.Name} t{state.Tick} #{creature.Id}: injury={creature.Injury} " +
                    $"but worst of [{string.Join(',', creature.Injuries.Select(item => $"{item.Part}:{item.Severity}"))}] is {worst}");
            }

            if (creature.Injuries.Select(injury => injury.Part).Distinct().Count() != creature.Injuries.Count)
            {
                tally.Duplicates.Add(
                    $"{tally.Name} t{state.Tick} #{creature.Id}: " +
                    string.Join(',', creature.Injuries.Select(item => $"{item.Part}:{item.Severity}")));
            }
        }

        // A wound taken: read off the journal entry the localisation writes, so a
        // count of them is a count of blows that landed somewhere and not of ticks
        // a creature spent hurt.
        foreach (var entry in state.Events)
        {
            if (entry.LastTick != acted || entry.ReasonCode != "injury_localised")
            {
                continue;
            }

            var part = entry.Details["part"];
            var severity = (InjuryKind)entry.Details["severity"];
            tally.Wounds += entry.Repeats;
            tally.WoundsByPart[part] += entry.Repeats;
            if (severity == InjuryKind.Heavy)
            {
                tally.Heavy += entry.Repeats;
            }
            else
            {
                tally.Light += entry.Repeats;
            }
        }

        // Criterion 3 is asked exactly where it is stated: on the tick a wave
        // arrives, of everybody still on their feet.
        foreach (var wave in state.Waves.Where(wave => wave.ArriveTick == acted && wave.Number > 1))
        {
            foreach (var creature in state.Creatures.Where(creature => creature.Mode != CreatureMode.Downed))
            {
                tally.EntriesIntoALaterWave++;
                if (creature.Injuries.Count > 0)
                {
                    tally.CarriedIntoNextWave++;
                }
            }
        }
    }

    private sealed class Measurement(string fixtureName, ulong seed)
    {
        public string Name { get; } = $"{fixtureName}/{seed}";

        public int[] WoundsByPart { get; } = new int[BodyParts.Count];

        public int Wounds { get; set; }

        public int Light { get; set; }

        public int Heavy { get; set; }

        public int EntriesIntoALaterWave { get; set; }

        public int CarriedIntoNextWave { get; set; }

        public List<string> Drift { get; } = [];

        public List<string> Duplicates { get; } = [];

        public override string ToString()
        {
            var byPart = BodyParts.All
                .Select(part => $"{Camel(part.ToString())}={WoundsByPart[(int)part]}");
            return
                $"  INJURY {Name} wounds={Wounds} byPart=[{string.Join(',', byPart)}] " +
                $"light={Light} heavy={Heavy} " +
                $"laterWaveEntries={EntriesIntoALaterWave} carrying={CarriedIntoNextWave}";
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

        throw new InvalidOperationException("Repository root not found.");
    }
}
