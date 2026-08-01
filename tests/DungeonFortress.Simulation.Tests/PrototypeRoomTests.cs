using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Issue #52 / ADR 0013, variant C: a room becomes a thing the simulation knows
/// about instead of a shade of floor.
///
/// The pain the ADR names is one sentence: «Если игрок забыл покрасить зону, столб
/// стоит, работы <c>Drill</c> не появляются, <b>и игра об этом молчит</b>». Every
/// test here is about that sentence. The room entity is checked against the same
/// world that refuses the work, so a room that claims to be finished while
/// <c>Drill</c> produces nothing would fail rather than look right.
/// </summary>
public sealed class PrototypeRoomTests
{
    // Two of the four authored training posts, side by side in the north store.
    private static readonly GridPoint LeftPost = new(10, 2);
    private static readonly GridPoint RightPost = new(11, 2);

    // Plain floor in the east chamber, far from every post and from every other
    // default zone: a gym painted here is a gym with nothing in it.
    private static readonly GridPoint EmptyFloorA = new(25, 6);
    private static readonly GridPoint EmptyFloorB = new(26, 6);

    private static PrototypeSnapshot Run(int ticks, params PrototypeCommand[] commands)
    {
        var world = new PrototypeWorld(new PrototypeCommandLog(
            "custom",
            PrototypeTuning.DefaultSeed,
            commands));
        world.RunTicks(ticks);
        return world.GetSnapshot();
    }

    private static PrototypeRoomSnapshot Room(PrototypeSnapshot state, string id) =>
        Assert.Single(state.Rooms.Where(room => room.Id == id));

    // ------------------------------------------------------- the starting rooms

    /// <summary>
    /// The four default zones of contract 4.4 are four rooms, and each of them
    /// already holds the feature its purpose needs. This is the baseline every
    /// other test moves away from: on the dungeon as shipped nothing is missing,
    /// so a missing thing later is the player's doing and not the fixture's.
    /// </summary>
    [Fact]
    public void The_starting_dungeon_is_four_finished_rooms()
    {
        var state = Run(1);

        Assert.Equal(
            ["farm@1,1", "kitchen@9,6", "larder@13,6", "quarters@19,2"],
            state.Rooms.Select(room => room.Id));
        Assert.Equal(
            [ZoneKind.Farm, ZoneKind.Kitchen, ZoneKind.Larder, ZoneKind.Quarters],
            state.Rooms.Select(room => room.Purpose));
        Assert.All(state.Rooms, room =>
        {
            Assert.True(room.Complete, $"{room.Id} is not complete");
            Assert.Equal("room_ready", room.StatusCode);
        });
    }

    /// <summary>
    /// «Состав объектов» is the objects that are actually standing there, read off
    /// the live map. The farm covers all eight beds of contract 4.3, the kitchen
    /// both stations, the larder both larder tiles and the quarters all four bunks.
    /// </summary>
    [Fact]
    public void A_room_carries_the_objects_standing_inside_it()
    {
        var state = Run(1);

        Assert.Equal(
            [
                new GridPoint(2, 1), new GridPoint(5, 1),
                new GridPoint(2, 3), new GridPoint(5, 3),
                new GridPoint(2, 5), new GridPoint(5, 5),
                new GridPoint(2, 7), new GridPoint(5, 7),
            ],
            Room(state, "farm@1,1").Contents.Select(item => item.Position));
        Assert.All(
            Room(state, "farm@1,1").Contents,
            item => Assert.Equal(TileKind.Bed, item.Kind));

        Assert.Equal(
            [new GridPoint(10, 7), new GridPoint(11, 7)],
            Room(state, "kitchen@9,6").Contents.Select(item => item.Position));
        Assert.All(
            Room(state, "kitchen@9,6").Contents,
            item => Assert.Equal(TileKind.Kitchen, item.Kind));

        Assert.Equal(
            [new GridPoint(14, 7), new GridPoint(15, 7)],
            Room(state, "larder@13,6").Contents.Select(item => item.Position));

        Assert.Equal(
            [
                new GridPoint(20, 3), new GridPoint(21, 3),
                new GridPoint(21, 4), new GridPoint(22, 4),
            ],
            Room(state, "quarters@19,2").Contents.Select(item => item.Position));
        Assert.All(
            Room(state, "quarters@19,2").Contents,
            item => Assert.Equal(TileKind.Bunk, item.Kind));
    }

