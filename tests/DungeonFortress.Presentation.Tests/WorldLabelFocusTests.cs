using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The guard of Issue #371: what the map says while the player is pointing at a
/// body, on the owner's own two frames.
///
/// <para>It is a separate file from <see cref="WorldLabelLayoutTests"/> because it
/// measures a different decision. That one holds «where a label goes» — overlap and
/// attachment, the rule of Issue #364. This one holds «who is named and with how
/// many lines while the pointer is on him», which the owner reversed on the
/// playtest of 2026-08-10: «для врагов — в обычных волнах имен нет при наведении,
/// в последней волне только 1 детализированная надпись».</para>
/// </summary>
public sealed class WorldLabelFocusTests
{
    /// <inheritdoc cref="WorldLabelLayoutTests.OwnerScene"/>
    private const int WaveThreeTick = 2025;

    /// <inheritdoc cref="WaveThreeTick"/>
    private const int WaveFourTick = 2380;

    /// <summary>
    /// Criterion 1, and the whole of the first half of the owner's complaint:
    /// <b>every</b> raider standing on the map is named while the pointer is on
    /// him, and named again while he is the selected body.
    ///
    /// <para><b>No filter, and that is the load-bearing word.</b> The check walks
    /// every raider the snapshot has on the map and skips none — not the ones
    /// nobody has met, not the ones lying downed, not the ones sharing a cell with
    /// four neighbours. Criterion 9 of Issue #364 was written with a filter that
    /// removed cells shared with a crew member, which was exactly the class the
    /// defect lived in, and it stayed green through it; the shape of that mistake
    /// is «the check looks for the property where it could not fail to hold», and
    /// this is the same property one round later.</para>
    ///
    /// <para>What it would have said before Issue #371: on tick 2025 one raider of
    /// ten answered under the pointer, on tick 2380 six of eleven. The other
    /// fifteen — strangers and downed bodies — had no world label under any
    /// condition.</para>
    /// </summary>
    [Theory]
    [InlineData(WaveThreeTick)]
    [InlineData(WaveFourTick)]
    public void Every_raider_on_the_map_is_named_under_the_pointer_and_when_selected(int ticks)
    {
        var state = WorldLabelLayoutTests.OwnerScene(ticks);
        var onMap = state.Raiders.Where(raider => raider.Mode != RaiderMode.Escaped).ToArray();

        Assert.NotEmpty(onMap);
        // And the frame really does contain the kind of raider the old rule was
        // silent about, so the loop below is not quietly a loop over returners.
        Assert.Contains(onMap, raider => !ReturningHeroLabel.IsCaptioned(raider));
        foreach (var raider in onMap)
        {
            var subject = new WorldLabelSubject(WorldLabelKind.Raider, raider.Id);
            foreach (var focus in new[]
                     {
                         new WorldLabelFocus(subject, null),
                         new WorldLabelFocus(null, subject),
                     })
            {
                var label = Assert.Single(
                    WorldLabels
                        .Of(state, focus, CameraView.DefaultTileSize)
                        .Where(placed => placed.Request.Subject == subject));

                Assert.Equal(raider.Name, label.Lines[0].Text);
            }
        }
    }

    /// <summary>
    /// Criterion 3. Pointing at a body must not buy its second line with somebody
    /// else's place: on both of the owner's frames, and with <b>every</b> raider of
    /// the frame pointed at in turn, no two labels share a pixel and none ends up
    /// further than a tile from its own head.
    ///
    /// <para>These are the two properties Issue #364 exists for, re-measured under
    /// the focus this Issue introduces rather than assumed to survive it. The limit
    /// is written out as twenty-two for the reason
    /// <see cref="WorldLabelLayoutTests.No_label_ends_up_further_than_a_tile_from_its_own_body"/>
    /// gives: a check measured against whatever the layout currently declares its
    /// own limit to be would pass for any limit at all.</para>
    /// </summary>
    [Theory]
    [InlineData(WaveThreeTick)]
    [InlineData(WaveFourTick)]
    public void Pointing_at_any_body_of_the_frame_breaks_neither_spacing_nor_attachment(
        int ticks)
    {
        const double oneTile = 22.0;
        var state = WorldLabelLayoutTests.OwnerScene(ticks);
        var bodies = state.Raiders
            .Where(raider => raider.Mode != RaiderMode.Escaped)
            .Select(raider => new WorldLabelSubject(WorldLabelKind.Raider, raider.Id))
            .Concat(state.Creatures
                .Select(creature => new WorldLabelSubject(WorldLabelKind.Creature, creature.Id)))
            .ToArray();

        Assert.NotEmpty(bodies);
        foreach (var body in bodies)
        {
            var placed = WorldLabels.Of(
                state,
                new WorldLabelFocus(body, OtherThan(bodies, body)),
                CameraView.DefaultTileSize);

            Assert.NotEmpty(placed);
            foreach (var one in placed)
            {
                Assert.True(
                    one.AttachmentRef <= oneTile,
                    $"«{one.Lines[0].Text}» sits {one.AttachmentRef:F2} reference pixels " +
                    $"from its body at tick {ticks} while «{body}» is pointed at; " +
                    $"the limit is {oneTile}.");
                foreach (var other in placed.Where(item => item != one))
                {
                    Assert.False(
                        Intersect(one.Box, other.Box),
                        $"«{one.Lines[0].Text}» and «{other.Lines[0].Text}» share pixels " +
                        $"at tick {ticks} while «{body}» is pointed at: " +
                        $"{one.Box} against {other.Box}.");
                }
            }
        }
    }

