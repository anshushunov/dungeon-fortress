using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The chain as a policy: what a body does at each point of the tick a blow was
/// recorded on, and which way the two ends of that blow are thrown.
///
/// <para>
/// Every input here already existed. <see cref="BodyActionPhase"/> is what
/// <see cref="BlowReadout"/> writes off the canonical journal, and
/// <c>tickAlpha</c> is the share of the tick the view has drawn. Nothing below
/// asks the simulation for a field it does not have, which is the second
/// criterion of the Issue stated as a property of the signatures.
/// </para>
/// </summary>
public sealed class StrikeChainTests
{
    /// <summary>
    /// A body no blow touches stands still through the whole tick — every phase
    /// <see cref="StrikePhase.Stance"/>, every part at rest, no lean and no
    /// throw. This is what makes the chain invisible on the ticks that have no
    /// fighting in them, which is nearly all of them.
    /// </summary>
    [Fact]
    public void A_body_no_blow_touches_never_leaves_its_stance()
    {
        foreach (var alpha in Alphas())
        {
            Assert.Equal(
                StrikePhase.Stance,
                StrikeChain.PhaseAt(BodyActionPhase.None, alpha));
            Assert.Equal(0.0, StrikeChain.LeanDegrees(BodyActionPhase.None, alpha));
            Assert.Equal(
                0.0,
                StrikeChain.RecoilOffsetRef(
                    StrikeChain.RoleOf(BodyActionPhase.None),
                    alpha));
            foreach (var part in Moving())
            {
                Assert.Equal(
                    PartPose.Rest,
                    StrikeChain.PoseOf(BodyActionPhase.None, part, alpha));
            }
        }
    }

    /// <summary>
    /// The chain is played whole, in order, on the tick the journal recorded the
    /// blow on: wind-up, strike, follow-through, return. This is the Issue's
    /// «стойка → замах → удар → проводка → возврат», and the check is that all
    /// five names are reachable and that none of them comes back after a later
    /// one has been.
    /// </summary>
    [Fact]
    public void The_striker_plays_the_whole_chain_in_order()
    {
        var seen = new List<StrikePhase>();
        foreach (var alpha in Alphas())
        {
            var phase = StrikeChain.PhaseAt(BodyActionPhase.Windup, alpha);
            if (seen.Count == 0 || seen[^1] != phase)
            {
                seen.Add(phase);
            }
        }

        Assert.Equal(
            new[]
            {
                StrikePhase.Windup,
                StrikePhase.Strike,
                StrikePhase.FollowThrough,
                StrikePhase.Recover,
            },
            seen);

        // The struck body's chain is the other half of the same blow: it stands
        // until the blow arrives and only then recoils.
        Assert.Equal(
            StrikePhase.Stance,
            StrikeChain.PhaseAt(BodyActionPhase.Flinch, 0.1));
        Assert.NotEqual(
            StrikePhase.Stance,
            StrikeChain.PhaseAt(BodyActionPhase.Flinch, 0.5));
    }

    /// <summary>
    /// Contact is where hit-stop already holds the picture, and that is stated as
    /// an identity rather than as two numbers that happen to agree. Two constants
    /// is how the strike pose and the moment the bodies stop sliding end up a
    /// frame apart.
    /// </summary>
    [Fact]
    public void Contact_is_the_moment_hit_stop_already_holds_the_picture()
    {
        Assert.Equal(BlowEffects.HitStopShare, StrikeChain.ContactShare);
        Assert.Equal(0.0, BlowEffects.HitStopAlpha(StrikeChain.ContactShare, true), 10);

        Assert.False(StrikeChain.HasLanded(StrikeChain.ContactShare - 0.01));
        Assert.True(StrikeChain.HasLanded(StrikeChain.ContactShare));
        // A paused frame and a captured screenshot are drawn at alpha 1, so the
        // marks of a blow have to survive there.
        Assert.True(StrikeChain.HasLanded(1.0));

        Assert.False(StrikeChain.ShowsContact(StrikeChain.ContactShare - 0.01));
        Assert.True(StrikeChain.ShowsContact(StrikeChain.ContactShare));
        Assert.False(StrikeChain.ShowsContact(StrikeChain.FollowThroughShare));
    }

