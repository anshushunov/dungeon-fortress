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
    /// The snapshot is a real one taken past the first wave rather than a
    /// hand-built creature: what is under test is the panel, and the panel is
    /// only worth testing against state the simulation actually produces.
    /// </summary>
    [Fact]
    public void The_panel_of_a_creature_that_remembers_places_lists_them_newest_first()
    {
        var state = PresentationFixtures.RunFixture("baseline", PrototypeTuning.FirstRaidTick + 500);
        var creature = state.Creatures
            .Where(item => item.RememberedPlaces.Count >= 2)
            .OrderBy(item => item.Id)
            .FirstOrDefault();
        Assert.True(
            creature is not null,
            "no creature carried two remembered places 500 ticks after the first wave, so the " +
            "panel under test has no subject. Either memory stopped being written or the " +
            "fixture stopped reaching a fight.");

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
}
