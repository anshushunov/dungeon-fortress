using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// What holds a group in the quarters after a raid, measured rather than guessed
/// (Issue #228).
///
/// <para><b>The complaint.</b> The owner watched a party on 2026-08-04, on the
/// tree that already carries the off-duty rule of Issue #201, and said:
/// «разошлись быстрее, все встали в больницу и не могли оттуда выйти, возможно
/// из-за того что поле боя блокировало». The first half is what #201 promised.
/// The second half is this file: the group arrives in the quarters and then
/// stands there.</para>
///
/// <para><b>Three mechanisms could produce it</b> and they need different
/// treatment: there is no work (economy, Issue #72); the way out is walled by
/// bodies (cell occupancy, Issue #76, left on slice 6); or the zone the creature
/// wants is unreachable (<c>refused_zone_unreachable</c>, carried out of the
/// scope of #201 on purpose). Naming one of them requires a decomposition that
/// leaves no remainder, which is what <see cref="Cause"/> and
/// <see cref="The_decomposition_of_the_stay_in_the_quarters_leaves_no_remainder"/>
/// are for.</para>
///
/// <para><b>The window is the quiet stretch, not sixty ticks.</b>
/// <see cref="PrototypePostCombatDispersalTests"/> watches 60 ticks because it
/// measures the dispersal itself, and the dispersal is over in 33. This file
/// measures what happens <i>after</i> the walk, and the last creature does not
/// even start walking until <c>OffDutyDelayTicks + 8 * OffDutyStaggerTicks</c> =
/// 32 ticks have passed. A 60-tick window would therefore end while the group is
/// still arriving and would answer a different question. The window used here
/// runs from the end of the wave to the tick the domain stops working — the
/// start of the next muster, or the arrival of the next wave, or the end of the
/// party, whichever is first. That boundary is the one independent review of
/// Issue #186 forced onto the control window of that file, for the same reason:
/// a window that reaches into the muster measures the muster.
/// <see cref="The_window_ends_before_the_domain_stops_working"/> keeps it
/// found.</para>
///
/// <para><b>The place.</b> «Больница» is <see cref="ZoneKind.Quarters"/>, and the
/// question is asked about the zone <b>and the tiles around it</b>, because a
/// creature stopped one tile short of the doorway is as stuck as one inside. The
/// halo is the passable tiles orthogonally adjacent to the zone and not in
/// it.</para>
/// </summary>
public sealed class PrototypeQuartersIdleTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    private static readonly string[] Fixtures = ["baseline", "prepared"];

    /// <summary>
    /// The bunks of the authored map, read from the layout rather than from the
    /// simulation, so that this file states a fact about the map instead of
    /// repeating the code it measures.
    /// </summary>
    private static readonly HashSet<GridPoint> Bunks = [.. PrototypeLayout.Read('q')];

    /// <summary>
    /// A hard stop on the window so that a fixture without a next wave cannot
    /// turn "the quiet stretch" into "the rest of the party".
    /// <see cref="PrototypeTuning.WaveIntervalTicks"/> is the distance between two
    /// waves, so a window of that length never reaches the fight after next.
    /// </summary>
    private const int WindowCap = PrototypeTuning.WaveIntervalTicks;

    /// <summary>
    /// How much longer a way round may be than the blind route before it stops
    /// counting as one. The same four tiles
    /// <see cref="PrototypePostCombatDispersalTests"/> uses, so that the share
    /// measured here and the 93,1 % of Issue #186 are the same quantity.
    /// </summary>
    private const int DetourSlack = 4;

    /// <summary>
    /// Why one creature-tick inside the quarters was spent standing.
    ///
    /// <para>This is a partition and not a histogram: <see cref="Classify"/> is a
    /// ladder, so a tick lands in exactly one of these by construction, and
    /// <see cref="Unclassified"/> is what a tick lands in when no rung claims it.
    /// The measurement is only worth quoting while that bucket is empty.</para>
    /// </summary>
    internal enum Cause
    {
        /// <summary>It walked. Not standing at all.</summary>
        Stepped,

        /// <summary>Down in the fight; no rule about work moves a body.</summary>
        OffItsFeet,

        /// <summary>Assembling for the next wave.</summary>
        Mustering,

        /// <summary>
        /// Tried to step and a body was on the tile.
        ///
        /// <para>This rung sits above "eating", "resting" and "working" on
        /// purpose, and the order is the answer to the question rather than a
        /// detail of bookkeeping. Issue #228 asks what <b>holds</b> the group in
        /// the quarters. A creature that has somewhere to be and is refused the
        /// step is held there, whatever the errand was; a creature asleep on a
        /// bunk is not held, it is asleep. Putting the errand first would file
        /// 500 refused steps of the matrix under «оно шло есть» and lose the
        /// symptom the issue was opened for. The cross-tabulation printed by
        /// <see cref="Report_what_the_journal_said_about_the_stay_in_the_quarters"/>
        /// shows the same ticks the other way round, so nothing is hidden by the
        /// choice.</para>
        /// </summary>
        BlockedByOthers,

        /// <summary>Tried to step and the map offered no route at all.</summary>
        RouteUnreachable,

        /// <summary>Hungry, and no larder tile it could walk to.</summary>
        LarderUnreachable,

        /// <summary>Eating, or on its way to a reserved meal.</summary>
        Eating,

        /// <summary>Asleep on a bunk: a <see cref="JobKind.Rest"/> job.</summary>
        Resting,

        /// <summary>Holding some other job.</summary>
        Working,

        /// <summary>The matching's best job kind has no zone it can reach.</summary>
        WorkZoneUnreachable,

        /// <summary>The matching ran and had nothing to give.</summary>
        NoWork,

        /// <summary>
        /// The job it was doing ran out during this very tick. The matching had
        /// already been and gone — <c>MatchJobs</c> runs before
        /// <c>ActCreatures</c> — so the creature was not in the pool and no
        /// waiting reason was written for it, but the tick was spent working all
        /// the same. Three ticks over the matrix, all of them the last tick of a
        /// <see cref="JobKind.Rest"/>; small, and named rather than swept into the
        /// remainder.
        /// </summary>
        JobEnded,

        /// <summary>Below <see cref="PrototypeTuning.CollapseThreshold"/>: not a
        /// candidate for work at all.</summary>
        Starving,

        /// <summary>Nothing above explains the tick. Has to stay zero.</summary>
        Unclassified,
    }

    /// <summary>
    /// The decision codes <c>RecordWaitingReason</c> can write. A creature that
    /// carries one of these on a tick was in the matching pool on that tick and
    /// the matching had nothing for it — which is the positive statement behind
    /// <see cref="Cause.NoWork"/>, as opposed to "none of the other rungs
    /// matched".
    /// </summary>
    private static readonly HashSet<string> MatchingHadNothing =
    [
        "waiting_no_job_available",
        "waiting_stock_sufficient",
        "waiting_crop_not_ripe",
        "waiting_input_missing",
        "waiting_storage_full",
        "waiting_no_stockpile",
        "waiting_stockpile_full",
        "waiting_no_designation",
        "waiting_no_blueprint",
        "refused_priority_zero",
        "refused_rule_min_satiety",
        "refused_rule_reserve",
        "refused_too_exhausted",
        "refused_zone_not_designated",
        "refused_place_of_panic",
        "refused_place_of_wound",
        "dig_unreachable",
        "build_unreachable",
        "build_waiting_material",
        "build_no_stone",
        "stone_unreachable",
    ];

    /// <summary>
    /// The decisions only <c>MatchJobs</c> writes, and therefore the observable
    /// proof that a creature was in its pool on the tick they carry: every
    /// diagnostic of <c>RecordWaitingReason</c>, the memory refusals it writes
    /// just before the matching, the <c>refused_zone_unreachable</c> it can write
    /// as a diagnostic, and every <c>chosen_*</c> by which work is handed out.
    /// </summary>
    private static bool InThePool(PrototypeEvent decision) =>
        MatchingHadNothing.Contains(decision.ReasonCode) ||
        // Only the matching's own flavour of this code counts. `Move` writes it
        // with the tile it could not path to and `TryStartEating` with the zone it
        // could not reach; neither is evidence that the matching saw anybody.
        (decision.ReasonCode == "refused_zone_unreachable" &&
            !decision.Details.ContainsKey("targetX") &&
            !decision.Details.ContainsKey("zoneKind")) ||
        decision.ReasonCode is "chosen_only_option"
            or "chosen_highest_priority"
            or "chosen_bottleneck"
            or "chosen_affinity_match"
            or "chosen_nearest"
            or "chosen_tie_break";

    /// <summary>
    /// The numbers themselves, printed rather than asserted: the measurement
    /// Issue #228 asks for in its first and third criteria.
    /// </summary>
    [Fact]
    public void Report_the_stay_in_the_quarters_over_the_seed_matrix()
    {
        var report = new StringBuilder();
        foreach (var party in Matrix)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{party}");
            foreach (var window in party.Windows)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"    {window}");
            }
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"MATRIX {Summary(Matrix)}");
        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// Criterion 1 of Issue #228: the decomposition covers the whole cohort and
    /// not most of it.
    ///
    /// <para>Two halves, and both are needed. <b>Coverage</b> says every
    /// creature-tick an ex-combatant spent in the quarters landed in a named
    /// bucket. <b>Corroboration</b> says the buckets are not one bucket wearing
    /// twelve hats: the two rungs that a canonical counter can check
    /// independently — the creature stepped, the creature was refused a step —
    /// are checked against <c>moveCount</c> and <c>blockedTicks</c>, which the
    /// snapshot maintains without any help from the journal this classification
    /// reads. Coverage alone passes for a classifier that answers "no work" to
    /// everything; corroboration is what makes that answer red.</para>
    /// </summary>
    [Fact]
    public void The_decomposition_of_the_stay_in_the_quarters_leaves_no_remainder()
    {
        var unclassified = Matrix.Sum(party => party.Total(Cause.Unclassified));
        var quartersTicks = Matrix.Sum(party => party.QuartersTicks);

        Assert.True(
            unclassified == 0,
            $"{unclassified} of {quartersTicks} creature-tick(s) an ex-combatant spent in the " +
            "quarters and the tiles around them are not explained by any named cause. The " +
            "decomposition Issue #228 asks for has to cover the whole cohort, and a remainder " +
            $"above zero is a missing cause rather than rounding.{Environment.NewLine}{Detail()}");

        var stepped = Matrix.Sum(party => party.Total(Cause.Stepped));
        var moved = Matrix.Sum(party => party.MovedByTheCanonicalCounter);
        Assert.True(
            stepped == moved,
            $"The ladder put {stepped} creature-tick(s) in '{Cause.Stepped}' and the canonical " +
            $"moveCount counter says {moved} step(s) were taken in the same ticks. The two read " +
            "different sources on purpose — the ladder reads the journal, the counter is kept by " +
            "the simulation — so a rung that has started swallowing ticks belonging to another " +
            $"shows up here.{Environment.NewLine}{Detail()}");

        var blocked = Matrix.Sum(party => party.Total(Cause.BlockedByOthers));
        var blockedWithTheCounter = Matrix.Sum(party => party.RefusedAStep(Cause.BlockedByOthers));
        var refused = Matrix.Sum(party => party.RefusedByTheCanonicalCounter);
        Assert.True(
            refused == blockedWithTheCounter,
            $"The canonical blockedTicks counter rose on {refused} creature-tick(s) in the " +
            $"quarters and only {blockedWithTheCounter} of them were classified as " +
            $"'{Cause.BlockedByOthers}'. A refused step has been filed as something else." +
            $"{Environment.NewLine}{Detail()}");

        // The bucket is larger than the counter, and the difference is real
        // rather than slack. `blockedTicks` is kept by `PrototypeWorld.Move`
        // alone, and three other places write `waiting_blocked_by_other` without
        // going through it: the two larder retries of `ActJob`
        // (PrototypeWorld.cs:3567 and :3630, "the tile I was sent to has somebody
        // on it") and the meal lane of `CanAdvanceMealQueue`
        // (PrototypeWorld.cs:4255). Those are refusals by a body just the same, so
        // they belong in the bucket; they simply cannot be corroborated by the
        // counter, and the inequality is what says which way the gap may go.
        Assert.True(
            blocked >= blockedWithTheCounter,
            $"The ladder put {blocked} creature-tick(s) in '{Cause.BlockedByOthers}' and " +
            $"{blockedWithTheCounter} of them are corroborated by the canonical counter, which is " +
            $"more than the bucket holds.{Environment.NewLine}{Detail()}");

        // The other half of the same corroboration, and the half that catches a
        // cause swallowing its neighbours: a tick on which the simulation itself
        // recorded a refused step may not be filed under a cause whose whole
        // meaning is that the creature was not trying to move.
        foreach (var cause in NeverRefusedAStep)
        {
            var wrong = Matrix.Sum(party => party.RefusedAStep(cause));
            Assert.True(
                wrong == 0,
                $"{wrong} creature-tick(s) on which the canonical blockedTicks counter rose were " +
                $"classified as '{cause}', a cause that says the creature took no step to be " +
                $"refused. One cause has absorbed another.{Environment.NewLine}{Detail()}");
        }
    }

    /// <summary>
    /// The causes whose meaning excludes a refused step. <see cref="Cause.Stepped"/>
    /// is here because <c>Move</c> zeroes <c>blockedTicks</c> the moment a step
    /// succeeds; the rest are the states in which nothing asked the creature to
    /// move at all.
    /// </summary>
    private static readonly Cause[] NeverRefusedAStep =
    [
        Cause.Stepped,
        Cause.WorkZoneUnreachable,
        Cause.NoWork,
        Cause.JobEnded,
        Cause.Starving,
        Cause.Unclassified,
    ];

    /// <summary>
    /// The decomposition is only a decomposition while it has more than one
    /// non-empty bucket, and only interesting while the sample is real. A layout
    /// or balance change that emptied the quarters would leave every share above
    /// well defined and meaningless.
    /// </summary>
    [Fact]
    public void The_matrix_still_puts_ex_combatants_in_the_quarters_to_measure()
    {
        var quartersTicks = Matrix.Sum(party => party.QuartersTicks);
        var occupied = Enum.GetValues<Cause>()
            .Count(cause => cause != Cause.Unclassified && Matrix.Sum(party => party.Total(cause)) > 0);

        Assert.True(
            quartersTicks >= 500,
            $"Only {quartersTicks} creature-tick(s) of the cohort were spent in the quarters over " +
            "the whole matrix, which is too few for anything said about the stay there to have " +
            $"been sampled.{Environment.NewLine}{Detail()}");
        Assert.True(
            occupied >= 4,
            $"Only {occupied} of the named causes ever occurred over the matrix. A partition into " +
            "one bucket explains nothing, and the shares quoted from it would be an artefact of " +
            $"the classifier rather than of the domain.{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// The overflow branch of the off-duty rule is not in this measurement, and
    /// this is why: on the authored map it cannot run.
    ///
    /// <para>Issue #228 allows a fix in the same task if the culprit turns out to
    /// be the rule that packs an overflowing quarters into one cluster of tiles.
    /// Ruling that candidate out is therefore part of the answer and not a note —
    /// and it is ruled out by counting rather than by reading the rule: the
    /// quarters hold sixteen tiles that are not bunks and the prototype has nine
    /// creatures, so every identifier is inside the zone and the branch that walks
    /// out of it is never reached.</para>
    /// </summary>
    [Fact]
    public void The_quarters_never_overflow_on_this_map()
    {
        var world = new PrototypeWorld(LoadFixture("baseline") with { Seed = MatrixSeeds[0] });
        var snapshot = world.GetSnapshot();
        var standing = snapshot.Zones[ZoneKind.Quarters].Count(tile => !Bunks.Contains(tile));

        Assert.True(
            snapshot.Creatures.Count <= standing,
            $"The quarters offer {standing} tile(s) that are not bunks and the party has " +
            $"{snapshot.Creatures.Count} creature(s). The overflow branch of the off-duty rule now " +
            "runs, so it is a live candidate for the jam Issue #228 is measuring and can no longer " +
            "be excluded by this assertion.");
        Assert.Equal(16, standing);
    }

    /// <summary>
    /// The boundary of the window, held the way Issue #186 had to learn to hold
    /// it: a window that runs into the muster measures the muster, when the whole
    /// domain drops its work and walks into the Watch zone. On <c>prepared</c> the
    /// journal sets <c>muster_lead_ticks</c> to 60 on tick 880, so the boundary is
    /// not hypothetical.
    ///
    /// <para>The second assertion is the sample: a matrix in which nobody musters
    /// satisfies the first without exercising it.</para>
    /// </summary>
    [Fact]
    public void The_window_ends_before_the_domain_stops_working()
    {
        foreach (var party in Matrix)
        {
            foreach (var window in party.Windows)
            {
                Assert.True(
                    window.MusteringTicks == 0,
                    $"{party.Fixture}/{party.Seed} window after wave {window.Wave}: " +
                    $"{window.MusteringTicks} tick(s) of it were spent by creatures assembling " +
                    "for the next wave. The stay in the quarters is then being measured over " +
                    "ticks in which the domain has stopped handing out work at all." +
                    $"{Environment.NewLine}{Detail()}");
                Assert.True(
                    window.ObservedTicks <= WindowCap,
                    $"{party.Fixture}/{party.Seed} window after wave {window.Wave} ran for " +
                    $"{window.ObservedTicks} ticks, past the cap of {WindowCap}. A window longer " +
                    $"than the interval between waves reaches the fight after next." +
                    $"{Environment.NewLine}{Detail()}");
            }
        }

        Assert.True(
            Matrix.Any(party => party.MusterLeadSeen > 0),
            "No party of the matrix ever set a muster lead, so the boundary above was never " +
            $"exercised.{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// Criterion 2 of Issue #228, and the sentence being checked is section 4.1
    /// of the contract: «уход не является работой: он не создаёт job, ничего не
    /// резервирует и не мешает сопоставлению — существо на пути к покоям доступно
    /// работе на каждом тике».
    ///
    /// <para>"Available to the matching" is measured and not read off the code:
    /// the quantity is the longest run of consecutive ticks during which one
    /// particular job stayed <b>unreserved</b>, was reachable by the map from
    /// where the creature stood, was not somebody else's personal job, belonged to
    /// a kind with a non-zero priority — and the ex-combatant standing idle in the
    /// quarters did not take it. Competition is excluded by construction: a job
    /// somebody else took stops being counted on the tick it is reserved.</para>
    ///
    /// <para>Zero would mean the claim holds exactly. One tick of lag is the
    /// matching and the walk taking their turn in the same tick. Anything larger
    /// is the claim failing, and the number is what says by how much.</para>
    /// </summary>
    [Fact]
    public void Report_how_long_work_waits_for_a_creature_standing_in_the_quarters()
    {
        var report = new StringBuilder();
        foreach (var party in Matrix)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{party.Fixture}/{party.Seed} " +
                $"offeredRuns={party.OfferedRuns.Count} " +
                $"maxOfferedRun={(party.OfferedRuns.Count == 0 ? 0 : party.OfferedRuns.Max(run => run.Ticks))} " +
                $"meanOfferedRun={(party.OfferedRuns.Count == 0 ? 0 : party.OfferedRuns.Average(run => run.Ticks)):F2} " +
                $"tookTheJob={party.OfferAnswered} neverTook={party.OfferUnanswered} " +
                $"firstStepDelays=[{string.Join(",", party.FirstStepDelays)}]");
        }

        var jobless = Matrix.Sum(party => party.JoblessTicks);
        var inThePool = Matrix.Sum(party => party.JoblessAndInThePool);
        report.AppendLine(CultureInfo.InvariantCulture,
            $"MATRIX joblessTicksInTheQuarters={jobless} sawTheMatching={inThePool} " +
            $"missedTheMatching={jobless - inThePool} " +
            $"share={(jobless == 0 ? 0 : (double)inThePool / jobless):P2}");
        foreach (var reason in Matrix
                     .SelectMany(party => party.MissedThePool)
                     .GroupBy(pair => pair.Key)
                     .OrderByDescending(group => group.Sum(pair => pair.Value)))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"    missed because: {reason.Key} = {reason.Sum(pair => pair.Value)}");
        }

        var all = Matrix.SelectMany(party => party.OfferedRuns).ToArray();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"MATRIX offeredRuns={all.Length} " +
            $"maxOfferedRun={(all.Length == 0 ? 0 : all.Max(run => run.Ticks))} " +
            $"meanOfferedRun={(all.Length == 0 ? 0 : all.Average(run => run.Ticks)):F2} " +
            $"tookTheJob={Matrix.Sum(party => party.OfferAnswered)} " +
            $"neverTook={Matrix.Sum(party => party.OfferUnanswered)} " +
            $"maxFirstStepDelay={Matrix.SelectMany(party => party.FirstStepDelays).DefaultIfEmpty(0).Max()}");
        var answered = all.Where(run => run.Ending == "took this very job").ToArray();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"MATRIX theResponseItself runs={answered.Length} " +
            $"max={(answered.Length == 0 ? 0 : answered.Max(run => run.Ticks))} " +
            $"mean={(answered.Length == 0 ? 0 : answered.Average(run => run.Ticks)):F2} " +
            $"histogram=[{string.Join(",", answered.GroupBy(run => run.Ticks).OrderBy(group => group.Key).Select(group => $"{group.Key}:{group.Count()}"))}]");
        foreach (var group in all
                     .Where(run => run.Ending != "took this very job")
                     .GroupBy(run => run.LastReason)
                     .OrderByDescending(group => group.Sum(run => run.Ticks)))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"    never taken, the matching last said '{group.Key}': runs={group.Count()} " +
                $"ticks={group.Sum(run => run.Ticks)} max={group.Max(run => run.Ticks)}");
        }

        foreach (var run in all.OrderByDescending(item => item.Ticks).Take(12))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"    longest: #{run.Creature} waited {run.Ticks} tick(s) on job {run.Job} " +
                $"({run.Kind}); it ended because it {run.Ending}; the matching last said " +
                $"'{run.LastReason}'");
        }

        foreach (var group in all.GroupBy(run => run.Kind).OrderByDescending(group => group.Count()))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"    by kind: {group.Key} runs={group.Count()} " +
                $"max={group.Max(run => run.Ticks)} mean={group.Average(run => run.Ticks):F2}");
        }

        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// The same matrix, walked again with the diagnostics on: which decision the
    /// journal actually carried, which tile the creature stood on and which tile
    /// it was refused a step towards. It is a second walk rather than a flag on
    /// the shared one because the shared one is cached across four assertions and
    /// a static collector filled by whichever of them ran first would depend on
    /// test order.
    /// </summary>
    [Fact]
    public void Report_what_the_journal_said_about_the_stay_in_the_quarters()
    {
        var diagnostics = new MatrixDiagnostics();
        Diagnostics = diagnostics;
        try
        {
            foreach (var fixtureName in Fixtures)
            {
                foreach (var seed in MatrixSeeds)
                {
                    _ = Measure(fixtureName, seed);
                }
            }
        }
        finally
        {
            Diagnostics = null;
        }

        output.WriteLine(diagnostics.ToString());
    }

    private static IReadOnlyList<PartyMeasurement> Matrix => MatrixMeasurements.Value;

    private static readonly Lazy<IReadOnlyList<PartyMeasurement>> MatrixMeasurements =
        new(() =>
        [
            .. Fixtures.SelectMany(
                _ => MatrixSeeds,
                (fixtureName, seed) => Measure(fixtureName, seed)),
        ]);

    private static string Detail() =>
        string.Join(Environment.NewLine, Matrix.Select(party => party.ToString()));

    internal static string Summary(IReadOnlyList<PartyMeasurement> matrix)
    {
        var report = new StringBuilder();
        var cohortTicks = matrix.Sum(party => party.CohortTicks);
        var quarters = matrix.Sum(party => party.QuartersTicks);
        report.Append(CultureInfo.InvariantCulture,
            $"windows={matrix.Sum(party => party.Windows.Count)} " +
            $"cohortTicks={cohortTicks} " +
            $"quartersTicks={quarters} " +
            $"inZone={matrix.Sum(party => party.ZoneTicks)} " +
            $"inHalo={matrix.Sum(party => party.HaloTicks)} " +
            $"quartersShare={(cohortTicks == 0 ? 0 : (double)quarters / cohortTicks):F3}");
        foreach (var cause in Enum.GetValues<Cause>())
        {
            report.Append(CultureInfo.InvariantCulture, $" {cause}={matrix.Sum(party => party.Total(cause))}");
        }

        var wayRound = matrix.Sum(party => party.WayRound);
        var walledIn = matrix.Sum(party => party.WalledIn);
        var tooLong = matrix.Sum(party => party.WayRoundTooLong);
        report.Append(CultureInfo.InvariantCulture,
            $" | blockedTowardsAnOffDutyTile={matrix.Sum(party => party.BlockedTowardsAnOffDutyTile)}" +
            $" wayRound={wayRound} wayRoundStrictlyLonger={matrix.Sum(party => party.WayRoundStrictlyLonger)}" +
            $" wayRoundTooLong={tooLong} walledIn={walledIn}" +
            $" wayRoundShare={(wayRound + walledIn + tooLong == 0 ? 0 : (double)wayRound / (wayRound + walledIn + tooLong)):F3}");
        var streaks = matrix.SelectMany(party => party.LongestBlockedStreaks).Where(run => run > 0).ToArray();
        report.Append(CultureInfo.InvariantCulture,
            $" | blockedStreaks={streaks.Length}" +
            $" longestBlockedStreak={(streaks.Length == 0 ? 0 : streaks.Max())}" +
            $" meanLongestStreak={(streaks.Length == 0 ? 0 : streaks.Average()):F1}");
        foreach (var who in matrix
                     .SelectMany(party => party.WhoWasInTheWay)
                     .GroupBy(pair => pair.Key)
                     .OrderByDescending(group => group.Sum(pair => pair.Value)))
        {
            report.Append(CultureInfo.InvariantCulture,
                $" | inTheWay[{who.Key}]={who.Sum(pair => pair.Value)}");
        }

        foreach (var tile in matrix
                     .SelectMany(party => party.BlockedOn)
                     .GroupBy(pair => pair.Key)
                     .OrderByDescending(group => group.Sum(pair => pair.Value))
                     .Take(6))
        {
            report.Append(CultureInfo.InvariantCulture,
                $" | refusedOnto({tile.Key.X},{tile.Key.Y})={tile.Sum(pair => pair.Value)}");
        }

        report.Append(CultureInfo.InvariantCulture,
            $" | refusedAStep={matrix.Sum(party => party.RefusedByTheCanonicalCounter)}" +
            $" tookAStep={matrix.Sum(party => party.MovedByTheCanonicalCounter)}");
        foreach (var cause in Enum.GetValues<Cause>().Where(cause => matrix.Sum(party => party.RefusedAStep(cause)) > 0))
        {
            report.Append(CultureInfo.InvariantCulture,
                $" refused:{cause}={matrix.Sum(party => party.RefusedAStep(cause))}");
        }

        return report.ToString();
    }

    /// <summary>
    /// One party, walked tick by tick.
    /// </summary>
    internal static PartyMeasurement Measure(string fixtureName, ulong seed)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var previous = world.GetSnapshot();
        var quartersArea = QuartersArea(previous);
        var parking = OffDutyParking(previous);
        var windows = new List<WindowMeasurement>();
        var open = new List<Window>();
        var musterLeadSeen = 0;
        var lastTick = previous.Tick;

        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            lastTick = current.Tick;
            var acted = current.Tick - 1;
            musterLeadSeen = Math.Max(musterLeadSeen, current.Rules.GetValueOrDefault("muster_lead_ticks"));

            // Close first, so that a tick which belongs to the next muster is
            // never observed by a window that was open when it started.
            foreach (var window in open.Where(item => item.ShouldClose(current, acted)).ToArray())
            {
                windows.Add(window.ToMeasurement());
                open.Remove(window);
            }

            foreach (var window in open)
            {
                window.Observe(previous, current, acted, quartersArea, parking);
            }

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
                if (cohort.Length > 0)
                {
                    open.Add(new Window(wave.Number, acted, cohort));
                }
            }

            previous = current;
        }

        foreach (var window in open)
        {
            windows.Add(window.ToMeasurement());
        }

        return new PartyMeasurement(
            fixtureName,
            seed,
            lastTick,
            musterLeadSeen,
            [.. windows.Where(window => window.ObservedTicks > 0).OrderBy(window => window.Wave)]);
    }

    /// <summary>
    /// The quarters and the tiles around them: the zone as the snapshot publishes
    /// it, plus every passable tile orthogonally adjacent to one of its tiles. A
    /// creature stopped one tile short of the doorway is as stuck as one inside,
    /// and Issue #228 asks the question about both.
    /// </summary>
    private static (IReadOnlySet<GridPoint> Zone, IReadOnlySet<GridPoint> Halo) QuartersArea(
        PrototypeSnapshot snapshot)
    {
        var zone = snapshot.Zones[ZoneKind.Quarters].ToHashSet();
        var rock = snapshot.Map.RockTiles.ToHashSet();
        var halo = zone
            .SelectMany(Neighbors)
            .Where(tile => InBounds(tile) && !rock.Contains(tile) && !zone.Contains(tile))
            .ToHashSet();
        return (zone, halo);
    }

    /// <summary>
    /// The tile the off-duty rule of Issue #201 sends each creature to: the
    /// quarters zone without its bunks, in the snapshot's own order, indexed by
    /// creature identifier.
    ///
    /// <para>It is restated here from the layout and the zone rather than asked of
    /// <c>PrototypeWorld</c>, for the reason every measurement in this project
    /// restates the rule it checks: a measurement that asks the code what it did
    /// cannot disagree with it. The overflow branch of the real rule — more
    /// creatures than free tiles, the rest standing next to the zone — is not
    /// restated because it never runs on this map: the quarters hold sixteen
    /// non-bunk tiles and the prototype has nine creatures, which
    /// <see cref="The_quarters_never_overflow_on_this_map"/> asserts rather than
    /// assumes.</para>
    /// </summary>
    private static IReadOnlyDictionary<int, GridPoint> OffDutyParking(PrototypeSnapshot snapshot)
    {
        var standing = snapshot.Zones[ZoneKind.Quarters]
            .Where(tile => !Bunks.Contains(tile))
            .Order()
            .ToArray();
        return snapshot.Creatures
            .Where(creature => creature.Id < standing.Length)
            .ToDictionary(creature => creature.Id, creature => standing[creature.Id]);
    }

    /// <summary>
    /// The step <c>PrototypeMap.NextStep</c> would return: the same BFS over the
    /// same passable tiles, visiting neighbours in the same order and returning
    /// the first tile of the first shortest path found.
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
                if (!InBounds(next) || walls.Contains(next) || !visited.Add(next))
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
    /// One quiet stretch after a fight, accumulating while the party runs.
    /// </summary>
    private sealed class Window(int wave, int endTick, IReadOnlyList<int> cohort)
    {
        private readonly Dictionary<Cause, int> _causes = [];
        private readonly Dictionary<Cause, int> _refusedAStep = [];
        private readonly Dictionary<Cause, int> _tookAStep = [];
        private readonly Dictionary<(int Creature, long Job), Offer> _offered = [];
        private readonly Dictionary<int, int> _jobTakenAt = [];
        private readonly List<Offer> _offeredRuns = [];
        private readonly List<int> _firstStepDelays = [];
        private int _observed;
        private int _cohortTicks;
        private int _zoneTicks;
        private int _haloTicks;
        private int _movedByCounter;
        private int _refusedByCounter;
        private int _mustering;
        private int _offerAnswered;
        private int _offerUnanswered;
        private readonly Dictionary<string, int> _missedThePool = [];
        private readonly Dictionary<string, int> _whoWasInTheWay = [];
        private readonly Dictionary<GridPoint, int> _blockedOn = [];
        private int _wayRound;
        private int _wayRoundStrictlyLonger;
        private int _wayRoundTooLong;
        private int _walledIn;
        private readonly Dictionary<int, int> _blockedStreak = [];
        private readonly Dictionary<int, int> _longestBlockedStreak = [];
        private int _blockedTowardsAnOffDutyTile;
        private int _joblessTicks;
        private int _joblessAndInThePool;

        public int Wave => wave;

        /// <summary>
        /// The window ends where the domain stops working: the next muster, the
        /// next wave, or <see cref="WindowCap"/> ticks, whichever is first.
        /// </summary>
        public bool ShouldClose(PrototypeSnapshot snapshot, int acted)
        {
            if (acted > endTick + WindowCap)
            {
                return true;
            }

            var lead = snapshot.Rules.GetValueOrDefault("muster_lead_ticks");
            var next = snapshot.Waves.FirstOrDefault(item => item.Outcome is null);
            return next is not null && acted >= next.ArriveTick - Math.Max(lead, 0);
        }

        public void Observe(
            PrototypeSnapshot previous,
            PrototypeSnapshot current,
            int acted,
            (IReadOnlySet<GridPoint> Zone, IReadOnlySet<GridPoint> Halo) area,
            IReadOnlyDictionary<int, GridPoint> parking)
        {
            _observed++;
            foreach (var id in cohort)
            {
                var creature = current.Creatures.FirstOrDefault(item => item.Id == id);
                var before = previous.Creatures.FirstOrDefault(item => item.Id == id);
                if (creature is null || before is null)
                {
                    continue;
                }

                _cohortTicks++;
                var inZone = area.Zone.Contains(creature.Position);
                var inHalo = area.Halo.Contains(creature.Position);
                if (creature.IsMustering)
                {
                    _mustering++;
                }

                if (!inZone && !inHalo)
                {
                    // Outside the quarters the creature is not the subject of this
                    // measurement, and its offers are dropped with it: a run that
                    // is about standing in the quarters cannot survive leaving.
                    foreach (var key in _offered.Keys.Where(key => key.Creature == id).ToArray())
                    {
                        _offeredRuns.Add(_offered[key] with { Ending = "left the quarters" });
                        _offerUnanswered++;
                        _offered.Remove(key);
                    }

                    continue;
                }

                if (inZone)
                {
                    _zoneTicks++;
                }
                else
                {
                    _haloTicks++;
                }

                var codes = current.Events
                    .Where(item => item.CreatureId == id && item.LastTick == acted)
                    .ToArray();
                var cause = Classify(current, creature, before, codes);
                _causes[cause] = _causes.GetValueOrDefault(cause) + 1;
                Diagnostics?.Count(cause, acted, creature, before, codes);
                if (creature.MoveCount > before.MoveCount)
                {
                    _movedByCounter++;
                    _tookAStep[cause] = _tookAStep.GetValueOrDefault(cause) + 1;
                }

                if (creature.BlockedTicks > before.BlockedTicks)
                {
                    _refusedByCounter++;
                    _refusedAStep[cause] = _refusedAStep.GetValueOrDefault(cause) + 1;
                }

                if (cause == Cause.BlockedByOthers)
                {
                    WhoWasInTheWay(current, creature, codes, parking);
                    var streak = _blockedStreak.GetValueOrDefault(id) + 1;
                    _blockedStreak[id] = streak;
                    _longestBlockedStreak[id] = Math.Max(
                        _longestBlockedStreak.GetValueOrDefault(id),
                        streak);
                }
                else
                {
                    _blockedStreak[id] = 0;
                }

                // Criterion 2 of Issue #228, measured directly: was this creature
                // in the matching pool on this tick? `MatchJobs` writes a decision
                // for every creature it considered — a `chosen_*` when it handed
                // work out and a diagnostic when it had none — so a fresh decision
                // of either shape on this tick is the observable form of "the
                // matching saw it". A tick with neither is a tick on which the
                // creature was not a candidate at all, whatever the contract says
                // about the walk to the quarters.
                if (creature.CurrentJobId is null && creature.Mode != CreatureMode.Downed)
                {
                    _joblessTicks++;
                    if (codes.Any(InThePool))
                    {
                        _joblessAndInThePool++;
                    }
                    else
                    {
                        // Why the matching did not see it. The conditions are
                        // `PrototypeWorld.MatchJobs`'s own candidate filter, read
                        // off the snapshot: a reserved meal, a muster, a fight, a
                        // collapse — or none of those, which would be the
                        // interesting answer.
                        var why = creature.Mode == CreatureMode.Eating || creature.MealReserved
                            ? "a meal is reserved"
                            : creature.IsMustering
                                ? "assembling for a wave"
                                : creature.Mode is CreatureMode.Fighting or CreatureMode.Fled
                                    ? $"mode={creature.Mode}"
                                    : creature.Satiety < PrototypeTuning.CollapseThreshold
                                        ? "below the collapse threshold"
                                        : "no candidate condition explains it";
                        _missedThePool[why] = _missedThePool.GetValueOrDefault(why) + 1;
                    }
                }

                TrackOffers(current, creature, before, acted, area);
            }
        }

        /// <summary>
        /// How long work waits for this creature. One run per (creature, job): the
        /// consecutive ticks a job stayed unreserved, reachable and open to this
        /// creature while the creature stood idle in the quarters.
        /// </summary>
        private void TrackOffers(
            PrototypeSnapshot snapshot,
            PrototypeCreatureSnapshot creature,
            PrototypeCreatureSnapshot before,
            int acted,
            (IReadOnlySet<GridPoint> Zone, IReadOnlySet<GridPoint> Halo) area)
        {
            if (creature.CurrentJobId is not null)
            {
                if (before.CurrentJobId is null)
                {
                    // It took work. Every run this creature had open is answered.
                    foreach (var key in _offered.Keys.Where(key => key.Creature == creature.Id).ToArray())
                    {
                        _offeredRuns.Add(_offered[key] with
                        {
                            Ending = key.Job == creature.CurrentJobId
                                ? "took this very job"
                                : "took other work",
                        });
                        _offerAnswered++;
                        _offered.Remove(key);
                    }

                    _jobTakenAt[creature.Id] = acted;
                }
                else if (_jobTakenAt.TryGetValue(creature.Id, out var takenAt) &&
                         creature.MoveCount > before.MoveCount)
                {
                    _firstStepDelays.Add(acted - takenAt);
                    _jobTakenAt.Remove(creature.Id);
                }

                return;
            }

            _jobTakenAt.Remove(creature.Id);
            // What the matching said to this creature on this tick. A job that
            // sits unreserved while the matching answers `refused_rule_min_satiety`
            // was not withheld by the walk to the quarters — it was withheld by a
            // rule, and the difference is the whole point of the measurement.
            var reason = snapshot.Events
                .Where(item => item.CreatureId == creature.Id && item.LastTick == acted && InThePool(item))
                .Select(item => item.ReasonCode)
                .FirstOrDefault() ?? "(the matching did not see it)";
            var walls = Walls(snapshot);
            var open = snapshot.Jobs
                .Where(job =>
                    job.ReservedBy is null &&
                    (job.PersonalCreatureId is null || job.PersonalCreatureId == creature.Id) &&
                    snapshot.Priorities.GetValueOrDefault(job.Kind) > 0 &&
                    Distance(creature.Position, job.Target, walls) is not null)
                .ToDictionary(job => job.JobId, job => job.Kind);

            foreach (var key in _offered.Keys
                         .Where(key => key.Creature == creature.Id && !open.ContainsKey(key.Job))
                         .ToArray())
            {
                // Somebody else took it, or it stopped existing. Not this
                // creature's slowness, and the run is closed without a verdict.
                _offeredRuns.Add(_offered[key] with { Ending = "the job went elsewhere" });
                _offerUnanswered++;
                _offered.Remove(key);
            }

            foreach (var (jobId, kind) in open)
            {
                var key = (creature.Id, jobId);
                _offered[key] = _offered.TryGetValue(key, out var run)
                    ? run with { Ticks = run.Ticks + 1, LastReason = reason }
                    : new Offer(creature.Id, jobId, kind, 1, "open", reason);
            }

            _ = area;
        }

        /// <summary>
        /// Who was standing on the tile the refused step was for, and what that
        /// creature was doing there.
        ///
        /// <para>The step is recomputed the way
        /// <see cref="PrototypePostCombatDispersalTests"/> recomputes it — the
        /// same BFS over the same map with the same (north, east, south, west)
        /// tie-break — because the journal publishes the destination of a refused
        /// step but not the tile it was refused onto. The blocker is then looked
        /// up in <paramref name="parking"/>: the tile the off-duty rule of Issue
        /// #201 hands that creature by its identifier. A blocker standing on its
        /// own parking tile is a body the rule put there and that nothing will
        /// move until work appears; a blocker anywhere else is ordinary
        /// traffic.</para>
        /// </summary>
        private void WhoWasInTheWay(
            PrototypeSnapshot snapshot,
            PrototypeCreatureSnapshot creature,
            IReadOnlyList<PrototypeEvent> codes,
            IReadOnlyDictionary<int, GridPoint> parking)
        {
            var destination = codes
                .Where(code => code.ReasonCode == "waiting_blocked_by_other")
                .Select(code => code.Target)
                .OfType<GridPoint>()
                .FirstOrDefault();
            if (destination == default)
            {
                _whoWasInTheWay["the journal named no destination"] =
                    _whoWasInTheWay.GetValueOrDefault("the journal named no destination") + 1;
                return;
            }

            if (parking.TryGetValue(creature.Id, out var mine) && mine == destination)
            {
                _blockedTowardsAnOffDutyTile++;
            }

            var walls = Walls(snapshot);
            var step = NextStep(creature.Position, destination, walls);
            if (step is not { } tile || tile == creature.Position)
            {
                Count("the arbitration itself; no tile to name");
                return;
            }

            if (snapshot.Creatures.FirstOrDefault(other => other.Position == tile) is not { } blocker)
            {
                Count("nobody; the arbitration refused it");
                return;
            }

            _blockedOn[tile] = _blockedOn.GetValueOrDefault(tile) + 1;

            // The same question Issue #186 asked of the ticks after a fight, asked
            // here of the ticks inside the quarters: with every other body treated
            // as a wall, does a route to the same destination still exist, and is
            // it at most DetourSlack steps longer? A high share means the tiles to
            // walk round by are there and `PrototypeMap.NextStep` cannot see them,
            // which is Issue #76; a low share would mean the creature is genuinely
            // walled in and only a yield could open the way.
            var bodies = snapshot.Creatures.Select(other => other.Position).ToHashSet();
            var blind = Distance(creature.Position, destination, walls);
            var seeing = Distance(creature.Position, destination, walls, bodies);
            if (seeing is null)
            {
                _walledIn++;
            }
            else if (blind is null || seeing.Value <= blind.Value + DetourSlack)
            {
                _wayRound++;
                if (blind is { } direct && seeing.Value > direct)
                {
                    _wayRoundStrictlyLonger++;
                }
            }
            else
            {
                _wayRoundTooLong++;
            }

            Count(blocker.Mode is CreatureMode.Downed or CreatureMode.Fighting
                ? $"a creature that cannot act (mode={blocker.Mode})"
                : Bunks.Contains(blocker.Position)
                    ? "a creature lying on a bunk"
                    : parking.TryGetValue(blocker.Id, out var parked) && parked == blocker.Position
                        ? "a creature parked on its own off-duty tile"
                        : blocker.LastMoveTick == snapshot.Tick - 1
                            ? "a creature that walked on this very tick"
                            : "a creature standing still somewhere else");

            void Count(string label) =>
                _whoWasInTheWay[label] = _whoWasInTheWay.GetValueOrDefault(label) + 1;
        }

        public WindowMeasurement ToMeasurement()
        {
            foreach (var value in _offered.Values)
            {
                _offeredRuns.Add(value with { Ending = "the window closed" });
                _offerUnanswered++;
            }

            _offered.Clear();
            return new WindowMeasurement(
                wave,
                endTick,
                cohort.Count,
                _observed,
                _cohortTicks,
                _zoneTicks,
                _haloTicks,
                _movedByCounter,
                _refusedByCounter,
                _mustering,
                new Dictionary<Cause, int>(_causes),
                new Dictionary<Cause, int>(_refusedAStep),
                new Dictionary<Cause, int>(_tookAStep),
                [.. _offeredRuns],
                _offerAnswered,
                _offerUnanswered,
                [.. _firstStepDelays],
                _joblessTicks,
                _joblessAndInThePool,
                new Dictionary<string, int>(_missedThePool),
                new Dictionary<string, int>(_whoWasInTheWay),
                new Dictionary<GridPoint, int>(_blockedOn),
                _blockedTowardsAnOffDutyTile,
                _wayRound,
                _wayRoundStrictlyLonger,
                _wayRoundTooLong,
                _walledIn,
                [.. _longestBlockedStreak.Values]);
        }
    }

    /// <summary>
    /// The ladder. First rung that claims the tick owns it, so the result is a
    /// partition rather than a histogram of overlapping facts.
    ///
    /// <para>The order is "what stopped this tick from being productive", most
    /// proximate first. A creature that walked is not standing; a body that is
    /// down is not idle; a creature refused a step was trying to move, whatever
    /// else the matching told it a moment earlier.</para>
    /// </summary>
    private static Cause Classify(
        PrototypeSnapshot snapshot,
        PrototypeCreatureSnapshot creature,
        PrototypeCreatureSnapshot before,
        IReadOnlyList<PrototypeEvent> codes)
    {
        if (creature.MoveCount > before.MoveCount)
        {
            return Cause.Stepped;
        }

        if (creature.Mode == CreatureMode.Downed)
        {
            return Cause.OffItsFeet;
        }

        if (creature.IsMustering)
        {
            return Cause.Mustering;
        }

        if (codes.Any(code => code.ReasonCode == "waiting_blocked_by_other"))
        {
            return Cause.BlockedByOthers;
        }

        // Three different failures share one reason code, and they are three
        // different answers to Issue #228. They are told apart by the arguments
        // the decision carries, which is the only place the difference survives
        // into the canonical log: `Move` writes the tile it could not path to,
        // `TryStartEating` writes the zone it could not reach, and the matching's
        // own diagnostic writes neither. The first two are read here; the third is
        // read below the errands, because "the zone of the work I would have taken
        // is unreachable" is a statement about work and not about this step.
        foreach (var code in codes.Where(item => item.ReasonCode == "refused_zone_unreachable"))
        {
            if (code.Details.ContainsKey("targetX"))
            {
                return Cause.RouteUnreachable;
            }

            if (code.Details.ContainsKey("zoneKind"))
            {
                return Cause.LarderUnreachable;
            }
        }

        if (creature.Mode == CreatureMode.Eating || creature.MealReserved)
        {
            return Cause.Eating;
        }

        if (creature.CurrentJobId is { } jobId)
        {
            return snapshot.Jobs.FirstOrDefault(job => job.JobId == jobId)?.Kind == JobKind.Rest
                ? Cause.Resting
                : Cause.Working;
        }

        if (codes.Any(code =>
                code.ReasonCode == "refused_zone_unreachable" &&
                !code.Details.ContainsKey("targetX") &&
                !code.Details.ContainsKey("zoneKind")))
        {
            return Cause.WorkZoneUnreachable;
        }

        if (codes.Any(code => MatchingHadNothing.Contains(code.ReasonCode)))
        {
            return Cause.NoWork;
        }

        if (before.CurrentJobId is not null)
        {
            return Cause.JobEnded;
        }

        if (creature.Satiety < PrototypeTuning.CollapseThreshold)
        {
            return Cause.Starving;
        }

        return Cause.Unclassified;
    }

    /// <summary>
    /// Where the ticks were spent and what the journal said about them, kept apart
    /// from the counters because it answers "where" and "which job" rather than
    /// "how much". Static and opt-in, so the shared matrix pays for it once.
    /// </summary>
    internal static MatrixDiagnostics? Diagnostics { get; set; }

    internal sealed class MatrixDiagnostics
    {
        private readonly Dictionary<(Cause, string), int> _byCode = [];
        private readonly Dictionary<(Cause, GridPoint), int> _byTile = [];
        private readonly Dictionary<(Cause, GridPoint), int> _byTarget = [];
        private readonly List<string> _samples = [];

        public void Count(
            Cause cause,
            int acted,
            PrototypeCreatureSnapshot creature,
            PrototypeCreatureSnapshot before,
            IReadOnlyList<PrototypeEvent> codes)
        {
            var label = codes.Count == 0
                ? $"(no decision this tick; last={creature.LastDecision.ReasonCode})"
                : string.Join("+", codes.Select(code =>
                    code.JobKind is { } kind ? $"{code.ReasonCode}:{kind}" : code.ReasonCode));
            _byCode[(cause, label)] = _byCode.GetValueOrDefault((cause, label)) + 1;
            _byTile[(cause, creature.Position)] = _byTile.GetValueOrDefault((cause, creature.Position)) + 1;
            foreach (var target in codes.Select(code => code.Target).OfType<GridPoint>())
            {
                _byTarget[(cause, target)] = _byTarget.GetValueOrDefault((cause, target)) + 1;
            }

            if (cause == Cause.Unclassified && _samples.Count < 20)
            {
                _samples.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"t{acted} #{creature.Id} at ({creature.Position.X},{creature.Position.Y}) " +
                    $"mode={creature.Mode} job={creature.CurrentJobId} satiety={creature.Satiety} " +
                    $"fatigue={creature.Fatigue} injury={creature.Injury} meal={creature.MealReserved} " +
                    $"muster={creature.IsMustering} codes={label} " +
                    $"| before: mode={before.Mode} job={before.CurrentJobId} " +
                    $"injury={before.Injury} meal={before.MealReserved} " +
                    $"last={before.LastDecision.ReasonCode}@{before.LastDecision.Tick}"));
            }
        }

        public override string ToString()
        {
            var report = new StringBuilder();
            report.AppendLine("decisions recorded on the tick, by cause:");
            foreach (var group in _byCode.GroupBy(pair => pair.Key.Item1).OrderBy(group => group.Key))
            {
                foreach (var entry in group.OrderByDescending(pair => pair.Value).Take(8))
                {
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"  {group.Key} {entry.Key.Item2} {entry.Value}");
                }
            }

            report.AppendLine("tiles stood on, by cause (top 8 each):");
            foreach (var group in _byTile.GroupBy(pair => pair.Key.Item1).OrderBy(group => group.Key))
            {
                foreach (var entry in group.OrderByDescending(pair => pair.Value).Take(8))
                {
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"  {group.Key} ({entry.Key.Item2.X},{entry.Key.Item2.Y}) {entry.Value}");
                }
            }

            report.AppendLine("targets of the decisions, by cause (top 8 each):");
            foreach (var group in _byTarget.GroupBy(pair => pair.Key.Item1).OrderBy(group => group.Key))
            {
                foreach (var entry in group.OrderByDescending(pair => pair.Value).Take(8))
                {
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"  {group.Key} -> ({entry.Key.Item2.X},{entry.Key.Item2.Y}) {entry.Value}");
                }
            }

            report.AppendLine("unclassified samples:");
            foreach (var sample in _samples)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {sample}");
            }

            return report.ToString();
        }
    }

    private static IReadOnlySet<GridPoint> Walls(PrototypeSnapshot snapshot)
    {
        var walls = new HashSet<GridPoint>(snapshot.Map.RockTiles);
        walls.UnionWith(snapshot.Zones[ZoneKind.Forbidden]);
        return walls;
    }

    /// <summary>
    /// Distance by the map, blind to bodies — the same thing
    /// <c>PrototypeMap.NextStep</c> sees. Restated here rather than exported,
    /// because a measurement that asks the code what it did cannot disagree with
    /// it.
    /// </summary>
    private static int? Distance(
        GridPoint start,
        GridPoint target,
        IReadOnlySet<GridPoint> walls,
        IReadOnlySet<GridPoint>? bodies = null)
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
                if (!InBounds(neighbor) || walls.Contains(neighbor) || distances.ContainsKey(neighbor))
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

    /// <summary>
    /// One stretch of ticks during which a single job stayed unreserved, reachable
    /// and open to a single ex-combatant standing idle in the quarters, and the
    /// way the stretch ended.
    /// </summary>
    internal sealed record Offer(
        int Creature,
        long Job,
        JobKind Kind,
        int Ticks,
        string Ending,
        string LastReason);

    internal sealed record PartyMeasurement(
        string Fixture,
        ulong Seed,
        int Ticks,
        int MusterLeadSeen,
        IReadOnlyList<WindowMeasurement> Windows)
    {
        public int CohortTicks => Windows.Sum(window => window.CohortTicks);

        public int QuartersTicks => Windows.Sum(window => window.ZoneTicks + window.HaloTicks);

        public int ZoneTicks => Windows.Sum(window => window.ZoneTicks);

        public int HaloTicks => Windows.Sum(window => window.HaloTicks);

        public int MovedByTheCanonicalCounter => Windows.Sum(window => window.MovedByTheCounter);

        public int RefusedByTheCanonicalCounter => Windows.Sum(window => window.RefusedByTheCounter);

        public IReadOnlyList<Offer> OfferedRuns => [.. Windows.SelectMany(window => window.OfferedRuns)];

        public int OfferAnswered => Windows.Sum(window => window.OfferAnswered);

        public int OfferUnanswered => Windows.Sum(window => window.OfferUnanswered);

        public IReadOnlyList<int> FirstStepDelays =>
            [.. Windows.SelectMany(window => window.FirstStepDelays)];

        public int JoblessTicks => Windows.Sum(window => window.JoblessTicks);

        public int JoblessAndInThePool => Windows.Sum(window => window.JoblessAndInThePool);

        public int BlockedTowardsAnOffDutyTile =>
            Windows.Sum(window => window.BlockedTowardsAnOffDutyTile);

        public int WayRound => Windows.Sum(window => window.WayRound);

        public int WayRoundStrictlyLonger => Windows.Sum(window => window.WayRoundStrictlyLonger);

        public int WayRoundTooLong => Windows.Sum(window => window.WayRoundTooLong);

        public int WalledIn => Windows.Sum(window => window.WalledIn);

        public IReadOnlyList<int> LongestBlockedStreaks =>
            [.. Windows.SelectMany(window => window.LongestBlockedStreaks)];

        public IReadOnlyDictionary<GridPoint, int> BlockedOn =>
            Windows
                .SelectMany(window => window.BlockedOn)
                .GroupBy(pair => pair.Key)
                .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value));

        public IReadOnlyDictionary<string, int> WhoWasInTheWay =>
            Windows
                .SelectMany(window => window.WhoWasInTheWay)
                .GroupBy(pair => pair.Key)
                .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value));

        public IReadOnlyDictionary<string, int> MissedThePool =>
            Windows
                .SelectMany(window => window.MissedThePool)
                .GroupBy(pair => pair.Key)
                .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value));

        public int Total(Cause cause) => Windows.Sum(window => window.Causes.GetValueOrDefault(cause));

        public int RefusedAStep(Cause cause) =>
            Windows.Sum(window => window.RefusedAStep.GetValueOrDefault(cause));

        public int TookAStep(Cause cause) =>
            Windows.Sum(window => window.TookAStep.GetValueOrDefault(cause));

        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"{Fixture}/{Seed} ticks={Ticks} windows={Windows.Count} " +
            $"cohortTicks={CohortTicks} quartersTicks={QuartersTicks} " +
            $"inZone={ZoneTicks} inHalo={HaloTicks} " +
            $"{string.Join(" ", Enum.GetValues<Cause>().Select(cause => $"{cause}={Total(cause)}"))}");
    }

    internal sealed record WindowMeasurement(
        int Wave,
        int EndTick,
        int CohortSize,
        int ObservedTicks,
        int CohortTicks,
        int ZoneTicks,
        int HaloTicks,
        int MovedByTheCounter,
        int RefusedByTheCounter,
        int MusteringTicks,
        IReadOnlyDictionary<Cause, int> Causes,
        IReadOnlyDictionary<Cause, int> RefusedAStep,
        IReadOnlyDictionary<Cause, int> TookAStep,
        IReadOnlyList<Offer> OfferedRuns,
        int OfferAnswered,
        int OfferUnanswered,
        IReadOnlyList<int> FirstStepDelays,
        int JoblessTicks,
        int JoblessAndInThePool,
        IReadOnlyDictionary<string, int> MissedThePool,
        IReadOnlyDictionary<string, int> WhoWasInTheWay,
        IReadOnlyDictionary<GridPoint, int> BlockedOn,
        int BlockedTowardsAnOffDutyTile,
        int WayRound,
        int WayRoundStrictlyLonger,
        int WayRoundTooLong,
        int WalledIn,
        IReadOnlyList<int> LongestBlockedStreaks)
    {
        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"wave={Wave} end={EndTick} cohort={CohortSize} observed={ObservedTicks} " +
            $"cohortTicks={CohortTicks} inZone={ZoneTicks} inHalo={HaloTicks} " +
            $"mustering={MusteringTicks} " +
            $"{string.Join(" ", Causes.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key}={pair.Value}"))}");
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
}
