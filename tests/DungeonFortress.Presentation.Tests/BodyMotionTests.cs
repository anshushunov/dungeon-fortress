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
    /// Issue #259, the whole of it in values: both bodies of a blow end up facing
    /// each other, whichever side the struck body stands on. The striker's half
    /// was already true; the struck body's is what the owner's duel playtest found
    /// missing.
    ///
    /// <para>
    /// The two are compared with each other as well as with the direction they are
    /// expected in, because "facing each other" is the property the picture is read
    /// by: two bodies both turned right are a body attacking a back whatever the
    /// two values are called.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(4.0, BodyFacing.Right, BodyFacing.Left)]
    [InlineData(-4.0, BodyFacing.Left, BodyFacing.Right)]
    [InlineData(1.0, BodyFacing.Right, BodyFacing.Left)]
    [InlineData(-1.0, BodyFacing.Left, BodyFacing.Right)]
    public void Both_bodies_of_a_blow_are_turned_towards_each_other(
        double dx,
        BodyFacing attacker,
        BodyFacing target)
    {
        // Both start turned the way the pack is authored, which is what a body
        // that has not stepped sideways yet is drawn in — and, on the duel scene,
        // exactly the state the defect was seen in.
        var exchange = BodyMotion.TurnToExchange(
            BodyMotion.RestingFacing,
            BodyMotion.RestingFacing,
            dx);

        Assert.Equal(attacker, exchange.Attacker);
        Assert.Equal(target, exchange.Target);
        Assert.NotEqual(exchange.Attacker, exchange.Target);
    }

    /// <summary>
    /// And the struck body is turned by where the striker is, not by where it was
    /// already looking: one that happens to face the striker already is left facing
    /// it, instead of being turned to the striker's own side.
    ///
    /// <para>
    /// This is the case that tells the two mutants apart. A build that turns the
    /// struck body not at all passes this check and fails the one above; a build
    /// that turns it <em>away</em> — with the striker's difference instead of the
    /// mirror of it — fails this one, because here the inherited facing and the
    /// wrong answer are different values. Rule 32 asks for a check per claim, and
    /// on PR #235 a guard was content with a body turned the wrong way.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(4.0, BodyFacing.Left)]
    [InlineData(-4.0, BodyFacing.Right)]
    public void A_struck_body_already_facing_the_striker_is_left_facing_it(
        double dx,
        BodyFacing already)
    {
        var exchange = BodyMotion.TurnToExchange(BodyMotion.RestingFacing, already, dx);

        Assert.Equal(already, exchange.Target);
        Assert.NotEqual(exchange.Attacker, exchange.Target);
    }

    /// <summary>
    /// A blow struck along a column — striker and target in the same column, one
    /// above the other — turns both bodies the way the pack is authored, and the
    /// answer is the pair's rather than each body's own memory.
    ///
    /// <para>
    /// Neither facing points at the other body here, so there is nothing to be
    /// "towards"; what is left to decide is whether the two are drawn from the same
    /// side. Inheriting would leave one arrangement out of four with the two
    /// standing back to back, which is the picture this Issue exists to remove, so
    /// the rule is named (<see cref="BodyMotion.VerticalExchangeFacing"/>) instead
    /// of falling through to <see cref="BodyMotion.Turn"/>'s memory.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(BodyFacing.Left, BodyFacing.Left)]
    [InlineData(BodyFacing.Left, BodyFacing.Right)]
    [InlineData(BodyFacing.Right, BodyFacing.Left)]
    [InlineData(BodyFacing.Right, BodyFacing.Right)]
    public void A_blow_struck_along_a_column_turns_both_bodies_the_authored_way(
        BodyFacing attacker,
        BodyFacing target)
    {
        var exchange = BodyMotion.TurnToExchange(attacker, target, 0.0);

        Assert.Equal(BodyMotion.AuthoredFacing, exchange.Attacker);
        Assert.Equal(BodyMotion.AuthoredFacing, exchange.Target);
        Assert.Equal(exchange.Attacker, exchange.Target);
    }

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
    ///
    /// <para>
    /// Both body lists are walked. Raiders are drawn by the same
    /// <see cref="BodyMotion.BobOffsetRef"/> as the crew, so a premise measured on
    /// the crew alone would leave half of the bodies it is claimed about defended
    /// by an argument rather than by a measurement.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_single_step_advances_the_path_by_exactly_one_cell()
    {
        var world = new PrototypeWorld(PresentationFixtures.LogOf("prepared"));
        var previous = new Dictionary<BodyRef, GridPoint>();

        var crewSteps = 0;
        var raiderSteps = 0;
        while (world.CurrentTick < 400 && !world.IsComplete)
        {
            world.RunTicks(1);
            crewSteps += MeasureSteps(world, previous).Crew;
        }

        // The raiders walk too, and they are drawn by the same bob. Their window is
        // the first two waves rather than the opening minutes, because before a
        // wave arrives there is no raider on the map to measure — which is why the
        // first round of this Issue measured only half of the bodies it claims
        // about, and why independent review asked for the other half.
        world.RunTicks(900);
        previous.Clear();
        MeasureSteps(world, previous);
        while (world.CurrentTick < 1700 && !world.IsComplete)
        {
            world.RunTicks(1);
            var measured = MeasureSteps(world, previous);
            crewSteps += measured.Crew;
            raiderSteps += measured.Raiders;
        }

        // And the walk really happened, on both sides: a party in which nothing
        // moved would make the checks above true and empty. The raiders' bound is
        // the low one because the shipped journal gives them little walking to do
        // — they arrive next to a defence line that is already standing there and
        // are put down within about thirty ticks — and 16 steps is what this party
        // actually offers by tick 1700. A low bound measured is still a bound; an
        // invented one is not.
        Assert.True(crewSteps > 500, $"Only {crewSteps} crew steps were taken.");
        Assert.True(raiderSteps > 10, $"Only {raiderSteps} raider steps were taken.");
    }

    /// <summary>
    /// Every step both body lists took since the last reading, checked against
    /// <see cref="BodyMotion.PathCells"/> and counted.
    /// </summary>
    private static (int Crew, int Raiders) MeasureSteps(
        PrototypeWorld world,
        Dictionary<BodyRef, GridPoint> previous)
    {
        var state = world.GetSnapshot();
        var crew = 0;
        var raiders = 0;
        foreach (var (body, to) in
                 state.Creatures
                     .Select(creature =>
                         (new BodyRef(BodyKind.Creature, creature.Id), creature.Position))
                     .Concat(state.Raiders
                         .Select(raider =>
                             (new BodyRef(BodyKind.Raider, raider.Id), raider.Position))))
        {
            if (!previous.TryGetValue(body, out var from))
            {
                previous[body] = to;
                continue;
            }

            previous[body] = to;
            if (from == to)
            {
                // Standing still moves nothing, which the bob relies on just as
                // much as it relies on the step below.
                Assert.Equal(
                    BodyMotion.PathCells(from, from, 1.0),
                    BodyMotion.PathCells(from, to, 1.0),
                    12);
                continue;
            }

            if (body.Kind == BodyKind.Creature)
            {
                crew++;
            }
            else
            {
                raiders++;
            }

            Assert.Equal(
                1.0,
                Math.Abs(
                    BodyMotion.PathCells(from, to, 1.0) -
                    BodyMotion.PathCells(from, from, 1.0)),
                12);
        }

        return (crew, raiders);
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

    /// <summary>
    /// A body tips into the side it walks to, by the same amount either way, and a
    /// body with no sideways step does not tip at all.
    /// </summary>
    [Fact]
    public void A_body_leans_into_the_side_it_walks_to()
    {
        var right = BodyMotion.LeanRadians(1.0);
        var left = BodyMotion.LeanRadians(-1.0);

        Assert.True(right > 0.0, "A step to the right does not tip the head right.");
        Assert.Equal(-right, left, 12);
        Assert.Equal(0.0, BodyMotion.LeanRadians(0.0));

        // The angle is the declared one and not a radian written out by hand: at
        // six degrees a body drawn 61.82 px tall moves its head 6.46 px sideways.
        Assert.Equal(BodyMotion.LeanDegrees * Math.PI / 180.0, right, 12);
    }

    /// <summary>
    /// A blow stretches the body that strikes and squashes the body that is
    /// struck, and leaves every other body exactly as it was.
    /// </summary>
    [Fact]
    public void A_blow_stretches_the_striker_and_squashes_the_struck()
    {
        Assert.True(BodyMotion.BlowHeightScale(BodyActionPhase.Windup, 0.0) > 1.0);
        Assert.True(BodyMotion.BlowHeightScale(BodyActionPhase.Flinch, 0.0) < 1.0);
        Assert.Equal(1.0, BodyMotion.BlowHeightScale(BodyActionPhase.None, 0.0));
        Assert.Equal(1.0, BodyMotion.BlowWidthScale(BodyActionPhase.None, 1.0));
    }

    /// <summary>
    /// And it is squash and <em>stretch</em> rather than a resize: the width is the
    /// height's reciprocal, so the body keeps its area at every phase and every
    /// moment of the tick.
    /// </summary>
    [Theory]
    [InlineData(BodyActionPhase.None)]
    [InlineData(BodyActionPhase.Windup)]
    [InlineData(BodyActionPhase.Flinch)]
    public void A_blow_conserves_the_area_of_the_body_it_scales(BodyActionPhase phase)
    {
        for (var alpha = 0.0; alpha <= 1.0; alpha += 0.05)
        {
            Assert.Equal(
                1.0,
                BodyMotion.BlowHeightScale(phase, alpha) *
                BodyMotion.BlowWidthScale(phase, alpha),
                12);
        }
    }

    /// <summary>
    /// The scale fades over the tick towards a floor and never to nothing, for the
    /// reason every curve of <see cref="BlowEffects"/> has a floor: a paused frame
    /// and a captured screenshot are both drawn at alpha 1, and an effect that
    /// rested there would be missing from every frame anybody can stop on.
    /// </summary>
    [Theory]
    [InlineData(BodyActionPhase.Windup)]
    [InlineData(BodyActionPhase.Flinch)]
    public void The_scale_of_a_blow_fades_to_a_floor_and_not_to_nothing(BodyActionPhase phase)
    {
        var peak = Math.Abs(BodyMotion.BlowHeightScale(phase, 0.0) - 1.0);
        var floor = Math.Abs(BodyMotion.BlowHeightScale(phase, 1.0) - 1.0);

        Assert.True(peak > floor, "The scale does not fade over the tick.");
        Assert.True(floor > 0.0, "The scale rests at exactly nothing on a paused frame.");

        // Beyond the tick it holds the floor rather than turning over.
        Assert.Equal(
            BodyMotion.BlowHeightScale(phase, 1.0),
            BodyMotion.BlowHeightScale(phase, 4.0),
            12);
    }
}
