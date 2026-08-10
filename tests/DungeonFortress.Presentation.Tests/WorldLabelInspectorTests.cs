using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Asking a body who it is (Issue #364, addendum of 2026-08-10).
///
/// <para>The owner's second finding was «Враги, кстати вообще не выбираются и при
/// наведении ничего нет». Both halves of the answer are checked here: that a
/// raider is in the set the pointer picks from at all, and that the panel he lands
/// in says the same thing his caption does.</para>
/// </summary>
public sealed class WorldLabelInspectorTests
{
    private const ulong OwnerSeed = 20260729UL;

    private const int WaveThreeTick = 2025;

    private static PrototypeSnapshot OwnerScene(int ticks) =>
        PrototypeScenario.Run(
            PresentationFixtures.LogOf("baseline") with { Seed = OwnerSeed },
            ticks).State;

    /// <summary>
    /// Criterion 9, on the owner's scene rather than on a raider written here. It
    /// walks every raider standing on the map at that moment, because "raiders can
    /// be picked" is a claim about the population and not about a lucky one.
    /// </summary>
    [Fact]
    public void Every_raider_on_the_map_can_be_pointed_at_and_selected()
    {
        var state = OwnerScene(WaveThreeTick);
        var onMap = state.Raiders
            .Where(raider => raider.Mode != RaiderMode.Escaped)
            .Where(raider => !state.Creatures.Any(crew => crew.Position == raider.Position))
            .ToArray();

        Assert.NotEmpty(onMap);
        foreach (var raider in onMap)
        {
            Assert.Equal(
                new WorldLabelSubject(WorldLabelKind.Raider, raider.Id),
                WorldLabels.At(state, raider.Position));
        }
    }

    /// <summary>
    /// The other side of the same function, so that "everything is a raider" would
    /// not pass the check above: a crew member is still picked where it stands, and
    /// an empty cell is still nobody.
    /// </summary>
    [Fact]
    public void A_crew_member_and_an_empty_cell_still_answer_as_they_did()
    {
        var state = OwnerScene(WaveThreeTick);
        var creature = state.Creatures[0];
        var empty = Enumerable
            .Range(0, PrototypeTuning.MapWidth)
            .SelectMany(x => Enumerable.Range(0, PrototypeTuning.MapHeight)
                .Select(y => new GridPoint(x, y)))
            .First(cell =>
                !state.Creatures.Any(item => item.Position == cell) &&
                !state.Raiders.Any(item => item.Position == cell));

        Assert.Equal(
            new WorldLabelSubject(WorldLabelKind.Creature, creature.Id),
            WorldLabels.At(state, creature.Position));
        Assert.Null(WorldLabels.At(state, empty));
    }

    /// <summary>
    /// A raider that walked out through the gate is off the map and is not picked,
    /// the same rule the drawing follows. A downed one is picked, because his body
    /// is still lying there.
    /// </summary>
    [Fact]
    public void A_raider_that_has_left_is_not_on_the_map_and_a_downed_one_is()
    {
        var here = new GridPoint(20, 7);
        var state = PresentationFixtures.Baseline(PrototypeTuning.FirstRaidTick + 5) with
        {
            Creatures = [],
            Raiders =
            [
                new PrototypeRaiderSnapshot(
                    1, 3, 30, 4, here, 0, 0, false, RaiderMode.Escaped, "Ушедший"),
            ],
        };

        Assert.Null(WorldLabels.At(state, here));
        Assert.Equal(
            new WorldLabelSubject(WorldLabelKind.Raider, 1),
            WorldLabels.At(
                state with
                {
                    Raiders = [state.Raiders[0] with { Mode = RaiderMode.Downed }],
                },
                here));
    }

    /// <summary>
    /// Criterion 10, and it compares the two places rather than checking each one
    /// alone. The panel of a returning raider has to carry what his caption
    /// carries: if the caption says «волна 2 · достали (23,7)», the panel says it
    /// too, and neither can be reworded without the other following.
    /// </summary>
    [Fact]
    public void The_panel_of_a_returning_raider_says_what_his_caption_says()
    {
        var state = OwnerScene(2380);
        var returning = state.Raiders
            .Where(ReturningHeroLabel.IsCaptioned)
            .Where(raider => ReturningHeroLabel.Story(raider) is not null)
            .ToArray();

        Assert.NotEmpty(returning);
        foreach (var raider in returning)
        {
            var panel = InspectorText.Raider(state, raider);
            foreach (var line in ReturningHeroLabel.Lines(raider))
            {
                Assert.Contains(line, panel, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// And the panel is not empty for the other two kinds of raider either — the
    /// returner nobody reached, and the stranger — because "при наведении ничего
    /// нет" was the complaint about all of them.
    /// </summary>
    [Fact]
    public void The_panel_of_any_raider_names_him_and_says_what_he_is_doing()
    {
        var state = OwnerScene(2380);

        Assert.NotEmpty(state.Raiders);
        foreach (var raider in state.Raiders)
        {
            var panel = InspectorText.Raider(state, raider);

            Assert.Contains(raider.Name, panel, StringComparison.Ordinal);
            Assert.Contains($"RAIDER #{raider.Id}", panel, StringComparison.Ordinal);
            Assert.Contains(raider.Mode.ToString(), panel, StringComparison.Ordinal);
            Assert.Contains(
                $"({raider.Position.X}, {raider.Position.Y})",
                panel,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A raider the domain has never met carries no caption, and the panel says so
    /// in as many words instead of leaving a blank where the past would be.
    /// </summary>
    [Fact]
    public void A_stranger_has_no_past_encounter_and_the_panel_says_that()
    {
        var state = PresentationFixtures.Baseline(PrototypeTuning.FirstRaidTick + 5) with
        {
            Raiders =
            [
                new PrototypeRaiderSnapshot(
                    1, 1, 30, 4, new GridPoint(20, 7), 0, 0, false, RaiderMode.Raiding, "Крюк"),
            ],
        };

        var panel = InspectorText.Raider(state, state.Raiders[0]);

        Assert.Empty(ReturningHeroLabel.Lines(state.Raiders[0]).Skip(1));
        Assert.Contains("first visit", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("LAST TIME", panel, StringComparison.Ordinal);
    }
}
