using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The structural half of Issue #221, by the method
/// <see cref="SideOutlineAdapterTests"/>, <see cref="WorldDrawPassGuardTests"/>
/// and <see cref="BlowAdapterTests"/> already use: the adapter is read as text and
/// the engine is not started (ADR 0011).
///
/// <para>
/// A rule about how a body turns is a rule about nothing until something draws by
/// it, and that half cannot be a value comparison — the decision is a policy in
/// this assembly, and whether the adapter asks it is a fact about
/// <c>Main.cs</c>. So these checks are about which routine calls which, in what
/// order and with what argument, and never about a literal repeated inside a test.
/// </para>
/// </summary>
public sealed class BodyMotionAdapterTests
{
    /// <summary>
    /// The facing of a body is decided in one place, from the two things the view
    /// already knew: the cell it stepped out of and the blow it landed. A step is
    /// read from the motion buffer, and a blow from the reading built for this
    /// tick.
    ///
    /// <para>
    /// <b>Both directions are checked, not just their existence.</b> The first
    /// round of this Issue only asked that a turn happens, and independent review
    /// showed what that misses: subtracting the two cells the other way round — in
    /// the blow branch, or one floor up where the difference is handed to the
    /// policy — turns every body away from what it is doing, compiles, draws a
    /// whole party and leaves the whole suite green. Hence the literal difference
    /// is pinned here, exactly as it already was for the step and the lean.
    /// </para>
    ///
    /// <para>
    /// Since Issue #259 the blow no longer has to be argued from text alone: which
    /// way each of the two bodies of an exchange ends up turned is a value question
    /// asked of <see cref="BodyMotion.TurnToExchange"/> in
    /// <see cref="BodyMotionTests"/>, where a target turned away from its striker
    /// fails a comparison of values. What is left for this file is the part no
    /// value can hold — that the adapter asks that question at all, about these two
    /// bodies, with the difference the right way round.
    /// </para>
    /// </summary>
    [Fact]
    public void The_facing_is_turned_by_the_step_and_by_the_blow()
    {
        var turn = AdapterSource.Body("TurnBodies");

        Assert.Equal(2, AdapterSource.CallsTo(turn, "SidewaysStep").Count);
        Assert.Contains("_creatureMotionOrigin", turn, StringComparison.Ordinal);
        Assert.Contains("_raiderMotionOrigin", turn, StringComparison.Ordinal);
        Assert.Contains($"_blows.{nameof(BlowReading.Blows)}", turn, StringComparison.Ordinal);

        var turns = AdapterSource.CallsTo(turn, "TurnBody");
        Assert.Equal(2, turns.Count);

        // The step is the difference between the cell the body came from and the
        // cell it is on. Measured the other way round the whole crew would face
        // backwards, and no value check inside this assembly could see it.
        var step = AdapterSource.Body("SidewaysStep");
        Assert.Contains("position.X - origin.X", step, StringComparison.Ordinal);

        // A blow turns the two bodies it names, and the difference it is turned by
        // is the target's cell minus the striker's. The other way round is the
        // mutation review found nothing catching: a striker turning its back on its
        // target. Which way each of the two ends up facing is a value question and
        // is asked of the policy in BodyMotionTests; what belongs here is that the
        // adapter hands over these two bodies and this difference.
        var exchange = Assert.Single(AdapterSource.CallsTo(turn, "TurnExchange"));
        Assert.Equal(3, exchange.Arguments.Count);
        Assert.Equal("attacker", exchange.Arguments[0]);
        Assert.Equal($"blow.{nameof(Blow.Target)}", exchange.Arguments[1]);
        Assert.Equal("to.X - from.X", exchange.Arguments[2]);

        // And both decisions are the policy's, not this file's — with the
        // difference handed over untouched. A minus sign here would turn the whole
        // party backwards while every check above stayed green.
        var decision = Assert.Single(AdapterSource.CallsTo(
            AdapterSource.Body("TurnBody"),
            $"{nameof(BodyMotion)}.{nameof(BodyMotion.Turn)}"));
        Assert.Equal(2, decision.Arguments.Count);
        Assert.Contains("BodyFacingOf(", decision.Arguments[0], StringComparison.Ordinal);
        Assert.Equal("dx", decision.Arguments[1]);

        // The blow's decision is one call for the pair, so the two answers cannot
        // be given by two different rules, and the striker is the first of them.
        var pair = Assert.Single(AdapterSource.CallsTo(
            AdapterSource.Body("TurnExchange"),
            $"{nameof(BodyMotion)}.{nameof(BodyMotion.TurnToExchange)}"));
        Assert.Equal(3, pair.Arguments.Count);
        Assert.Contains("BodyFacingOf(attacker", pair.Arguments[0], StringComparison.Ordinal);
        Assert.Contains("BodyFacingOf(target", pair.Arguments[1], StringComparison.Ordinal);
        Assert.Equal("dx", pair.Arguments[2]);
    }

