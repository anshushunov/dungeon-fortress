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

    /// <summary>
    /// Stepped to a <b>tick</b> and not for a number of steps, which is the same
    /// distinction <c>Main.LoadFixture</c> draws and for the same reason: a step
    /// stopped being a tick when the party learned to stand still between two
    /// waves. <c>PrototypeScenario.Run(log, 2380)</c> lands on tick 2260 of this
    /// journal — a different frame with a different set of raiders standing in it,
    /// and therefore not the frame the owner looked at.
    /// </summary>
    internal static PrototypeSnapshot OwnerScene(int ticks)
    {
        var world = new PrototypeWorld(
            PresentationFixtures.LogOf("baseline") with { Seed = OwnerSeed });
        while (!world.IsComplete && world.CurrentTick < ticks)
        {
            world.Step();
        }

        return world.GetSnapshot();
    }

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
    ///
    /// <para><b>The number is written out here and not read from the constant.</b>
    /// A check that measured the layout against whatever the layout currently
    /// declares its own limit to be would pass for any limit at all — raising the
    /// constant back to the unbounded ladder this replaces would move the
    /// assertion along with it and stay green. Twenty-two is the tile the whole
    /// assembly's geometry is authored against, and
    /// <see cref="The_limit_is_one_tile_and_that_is_what_the_layout_declares"/>
    /// is what ties the two together.</para>
    /// </summary>
    [Theory]
    [InlineData(WaveThreeTick)]
    [InlineData(WaveFourTick)]
    public void No_label_ends_up_further_than_a_tile_from_its_own_body(int ticks)
    {
        const double oneTile = 22.0;
        var placed = Layout(ticks);

        Assert.NotEmpty(placed);
        foreach (var label in placed)
        {
            Assert.True(
                label.AttachmentRef <= oneTile,
                $"«{label.Lines[0].Text}» sits {label.AttachmentRef:F2} reference " +
                $"pixels from its body at tick {ticks}; the limit is {oneTile}.");
        }
    }

    /// <inheritdoc cref="No_label_ends_up_further_than_a_tile_from_its_own_body"/>
    [Fact]
    public void The_limit_is_one_tile_and_that_is_what_the_layout_declares()
    {
        Assert.Equal(22.0, WorldLabelLayout.ReferenceTileSize);
        Assert.Equal(22.0, WorldLabelLayout.MaximumAttachmentRef);
    }

    /// <summary>
    /// What stops the two checks above from being satisfied by drawing nothing.
    /// Both of them are true of an empty layout, and an empty layout is a worse
    /// answer than the defect: the owner asked to recognise the raider who came
    /// back, and a frame with no names at all cannot be asked the question.
    ///
    /// <para>The frame is the owner's own, with nothing hovered and nothing
    /// selected, because that is the frame he was looking at and the one
    /// <c>evidence/364-before.png</c> was taken on. The counts are measured on the
    /// scene and stated here rather than derived: the point of the check is that a
    /// change which starts giving names up is noticed the day it happens.</para>
    /// </summary>
    [Theory]
    [InlineData(WaveThreeTick, 1, 1)]
    [InlineData(WaveFourTick, 6, 5)]
    public void Every_returning_raider_of_the_owners_scene_is_still_named(
        int ticks,
        int captioned,
        int named)
    {
        var state = OwnerScene(ticks);
        var asked = state.Raiders.Where(ReturningHeroLabel.IsCaptioned).ToArray();

        var placed = WorldLabels
            .Of(state, WorldLabelFocus.None, CameraView.DefaultTileSize)
            .Where(label => label.Request.Subject.Kind == WorldLabelKind.Raider)
            .ToArray();

        Assert.Equal(captioned, asked.Length);
        Assert.Equal(named, placed.Length);
    }

    /// <summary>
    /// The one name of the owner's wave-4 frame that is <em>not</em> shown, and why
    /// no layout can show it — stated as a check, so the reason stays true instead
    /// of merely staying written down.
    ///
    /// <para>Four returning raiders stand on cell (15,7) of that frame. They share
    /// one head, so their labels share one ladder of places; a label is ten
    /// reference pixels tall and the ladder is twenty-two long, so three fit above
    /// that head and the fourth has nowhere inside the limit to be. <b>The reason
    /// is not the layout.</b> Four identical goblins standing on one cell cannot be
    /// told apart by any caption however placed — that is model differentiation,
    /// which the owner deferred himself on this very playtest: «модельки одинаковые
    /// … наверно это нужно делать в следующем подходе».</para>
    /// </summary>
    [Fact]
    public void The_name_that_is_not_shown_is_one_of_four_raiders_sharing_a_cell()
    {
        var state = OwnerScene(WaveFourTick);
        var captioned = state.Raiders.Where(ReturningHeroLabel.IsCaptioned).ToArray();

        var placed = WorldLabels
            .Of(state, WorldLabelFocus.None, CameraView.DefaultTileSize)
            .Where(label => label.Request.Subject.Kind == WorldLabelKind.Raider)
            .Select(label => label.Request.Subject.Id)
            .ToHashSet();
        var unnamed = captioned.Where(raider => !placed.Contains(raider.Id)).ToArray();

        var crowded = Assert.Single(unnamed);
        Assert.Equal(4, captioned.Count(raider => raider.Position == crowded.Position));
    }

    /// <summary>
    /// What the two passes of <see cref="WorldLabelLayout.Place"/> buy. The second
    /// line of a caption is a sentence four and a half tiles wide; laid greedily —
    /// each label taking its whole text before the next one is looked at — it fills
    /// the only places the name beside it could have taken. Laying the names first
    /// and growing them afterwards is what keeps both on screen, and this states
    /// the difference as behaviour, so collapsing the two passes back into one
    /// cannot pass unnoticed.
    /// </summary>
    [Fact]
    public void A_sentence_never_takes_the_place_of_a_neighbours_name()
    {
        var sentence = new WorldLabelLine(
            "волна 2 · достали (23,7)",
            ReturningHeroLabel.StoryTextRef);
        var scarred = Request(
            1,
            new GridPoint(20, 7),
            WorldLabelRank.ReturningWithStory,
            [new WorldLabelLine("Секира", ReturningHeroLabel.NameTextRef), sentence]);
        var neighbour = Request(
            2,
            new GridPoint(21, 7),
            WorldLabelRank.Returning,
            [new WorldLabelLine("Сиплый", ReturningHeroLabel.NameTextRef)]);

        // The sentence is wider than the gap between the two heads, so under one
        // greedy pass it would cover the neighbour's own column outright.
        Assert.True(
            WorldLabelLayout.WidthRef(sentence) > WorldLabelLayout.ReferenceTileSize * 2);

        var placed = WorldLabelLayout.Place([scarred, neighbour], CameraView.DefaultTileSize);

        Assert.Equal(2, placed.Count);
        Assert.All(placed, label => Assert.True(
            label.AttachmentRef <= WorldLabelLayout.MaximumAttachmentRef));
        Assert.Equal(2, placed.Single(label => label.Request.Subject.Id == 1).Lines.Count);
        Assert.Single(placed.Single(label => label.Request.Subject.Id == 2).Lines);
    }

    /// <summary>
    /// The other half of the same rule: where there is no room for the sentence at
    /// all, the caption is laid as its bare name rather than given up. The name is
    /// the half the player recognises at a glance; the sentence is the half he
    /// reads when he has stopped to look, and stopping to look now opens a panel
    /// that carries the same words (<see cref="InspectorText.Raider"/>).
    /// </summary>
    [Fact]
    public void A_caption_that_cannot_fit_whole_keeps_its_name_and_loses_its_sentence()
    {
        var lines = new WorldLabelLine[]
        {
            new("Секира", ReturningHeroLabel.NameTextRef),
            new("волна 2 · достали (23,7)", ReturningHeroLabel.StoryTextRef),
        };
        var crowd = Enumerable
            .Range(1, 4)
            .Select(id => Request(
                id,
                new GridPoint(20, 7),
                WorldLabelRank.ReturningWithStory,
                lines))
            .ToArray();

        var placed = WorldLabelLayout.Place(crowd, CameraView.DefaultTileSize);

        Assert.NotEmpty(placed);
        Assert.Contains(placed, label => label.Lines.Count == 1);
        Assert.All(placed, label => Assert.Equal("Секира", label.Lines[0].Text));
    }

    private static WorldLabelRequest Request(
        int id,
        GridPoint cell,
        WorldLabelRank rank,
        IReadOnlyList<WorldLabelLine> lines) =>
        new(
            new WorldLabelSubject(WorldLabelKind.Raider, id),
            WorldLabelLayout.HeadOf(
                CameraView.CellCenter(cell, CameraView.DefaultTileSize),
                CameraView.DefaultTileSize),
            lines,
            rank,
            id);

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
