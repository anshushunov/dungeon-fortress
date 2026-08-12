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
    /// Criterion 2 for the arm: «по руке — роняет оружие» is a consequence and not
    /// a note in the journal. A creature whose arm is hurt lands a measurably
    /// weaker blow than one whose arm is whole, over every blow of every shipped
    /// party.
    ///
    /// <para>The threshold is stated as a direction plus a floor rather than as a
    /// number, per ADR 0010: the size of the gap is tuning and may move, that
    /// there is a gap at all is what the mutant of
    /// <c>evidence/409-mutants.json</c> holds.</para>
    /// </summary>
    [Fact]
    public void A_hurt_arm_puts_less_of_its_own_strength_into_a_blow()
    {
        var pooled = new Ratio();
        foreach (var run in Matrix)
        {
            pooled.Add(true, run.WeaponPerMight.Hurt * run.WeaponPerMight.HurtSamples / 100, run.WeaponPerMight.HurtSamples);
            pooled.Add(false, run.WeaponPerMight.Whole * run.WeaponPerMight.WholeSamples / 100, run.WeaponPerMight.WholeSamples);
        }

        Assert.True(
            pooled.HurtSamples > 0,
            "No party landed a blow with a hurt arm, so the question cannot be asked.");

        output.WriteLine(
            $"ARM weaponPerMight hurt={pooled.Hurt / 100.0} whole={pooled.Whole / 100.0} " +
            $"(T.damage_might_weight = {PrototypeTuning.DamageMightWeight})");

        // Four fifths, and the number is measured on both sides rather than
        // chosen. With the consequence on, the hurt cohort reads 2.66 against 4.08
        // whole — a ratio of 0.65. With ArmLightMightPercent and
        // ArmHeavyMightPercent both set to 100 and nothing else touched, it reads
        // 4.14 against 4.08 — a ratio of 1.01, the gap gone entirely. A bar at 0.8
        // sits with room on both sides of that pair and is what the mutant of
        // evidence/409-mutants.json is red against.
        Assert.True(
            pooled.Hurt * 5 < pooled.Whole * 4,
            $"a hurt arm put {pooled.Hurt / 100.0} of weapon into a blow per point of might against " +
            $"{pooled.Whole / 100.0} whole — not the gap a dropped weapon makes." +
            Environment.NewLine + Detail());
    }

    /// <summary>
    /// Criterion 2 for the leg: «по ноге — хромает и не убегает». A creature with
    /// a hurt leg loses steps, and one whose leg is whole loses none at all.
    ///
    /// <para>The second half is the one that makes the mutant sharp. The rate for
    /// whole legs is not "small" — it is exactly zero, because nothing but a hurt
    /// leg can charge a step to the limp, so the check needs no threshold on that
    /// side and cannot be passed by a party that simply walks less.</para>
    /// </summary>
    [Fact]
    public void A_hurt_leg_loses_steps_and_a_whole_one_loses_none()
    {
        var pooled = new Ratio();
        foreach (var run in Matrix)
        {
            pooled.Add(true, run.Limp.Hurt * run.Limp.HurtSamples / 100, run.Limp.HurtSamples);
            pooled.Add(false, run.Limp.Whole * run.Limp.WholeSamples / 100, run.Limp.WholeSamples);
        }

        output.WriteLine(
            $"LEG stepsLostPerCreatureTick hurt={pooled.Hurt / 100.0} whole={pooled.Whole / 100.0} " +
            $"hurtTicks={pooled.HurtSamples} wholeTicks={pooled.WholeSamples}");

        Assert.True(pooled.HurtSamples > 0, "No creature of any shipped party spent a tick with a hurt leg.");
        Assert.True(
            pooled.Whole == 0,
            $"a whole leg lost {pooled.Whole / 100.0} steps a tick, which it cannot: only the limp " +
            "charges a step to a wound, so anything but zero here means the counter is being moved " +
            "by something that is not the leg." + Environment.NewLine + Detail());
        Assert.True(
            pooled.Hurt > 0,
            "a hurt leg lost no steps at all over the whole matrix: the limp is not there."
            + Environment.NewLine + Detail());
    }

    /// <summary>
    /// Criterion 2 for the head: «по голове — оглушён». A creature with a hurt
    /// head loses whole combat actions, and one whose head is whole loses none at
    /// all.
    ///
    /// <para>The quantity is the head's own and nothing else's, which is what the
    /// arm had to be rewritten to become. Raw output of a fight — blows landed,
    /// damage dealt — would move for any of the four wounds: a hurt arm takes
    /// weight off the blow, a hurt torso takes readiness off it, a hurt leg makes
    /// the fighter arrive later. Actions charged to the stun are charged by one
    /// place in the simulation, and that place asks about the head.</para>
    /// </summary>
    [Fact]
    public void A_hurt_head_loses_whole_actions_and_a_whole_one_loses_none()
    {
        var pooled = new Ratio();
        foreach (var run in Matrix)
        {
            pooled.Add(true, run.Stun.Hurt * run.Stun.HurtSamples / 100, run.Stun.HurtSamples);
            pooled.Add(false, run.Stun.Whole * run.Stun.WholeSamples / 100, run.Stun.WholeSamples);
        }

        output.WriteLine(
            $"HEAD actionsLostPerCreatureTick hurt={pooled.Hurt / 100.0} whole={pooled.Whole / 100.0} " +
            $"hurtTicks={pooled.HurtSamples} wholeTicks={pooled.WholeSamples}");

        Assert.True(pooled.HurtSamples > 0, "No creature of any shipped party spent a tick with a hurt head.");
        Assert.True(
            pooled.Whole == 0,
            $"a whole head lost {pooled.Whole / 100.0} actions a tick, which it cannot: only the stun " +
            "charges an action to a wound, so anything but zero here means the counter is being moved " +
            "by something that is not the head." + Environment.NewLine + Detail());
        Assert.True(
            pooled.Hurt > 0,
            "a hurt head lost no actions at all over the whole matrix: the stun is not there."
            + Environment.NewLine + Detail());
    }

    /// <summary>
    /// The stun takes the action whole, and this is what says so: on a tick a
    /// creature was stunned it struck nobody. Without it the check above would be
    /// satisfied by a counter that goes up beside a fight that carries on.
    /// </summary>
    [Fact]
    public void A_stunned_creature_strikes_nobody_on_the_tick_it_is_stunned()
    {
        var struck = new List<string>();
        var stunned = 0;
        foreach (var fixtureName in Fixtures)
        {
            var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = PlaytestSeed });
            var previous = world.GetSnapshot();
            while (!world.IsComplete)
            {
                world.Step();
                var current = world.GetSnapshot();
                if (current.Tick == previous.Tick)
                {
                    previous = current;
                    continue;
                }

                var acted = current.Tick - 1;
                foreach (var creature in current.Creatures)
                {
                    var was = previous.Creatures.FirstOrDefault(item => item.Id == creature.Id);
                    if (was is null || creature.ActionsLostToStun == was.ActionsLostToStun)
                    {
                        continue;
                    }

                    stunned++;
                    var attacked = current.Events.Any(entry =>
                        entry.CreatureId == creature.Id &&
                        entry.ReasonCode == "combat_attack" &&
                        entry.LastTick == acted);
                    if (attacked)
                    {
                        struck.Add($"{fixtureName}/{PlaytestSeed} t{acted} #{creature.Id}");
                    }
                }

                previous = current;
            }
        }

        output.WriteLine($"HEAD stunnedCreatureTicks={stunned} ofWhichAlsoStruck={struck.Count}");
        Assert.True(stunned > 0, "Nobody was stunned on the playtest seed; the question cannot be asked.");
        Assert.True(
            struck.Count == 0,
            "Stunned on the same tick it struck: " + string.Join(", ", struck.Take(10)));
    }

    /// <summary>
    /// Criterion 2 for the torso. The torso is the one part the pitch gives no
    /// sentence of its own, and what it does is what readiness always meant: a
    /// hurt body brings less of itself to everything. A creature with a hurt torso
    /// therefore reads a measurably lower readiness than one whose torso is whole.
    ///
    /// <para><b>Why this quantity is the torso's only after this slice and was
    /// nobody's before it.</b> Until Issue #409 readiness was charged off the
    /// summary <c>Injury</c> — the worst of the four parts — so this same split
    /// showed a gap for a creature whose torso was untouched and whose arm was
    /// gone. That is the trap the arm's own check fell into and had to be
    /// rewritten out of: a quantity that every wound moves cannot say which wound
    /// moved it. One line in <c>ComputeReadiness</c> is what makes it the torso's,
    /// and the mutant of <c>evidence/409-mutants.json</c> is what holds it
    /// there.</para>
    ///
    /// <para><b>The bar is measured on both sides rather than chosen.</b> A
    /// residual gap is expected with the consequence switched off, because a
    /// creature that has been opened up has usually also been running, fighting
    /// and going without supper — readiness carries satiety and rest as well. The
    /// numbers behind the bar are in the evidence file.</para>
    /// </summary>
    [Fact]
    public void A_hurt_torso_brings_less_of_itself_to_everything()
    {
        var pooled = new Ratio();
        foreach (var run in Matrix)
        {
            pooled.Add(true, run.Torso.HurtMean * run.Torso.HurtCount / 100, run.Torso.HurtCount);
            pooled.Add(false, run.Torso.WholeMean * run.Torso.WholeCount / 100, run.Torso.WholeCount);
        }

        output.WriteLine(
            $"TORSO readiness hurt={pooled.Hurt / 100.0} whole={pooled.Whole / 100.0} " +
            $"hurtTicks={pooled.HurtSamples} wholeTicks={pooled.WholeSamples} " +
            $"(T.torso_heavy_penalty = {PrototypeTuning.TorsoHeavyPenalty})");

        Assert.True(pooled.HurtSamples > 0, "No creature of any shipped party spent a tick with a hurt torso.");
        Assert.True(
            pooled.Hurt * 4 < pooled.Whole * 3,
            $"a hurt torso read {pooled.Hurt / 100.0} readiness against {pooled.Whole / 100.0} whole — " +
            "not the gap a wounded body makes." + Environment.NewLine + Detail());
    }

    /// <summary>
    /// The other half of the torso, and the half the owner will notice: <b>a
    /// ruined body keeps a creature out of the fight and a ruined limb does
    /// not</b> (coordinator's decision of 2026-08-12, record 1 of
    /// <see href="https://github.com/anshushunov/dungeon-fortress/issues/415">#415</see>).
    ///
    /// <para>Stated so it cannot pass by accident: every refusal on the ground of
    /// a wound names a creature whose torso is ruined, and there is at least one
    /// creature over the matrix who took the field carrying a heavy wound
    /// somewhere else. Without the second clause the check would be satisfied by a
    /// world where nobody is ever hurt badly enough to be asked.</para>
    /// </summary>
    [Fact]
    public void Only_a_ruined_body_is_kept_out_of_the_fight()
    {
        var wrongRefusals = Matrix.SelectMany(run => run.RefusedWithAWholeTorso).ToArray();
        var foughtHurt = Matrix.Sum(run => run.FoughtWithAHeavyLimb);

        output.WriteLine(
            $"MUSTER refusedByWound={Matrix.Sum(run => run.RefusedByWound)} " +
            $"ofWhichWithAWholeTorso={wrongRefusals.Length} " +
            $"foughtCarryingAHeavyLimb={foughtHurt}");

        Assert.True(
            wrongRefusals.Length == 0,
            "Refused the line on the ground of a wound while the body was whole: " +
            string.Join(", ", wrongRefusals.Take(10)));
        Assert.True(
            foughtHurt > 0,
            "Nobody over the whole matrix ever took the field carrying a heavy arm, leg or head, so " +
            "the rule that lets them is unobserved." + Environment.NewLine + Detail());
    }

    /// <summary>
    /// Criterion 4: <b>mending goes part by part and costs time in a bunk</b>, and
    /// how much time is a number this prints rather than a number anybody chose.
    ///
    /// <para>One part closes per recovery period, worst first, so a creature that
    /// came out of a wave with three hurt parts lies still about three times as
    /// long as one that came out with one. That is what makes «where» cost the
    /// domain something: before Issue #409 every part closed on the same tick, so
    /// a body broken in three places left the bunk with the body that was bruised
    /// in one.</para>
    ///
    /// <para>The gate is asserted as an absolute and not as a tendency: no closure
    /// anywhere in the matrix happens to a creature below
    /// <see cref="PrototypeTuning.RecoveryMinSatiety"/>. A domain that cannot feed
    /// its wounded does not heal them, and that is the whole of why the window
    /// between two waves is a decision.</para>
    /// </summary>
    [Fact]
    public void Mending_closes_one_part_at_a_time_and_costs_ticks_in_a_bunk()
    {
        var report = new StringBuilder();
        var costByPart = new long[BodyParts.Count];
        var closuresByPart = new int[BodyParts.Count];
        var starved = new List<string>();
        var twoAtOnce = new List<string>();

        foreach (var run in Matrix)
        {
            foreach (var (part, ticks) in run.Closures)
            {
                costByPart[(int)part] += ticks;
                closuresByPart[(int)part]++;
            }

            starved.AddRange(run.MendedWhileStarving);
            twoAtOnce.AddRange(run.MendedTwoPartsInOneTick);
        }

        foreach (var part in BodyParts.All)
        {
            var closures = closuresByPart[(int)part];
            var each = closures == 0
                ? "-"
                : (costByPart[(int)part] / closures).ToString(CultureInfo.InvariantCulture);
            report.AppendLine(
                $"MEND {Camel(part.ToString())}: {closures} closures, {each} ticks resting each");
        }

        output.WriteLine(report.ToString());

        Assert.True(
            closuresByPart.Sum() > 0,
            "Nothing mended anywhere on the matrix, so the cost of mending cannot be measured.");
        Assert.True(
            twoAtOnce.Count == 0,
            "Two parts closed inside one tick, so mending is not going part by part: " +
            string.Join(", ", twoAtOnce.Take(10)));
        Assert.True(
            starved.Count == 0,
            $"A wound closed on a creature below satiety {PrototypeTuning.RecoveryMinSatiety}: " +
            string.Join(", ", starved.Take(10)));
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
        Observe(previous, previous, previous.Tick, tally);

        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            if (current.Tick == previous.Tick)
            {
                previous = current;
                continue;
            }

            Observe(previous, current, current.Tick - 1, tally);
            previous = current;
        }

        return tally;
    }

    private static void Observe(
        PrototypeSnapshot before,
        PrototypeSnapshot state,
        int acted,
        Measurement tally)
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

        // The named quantity of each consequence, split by whether the part that
        // is supposed to move it is hurt. Each of the four is measured on its own
        // part and on nothing else, which is what makes a mutant on one of them
        // able to be red alone (criterion 2).
        //
        // The state read is `state` — the snapshot after the tick acted — and the
        // wound was set inside that same tick, so a creature whose arm was ruined
        // on this tick is already counted as hurt for the blow it struck. That is
        // one tick of imprecision per wound in the direction that makes the
        // measured gap SMALLER, so it can only understate a consequence and never
        // invent one.
        foreach (var entry in state.Events.Where(entry => entry.LastTick == acted))
        {
            var creature = state.Creatures.FirstOrDefault(item => item.Id == entry.CreatureId);
            if (creature is null)
            {
                continue;
            }

            if (entry.ReasonCode == "combat_attack" && entry.Details.TryGetValue("damage", out var damage))
            {
                var hurtArm = Hurt(creature, BodyPart.Arm);
                tally.ArmBlow.Add(hurtArm, damage, entry.Repeats);

                // The weapon alone, isolated from the rest of the blow, and the
                // isolation is what makes the mutant of criterion 2 able to be red.
                //
                // A blow is weapon + readiness/T.damage_readiness_divisor +
                // scatter. Raw blow damage is therefore NOT a measurement of the
                // arm: a creature with a hurt arm is usually hurt somewhere else
                // too, that other wound costs it readiness, and readiness is a term
                // of the same sum. Measured: with the arm's consequence switched
                // off entirely, raw damage still read 16.57 for hurt arms against
                // 19.09 for whole ones — a gap of the torso's making that a check
                // on raw damage would have reported as the arm's.
                //
                // Subtracting the published readiness term leaves weapon + scatter,
                // and the scatter is uniform on [-a,+a] and so averages out. What is
                // left is divided by the creature's own might, because the weapon
                // term is might times T.damage_might_weight: whole arms should read
                // about that constant, a light arm about half of it, a gone one
                // about nothing. Sums are pooled rather than averaged per blow so a
                // strong creature and a weak one weigh what they actually swing.
                tally.WeaponPerMight.Add(
                    hurtArm,
                    (damage - creature.Readiness / PrototypeTuning.DamageReadinessDivisor) * entry.Repeats,
                    creature.Might * entry.Repeats);
            }

            // Who the roll call turned away for a wound, and whose body it was.
            // Read off the journal for the reason the file's own summary gives:
            // the refusal is decided before the creature acts again, so the
            // snapshot's LastDecision no longer holds it by the end of the tick.
            if (entry.ReasonCode == "combat_refused_injured")
            {
                tally.RefusedByWound += entry.Repeats;
                if (!Hurt(creature, BodyPart.Torso, InjuryKind.Heavy))
                {
                    tally.RefusedWithAWholeTorso.Add(
                        $"{tally.Name} t{acted} #{creature.Id}: refused for a wound carrying " +
                        $"[{string.Join(',', creature.Injuries.Select(item => $"{item.Part}:{item.Severity}"))}]");
                }
            }

            // Mending, read off the journal because the journal is the only place
            // that says WHICH part closed: the counter beside it resets on the same
            // tick, so a snapshot cannot be differenced for it.
            if (entry.ReasonCode is "injury_mending" or "injury_healed" &&
                entry.Details.TryGetValue("part", out var mendedPart))
            {
                if (tally.ClosedThisTick.TryGetValue(creature.Id, out var already))
                {
                    tally.MendedTwoPartsInOneTick.Add(
                        $"{tally.Name} t{acted} #{creature.Id}: {already} and {(BodyPart)mendedPart}");
                }

                tally.ClosedThisTick[creature.Id] = (BodyPart)mendedPart;
                tally.Closures.Add(
                    ((BodyPart)mendedPart, tally.RestingSince.GetValueOrDefault(creature.Id)));
                tally.RestingSince[creature.Id] = 0;

                // The gate, asserted where it is decided rather than inferred. The
                // satiety read is the one published at the end of the tick the
                // closure happened on, and the rule is asked at the top of that
                // same tick, so a creature that ate afterwards cannot make this
                // fire and one that starved afterwards cannot be excused by it.
                if (creature.Satiety < PrototypeTuning.RecoveryMinSatiety)
                {
                    tally.MendedWhileStarving.Add(
                        $"{tally.Name} t{acted} #{creature.Id} satiety {creature.Satiety}");
                }
            }

            // The head speaks in the journal too, and this is the count of the
            // entries rather than of the actions behind them: folding merges two
            // stuns a tick apart into one, so this is a floor on how often the
            // player was told and never the rate. The rate is the counter below.
            if (entry.ReasonCode == "injury_stunned")
            {
                tally.StunEntries++;
            }
        }

        tally.ClosedThisTick.Clear();
        foreach (var creature in state.Creatures)
        {
            // Ticks spent lying still since the last part closed — the cost of
            // mending, counted where the domain actually pays it. A bunk is the
            // only place mending happens, so ticks anywhere else are not its
            // price.
            if (creature.Mode == CreatureMode.Resting && state.Tick > before.Tick)
            {
                tally.RestingSince[creature.Id] =
                    tally.RestingSince.GetValueOrDefault(creature.Id) + 1;
            }

            tally.Torso.Add(Hurt(creature, BodyPart.Torso), creature.Readiness, 1);

            // Somebody standing in the line with a ruined arm, leg or head — the
            // thing the old gate on the summary `Injury` made impossible. Counted
            // per creature-tick rather than per creature, because what the rule
            // buys is fighting ticks the domain used not to have.
            if (creature.Mode == CreatureMode.Fighting &&
                !Hurt(creature, BodyPart.Torso, InjuryKind.Heavy) &&
                creature.Injuries.Any(injury =>
                    injury.Part != BodyPart.Torso && injury.Severity == InjuryKind.Heavy))
            {
                tally.FoughtWithAHeavyLimb++;
            }

            // The limp, per creature-tick during which the leg was hurt. Both
            // halves are needed: a count of lost steps alone would rise simply
            // because a party has more wounds in it, and the denominator is what
            // makes it a rate. Steps lost while whole must stay at exactly zero,
            // which is the half that says the limp belongs to the leg and to
            // nothing else.
            var was = before.Creatures.FirstOrDefault(item => item.Id == creature.Id);
            if (was is not null && state.Tick > before.Tick)
            {
                tally.Limp.Add(
                    Hurt(creature, BodyPart.Leg),
                    creature.StepsLostToLimp - was.StepsLostToLimp,
                    1);

                // The stun, per **fighting** creature-tick, and the head's own
                // quantity for exactly the reason the limp is the leg's: nothing
                // but a hurt head charges an action to the stun, so the whole side
                // is not "small" but exactly zero.
                //
                // The denominator is fighting ticks and not all ticks, because the
                // stun is asked in ActCombatant and nowhere else: a hurt head
                // hauling mushrooms cannot lose an action, and counting those ticks
                // would divide the consequence by how long the party's peace was
                // rather than by how long its fight was. Pooled over the whole
                // matrix that is 0.35 against 0.00 on all ticks — a true number
                // about the wrong question, and one too small to print.
                //
                // A creature stunned on the tick a raider put it down is dropped
                // from both sides rather than from one: it ends the tick Downed, so
                // its lost action would otherwise be counted over a denominator
                // that no longer contains it.
                if (creature.Mode == CreatureMode.Fighting)
                {
                    tally.Stun.Add(
                        Hurt(creature, BodyPart.Head),
                        creature.ActionsLostToStun - was.ActionsLostToStun,
                        1);
                }
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

    private static bool Hurt(PrototypeCreatureSnapshot creature, BodyPart part) =>
        creature.Injuries.Any(injury => injury.Part == part);

    private static bool Hurt(PrototypeCreatureSnapshot creature, BodyPart part, InjuryKind atLeast) =>
        creature.Injuries.Any(injury => injury.Part == part && injury.Severity >= atLeast);

    /// <summary>
    /// One quantity measured twice: over the creature-ticks where the part in
    /// question is hurt, and over the ones where it is whole. The gap between the
    /// two means is the consequence, and a mutant that switches the consequence
    /// off has to close it.
    /// </summary>
    private sealed class Split
    {
        private long _hurtSum;
        private long _hurtCount;
        private long _wholeSum;
        private long _wholeCount;

        public void Add(bool hurt, int value, int weight)
        {
            if (hurt)
            {
                _hurtSum += (long)value * weight;
                _hurtCount += weight;
            }
            else
            {
                _wholeSum += (long)value * weight;
                _wholeCount += weight;
            }
        }

        public long HurtCount => _hurtCount;

        public long WholeCount => _wholeCount;

        /// <summary>Mean over the hurt side, in hundredths so it prints exactly.</summary>
        public long HurtMean => _hurtCount == 0 ? 0 : _hurtSum * 100 / _hurtCount;

        public long WholeMean => _wholeCount == 0 ? 0 : _wholeSum * 100 / _wholeCount;

        public override string ToString() =>
            $"hurt={Format(HurtMean)}/n{_hurtCount} whole={Format(WholeMean)}/n{_wholeCount}";

        private static string Format(long hundredths) =>
            string.Create(CultureInfo.InvariantCulture, $"{hundredths / 100}.{Math.Abs(hundredths % 100):00}");
    }

    /// <summary>
    /// A quotient measured twice — once where the part is hurt and once where it
    /// is whole — pooled over numerators and denominators rather than averaged per
    /// sample, so that a big contributor weighs what it contributes.
    /// </summary>
    private sealed class Ratio
    {
        private long _hurtNumerator;
        private long _hurtDenominator;
        private long _wholeNumerator;
        private long _wholeDenominator;

        public void Add(bool hurt, long numerator, long denominator)
        {
            if (hurt)
            {
                _hurtNumerator += numerator;
                _hurtDenominator += denominator;
            }
            else
            {
                _wholeNumerator += numerator;
                _wholeDenominator += denominator;
            }
        }

        public long HurtSamples => _hurtDenominator;

        public long WholeSamples => _wholeDenominator;

        /// <summary>The quotient in hundredths, so it prints exactly.</summary>
        public long Hurt => _hurtDenominator == 0 ? 0 : _hurtNumerator * 100 / _hurtDenominator;

        public long Whole => _wholeDenominator == 0 ? 0 : _wholeNumerator * 100 / _wholeDenominator;

        public override string ToString() =>
            $"hurt={Hurt / 100.0} whole={Whole / 100.0}";
    }

    private sealed class Measurement(string fixtureName, ulong seed)
    {
        public string Name { get; } = $"{fixtureName}/{seed}";

        /// <summary>
        /// Raw damage of a blow, split by whether the striker's arm is hurt. Kept
        /// in the report and deliberately not asserted on: see
        /// <see cref="WeaponPerMight"/>.
        /// </summary>
        public Split ArmBlow { get; } = new();

        /// <summary>
        /// What one point of might puts into a blow — the weapon term, isolated.
        /// This is the arm's own quantity and the one criterion 2 is asserted on.
        /// </summary>
        public Ratio WeaponPerMight { get; } = new();

        /// <summary>Readiness, split by whether the torso is hurt. The torso's own quantity.</summary>
        public Split Torso { get; } = new();

        /// <summary>Roll calls that turned somebody away on the ground of a wound.</summary>
        public int RefusedByWound { get; set; }

        /// <summary>Of those, the ones whose body was not ruined. Must stay empty.</summary>
        public List<string> RefusedWithAWholeTorso { get; } = [];

        /// <summary>
        /// Fighting creature-ticks spent by somebody carrying a heavy arm, leg or
        /// head and a torso that is not ruined — the ticks the old gate on the
        /// summary <c>Injury</c> made impossible.
        /// </summary>
        public int FoughtWithAHeavyLimb { get; set; }

        /// <summary>
        /// Steps the limp took away per creature-tick, split by whether the leg is
        /// hurt. The leg's own quantity.
        /// </summary>
        public Ratio Limp { get; } = new();

        /// <summary>
        /// Actions the stun took away per creature-tick, split by whether the head
        /// is hurt. The head's own quantity.
        /// </summary>
        public Ratio Stun { get; } = new();

        /// <summary>
        /// Journal entries the stun wrote. A floor and not a rate — see the note
        /// beside where it is counted — and printed rather than asserted on.
        /// </summary>
        public int StunEntries { get; set; }

        public int[] WoundsByPart { get; } = new int[BodyParts.Count];

        public int Wounds { get; set; }

        public int Light { get; set; }

        public int Heavy { get; set; }

        public int EntriesIntoALaterWave { get; set; }

        public int CarriedIntoNextWave { get; set; }

        /// <summary>Every part that closed, with the resting ticks it cost.</summary>
        public List<(BodyPart Part, int RestingTicks)> Closures { get; } = [];

        /// <summary>Resting ticks accumulated since each creature's last closure.</summary>
        public Dictionary<int, int> RestingSince { get; } = [];

        /// <summary>What each creature closed on the tick in hand. Cleared per tick.</summary>
        public Dictionary<int, BodyPart> ClosedThisTick { get; } = [];

        /// <summary>Closures on a creature the satiety gate should have refused. Must stay empty.</summary>
        public List<string> MendedWhileStarving { get; } = [];

        /// <summary>Ticks that closed two parts of one creature. Must stay empty.</summary>
        public List<string> MendedTwoPartsInOneTick { get; } = [];

        public List<string> Drift { get; } = [];

        public List<string> Duplicates { get; } = [];

        public override string ToString()
        {
            var byPart = BodyParts.All
                .Select(part => $"{Camel(part.ToString())}={WoundsByPart[(int)part]}");
            return
                $"  INJURY {Name} wounds={Wounds} byPart=[{string.Join(',', byPart)}] " +
                $"light={Light} heavy={Heavy} " +
                $"laterWaveEntries={EntriesIntoALaterWave} carrying={CarriedIntoNextWave}" +
                Environment.NewLine +
                $"    CONSEQUENCE {Name} weaponPerMight[{WeaponPerMight}] armBlow[{ArmBlow}] " +
                $"stepsLostPerTick[{Limp}] actionsLostPerTick[{Stun}] " +
                $"torsoReadiness[{Torso}] stunEntries={StunEntries} " +
                $"refusedByWound={RefusedByWound} foughtWithAHeavyLimb={FoughtWithAHeavyLimb}";
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
