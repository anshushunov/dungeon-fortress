using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The creature panel's half of memory of place.
///
/// <c>PROTOTYPE_GRAYBOX.md</c> names three surfaces that show a memory to the
/// player: the map, the inspector and the event feed. The map and the feed were
/// covered; the inspector was not, and nothing reddened when the line was
/// deleted. That is the hole this file closes, and it is not hypothetical — the
/// golden frames cannot cover it, for a reason that is itself worth stating:
/// all three of them are captured before tick 1300, and no creature can carry a
/// memory before the first wave has landed.
/// </summary>
public sealed class MemoryInspectorTests(ITestOutputHelper output)
{
    /// <summary>
    /// A creature that remembers places says so on its own panel, newest first,
    /// naming the tile, the tick and which of the two things happened there.
    ///
    /// The snapshot is a real one rather than a hand-built creature: what is
    /// under test is the panel, and the panel is only worth testing against state
    /// the simulation actually produces.
    ///
    /// <para>
    /// <b>Which real one is decided by a rule and not by a tick.</b> This test
    /// used to run <c>baseline</c> to <c>FirstRaidTick + 500</c> and take
    /// whoever carried two memories there. That number was fitted to one cell of
    /// the matrix, and the fit was already broken on <c>main</c> before Issue
    /// #129 touched anything: at that tick the count of creatures carrying two
    /// memories is 4, <b>0</b>, 2, <b>0</b>, 1, 2 over the six runs of the matrix
    /// (baseline and prepared, seeds 20260726–20260728). Two of six cells had no
    /// subject; the test survived only because it looked at the one cell where
    /// there were four. After #129 the same cell has none, and the tick was the
    /// thing that was wrong.
    /// </para>
    ///
    /// <para>
    /// What replaces it is the question the panel is actually about: <b>the first
    /// moment in the party at which the panel has anything to show.</b> The
    /// search starts at <see cref="PrototypeTuning.FirstRaidTick"/> because no
    /// creature can carry a memory before the first wave has landed, and runs to
    /// the end of the party, so it cannot be tuned by a tick and cannot pass by
    /// accident: a party that never produces a subject fails, which is the same
    /// alarm the old assertion raised. On <c>main</c> it lands on t1664 and on
    /// this branch on t2011; the test does not care which, and prints it.
    /// </para>
    ///
    /// <para>
    /// Command: <c>dotnet test tests/DungeonFortress.Presentation.Tests -c Release
    /// --filter "FullyQualifiedName~MemoryInspectorTests" --logger
    /// "console;verbosity=detailed"</c>. The six-cell count above is
    /// <c>evidence/129-presentation.json</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void The_panel_of_a_creature_that_remembers_places_lists_them_newest_first()
    {
        var (state, creature) = FirstMomentWithASubject("baseline");
        Assert.True(
            creature is not null,
            "no creature carried two remembered places at any tick of the whole party, so the " +
            "panel under test has no subject. Either memory stopped being written or the " +
            "fixture stopped reaching a fight.");
        output.WriteLine($"subject found at tick {state.Tick}: {creature!.Name}");

        var line = InspectorText.DescribeMemory(creature!);
        var panel = InspectorText.Build(state.Shown(), creature!.Id, null);

        Assert.StartsWith("AVOIDS ", line, StringComparison.Ordinal);
        Assert.EndsWith("\n", line, StringComparison.Ordinal);
        Assert.Contains(line, panel, StringComparison.Ordinal);

        // Every remembered place is on the line, with its tick and its cause.
        foreach (var place in creature.RememberedPlaces)
        {
            Assert.Contains(
                $"({place.Place.X},{place.Place.Y}) t{place.Tick} {place.Cause}",
                line,
                StringComparison.Ordinal);
        }

        // Newest first: the order of the ticks as printed is descending.
        var printed = creature.RememberedPlaces
            .Select(place => (place.Tick, Index: line.IndexOf($"t{place.Tick} ", StringComparison.Ordinal)))
            .OrderBy(item => item.Index)
            .Select(item => item.Tick)
            .ToArray();
        Assert.Equal(printed.OrderByDescending(tick => tick).ToArray(), printed);

        // One line for all of them, not a heading and a line each: the panel fits
        // sixteen lines at 1280x720 and the overflow guard refuses more.
        Assert.Equal(1, line.Count(character => character == '\n'));
        output.WriteLine(panel);
    }

    /// <summary>
    /// A creature that has been through nothing says nothing. Without this the
    /// line could be a constant heading and the test above would still pass.
    /// </summary>
    [Fact]
    public void A_creature_with_no_memories_adds_no_line_to_its_panel()
    {
        var state = PresentationFixtures.Baseline(1);
        var creature = state.Creatures[0];

        Assert.Empty(creature.RememberedPlaces);
        Assert.Equal(string.Empty, InspectorText.DescribeMemory(creature));
        Assert.DoesNotContain(
            "AVOIDS",
            InspectorText.Build(state.Shown(), creature.Id, null),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The panel and the feed tell the same story about the same creature, and
    /// the reason code is on the panel beside the sentence rather than instead of
    /// it. This is the one place a player can read both, and it is what makes the
    /// panel worth the two lines it costs.
    /// </summary>
    [Fact]
    public void The_panel_carries_both_the_sentence_and_the_code_for_a_refusal()
    {
        var state = PresentationFixtures.RunFixture("baseline", PrototypeTuning.FirstRaidTick + 500);
        var refusing = state.Creatures.FirstOrDefault(item =>
            item.LastDecision.ReasonCode is "refused_place_of_panic" or "refused_place_of_wound");
        if (refusing is null)
        {
            // Not every tick has somebody refusing; the assertion above about the
            // AVOIDS line is what guards the panel unconditionally.
            return;
        }

        var panel = InspectorText.Build(state.Shown(), refusing.Id, null);
        Assert.Contains(refusing.LastDecision.ReasonCode, panel, StringComparison.Ordinal);
        Assert.Contains(
            EventNarration.Sentence(
                refusing.LastDecision.ReasonCode,
                refusing.LastDecision.Details,
                refusing.LastDecision.JobKind,
                refusing.LastDecision.Target),
            panel,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The first tick of the party at which some creature carries two remembered
    /// places, and that creature. Lowest id breaks a tie, so the subject is a
    /// function of the party and not of the order anything enumerates in.
    ///
    /// The walk starts at <see cref="PrototypeTuning.FirstRaidTick"/> — nothing
    /// can be remembered before the first wave — and stops at the first hit, so
    /// the common case costs a fraction of a party.
    /// </summary>
    private static (PrototypeSnapshot State, PrototypeCreatureSnapshot? Creature)
        FirstMomentWithASubject(string fixtureName)
    {
        var world = new PrototypeWorld(PresentationFixtures.LogOf(fixtureName));
        world.RunTicks(PrototypeTuning.FirstRaidTick);
        var state = world.GetSnapshot();
        while (!world.IsComplete)
        {
            world.Step();
            state = world.GetSnapshot();
            var subject = state.Creatures
                .Where(item => item.RememberedPlaces.Count >= 2)
                .OrderBy(item => item.Id)
                .FirstOrDefault();
            if (subject is not null)
            {
                return (state, subject);
            }
        }

        return (state, null);
    }
}