    /// <summary>
    /// Criterion 4, in two numbers per frame: with nothing pointed at, the map
    /// carries exactly what it carried before Issue #371. The owner chose naming
    /// under the cursor, and a rule that leaked into the quiet map would be his
    /// decision taken wider than he took it.
    ///
    /// <para>The numbers are measured on the scene and written out rather than
    /// derived, for the reason
    /// <see cref="WorldLabelLayoutTests.Every_returning_raider_of_the_owners_scene_is_still_named"/>
    /// gives: a change that starts naming strangers on the quiet map is noticed the
    /// day it happens.</para>
    /// </summary>
    [Theory]
    [InlineData(WaveThreeTick, 10, 1)]
    [InlineData(WaveFourTick, 11, 5)]
    public void With_nothing_pointed_at_the_map_names_exactly_who_it_named_before(
        int ticks,
        int onMap,
        int labels)
    {
        var state = WorldLabelLayoutTests.OwnerScene(ticks);
        var placed = WorldLabels.Of(state, WorldLabelFocus.None, CameraView.DefaultTileSize);

        Assert.Equal(
            onMap,
            state.Raiders.Count(raider => raider.Mode != RaiderMode.Escaped));
        Assert.Equal(labels, placed.Count);
        // Every one of them is a raider the domain has met: no crew member is
        // named with nothing pointed at, and no stranger is either.
        Assert.All(placed, label => Assert.Equal(WorldLabelKind.Raider, label.Request.Subject.Kind));
        Assert.All(placed, label => Assert.True(
            ReturningHeroLabel.IsCaptioned(
                state.Raiders.Single(raider => raider.Id == label.Request.Subject.Id)),
            $"«{label.Lines[0].Text}» is named with nothing pointed at."));
    }

    /// <summary>
    /// The rule stated on values rather than on the owner's scene, including the
    /// one raider it does not name: the one who has walked out through the gate is
    /// not on the map, so there is nothing to point at.
    /// </summary>
    [Fact]
    public void A_raider_who_has_left_through_the_gate_is_named_by_nothing()
    {
        var here = new GridPoint(20, 7);
        var stranger = new PrototypeRaiderSnapshot(
            1, 3, 30, 4, here, 0, 0, false, RaiderMode.Raiding, "Крюк");

        Assert.Equal(["Крюк"], ReturningHeroLabel.LinesUnderFocus(stranger));
        Assert.Empty(ReturningHeroLabel.Lines(stranger));
        Assert.Equal(
            ["Крюк"],
            ReturningHeroLabel.LinesUnderFocus(stranger with { Mode = RaiderMode.Downed }));
        Assert.Empty(ReturningHeroLabel.LinesUnderFocus(stranger with { Mode = RaiderMode.Escaped }));
    }

    /// <summary>
    /// The other body of another kind, so the focus a check hands the layout is a
    /// real pair rather than a hover with nothing selected beside it.
    /// </summary>
    private static WorldLabelSubject OtherThan(
        IReadOnlyList<WorldLabelSubject> bodies,
        WorldLabelSubject body) =>
        bodies.FirstOrDefault(item => item != body, body);

    /// <inheritdoc cref="WorldLabelLayoutTests"/>
    private static bool Intersect(ViewRect one, ViewRect other) =>
        one.X < other.X + other.Width &&
        other.X < one.X + one.Width &&
        one.Y < other.Y + other.Height &&
        other.Y < one.Y + one.Height;
}
