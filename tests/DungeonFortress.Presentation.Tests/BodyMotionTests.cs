using DungeonFortress.Simulation;
using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The pure half of Issue #221: what a body's facing, walk and blow do to the
/// picture, decided where the engine is not needed to ask (ADR 0011).
/// </summary>
public sealed class BodyMotionTests
{
    /// <summary>
    /// A sideways step turns the body, either way.
    /// </summary>
    [Theory]
    [InlineData(BodyFacing.Right, -1.0, BodyFacing.Left)]
    [InlineData(BodyFacing.Left, 1.0, BodyFacing.Right)]
    [InlineData(BodyFacing.Left, -1.0, BodyFacing.Left)]
    [InlineData(BodyFacing.Right, 1.0, BodyFacing.Right)]
    public void A_sideways_step_turns_the_body(
        BodyFacing current,
        double dx,
        BodyFacing expected) =>
        Assert.Equal(expected, BodyMotion.Turn(current, dx));

    /// <summary>
    /// A step with no sideways part keeps the facing the body already had, and
    /// that memory is the point: a creature walking down a corridor after turning
    /// left would otherwise flip back to the right on its first vertical step,
    /// and flip again on every tick it is blocked.
    /// </summary>
    [Theory]
    [InlineData(BodyFacing.Left)]
    [InlineData(BodyFacing.Right)]
    public void A_step_with_no_sideways_part_keeps_the_facing(BodyFacing current) =>
        Assert.Equal(current, BodyMotion.Turn(current, 0.0));

    /// <summary>
    /// The flip is what the facing costs the drawing: nothing at all while the
    /// body faces the way the pack was authored, and a mirrored width when it does
    /// not. Both halves matter — a flip that never returned 1 would mirror a body
    /// that never turned.
    /// </summary>
    [Fact]
    public void The_flip_mirrors_exactly_the_facing_the_pack_was_not_authored_in()
    {
        Assert.Equal(1.0, BodyMotion.FlipScale(BodyMotion.AuthoredFacing));

        var mirrored = BodyMotion.AuthoredFacing == BodyFacing.Right
            ? BodyFacing.Left
            : BodyFacing.Right;
        Assert.Equal(-1.0, BodyMotion.FlipScale(mirrored));

        // A mirror is a mirror: it changes which way the body points and nothing
        // about how large it is.
        Assert.Equal(1.0, Math.Abs(BodyMotion.FlipScale(mirrored)));
    }

    /// <summary>
    /// A body with no history is drawn exactly as it was drawn before any of this
    /// existed. It is the answer every body gets on the first frame of a fixture,
    /// and a resting facing that differed from the authored one would silently
    /// mirror the whole crew at load time.
    /// </summary>
    [Fact]
    public void A_body_that_has_never_moved_is_drawn_the_way_the_pack_is_authored()
    {
        Assert.Equal(BodyMotion.AuthoredFacing, BodyMotion.RestingFacing);
        Assert.Equal(1.0, BodyMotion.FlipScale(BodyMotion.RestingFacing));
    }

    /// <summary>
    /// The claim the whole walk phase rests on, measured on a shipped party rather
    /// than argued from the movement code: every step a body actually takes moves
    /// its path by exactly one cell.
    ///
    /// <para>
    /// It is asked of the simulation and not of a list of four offsets copied into
    /// this test, because the copy would keep agreeing with itself after
    /// <c>PrototypeMap</c> grew a diagonal. A diagonal step would leave
    /// <c>X + Y</c> unchanged or move it by two, and the gait would silently stop
    /// being a gait.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_single_step_advances_the_path_by_exactly_one_cell()
    {
        var world = new PrototypeWorld(PresentationFixtures.LogOf("prepared"));
        var previous = world.GetSnapshot().Creatures
            .ToDictionary(creature => creature.Id, creature => creature.Position);

        var steps = 0;
        while (world.CurrentTick < 400 && !world.IsComplete)
        {
            world.RunTicks(1);
            foreach (var creature in world.GetSnapshot().Creatures)
            {
                var from = previous[creature.Id];
                var to = creature.Position;
                previous[creature.Id] = to;
                if (from == to)
                {
                    // Standing still moves nothing, which the bob relies on just
                    // as much as it relies on the step below.
                    Assert.Equal(
                        BodyMotion.PathCells(from, from, 1.0),
                        BodyMotion.PathCells(from, to, 1.0),
                        12);
                    continue;
                }

                steps++;
                Assert.Equal(
                    1.0,
                    Math.Abs(
                        BodyMotion.PathCells(from, to, 1.0) -
                        BodyMotion.PathCells(from, from, 1.0)),
                    12);
            }
        }

        // And the walk really happened: a party in which nothing moved would make
        // the check above true and empty.
        Assert.True(steps > 500, $"Only {steps} steps were taken in 400 ticks.");
    }