    /// <summary>
    /// It is turned once per tick, where the snapshot is taken, and after the
    /// blows of that tick are read — a body that strikes turns towards what it
    /// struck, and that answer does not exist before the reading does.
    /// </summary>
    [Fact]
    public void The_facing_is_turned_once_a_tick_and_after_the_blows_are_read()
    {
        var refresh = AdapterSource.Body("RefreshState");
        Assert.Single(AdapterSource.CallsTo(refresh, "TurnBodies"));
        Assert.True(
            refresh.IndexOf(
                $"{nameof(BlowReadout)}.{nameof(BlowReadout.Of)}(",
                StringComparison.Ordinal) <
            refresh.IndexOf("TurnBodies(", StringComparison.Ordinal),
            "RefreshState turns the bodies before it reads the blows, so a body " +
            "that struck this tick is turned by last tick's reading.");
    }

    /// <summary>
    /// Every drawing of a body goes through the body's own frame, and the flip in
    /// that frame is this body's facing rather than a constant.
    ///
    /// <para>
    /// This is the check the first mutant of Issue #221 runs into: a
    /// <c>PushBodyPose</c> that passed a fixed scale would compile, draw a whole
    /// party and put every body back to facing one way.
    /// </para>
    /// </summary>
    [Fact]
    public void The_frame_a_body_is_drawn_in_takes_the_flip_from_that_body_s_facing()
    {
        var push = AdapterSource.Body("PushBodyPose");

        var flip = Assert.Single(AdapterSource.CallsTo(
            push,
            $"{nameof(BodyMotion)}.{nameof(BodyMotion.FlipScale)}"));
        Assert.Single(flip.Arguments);
        Assert.Contains("BodyFacingOf(", flip.Arguments[0], StringComparison.Ordinal);

        // The frame stands on the body's feet, which is where CameraView already
        // stands the sprite: a body may turn and lean, the ground may not move.
        Assert.Contains(
            $"{nameof(CameraView)}.{nameof(CameraView.GoblinFootLine)}(",
            push,
            StringComparison.Ordinal);

        // And no facing is ever named in the adapter: the resting one included,
        // because a literal is invisible to every check in the repository.
        Assert.DoesNotContain(
            $"{nameof(BodyFacing)}.{nameof(BodyFacing.Left)}",
            AdapterSource.Masked,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"{nameof(BodyFacing)}.{nameof(BodyFacing.Right)}",
            AdapterSource.Masked,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{nameof(BodyMotion)}.{nameof(BodyMotion.RestingFacing)}",
            AdapterSource.Body("BodyFacingOf"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The bob is a function of the path the body has walked and of the body's own
    /// two cells — never of a clock, and never of a constant.
    ///
    /// <para>
    /// This is the check the second mutant of Issue #221 runs into: a phase
    /// replaced by a fixed number still compiles, still draws every body, and puts
    /// the whole crew back on one line.
    /// </para>
    /// </summary>
    [Fact]
    public void The_bob_takes_its_phase_from_the_path_the_body_has_walked()
    {
        var push = AdapterSource.Body("PushBodyPose");

        var bob = Assert.Single(AdapterSource.CallsTo(
            push,
            $"{nameof(BodyMotion)}.{nameof(BodyMotion.BobOffsetRef)}"));
        Assert.Equal(2, bob.Arguments.Count);
        Assert.Contains(
            $"{nameof(BodyMotion)}.{nameof(BodyMotion.PathCells)}(",
            bob.Arguments[0],
            StringComparison.Ordinal);

        // "Walking" is the body's own two cells and nothing else: not a mode, not
        // a speed, not a timer.
        Assert.Contains("from != to", bob.Arguments[1], StringComparison.Ordinal);

        var path = Assert.Single(AdapterSource.CallsTo(
            push,
            $"{nameof(BodyMotion)}.{nameof(BodyMotion.PathCells)}"));
        Assert.Equal(3, path.Arguments.Count);
        Assert.Contains("alpha", path.Arguments[2], StringComparison.Ordinal);

        // And that share of the tick is the one hit-stop already decides, so the
        // gait holds still with the rest of the picture when a blow lands.
        Assert.Contains("alpha = MotionAlpha()", push, StringComparison.Ordinal);

        // And the two cells are the interpolation buffer's, which is where the
        // cell a body came from is already kept.
        var step = AdapterSource.Body("BodyStep");
        Assert.Contains("_creatureMotionOrigin", step, StringComparison.Ordinal);
        Assert.Contains("_raiderMotionOrigin", step, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bob moves the drawing and never the point the world is sorted and
    /// measured by.
    ///
    /// <para>
    /// <c>RenderCenter</c> is what the depth pass orders bodies by and what
    /// <c>--frame-pacing</c> converts back into a cell to count a body drawn ahead
    /// of the simulation. A vertical offset added there would change depth order
    /// and could report a body in a cell the simulation has not reached — which is
    /// the hard constraint of this Issue, not a matter of taste.
    /// </para>
    /// </summary>
    [Fact]
    public void The_bob_is_in_the_drawing_and_not_in_the_render_centre()
    {
        Assert.DoesNotContain(
            nameof(BodyMotion.BobOffsetRef),
            AdapterSource.Body("RenderCenter"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            nameof(BodyMotion.BobOffsetRef),
            AdapterSource.Body("MeasureFramePacingFrame"),
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOf($"{nameof(BodyMotion)}.{nameof(BodyMotion.BobOffsetRef)}("));
    }

    /// <summary>
    /// The lean and the squash go into the same frame, from the same policy: the
    /// tip is the step's sideways part, and the two scales are the phase the blow
    /// reading gives this body.
    /// </summary>
    [Fact]
    public void The_lean_is_the_step_and_the_squash_is_the_blow()
    {
        var push = AdapterSource.Body("PushBodyPose");

        var lean = Assert.Single(AdapterSource.CallsTo(
            push,
            $"{nameof(BodyMotion)}.{nameof(BodyMotion.LeanRadians)}"));
        Assert.Single(lean.Arguments);
        Assert.Contains("to.X - from.X", lean.Arguments[0], StringComparison.Ordinal);

        foreach (var member in new[]
                 {
                     nameof(BodyMotion.BlowWidthScale),
                     nameof(BodyMotion.BlowHeightScale),
                 })
        {
            var scale = Assert.Single(AdapterSource.CallsTo(
                push,
                $"{nameof(BodyMotion)}.{member}"));
            Assert.Equal(2, scale.Arguments.Count);
            Assert.Contains("phase", scale.Arguments[0], StringComparison.Ordinal);
            Assert.Contains("alpha", scale.Arguments[1], StringComparison.Ordinal);
        }

        // The phase is the reading's, the same one the pose itself is chosen by,
        // so a stretched body and a wind-up pose can never disagree.
        Assert.Contains("BodyPhase(", push, StringComparison.Ordinal);

        // No number of this policy is written next to the draw call: a literal is
        // invisible to every check in the repository — the argument the alpha check
        // of WorldDrawPassGuardTests is built on.
        foreach (var member in new[]
                 {
                     nameof(BodyMotion.BobHeightRef),
                     nameof(BodyMotion.LeanDegrees),
                     nameof(BodyMotion.StretchPeak),
                     nameof(BodyMotion.SquashPeak),
                     nameof(BodyMotion.GaitPeriodCells),
                 })
        {
            Assert.DoesNotContain(
                $"{nameof(BodyMotion)}.{member}",
                AdapterSource.Masked,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The sprite, the side outline and the blow flash are all drawn in that same
    /// frame, and every routine that opens it closes it.
    ///
    /// <para>
    /// The three are made in two different passes of <see cref="WorldDrawOrder"/>,
    /// so a flip applied to one and not the others is a body wearing somebody
    /// else's silhouette — the defect
    /// <c>BlowAdapterTests.The_flash_is_the_silhouette_of_the_pose_the_body_is_drawn_in</c>
    /// already holds the flash to, one property further on. A frame left open
    /// would mirror everything drawn after it, which on this canvas is the whole
    /// rest of the map.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("DrawSidedBody")]
    [InlineData("DrawBlowFlash")]
    public void Every_drawing_of_a_body_opens_the_body_frame_and_closes_it(string routine)
    {
        var body = AdapterSource.Body(routine);

        Assert.Single(AdapterSource.CallsTo(body, "PushBodyPose"));
        Assert.Single(AdapterSource.CallsTo(body, "ClearBodyPose"));
        Assert.True(
            body.IndexOf("PushBodyPose(", StringComparison.Ordinal) <
            body.IndexOf("ClearBodyPose(", StringComparison.Ordinal),
            $"{routine} closes the body frame before it opens it.");

        // Everything it draws is inside the frame, so the rectangle is the local
        // one rather than a rectangle around a world point.
        Assert.DoesNotContain(
            $"{nameof(CameraView)}.{nameof(CameraView.GoblinDrawRect)}(",
            body,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And the local rectangle is the same rectangle as before — asked about a
    /// body whose feet are at the origin, not measured again by hand.
    /// </summary>
    [Fact]
    public void The_local_rectangle_is_still_the_cameras_rectangle()
    {
        var rect = AdapterSource.Body("BodyLocalRect");
        Assert.Contains(
            $"{nameof(CameraView)}.{nameof(CameraView.GoblinDrawRect)}(",
            rect,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{nameof(CameraView)}.{nameof(CameraView.GoblinFootLine)}(",
            rect,
            StringComparison.Ordinal);

        foreach (var routine in new[] { "DrawGoblin", "DrawGoblinOutline" })
        {
            Assert.Contains(
                "BodyLocalRect(",
                AdapterSource.Body(routine),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A captured frame is drawn from a fixture, and a fixture used to be run in
    /// one go — so nothing in it had a previous cell, and every body in every
    /// screenshot would be standing still by construction. The last tick runs on
    /// its own, which is what makes a walk visible in a frame at all.
    ///
    /// <para>
    /// It is a change to <em>when</em> ticks are run and not to how many: the
    /// evidence that canonical state does not notice is the checksum the capture
    /// prints, and it is in <c>evidence/221-after.json</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void Loading_a_fixture_runs_its_last_tick_on_its_own()
    {
        var load = AdapterSource.Body("LoadFixture");

        Assert.Equal(2, AdapterSource.CallsTo(load, "RefreshState").Count);
        Assert.Single(AdapterSource.CallsTo(load, "RememberMotionOrigin"));
        Assert.True(
            load.IndexOf("RememberMotionOrigin(", StringComparison.Ordinal) <
            load.LastIndexOf("RefreshState(", StringComparison.Ordinal),
            "LoadFixture remembers the previous cells after the last tick has " +
            "already overwritten them.");
    }

    private static int CountOf(string needle)
    {
        var count = 0;
        for (var index = AdapterSource.Masked.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = AdapterSource.Masked.IndexOf(needle, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
