using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// What a room says about itself in a glyph and a word, and the warning on an
/// object that no room has taken in.
///
/// The reading a room has is <see cref="RoomAccentTests"/>; this is the part the
/// player actually reads. Both halves of ADR 0013's «иконка назначения и подпись
/// с состоянием» are here, and so is the other half of the silence the ADR names:
/// a zone with nothing in it says so on its own caption, and an object with no
/// zone over it has no caption to say it with, so it gets a mark of its own.
/// </summary>
public sealed class RoomReadingTests
{
    private static readonly GridPoint LeftPost = new(10, 2);
    private static readonly GridPoint RightPost = new(11, 2);
    private static readonly GridPoint EmptyFloorA = new(25, 6);

    private static MapProjection View(int ticks, params PrototypeCommand[] commands) =>
        RoomAccentTests.View(ticks, commands);

    private static PrototypeRoomSnapshot Room(MapProjection view, string id) =>
        RoomAccentTests.Room(view, id);

    // ---------------------------------------------------------------- the icon

    [Fact]
    public void Every_purpose_has_an_icon_and_no_two_share_one()
    {
        Assert.Equal(Enum.GetValues<ZoneKind>().Order(), RoomIcons.Declared);

        var rendered = Enum.GetValues<ZoneKind>()
            .Select(purpose => string.Join(
                "|",
                RoomIcons.Of(purpose).Select(stroke => string.Join(
                    " ",
                    stroke.Select(point => $"{point.X},{point.Y}")))))
            .ToArray();

        Assert.Equal(rendered.Length, rendered.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A glyph is strokes, and a stroke needs two points. One point would be drawn
    /// as nothing at all and would look like a rendering fault.
    /// </summary>
    [Fact]
    public void Every_stroke_of_every_icon_is_a_line_inside_the_unit_box()
    {
        foreach (var purpose in Enum.GetValues<ZoneKind>())
        {
            var glyph = RoomIcons.Of(purpose);
            Assert.NotEmpty(glyph);
            foreach (var stroke in glyph)
            {
                Assert.True(stroke.Count >= 2, $"{purpose} has a stroke of {stroke.Count} point(s)");
                foreach (var point in stroke)
                {
                    Assert.InRange(point.X, 0.0, 1.0);
                    Assert.InRange(point.Y, 0.0, 1.0);
                }
            }
        }
    }

    [Fact]
    public void A_purpose_with_no_icon_is_refused_rather_than_drawn_blank()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomIcons.Of((ZoneKind)(-1)));
    }

    // ------------------------------------------------------------- the caption

    /// <summary>
    /// A working room is its name and nothing else; anything else says what to go
    /// and do about it. The missing-object wording names the object the simulation
    /// actually requires rather than a word chosen here.
    /// </summary>
    [Fact]
    public void The_caption_of_a_working_room_is_its_name_and_nothing_else()
    {
        var view = View(3, new ZonePaintCommand(1, ZoneKind.Watch, [EmptyFloorA]),
            new SetPriorityCommand(1, JobKind.Watch, 2));

        Assert.Equal("WATCH", RoomLabels.Caption(Room(view, "watch@25,6")));
        Assert.Equal("FARM", RoomLabels.Caption(Room(view, "farm@1,1")));
    }

    [Fact]
    public void The_caption_of_a_room_that_is_not_working_says_what_is_wrong()
    {
        var unfinished = View(2, new ZonePaintCommand(1, ZoneKind.TrainingGround, [EmptyFloorA]));
        Assert.Equal(
            "TRAIN · no post",
            RoomLabels.Caption(Room(unfinished, "trainingGround@25,6")));

        var blocked = View(2, new ZonePaintCommand(1, ZoneKind.TrainingGround, [LeftPost]));
        Assert.Equal("TRAIN · off", RoomLabels.Caption(Room(blocked, "trainingGround@10,2")));

        var shut = View(
            3,
            new ZonePaintCommand(1, ZoneKind.Watch, [EmptyFloorA]),
            new ZonePaintCommand(2, ZoneKind.Forbidden, [EmptyFloorA]));
        Assert.Equal("WATCH · forbidden", RoomLabels.Caption(Room(shut, "watch@25,6")));
    }

    /// <summary>
    /// The caption names the object contract 12.3 requires, read from the
    /// simulation rather than restated here — so a purpose whose requirement moved
    /// cannot keep a caption that names the old one.
    /// </summary>
    [Theory]
    [InlineData(ZoneKind.Farm, "FARM · no bed")]
    [InlineData(ZoneKind.Kitchen, "KITCHEN · no stove")]
    [InlineData(ZoneKind.Larder, "LARDER · no larder tile")]
    [InlineData(ZoneKind.Quarters, "QUARTERS · no bunk")]
    [InlineData(ZoneKind.TrainingGround, "TRAIN · no post")]
    public void An_unfinished_room_names_the_object_the_contract_requires(
        ZoneKind purpose,
        string caption)
    {
        var room = new PrototypeRoomSnapshot(
            "x@0,0",
            purpose,
            [new GridPoint(0, 0)],
            [],
            "room_missing_feature",
            Complete: false);

        Assert.Equal(caption, RoomLabels.Caption(room));
        Assert.Equal(
            RoomLabels.FeatureName(PrototypeRooms.RequiredFeature(purpose)!.Value),
            caption[(caption.IndexOf("no ", StringComparison.Ordinal) + 3)..]);
    }