    /// <summary>
    /// And it advances continuously across the step rather than jumping at the
    /// tick boundary: alpha 0 is the cell the body came from, alpha 1 the cell it
    /// is on, and half way is half a cell of path.
    /// </summary>
    [Fact]
    public void The_path_runs_continuously_from_one_cell_to_the_next()
    {
        var from = new GridPoint(4, 9);
        var to = new GridPoint(5, 9);

        Assert.Equal(BodyMotion.PathCells(from, from, 1.0), BodyMotion.PathCells(from, to, 0.0), 12);
        Assert.Equal(
            BodyMotion.PathCells(from, to, 0.0) + 0.5,
            BodyMotion.PathCells(from, to, 0.5),
            12);
        Assert.Equal(
            BodyMotion.PathCells(from, to, 0.0) + 1.0,
            BodyMotion.PathCells(from, to, 1.0),
            12);
    }

    /// <summary>
    /// A body that is not walking does not bob — exactly, at every point of the
    /// cycle. This is the half of the Issue's second criterion that a phase alone
    /// cannot give: a phase frozen mid-cycle would leave a resting creature
    /// hanging above its own feet.
    /// </summary>
    [Fact]
    public void A_standing_body_does_not_bob_at_any_phase()
    {
        for (var path = -4.0; path <= 4.0; path += 0.125)
        {
            Assert.Equal(0.0, BodyMotion.BobOffsetRef(path, walking: false));
        }
    }

    /// <summary>
    /// A walking body does, and the phase is the path: two bodies that have walked
    /// different distances are at different heights, and one that has walked a
    /// whole cycle further is at the same one.
    /// </summary>
    [Fact]
    public void A_walking_body_bobs_and_its_phase_is_the_path_it_has_walked()
    {
        var heights = new HashSet<double>();
        for (var path = 0.0; path < BodyMotion.GaitPeriodCells; path += 0.125)
        {
            heights.Add(BodyMotion.BobOffsetRef(path, walking: true));
            Assert.Equal(
                BodyMotion.BobOffsetRef(path, walking: true),
                BodyMotion.BobOffsetRef(path + BodyMotion.GaitPeriodCells, walking: true),
                12);
        }

        Assert.True(
            heights.Count > 8,
            $"The bob takes {heights.Count} distinct heights over a full cycle, " +
            "which is not a curve.");
    }

    /// <summary>
    /// Two cells of path is one cycle, so a body standing on a cell it has just
    /// stepped onto is at one end of the bob and at the other end after the next
    /// step. That alternation is the whole of why a captured frame — always drawn
    /// at alpha 1, on a cell centre — can show a walk at all.
    /// </summary>
    [Fact]
    public void Two_cells_of_path_is_one_cycle_so_neighbouring_steps_alternate()
    {
        Assert.Equal(2.0, BodyMotion.GaitPeriodCells);

        for (var steps = 0; steps < 6; steps++)
        {
            var expected = steps % 2 == 0 ? -BodyMotion.BobHeightRef : 0.0;
            Assert.Equal(expected, BodyMotion.BobOffsetRef(steps, walking: true), 12);
        }
    }

    /// <summary>
    /// And the body never goes under the line it stands on: the ground is not the
    /// drawing's to move.
    /// </summary>
    [Fact]
    public void The_bob_never_pushes_a_body_below_its_own_feet()
    {
        for (var path = -4.0; path <= 4.0; path += 0.0625)
        {
            var offset = BodyMotion.BobOffsetRef(path, walking: true);
            Assert.InRange(offset, -BodyMotion.BobHeightRef, 0.0);
        }
    }
}
