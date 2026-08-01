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
