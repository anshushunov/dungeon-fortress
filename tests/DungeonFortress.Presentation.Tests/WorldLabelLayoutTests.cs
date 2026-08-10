using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The guard of Issue #364, on the owner's own scene.
///
/// <para>It reddens on two separate defects, and that separation is the point:
/// the technique this replaces had solved one of them by causing the other.
/// <see cref="No_two_labels_of_the_owners_scene_are_printed_over_one_another"/>
/// is about overlap, <see cref="No_label_ends_up_further_than_a_tile_from_its_own_body"/>
/// is about attachment, and neither passes by making the other fail — the third
/// check below refuses a layout that simply drew nothing.</para>
/// </summary>
public sealed class WorldLabelLayoutTests
{
    /// <summary>
    /// The scene the owner played on 2026-08-10: the shipped baseline journal at
    /// the seed he ran it with. Nothing about this is a fixture written for the
    /// test — the same two numbers reproduce his frames through
    /// <c>scripts/run-game.ps1 -Fixture baseline -Seed 20260729</c>.
    /// </summary>
    private const ulong OwnerSeed = 20260729UL;

    /// <summary>The tick his wave-3 frame was captured at, and his wave-4 one.</summary>
    private const int WaveThreeTick = 2025;

    private const int WaveFourTick = 2380;

    private static PrototypeSnapshot OwnerScene(int ticks) =>
        PrototypeScenario.Run(
            PresentationFixtures.LogOf("baseline") with { Seed = OwnerSeed },
            ticks).State;

    /// <summary>
    /// The frame as the owner had it: he was pointing at one body and had another
    /// selected, so the crew's labels and the raiders' captions are on screen at
    /// the same time. Both are picked from the scene rather than named, so the
    /// check keeps meaning the same thing if the journal moves.
    /// </summary>
    private static WorldLabelFocus CrowdedFocus(PrototypeSnapshot state)
    {
        var captioned = state.Raiders.Where(ReturningHeroLabel.IsCaptioned).ToArray();
        Assert.NotEmpty(captioned);
        var nearest = state.Creatures
            .OrderBy(creature => captioned.Min(raider =>
                Math.Abs(raider.Position.X - creature.Position.X) +
                Math.Abs(raider.Position.Y - creature.Position.Y)))
            .ThenBy(creature => creature.Id)
            .First();
        var other = state.Creatures
            .Where(creature => creature.Id != nearest.Id)
            .OrderBy(creature => creature.Id)
            .First();
        return new WorldLabelFocus(
            new WorldLabelSubject(WorldLabelKind.Creature, nearest.Id),
            new WorldLabelSubject(WorldLabelKind.Creature, other.Id));
    }

    private static IReadOnlyList<PlacedWorldLabel> Layout(int ticks)
    {
        var state = OwnerScene(ticks);
        return WorldLabels.Of(state, CrowdedFocus(state), CameraView.DefaultTileSize);
    }

    /// <summary>
    /// Criterion 2. Every pair, not a sample: a layout that separated most of the
    /// labels and printed two of them on each other would be the defect wearing a
    /// smaller hat.
    /// </summary>
    [Theory]
    [InlineData(WaveThreeTick)]
    [InlineData(WaveFourTick)]
    public void No_two_labels_of_the_owners_scene_are_printed_over_one_another(int ticks)
    {
        var placed = Layout(ticks);

        Assert.NotEmpty(placed);
        foreach (var one in placed)
        {
            foreach (var other in placed.Where(item => item != one))
            {
                Assert.False(
                    WorldLabelLayout.Overlap(one.Box, other.Box),
                    $"«{one.Lines[0].Text}» and «{other.Lines[0].Text}» " +
                    $"share pixels at tick {ticks}: {one.Box} against {other.Box}.");
            }
        }
    }

    /// <summary>
    /// Criterion 3, and the half the technique this replaces failed. One tile is
    /// the limit and the reason is in
    /// <see cref="WorldLabelLayout.MaximumAttachmentRef"/>: bodies stand a tile
    /// apart, so a label further than that from its own head is nearer somebody
    /// else's.
    /// </summary>
    [Theory]
    [InlineData(WaveThreeTick)]
    [InlineData(WaveFourTick)]
    public void No_label_ends_up_further_than_a_tile_from_its_own_body(int ticks)
    {
        var placed = Layout(ticks);

        Assert.NotEmpty(placed);
        foreach (var label in placed)
        {
            Assert.True(
                label.AttachmentRef <= WorldLabelLayout.MaximumAttachmentRef,
                $"«{label.Lines[0].Text}» sits {label.AttachmentRef:F2} reference " +
                $"pixels from its body at tick {ticks}; the limit is " +
                $"{WorldLabelLayout.MaximumAttachmentRef}.");
        }
    }

