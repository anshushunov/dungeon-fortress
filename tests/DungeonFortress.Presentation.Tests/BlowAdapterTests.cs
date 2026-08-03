using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The structural half of Issue #210, by the same method
/// <see cref="SideOutlineAdapterTests"/> and <see cref="WorldDrawPassGuardTests"/>
/// use: the adapter is read as text, the engine is not started (ADR 0011).
///
/// <para>
/// A reading that nothing draws is a rule about nothing, and a pose the pack
/// contains but the adapter never asks for is exactly the state this Issue was
/// opened about — <c>windup</c> and <c>flinch</c> shipped in the v2 pack, were
/// loaded at start-up, and were unreachable because both callers passed an
/// unconditional <c>BodyActionPhase.None</c>. These checks are what makes that
/// unable to come back quietly.
/// </para>
/// </summary>
public sealed class BlowAdapterTests
{
    /// <summary>
    /// The two poses become reachable, and they stay reachable. Neither sprite key
    /// hands <c>BodySprites</c> a literal phase, and the literal that made them
    /// unreachable is nowhere in the adapter at all — including the spellings a
    /// per-call check would miss, such as a phase folded into a helper.
    /// </summary>
    [Fact]
    public void Neither_sprite_key_passes_a_literal_phase()
    {
        var crew = Assert.Single(AdapterSource.CallsTo(
            AdapterSource.Body("CrewSpriteKey"),
            $"{nameof(BodySprites)}.{nameof(BodySprites.CrewKey)}"));
        Assert.Equal(2, crew.Arguments.Count);
        Assert.Contains("BodyPhase(", crew.Arguments[1], StringComparison.Ordinal);

        var raider = Assert.Single(AdapterSource.CallsTo(
            AdapterSource.Body("RaiderSpriteKey"),
            $"{nameof(BodySprites)}.{nameof(BodySprites.RaiderKey)}"));
        Assert.Equal(3, raider.Arguments.Count);
        Assert.Contains("BodyPhase(", raider.Arguments[2], StringComparison.Ordinal);

        Assert.DoesNotContain(
            $"{nameof(BodyActionPhase)}.{nameof(BodyActionPhase.None)}",
            AdapterSource.Masked,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And the phase itself comes from the journal reading rather than from a
    /// decision taken here. A <c>BodyPhase</c> that answered on its own would leave
    /// the check above green while putting every body back in its idle pose.
    /// </summary>
    [Fact]
    public void The_phase_comes_from_the_reading_built_for_this_tick()
    {
        var phase = AdapterSource.Body("BodyPhase");
        Assert.Contains($".{nameof(BlowReading.PhaseOf)}(", phase, StringComparison.Ordinal);
        Assert.Contains(nameof(BodyRef), phase, StringComparison.Ordinal);

        // Built once per tick, where the snapshot is taken, and from the canonical
        // journal plus the hit points measured before the tick ran.
        var refresh = AdapterSource.Body("RefreshState");
        var built = Assert.Single(AdapterSource.CallsTo(
            refresh,
            $"{nameof(BlowReadout)}.{nameof(BlowReadout.Of)}"));
        Assert.Equal(2, built.Arguments.Count);
        Assert.Contains(
            $"{nameof(BlowReadout)}.{nameof(BlowReadout.Of)}",
            AdapterSource.Masked,
            StringComparison.Ordinal);

        // The hit points are captured before the world runs, not after: measured
        // afterwards they would equal the values they are supposed to differ from.
        var advance = AdapterSource.Body("Advance");
        Assert.Single(AdapterSource.CallsTo(advance, "RememberCreatureHitPoints"));
        Assert.True(
            advance.IndexOf("RememberCreatureHitPoints(", StringComparison.Ordinal) <
            advance.IndexOf("RunTicks(", StringComparison.Ordinal),
            "Advance remembers the hit points after running the tick, so every " +
            "difference it could measure has already been overwritten.");
    }

    /// <summary>
    /// The three marks of a blow are actually drawn, each from the routine whose
    /// pass it belongs to. Deleting any one of them is the mutation this test
    /// exists for: the phase would still be computed and the frame would go back
    /// to saying nothing about the blow.
    /// </summary>
    [Fact]
    public void Both_kinds_of_body_get_a_flash_and_a_number_and_a_blow_gets_a_streak()
    {
        foreach (var routine in new[] { "DrawCreatureInformation", "DrawRaiderInformation" })
        {
            var body = AdapterSource.Body(routine);
            Assert.Single(AdapterSource.CallsTo(body, "DrawBlowFlash"));
            Assert.Single(AdapterSource.CallsTo(body, "DrawBlowDamage"));

            // The flash is a tint the size of the body, so everything else this
            // routine draws has to go on top of it.
            Assert.True(
                body.IndexOf("DrawBlowFlash(", StringComparison.Ordinal) <
                body.IndexOf("DrawBlowDamage(", StringComparison.Ordinal),
                $"{routine} draws the flash over its own readouts instead of under " +
                "them.");
        }

        Assert.Single(AdapterSource.CallsTo(
            AdapterSource.Body("DrawBodyInformationOverlays"),
            "DrawBlowStreaks"));
    }

    /// <summary>
    /// The flash is the shape of the body and nothing else. Drawn from anything but
    /// the pose silhouette it would be the goblin's own palette multiplied by a
    /// colour, which is the defect the side outline of Issue #208 had to solve
    /// already, and it has to be the silhouette of the pose the body is actually
    /// drawn in — a flinching body flashed in its idle outline is a second body.
    /// </summary>
    [Fact]
    public void The_flash_is_the_silhouette_of_the_pose_the_body_is_drawn_in()
    {
        Assert.Contains(
            "CrewSpriteKey(",
            AdapterSource.Body("DrawCreatureInformation"),
            StringComparison.Ordinal);
        Assert.Contains(
            "RaiderSpriteKey(",
            AdapterSource.Body("DrawRaiderInformation"),
            StringComparison.Ordinal);

        var flash = AdapterSource.Body("DrawBlowFlash");
        Assert.Contains("_goblinSilhouettes", flash, StringComparison.Ordinal);
        Assert.Contains(
            $"{nameof(BlowEffects)}.{nameof(BlowEffects.FlashColor)}(",
            flash,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{nameof(BlowEffects)}.{nameof(BlowEffects.FlashAlpha)}(",
            flash,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every value a blow's marks are drawn with is read from the policy rather
    /// than written next to the draw call. A literal here is invisible to every
    /// check in the repository — the same argument the alpha check of
    /// <see cref="WorldDrawPassGuardTests"/> is built on.
    /// </summary>
    [Fact]
    public void The_marks_of_a_blow_take_every_value_from_the_policy()
    {
        var damage = AdapterSource.Body("DrawBlowDamage");
        foreach (var member in new[]
                 {
                     nameof(BlowEffects.DamageLabel),
                     nameof(BlowEffects.DamageColor),
                     nameof(BlowEffects.DamageAlpha),
                     nameof(BlowEffects.DamageOffsetRef),
                     nameof(BlowEffects.DamageSlotOffsetRef),
                     nameof(BlowEffects.DamageTextRef),
                     nameof(BlowEffects.DamageOutlineRef),
                     nameof(BlowEffects.DamageOutlineColor),
                 })
        {
            Assert.Contains(
                $"{nameof(BlowEffects)}.{member}",
                damage,
                StringComparison.Ordinal);
        }

        var streaks = AdapterSource.Body("DrawBlowStreaks");
        foreach (var member in new[]
                 {
                     nameof(BlowEffects.Streak),
                     nameof(BlowEffects.StreakColor),
                 })
        {
            Assert.Contains(
                $"{nameof(BlowEffects)}.{member}(",
                streaks,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            $"{nameof(BlowEffects)}.{nameof(BlowEffects.StreakWidthRef)}",
            streaks,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Hit-stop lives in the one place that decides which frame of the journey
    /// between two canonical positions is drawn, and nowhere else. A pause put
    /// anywhere near <c>Advance</c> or the accumulator would be a pause of the
    /// simulation, which is what the hard constraint of this Issue forbids.
    /// </summary>
    [Fact]
    public void Hit_stop_holds_the_drawing_and_never_the_tick()
    {
        var alpha = AdapterSource.Body("MotionAlpha");
        Assert.Contains(
            $"{nameof(BlowEffects)}.{nameof(BlowEffects.HitStopAlpha)}(",
            alpha,
            StringComparison.Ordinal);

        Assert.Empty(AdapterSource.CallsTo(
            AdapterSource.Body("Advance"),
            $"{nameof(BlowEffects)}.{nameof(BlowEffects.HitStopAlpha)}"));
        Assert.Equal(
            1,
            CountOf($"{nameof(BlowEffects)}.{nameof(BlowEffects.HitStopAlpha)}("));
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