    /// <summary>
    /// Plain floor is not an object, so the room's contents are shorter than its
    /// perimeter. Without this a "contents" that simply repeated the tiles would
    /// pass every other assertion in this file.
    /// </summary>
    [Fact]
    public void The_floor_of_a_room_is_not_its_contents()
    {
        var farm = Room(Run(1), "farm@1,1");

        Assert.Equal(42, farm.Perimeter.Count);
        Assert.Equal(8, farm.Contents.Count);
    }

    // ------------------------------------------------------------- connectivity

    /// <summary>
    /// One patch is one room and two patches are two, which is the whole reason
    /// the entity exists: Issue #52 says the zone stopped being readable «как
    /// только зон стало больше одной», and a zone that is one object no matter how
    /// many places it is painted in has nothing to draw a border around.
    /// </summary>
    [Fact]
    public void Two_separated_patches_of_one_zone_are_two_rooms()
    {
        var state = Run(2, new ZonePaintCommand(
            1,
            ZoneKind.TrainingGround,
            [LeftPost, RightPost, EmptyFloorA, EmptyFloorB]));

        var gyms = state.Rooms.Where(room => room.Purpose == ZoneKind.TrainingGround).ToArray();

        Assert.Equal(["trainingGround@10,2", "trainingGround@25,6"], gyms.Select(room => room.Id));
        Assert.Equal([LeftPost, RightPost], gyms[0].Perimeter);
        Assert.Equal([EmptyFloorA, EmptyFloorB], gyms[1].Perimeter);
    }

    /// <summary>
    /// And touching tiles are one room, not two. The two halves of connectivity
    /// fail apart: a walk that never joins neighbours passes the test above and
    /// this one only fails if the join really happens.
    /// </summary>
    [Fact]
    public void Touching_tiles_of_one_zone_are_one_room()
    {
        var state = Run(2, new ZonePaintCommand(
            1,
            ZoneKind.TrainingGround,
            [LeftPost, RightPost]));

        var gym = Assert.Single(state.Rooms.Where(room => room.Purpose == ZoneKind.TrainingGround));
        Assert.Equal([LeftPost, RightPost], gym.Perimeter);
    }

    /// <summary>
    /// Diagonal is not touching. The simulation walks 4-connected everywhere else
    /// — movement, reachability, the dig ladder — and a room that joined across a
    /// corner would be a region no creature can cross without leaving it.
    /// </summary>
    [Fact]
    public void A_diagonal_step_does_not_join_two_rooms()
    {
        var state = Run(2, new ZonePaintCommand(
            1,
            ZoneKind.Watch,
            [new GridPoint(25, 6), new GridPoint(26, 7)]));

        Assert.Equal(2, state.Rooms.Count(room => room.Purpose == ZoneKind.Watch));
    }

    // ------------------------------------------------- the silence the ADR names

    /// <summary>
    /// The case ADR 0013 was written about: the zone is painted, no post is inside
    /// it, and <c>Drill</c> work does not appear. Before this entity the only
    /// evidence of that was the absence of jobs; now the room says it.
    /// </summary>
    [Fact]
    public void A_gym_with_no_post_in_it_is_incomplete_and_says_so()
    {
        var state = Run(2, new ZonePaintCommand(
            1,
            ZoneKind.TrainingGround,
            [EmptyFloorA, EmptyFloorB]));

        var gym = Room(state, "trainingGround@25,6");

        Assert.False(gym.Complete);
        Assert.Equal("room_missing_feature", gym.StatusCode);
        Assert.Empty(gym.Contents);

        // The reading is not a decoration on top of the world: the work really
        // does not exist while the room is unfinished.
        Assert.DoesNotContain(state.Jobs, job => job.Kind == JobKind.Drill);
    }

    /// <summary>
    /// The same zone painted over the posts is finished, and the work exists. The
    /// two tests are the two halves of «признак завершённости»: one of them is red
    /// whichever way the flag is nailed down.
    /// </summary>
    [Fact]
    public void A_gym_painted_over_the_posts_is_complete_and_the_work_appears()
    {
        var state = Run(
            40,
            new ZonePaintCommand(1, ZoneKind.TrainingGround, [LeftPost, RightPost]),
            new SetPriorityCommand(1, JobKind.Drill, 3));

        var gym = Room(state, "trainingGround@10,2");

        Assert.True(gym.Complete);
        Assert.Equal("room_ready", gym.StatusCode);
        Assert.Equal([LeftPost, RightPost], gym.Contents.Select(item => item.Position));
        Assert.All(gym.Contents, item => Assert.Equal(TileKind.Post, item.Kind));
        Assert.Contains(state.Jobs, job => job.Kind == JobKind.Drill);
    }