    /// <summary>
    /// What stops the two checks above from being satisfied by drawing nothing.
    /// Both of them are true of an empty layout, and an empty layout is a worse
    /// answer than the defect: the owner asked to recognise the raider who came
    /// back, and a frame with no names at all cannot be asked the question.
    ///
    /// <para>The numbers are measured on the scene and stated here rather than
    /// derived, because the point of the check is that a change which starts
    /// giving labels up is noticed.</para>
    /// </summary>
    [Theory]
    [InlineData(WaveThreeTick, 1)]
    [InlineData(WaveFourTick, 4)]
    public void Every_returning_raider_of_the_owners_scene_is_still_named(int ticks, int expected)
    {
        var state = OwnerScene(ticks);
        var captioned = state.Raiders.Where(ReturningHeroLabel.IsCaptioned).ToArray();

        var placed = Layout(ticks)
            .Where(label => label.Request.Subject.Kind == WorldLabelKind.Raider)
            .ToArray();

        Assert.Equal(expected, captioned.Length);
        Assert.Equal(
            captioned.Select(raider => raider.Name).OrderBy(name => name, StringComparer.Ordinal),
            placed.Select(label => label.Lines[0].Text)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// The crew and the raiders are laid out by one pass and not two. Before Issue
    /// #364 they were two, and neither could see the other, so a collision
    /// <em>between</em> them was not resolvable anywhere: this asserts that the
    /// frame the two checks above measured really did contain both kinds.
    /// </summary>
    [Fact]
    public void One_layout_holds_the_crew_and_the_raiders_together()
    {
        var kinds = Layout(WaveFourTick)
            .Select(label => label.Request.Subject.Kind)
            .Distinct()
            .OrderBy(kind => kind);

        Assert.Equal([WorldLabelKind.Creature, WorldLabelKind.Raider], kinds);
    }

    /// <summary>
    /// What the limit buys, stated as behaviour rather than as a constant: when
    /// the places inside a tile of the head are all taken, the label is left out
    /// of the answer instead of being pushed somewhere it no longer belongs to
    /// anybody. Six two-line captions stacked on one cell is the case that cannot
    /// be solved without giving one up.
    /// </summary>
    [Fact]
    public void A_label_with_nowhere_to_go_is_dropped_rather_than_detached()
    {
        var here = new GridPoint(20, 7);
        var requests = Enumerable.Range(1, 6).Select(id => new WorldLabelRequest(
            new WorldLabelSubject(WorldLabelKind.Raider, id),
            WorldLabelLayout.HeadOf(
                CameraView.CellCenter(here, CameraView.DefaultTileSize),
                CameraView.DefaultTileSize),
            [
                new WorldLabelLine($"Налётчик {id}", ReturningHeroLabel.NameTextRef),
                new WorldLabelLine("волна 2 · едва не добили (20,7)", ReturningHeroLabel.StoryTextRef),
            ],
            WorldLabelRank.ReturningWithStory,
            id)).ToArray();

        var placed = WorldLabelLayout.Place(requests, CameraView.DefaultTileSize);

        Assert.InRange(placed.Count, 1, requests.Length - 1);
        Assert.All(placed, label => Assert.True(
            label.AttachmentRef <= WorldLabelLayout.MaximumAttachmentRef));
        foreach (var one in placed)
        {
            Assert.All(
                placed.Where(item => item != one),
                other => Assert.False(WorldLabelLayout.Overlap(one.Box, other.Box)));
        }
    }

    /// <summary>
    /// The label is over the head and not over the body. "Над головой" is the
    /// owner's own wording, and the number it turns into is the top of the drawn
    /// pixels of the sprite pack: the box's bottom edge is above it, and nothing
    /// of the label reaches down into the face.
    /// </summary>
    [Fact]
    public void The_label_sits_above_the_drawn_body_and_not_over_it()
    {
        var centre = CameraView.CellCenter(new GridPoint(10, 6), CameraView.DefaultTileSize);
        var body = CameraView.GoblinOpaqueRect(centre, CameraView.DefaultTileSize);
        var request = new WorldLabelRequest(
            new WorldLabelSubject(WorldLabelKind.Creature, 1),
            WorldLabelLayout.HeadOf(centre, CameraView.DefaultTileSize),
            [new WorldLabelLine("Брусок READY", WorldLabelLayout.CreatureNameTextRef)],
            WorldLabelRank.Hovered,
            0);

        var placed = Assert.Single(
            WorldLabelLayout.Place([request], CameraView.DefaultTileSize));

        Assert.True(
            placed.Box.Y + placed.Box.Height <= body.Y,
            $"the label reaches down to {placed.Box.Y + placed.Box.Height} and the body's " +
            $"drawn pixels start at {body.Y}.");
        // Centred over the head rather than hanging off to one side, which is what
        // the crew's label used to do: its box began nine reference pixels left of
        // the body and ran ninety-eight to the right of that.
        Assert.Equal(WorldLabelSide.Centre, placed.Alignment);
        Assert.Equal(centre.X, placed.Box.Center.X, 6);
    }

    /// <summary>
    /// A wider label is a wider box. It is asserted because the estimate is what
    /// the whole layout is resolved against: a width that stopped following the
    /// text would make every collision answer meaningless without failing
    /// anything else.
    /// </summary>
    [Fact]
    public void A_box_is_measured_from_the_text_and_not_from_a_fixed_width()
    {
        var narrow = new WorldLabelLine("Крюк", ReturningHeroLabel.NameTextRef);
        var wide = new WorldLabelLine("волна 2 · достали (23,7)", ReturningHeroLabel.NameTextRef);

        Assert.True(WorldLabelLayout.WidthRef(narrow) < WorldLabelLayout.WidthRef(wide));
        Assert.Equal(
            "Крюк".Length * WorldLabelLayout.GlyphAdvanceRef * ReturningHeroLabel.NameTextRef,
            WorldLabelLayout.WidthRef(narrow),
            9);
        // Two lines are taller than one, and the box of a two-line caption clears
        // the one-line box below it by construction rather than by a slot height.
        Assert.True(
            WorldLabelLayout.HeightRef([narrow, wide]) >
            WorldLabelLayout.HeightRef([narrow]));
    }
}
