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
    /// Criterion 9, on the owner's scene rather than on a raider written here, and
    /// on <b>every</b> raider standing on the map — not only the ones whose tile is
    /// their own.
    ///
    /// <para><b>The filter this used to carry is what let a defect through.</b> It
    /// skipped cells shared with a crew member, and on the tick-2380 frame as it
    /// stood then, four of the six captioned returners stood on (15,7) with
    /// «Тишина» — so the class of bodies that could not be selected at all was
    /// exactly the class the check refused to look at. Independent review of
    /// PR #368 measured it. The check is now written so that the same defect
    /// reddens it. Since Issue #361 made the damage jitter live the frame is a
    /// different one — the crowd of that room now stands on (14,7) — which is
    /// precisely why this check names no cell and no count of its own.</para>
    ///
    /// <para>Reachability is what is asserted rather than a single answer, because
    /// a cell holding five bodies cannot answer with all five at once: clicking it
    /// again has to reach the next one, and come back round after the last.</para>
    /// </summary>
    [Theory]
    [InlineData(WaveThreeTick)]
    [InlineData(WaveFourTick)]
    public void Every_raider_on_the_map_can_be_pointed_at_and_selected(int ticks)
    {
        var state = OwnerScene(ticks);
        var onMap = state.Raiders
            .Where(raider => raider.Mode != RaiderMode.Escaped)
            .ToArray();

        Assert.NotEmpty(onMap);
        foreach (var raider in onMap)
        {
            Assert.Contains(
                new WorldLabelSubject(WorldLabelKind.Raider, raider.Id),
                Reachable(state, raider.Position));
        }
    }

    /// <summary>
    /// The finding of the returning round, as its own check: on the frame the owner
    /// played, every returning raider carrying a caption can be selected. Before the
    /// fix of Issue #364 four of the six could not be — «Секира», «Сиплый»,
    /// «Ловчий» and «Косой» all stood on (15,7) behind a crew member, and the cell
    /// answered «Тишина» however many times it was clicked.
    ///
    /// <para>It is separate from the check above because it is the one that makes
    /// the promise «the second line is not lost, it is in the panel» true. The panel
    /// is reached by selecting, and the caption whose second line the layout sheds
    /// on this frame is among the ones it walks.</para>
    ///
    /// <para><b>Re-pinned by Issue #361:</b> the frame carries five captioned
    /// returners where it carried six, because a party fought with a live damage
    /// jitter puts a different set of survivors back through the gate. The count is
    /// written out rather than derived for the same reason the other counts of this
    /// scene are — so that a change which starts losing captions is noticed — and
    /// the walk under it is over every one of them, as before.</para>
    /// </summary>
    [Fact]
    public void Every_captioned_returner_of_the_owners_frame_can_be_reached_by_clicking()
    {
        var state = OwnerScene(WaveFourTick);
        var captioned = state.Raiders.Where(ReturningHeroLabel.IsCaptioned).ToArray();

        Assert.Equal(5, captioned.Length);
        foreach (var raider in captioned)
        {
            Assert.Contains(
                new WorldLabelSubject(WorldLabelKind.Raider, raider.Id),
                Reachable(state, raider.Position));
            // And the panel that selection opens carries the caption's own lines,
            // so reaching him is reaching the sentence the layout took off his head.
            var panel = InspectorText.Raider(state, raider);
            foreach (var line in WorldLabels.CaptionOf(raider))
            {
                Assert.Contains(line.Text, panel, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// Every body repeated clicking on one cell reaches, in order, stopping when the
    /// cycle closes. A cycle that skipped a body, repeated one or never came back
    /// round shows up here rather than on the owner's screen.
    /// </summary>
    private static IReadOnlyList<WorldLabelSubject> Reachable(
        PrototypeSnapshot state,
        GridPoint cell)
    {
        var bodies = WorldLabels.BodiesAt(state, cell);
        var reached = new List<WorldLabelSubject>();
        WorldLabelSubject? selected = null;
        for (var click = 0; click < bodies.Count; click++)
        {
            selected = WorldLabels.NextAt(state, cell, selected);
            Assert.NotNull(selected);
            Assert.DoesNotContain(selected!.Value, reached);
            reached.Add(selected.Value);
        }

        Assert.NotEmpty(reached);
        // The click after the last body comes back to the first.
        Assert.Equal(reached[0], WorldLabels.NextAt(state, cell, selected));
        return reached;
    }

    /// <summary>
    /// What the pointer reports on a crowded cell — the check Issue
    /// <see href="https://github.com/anshushunov/dungeon-fortress/issues/370">#370</see>
    /// was opened for and Issue #371 absorbed.
    ///
    /// <para><c>PointedAt</c> changed the meaning of hovering for <b>every</b> body
    /// on the map and was covered by nothing: independent review of PR #368
    /// measured that reverting it to the plain <c>At</c> it replaced reddened no
    /// test at all. The rule it states is «the selected body when it is standing on
    /// the pointed cell, the cell's first body otherwise», and its point is that a
    /// player who has clicked past a crew member to the raider behind him does not
    /// get the crew member back at the next twitch of the mouse.</para>
    ///
    /// <para>The crowded cell is the owner's own (14,7) at tick 2380 — six bodies
    /// — because a rule about crowded cells checked on a cell of two would be
    /// checked where it cannot fail.</para>
    ///
    /// <para><b>Re-pinned by Issue #361, and one assertion was added rather than
    /// moved.</b> The crowd used to stand on (15,7) and used to have the crew
    /// member «Тишина» in front of five raiders. With the damage jitter live the
    /// same journal puts six raiders on (14,7) instead, and no single cell of the
    /// frame is now both crowded and mixed. Re-pinning the cell alone would have
    /// dropped «the player has clicked past a <em>crew member</em>» — the half the
    /// rule was written for — without saying so, so the mixed case is asserted
    /// separately on the cell that still carries it, (15,7), where a crew member
    /// stands in front of one raider. The rule under test, the assertions about it
    /// and their form are unchanged; the frame simply no longer offers both halves
    /// in one place.</para>
    /// </summary>
    [Fact]
    public void The_pointer_answers_with_the_selected_body_when_it_stands_on_that_cell()
    {
        var state = OwnerScene(WaveFourTick);
        var cell = new GridPoint(14, 7);
        var bodies = WorldLabels.BodiesAt(state, cell);

        Assert.Equal(6, bodies.Count);

        // Nothing selected: the cell answers with its first body.
        Assert.Equal(bodies[0], WorldLabels.PointedAt(state, cell, null));

        // Every other body of that cell, once it is the selection, is what the
        // pointer answers with — not a sample of them, because the one this rule
        // exists for is whichever raider the player has clicked through to.
        foreach (var body in bodies)
        {
            Assert.Equal(body, WorldLabels.PointedAt(state, cell, body));
        }

        // And a selection standing somewhere else does not follow the cursor: the
        // pointed cell answers with its own first body again.
        var elsewhere = WorldLabels
            .BodiesAt(state, MixedCell)
            .First(body => !bodies.Contains(body));
        Assert.Equal(bodies[0], WorldLabels.PointedAt(state, cell, elsewhere));

        // The half the crowded cell of this frame no longer carries: a crew member
        // in front of a raider. Clicking through to the raider must not hand the
        // crew member back at the next twitch of the mouse.
        var mixed = WorldLabels.BodiesAt(state, MixedCell);
        Assert.Equal(2, mixed.Count);
        Assert.Equal(WorldLabelKind.Creature, mixed[0].Kind);
        Assert.Equal(WorldLabelKind.Raider, mixed[1].Kind);
        Assert.Equal(mixed[0], WorldLabels.PointedAt(state, MixedCell, null));
        foreach (var body in mixed)
        {
            Assert.Equal(body, WorldLabels.PointedAt(state, MixedCell, body));
        }
    }

    /// <summary>
    /// The cell of the owner's wave-4 frame where a crew member stands in front of
    /// a raider. See
    /// <see cref="The_pointer_answers_with_the_selected_body_when_it_stands_on_that_cell"/>
    /// for why it is named apart from the crowded one.
    /// </summary>
    private static readonly GridPoint MixedCell = new(15, 7);

    /// <summary>
    /// The feedback line after a click, and the three cases in which there is
    /// none — the second half of Issue #370.
    ///
    /// <para><b>Re-pinned by Issue #361:</b> the crowd of the owner's frame moved
    /// from (15,7) to (14,7) and is still six bodies deep, so the cell and the
    /// text it prints moved with it and the count did not. The cell the «selected
    /// body stands elsewhere» case reads from moved for the same reason: (16,7) is
    /// empty on this frame, and (15,7) is where a body that is not on the crowded
    /// cell now stands.</para>
    ///
    /// <para>It is not decoration. Cycling was chosen over a chooser popup partly
    /// <em>because</em> this line pays the cost of cycling: four raiders on one tile
    /// look exactly like one raider on one tile, so without it the second click
    /// reads as the map changing its mind. Review of PR #368 wrote that the
    /// compensation was «заявлена, верна, не доказана» — the same position the
    /// compensation «the shed line is in the panel» was in before it was measured.
    /// This is the measurement.</para>
    /// </summary>
    [Fact]
    public void The_feedback_line_says_which_body_of_how_many_the_click_reached()
    {
        var state = OwnerScene(WaveFourTick);
        var cell = new GridPoint(14, 7);
        var bodies = WorldLabels.BodiesAt(state, cell);

        Assert.Equal(6, bodies.Count);
        for (var index = 0; index < bodies.Count; index++)
        {
            Assert.Equal(
                $"(14,7): {index + 1} of 6 standing here; click the cell again for the next one.",
                WorldLabels.SelectionHint(state, cell, bodies[index]));
        }

        // No hint when the cell holds one body: the single click already answered
        // it whole, and a line saying «1 of 1» is noise.
        var alone = Enumerable
            .Range(0, PrototypeTuning.MapWidth)
            .SelectMany(x => Enumerable.Range(0, PrototypeTuning.MapHeight)
                .Select(y => new GridPoint(x, y)))
            .First(spot => WorldLabels.BodiesAt(state, spot).Count == 1);
        Assert.Null(
            WorldLabels.SelectionHint(state, alone, WorldLabels.BodiesAt(state, alone)[0]));

        // No hint when nothing is selected — there is no «which of them» to report.
        Assert.Null(WorldLabels.SelectionHint(state, cell, null));

        // And none when the selected body is not standing on the clicked cell,
        // because then the number it would print would be about somebody else.
        var elsewhere = WorldLabels
            .BodiesAt(state, MixedCell)
            .First(body => !bodies.Contains(body));
        Assert.Null(WorldLabels.SelectionHint(state, cell, elsewhere));
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
            // Compared against the lines the caption is actually composed of —
            // WorldLabels.CaptionOf — and not against ReturningHeroLabel.Lines one
            // step behind it. Independent review of PR #368 noted that a
            // substitution inside the composer would not have reddened the older
            // form; this closes that step.
            foreach (var line in WorldLabels.CaptionOf(raider))
            {
                Assert.Contains(line.Text, panel, StringComparison.Ordinal);
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
