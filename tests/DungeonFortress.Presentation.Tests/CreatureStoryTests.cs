using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The story of one creature (Issue #128): what it decided this party, read from
/// the panel a player is already looking at.
///
/// <para>
/// The owner played the first slice of memory of place and said: "Метки вижу, они
/// остаются на месте боев. Но без лога событий по каждому персонажу трудно понять,
/// как он реагирует на них." The exit criterion of the slice ends "…и как это
/// изменило его следующее решение", and until there was a per-creature log there
/// was nothing that answered it: the feed is the whole domain's and one creature
/// cannot be found in it, and the inspector is a slice of now.
/// </para>
///
/// <para>
/// Every panel here is a function of a snapshot, and that is the claim being made
/// as much as it is the way the tests are written: selecting a creature answers
/// the question out of facts the world has already published, so it needs no tick
/// to run.
/// </para>
///
/// <para>
/// Stepping a world is how most of these tests <b>reach</b> a snapshot worth
/// asking about — a party has to be played before anybody has a story — and
/// <see cref="A_refusal_by_memory_reads_in_the_creature_s_own_story"/> steps one
/// tick at a time because it is hunting for the tick a refusal lands on. What
/// none of them does is step a world <b>between</b> asking for a panel and
/// reading it. The claim is carried by
/// <see cref="Reading_a_creature_s_story_needs_no_tick_to_run"/>, which takes one
/// snapshot before its loop and answers for all nine creatures out of that one
/// object; the rest are ordinary tests that happen to need a played party as
/// their input.
/// </para>
/// </summary>
public sealed class CreatureStoryTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];
    private static readonly string[] Fixtures = ["baseline", "prepared"];

    /// <summary>
    /// The tick the measurements of Issue #140 were taken at: the whole shipped
    /// <c>baseline</c> party, four waves, the same run the evidence files record.
    /// </summary>
    private const int MeasuredTicks = 2_400;

    private static bool IsRefusalByMemory(PrototypeEvent @event) =>
        @event.ReasonCode is "refused_place_of_panic" or "refused_place_of_wound";

    /// <summary>
    /// The decisions a story panel is allowed to leave out — named one by one, so
    /// that "routine" is a judgement somebody made and not the default an
    /// unranked code falls into. Choosing work, waiting on something, refusing on
    /// a rule the player set, and every step of digging, hauling and building are
    /// what a creature spends its life doing; the inspector beside the panel
    /// already says which of them it is doing now.
    ///
    /// <para>
    /// Two entries are worth their own line. <c>combat_attack</c> is inside the
    /// fight rather than about it — a creature strikes many times per wave, and
    /// "it joined, it broke, it came back" is the shape of the story. And
    /// <c>refused_too_exhausted</c>/<c>refused_injured</c> are a state repeating
    /// itself rather than something happening: what happened is
    /// <c>injury_tended</c>, and that is ranked.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> DeliberatelyRoutine = new(StringComparer.Ordinal)
    {
        "chosen_highest_priority", "chosen_bottleneck", "chosen_affinity_match", "chosen_nearest",
        "chosen_only_option", "chosen_tie_break", "chosen_need_hunger", "chosen_need_fatigue",
        "chosen_muster", "chosen_ration", "chosen_traffic_yield",
        "waiting_no_job_available", "waiting_input_missing", "waiting_storage_full",
        "waiting_stock_sufficient", "waiting_crop_not_ripe", "waiting_blocked_by_other",
        "waiting_no_designation", "waiting_no_blueprint", "waiting_no_stockpile",
        "waiting_stockpile_full",
        "refused_zone_not_designated", "refused_zone_unreachable", "refused_priority_zero",
        "refused_rule_reserve", "refused_rule_min_satiety", "refused_too_exhausted",
        "refused_injured",
        "combat_attack",
        "dig_started", "dig_completed", "dig_cancelled", "dig_unreachable",
        "stone_picked_up", "stone_stored", "stone_spilled", "stone_target_replanned",
        "stone_haul_cancelled", "stone_unreachable", "stone_delivered",
        "build_started", "build_completed", "build_cancelled", "build_no_stone",
        "build_waiting_material", "build_unreachable",
    };

    /// <summary>
    /// What the panel orders by, stated in the test rather than borrowed from the
    /// code: what a decision means to the creature first, when it happened second.
    /// </summary>
    private static (int Weight, int Tick) Rank(PrototypeEvent @event) =>
        (HudText.StoryWeight(@event.ReasonCode), @event.LastTick);

    /// <summary>Whether this line of the panel is this entry of the journal.</summary>
    private static bool Renders(PrototypeEvent @event, string line) =>
        line.StartsWith(
            string.Create(CultureInfo.InvariantCulture, $"t{@event.FirstTick}"),
            StringComparison.Ordinal) &&
        line.Contains(
            EventNarration.Sentence(
                @event.ReasonCode,
                @event.Details,
                @event.JobKind,
                @event.Target),
            StringComparison.Ordinal);

    /// <summary>
    /// The panel is the canonical journal and nothing else, and the rule by which
    /// it picks four of six hundred entries is stated here rather than restated
    /// from the code: <b>one entry per kind of decision, the newest of that kind,
    /// and the kinds that matter most</b>.
    ///
    /// <para>
    /// Five claims, each of which a different mistake would break:
    /// every line is an entry of this creature's journal, rendered from that
    /// entry alone; the entry shown for a kind is the <b>last</b> one of that
    /// kind the world wrote, so two entries of one kind on one tick are told
    /// apart by write order and not by an accident of the search; no two lines
    /// are the same kind of decision; nothing left off the panel outranks
    /// anything on it, in the order "what it means, then when it happened"; and
    /// the lines are in time order, newest first.
    /// </para>
    ///
    /// <para>
    /// The tie-break is stated here as the desired behaviour and computed from
    /// the journal — the last entry among those at the kind's highest tick —
    /// rather than borrowed from the code. It used to be written as
    /// <c>kind.MaxBy(LastTick)</c>, which is a restatement of what
    /// <see cref="HudText.StorySelection"/> did, and it therefore pinned a wrong
    /// answer: <c>MaxBy</c> returns the <em>first</em> element at the highest key,
    /// so on <c>baseline</c> the panel showed the job-cancelling
    /// <c>combat_joined</c> written a line before the real one and printed
    /// "joined the fight for wave ?.". A check that copies the implementation
    /// cannot disagree with it.
    /// </para>
    ///
    /// <para>
    /// Asserted over the whole matrix and over every creature in it, because the
    /// claim "the show never disagrees with the journal" is worth nothing if it is
    /// only tried on one party.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void The_story_a_creature_shows_is_the_journal_ranked_by_what_it_meant(string fixtureName)
    {
        var creaturesSeen = 0;
        var kindsWhoseNewestIsATie = 0;
        foreach (var seed in MatrixSeeds)
        {
            var state = EndOfParty(fixtureName, seed);
            foreach (var creature in state.Creatures)
            {
                creaturesSeen++;
                var mine = state.Events.Where(@event => @event.CreatureId == creature.Id).ToArray();
                var newestOfEachKind = mine
                    .GroupBy(@event => @event.ReasonCode, StringComparer.Ordinal)
                    .Select(kind =>
                    {
                        var latest = kind.Max(@event => @event.LastTick);
                        if (kind.Count(@event => @event.LastTick == latest) > 1)
                        {
                            kindsWhoseNewestIsATie++;
                        }

                        return kind.Last(@event => @event.LastTick == latest);
                    })
                    .ToArray();
                var lines = Body(HudText.CreatureStory(state, creature.Id));
                var where = $"{fixtureName}/{seed}/{creature.Name}";

                // One line per kind, and never the same kind twice: four lines of
                // one refusal is as poor a story as four lines of traffic.
                Assert.Equal(
                    Math.Min(HudText.CreatureStoryLines, newestOfEachKind.Length),
                    lines.Length);
                var shown = lines
                    .Select(line => Assert.Single(
                        newestOfEachKind.Where(@event => Renders(@event, line))))
                    .ToArray();
                Assert.Equal(
                    shown.Length,
                    shown.Select(@event => @event.ReasonCode).Distinct(StringComparer.Ordinal).Count());

                // Nothing off the panel outranks anything on it.
                var floor = shown.Min(Rank);
                foreach (var missing in newestOfEachKind.Except(shown))
                {
                    // Not strictly below: two kinds can end on the same tick with
                    // the same weight, and which of them the panel took is then an
                    // arbitrary tie the journal's own order settles. What must
                    // never happen is something that outranks the panel's floor
                    // being left off it.
                    Assert.True(
                        Comparer<(int Weight, int Tick)>.Default.Compare(Rank(missing), floor) <= 0,
                        $"{where}: '{missing.ReasonCode}' at t{missing.LastTick} was left off the " +
                        $"panel while something that means less to this creature is on it. The " +
                        "panel spends its lines from the top of HudText.StoryWeight down.");
                }

                // Newest first, so the panel is read bottom to top as it happened.
                Assert.Equal(shown.OrderByDescending(@event => @event.LastTick).ToArray(), shown);
            }
        }

        Assert.True(
            creaturesSeen >= 3 * 3,
            $"{fixtureName}: only {creaturesSeen} creature-parties were read, which is too few for " +
            "the comparison above to have been made against anything.");
        Assert.True(
            kindsWhoseNewestIsATie > 0,
            $"{fixtureName}: no kind of decision on this matrix ended with two entries on the same " +
            "tick, so the tie-break above was never exercised and the claim about it proves nothing. " +
            "Either the world stopped writing two entries of one kind in one tick, or this is no " +
            "longer the matrix to read that on.");
    }

    /// <summary>
    /// Every reason code the shipped matrix produces has been ranked <b>on
    /// purpose</b> — either as something that matters to a creature or as one of
    /// the routine decisions this file names.
    ///
    /// <para>
    /// <see cref="HudText.StoryWeight"/> answers "routine" for a code it has
    /// never heard of, and that default is the right one — an unranked code must
    /// not be promoted to a turning point by accident. But a default is also how
    /// a new kind of turning point would arrive silently and never reach a panel,
    /// so the list of things deliberately called routine is written out here and
    /// a code on neither list fails. This is the same guard
    /// <c>EventNarrationTests.Every_reason_code_the_matrix_produces_has_a_sentence</c>
    /// keeps over the wording.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_reason_code_the_matrix_produces_is_ranked_on_purpose()
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var fixtureName in new[] { "baseline", "prepared", "neglected" })
        {
            var state = PresentationFixtures.RunFixture(fixtureName, PrototypeTuning.SessionTicks);
            foreach (var @event in state.Events)
            {
                seen.Add(@event.ReasonCode);
            }
        }

        var unranked = seen
            .Where(code => HudText.StoryWeight(code) == 0 && !DeliberatelyRoutine.Contains(code))
            .ToArray();
        output.WriteLine(string.Join(
            "\n",
            seen.Select(code => $"{HudText.StoryWeight(code)}  {code}")));
        Assert.True(
            unranked.Length == 0,
            $"{unranked.Length} reason code(s) the shipped fixtures produce are routine only because " +
            $"nobody said otherwise: {string.Join(", ", unranked)}. Rank them in HudText.StoryWeight " +
            "or name them in DeliberatelyRoutine, but do not let a story panel decide it by default.");
        Assert.True(
            seen.Count >= 20,
            $"only {seen.Count} distinct reason codes were exercised, which is too few for this to " +
            "be a guard at all.");
    }

    /// <summary>
    /// The bound, and the fact that the panel says what it is hiding — <b>both
    /// how much and of what kind</b>.
    ///
    /// <para>
    /// A party leaves a creature with hundreds of journal entries and the panel is
    /// worth about nine drawn lines at the frame the HUD is authored for. Showing
    /// four of six hundred is the only thing that fits; showing four of six
    /// hundred <em>without saying so</em> would make a player believe a creature
    /// decided four things all party. So the header carries three numbers — shown,
    /// in all, and how many of them meant anything — and the two that are not
    /// shown are the difference between them: the older beats of the story, and
    /// the routine.
    /// </para>
    ///
    /// <para>
    /// The word "last" is asserted <em>absent</em>. It was true while the panel
    /// was the tail of the journal and became a lie the moment the panel started
    /// choosing (Issue #140); a header that kept it would be the same defect this
    /// issue is about, told from the other end.
    /// </para>
    /// </summary>
    [Fact]
    public void The_story_is_bounded_and_the_header_says_what_is_off_the_panel()
    {
        var report = new StringBuilder();
        var longest = 0;
        var truncated = 0;
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                var state = EndOfParty(fixtureName, seed);
                foreach (var creature in state.Creatures)
                {
                    var mine = state.Events
                        .Where(@event => @event.CreatureId == creature.Id)
                        .ToArray();
                    longest = Math.Max(longest, mine.Length);
                    var panel = HudText.CreatureStory(state, creature.Id);
                    var lines = Body(panel);
                    var head = Head(panel);

                    Assert.True(
                        lines.Length <= HudText.CreatureStoryLines,
                        $"{fixtureName}/{seed}: {creature.Name} shows {lines.Length} lines of story, " +
                        $"over the {HudText.CreatureStoryLines} the panel is worth. Text that does " +
                        "not fit its label is dropped or drawn over the panel below it.");
                    Assert.Contains(creature.Name, head, StringComparison.Ordinal);
                    Assert.DoesNotContain("last", head, StringComparison.Ordinal);

                    if (lines.Length == mine.Length)
                    {
                        Assert.Contains(
                            string.Create(CultureInfo.InvariantCulture, $"{mine.Length} in all"),
                            head,
                            StringComparison.Ordinal);
                        continue;
                    }

                    truncated++;
                    var mattered = mine.Count(@event => HudText.StoryWeight(@event.ReasonCode) > 0);
                    Assert.Contains(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{lines.Length} of {mine.Length} · {mattered} mattered"),
                        head,
                        StringComparison.Ordinal);
                }

                report.AppendLine(CultureInfo.InvariantCulture,
                    $"{fixtureName}/{seed}: longest story so far {longest} entries");
            }
        }

        output.WriteLine(report.ToString());
        Assert.True(
            longest > HudText.CreatureStoryLines,
            $"no creature on the matrix ended a party with more than {HudText.CreatureStoryLines} " +
            "journal entries, so the bound was never reached and this test proved nothing about it.");
        Assert.True(
            truncated > 0,
            "no panel on the matrix was truncated, so the header arm that says what is off the " +
            "panel was never read.");
    }

    /// <summary>
    /// Reading a creature's story needs no tick.
    ///
    /// <para>
    /// This is the "projection against world" line of the issue, and it is checked
    /// by construction rather than argued: one snapshot, taken once, answers the
    /// question for every creature in it, and each answer is that creature's own.
    /// A show that needed the world to run would need a different snapshot per
    /// creature, and there is only one here.
    /// </para>
    /// </summary>
    [Fact]
    public void Reading_a_creature_s_story_needs_no_tick_to_run()
    {
        var state = EndOfParty("prepared", MatrixSeeds[0]);
        var stories = state.Creatures.ToDictionary(
            creature => creature.Id,
            creature => HudText.Feedback(View(state, selectedCreatureId: creature.Id)));

        foreach (var creature in state.Creatures)
        {
            // Every one of these came out of the same snapshot object, taken once
            // before the loop: the answer for the ninth creature is as available
            // as the answer for the first, and no world was stepped between them.
            Assert.StartsWith(
                HudText.CreatureStory(state, creature.Id),
                stories[creature.Id],
                StringComparison.Ordinal);
            Assert.Contains(creature.Name, stories[creature.Id], StringComparison.Ordinal);
            foreach (var other in state.Creatures.Where(item => item.Id != creature.Id))
            {
                // Two creatures can legitimately decide the same thing, so the
                // panels are not required to differ. What is required is that the
                // panel names the creature it was asked about and nobody else.
                Assert.DoesNotContain(
                    $"STORY · {other.Name}",
                    stories[creature.Id],
                    StringComparison.Ordinal);
            }
        }

        Assert.True(state.Creatures.Count > 1);
    }

    /// <summary>
    /// With nothing selected the panel is the domain's feed. The feed is not
    /// replaced by a story, it is scoped: click a creature and the panel is about
    /// that creature, click away and it is about the domain again.
    ///
    /// <para>
    /// The domain feed has a rule of its own since Issue #145 and
    /// <c>DomainFeedTests</c> is where it is checked. What this asserts is only the
    /// boundary between the two panels, which is what Issue #128 built and what a
    /// change to either of them could break.
    /// </para>
    /// </summary>
    [Fact]
    public void Nothing_selected_leaves_the_domain_feed_and_not_a_story()
    {
        var state = PresentationFixtures.Baseline(400);
        var feedback = HudText.Feedback(View(state, diagnosticCount: 2));
        var lines = feedback.Split('\n');

        Assert.StartsWith("EVENT FEEDBACK", lines[0], StringComparison.Ordinal);
        Assert.Equal(3 + HudText.DomainFeedLines, lines.Length);
        Assert.EndsWith(
            "Diagnostics: 2",
            feedback,
            StringComparison.Ordinal);
        Assert.DoesNotContain("STORY", feedback, StringComparison.Ordinal);
    }

    /// <summary>
    /// The diagnostics counter stays on the domain feed and is not carried into a
    /// creature's story.
    ///
    /// <para>
    /// It is a decision and not an omission, which is why it has a check. The
    /// counter plus the blank line above it is three of the ten drawn lines this
    /// panel has at the tightest frame the HUD guard measures — more than one
    /// entry of a story — and it is a fact about the session rather than about
    /// the creature that was clicked. Deselecting brings it straight back.
    /// </para>
    /// </summary>
    [Fact]
    public void The_diagnostics_counter_belongs_to_the_domain_feed_and_not_to_a_story()
    {
        var state = EndOfParty("baseline", MatrixSeeds[0]);
        var creature = state.Creatures[0];

        Assert.DoesNotContain(
            "Diagnostics:",
            HudText.Feedback(View(state, selectedCreatureId: creature.Id, diagnosticCount: 3)),
            StringComparison.Ordinal);
        Assert.Contains(
            "Diagnostics: 3",
            HudText.Feedback(View(state, diagnosticCount: 3)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Criterion 1 of Issue #140, as a run rather than as an eye: <b>every</b>
    /// creature that refused work by memory of a place at any point of a party
    /// reads that refusal on its own panel.
    ///
    /// <para>
    /// Before the selection of #140 this failed for every creature it applies to.
    /// A party leaves creature #0 with 419 journal entries of which 14 are
    /// refusals by memory; the four newest entries are "somebody in the way" and
    /// "nothing to work with", so the sentence the whole slice exists for was
    /// measured to be unreachable on the panel — 0 of 3 creatures showed it.
    /// The numbers are in <c>evidence/140-before.json</c>.
    /// </para>
    ///
    /// <para>
    /// <b>The criterion is the same; the sample is the matrix now, and that is a
    /// correction rather than a widening.</b> It used to read one cell — the
    /// <c>baseline</c> journal on its own seed at tick 2400 — and guard itself
    /// with "at least three creatures refused by memory here". Counted over the
    /// six runs of the matrix on <c>main</c>, that number is 3, 2, 2, 0, 4, 2:
    /// the guard passes on exactly one cell and does so with no slack at all, and
    /// on a second cell there would have been nothing to check at all. A
    /// criterion about every creature that refused was being read on whichever
    /// creatures one seed happened to produce.
    /// </para>
    ///
    /// <para>
    /// Reading the whole matrix removes the seed from the answer: on <c>main</c>
    /// it collects 13 creature-parties and on this branch 15, so Issue #129 gives
    /// the criterion <b>more</b> subjects rather than fewer, and the old form
    /// reddened only because it was looking at one cell. The sample floor is now
    /// a rule instead of a figure — as many subjects as a party has creatures,
    /// counted off the party rather than written down — plus the requirement that
    /// they come from more than one run, so no single party can carry the
    /// criterion on its own again.
    /// </para>
    ///
    /// <para>
    /// Command: <c>dotnet test tests/DungeonFortress.Presentation.Tests -c Release
    /// --filter "FullyQualifiedName~Every_creature_that_refused_by_memory" --logger
    /// "console;verbosity=detailed"</c>. The per-cell counts are in
    /// <c>evidence/129-presentation.json</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_creature_that_refused_by_memory_reads_that_refusal_on_its_panel()
    {
        var report = new StringBuilder();
        var missing = new List<string>();
        var applies = 0;
        var runsWithASubject = 0;
        var creaturesInAParty = 0;
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                var state = EndOfParty(fixtureName, seed);
                creaturesInAParty = state.Creatures.Count;
                var here = 0;
                foreach (var creature in state.Creatures)
                {
                    var mine = state.Events
                        .Where(@event => @event.CreatureId == creature.Id)
                        .ToArray();
                    var refusals = mine.Where(IsRefusalByMemory).ToArray();
                    if (refusals.Length == 0)
                    {
                        continue;
                    }

                    applies++;
                    here++;
                    var panel = HudText.CreatureStory(state, creature.Id);
                    var read = refusals.Any(refusal => panel.Contains(
                        EventNarration.Sentence(
                            refusal.ReasonCode,
                            refusal.Details,
                            refusal.JobKind,
                            refusal.Target),
                        StringComparison.Ordinal));
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"{fixtureName}/{seed} {creature.Name}: {refusals.Length} refusals by " +
                        $"memory among {mine.Length} entries — " +
                        $"{(read ? "on the panel" : "NOT on the panel")}");
                    if (!read)
                    {
                        report.AppendLine(panel);
                        missing.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{fixtureName}/{seed} {creature.Name} refused by memory " +
                            $"{refusals.Length} time(s) and its panel says none of it"));
                    }
                }

                if (here > 0)
                {
                    runsWithASubject++;
                }
            }
        }

        output.WriteLine(report.ToString());
        Assert.True(
            applies >= creaturesInAParty,
            $"only {applies} creature-parties over the whole matrix ever refused by memory, fewer " +
            $"than the {creaturesInAParty} creatures of a single party, so this criterion was " +
            "checked against almost nothing. Either the memory of place stopped firing or the " +
            "matrix stopped reaching a fight.");
        Assert.True(
            runsWithASubject >= 2,
            $"all {applies} subjects came from {runsWithASubject} run(s) of the matrix. A criterion " +
            "carried by one party is a criterion about that party.");
        Assert.True(
            missing.Count == 0,
            $"{missing.Count} of {applies} creatures hide the one decision that explains their next " +
            $"one: {string.Join("; ", missing)}. A story panel that drops the refusal by memory answers " +
            "\"what is it doing\" and not \"what happened to it\".");
    }

    /// <summary>
    /// The number every document of this issue leans on: <b>how much of what a
    /// creature decides is routine</b>. It is why the newest four entries were
    /// almost never a story, and it is quoted in contract §11.1, in
    /// <c>PROTOTYPE_GRAYBOX.md</c>, in <see cref="HudText"/> and in
    /// <c>evidence/140-after.json</c>.
    ///
    /// <para>
    /// It lives here as a run rather than in a document as a number, because a
    /// measured number in four documents and nowhere executable is a number that
    /// drifts: the first version of this issue said "89 %", which was in fact
    /// four particular codes of one particular creature (726 of Уголёк's 820)
    /// generalised to the journal. The run below is the command those documents
    /// name, and the assertion is deliberately a floor rather than the figure —
    /// the figure is what the run prints, and a floor is the part of it a
    /// document is entitled to rely on.
    /// </para>
    /// </summary>
    [Fact]
    public void Most_of_what_a_creature_decides_is_routine()
    {
        var state = PresentationFixtures.RunFixture("baseline", MeasuredTicks);
        var report = new StringBuilder();
        var routine = state.Events.Count(@event => HudText.StoryWeight(@event.ReasonCode) == 0);
        var share = 100.0 * routine / state.Events.Count;
        report.AppendLine(CultureInfo.InvariantCulture,
            $"baseline t{MeasuredTicks}: {routine} of {state.Events.Count} entries are routine " +
            $"({share:0.0}%)");

        var lowest = 100.0;
        foreach (var creature in state.Creatures)
        {
            var mine = state.Events.Where(@event => @event.CreatureId == creature.Id).ToArray();
            var mineRoutine = mine.Count(@event => HudText.StoryWeight(@event.ReasonCode) == 0);
            var mineShare = 100.0 * mineRoutine / mine.Length;
            lowest = Math.Min(lowest, mineShare);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  {creature.Name}: {mineRoutine} of {mine.Length} ({mineShare:0.0}%)");
        }

        output.WriteLine(report.ToString());
        Assert.True(
            share >= 90.0,
            $"routine is {share:0.0}% of the journal on this party, under the 90% the documents of " +
            "Issue #140 rely on. The number in contract §11.1, PROTOTYPE_GRAYBOX.md, HudText and " +
            "evidence/140-after.json is now wrong and has to be re-measured with this run.");
        Assert.True(
            lowest >= 90.0,
            $"the least routine creature of this party is at {lowest:0.0}%, under the 90% the same " +
            "documents claim holds for every creature.");
    }

    /// <summary>
    /// A creature that did the same thing fourteen times does not get fourteen
    /// lines of it. One line per kind of decision is what turns four slots into
    /// four beats of a story.
    ///
    /// <para>
    /// Ranking without this rule would be the defect it was written to fix, moved
    /// rather than removed: creature #0 of the baseline party refused work by
    /// memory fourteen times, and a panel of four identical refusals says as
    /// little as a panel of four "somebody in the way". The check is aimed at a
    /// creature that actually repeats itself, and it says how much it repeated,
    /// so a party where nothing repeats cannot make it pass by default.
    /// </para>
    /// </summary>
    [Fact]
    public void A_decision_a_creature_took_again_and_again_takes_one_line_of_its_story()
    {
        var state = PresentationFixtures.RunFixture("baseline", MeasuredTicks);
        var repeated = 0;
        foreach (var creature in state.Creatures)
        {
            var mine = state.Events.Where(@event => @event.CreatureId == creature.Id).ToArray();
            var lines = Body(HudText.CreatureStory(state, creature.Id));
            var kinds = lines
                .SelectMany(line => mine.Where(@event => Renders(@event, line)))
                .Select(@event => @event.ReasonCode)
                .ToArray();

            Assert.Equal(kinds.Length, kinds.Distinct(StringComparer.Ordinal).Count());
            var worst = mine
                .GroupBy(@event => @event.ReasonCode, StringComparer.Ordinal)
                .Max(kind => kind.Count());
            repeated = Math.Max(repeated, worst);
        }

        Assert.True(
            repeated >= HudText.CreatureStoryLines * 2,
            $"the busiest kind of decision on this party was taken {repeated} times, which is too " +
            "few for a panel to have been at risk of filling with it.");
    }

    /// <summary>
    /// Before anything has happened to a creature, the panel is still full of
    /// what it has been doing. Ranking decides the <em>order</em> of the four
    /// lines and not whether there are four: a party spends its first thousand
    /// ticks with nothing dramatic in it, and a panel that went blank until the
    /// first wave would answer "what happened to it" with silence for the whole
    /// of that time.
    /// </summary>
    [Fact]
    public void Before_anything_has_happened_the_panel_is_still_what_it_has_been_doing()
    {
        var state = PresentationFixtures.RunFixture("baseline", 600);
        Assert.DoesNotContain(
            state.Events,
            @event => HudText.StoryWeight(@event.ReasonCode) > 0);

        foreach (var creature in state.Creatures)
        {
            var mine = state.Events.Count(@event => @event.CreatureId == creature.Id);
            var lines = Body(HudText.CreatureStory(state, creature.Id));
            Assert.Equal(HudText.CreatureStoryLines, lines.Length);
            Assert.True(mine > HudText.CreatureStoryLines);
            Assert.Contains("0 mattered", Head(HudText.CreatureStory(state, creature.Id)), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The first minutes of a party, where the panel is genuinely shorter than
    /// four lines — and where the whole claim above is untrue and a different
    /// one holds instead.
    ///
    /// <para>
    /// One line per kind means the panel is as tall as the creature has
    /// <b>kinds</b> of decision. At tick 20 a creature has usually taken one
    /// kind and the panel is one line; the old panel showed four lines there,
    /// all of them the same sentence. This is the cost of the rule, it is real,
    /// and the check stands where it is real rather than at tick 600 where it
    /// has already gone away: it says how many creatures are short at each of
    /// the three ticks, so a party that stopped being short here fails instead
    /// of passing quietly.
    /// </para>
    ///
    /// <para>
    /// What must hold everywhere is the other half: the panel is never empty
    /// while the creature has decided anything, it is exactly one line per kind
    /// up to the bound, and the header keeps saying how many entries are behind
    /// it — so a one-line panel reads as "41 decisions, three kinds, nothing has
    /// happened to it" and not as "it has done one thing".
    /// </para>
    /// </summary>
    [Fact]
    public void Early_in_a_party_the_panel_is_shorter_than_four_lines_and_says_so()
    {
        var report = new StringBuilder();
        var shortPanels = new Dictionary<int, int>();
        foreach (var tick in new[] { 20, 40, 600 })
        {
            var state = PresentationFixtures.RunFixture("baseline", tick);
            shortPanels[tick] = 0;
            foreach (var creature in state.Creatures)
            {
                var mine = state.Events
                    .Where(@event => @event.CreatureId == creature.Id)
                    .ToArray();
                var kinds = mine.Select(@event => @event.ReasonCode).Distinct(StringComparer.Ordinal).Count();
                var panel = HudText.CreatureStory(state, creature.Id);
                var lines = Body(panel);

                Assert.Equal(Math.Min(HudText.CreatureStoryLines, kinds), lines.Length);
                Assert.NotEmpty(lines);
                Assert.Contains(
                    lines.Length == mine.Length
                        ? string.Create(CultureInfo.InvariantCulture, $"{mine.Length} in all")
                        : string.Create(CultureInfo.InvariantCulture, $"{lines.Length} of {mine.Length}"),
                    Head(panel),
                    StringComparison.Ordinal);
                if (lines.Length < HudText.CreatureStoryLines)
                {
                    shortPanels[tick]++;
                }

                report.AppendLine(CultureInfo.InvariantCulture,
                    $"t{tick} {creature.Name}: {mine.Length} entries, {kinds} kinds, {lines.Length} lines");
            }
        }

        output.WriteLine(report.ToString());
        Assert.True(
            shortPanels[20] >= 8,
            $"only {shortPanels[20]} creatures of nine had a panel under {HudText.CreatureStoryLines} " +
            "lines at tick 20, so the shrink this check exists to pin was not there to see.");
        Assert.True(
            shortPanels[40] >= 1,
            $"{shortPanels[40]} creatures were short at tick 40; the shrink is supposed to still be " +
            "visible there and to be gone by tick 600.");
        Assert.Equal(0, shortPanels[600]);
    }

    /// <summary>
    /// The question the slice exists for, asked of the panel: a creature that
    /// refused work because of a place it remembers says so <b>in its own story</b>,
    /// with the place and the tick it has remembered it since.
    ///
    /// <para>
    /// This is where Issue #128 and Issue #125 meet. The refusal is the one
    /// sentence in a creature's history that answers "how did what happened to it
    /// change what it did next", and it is worth nothing on a panel if it names
    /// the wrong work — which is why the two were done together.
    /// </para>
    /// </summary>
    [Fact]
    public void A_refusal_by_memory_reads_in_the_creature_s_own_story()
    {
        var found = 0;
        var report = new StringBuilder();
        foreach (var fixtureName in Fixtures)
        {
            var world = new PrototypeWorld(Fixture(fixtureName) with { Seed = MatrixSeeds[0] });
            while (!world.IsComplete && found < 2)
            {
                world.Step();
                var state = world.GetSnapshot();
                var refuser = state.Creatures.FirstOrDefault(creature =>
                    creature.LastDecision.Tick == state.Tick - 1 &&
                    creature.LastDecision.ReasonCode is
                        "refused_place_of_panic" or "refused_place_of_wound");
                if (refuser is null)
                {
                    continue;
                }

                var panel = HudText.Feedback(View(state, selectedCreatureId: refuser.Id));
                var newest = Body(HudText.CreatureStory(state, refuser.Id))[0];
                var place = refuser.LastDecision.Details;
                Assert.Contains(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"({place["placeX"]},{place["placeY"]}) t{place["sinceTick"]}"),
                    newest,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    refuser.LastDecision.ReasonCode,
                    panel,
                    StringComparison.Ordinal);
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"{fixtureName} t{state.Tick - 1}: {newest}");
                found++;
            }
        }

        output.WriteLine(report.ToString());
        Assert.True(
            found >= 2,
            $"only {found} refusals by memory were reachable on the two fixtures, so the sentence " +
            "the whole slice exists for was never actually read off a panel.");
    }

    /// <summary>
    /// A decision the journal folded prints the span it held and how many ticks it
    /// held for. "It refused this for thirty-six ticks" and "it refused this once"
    /// are different stories, and contract 11.1 is the reason the difference
    /// arrives as a count instead of thirty-six lines.
    /// </summary>
    [Fact]
    public void A_decision_that_held_for_several_ticks_prints_its_span_and_its_count()
    {
        var state = EndOfParty("baseline", MatrixSeeds[0]);
        var folded = state.Creatures
            .Select(creature => new
            {
                creature.Id,
                Event = HudText
                    .StorySelection(state.Events.Where(@event => @event.CreatureId == creature.Id))
                    .FirstOrDefault(@event => @event.Repeats > 1 && @event.FirstTick != @event.LastTick),
            })
            .FirstOrDefault(item => item.Event is not null);

        Assert.True(
            folded is not null,
            "no creature ended this party with a folded decision among the entries the panel shows, " +
            "so the span wording was never exercised. Either the deduplication rule of contract 11.1 " +
            "stopped folding, or this fixture stopped being the one to read it on.");

        var held = folded!.Event!;
        var line = Body(HudText.CreatureStory(state, folded.Id))
            .Single(item => item.StartsWith(
                string.Create(CultureInfo.InvariantCulture, $"t{held.FirstTick}-{held.LastTick} "),
                StringComparison.Ordinal));
        Assert.EndsWith(
            string.Create(CultureInfo.InvariantCulture, $"(x{held.Repeats})"),
            line,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// No reason code reaches the panel. The code is still what the canonical
    /// journal carries — that is an invariant of ADR 0010 — and what the player
    /// reads is a sentence built from it.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void No_line_of_a_story_shows_a_raw_reason_code(string fixtureName)
    {
        var state = EndOfParty(fixtureName, MatrixSeeds[1]);
        var codes = state.Events.Select(@event => @event.ReasonCode).Distinct().ToArray();
        Assert.NotEmpty(codes);

        foreach (var creature in state.Creatures)
        {
            var panel = HudText.CreatureStory(state, creature.Id);
            foreach (var code in codes)
            {
                Assert.DoesNotContain(code, panel, StringComparison.Ordinal);
            }
        }
    }

    private static string[] Body(string panel) => panel.Split('\n').Skip(1).ToArray();

    private static string Head(string panel) => panel.Split('\n')[0];

    private static PrototypeSnapshot EndOfParty(string fixtureName, ulong seed)
    {
        var world = new PrototypeWorld(Fixture(fixtureName) with { Seed = seed });
        while (!world.IsComplete)
        {
            world.Step();
        }

        return world.GetSnapshot();
    }

    private static PrototypeCommandLog Fixture(string fixtureName) =>
        PrototypeCommandDocument.Load(Path.Combine(
            PresentationFixtures.FindRepositoryRoot(),
            "scenarios",
            "prototype1",
            $"{fixtureName}.commands.v2.json"));

    private static HudViewState View(
        PrototypeSnapshot state,
        int? selectedCreatureId = null,
        int diagnosticCount = 0) =>
        new(
            state,
            "baseline",
            "0123abcdef",
            true,
            1.0,
            selectedCreatureId,
            null,
            string.Empty,
            [],
            diagnosticCount);
}
