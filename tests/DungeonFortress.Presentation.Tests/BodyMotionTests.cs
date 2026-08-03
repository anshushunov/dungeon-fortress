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
}
