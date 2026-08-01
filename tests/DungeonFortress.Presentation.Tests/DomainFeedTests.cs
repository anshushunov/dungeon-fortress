using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The panel a player reads <b>before clicking anything</b> (Issue #145).
///
/// <para>
/// Issue #140 fixed the story of a selected creature and left this one alone.
/// Independent review then measured what the owner's next playtest would open on:
/// on the shipped <c>baseline</c> party the domain feed's window of "the last
/// three entries" carried something that mattered in <b>one sample of 47</b>, and
/// at tick 2400 all three lines were the same creature waiting in the same
/// corridor. That is the screen the owner already described as "а лог очень
/// короткий".
/// </para>
///
/// <para>
/// Everything here is a function of one snapshot. The world is stepped to
/// <em>reach</em> a party worth reading — nobody has a story at tick 1 — and never
/// between asking the panel a question and reading its answer;
/// <see cref="Reading_the_domain_feed_needs_no_tick_to_run"/> carries that claim
/// on its own.
/// </para>
/// </summary>
public sealed class DomainFeedTests(ITestOutputHelper output)
{
    /// <summary>
    /// The party every number of Issue #145 is measured on: the shipped
    /// <c>baseline</c> command log, the same 2400 ticks Issue #140 measured, the
    /// same run the evidence files record.
    /// </summary>
    private const int MeasuredTicks = 2_400;

    /// <summary>
    /// How often the feed is read while the party runs. The window is sampled
    /// rather than read every tick because the claim is about what a player who
    /// looks at the screen sees, and 50 ticks is about how often a player looks.
    /// It is also the sampling independent review used, so the before and after
    /// numbers are the same measurement.
    /// </summary>
    private const int SampleEvery = 50;

    /// <summary>
    /// The seeds the presentation matrix is read on, the same three
    /// <c>CreatureStoryTests</c> uses: a claim about a panel is worth nothing if it
    /// is only tried on the party that motivated the change.
    /// </summary>
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    /// <summary>
    /// The share of sampled windows that must carry something that mattered.
    ///
    /// <para>
    /// It is a floor and not the figure: the figure is what the run prints. The
    /// floor is set by what the party makes possible rather than by what the code
    /// happens to do — see
    /// <see cref="The_feed_carries_something_that_mattered_whenever_the_party_has_one"/>,
    /// which is the criterion with no slack in it at all: <b>every</b> window in
    /// which the party has ever produced a turning point has to show one. This
    /// number is the same claim stated over the whole party, including the long
    /// opening in which nothing has happened yet and there is honestly nothing to
    /// show.
    /// </para>
    /// </summary>
    private const double WindowsThatMustMatter = 0.40;

    [Fact]
    public void The_domain_feed_shows_what_mattered_and_not_only_what_happened_last()
    {
        var report = new StringBuilder();
        var windows = 0;
        var carried = 0;
        var possible = 0;
        foreach (var (tick, state) in Party("baseline", MeasuredTicks))
        {
            windows++;
            var significant = state.Events
                .Where(@event => HudText.StoryWeight(@event.ReasonCode) > 0)
                .ToArray();
            var lines = FeedLines(HudText.Feedback(View(state)));
            var shown = lines.Count(line => significant.Any(@event => Renders(state, @event, line)));
            if (significant.Length > 0)
            {
                possible++;
            }

            if (shown > 0)
            {
                carried++;
            }

            report.AppendLine(CultureInfo.InvariantCulture,
                $"t{tick}: {state.Events.Count} entries, {significant.Length} mattered, " +
                $"{lines.Length} lines, {shown} of them mattered");
        }

        var share = (double)carried / windows;
        report.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"baseline/{MeasuredTicks} every {SampleEvery} ticks: {carried} of {windows} windows carry something that mattered ({100.0 * share:0.0}%); {possible} of {windows} windows were taken at a moment when the party had one to show."));
        output.WriteLine(report.ToString());

        Assert.True(
            windows >= 40,
            $"only {windows} windows were sampled, which is too few for a share to mean anything.");
        Assert.True(
            share >= WindowsThatMustMatter,
            $"{carried} of {windows} windows of the domain feed ({100.0 * share:0.0}%) carry anything " +
            $"that mattered to this party, under the {100.0 * WindowsThatMustMatter:0}% Issue #145 " +
            "names. A feed of the newest three entries measured 2% here, because 96.5% of what a " +
            "creature decides is waiting and stepping aside.");
    }

    /// <summary>
    /// The criterion with no slack: at every moment the party has produced a
    /// turning point, the feed carries one. The share above is this claim spread
    /// over the whole party, including the opening thousand ticks in which nothing
    /// has happened yet.
    /// </summary>
    [Fact]
    public void The_feed_carries_something_that_mattered_whenever_the_party_has_one()
    {
        var silent = new List<string>();
        var applies = 0;
        foreach (var (tick, state) in Party("baseline", MeasuredTicks))
        {
            var significant = state.Events
                .Where(@event => HudText.StoryWeight(@event.ReasonCode) > 0)
                .ToArray();
            if (significant.Length == 0)
            {
                continue;
            }

            applies++;
            var lines = FeedLines(HudText.Feedback(View(state)));
            if (!lines.Any(line => significant.Any(@event => Renders(state, @event, line))))
            {
                silent.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"t{tick} ({significant.Length} of {state.Events.Count} entries mattered)"));
            }
        }

        Assert.True(
            applies >= 20,
            $"only {applies} of the sampled windows were taken after anything had happened to this " +
            "party, so the criterion was checked against almost nothing.");
        Assert.True(
            silent.Count == 0,
            $"{silent.Count} of {applies} windows show nothing that mattered while the party had " +
            $"something to show: {string.Join(", ", silent.Take(8))}" +
            $"{(silent.Count > 8 ? ", …" : string.Empty)}. Without a click, this is the whole of what " +
            "the player is told about the domain.");
    }

    /// <summary>
    /// The feed is the canonical journal and nothing else, and the rule by which it
    /// picks three of four thousand entries is stated here rather than restated from
    /// the code: <b>one line per creature, that creature's most significant
    /// decision, and the creatures whose decisions mean most</b>.
    ///
    /// <para>
    /// Four claims, each of which a different mistake would break: every line is an
    /// entry of this party's journal, rendered from that entry alone; no two lines
    /// are about the same creature; nothing left off the feed outranks anything on
    /// it, in the order "what it means, then when it happened"; and the lines are in
    /// time order, newest first, so the panel reads bottom to top as the party
    /// happened.
    /// </para>
    ///
    /// <para>
    /// Asserted over three seeds of two shipped fixtures and at four moments of
    /// each party — before the first wave, during it, after it and at the end —
    /// because "the panel never disagrees with the journal" is worth nothing if it
    /// is only tried on the frame that motivated the change.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void The_feed_a_domain_shows_is_the_journal_ranked_by_what_it_meant(string fixtureName)
    {
        var momentsRead = 0;
        var momentsAfterSomethingMattered = 0;
        foreach (var seed in MatrixSeeds)
        {
            foreach (var tick in new[] { 600, 1_400, 2_000, MeasuredTicks })
            {
                var state = RunFixture(fixtureName, seed, tick);
                if (state.Events.Count == 0)
                {
                    continue;
                }

                momentsRead++;
                var where = $"{fixtureName}/{seed}/t{tick}";
                var perCreature = state.Events
                    .GroupBy(@event => @event.CreatureId)
                    .Select(crew => crew
                        .OrderBy(@event => HudText.StoryWeight(@event.ReasonCode))
                        .ThenBy(@event => @event.LastTick)
                        .Last())
                    .ToArray();
                if (perCreature.Any(@event => HudText.StoryWeight(@event.ReasonCode) > 0))
                {
                    momentsAfterSomethingMattered++;
                }

                var lines = FeedLines(HudText.Feedback(View(state)));
                Assert.Equal(Math.Min(HudText.DomainFeedLines, perCreature.Length), lines.Length);

                var shown = lines
                    .Select(line => Assert.Single(
                        perCreature.Where(@event => Renders(state, @event, line))))
                    .ToArray();

                // One line per creature: three lines about one creature stopped in
                // one corridor is the defect this issue is about.
                Assert.Equal(
                    shown.Length,
                    shown.Select(@event => @event.CreatureId).Distinct().Count());

                // Nothing off the feed outranks anything on it.
                var floor = shown.Min(Rank);
                foreach (var missing in perCreature.Except(shown))
                {
                    Assert.True(
                        Comparer<(int Weight, int Tick)>.Default.Compare(Rank(missing), floor) <= 0,
                        $"{where}: {HudText.CreatureName(state, missing.CreatureId)} was left off the " +
                        $"feed with '{missing.ReasonCode}' at t{missing.LastTick} while somebody whose " +
                        "decision means less to this party is on it. The feed spends its lines from " +
                        "the top of HudText.StoryWeight down.");
                }

                Assert.Equal(shown.OrderByDescending(@event => @event.LastTick).ToArray(), shown);
            }
        }

        Assert.True(
            momentsRead >= 3 * 4,
            $"{fixtureName}: only {momentsRead} moments of a party were read, which is too few for " +
            "the comparison above to have been made against anything.");
        Assert.True(
            momentsAfterSomethingMattered >= 3,
            $"{fixtureName}: at only {momentsAfterSomethingMattered} of the moments read had anything " +
            "that matters happened yet, so the ranking was never exercised against a turning point.");
    }

    /// <summary>
    /// The line a creature gets is the <b>worst thing that happened to it</b>, not
    /// the last thing it did.
    ///
    /// <para>
    /// This is the half of the rule that only the domain feed has. A creature's own
    /// story groups by kind of decision, and every entry in one such group weighs
    /// the same, so "the representative of a beat" there can be read as "the newest
    /// of that kind" without anybody noticing the difference. Group by creature and
    /// the difference is the whole panel: at the end of a baseline party the newest
    /// entry of nearly every creature is somebody in the way, and the entry that
    /// matters is hundreds of ticks old.
    /// </para>
    /// </summary>
    [Fact]
    public void The_line_a_creature_gets_is_the_worst_thing_that_happened_to_it()
    {
        var state = PresentationFixtures.RunFixture("baseline", MeasuredTicks);
        var shown = HudText.DomainSelection(state.Events);
        var overtakenTheNewest = 0;

        foreach (var @event in shown)
        {
            var mine = state.Events.Where(item => item.CreatureId == @event.CreatureId).ToArray();
            var best = mine.Max(Rank);
            Assert.Equal(best, Rank(@event));
            if (Rank(mine[^1]) != best)
            {
                overtakenTheNewest++;
            }
        }

        Assert.Equal(HudText.DomainFeedLines, shown.Count);
        Assert.True(
            overtakenTheNewest == shown.Count,
            $"only {overtakenTheNewest} of the {shown.Count} lines of this feed show something other " +
            "than that creature's newest entry, so the difference between 'the worst thing that " +
            "happened to it' and 'the last thing it did' was not on the panel to be read.");
    }

    /// <summary>
    /// A creature that kept deciding takes one line of the feed and not three.
    ///
    /// <para>
    /// This is the measured defect, stated as a rule. At tick 2400 the shipped feed
    /// read "Брусок stopped at (14,7)", "Брусок waits on cooking" and "Брусок
    /// stopped at (14,7)" — one creature, one corridor, the whole of what the player
    /// was told about a domain of nine. The check is aimed at a party where one
    /// creature really can fill the panel, and says how many entries it had, so a
    /// quiet party cannot make it pass by default.
    /// </para>
    /// </summary>
    [Fact]
    public void One_creature_takes_one_line_of_the_feed_however_much_it_decided()
    {
        var state = PresentationFixtures.RunFixture("baseline", MeasuredTicks);
        var shown = HudText.DomainSelection(state.Events);

        Assert.Equal(
            shown.Count,
            shown.Select(@event => @event.CreatureId).Distinct().Count());

        var busiest = state.Events
            .GroupBy(@event => @event.CreatureId)
            .Max(crew => crew.Count());
        Assert.True(
            busiest >= HudText.DomainFeedLines * 20,
            $"the busiest creature of this party left {busiest} journal entries, which is too few for " +
            "the feed to have been at risk of filling with one of them.");
    }

    /// <summary>
    /// The bound, and the fact that the panel says what it is hiding — both how many
    /// of the crew and how much of the journal.
    ///
    /// <para>
    /// A party leaves the domain with thousands of journal entries and the panel is
    /// worth three lines (see <see cref="HudText.DomainFeedLines"/> for why three
    /// and what the question's own answer was). Showing three of nine crew is all
    /// that fits; showing three of nine <em>without saying so</em> would make a
    /// player believe a domain of nine had three people in it and that nothing had
    /// happened to the rest.
    /// </para>
    /// </summary>
    [Fact]
    public void The_feed_is_bounded_and_the_header_says_what_is_off_the_panel()
    {
        var truncated = 0;
        foreach (var (tick, state) in Party("baseline", MeasuredTicks))
        {
            var head = HudText.Feedback(View(state)).Split('\n')[0];
            var lines = FeedLines(HudText.Feedback(View(state)));
            var crew = state.Events.Select(@event => @event.CreatureId).Distinct().Count();
            var mattered = state.Events.Count(@event => HudText.StoryWeight(@event.ReasonCode) > 0);

            Assert.True(
                lines.Length <= HudText.DomainFeedLines,
                $"t{tick}: the feed shows {lines.Length} lines, over the {HudText.DomainFeedLines} " +
                "the panel is worth. Text that does not fit its label is dropped or drawn over the " +
                "panel below it.");
            Assert.Contains(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{lines.Length} of {crew} crew · {mattered} of {state.Events.Count} mattered"),
                head,
                StringComparison.Ordinal);
            if (crew > lines.Length)
            {
                truncated++;
            }
        }

        Assert.True(
            truncated >= 20,
            $"only {truncated} of the sampled feeds had more crew behind them than lines on them, so " +
            "the header arm that says what is off the panel was barely read.");
    }

    /// <summary>
    /// Before anything has mattered, the feed is still the crew at work — and it is
    /// three different creatures rather than three lines about one.
    ///
    /// <para>
    /// Ranking decides the <em>order</em> of the three lines and not whether there
    /// are three. A baseline party spends its first thirteen hundred ticks with
    /// nothing dramatic in it, and a feed that went blank until the first wave would
    /// answer "what is happening in the domain" with silence for more than half the
    /// party — the opposite defect to the one being fixed and an easy one to ship by
    /// accident.
    /// </para>
    /// </summary>
    [Fact]
    public void Before_anything_has_mattered_the_feed_is_still_the_crew_at_work()
    {
        var state = PresentationFixtures.RunFixture("baseline", 600);
        Assert.DoesNotContain(
            state.Events,
            @event => HudText.StoryWeight(@event.ReasonCode) > 0);

        var lines = FeedLines(HudText.Feedback(View(state)));
        var shown = HudText.DomainSelection(state.Events);

        Assert.Equal(HudText.DomainFeedLines, lines.Length);
        Assert.Equal(
            HudText.DomainFeedLines,
            shown.Select(@event => @event.CreatureId).Distinct().Count());
        Assert.Contains("0 of ", HudText.Feedback(View(state)).Split('\n')[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Reading the domain feed needs no tick.
    ///
    /// <para>
    /// This is the "projection against world" line of the issue, and it is checked
    /// by construction rather than argued: the panel is built from a snapshot object
    /// taken once, the world is then stepped a further two hundred ticks, and the
    /// same object still answers the same question the same way. A show that needed
    /// the world to run would follow the world.
    /// </para>
    ///
    /// <para>
    /// Every fact on every line is checked to come out of that snapshot too — the
    /// tick, the creature's name and the sentence are all read off an entry the
    /// snapshot itself carries — so "no tick" is not merely "no tick was run", it is
    /// "nothing here could have needed one".
    /// </para>
    /// </summary>
    [Fact]
    public void Reading_the_domain_feed_needs_no_tick_to_run()
    {
        var world = new PrototypeWorld(Fixture("baseline"));
        world.RunTicks(1_400);
        var taken = world.GetSnapshot();
        var before = HudText.Feedback(View(taken));

        foreach (var line in FeedLines(before))
        {
            var entry = Assert.Single(taken.Events.Where(@event =>
                line == string.Create(
                    CultureInfo.InvariantCulture,
                    $"t{@event.LastTick} · {EventNarration.Describe(taken, @event)}")));
            Assert.Contains(
                HudText.CreatureName(taken, entry.CreatureId),
                line,
                StringComparison.Ordinal);
        }

        world.RunTicks(200);
        Assert.NotEqual(taken.Tick, world.GetSnapshot().Tick);
        Assert.Equal(before, HudText.Feedback(View(taken)));
        Assert.NotEqual(before, HudText.Feedback(View(world.GetSnapshot())));
    }

    /// <summary>
    /// What the feed orders by, stated in the test rather than borrowed from the
    /// code: what a decision means to the creature that took it first, when it
    /// happened second.
    /// </summary>
    private static (int Weight, int Tick) Rank(PrototypeEvent @event) =>
        (HudText.StoryWeight(@event.ReasonCode), @event.LastTick);

    private static PrototypeSnapshot RunFixture(string fixtureName, ulong seed, int ticks)
    {
        var world = new PrototypeWorld(Fixture(fixtureName) with { Seed = seed });
        world.RunTicks(ticks);
        return world.GetSnapshot();
    }

    /// <summary>The domain feed, one snapshot at a time, every <see cref="SampleEvery"/> ticks.</summary>
    private static IEnumerable<(int Tick, PrototypeSnapshot State)> Party(string fixtureName, int ticks)
    {
        var world = new PrototypeWorld(Fixture(fixtureName));
        for (var tick = 1; tick <= ticks && !world.IsComplete; tick++)
        {
            world.Step();
            if (tick % SampleEvery == 0)
            {
                yield return (tick, world.GetSnapshot());
            }
        }
    }

    /// <summary>
    /// The event lines of the panel: everything between the header and the
    /// diagnostics counter that belongs to the session rather than to the feed.
    /// </summary>
    private static string[] FeedLines(string panel) => panel
        .Split('\n')
        .Skip(1)
        .Where(line => line.Length > 0 && !line.StartsWith("Diagnostics:", StringComparison.Ordinal))
        .ToArray();

    /// <summary>
    /// Whether this line of the feed is this entry of the journal. The sentence is
    /// what identifies it: one reason code has one sentence template, and
    /// <see cref="HudText.StoryWeight"/> is a function of the code, so a line
    /// carrying a turning point's sentence is a turning point.
    /// </summary>
    private static bool Renders(PrototypeSnapshot state, PrototypeEvent @event, string line) =>
        line.Contains(
            EventNarration.Describe(state, @event),
            StringComparison.Ordinal);

    private static PrototypeCommandLog Fixture(string fixtureName) =>
        PrototypeCommandDocument.Load(Path.Combine(
            PresentationFixtures.FindRepositoryRoot(),
            "scenarios",
            "prototype1",
            $"{fixtureName}.commands.v2.json"));

    private static HudViewState View(PrototypeSnapshot state, int diagnosticCount = 0) =>
        new(
            state,
            "baseline",
            "0123abcdef",
            true,
            1.0,
            null,
            null,
            string.Empty,
            [],
            diagnosticCount);
}