    [Fact]
    public void Every_purpose_has_a_name()
    {
        var names = Enum.GetValues<ZoneKind>().Select(RoomLabels.Name).ToArray();

        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomLabels.Name((ZoneKind)(-1)));
    }

    // ------------------------------------------------- the object with no room

    /// <summary>
    /// The state the shipped fixture starts in, and the one ADR 0013 quotes: four
    /// training posts stand in the north store, no gym is painted over them, and
    /// before this mark nothing said so.
    /// </summary>
    [Fact]
    public void The_authored_posts_of_the_shipped_map_start_with_no_room()
    {
        var view = View(1);
        var orphans = RoomObjects.Unroomed(view);

        Assert.Equal(
            [new GridPoint(10, 2), new GridPoint(11, 2), new GridPoint(10, 3), new GridPoint(11, 3)],
            orphans.Where(item => item.Kind == TileKind.Post).Select(item => item.Position));
        Assert.All(
            orphans.Where(item => item.Kind == TileKind.Post),
            item => Assert.Equal(ZoneKind.TrainingGround, item.Needs));

        // The beds and the stoves are inside the default farm and kitchen, so they
        // are not reported: a warning on everything is a warning on nothing.
        Assert.DoesNotContain(orphans, item => item.Kind is TileKind.Bed or TileKind.Kitchen);
    }

    /// <summary>
    /// Painting the gym clears the warning, and it clears in the same paused moment
    /// the paint is accepted — the room does not exist yet on that tick, so the
    /// membership question is asked of the projection.
    /// </summary>
    [Fact]
    public void Painting_the_zone_clears_the_warning_without_waiting_for_the_tick()
    {
        var painted = View(
            40,
            new ZonePaintCommand(40, ZoneKind.TrainingGround, [LeftPost, RightPost]));

        Assert.Empty(painted.State.Rooms.Where(room => room.Purpose == ZoneKind.TrainingGround));
        Assert.Equal(
            [new GridPoint(10, 3), new GridPoint(11, 3)],
            RoomObjects.Unroomed(painted)
                .Where(item => item.Kind == TileKind.Post)
                .Select(item => item.Position));
    }

    /// <summary>
    /// A bed outside the farm is reported too, so the mark is about objects and not
    /// about posts. Erasing part of the farm is the shortest way to make one.
    /// </summary>
    [Fact]
    public void A_bed_outside_the_farm_is_reported_as_well()
    {
        var view = View(
            3,
            new ZoneEraseCommand(1, ZoneKind.Farm, [new GridPoint(2, 1)]));

        var orphan = Assert.Single(
            RoomObjects.Unroomed(view).Where(item => item.Kind == TileKind.Bed));
        Assert.Equal(new GridPoint(2, 1), orphan.Position);
        Assert.Equal(ZoneKind.Farm, orphan.Needs);
    }

    /// <summary>
    /// The equivalence the warning rests on: an object is inside a zone exactly
    /// when a room of that purpose holds it in its contents. It is checked against
    /// a real session rather than argued, because if the two ever came apart the
    /// map would warn about a post a room is already using.
    /// </summary>
    [Fact]
    public void A_furniture_tile_is_in_a_zone_exactly_when_a_room_holds_it()
    {
        var world = new PrototypeWorld(PresentationFixtures.Log(RoomAccentTests.SweptRooms()));
        var checkedTiles = 0;
        for (var step = 0; step < 120; step++)
        {
            var view = MapProjection.Of(world.GetSnapshot());
            world.Step();
            if (view.HasPendingIntent)
            {
                continue;
            }

            var state = view.State;
            var held = state.Rooms
                .SelectMany(room => room.Contents.Select(item => (room.Purpose, item.Position)))
                .ToHashSet();

            foreach (var position in state.Beds.Select(bed => bed.Position)
                         .Concat(state.Stations.Select(station => station.Position)))
            {
                foreach (var purpose in Enum.GetValues<ZoneKind>())
                {
                    Assert.Equal(
                        view.IsInZone(purpose, position),
                        held.Contains((purpose, position)));
                    checkedTiles++;
                }
            }
        }

        Assert.True(checkedTiles > 1_000, $"only {checkedTiles} memberships were checked");
    }

    // ----------------------------------------------------------- the inspector

    /// <summary>
    /// The panel names the room the cell is in, with the same caption the map
    /// draws — so the words on the tile and the words in the panel can never
    /// disagree about whether the room is working.
    /// </summary>
    [Fact]
    public void The_panel_names_the_room_the_cell_belongs_to()
    {
        var view = View(1);

        Assert.Equal("room FARM [farm@1,1]\n", InspectorText.DescribeRooms(view, new GridPoint(2, 1)));
        Assert.Equal(string.Empty, InspectorText.DescribeRooms(view, new GridPoint(13, 10)));
    }

    /// <summary>
    /// A cell inside two rooms names both, in the order the snapshot publishes
    /// them. Overlapping zones are ordinary — a <c>Forbidden</c> paint over a gym
    /// is the common case — and the panel must not pick one and hide the other.
    /// </summary>
    [Fact]
    public void The_panel_names_every_room_a_cell_belongs_to()
    {
        var view = View(
            3,
            new ZonePaintCommand(1, ZoneKind.TrainingGround, [LeftPost]),
            new ZonePaintCommand(2, ZoneKind.Forbidden, [LeftPost]));

        Assert.Equal(
            "room TRAIN · forbidden [trainingGround@10,2] · FORBIDDEN [forbidden@10,2]\n",
            InspectorText.DescribeRooms(view, LeftPost));
    }

    /// <summary>
    /// And an object with no room over it gets the sentence its absent caption
    /// cannot give it. This is the panel half of the mark on the map.
    /// </summary>
    [Fact]
    public void The_panel_says_what_an_object_with_no_room_is_waiting_for()
    {
        var view = View(1);

        Assert.Equal(
            "no room: this post needs TrainingGround painted over it\n",
            InspectorText.DescribeRooms(view, LeftPost));
    }

    /// <summary>
    /// The whole panel carries the line, not only the helper — otherwise the
    /// section could be correct and never reach a player.
    /// </summary>
    [Fact]
    public void The_whole_panel_carries_the_room_line()
    {
        var view = View(1);

        Assert.Contains(
            "room FARM [farm@1,1]",
            InspectorText.Build(view, null, new GridPoint(2, 1)),
            StringComparison.Ordinal);
        Assert.Contains(
            "no room: this post needs TrainingGround painted over it",
            InspectorText.Build(view, null, LeftPost),
            StringComparison.Ordinal);
    }

    // ------------------------------------------- the pending-intent window

    /// <summary>
    /// Issue #130. Painting a zone over an object while paused: the room is
    /// created by the world only when the tick runs, and the folded membership
    /// has already cleared the orphan warning, so before the fix the panel said
    /// nothing about the zone at all. The panel has to name the player's intent.
    /// </summary>
    [Fact]
    public void Painting_a_zone_over_a_post_names_the_intent_while_paused()
    {
        var view = View(40, new ZonePaintCommand(40, ZoneKind.TrainingGround, [LeftPost]));

        // The room does not exist yet — the world creates it when the tick runs.
        Assert.Empty(view.State.Rooms.Where(room => room.Purpose == ZoneKind.TrainingGround));
        Assert.Contains(
            "marked as TrainingGround",
            InspectorText.DescribeRooms(view, LeftPost),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #130. Erasing a zone from under an object while paused: the
    /// canonical room still holds the cell and the folded membership has already
    /// released it, so before the fix the panel printed <c>room FARM</c> and
    /// <c>no room</c> about the same cell at once. It must answer with one
    /// statement, and the statement has to name the erase.
    /// </summary>
    [Fact]
    public void Erasing_a_zone_from_under_a_bed_names_the_intent_while_paused()
    {
        var cell = new GridPoint(2, 1);
        var view = View(40, new ZoneEraseCommand(40, ZoneKind.Farm, [cell]));

        // The two sources disagree on purpose: the room still holds the cell,
        // the fold has already released it.
        Assert.Contains(
            view.State.Rooms,
            room => room.Purpose == ZoneKind.Farm && room.Perimeter.Contains(cell));
        Assert.False(view.IsInZone(ZoneKind.Farm, cell));

        var text = InspectorText.DescribeRooms(view, cell);
        Assert.False(
            text.Contains("room FARM", StringComparison.Ordinal) &&
            text.Contains("no room", StringComparison.Ordinal),
            "The panel printed room membership and no-room about the same cell at once.");
        Assert.Contains("marked as erasing Farm", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The inverse of contract table 12.3, derived rather than restated: a second
    /// copy of the table on this side of the seam is a second table to keep in
    /// step.
    /// </summary>
    [Theory]
    [InlineData(TileKind.Bed, ZoneKind.Farm)]
    [InlineData(TileKind.Kitchen, ZoneKind.Kitchen)]
    [InlineData(TileKind.Larder, ZoneKind.Larder)]
    [InlineData(TileKind.Bunk, ZoneKind.Quarters)]
    [InlineData(TileKind.Post, ZoneKind.TrainingGround)]
    [InlineData(TileKind.Floor, null)]
    [InlineData(TileKind.Rock, null)]
    [InlineData(TileKind.Gate, null)]
    public void The_purpose_that_needs_an_object_is_the_inverse_of_the_contract_table(
        TileKind feature,
        ZoneKind? purpose)
    {
        Assert.Equal(purpose, RoomObjects.PurposeFor(feature));
    }
}
