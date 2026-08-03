using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// How a blow reads: the curves, the colours, the geometry and the hit-stop, all
/// without starting the engine (ADR 0011).
/// </summary>
public sealed class BlowEffectsTests
{
    private static Blow Hit(BodyKind target = BodyKind.Raider, int damage = 5) =>
        new(
            new BodyRef(BodyKind.Creature, 1),
            new BodyRef(target, 2),
            damage,
            BlowOutcome.Hit,
            BlowEvidence.Recorded);

    private static Blow Downed(BodyKind target = BodyKind.Raider) =>
        new(
            new BodyRef(BodyKind.Creature, 1),
            new BodyRef(target, 2),
            3,
            BlowOutcome.Downed,
            BlowEvidence.Recorded);

    /// <summary>
    /// The property the whole grammar hangs on: an effect fades within its tick
    /// but never to nothing. A curve that reached zero would be invisible in every
    /// paused frame and in every captured screenshot, because those are drawn at
    /// alpha 1 — which is exactly how the evidence for this Issue is taken.
    /// </summary>
    [Fact]
    public void An_effect_fades_across_its_tick_and_is_still_there_at_the_end_of_it()
    {
        Assert.Equal(BlowEffects.FlashPeak, BlowEffects.FlashAlpha(0.0), 6);
        Assert.Equal(BlowEffects.FlashFloor, BlowEffects.FlashAlpha(1.0), 6);
        Assert.True(BlowEffects.FlashAlpha(1.0) > 0.0);
        Assert.True(BlowEffects.FlashAlpha(0.5) < BlowEffects.FlashAlpha(0.0));
        Assert.True(BlowEffects.FlashAlpha(0.5) > BlowEffects.FlashAlpha(1.0));

        Assert.Equal(BlowEffects.DamagePeak, BlowEffects.DamageAlpha(0.0), 6);
        Assert.Equal(BlowEffects.DamageFloor, BlowEffects.DamageAlpha(1.0), 6);
        Assert.True(BlowEffects.DamageAlpha(1.0) > 0.0);

        // Out-of-range input is clamped rather than extrapolated: an accumulator
        // that overshot a tick must not drive an alpha above one or below zero.
        Assert.Equal(BlowEffects.FlashPeak, BlowEffects.FlashAlpha(-3.0), 6);
        Assert.Equal(BlowEffects.FlashFloor, BlowEffects.FlashAlpha(4.0), 6);
    }

    /// <summary>The number drifts upwards over the tick and never sits on the body.</summary>
    [Fact]
    public void The_damage_number_rises_above_the_body()
    {
        Assert.True(BlowEffects.DamageOffsetRef(0.0) < 0.0);
        Assert.True(BlowEffects.DamageOffsetRef(1.0) < BlowEffects.DamageOffsetRef(0.0));
        Assert.Equal(
            -(BlowEffects.DamageBaseRef + BlowEffects.DamageRiseRef),
            BlowEffects.DamageOffsetRef(1.0),
            6);
    }

    /// <summary>
    /// Two crew members striking the same raider on one tick happens twice in the
    /// first wave of the shipped journal, so two numbers over one body is the
    /// ordinary case and not the exotic one. They stand side by side and the row
    /// stays centred on the body.
    /// </summary>
    [Fact]
    public void Numbers_over_one_body_stand_side_by_side_and_stay_centred()
    {
        Assert.Equal(0.0, BlowEffects.DamageSlotOffsetRef(0, 1), 6);

        var pair = new[]
        {
            BlowEffects.DamageSlotOffsetRef(0, 2),
            BlowEffects.DamageSlotOffsetRef(1, 2),
        };
        Assert.True(pair[0] < pair[1]);
        Assert.Equal(0.0, pair[0] + pair[1], 6);
        Assert.Equal(BlowEffects.DamageSlotRef, pair[1] - pair[0], 6);

        var triple = new[]
        {
            BlowEffects.DamageSlotOffsetRef(0, 3),
            BlowEffects.DamageSlotOffsetRef(1, 3),
            BlowEffects.DamageSlotOffsetRef(2, 3),
        };
        Assert.Equal(0.0, triple[1], 6);
        Assert.Equal(0.0, triple.Sum(), 6);
    }

    /// <summary>The number says hit points leaving a body, sign included.</summary>
    [Fact]
    public void A_damage_number_is_written_as_a_loss()
    {
        Assert.Equal("-5", BlowEffects.DamageLabel(Hit()));
        Assert.Equal("-1", BlowEffects.DamageLabel(Hit(damage: 1)));
    }

