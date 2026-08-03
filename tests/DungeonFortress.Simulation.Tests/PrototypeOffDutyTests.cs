using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// What a creature does when the fight is over and there is nothing for it to do
/// (Issue #201).
///
/// <para><b>The sentence being tested.</b> The owner's decision of 2026-08-03:
/// «Существо без работы должно возвращаться куда-то — в покои или на пост, — а не
/// стоять там, где кончилась драка.» Two halves live in that sentence and this
/// file states them separately, because each can be broken without the
/// other:</para>
///
/// <list type="bullet">
/// <item><description><b>it leaves</b> — a member of a wave's cohort that ends up
/// without work does not spend the window standing on the ground the fight was
/// fought on;</description></item>
/// <item><description><b>it leaves to somewhere that costs nothing</b> — the tile
/// it walks to is in the quarters and is never a bunk, because a body parked on a
/// bunk is a bunk a tired creature cannot lie down on.</description></item>
/// </list>
///
/// <para><b>Why the trigger is the end of a fight and not idleness in general.</b>
/// Measured, not chosen: a first version fired on any idle creature, and the
/// ordinary <c>waiting_stock_sufficient</c> pause is frequent enough that the
/// party walked to the far corner and back all session —
/// <c>prepared/20260726</c> ended `fallen` at t2032 with an average satiety of 0,
/// the food chain lost to the commute. The third test below pins that boundary
/// down: peacetime idleness is left alone.</para>
///
/// <para><b>What this file does not claim.</b> It does not fix the jam — bodies
/// blocking each other is cell occupancy, Issue #76, left on slice 6 by the
/// owner. It does not claim to move creatures that stand because they cannot
/// reach their zone (<c>refused_zone_unreachable</c>); those move too, but the
/// reason they were standing is a different one.</para>
/// </summary>
public sealed class PrototypeOffDutyTests(ITestOutputHelper output)
{
    /// <summary>How long after a wave ends the cohort is watched. The same window
    /// <see cref="PrototypePostCombatDispersalTests"/> uses, so the numbers of the
    /// two files are comparable.</summary>
    private const int Window = 60;

    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    private static readonly string[] Fixtures = ["baseline", "prepared"];

