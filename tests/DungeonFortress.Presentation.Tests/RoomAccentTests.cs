using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// How a room reads, and whether the reading survives the tick that produces it.
///
/// This is the room's half of what <see cref="MapAccentTests"/> does for dig
/// marks, blueprints and stockpile cells, and it is the same comparison:
/// <see cref="MapAccents.Room"/> walks the world's own ladder over published
/// facts, so on every tick where nothing is waiting it must agree with the world's
/// <c>statusCode</c> exactly. It lives in its own file because rooms bring their
/// own geometry and captions with them, and those belong next to this rather than
/// next to the dig marks.
/// </summary>
public sealed class RoomAccentTests
{
    private static readonly GridPoint LeftPost = new(10, 2);
    private static readonly GridPoint RightPost = new(11, 2);
    private static readonly GridPoint EmptyFloorA = new(25, 6);
    private static readonly GridPoint EmptyFloorB = new(26, 6);
    private static readonly GridPoint EmptyFloorC = new(25, 8);

    internal static MapProjection View(int ticks, params PrototypeCommand[] commands) =>
        MapProjection.Of(PrototypeScenario.Run(PresentationFixtures.Log(commands), ticks).State);

    internal static PrototypeRoomSnapshot Room(MapProjection view, string id) =>
        Assert.Single(view.State.Rooms.Where(room => room.Id == id));

    /// <summary>
    /// The four readings of a room, each reached by the thing that causes it.
    /// </summary>
    [Fact]
    public void A_room_reads_as_the_four_states_the_decision_gives_it()
    {
        var ready = View(
            3,
            new ZonePaintCommand(1, ZoneKind.Watch, [EmptyFloorA]),
            new SetPriorityCommand(1, JobKind.Watch, 2));
        Assert.Equal(RoomAccent.Ready, MapAccents.Room(ready, Room(ready, "watch@25,6")));

        var unfinished = View(2, new ZonePaintCommand(1, ZoneKind.TrainingGround, [EmptyFloorA]));
        Assert.Equal(
            RoomAccent.Unfinished,
            MapAccents.Room(unfinished, Room(unfinished, "trainingGround@25,6")));

        var blocked = View(2, new ZonePaintCommand(1, ZoneKind.TrainingGround, [LeftPost]));
        Assert.Equal(
            RoomAccent.BlockedByPriority,
            MapAccents.Room(blocked, Room(blocked, "trainingGround@10,2")));

        var shut = View(
            3,
            new ZonePaintCommand(1, ZoneKind.Watch, [EmptyFloorA]),
            new ZonePaintCommand(2, ZoneKind.Forbidden, [EmptyFloorA]));
        Assert.Equal(RoomAccent.Unreachable, MapAccents.Room(shut, Room(shut, "watch@25,6")));
    }

    /// <summary>
    /// A priority the player changed in this same paused moment counts, exactly as
    /// it does for a dig mark. Switching <c>Drill</c> on and looking at the gym is
    /// one gesture, and the gym must not stay grey until time moves.
    /// </summary>
    [Theory]
    [InlineData(0, 3, RoomAccent.Ready)]
    [InlineData(3, 0, RoomAccent.BlockedByPriority)]
    public void A_room_reads_a_priority_the_same_moment_it_is_accepted(
        int from,
        int to,
        RoomAccent expected)
    {
        var commands = new PrototypeCommand[]
        {
            new ZonePaintCommand(1, ZoneKind.TrainingGround, [LeftPost, RightPost]),
            new SetPriorityCommand(1, JobKind.Drill, from),
            new SetPriorityCommand(40, JobKind.Drill, to),
        };
        var waiting = View(40, commands);
        var applied = View(41, commands);

        Assert.Equal(expected, MapAccents.Room(waiting, Room(waiting, "trainingGround@10,2")));
        Assert.Equal(
            MapAccents.RoomReadingOfStatus(Room(applied, "trainingGround@10,2").StatusCode),
            MapAccents.Room(waiting, Room(waiting, "trainingGround@10,2")));
    }

