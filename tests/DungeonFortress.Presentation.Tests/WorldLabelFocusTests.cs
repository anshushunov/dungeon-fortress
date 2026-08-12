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
    /// <inheritdoc cref="WorldLabelLayoutTests.OwnerFrame"/>
    private const WorldLabelLayoutTests.OwnerFrame Thin =
        WorldLabelLayoutTests.OwnerFrame.WhereTheFirstReturnerIsNamed;

    /// <inheritdoc cref="Thin"/>
    private const WorldLabelLayoutTests.OwnerFrame Crowded =
        WorldLabelLayoutTests.OwnerFrame.WhereTheCrowdIsThickest;

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
    /// condition. Those counts describe the frame as it stood before Issue #361
    /// made the damage jitter live; the frame now carries twelve raiders of whom
    /// five are captioned, and the check itself names no count at all.</para>
    /// </summary>
    [Theory]
    [InlineData(Thin)]
    [InlineData(Crowded)]
    public void Every_raider_on_the_map_is_named_under_the_pointer_and_when_selected(
        WorldLabelLayoutTests.OwnerFrame frame)
    {
        var state = WorldLabelLayoutTests.OwnerScene(frame);
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
    /// Criterion 2 of Issue #371, re-stated by Issue #379 and re-stated again here:
    /// <b>the crowd is what takes a sentence off a caption, and where there is no
    /// crowd there is nothing to take.</b>
    ///
    /// <para><b>Why the name of this check changed.</b> It was
    /// <c>The_line_the_crowd_took_off_a_caption_comes_back_under_the_pointer_where_there_is_room</c>,
    /// and it read a caption the layout had shed on the owner wave-4 frame: five
    /// captioned returners shared cell (14,7), they shared one ladder of places, and
    /// «Сиплый» came out of it with a bare name. <b>That scene is no longer in the
    /// party.</b> Cell occupancy (Issue #76) keeps raiding raiders off one another
    /// tiles and a captioned returner is a raiding one, so over every tick of the
    /// owner party there is now at most one caption at a time and nothing is ever
    /// shed. Its own docstring already recorded that the promise of #371 - a gesture
    /// hands the sentence back - is <em>not</em> what this check held, and that the
    /// number of sentences a gesture gives back on a shipped scene is zero
    /// (<c>evidence/379-criterion4.json</c>). What it held is the sentence above,
    /// and that is what it is now named for.
    ///
    /// <para>Both halves are asserted on both shipped frames, and the first is what
    /// makes the second mean anything: no caption is shed at all, and the caption
    /// that carries a story keeps its whole text with nothing pointed at, under the
    /// pointer, and while selected. Including <c>None</c> is deliberate - the quiet
    /// map has to be asserted alongside the gestures, so that a layout which simply
    /// stopped shedding anything could not pass by doing nothing.</para>
    ///
    /// <para>The arithmetic of the shed line itself - a ladder of twenty-two
    /// reference pixels, a name of ten, a two-line box of seventeen and a half - is
    /// held on values by
    /// <see cref="WorldLabelLayoutTests.A_caption_that_cannot_fit_whole_keeps_its_name_and_loses_its_sentence"/>,
    /// which builds the crowd rather than waiting for the party to produce one.</para>
    /// </summary>
    [Theory]
    [InlineData(Thin)]
    [InlineData(Crowded)]
    public void The_crowd_is_what_takes_a_sentence_and_where_there_is_none_nothing_is_taken(
        WorldLabelLayoutTests.OwnerFrame frame)
    {
        var state = WorldLabelLayoutTests.OwnerScene(frame);
        var quiet = WorldLabels
            .Of(state, WorldLabelFocus.None, CameraView.DefaultTileSize)
            .Where(placed => placed.Request.Subject.Kind == WorldLabelKind.Raider)
            .ToArray();

        // Nothing is shed on this frame, and every caption that is shed anywhere has
        // to be one whose head is shared - which is the rule, stated where it can be
        // seen to hold rather than assumed because the crowd has gone.
        foreach (var placed in quiet.Where(item => item.Lines.Count < item.Request.Lines.Count))
        {
            var owner = state.Raiders.Single(raider => raider.Id == placed.Request.Subject.Id);
            Assert.True(
                WorldLabels.BodiesAt(state, owner.Position).Count > 1,
                $"«{owner.Name}» lost a line on the {frame} frame while standing alone on " +
                $"({owner.Position.X},{owner.Position.Y}). Only a shared head may cost a caption " +
                "its sentence.");
        }

        // And the caption that has a sentence keeps it, with the pointer off and on.
        var alone = state.Raiders.Single(raider =>
            ReturningHeroLabel.IsCaptioned(raider) && ReturningHeroLabel.Story(raider) is not null);
        var body = new WorldLabelSubject(WorldLabelKind.Raider, alone.Id);
        foreach (var focus in new[]
                 {
                     WorldLabelFocus.None,
                     new WorldLabelFocus(body, null),
                     new WorldLabelFocus(null, body),
                 })
        {
            var label = Assert.Single(
                WorldLabels
                    .Of(state, focus, CameraView.DefaultTileSize)
                    .Where(placed => placed.Request.Subject == body));

            Assert.Equal(
                [alone.Name, ReturningHeroLabel.Story(alone)],
                label.Lines.Select(line => line.Text));
        }
    }

    /// <summary>
    /// <b>The guard of Issue #379: a sentence never costs anybody his name — not
    /// somebody else's sentence, and not the sentence of the body under the
    /// cursor.</b>
    ///
    /// <para>It is stated as a comparison of two layouts of the same frame: the one
    /// the game draws, and the one it would draw if no caption had a second line at
    /// all. Everything else about the two is identical — the same focus, the same
    /// ranks, the same scene order, the same bodies asking — so the only thing the
    /// comparison is sensitive to is what the second lines cost. <b>The set of named
    /// bodies must be the same set.</b></para>
    ///
    /// <para><b>Why this shape and not «the frame never names fewer bodies».</b>
    /// That statement is false for a reason no layout can fix and Issue #364 already
    /// measured: five captioned raiders share cell (14,7) of the wave-4 frame, they
    /// therefore share one head and one ladder of places, and three of them fit.
    /// Pointing at a fourth means one of the three has to go — the crowd doing what
    /// <see cref="WorldLabelLayoutTests.The_names_that_are_not_shown_are_raiders_sharing_one_cell"/>
    /// states. Calling <em>that</em> the defect would be blaming the rule for the
    /// tile. The defect independent review of PR #376 found is the other one: on the
    /// base commit, pointing at «Сиплый» took the frame from three names to
    /// <b>two</b>, because his caption bid its whole four-and-a-half-tile sentence in
    /// the first pass and «Долговязый» lost his name to it.</para>
    ///
    /// <para>Both gestures and every body of the frame, because Issue #371 gave the
    /// second line to hover <em>and</em> to selection: whatever one of them can cost,
    /// the other can cost too.</para>
    /// </summary>
    [Theory]
    [InlineData(Thin)]
    [InlineData(Crowded)]
    public void A_second_line_never_costs_a_neighbour_his_name(
        WorldLabelLayoutTests.OwnerFrame frame)
    {
        var state = WorldLabelLayoutTests.OwnerScene(frame);
        var bodies = BodiesOf(state);

        Assert.NotEmpty(bodies);
        foreach (var focus in bodies
                     .SelectMany(body => new[]
                     {
                         new WorldLabelFocus(body, null),
                         new WorldLabelFocus(null, body),
                     })
                     .Prepend(WorldLabelFocus.None))
        {
            var requests = WorldLabels.Requests(state, focus, CameraView.DefaultTileSize);
            var drawn = WorldLabelLayout.Place(requests, CameraView.DefaultTileSize);
            var namesOnly = WorldLabelLayout.Place(
                requests
                    .Select(request => request with { Lines = new[] { request.Lines[0] } })
                    .ToArray(),
                CameraView.DefaultTileSize);

            Assert.Equal(NamedBodies(namesOnly), NamedBodies(drawn));
        }
    }

    /// <summary>
    /// <b>The guard of Issue #389: a gesture never takes a name away from a body
    /// standing somewhere else.</b>
    ///
    /// <para><b>Why a second guard and not a wider one.</b> The check above holds the
    /// focus <em>constant</em> in both of the layouts it compares — same hover, same
    /// selection, the difference being only whether captions carry a second line. A
    /// comparison built that way cannot see anything that depends on <em>where the
    /// cursor is</em>, and that is not a gap in its wording but its construction: the
    /// gesture is on both sides of the equals sign. So on the base commit of this
    /// Issue it was green while pointing at the crew member «Тишина» on (15,7) took
    /// the wave-4 map from three raider names to one. This check varies the focus
    /// instead — every body of the frame, both gestures, each against the quiet map —
    /// and is therefore the one that can fail on that class at all.</para>
    ///
    /// <para><b>The exception is the tile and not the rule.</b> A body standing on the
    /// focused body's own cell may still lose its name, and no layout can prevent it:
    /// five captioned raiders share cell (14,7) of the wave-4 frame, they share one
    /// head and therefore one ladder of places, three fit, and pointing at a fourth
    /// means one of the three has to go
    /// (<see cref="WorldLabelLayoutTests.The_names_that_are_not_shown_are_raiders_sharing_one_cell"/>).
    /// Calling that a defect would be blaming the rule for the tile. Losing the name
    /// of somebody on <em>another</em> cell is the other thing entirely — the gesture
    /// reaching past its own body — and that is what this refuses.</para>
    ///
    /// <para><b>And the gesture has to answer.</b> Every focused body is asserted to
    /// be named under its own gesture, because without it a build where pointing at
    /// something did nothing at all would satisfy every line above: no label added,
    /// nothing displaced, no name lost.</para>
    /// </summary>
    [Theory]
    [InlineData(Thin)]
    [InlineData(Crowded)]
    public void A_gesture_never_takes_a_name_from_a_body_on_another_cell(
        WorldLabelLayoutTests.OwnerFrame frame)
    {
        var state = WorldLabelLayoutTests.OwnerScene(frame);

        Assert.NotEmpty(BodiesOf(state));
        foreach (var body in BodiesOf(state))
        {
            var here = CellOf(state, body);
            foreach (var focus in new[]
                     {
                         new WorldLabelFocus(body, null),
                         new WorldLabelFocus(null, body),
                     })
            {
                // The one line that decides whether this guard can see anything:
                // the layout the gesture is measured against is the *quiet* map.
                // Written out as a local rather than inlined so that the
                // substitution which makes a guard of this shape blind — holding
                // the focus constant in both layouts, as the guard above does — is
                // one token, and evidence/389-mutants.json runs exactly that one.
                var reference = WorldLabelFocus.None;
                var quiet = NamedSubjects(
                    WorldLabels.Of(state, reference, CameraView.DefaultTileSize));
                var named = NamedSubjects(
                    WorldLabels.Of(state, focus, CameraView.DefaultTileSize));

                Assert.NotEmpty(quiet);
                Assert.Contains(body, named);
                foreach (var lost in quiet.Where(subject => !named.Contains(subject)))
                {
                    Assert.True(
                        CellOf(state, lost) == here,
                        $"pointing at «{NameOf(state, body)}» on {here} of the {frame} frame " +
                        $"costs «{NameOf(state, lost)}» on {CellOf(state, lost)} his name; " +
                        $"the quiet map names {quiet.Count} bodies and this one names " +
                        $"{named.Count}.");
                }
            }
        }
    }

    /// <summary>
    /// The other half of the same rule, and the half Issue #371 bought: the body the
    /// player is asking about is the <b>first</b> one offered its sentence back once
    /// every name is down. A neighbour that also carries a sentence and stands
    /// earlier in scene order does not take the only place there is.
    ///
    /// <para>Stated on values rather than on the owner's scene because it is a
    /// statement about order, and an order needs a scene where two labels want one
    /// place. This is that scene: two returners carrying a sentence stand on
    /// neighbouring cells with a bare name each beside them, the bare names take the
    /// rungs the second sentence would have used, and one sentence is all that fits.
    /// The focused label is the <em>later</em> of the two in scene order, so a second
    /// pass that walked the placed labels in scene order instead of rank order would
    /// hand the place to the other one — which is the substitution
    /// <c>evidence/379-mutants.json</c> runs against this check.</para>
    ///
    /// <para><b>It says «offered», not «given».</b> Being first in the queue is not
    /// the same as fitting: on a head five other bodies share, the label the cursor
    /// is on is served its <em>name</em> first, which puts it at the bottom of the
    /// ladder with its neighbours' names above it and no room to grow into. That is
    /// the owner's wave-4 cell (14,7), it is measured in
    /// <see cref="The_crowd_is_what_takes_a_sentence_and_where_there_is_none_nothing_is_taken"/>,
    /// and it is the price named in <c>WorldLabelLayout.FirstAttempt</c>.</para>
    /// </summary>
    [Fact]
    public void The_body_under_the_cursor_is_offered_its_sentence_first()
    {
        var mine = new GridPoint(20, 7);
        var theirs = new GridPoint(21, 7);
        var neighbour = Caption(1, mine, "Косой", WorldLabelRank.ReturningWithStory, 0);
        var asked = Caption(2, theirs, "Сиплый", WorldLabelRank.Hovered, 1);
        var crowd = new[]
        {
            Name(3, mine, "Бурый", 2),
            Name(4, theirs, "Хват", 3),
        };

        var placed = WorldLabelLayout.Place(
            [neighbour, asked, .. crowd],
            CameraView.DefaultTileSize);

        Assert.Equal(4, placed.Count);
        Assert.Equal(
            2,
            placed.Single(label => label.Request.Subject == asked.Subject).Lines.Count);
        Assert.Single(placed.Single(label => label.Request.Subject == neighbour.Subject).Lines);
    }

    /// <summary>Both populations of a frame, in one list.</summary>
    private static IReadOnlyList<WorldLabelSubject> BodiesOf(PrototypeSnapshot state) =>
    [
        .. state.Raiders
            .Where(raider => raider.Mode != RaiderMode.Escaped)
            .Select(raider => new WorldLabelSubject(WorldLabelKind.Raider, raider.Id)),
        .. state.Creatures
            .Select(creature => new WorldLabelSubject(WorldLabelKind.Creature, creature.Id)),
    ];

    /// <summary>
    /// Who is named by a layout, in a form two layouts can be compared by: the
    /// bodies, sorted, without the boxes — which line each label ended up with is a
    /// different question from whether the body is named at all.
    /// </summary>
    private static IReadOnlyList<string> NamedBodies(IReadOnlyList<PlacedWorldLabel> placed) =>
    [
        .. placed
            .Select(label => $"{label.Request.Subject.Kind}#{label.Request.Subject.Id}")
            .OrderBy(text => text, StringComparer.Ordinal),
    ];

    /// <inheritdoc cref="NamedBodies"/>
    private static IReadOnlyList<WorldLabelSubject> NamedSubjects(
        IReadOnlyList<PlacedWorldLabel> placed) =>
    [
        .. placed.Select(label => label.Request.Subject),
    ];

    /// <summary>Which cell a body of either population stands on.</summary>
    private static GridPoint CellOf(PrototypeSnapshot state, WorldLabelSubject body) =>
        body.Kind == WorldLabelKind.Creature
            ? state.Creatures.Single(creature => creature.Id == body.Id).Position
            : state.Raiders.Single(raider => raider.Id == body.Id).Position;

    /// <summary>What a body is called, for a failure message that names names.</summary>
    private static string NameOf(PrototypeSnapshot state, WorldLabelSubject body) =>
        body.Kind == WorldLabelKind.Creature
            ? state.Creatures.Single(creature => creature.Id == body.Id).Name
            : state.Raiders.Single(raider => raider.Id == body.Id).Name;

    /// <summary>A raider nobody reached: one line, and no sentence to grow.</summary>
    private static WorldLabelRequest Name(int id, GridPoint cell, string name, int order) =>
        new(
            new WorldLabelSubject(WorldLabelKind.Raider, id),
            WorldLabelLayout.HeadOf(
                CameraView.CellCenter(cell, CameraView.DefaultTileSize),
                CameraView.DefaultTileSize),
            [new WorldLabelLine(name, ReturningHeroLabel.NameTextRef)],
            WorldLabelRank.Returning,
            order);

    /// <summary>A two-line caption asking for a place over a named cell.</summary>
    private static WorldLabelRequest Caption(
        int id,
        GridPoint cell,
        string name,
        WorldLabelRank rank,
        int order) =>
        new(
            new WorldLabelSubject(WorldLabelKind.Raider, id),
            WorldLabelLayout.HeadOf(
                CameraView.CellCenter(cell, CameraView.DefaultTileSize),
                CameraView.DefaultTileSize),
            [
                new WorldLabelLine(name, ReturningHeroLabel.NameTextRef),
                new WorldLabelLine("волна 2 · достали (24,7)", ReturningHeroLabel.StoryTextRef),
            ],
            rank,
            order);

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
    [InlineData(Thin)]
    [InlineData(Crowded)]
    public void Pointing_at_any_body_of_the_frame_breaks_neither_spacing_nor_attachment(
        WorldLabelLayoutTests.OwnerFrame frame)
    {
        const double oneTile = 22.0;
        var state = WorldLabelLayoutTests.OwnerScene(frame);
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
                    $"from its body on the {frame} frame while «{body}» is pointed at; " +
                    $"the limit is {oneTile}.");
                foreach (var other in placed.Where(item => item != one))
                {
                    Assert.False(
                        Intersect(one.Box, other.Box),
                        $"«{one.Lines[0].Text}» and «{other.Lines[0].Text}» share pixels " +
                        $"on the {frame} frame while «{body}» is pointed at: " +
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
    ///
    /// <para><b>The two counts per row are gone and the selectiveness is asserted
    /// instead.</b> They were <c>10, 1</c> and <c>11, 5</c>, then <c>12, 3</c> after
    /// Issue #361 re-pinned them - recordings of a party, and the party has moved
    /// twice more since. What the pair was there to catch is a rule leaking into
    /// the quiet map, and that is now said as what it is: the quiet map names
    /// <b>only</b> raiders the domain has met, and the frame carries at least one
    /// raider it does not name. A frame that started naming strangers fails the
    /// first half; a frame on which the rule had quietly become name everybody
    /// fails the second. Neither has to be rewritten when the balance moves.</para>
    /// </summary>
    [Theory]
    [InlineData(Thin)]
    [InlineData(Crowded)]
    public void With_nothing_pointed_at_the_map_names_exactly_who_it_named_before(
        WorldLabelLayoutTests.OwnerFrame frame)
    {
        var state = WorldLabelLayoutTests.OwnerScene(frame);
        var placed = WorldLabels.Of(state, WorldLabelFocus.None, CameraView.DefaultTileSize);
        var onMap = state.Raiders.Where(raider => raider.Mode != RaiderMode.Escaped).ToArray();

        Assert.NotEmpty(placed);
        // Every one of them is a raider the domain has met: no crew member is
        // named with nothing pointed at, and no stranger is either.
        Assert.All(placed, label => Assert.Equal(WorldLabelKind.Raider, label.Request.Subject.Kind));
        Assert.All(placed, label => Assert.True(
            ReturningHeroLabel.IsCaptioned(
                state.Raiders.Single(raider => raider.Id == label.Request.Subject.Id)),
            $"«{label.Lines[0].Text}» is named with nothing pointed at."));
        // And the quiet map is selective rather than silent: the frame holds
        // raiders it deliberately leaves unnamed.
        Assert.True(
            onMap.Length > placed.Count,
            $"the {frame} frame has {onMap.Length} raider(s) on the map and the quiet map names " +
            $"{placed.Count} of them. With nothing pointed at, the map names the ones the domain " +
            "has met and nobody else - a frame where it names all of them is the rule taken wider " +
            "than the owner took it.");
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