    /// <summary>
    /// <b>Both ends of a blow move, and they move apart.</b>
    ///
    /// <para>
    /// This is the check the polarity mutant of this Issue attacks from the policy
    /// side, and the third criterion of the Issue is the same claim on the adapter
    /// side. The striker lunges in before contact and is thrown back after it; the
    /// body it struck is never pulled towards the thing that hit it. Turn either
    /// of them round and a goblin is sucked into the spear it just planted.
    /// </para>
    ///
    /// <para>
    /// Stated as signs and as an ordering, not as literals: the numbers are
    /// tuning, the directions are the reading.
    /// </para>
    /// </summary>
    [Fact]
    public void The_two_ends_of_a_blow_are_thrown_apart_and_never_together()
    {
        var after = new[] { 0.55, 0.65, 0.75 };

        // The striker: forward into the blow, back out of it.
        Assert.True(
            StrikeChain.RecoilOffsetRef(StrikeRole.Attacker, StrikeChain.ContactShare) > 0.0,
            "A striker that is not moving forwards at the moment of contact is not striking.");
        foreach (var alpha in after)
        {
            Assert.True(
                StrikeChain.RecoilOffsetRef(StrikeRole.Attacker, alpha) < 0.0,
                $"At {alpha} of the tick the striker is still being carried towards " +
                "what it hit, so the blow has no recoil at all.");
        }

        // The struck body: away, and never towards.
        foreach (var alpha in Alphas())
        {
            Assert.True(
                StrikeChain.RecoilOffsetRef(StrikeRole.Target, alpha) >= 0.0,
                $"At {alpha} of the tick the struck body is being pulled towards the blow.");
        }

        Assert.True(
            after.All(alpha =>
                StrikeChain.RecoilOffsetRef(StrikeRole.Target, alpha) >
                StrikeChain.RecoilOffsetRef(StrikeRole.Attacker, alpha)),
            "After contact the two bodies have to be moving apart, not one after the other.");

        // Both really move: a recoil of zero would satisfy every "not towards"
        // above and show nothing at all.
        Assert.True(
            after.Select(alpha =>
                Math.Abs(StrikeChain.RecoilOffsetRef(StrikeRole.Attacker, alpha))).Max() > 1.0,
            "A striker thrown less than a reference pixel is not thrown at all.");
        Assert.True(
            after.Select(alpha =>
                StrikeChain.RecoilOffsetRef(StrikeRole.Target, alpha)).Max() > 1.0,
            "A target moved less than a reference pixel only changed pose.");

        // A bystander is not thrown anywhere.
        Assert.Equal(0.0, StrikeChain.RecoilOffsetRef(StrikeRole.Bystander, 0.5));
    }

    /// <summary>
    /// The roles are read off the pose the canonical journal gave the body, and
    /// nowhere else. This is the seam between "what the simulation recorded" and
    /// "how it is drawn", and it is one line wide on purpose.
    /// </summary>
    [Fact]
    public void The_role_of_a_body_is_the_phase_the_journal_gave_it()
    {
        Assert.Equal(StrikeRole.Attacker, StrikeChain.RoleOf(BodyActionPhase.Windup));
        Assert.Equal(StrikeRole.Target, StrikeChain.RoleOf(BodyActionPhase.Flinch));
        Assert.Equal(StrikeRole.Bystander, StrikeChain.RoleOf(BodyActionPhase.None));
    }

    /// <summary>
    /// The chain begins and ends at the stance, and moves continuously in
    /// between. A chain that started away from rest would snap on the first frame
    /// of every blow; one that ended away from it would leave a body posed for a
    /// blow that is over — and a paused frame is drawn at alpha 1, which is
    /// exactly there.
    /// </summary>
    [Theory]
    [InlineData(BodyActionPhase.Windup)]
    [InlineData(BodyActionPhase.Flinch)]
    public void The_chain_leaves_the_stance_and_comes_back_to_it(BodyActionPhase phase)
    {
        foreach (var part in Moving())
        {
            Assert.Equal(PartPose.Rest, StrikeChain.PoseOf(phase, part, 0.0));
            Assert.Equal(PartPose.Rest, StrikeChain.PoseOf(phase, part, 1.0));
        }

        Assert.Equal(0.0, StrikeChain.LeanDegrees(phase, 0.0));
        Assert.Equal(0.0, StrikeChain.LeanDegrees(phase, 1.0));

        // Something actually happens in between, and it happens smoothly: no step
        // between two adjacent hundredths is larger than the whole travel of the
        // chain, which is what "interpolated" means as a checkable claim.
        var arm = Alphas()
            .Select(alpha => StrikeChain.PoseOf(phase, "arm_near", alpha).Degrees)
            .ToArray();
        var travel = arm.Max() - arm.Min();
        Assert.True(travel > 5.0, $"The near arm moves only {travel} degrees in all.");
        var lean = Alphas()
            .Select(alpha => Math.Abs(StrikeChain.LeanDegrees(phase, alpha)))
            .Max();
        Assert.True(lean > 5.0, $"The body leans only {lean} degrees in all.");
        for (var index = 1; index < arm.Length; index++)
        {
            Assert.True(
                Math.Abs(arm[index] - arm[index - 1]) < travel,
                "The chain jumps between two adjacent frames instead of running.");
        }
    }

    /// <summary>
    /// The angles and offsets of this rig are chosen against a measurement of the
    /// seams they open, and the measurement found one direction of the strike
    /// shoulder far worse than the other. That is a fact about the art rather than
    /// a taste, so the shape it forces on the chain is pinned here: the wind-up
    /// raises the near arm and the strike brings it down, not the other way round.
    /// Numbers and method: <c>evidence/244-rig-gaps.json</c>.
    /// </summary>
    [Fact]
    public void The_wind_up_raises_the_strike_arm_and_the_blow_brings_it_down()
    {
        var windup = StrikeChain.PoseOf(BodyActionPhase.Windup, "arm_near", 0.28).Degrees;
        var strike = StrikeChain.PoseOf(
            BodyActionPhase.Windup,
            "arm_near",
            StrikeChain.ContactShare).Degrees;

        Assert.True(windup < 0.0, "The wind-up has to raise the near arm.");
        Assert.True(strike > 0.0, "The strike has to bring it down again.");
        Assert.True(strike - windup > 30.0, "A swing under 30 degrees does not read.");
    }

    private static IEnumerable<double> Alphas() =>
        Enumerable.Range(0, 101).Select(step => step / 100.0);

    private static IEnumerable<string> Moving() =>
        BodyRig.LayerOrder.Where(part =>
            part is not (BodyRig.RootPart or BodyRig.WeaponPart));
}
