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
/// Everything here is a function of a snapshot. That is the claim being made as
/// much as it is the way the tests are written: selecting a creature answers the
/// question out of facts the world has already published, so it needs no tick to
/// run. Nothing in this file steps a world in order to read a panel.
/// </para>
/// </summary>
public sealed class CreatureStoryTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];
    private static readonly string[] Fixtures = ["baseline", "prepared"];

    /// <summary>
    /// The panel is the canonical journal and nothing else: its lines are that
    /// creature's own entries, the last <see cref="HudText.CreatureStoryLines"/>
    /// of them, newest first, each rendered from its own entry.
    ///
    /// <para>
    /// Asserted over the whole matrix and over every creature in it, because the
    /// claim "the show never disagrees with the journal" is worth nothing if it is
    /// only tried on one party. Criterion 2 of the issue asks for exactly this and
    /// asks for it executably.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void The_story_a_creature_shows_is_its_own_tail_of_the_canonical_journal(string fixtureName)
    {
        var creaturesSeen = 0;
        foreach (var seed in MatrixSeeds)
        {
            var state = EndOfParty(fixtureName, seed);
            foreach (var creature in state.Creatures)
            {
                creaturesSeen++;
                var mine = state.Events.Where(@event => @event.CreatureId == creature.Id).ToArray();
                var lines = Body(HudText.CreatureStory(state, creature.Id));
                var expected = mine.TakeLast(HudText.CreatureStoryLines).Reverse().ToArray();

                Assert.Equal(expected.Length, lines.Length);
                for (var index = 0; index < expected.Length; index++)
                {
                    var @event = expected[index];
                    Assert.Contains(
                        EventNarration.Sentence(
                            @event.ReasonCode,
                            @event.Details,
                            @event.JobKind,
                            @event.Target),
                        lines[index],
                        StringComparison.Ordinal);
                    Assert.StartsWith(
                        string.Create(CultureInfo.InvariantCulture, $"t{@event.FirstTick}"),
                        lines[index],
                        StringComparison.Ordinal);
                }
            }
        }

        Assert.True(
            creaturesSeen >= 3 * 3,
            $"{fixtureName}: only {creaturesSeen} creature-parties were read, which is too few for " +
            "the comparison above to have been made against anything.");
    }

    /// <summary>
    /// The bound, and the fact that the panel says what it is hiding.
    ///
    /// <para>
    /// A party leaves a creature with hundreds of journal entries and the panel is
    /// worth about nine lines at the frame the HUD is authored for. Showing six of
    /// four hundred is the only thing that fits; showing six of four hundred
    /// <em>without saying so</em> would make a player believe a creature decided
    /// six things all party. So the header carries both numbers.
    /// </para>
    /// </summary>
    [Fact]
    public void The_story_is_bounded_and_the_bound_is_on_the_panel()
    {
        var report = new StringBuilder();
        var longest = 0;
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                var state = EndOfParty(fixtureName, seed);
                foreach (var creature in state.Creatures)
                {
                    var total = state.Events.Count(@event => @event.CreatureId == creature.Id);
                    longest = Math.Max(longest, total);
                    var panel = HudText.CreatureStory(state, creature.Id);
                    var lines = Body(panel);

                    Assert.True(
                        lines.Length <= HudText.CreatureStoryLines,
                        $"{fixtureName}/{seed}: {creature.Name} shows {lines.Length} lines of story, " +
                        $"over the {HudText.CreatureStoryLines} the panel is worth. Text that does " +
                        "not fit its label is dropped or drawn over the panel below it.");
                    Assert.Contains(creature.Name, Head(panel), StringComparison.Ordinal);
                    Assert.Contains(
                        total > HudText.CreatureStoryLines
                            ? string.Create(
                                CultureInfo.InvariantCulture,
                                $"last {HudText.CreatureStoryLines} of {total}")
                            : string.Create(CultureInfo.InvariantCulture, $"{total} in all"),
                        Head(panel),
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
    /// With nothing selected the panel is what it always was. The domain feed is
    /// not replaced, it is scoped: click a creature and the feed is about that
    /// creature, click away and it is about the domain again.
    /// </summary>
    [Fact]
    public void Nothing_selected_leaves_the_domain_feed_exactly_as_it_was()
    {
        var state = PresentationFixtures.Baseline(400);
        var feedback = HudText.Feedback(View(state, diagnosticCount: 2));
        var lines = feedback.Split('\n');

        Assert.Equal("EVENT FEEDBACK", lines[0]);
        Assert.Equal(6, lines.Length);
        Assert.EndsWith(
            "Diagnostics: 2 (structured JSON is emitted by smoke/capture).",
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
                Event = state.Events
                    .Where(@event => @event.CreatureId == creature.Id)
                    .TakeLast(HudText.CreatureStoryLines)
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