    public static TheoryData<string, ulong> Matrix()
    {
        var data = new TheoryData<string, ulong>();
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                data.Add(fixtureName, seed);
            }
        }

        return data;
    }

    /// <summary>
    /// The first half: nobody spends a whole post-combat window standing on the
    /// spot without work.
    ///
    /// <para>"Without work" is read from the snapshot, not from the journal: a
    /// creature counts only if it held no job on any tick of the window. One that
    /// was given something to do is out of scope of the issue — it is not idle,
    /// it is busy — and one that never moved because it was carrying a job to a
    /// tile it could not reach is Issue #76, not this rule.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void A_creature_left_without_work_after_a_fight_does_not_stand_where_it_was_fought(
        string fixtureName,
        ulong seed)
    {
        var measurement = Measure(fixtureName, seed);

        Assert.True(
            measurement.StoodStillWithoutWork == 0,
            $"{fixtureName}/{seed}: {measurement.StoodStillWithoutWork} creature(s) spent a whole " +
            $"{Window}-tick window after a wave standing on the tile the fight left them on, with " +
            "no job on any tick of it. That is the symptom Issue #201 was opened for: the group " +
            $"looks stuck. {measurement}");
    }

    /// <summary>
    /// The second half: going off duty never costs a bunk.
    ///
    /// <para>The bunks are read from <see cref="PrototypeLayout.Rows"/> — the
    /// authored map — rather than from the simulation's own tile lookup, so the
    /// assertion does not restate the code it is checking. If the rule ever
    /// forgets the exclusion, the target of a <c>chosen_off_duty</c> event lands
    /// on a `q` of the map and this fails.</para>
    ///
    /// <para>It is not a theoretical worry. Without the exclusion twelve tests of
    /// this project went red at once, among them the personal-rest contract and
    /// «a party that wins its fights does not end it starving»: idle bodies were
    /// standing on the beds and <see cref="PrototypeWorld"/> refuses a step onto
    /// an occupied tile.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void Going_off_duty_never_takes_a_bunk(string fixtureName, ulong seed)
    {
        var measurement = Measure(fixtureName, seed);

        Assert.True(
            measurement.OffDutyOntoABunk == 0,
            $"{fixtureName}/{seed}: {measurement.OffDutyOntoABunk} off-duty departure(s) aimed at " +
            "a bunk. A creature standing there occupies the tile a Rest job needs, and a step " +
            $"onto an occupied tile is refused. {measurement}");
    }

    /// <summary>
    /// The assertion above only means something while the rule fires somewhere,
    /// and "somewhere" is the matrix rather than every cell of it.
    ///
    /// <para>It is deliberately not per fixture: on <c>prepared</c> the rule
    /// almost never fires, and that is correct behaviour rather than a gap. That
    /// fixture paints a Watch zone and raises the Watch priority to 3, so when a
    /// wave ends there is standing work for everybody — the matching hands it out
    /// and nobody is off duty. The whole rule is «when there is no work», and on
    /// <c>prepared</c> there is.</para>
    /// </summary>
    [Fact]
    public void The_matrix_still_sends_somebody_off_duty_to_measure()
    {
        var departures = Fixtures
            .SelectMany(fixture => MatrixSeeds.Select(seed => Measure(fixture, seed)))
            .Sum(measurement => measurement.OffDutyDepartures);

        Assert.True(
            departures > 0,
            $"the rule never fired anywhere on the matrix ({departures} departures), so every " +
            "assertion about where creatures go passed for the wrong reason.");
    }

    /// <summary>
    /// The boundary: the rule belongs to the end of a fight, and peacetime
    /// idleness is not its business.
    ///
    /// <para>Before the first wave arrives the domain is at its busiest and its
    /// creatures still pause constantly between jobs. Not one of those pauses may
    /// produce a departure — that version of the rule was measured and it starved
    /// the party (see the class docstring).</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void Idleness_before_the_first_fight_sends_nobody_anywhere(
        string fixtureName,
        ulong seed)
    {
        var measurement = Measure(fixtureName, seed);

        Assert.True(
            measurement.DeparturesBeforeTheFirstWaveEnded == 0,
            $"{fixtureName}/{seed}: {measurement.DeparturesBeforeTheFirstWaveEnded} departure(s) " +
            "happened before the first wave was over. The rule is tied to the end of a fight on " +
            "purpose: fired on ordinary idleness it makes the party walk instead of work, and the " +
            $"food chain loses. {measurement}");
    }

    /// <summary>
    /// The numbers themselves, printed rather than asserted, for the "before and
    /// after" Issue #201 asks for in its second criterion.
    /// </summary>
    [Fact]
    public void Report_off_duty_over_the_seed_matrix()
    {
        var report = new StringBuilder();
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                report.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{fixtureName}/{seed} {Measure(fixtureName, seed)}"));
            }
        }

        output.WriteLine(report.ToString());
    }

    private static OffDutyMeasurement Measure(string fixtureName, ulong seed)
    {
        var bunks = BunkTiles();
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var previous = world.GetSnapshot();
        var departures = 0;
        var ontoABunk = 0;
        var beforeTheFirstWaveEnded = 0;
        var firstWaveEndTick = int.MaxValue;
        var stuck = new List<string>();

        // Per wave: the cohort read from the tick before the wave ended, then the
        // window that follows. Positions and jobs come from the snapshot, which
        // does not coalesce; departures come from the journal by LastTick, the way
        // every other measurement in this project reads it.
        var open = new List<(
            int EndTick,
            Dictionary<int, GridPoint> Where,
            HashSet<int> Moved,
            HashSet<int> HadWork,
            HashSet<int> OffItsFeet)>();
        var stoodStillWithoutWork = 0;

        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            var acted = current.Tick - 1;

            foreach (var @event in current.Events.Where(item => item.LastTick == acted))
            {
                if (@event.ReasonCode != "chosen_off_duty")
                {
                    continue;
                }

                departures++;
                if (@event.Target is { } target && bunks.Contains(target))
                {
                    ontoABunk++;
                }

                if (acted < firstWaveEndTick)
                {
                    beforeTheFirstWaveEnded++;
                }
            }

            foreach (var wave in current.Waves.Where(item => item.EndTick == acted))
            {
                firstWaveEndTick = Math.Min(firstWaveEndTick, wave.EndTick!.Value);
                var cohort = previous.Creatures
                    .Where(creature => creature.Mode == CreatureMode.Fighting)
                    .ToDictionary(creature => creature.Id, creature => creature.Position);
                if (cohort.Count > 0)
                {
                    open.Add((acted, cohort, [], [], []));
                }
            }

            foreach (var window in open)
            {
                foreach (var creature in current.Creatures)
                {
                    if (!window.Where.TryGetValue(creature.Id, out var startedAt))
                    {
                        continue;
                    }

                    if (creature.Position != startedAt)
                    {
                        window.Moved.Add(creature.Id);
                    }

                    if (creature.CurrentJobId is not null)
                    {
                        window.HadWork.Add(creature.Id);
                    }

                    // A creature that went down in the fight, or is still mending
                    // the wound it took there, is not "idle": it is out of action,
                    // and no rule about work sends it anywhere. Counting it as
                    // stuck would make this test fail for a reason Issue #201 does
                    // not own — measured on baseline/20260728, where exactly two
                    // such creatures stood out the window.
                    if (creature.Mode == CreatureMode.Downed ||
                        creature.Injury != InjuryKind.None)
                    {
                        window.OffItsFeet.Add(creature.Id);
                    }
                }
            }

            foreach (var window in open.Where(item => acted >= item.EndTick + Window).ToArray())
            {
                foreach (var id in window.Where.Keys.Where(id =>
                    !window.Moved.Contains(id) &&
                    !window.HadWork.Contains(id) &&
                    !window.OffItsFeet.Contains(id)))
                {
                    var creature = current.Creatures.Single(item => item.Id == id);
                    stuck.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"#{id} at ({creature.Position.X},{creature.Position.Y}) " +
                        $"mode={creature.Mode} last={creature.LastDecision.ReasonCode}"));
                }

                stoodStillWithoutWork += window.Where.Keys
                    .Count(id =>
                        !window.Moved.Contains(id) &&
                        !window.HadWork.Contains(id) &&
                        !window.OffItsFeet.Contains(id));
                open.Remove(window);
            }

            previous = current;
        }

        // A party can end inside an open window, and those windows are **not**
        // counted: the creature had not been watched for the whole 60 ticks, so
        // "stood still for the window" cannot be said about it. Counting them
        // measured the end of the party rather than the rule — on
        // baseline/20260728 exactly that produced two creatures whose window the
        // session outlived by a handful of ticks. How many were dropped is
        // reported instead of hidden, the way PrototypePostCombatDispersalTests
        // reports `windows` against `measured`.
        var unfinished = open.Count;

        return new OffDutyMeasurement(
            departures,
            ontoABunk,
            beforeTheFirstWaveEnded,
            stoodStillWithoutWork,
            unfinished,
            stuck);
    }

    /// <summary>
    /// The bunks of the authored map, read from the layout rather than from the
    /// simulation, so that this file states a fact about the map instead of
    /// repeating the code under test.
    /// </summary>
    private static HashSet<GridPoint> BunkTiles()
    {
        var rows = PrototypeLayout.Rows;
        var bunks = new HashSet<GridPoint>();
        for (var y = 0; y < rows.Count; y++)
        {
            for (var x = 0; x < rows[y].Length; x++)
            {
                if (rows[y][x] == 'q')
                {
                    bunks.Add(new GridPoint(x, y));
                }
            }
        }

        return bunks;
    }

    private static PrototypeCommandLog LoadFixture(string fixtureName) =>
        PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(), "scenarios", "prototype1", $"{fixtureName}.commands.v2.json"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "scenarios")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root with a scenarios directory not found.");
    }

    private sealed record OffDutyMeasurement(
        int OffDutyDepartures,
        int OffDutyOntoABunk,
        int DeparturesBeforeTheFirstWaveEnded,
        int StoodStillWithoutWork,
        int WindowsCutShortByTheEndOfTheParty,
        IReadOnlyList<string> Stuck)
    {
        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"departures={OffDutyDepartures} ontoABunk={OffDutyOntoABunk} " +
            $"beforeTheFirstWaveEnded={DeparturesBeforeTheFirstWaveEnded} " +
            $"stoodStillWithoutWork={StoodStillWithoutWork} " +
            $"windowsCutShort={WindowsCutShortByTheEndOfTheParty} [{string.Join("; ", Stuck)}]");
    }
}