    /// <summary>
    /// A room that has everything and produces nothing anyway, because the
    /// priority of its work is 0. This is the second way the game used to stay
    /// silent, and it is deliberately a different code from the first: "you forgot
    /// to build" and "you switched it off" are different mistakes.
    ///
    /// The priority is raised and then dropped, rather than simply left at
    /// <see cref="PrototypeTuning.DefaultDrillPriority"/>, which is 0: a test that
    /// asserted the blocked reading over the default would be green whether or not
    /// the rung existed.
    /// </summary>
    [Fact]
    public void A_finished_room_whose_work_is_switched_off_says_that_instead()
    {
        var state = Run(
            4,
            new ZonePaintCommand(1, ZoneKind.TrainingGround, [LeftPost, RightPost]),
            new SetPriorityCommand(2, JobKind.Drill, 3),
            new SetPriorityCommand(3, JobKind.Drill, 0));

        var gym = Room(state, "trainingGround@10,2");

        Assert.True(gym.Complete);
        Assert.Equal("room_blocked_priority", gym.StatusCode);
        Assert.DoesNotContain(state.Jobs, job => job.Kind == JobKind.Drill);
    }

    /// <summary>
    /// The same reading on a gym the player painted and never switched on. It is
    /// the ordinary first mistake — <see cref="PrototypeTuning.DefaultDrillPriority"/>
    /// is 0 — and the room names it instead of looking finished and doing nothing.
    /// </summary>
    [Fact]
    public void A_gym_painted_before_the_priority_was_raised_reads_as_blocked()
    {
        var state = Run(3, new ZonePaintCommand(1, ZoneKind.TrainingGround, [LeftPost]));

        Assert.Equal("room_blocked_priority", Room(state, "trainingGround@10,2").StatusCode);
    }

    /// <summary>
    /// An unfinished room whose work is also switched off still reports the
    /// missing object. The order of the ladder is a decision: the player cannot
    /// act on a priority for work that could not exist anyway.
    /// </summary>
    [Fact]
    public void A_missing_object_outranks_a_switched_off_priority()
    {
        var state = Run(
            3,
            new ZonePaintCommand(1, ZoneKind.TrainingGround, [EmptyFloorA]),
            new SetPriorityCommand(2, JobKind.Drill, 0));

        Assert.Equal("room_missing_feature", Room(state, "trainingGround@25,6").StatusCode);
    }

    // --------------------------------------------------------------- forbidden

    /// <summary>
    /// A room nobody of the domain may set foot in cannot work, whatever else is
    /// true about it. It outranks every other rung for that reason.
    /// </summary>
    [Fact]
    public void A_room_covered_entirely_by_forbidden_reads_as_forbidden()
    {
        var state = Run(
            3,
            new ZonePaintCommand(1, ZoneKind.TrainingGround, [LeftPost, RightPost]),
            new ZonePaintCommand(2, ZoneKind.Forbidden, [LeftPost, RightPost]));

        var gym = Room(state, "trainingGround@10,2");

        Assert.True(gym.Complete);
        Assert.Equal("room_forbidden", gym.StatusCode);
    }

    /// <summary>
    /// Half a room forbidden is not a forbidden room: the crew can still use the
    /// part that is left. The two tests fail apart, which is what stops
    /// <c>All</c> from being quietly written as <c>Any</c>.
    /// </summary>
    [Fact]
    public void A_partly_forbidden_room_still_works()
    {
        var state = Run(
            3,
            new ZonePaintCommand(1, ZoneKind.TrainingGround, [LeftPost, RightPost]),
            new SetPriorityCommand(1, JobKind.Drill, 3),
            new ZonePaintCommand(2, ZoneKind.Forbidden, [LeftPost]));

        Assert.Equal("room_ready", Room(state, "trainingGround@10,2").StatusCode);
    }

    /// <summary>
    /// The forbidden paint itself is a room that is doing its job. Reporting it as
    /// unusable would be the entity arguing with itself.
    /// </summary>
    [Fact]
    public void The_forbidden_paint_is_a_room_that_is_working()
    {
        var state = Run(2, new ZonePaintCommand(1, ZoneKind.Forbidden, [EmptyFloorA, EmptyFloorB]));

        var refusal = Room(state, "forbidden@25,6");

        Assert.True(refusal.Complete);
        Assert.Equal("room_ready", refusal.StatusCode);
    }

    // -------------------------------------------------------- built, not authored

