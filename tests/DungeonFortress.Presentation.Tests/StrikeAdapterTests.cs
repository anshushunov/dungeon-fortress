using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The adapter's half of Issue #244, by the method
/// <see cref="BodyMotionAdapterTests"/>, <see cref="BlowAdapterTests"/> and
/// <see cref="WorldDrawPassGuardTests"/> already use: <c>Main.cs</c> is read as
/// text and the engine is never started (ADR 0011).
///
/// <para>
/// It is a structural guard on purpose, and the third criterion of the Issue says
/// so in as many words — "проверяется структурным гардом по адаптеру, а не
/// сравнением литералов внутри теста". The reason is measured rather than
/// preferred: on PR #235 a value test was satisfied by a body turning the wrong
/// way, because the decision lives in this assembly and whether the adapter asks
/// it is a fact about a file no test project references.
/// </para>
/// </summary>
public sealed class StrikeAdapterTests
{
    /// <summary>
    /// <b>Every part of the rig is posed by the chain, and the chain is asked
    /// with the phase the canonical journal gave the body.</b>
    ///
    /// <para>
    /// This is the check the absence mutant of this Issue runs into: drop the call
    /// and every part sits at its rest angle, which compiles, draws a whole fight
    /// and looks exactly like the flat body this Issue replaced.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_part_of_the_rig_is_posed_by_the_chain()
    {
        var layout = AdapterSource.Body("RigLayout");

        var pose = Assert.Single(AdapterSource.CallsTo(
            layout,
            $"{nameof(StrikeChain)}.{nameof(StrikeChain.PoseOf)}"));
        Assert.Equal(3, pose.Arguments.Count);
        Assert.Equal("phase", pose.Arguments[0]);
        Assert.Equal("name", pose.Arguments[1]);
        Assert.Equal("beat", pose.Arguments[2]);

        // The phase is the reading's, so a part cannot be posed for a blow the
        // journal never recorded.
        Assert.Contains("BodyPhase(", layout, StringComparison.Ordinal);
        Assert.Contains(
            $"_blows.{nameof(BlowReading.PhaseOf)}",
            AdapterSource.Body("BodyPhase"),
            StringComparison.Ordinal);

        // And "beat" is the raw share of the tick rather than the hit-stopped one:
        // hit-stop maps the whole wind-up onto zero, so a chain driven by
        // MotionAlpha would stand still through it and then jump.
        Assert.Contains("beat = TickAlpha()", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("MotionAlpha", layout, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The body is assembled in the rig's declared order, and every part turns
    /// around its own joint inside its parent's frame.</b>
    ///
    /// <para>
    /// The order mutant of this Issue is caught by
    /// <see cref="BodyRigTests.The_layer_order_is_the_rig_s_own_back_to_front_order"/>;
    /// what this adds is that the adapter walks that list rather than a list of
    /// its own, and that a child is composed onto its parent — a frame built
    /// without the parent's rotation is a limb that ignores the body it is
    /// attached to.
    /// </para>
    /// </summary>
    [Fact]
    public void The_body_is_assembled_in_the_rig_s_own_order_and_hangs_off_its_joints()
    {
        var layout = AdapterSource.Body("RigLayout");

        Assert.Contains(
            $"{nameof(BodyRig)}.{nameof(BodyRig.LayerOrder)}",
            layout,
            StringComparison.Ordinal);
        // No order of the adapter's own: a second list is a second truth, and the
        // rig's is the one the art was cut at.
        foreach (var part in BodyRig.LayerOrder)
        {
            Assert.DoesNotContain($"\"{part}\"", AdapterSource.Masked, StringComparison.Ordinal);
        }

        // The pivot is the part's joint, converted once, and the rotation is about
        // it: `x -> R(x - pivot) + pivot + slide`.
        Assert.Contains("RigLocalPoint(part.Joint", layout, StringComparison.Ordinal);
        Assert.Contains(
            "pivot + slide - pivot.Rotated(turn)",
            layout,
            StringComparison.Ordinal);

        // A child is its parent's frame times its own, in that order.
        Assert.Contains("FrameOf(parent) * own", layout, StringComparison.Ordinal);

        // And the whole assembly sits inside the body frame the rest of the
        // drawing already uses, so the flip, the lean and the squash reach it.
        Assert.Contains("_bodyFrame", AdapterSource.Body("DrawRigBody"), StringComparison.Ordinal);
        Assert.Contains("_bodyFrame", AdapterSource.Body("DrawRigFlash"), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The recoil of a blow runs from the striker towards the body it struck,
    /// and this is the check the polarity mutant of this Issue runs into.</b>
    ///
    /// <para>
    /// The policy answers a signed distance <em>along that line</em>
    /// (<c>StrikeChainTests.The_two_ends_of_a_blow_are_thrown_apart_and_never_together</c>
    /// holds the signs). The adapter owns the line itself, and subtracting the two
    /// cells the other way round turns both signs over at once: the striker is
    /// pulled into what it speared and the target is driven onto the spear. It
    /// compiles, it animates, and no value check inside this assembly can see it —
    /// which is exactly the shape of the mutation that survived on PR #235.
    /// </para>
    /// </summary>
    [Fact]
    public void The_recoil_of_a_blow_runs_from_the_striker_towards_the_struck()
    {
        var axis = AdapterSource.Body("BlowAxis");

        // Target minus striker, on both coordinates. Either one reversed is a body
        // thrown the wrong way.
        Assert.Contains("to.X - from.X", axis, StringComparison.Ordinal);
        Assert.Contains("to.Y - from.Y", axis, StringComparison.Ordinal);
        Assert.DoesNotContain("from.X - to.X", axis, StringComparison.Ordinal);
        Assert.DoesNotContain("from.Y - to.Y", axis, StringComparison.Ordinal);

        // The line is between the cells the snapshot states, not between the
        // points the recoil has already moved: a direction fed by its own output
        // drifts further every frame.
        Assert.Contains("BodyPosition(attacker)", axis, StringComparison.Ordinal);
        Assert.Contains("BodyPosition(blow.Target)", axis, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderCenter", axis, StringComparison.Ordinal);

        // The distance along it is the policy's and is multiplied by that line —
        // not by a constant, and not by a direction of the adapter's own.
        var recoil = AdapterSource.Body("BodyRecoil");
        var throwBack = Assert.Single(AdapterSource.CallsTo(
            recoil,
            $"{nameof(StrikeChain)}.{nameof(StrikeChain.RecoilOffsetRef)}"));
        Assert.Equal(2, throwBack.Arguments.Count);
        Assert.Contains(
            $"{nameof(StrikeChain)}.{nameof(StrikeChain.RoleOf)}(phase)",
            throwBack.Arguments[0],
            StringComparison.Ordinal);
        Assert.Contains("direction * ScaleWorld", recoil, StringComparison.Ordinal);

        // Both ends of a blow reach it, because both of them ask BlowAxis about
        // themselves and the loop accepts either end.
        Assert.Contains("attacker != body && blow.Target != body", axis, StringComparison.Ordinal);

        // And it is used once, where the body's own frame is built, so a body
        // cannot be thrown in the drawing of one mark and not of another.
        var push = AdapterSource.Body("PushBodyPose");
        Assert.Single(AdapterSource.CallsTo(push, "BodyRecoil"));
        Assert.Single(AdapterSource.CallsTo(push, "BlowAxis"));
    }

    /// <summary>
    /// The throw moves the drawing and never the point the world is sorted and
    /// measured by — the same rule the bob is already held to, and the same
    /// reason: <c>--frame-pacing</c> turns a render centre back into a cell and
    /// counts a body drawn in a cell the simulation has not reached.
    /// </summary>
    [Fact]
    public void The_throw_is_in_the_drawing_and_not_in_the_render_centre()
    {
        foreach (var routine in new[] { "RenderCenter", "MeasureFramePacingFrame" })
        {
            var body = AdapterSource.Body(routine);
            Assert.DoesNotContain("BodyRecoil", body, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(StrikeChain), body, StringComparison.Ordinal);
        }

        // Twice in the file: the declaration and the one call PushBodyPose makes.
        // A third occurrence is a second place a body can be moved from, and the
        // only two places that must never move it are the two above.
        Assert.Equal(2, Occurrences("BodyRecoil("));
        Assert.Equal(
            1,
            Occurrences($"{nameof(StrikeChain)}.{nameof(StrikeChain.RecoilOffsetRef)}("));
    }

    /// <summary>
    /// The lean into a blow is a rotation of the whole body frame and never a
    /// rotation of the torso part against the legs.
    ///
    /// <para>
    /// It is a seam decision and not a style one: turning the trunk against the
    /// lower body opens the widest gap of any joint in this rig, measured before
    /// the chain was written. Turning the frame moves nothing relative to anything
    /// and therefore costs no seam at any angle.
    /// </para>
    /// </summary>
    [Fact]
    public void The_lean_into_a_blow_turns_the_whole_body_and_not_the_torso()
    {
        var lean = AdapterSource.Body("StrikeLean");
        var call = Assert.Single(AdapterSource.CallsTo(
            lean,
            $"{nameof(StrikeChain)}.{nameof(StrikeChain.LeanDegrees)}"));
        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal("phase", call.Arguments[0]);
        Assert.Equal("tickAlpha", call.Arguments[1]);

        // Signed by the line the blow travels along, so a body leans towards what
        // it is fighting whichever side that is on.
        Assert.Contains("direction.X", lean, StringComparison.Ordinal);

        // It goes into the frame's rotation, beside the walking lean.
        var push = AdapterSource.Body("PushBodyPose");
        Assert.Contains("StrikeLean(phase, axis, beat)", push, StringComparison.Ordinal);

        // And the root part of the rig is never given an angle of its own.
        Assert.DoesNotContain(
            $"{nameof(BodyRig)}.{nameof(BodyRig.RootPart)}, ",
            AdapterSource.Body("RigLayout"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The contact effect is one spark, drawn only inside the window the chain
    /// says contact is in, and every value it is drawn with comes from the policy
    /// rather than from a literal beside the draw call.
    /// </summary>
    [Fact]
    public void The_contact_spark_is_drawn_from_the_policy_and_only_at_contact()
    {
        var sparks = AdapterSource.Body("DrawContactSparks");

        Assert.Contains(
            $"{nameof(StrikeChain)}.{nameof(StrikeChain.ShowsContact)}(",
            sparks,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{nameof(StrikeChain)}.{nameof(StrikeChain.ContactAlpha)}(",
            sparks,
            StringComparison.Ordinal);

        foreach (var member in new[]
                 {
                     nameof(BlowEffects.SparkAt),
                     nameof(BlowEffects.SparkAlpha),
                     nameof(BlowEffects.SparkColor),
                     nameof(BlowEffects.SparkRayRef),
                     nameof(BlowEffects.SparkRayRadians),
                     nameof(BlowEffects.SparkRays),
                     nameof(BlowEffects.SparkCoreRef),
                     nameof(BlowEffects.SparkWidthRef),
                 })
        {
            Assert.Contains(
                $"{nameof(BlowEffects)}.{member}",
                sparks,
                StringComparison.Ordinal);
        }

        // A blow whose striker the journal does not name has no spark, for the
        // reason it has no streak: one end of the line would be a guess.
        Assert.Contains(
            "blow.Attacker is not { } attacker",
            sparks,
            StringComparison.Ordinal);

        // And the flash waits for the blow to arrive. It used to burn for the
        // whole tick, which at the duel's zoom lights a body up before the spear
        // reaches it.
        Assert.Contains(
            $"{nameof(StrikeChain)}.{nameof(StrikeChain.HasLanded)}(",
            AdapterSource.Body("DrawBlowFlash"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The duel scene draws two bodies and stops on a tick the canonical journal
    /// recorded a blow on. It runs ordinary ticks to get there and writes nothing
    /// back, which is what keeps ADR 0011 intact while a scene picks its own
    /// moment.
    /// </summary>
    [Fact]
    public void The_duel_scene_stops_on_a_recorded_blow_and_shows_its_two_bodies()
    {
        var duel = AdapterSource.Body("ApplyDemoDuel");
        Assert.Contains("FindDuelTick()", duel, StringComparison.Ordinal);
        Assert.Contains("DuelPair()", duel, StringComparison.Ordinal);

        // The pair is read off the blows of the tick, which BlowReadout builds
        // from the canonical journal — not from a new field and not from a guess.
        var pair = AdapterSource.Body("DuelPair");
        Assert.Contains("_blows.Blows", pair, StringComparison.Ordinal);
        Assert.Contains("blow.Attacker is { } attacker", pair, StringComparison.Ordinal);

        // Only the two bodies of that blow are drawn, and both drawing passes read
        // the same filter, so a body hidden in one pass cannot keep its readout in
        // the other.
        foreach (var routine in new[] { "DrawElevatedWorld", "DrawBodyInformationOverlays" })
        {
            var body = AdapterSource.Body(routine);
            Assert.NotEmpty(AdapterSource.CallsTo(body, "SceneCreatures"));
            Assert.NotEmpty(AdapterSource.CallsTo(body, "SceneRaiders"));
            Assert.DoesNotContain("_state.Creatures", body, StringComparison.Ordinal);
            Assert.DoesNotContain("_state!.Creatures", body, StringComparison.Ordinal);
            Assert.DoesNotContain("_state.Raiders", body, StringComparison.Ordinal);
        }

        // Off unless the scene is on: with no duel every body is in the picture.
        Assert.Contains(
            "_duelPair is not { } duel ||",
            AdapterSource.Body("IsInScene"),
            StringComparison.Ordinal);

        // And the frame-by-frame step runs no tick at all.
        var step = AdapterSource.Body("StepStrikeFrame");
        Assert.Empty(AdapterSource.CallsTo(step, "Advance"));
        Assert.Empty(AdapterSource.CallsTo(step, "RefreshState"));
        Assert.Contains("_strikeScrub", step, StringComparison.Ordinal);
    }

    private static int Occurrences(string needle)
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