    /// <summary>
    /// Three readings, three colours, and the two channels do not contradict each
    /// other: a body put down is white on both the number and the flash, which is
    /// the white the downed cross is already drawn in.
    /// </summary>
    [Fact]
    public void Hit_and_downed_and_which_side_lost_the_hit_points_all_read_apart()
    {
        var raiderHit = BlowEffects.DamageColor(Hit(BodyKind.Raider));
        var crewHit = BlowEffects.DamageColor(Hit(BodyKind.Creature));
        var raiderDowned = BlowEffects.DamageColor(Downed(BodyKind.Raider));
        var crewDowned = BlowEffects.DamageColor(Downed(BodyKind.Creature));

        Assert.NotEqual(raiderHit, crewHit);
        Assert.NotEqual(raiderHit, raiderDowned);
        Assert.NotEqual(crewHit, crewDowned);
        Assert.Equal(raiderDowned, crewDowned);

        Assert.NotEqual(
            BlowEffects.FlashColor(BlowOutcome.Hit),
            BlowEffects.FlashColor(BlowOutcome.Downed));
        Assert.NotEqual(
            BlowEffects.StreakColor(Hit(BodyKind.Raider)),
            BlowEffects.StreakColor(Hit(BodyKind.Creature)));

        foreach (var colour in new[]
                 {
                     raiderHit, crewHit, raiderDowned,
                     BlowEffects.FlashColor(BlowOutcome.Hit),
                     BlowEffects.FlashColor(BlowOutcome.Downed),
                     BlowEffects.StreakColor(Hit()),
                     BlowEffects.StreakColor(Hit(BodyKind.Creature)),
                 })
        {
            Assert.Matches("^#[0-9a-f]{6}$", colour);
        }
    }

    /// <summary>
    /// The streak is a piece of the line between two bodies, pointing from the
    /// striker to the struck, and it touches neither of them.
    /// </summary>
    [Fact]
    public void The_streak_points_from_the_striker_to_the_struck_and_reaches_neither()
    {
        var attacker = new ViewPoint(100, 200);
        var target = new ViewPoint(300, 200);
        var streak = BlowEffects.Streak(attacker, target);

        Assert.True(streak.From.X > attacker.X);
        Assert.True(streak.To.X < target.X);
        Assert.True(streak.From.X < streak.To.X);
        Assert.Equal(200, streak.From.Y, 6);
        Assert.Equal(200, streak.To.Y, 6);

        // Reversing the pair reverses the stroke: the direction is the reading.
        var back = BlowEffects.Streak(target, attacker);
        Assert.True(back.From.X > back.To.X);

        // Two bodies drawn on one point give a segment of zero length, which the
        // adapter skips instead of drawing a dot nobody can read a direction off.
        var stacked = BlowEffects.Streak(attacker, attacker);
        Assert.Equal(stacked.From, stacked.To);
    }

    /// <summary>
    /// Hit-stop holds the drawing and only the drawing. The remapping can never
    /// raise the alpha, which is what keeps it away from the frame-pacing probe's
    /// lead check: a lower alpha draws a body nearer the cell it came from, never
    /// past the cell the simulation has reached.
    /// </summary>
    [Fact]
    public void Hit_stop_only_ever_holds_the_drawing_back()
    {
        Assert.Equal(0.0, BlowEffects.HitStopAlpha(0.0, landed: true), 6);
        Assert.Equal(0.0, BlowEffects.HitStopAlpha(BlowEffects.HitStopShare, landed: true), 6);
        Assert.Equal(1.0, BlowEffects.HitStopAlpha(1.0, landed: true), 6);

        for (var step = 0; step <= 20; step++)
        {
            var alpha = step / 20.0;
            Assert.True(BlowEffects.HitStopAlpha(alpha, landed: true) <= alpha + 1e-9);
            Assert.Equal(alpha, BlowEffects.HitStopAlpha(alpha, landed: false), 6);
        }

        // A tick with no blow in it is untouched, including the accumulator's own
        // out-of-range values.
        Assert.Equal(1.0, BlowEffects.HitStopAlpha(1.7, landed: false), 6);
        Assert.Equal(0.0, BlowEffects.HitStopAlpha(-0.2, landed: true), 6);
    }

    [Fact]
    public void A_blow_is_required_where_the_reading_depends_on_one()
    {
        Assert.Throws<ArgumentNullException>(() => BlowEffects.DamageLabel(null!));
        Assert.Throws<ArgumentNullException>(() => BlowEffects.DamageColor(null!));
        Assert.Throws<ArgumentNullException>(() => BlowEffects.StreakColor(null!));
    }
}
