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
    private const int WaveThreeTick = 2025;

    private const int WaveFourTick = 2380;

    /// <inheritdoc cref="WorldLabelLayoutTests.OwnerScene"/>
    private static PrototypeSnapshot OwnerScene(int ticks) =>
        WorldLabelLayoutTests.OwnerScene(ticks);

    /// <summary>
    /// Criterion 9, on the owner's scene rather than on a raider written here. It
    /// walks every cell a raider stands on at that moment, because "raiders can be
    /// picked" is a claim about the population and not about one lucky body.
    ///
    /// <para>The claim is about the cell and not about a named raider, and it has
    /// to be: several raiders share a cell on this frame — four of them stand on
    /// (15,7) — so the answer for such a cell is <em>one</em> of them rather than a
    /// particular one. What has to be true is that the answer is a raider who is
    /// standing there.</para>
    /// </summary>
    [Fact]
    public void Every_raider_on_the_map_can_be_pointed_at_and_selected()
    {
        var state = OwnerScene(WaveThreeTick);
        var cells = state.Raiders
            .Where(raider => raider.Mode != RaiderMode.Escaped)
            .Where(raider => !state.Creatures.Any(crew => crew.Position == raider.Position))
            .Select(raider => raider.Position)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(cells);
        foreach (var cell in cells)
        {
            var picked = WorldLabels.At(state, cell);

            Assert.NotNull(picked);
            Assert.Equal(WorldLabelKind.Raider, picked!.Value.Kind);
            Assert.Contains(
                state.Raiders,
                raider => raider.Id == picked.Value.Id && raider.Position == cell);
        }
    }

    /// <summary>
    /// Criterion 9's other half: the choice is <em>visible on the map</em> and not
    /// merely recorded. Clicking a raider fills the panel, and until this the map
    /// still said nothing about which body the panel was about.
    ///
    /// <para>One rule for both populations, so a raider is ringed under exactly the
    /// condition a creature is and never under a different one.</para>
    /// </summary>
    [Fact]
    public void The_selected_body_is_ringed_and_it_is_the_same_rule_for_both_kinds()
    {
        var creature = new WorldLabelSubject(WorldLabelKind.Creature, 3);
        var raider = new WorldLabelSubject(WorldLabelKind.Raider, 3);

        Assert.True(WorldSelectionMark.IsRinged(raider, new WorldLabelFocus(null, raider)));
        Assert.True(WorldSelectionMark.IsRinged(creature, new WorldLabelFocus(null, creature)));
        // The two populations number themselves independently, so choosing raider 3
        // must not ring creature 3.
        Assert.False(WorldSelectionMark.IsRinged(creature, new WorldLabelFocus(null, raider)));
        Assert.False(WorldSelectionMark.IsRinged(raider, new WorldLabelFocus(null, creature)));
        // Pointing at a body is not choosing it: a ring that followed the cursor
        // would blink round every body it crossed.
        Assert.False(WorldSelectionMark.IsRinged(raider, new WorldLabelFocus(raider, null)));
        Assert.False(WorldSelectionMark.IsRinged(raider, WorldLabelFocus.None));
    }

    /// <summary>
    /// And that the adapter really draws it for both — the one question a pure test
    /// cannot ask of a value, asked of the adapter's source the way
    /// <c>WorldDrawPassGuardTests</c> asks its own. No test project references
    /// <c>DungeonFortress.Game</c> and none should (ADR 0011), so structure is read
    /// as text.
    ///
    /// <para>This is what covers the ring instead of a fourth mutant. Deleting the
    /// ring from <c>DrawRaiderInformation</c> — precisely the state this Issue
    /// found the adapter in — reddens it, and so does replacing the shared rule
    /// with a hand-written condition in either routine.</para>
    /// </summary>
    [Theory]
    [InlineData("DrawCreatureInformation")]
    [InlineData("DrawRaiderInformation")]
    public void The_adapter_rings_the_selected_body_of_either_kind(string routine)
    {
        var body = AdapterSource.Body(routine);

        Assert.Contains(nameof(WorldSelectionMark.IsRinged), body, StringComparison.Ordinal);
        Assert.Contains(nameof(WorldSelectionMark.RadiusRef), body, StringComparison.Ordinal);
        Assert.Contains(nameof(WorldSelectionMark.StrokeRef), body, StringComparison.Ordinal);
        Assert.Contains(nameof(WorldSelectionMark.Segments), body, StringComparison.Ordinal);
        Assert.Single(AdapterSource.CallsTo(body, "DrawArc"));
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
        var state = OwnerScene(WaveFourTick);
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
        var state = OwnerScene(WaveFourTick);

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
