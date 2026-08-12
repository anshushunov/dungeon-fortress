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

    /// <summary>
    /// The two frames of the owner's party every check of this family reads.
    ///
    /// <para><b>They are found by their shape and no longer pinned to a tick, and
    /// that is the repair rather than a convenience.</b> They were ticks 2025 and
    /// 2380 — the two frames the owner captured on 2026-08-10 — and the party has
    /// moved under them twice since: the health scale of Issue #336 and the cell
    /// occupancy of Issue #76. Measured on this branch, tick 2025 carries no
    /// captioned returner at all, so <see cref="CrowdedFocus"/> throws before any
    /// invariant is reached, and tick 2380 carries one where it carried five. A
    /// tick number is the wrong anchor for a scene: what makes the frame the right
    /// frame is what is standing in it.</para>
    ///
    /// <list type="bullet">
    /// <item><see cref="OwnerFrame.WhereTheFirstReturnerIsNamed"/> — the first
    /// tick at which any raider carries a caption. That is the moment the mark of
    /// Issue #358 exists at all, and it is the thin frame: the returner stands
    /// alone and the layout has room for everything.</item>
    /// <item><see cref="OwnerFrame.WhereTheCrowdIsThickest"/> — of the ticks that
    /// carry a caption, the one with the most bodies standing on a single cell,
    /// earliest first. That is the толчея the layout rules exist for.</item>
    /// </list>
    ///
    /// <para>Both are taken from one walk of the party, because a walk costs a
    /// second and eleven checks read them.</para>
    /// </summary>
    public enum OwnerFrame
    {
        WhereTheFirstReturnerIsNamed,
        WhereTheCrowdIsThickest,
    }

    /// <inheritdoc cref="OwnerFrame"/>
    internal static PrototypeSnapshot OwnerScene(OwnerFrame frame) =>
        frame == OwnerFrame.WhereTheFirstReturnerIsNamed ? Scenes.Value.Thin : Scenes.Value.Crowded;

    /// <summary>
    /// How many bodies of both populations stand on the fullest cell of a frame.
    /// The raiders that have walked out through the gate are not on the map and
    /// are not counted.
    /// </summary>
    internal static int ThickestCrowd(PrototypeSnapshot state) =>
        state.Creatures
            .Select(creature => creature.Position)
            .Concat(state.Raiders
                .Where(raider => raider.Mode != RaiderMode.Escaped)
                .Select(raider => raider.Position))
            .GroupBy(position => position)
            .Max(group => group.Count());

    private static readonly Lazy<(PrototypeSnapshot Thin, PrototypeSnapshot Crowded)> Scenes =
        new(() =>
        {
            var world = new PrototypeWorld(
                PresentationFixtures.LogOf("baseline") with { Seed = OwnerSeed });
            PrototypeSnapshot? thin = null;
            PrototypeSnapshot? crowded = null;
            var thickest = 0;
            while (!world.IsComplete)
            {
                world.Step();
                var state = world.GetSnapshot();
                if (!state.Raiders.Any(ReturningHeroLabel.IsCaptioned))
                {
                    continue;
                }

                thin ??= state;
                var crowd = ThickestCrowd(state);
                if (crowd > thickest)
                {
                    thickest = crowd;
                    crowded = state;
                }
            }

            if (thin is null || crowded is null)
            {
                throw new InvalidOperationException(
                    "no tick of the owner's party carries a captioned returner, so neither frame " +
                    "of this family exists. That is a change in what the party does — nobody " +
                    "survives a wave and comes back — and not a broken test.");
            }

            return (thin, crowded);
        });

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

    private static IReadOnlyList<PlacedWorldLabel> Layout(OwnerFrame frame)
    {
        var state = OwnerScene(frame);
        return WorldLabels.Of(state, CrowdedFocus(state), CameraView.DefaultTileSize);
    }

    /// <summary>
    /// Criterion 2. Every pair, not a sample: a layout that separated most of the
    /// labels and printed two of them on each other would be the defect wearing a
    /// smaller hat.
    /// </summary>
    [Theory]
    [InlineData(OwnerFrame.WhereTheFirstReturnerIsNamed)]
    [InlineData(OwnerFrame.WhereTheCrowdIsThickest)]
    public void No_two_labels_of_the_owners_scene_are_printed_over_one_another(OwnerFrame frame)
    {
        var placed = Layout(frame);

        Assert.NotEmpty(placed);
        foreach (var one in placed)
        {
            foreach (var other in placed.Where(item => item != one))
            {
                Assert.False(
                    Intersect(one.Box, other.Box),
                    $"«{one.Lines[0].Text}» and «{other.Lines[0].Text}» " +
                    $"share pixels on the {frame} frame: {one.Box} against {other.Box}.");
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
    [InlineData(OwnerFrame.WhereTheFirstReturnerIsNamed)]
    [InlineData(OwnerFrame.WhereTheCrowdIsThickest)]
    public void No_label_ends_up_further_than_a_tile_from_its_own_body(OwnerFrame frame)
    {
        const double oneTile = 22.0;
        var placed = Layout(frame);

        Assert.NotEmpty(placed);
        foreach (var label in placed)
        {
            Assert.True(
                label.AttachmentRef <= oneTile,
                $"«{label.Lines[0].Text}» sits {label.AttachmentRef:F2} reference " +
                $"pixels from its body on the {frame} frame; the limit is {oneTile}.");
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
    /// <para><b>The two counts are gone and the rule is asserted instead.</b> They
    /// were <c>1, 1</c> and <c>5, 3</c> - recordings of two parties, re-pinned once
    /// already by Issue #361 - and the party has moved twice more since. What the
    /// counts were there to catch is a change that starts giving names up, and that
    /// is now said directly: <b>every captioned returner is named on the quiet map
    /// unless another captioned returner stands on its own cell</b>, because bodies
    /// that share a head share one ladder of places and a ladder holds three. A
    /// count could only ever suggest that; this states it, and it does not have to
    /// be rewritten the next time the balance moves.</para>
    [Theory]
    [InlineData(OwnerFrame.WhereTheFirstReturnerIsNamed)]
    [InlineData(OwnerFrame.WhereTheCrowdIsThickest)]
    public void Every_returning_raider_of_the_owners_scene_is_still_named(OwnerFrame frame)
    {
        var state = OwnerScene(frame);
        var asked = state.Raiders.Where(ReturningHeroLabel.IsCaptioned).ToArray();

        var placed = WorldLabels
            .Of(state, WorldLabelFocus.None, CameraView.DefaultTileSize)
            .Where(label => label.Request.Subject.Kind == WorldLabelKind.Raider)
            .Select(label => label.Request.Subject.Id)
            .ToHashSet();

        Assert.NotEmpty(asked);
        foreach (var raider in asked)
        {
            var sharingItsHead = asked.Count(other => other.Position == raider.Position);
            Assert.True(
                placed.Contains(raider.Id) || sharingItsHead > 1,
                $"«{raider.Name}» carries a caption on the {frame} frame, stands alone " +
                $"on ({raider.Position.X},{raider.Position.Y}) and is not named. Only a shared " +
                "head may cost a returner his name.");
        }
    }

    /// <summary>
    /// The names of the owner's wave-4 frame that are <em>not</em> shown, and why
    /// no layout can show them — stated as a check, so the reason stays true
    /// instead of merely staying written down.
    ///
    /// <para>Five returning raiders stand on cell (14,7) of that frame. They share
    /// one head, so their labels share one ladder of places; a label is ten
    /// reference pixels tall and the ladder is twenty-two long, so three fit above
    /// that head and the other two have nowhere inside the limit to be. <b>The
    /// reason is not the layout.</b> Five identical goblins standing on one cell
    /// cannot be told apart by any caption however placed — that is model
    /// differentiation, which the owner deferred himself on this very playtest:
    /// «модельки одинаковые … наверно это нужно делать в следующем подходе».</para>
    ///
    /// <para><b>The crowd of captions has left the shipped party, and the rule is
    /// what stayed.</b> Cell occupancy (Issue #76) keeps raiders in
    /// <c>RaiderMode.Raiding</c> off one another tiles, and a captioned returner is
    /// by definition a raiding one - so on the owner party no two of them share a
    /// head any more, and no caption is dropped at all. Measured over every tick of
    /// it: one captioned returner at a time, never two on a cell, never a name
    /// given up. The count of two unnamed and the count of five sharing were
    /// recordings of a party that no longer exists.</para>
    ///
    /// <para>So the check states the rule over both shipped frames - <b>a caption
    /// is dropped only where another caption shares its head</b> - which is
    /// currently satisfied by nothing being dropped, while the arithmetic of the
    /// ladder itself is held on values by
    /// <see cref="A_label_with_nowhere_to_go_is_dropped_rather_than_detached"/> and
    /// <see cref="A_caption_that_cannot_fit_whole_keeps_its_name_and_loses_its_sentence"/>,
    /// which build the crowd instead of waiting for the party to produce one.</para>
    /// </summary>
    [Theory]
    [InlineData(OwnerFrame.WhereTheFirstReturnerIsNamed)]
    [InlineData(OwnerFrame.WhereTheCrowdIsThickest)]
    public void The_names_that_are_not_shown_are_raiders_sharing_one_cell(OwnerFrame frame)
    {
        var state = OwnerScene(frame);
        var captioned = state.Raiders.Where(ReturningHeroLabel.IsCaptioned).ToArray();

        var placed = WorldLabels
            .Of(state, WorldLabelFocus.None, CameraView.DefaultTileSize)
            .Where(label => label.Request.Subject.Kind == WorldLabelKind.Raider)
            .Select(label => label.Request.Subject.Id)
            .ToHashSet();
        Assert.NotEmpty(captioned);

        // Asked over the captioned raiders and NOT over the unnamed ones. Since
        // Issue #76 the unnamed set is empty on both shipped frames, and an
        // Assert.All over an empty set is true identically — it states nothing at
        // all, which is what independent review of PR #402 caught here. Over
        // `captioned` the same rule is evaluated on subjects that exist and can
        // fail: mutant M10c, which places no raider caption at all, reddens it.
        Assert.All(captioned, raider => Assert.True(
            placed.Contains(raider.Id) ||
            captioned.Count(other => other.Position == raider.Position) > 1,
            $"«{raider.Name}» carries a caption on the {frame} frame and is not named, " +
            $"and nobody else with a caption stands on " +
            $"({raider.Position.X},{raider.Position.Y}). A name may be lost to a shared head and " +
            "to nothing else."));

        // ... and at least one caption is actually placed, so that «no name was
        // lost» cannot be satisfied by no name being drawn.
        Assert.True(
            captioned.Any(raider => placed.Contains(raider.Id)),
            $"no captioned raider is named at all on the {frame} frame, so the rule above " +
            "would be satisfied by an empty screen");
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

    /// <summary>
    /// Issue #389, stated on values: <b>the crew's caption bids its name in the
    /// first pass like everybody else's</b>, and grows into its full line
    /// afterwards.
    ///
    /// <para>The scene is the owner's, reduced to what makes it work: a crew member
    /// under the cursor on one cell and a crowd of returning raiders sharing the cell
    /// beside him. «Тишина DOWN» is seventy-nine reference pixels — three and a half
    /// tiles — so laid whole in the first pass it covers the neighbouring cell's own
    /// column and the two rungs above it; laid as «Тишина» it is forty-three and
    /// covers only its own. The first is what the map did before this Issue.</para>
    ///
    /// <para>Both halves are asserted, and the second is what stops the first from
    /// being bought by simply shortening the crew's label for good: the state comes
    /// back in the second pass, so what the player reads is unchanged wherever there
    /// is room for it — which is every crew member of both shipped frames
    /// (<c>evidence/389-after.json</c>).</para>
    /// </summary>
    [Fact]
    public void The_crews_caption_bids_its_name_first_and_grows_into_its_state_after()
    {
        var crowd = new GridPoint(20, 7);
        var mine = new GridPoint(21, 7);
        var crew = new WorldLabelRequest(
            new WorldLabelSubject(WorldLabelKind.Creature, 1),
            WorldLabelLayout.HeadOf(
                CameraView.CellCenter(mine, CameraView.DefaultTileSize),
                CameraView.DefaultTileSize),
            [new WorldLabelLine("Тишина DOWN", WorldLabelLayout.CreatureNameTextRef)],
            WorldLabelRank.Hovered,
            0,
            "Тишина");
        var raiders = new[] { "Гнилозуб", "Долговязый", "Сиплый" }
            .Select((name, index) => Request(
                index + 1,
                crowd,
                WorldLabelRank.Returning,
                [new WorldLabelLine(name, ReturningHeroLabel.NameTextRef)]))
            .ToArray();

        var placed = WorldLabelLayout.Place([crew, .. raiders], CameraView.DefaultTileSize);

        // Nobody on the neighbouring cell lost a name to the gesture.
        Assert.Equal(
            raiders.Length + 1,
            placed.Count);
        // And the crew member still says what it was saying: the state is back.
        Assert.Equal(
            "Тишина DOWN",
            placed.Single(label => label.Request.Subject.Kind == WorldLabelKind.Creature)
                .Lines[0]
                .Text);
    }

    /// <summary>
    /// The rung Issue #389 added, on its own: where there is no room for the whole
    /// line, the crew's caption is laid as its bare name rather than given up. It is
    /// the same third step of «полная подпись → только имя → ничего» that
    /// <see cref="A_caption_that_cannot_fit_whole_keeps_its_name_and_loses_its_sentence"/>
    /// states for a raider, and until this Issue the crew had no middle step at all:
    /// its name and its full text were one string, so the ladder went straight from
    /// «whole» to «nothing».
    ///
    /// <para>Stated on values because it is <b>not observable on either shipped
    /// scene</b>: on both of the owner's frames every crew label the layout places
    /// grows back into its full line. That is named here rather than left to be
    /// discovered, for the reason the price of Issue #379 was named — a rung nobody
    /// can reach is a claim about the future, and this one is only reachable on a
    /// frame more crowded than the ones we ship.</para>
    /// </summary>
    [Fact]
    public void A_crew_caption_that_cannot_fit_whole_keeps_its_name_and_loses_its_state()
    {
        var here = new GridPoint(20, 7);
        var crew = new WorldLabelRequest(
            new WorldLabelSubject(WorldLabelKind.Creature, 1),
            WorldLabelLayout.HeadOf(
                CameraView.CellCenter(here, CameraView.DefaultTileSize),
                CameraView.DefaultTileSize),
            [new WorldLabelLine("Тишина DOWN", WorldLabelLayout.CreatureNameTextRef)],
            WorldLabelRank.Hovered,
            0,
            "Тишина");
        var crowd = Enumerable
            .Range(0, 6)
            .Select(index => Request(
                index + 2,
                new GridPoint(here.X + (index < 3 ? -1 : 1), here.Y),
                WorldLabelRank.Returning,
                [new WorldLabelLine("Долговязый", ReturningHeroLabel.NameTextRef)]))
            .ToArray();

        var placed = WorldLabelLayout.Place([crew, .. crowd], CameraView.DefaultTileSize);
        var laid = Assert.Single(
            placed.Where(label => label.Request.Subject.Kind == WorldLabelKind.Creature));

        Assert.Equal("Тишина", laid.Lines[0].Text);
        Assert.Single(laid.Lines);
        // And it is the crowd that did it, not an empty answer. The frame is full
        // enough that labels are being given up outright — the upper bound below is
        // the whole crowd, so at least one neighbour got nothing — and the name
        // that survived is narrower than the line it came from rather than merely
        // spelled differently.
        Assert.InRange(placed.Count, 2, crowd.Length);
        Assert.True(
            WorldLabelLayout.WidthRef(laid.Lines) <
            WorldLabelLayout.WidthRef(crew.Lines));
    }

    /// <summary>
    /// Whether two rectangles share a pixel, written here rather than called on
    /// <c>WorldLabelLayout.Overlap</c>.
    ///
    /// <para>Independent review of PR #368 pointed out that the guard was
    /// self-referential: it asserted «no overlap» with the very function
    /// <c>Fit</c> uses to avoid overlap, so substituting <c>Overlap =&gt; false</c>
    /// left the guard green. Four neighbouring checks with counts closed the hole
    /// in practice; this closes it at the source, and the cost is four lines.</para>
    /// </summary>
    private static bool Intersect(ViewRect one, ViewRect other) =>
        one.X < other.X + other.Width &&
        other.X < one.X + one.Width &&
        one.Y < other.Y + other.Height &&
        other.Y < one.Y + one.Height;

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
        var kinds = Layout(OwnerFrame.WhereTheCrowdIsThickest)
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
