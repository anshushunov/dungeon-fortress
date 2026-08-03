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
    /// </summary>
    [Fact]
    public void The_facing_is_turned_by_the_step_and_by_the_blow()
    {
        var turn = AdapterSource.Body("TurnBodies");

        Assert.Equal(2, AdapterSource.CallsTo(turn, "SidewaysStep").Count);
        Assert.Contains("_creatureMotionOrigin", turn, StringComparison.Ordinal);
        Assert.Contains("_raiderMotionOrigin", turn, StringComparison.Ordinal);
        Assert.Contains($"_blows.{nameof(BlowReading.Blows)}", turn, StringComparison.Ordinal);
        Assert.Equal(3, AdapterSource.CallsTo(turn, "TurnBody").Count);

        // The step is the difference between the cell the body came from and the
        // cell it is on. Measured the other way round the whole crew would face
        // backwards, and no value check inside this assembly could see it.
        var step = AdapterSource.Body("SidewaysStep");
        Assert.Contains("position.X - origin.X", step, StringComparison.Ordinal);

        // And the decision itself is the policy's, not this file's.
        Assert.Contains(
            $"{nameof(BodyMotion)}.{nameof(BodyMotion.Turn)}(",
            AdapterSource.Body("TurnBody"),
            StringComparison.Ordinal);
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
}