    /// <summary>
    /// A post the player built is content of the room around it exactly like an
    /// authored one, which is the rule of contract 4.3 seen from the room's side.
    /// The room flips from unfinished to finished on the tick the post appears,
    /// and nothing else about it changes.
    /// </summary>
    [Fact]
    public void A_post_the_player_built_finishes_the_room_around_it()
    {
        var log = PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(), "scenarios", "prototype1", "build-demo.commands.v2.json"));
        var world = new PrototypeWorld(log);

        // The zone is painted at tick 1000 and the post is still to be built.
        world.RunTicks(1_001);
        var before = Room(world.GetSnapshot(), "trainingGround@25,2");
        Assert.False(before.Complete);
        Assert.Equal("room_missing_feature", before.StatusCode);

        while (!world.IsComplete &&
               !world.GetSnapshot().Map.BuiltPostTiles.Contains(new GridPoint(25, 2)))
        {
            world.Step();
        }

        var after = Room(world.GetSnapshot(), "trainingGround@25,2");
        Assert.True(after.Complete);
        Assert.Equal("room_ready", after.StatusCode);
        Assert.Equal(
            [new PrototypeRoomObjectSnapshot(new GridPoint(25, 2), TileKind.Post)],
            after.Contents);
        Assert.Equal(before.Perimeter, after.Perimeter);
        Assert.Equal(before.Id, after.Id);
    }

    // ------------------------------------------------------------- the document

    /// <summary>
    /// The canonical document orders rooms by purpose and then by anchor, so it
    /// cannot depend on the order the patches were walked in. Painting the far
    /// patch first and the near one second has to produce the same bytes as the
    /// other way round.
    /// </summary>
    [Fact]
    public void The_canonical_document_does_not_depend_on_the_order_of_painting()
    {
        var farFirst = PrototypeScenario.Run(
            new PrototypeCommandLog(
                "custom",
                PrototypeTuning.DefaultSeed,
                [
                    new ZonePaintCommand(1, ZoneKind.Watch, [EmptyFloorA]),
                    new ZonePaintCommand(2, ZoneKind.Watch, [new GridPoint(1, 10)]),
                ]),
            5);
        var nearFirst = PrototypeScenario.Run(
            new PrototypeCommandLog(
                "custom",
                PrototypeTuning.DefaultSeed,
                [
                    new ZonePaintCommand(1, ZoneKind.Watch, [new GridPoint(1, 10)]),
                    new ZonePaintCommand(2, ZoneKind.Watch, [EmptyFloorA]),
                ]),
            5);

        Assert.Equal(farFirst.Checksum, nearFirst.Checksum);

        // Reading order is top to bottom and only then left to right, so the
        // chamber on row 6 anchors before the spine on row 10.
        Assert.Equal(
            ["watch@25,6", "watch@1,10"],
            farFirst.State.Rooms
                .Where(room => room.Purpose == ZoneKind.Watch)
                .Select(room => room.Id));
    }

    /// <summary>
    /// The canonical writer imposes the order rather than inheriting it, and this
    /// is what makes that claim checkable instead of decorative.
    ///
    /// The producer already hands the rooms over sorted, so the sort inside
    /// <see cref="PrototypeCanonical"/> would be a line no test could redden — the
    /// exact shape of "a value nobody reads looks load-bearing and is not". Here
    /// the same state is serialised twice with the lists turned inside out, and the
    /// bytes have to be identical.
    /// </summary>
    [Fact]
    public void The_canonical_document_is_the_same_whatever_order_the_rooms_arrive_in()
    {
        // Two patches of one purpose and a second purpose besides, so that both
        // sort keys and both inner orders have something to decide.
        var state = Run(
            3,
            new ZonePaintCommand(1, ZoneKind.Watch, [EmptyFloorA, EmptyFloorB]),
            new ZonePaintCommand(1, ZoneKind.Watch, [new GridPoint(1, 10), new GridPoint(2, 10)]),
            new ZonePaintCommand(1, ZoneKind.TrainingGround, [LeftPost, RightPost]));

        var reversed = state with
        {
            Rooms =
            [
                .. state.Rooms
                    .Reverse()
                    .Select(room => room with
                    {
                        Perimeter = [.. room.Perimeter.Reverse()],
                        Contents = [.. room.Contents.Reverse()],
                    }),
            ],
        };

        Assert.NotEqual(
            state.Rooms.Select(room => room.Id),
            reversed.Rooms.Select(room => room.Id));
        Assert.Equal(
            PrototypeCanonical.ComputeChecksum(PrototypeCanonical.Serialize(state)),
            PrototypeCanonical.ComputeChecksum(PrototypeCanonical.Serialize(reversed)));
    }

    /// <summary>
    /// The section really is in the canonical JSON with the fields the entity
    /// promises, rather than only in the in-memory record.
    /// </summary>
    [Fact]
    public void The_room_reaches_the_canonical_json()
    {
        var run = PrototypeScenario.Run(
            new PrototypeCommandLog(
                "custom",
                PrototypeTuning.DefaultSeed,
                [
                    new ZonePaintCommand(1, ZoneKind.TrainingGround, [LeftPost]),
                    new SetPriorityCommand(1, JobKind.Drill, 3),
                ]),
            2);

        using var document = JsonDocument.Parse(run.CanonicalJson);
        var gym = document.RootElement
            .GetProperty("rooms")
            .EnumerateArray()
            .Single(room => room.GetProperty("purpose").GetString() == "trainingGround");

        Assert.Equal("trainingGround@10,2", gym.GetProperty("id").GetString());
        Assert.True(gym.GetProperty("complete").GetBoolean());
        Assert.Equal("room_ready", gym.GetProperty("statusCode").GetString());
        Assert.Equal(1, gym.GetProperty("perimeter").GetArrayLength());
        Assert.Equal(
            "post",
            gym.GetProperty("contents")[0].GetProperty("kind").GetString());
    }

    // ----------------------------------------------------------- the two tables

    /// <summary>
    /// The feature column of contract table 12.3, as code. It is public because
    /// the presentation layer reads it rather than keeping a second copy of the
    /// table on its side of the seam.
    /// </summary>
    [Theory]
    [InlineData(ZoneKind.Farm, TileKind.Bed)]
    [InlineData(ZoneKind.Kitchen, TileKind.Kitchen)]
    [InlineData(ZoneKind.Larder, TileKind.Larder)]
    [InlineData(ZoneKind.Quarters, TileKind.Bunk)]
    [InlineData(ZoneKind.TrainingGround, TileKind.Post)]
    [InlineData(ZoneKind.Watch, null)]
    [InlineData(ZoneKind.Forbidden, null)]
    [InlineData(ZoneKind.MaterialStockpile, null)]
    public void The_required_feature_of_a_purpose_is_the_contract_table(
        ZoneKind purpose,
        TileKind? feature)
    {
        Assert.Equal(feature, PrototypeRooms.RequiredFeature(purpose));
    }

    /// <summary>
    /// The work column of the same table. <see cref="ZoneKind.Larder"/> and
    /// <see cref="ZoneKind.Forbidden"/> enable no single job, so neither can ever
    /// read as blocked by a priority.
    /// </summary>
    [Theory]
    [InlineData(ZoneKind.Farm, JobKind.Harvest)]
    [InlineData(ZoneKind.Kitchen, JobKind.Cook)]
    [InlineData(ZoneKind.Larder, null)]
    [InlineData(ZoneKind.Quarters, JobKind.Rest)]
    [InlineData(ZoneKind.TrainingGround, JobKind.Drill)]
    [InlineData(ZoneKind.Watch, JobKind.Watch)]
    [InlineData(ZoneKind.Forbidden, null)]
    [InlineData(ZoneKind.MaterialStockpile, JobKind.Haul)]
    public void The_work_a_purpose_enables_is_the_contract_table(
        ZoneKind purpose,
        JobKind? work)
    {
        Assert.Equal(work, PrototypeRooms.EnabledWork(purpose));
    }

    /// <summary>
    /// Every purpose produces a room, so no zone is silently outside the entity.
    /// </summary>
    [Fact]
    public void Every_zone_kind_can_be_a_room()
    {
        var state = Run(
            3,
            new ZonePaintCommand(1, ZoneKind.Farm, [new GridPoint(1, 10)]),
            new ZonePaintCommand(1, ZoneKind.Kitchen, [new GridPoint(2, 10)]),
            new ZonePaintCommand(1, ZoneKind.Larder, [new GridPoint(3, 10)]),
            new ZonePaintCommand(1, ZoneKind.Quarters, [new GridPoint(4, 10)]),
            new ZonePaintCommand(1, ZoneKind.TrainingGround, [new GridPoint(5, 10)]),
            new ZonePaintCommand(1, ZoneKind.Watch, [new GridPoint(6, 10)]),
            new ZonePaintCommand(1, ZoneKind.Forbidden, [new GridPoint(7, 10)]),
            new ZonePaintCommand(1, ZoneKind.MaterialStockpile, [new GridPoint(8, 10)]));

        Assert.Equal(
            Enum.GetValues<ZoneKind>(),
            state.Rooms.Select(room => room.Purpose).Distinct().Order());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DungeonFortress.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