    /// <summary>
    /// And so does a <c>Forbidden</c> paint. The room is shut the moment the paint
    /// is accepted, not one tick later.
    /// </summary>
    [Fact]
    public void A_room_reads_a_forbidden_paint_the_same_moment_it_is_accepted()
    {
        var commands = new PrototypeCommand[]
        {
            new ZonePaintCommand(1, ZoneKind.Watch, [EmptyFloorA, EmptyFloorB]),
            new SetPriorityCommand(1, JobKind.Watch, 2),
            new ZonePaintCommand(40, ZoneKind.Forbidden, [EmptyFloorA, EmptyFloorB]),
        };
        var waiting = View(40, commands);
        var applied = View(41, commands);

        Assert.Equal(RoomAccent.Unreachable, MapAccents.Room(waiting, Room(waiting, "watch@25,6")));
        Assert.Equal(
            MapAccents.RoomReadingOfStatus(Room(applied, "watch@25,6").StatusCode),
            MapAccents.Room(waiting, Room(waiting, "watch@25,6")));
    }

    /// <summary>
    /// Half a room forbidden is not a forbidden room: the crew can still use the
    /// part that is left.
    ///
    /// This test exists because of a hole the mutation rule found rather than
    /// because it was foreseen. Turning <c>All</c> into <c>Any</c> on the forbidden
    /// rung left every check in this file green: the swept session only ever
    /// painted <c>Forbidden</c> over a whole room, where the two words agree. The
    /// partial case is now both here and inside the sweep.
    /// </summary>
    [Fact]
    public void A_partly_forbidden_room_still_reads_as_working()
    {
        var view = View(
            4,
            new ZonePaintCommand(1, ZoneKind.Watch, [EmptyFloorA, EmptyFloorB]),
            new SetPriorityCommand(1, JobKind.Watch, 2),
            new ZonePaintCommand(2, ZoneKind.Forbidden, [EmptyFloorA]));

        Assert.Equal(RoomAccent.Ready, MapAccents.Room(view, Room(view, "watch@25,6")));
    }

    /// <summary>
    /// The sweep. On every tick where nothing is waiting, the ladder walked in the
    /// presentation layer has to give the same answer as the world's own status
    /// code — which is what makes repeating the ladder safe rather than a second
    /// source of truth.
    /// </summary>
    [Fact]
    public void The_predicted_room_readings_match_the_world_at_every_tick_of_a_session()
    {
        var world = new PrototypeWorld(PresentationFixtures.Log(SweptRooms()));
        var compared = 0;
        var seen = new HashSet<RoomAccent>();
        for (var step = 0; step < 120; step++)
        {
            var view = MapProjection.Of(world.GetSnapshot());
            world.Step();
            if (view.HasPendingIntent)
            {
                // On the tick a command lands the world has not applied it yet, so
                // its word is the old one and disagreeing with it is the point.
                continue;
            }

            foreach (var room in view.State.Rooms)
            {
                var predicted = MapAccents.Room(view, room);
                Assert.Equal(MapAccents.RoomReadingOfStatus(room.StatusCode), predicted);
                seen.Add(predicted);
                compared++;
            }
        }

        Assert.True(compared > 200, $"only {compared} room readings were compared");
        Assert.Equal(
            [
                RoomAccent.Ready,
                RoomAccent.Unfinished,
                RoomAccent.BlockedByPriority,
                RoomAccent.Unreachable,
            ],
            seen.Order().ToArray());
    }

    /// <summary>
    /// A session that reaches every rung: a watch post switched off and then on, a
    /// gym painted with nothing in it, and a room forbidden by halves — one cell
    /// first, then both, then released. Without it a sweep that only ever saw
    /// "ready" would prove nothing at all, and without the half-forbidden stretch
    /// it could not tell <c>All</c> from <c>Any</c>.
    /// </summary>
    internal static PrototypeCommand[] SweptRooms() =>
    [
        new ZonePaintCommand(5, ZoneKind.Watch, [EmptyFloorA, EmptyFloorB]),
        new ZonePaintCommand(15, ZoneKind.TrainingGround, [EmptyFloorC]),
        new SetPriorityCommand(30, JobKind.Watch, 2),
        new ZonePaintCommand(40, ZoneKind.Forbidden, [EmptyFloorA]),
        new ZonePaintCommand(50, ZoneKind.Forbidden, [EmptyFloorB]),
        new ZoneEraseCommand(80, ZoneKind.Forbidden, [EmptyFloorA, EmptyFloorB]),
    ];
}
