namespace DungeonFortress.Presentation;

/// <summary>
/// Where in a blow a body is, as far as the picture is concerned.
///
/// <para>
/// The five names are ADR 0020's own: «стойка → замах → удар → проводка →
/// возврат». They are <em>not</em> a new field on anything: a body is in one of
/// them because <see cref="BlowReadout"/> read a blow off the canonical journal
/// and because the view knows how far through that tick it has drawn. Both
/// inputs already existed before this enum did.
/// </para>
/// </summary>
public enum StrikePhase
{
    /// <summary>No blow touches this body; it stands as the rig rests.</summary>
    Stance,

    /// <summary>Drawing back, before contact.</summary>
    Windup,

    /// <summary>The moment of contact.</summary>
    Strike,

    /// <summary>Carrying the blow through, after contact.</summary>
    FollowThrough,

    /// <summary>Coming back to the stance.</summary>
    Recover,
}

/// <summary>Which end of a blow a body is.</summary>
public enum StrikeRole
{
    /// <summary>Neither: no blow touches it this tick.</summary>
    Bystander,

    /// <summary>The body that struck.</summary>
    Attacker,

    /// <summary>The body that was struck.</summary>
    Target,
}

/// <summary>
/// One part of the rig at one moment: how far it has turned around its joint,
/// and how far it has been slid off its rest place.
/// </summary>
/// <param name="Degrees">
/// Rotation about the part's own joint. Positive is clockwise on a canvas whose
/// Y grows downwards, so a part hanging below its joint swings towards
/// decreasing X and a part standing above it swings towards increasing X.
/// </param>
/// <param name="OffsetX">
/// A slide in the rig's own source-cell pixels, applied after the rotation.
/// </param>
/// <param name="OffsetY"><inheritdoc cref="OffsetX"/></param>
public readonly record struct PartPose(double Degrees, double OffsetX, double OffsetY)
{
    public static PartPose Rest { get; } = new(0.0, 0.0, 0.0);

    public static PartPose Lerp(PartPose from, PartPose to, double share) =>
        new(
            from.Degrees + ((to.Degrees - from.Degrees) * share),
            from.OffsetX + ((to.OffsetX - from.OffsetX) * share),
            from.OffsetY + ((to.OffsetY - from.OffsetY) * share));
}

/// <summary>
/// The whole of a blow as movement: the five phases of the striker's chain, the
/// two of the struck body's recoil, the lean of each body and how far each of
/// them is thrown along the line between them.
///
/// <para>
/// <b>Nothing here reaches the simulation.</b> Not one angle, not one offset and
/// not one displacement enters the canonical snapshot, the checksum or the
/// command log; a build in which every method below answered
/// <see cref="PartPose.Rest"/> and zero would be the same game with a stiffer
/// picture. That is <see href="../../docs/decisions/0011-presentation-layer-without-engine.md">ADR
/// 0011</see>, and <see href="../../docs/decisions/0020-body-animation-cutout-rig.md">ADR
/// 0020</see> restates it for the skeleton in particular: "ни одна часть
/// скелета, ни одна кривая движения и ни один эффект не входят в канонический
/// снапшот и не меняют checksum".
/// </para>
///
/// <para>
/// <b>Where the phase comes from.</b> From <see cref="BodyActionPhase"/>, which
/// <see cref="BlowReadout"/> writes off the canonical journal's
/// <c>combat_attack</c>, <c>combat_raider_downed</c> and <c>combat_downed</c>
/// entries, plus <c>tickAlpha</c> — the share of the tick already drawn, which
/// the view has owned since interpolation existed. There is no new snapshot
/// field and there is no clock: the same tick of the same fixture, drawn at the
/// same alpha, gives the same pose however long the process has been alive.
/// </para>
///
/// <para>
/// <b>Why contact is at <see cref="ContactShare"/> and not at a number of its
/// own.</b> That is where <see cref="BlowEffects.HitStopAlpha"/> already holds
/// the picture still. Hit-stop and the strike pose are the same event seen from
/// two sides, and giving them two constants is how they end up a frame apart.
/// </para>
///
/// <para>
/// <b>Why the angles are what they are.</b> They are chosen against a
/// measurement, not against a look. ADR 0020's named risk is that a cutout body
/// comes apart at the joints, and the review of Issue #243 photographed exactly
/// that. <c>evidence/244-measure-rig-gaps.py</c> composites the rig at a pose and
/// counts the pixels of background that can be seen <em>through</em> the body —
/// zero on the rest pose by construction — and the offsets below are what that
/// script's search reduced each keyframe to. The measurement also decides a
/// direction: the near arm can be swung far to one side of its shoulder and
/// barely at all to the other, which is why the wind-up raises the spear and the
/// strike brings it down rather than the other way round. Numbers are in
/// <c>evidence/244-rig-gaps.json</c>.
/// </para>
///
/// <para>
/// <b>Why the lean is not a part angle.</b> Turning the torso against the legs
/// opens the widest seam of any joint in the rig, and a lean is a whole body
/// leaning anyway. So <see cref="LeanDegrees"/> is a rotation of the frame the
/// body is drawn in — the same frame <see cref="BodyMotion.LeanRadians"/> already
/// uses for a walking step — and it costs no seam at all, because nothing moves
/// relative to anything.
/// </para>
/// </summary>
public static class StrikeChain
{
    /// <summary>
    /// How far through its tick a blow lands, as a share. It is
    /// <see cref="BlowEffects.HitStopShare"/> and deliberately not a number of
    /// its own.
    /// </summary>
    public const double ContactShare = BlowEffects.HitStopShare;

    /// <summary>Where the strike ends and the follow-through begins.</summary>
    public const double FollowThroughShare = 0.52;

    /// <summary>And where the follow-through gives way to the return.</summary>
    public const double RecoverShare = 0.78;

    /// <summary>
    /// The share of a tick a body already struck by is still snapping back. Held
    /// a hair after <see cref="ContactShare"/> so that the struck body is still
    /// standing on the frame the blow arrives on: a target already recoiling
    /// before contact reads as a body that flinched at nothing.
    /// </summary>
    private const double ImpactShare = 0.38;

    private const double SettleShare = 0.62;

    private sealed record Keyframe(
        double At,
        StrikePhase Phase,
        double LeanDegrees,
        double RecoilRef,
        IReadOnlyDictionary<string, PartPose> Parts);

    private static IReadOnlyDictionary<string, PartPose> Pose(
        (double Degrees, double X, double Y) head,
        (double Degrees, double X, double Y) armNear,
        (double Degrees, double X, double Y) armFar,
        (double Degrees, double X, double Y) legNear,
        (double Degrees, double X, double Y) legFar) =>
        new Dictionary<string, PartPose>(StringComparer.Ordinal)
        {
            ["head"] = new(head.Degrees, head.X, head.Y),
            ["arm_near"] = new(armNear.Degrees, armNear.X, armNear.Y),
            ["arm_far"] = new(armFar.Degrees, armFar.X, armFar.Y),
            ["leg_near"] = new(legNear.Degrees, legNear.X, legNear.Y),
            ["leg_far"] = new(legFar.Degrees, legFar.X, legFar.Y),
        };

    private static readonly IReadOnlyDictionary<string, PartPose> RestPose = Pose(
        (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0));

    /// <summary>
    /// The striker's chain. Read down the <c>At</c> column and it is the shape of
    /// a blow: nothing, then a fast draw-back, then a very short snap into
    /// contact, then a long carry-through and a longer return. The wind-up gets
    /// four times the tick the strike does on purpose — anticipation is what
    /// makes a blow readable, and a strike that is not brief does not look like
    /// an impact.
    /// </summary>
    private static readonly Keyframe[] AttackerChain =
    [
        new(0.00, StrikePhase.Windup, 0.0, 0.0, RestPose),
        new(0.28, StrikePhase.Windup, -7.0, -2.5, Pose(
            (3, -6, 1), (-30, -8, -8), (-6, -8, 8), (6, 4, 1), (-5, -4, -6))),
        new(ContactShare, StrikePhase.Strike, 9.0, 3.5, Pose(
            (-4, 7, 1), (8, 8, 8), (10, -8, -8), (9, 1, 1), (-7, -8, 6))),
        new(FollowThroughShare, StrikePhase.FollowThrough, 6.0, -4.5, Pose(
            (-3, 5, 0), (12, 8, 8), (7, -8, 1), (6, -8, -8), (-5, -5, 0))),
        new(RecoverShare, StrikePhase.Recover, 2.0, -1.5, Pose(
            (-1, 2, 0), (3, 8, -1), (2, -5, 6), (2, 1, 0), (-1, -2, 0))),
        new(1.00, StrikePhase.Recover, 0.0, 0.0, RestPose),
    ];

    /// <summary>
    /// The struck body's. It stands still until the blow arrives — the second
    /// keyframe repeats the first — and only then is thrown, which is the whole
    /// difference between a recoil and a wobble.
    /// </summary>
    private static readonly Keyframe[] TargetChain =
    [
        new(0.00, StrikePhase.Stance, 0.0, 0.0, RestPose),
        new(ContactShare - 0.02, StrikePhase.Stance, 0.0, 0.0, RestPose),
        new(ImpactShare, StrikePhase.Strike, -11.0, 6.0, Pose(
            (-7, 8, -1), (-8, 8, 4), (6, -5, 3), (-4, -1, 1), (5, -1, 1))),
        new(SettleShare, StrikePhase.FollowThrough, -4.0, 2.5, Pose(
            (-3, 5, 0), (-4, 8, -3), (3, -2, 8), (-2, 0, 0), (2, -7, 1))),
        new(1.00, StrikePhase.Recover, 0.0, 0.0, RestPose),
    ];

    /// <summary>
    /// Which end of the blow a body is, from the pose
    /// <see cref="BlowReading.PhaseOf"/> gave it. This is the whole of the link
    /// between the canonical journal and everything below.
    /// </summary>
    public static StrikeRole RoleOf(BodyActionPhase phase) => phase switch
    {
        BodyActionPhase.Windup => StrikeRole.Attacker,
        BodyActionPhase.Flinch => StrikeRole.Target,
        _ => StrikeRole.Bystander,
    };

    /// <summary>
    /// Where in the chain a body is. A body no blow touched is in
    /// <see cref="StrikePhase.Stance"/> at every alpha, which is what makes the
    /// whole of this file invisible on a tick with no fighting in it.
    /// </summary>
    public static StrikePhase PhaseAt(BodyActionPhase phase, double tickAlpha)
    {
        var chain = ChainOf(RoleOf(phase));
        if (chain is null)
        {
            return StrikePhase.Stance;
        }

        var alpha = Clamp01(tickAlpha);
        var current = chain[0].Phase;
        foreach (var key in chain)
        {
            if (key.At <= alpha)
            {
                current = key.Phase;
            }
        }

        return current;
    }

    /// <summary>
    /// How one part of the rig stands at this moment of this body's chain.
    /// Interpolated between the two keyframes around <paramref name="tickAlpha"/>,
    /// so the chain is continuous and a paused frame is a real frame of it rather
    /// than one of five stills.
    /// </summary>
    public static PartPose PoseOf(BodyActionPhase phase, string part, double tickAlpha)
    {
        ArgumentNullException.ThrowIfNull(part);
        var chain = ChainOf(RoleOf(phase));
        if (chain is null)
        {
            return PartPose.Rest;
        }

        var (before, after, share) = Span(chain, Clamp01(tickAlpha));
        return PartPose.Lerp(PartOf(before, part), PartOf(after, part), share);
    }

    /// <summary>
    /// How far the whole body is tipped into the blow, in degrees along the line
    /// between the two bodies. Positive leans towards what it is fighting.
    /// </summary>
    public static double LeanDegrees(BodyActionPhase phase, double tickAlpha)
    {
        var chain = ChainOf(RoleOf(phase));
        if (chain is null)
        {
            return 0.0;
        }

        var (before, after, share) = Span(chain, Clamp01(tickAlpha));
        return before.LeanDegrees + ((after.LeanDegrees - before.LeanDegrees) * share);
    }

    /// <summary>
    /// How far the body is thrown, in the reference pixels <c>Main.ScaleWorld</c>
    /// multiplies, measured <b>along the line from the striker to the body it
    /// struck</b>.
    ///
    /// <para>
    /// <b>The sign is the reading.</b> A striker lunges into contact and is thrown
    /// <em>back</em> after it, so its value is positive before
    /// <see cref="ContactShare"/> and negative after: the recoil of the blow
    /// pushes it away from what it hit. A target is only ever pushed away, so its
    /// value is never negative. Turn either of them round and the picture shows a
    /// goblin sucked into the thing it just speared —
    /// <c>StrikeChainTests.The_two_ends_of_a_blow_are_thrown_apart_and_never_together</c>
    /// is what refuses it, and the polarity mutant of this Issue is what proves
    /// the refusal is live.
    /// </para>
    ///
    /// <para>
    /// It moves the drawing and never the point the world is sorted and measured
    /// by, for the reason <see cref="BodyMotion.BobOffsetRef"/> gives: the frame
    /// pacing probe turns a render centre back into a cell, and a body reported in
    /// a cell the simulation has not reached is a violation of the hard
    /// constraint rather than a matter of taste.
    /// </para>
    /// </summary>
    public static double RecoilOffsetRef(StrikeRole role, double tickAlpha)
    {
        var chain = ChainOf(role);
        if (chain is null)
        {
            return 0.0;
        }

        var (before, after, share) = Span(chain, Clamp01(tickAlpha));
        return before.RecoilRef + ((after.RecoilRef - before.RecoilRef) * share);
    }

    /// <summary>
    /// Whether the blow has landed yet, at this point of its tick.
    ///
    /// <para>
    /// The flash of <see cref="BlowEffects"/> used to burn for the whole tick,
    /// which was right while a blow was one pose and one moment. It is not right
    /// any more: a body lit up before the spear reaches it is a body flinching at
    /// nothing, and at the duel's zoom that is the first thing an eye finds. It
    /// still holds after contact, so a paused frame and a captured screenshot —
    /// drawn at alpha 1 — see the flash at its floor, which is the rule every
    /// curve in <see cref="BlowEffects"/> is written to.
    /// </para>
    /// </summary>
    public static bool HasLanded(double tickAlpha) => Clamp01(tickAlpha) >= ContactShare;

    /// <summary>
    /// Whether the moment of contact is being drawn — the one window the spark
    /// of <see cref="BlowEffects"/> is allowed in. It opens on the frame the blow
    /// lands and closes with the follow-through, so a paused frame either shows
    /// the impact or does not, and never shows a spark hanging over a finished
    /// blow.
    /// </summary>
    public static bool ShowsContact(double tickAlpha)
    {
        var alpha = Clamp01(tickAlpha);
        return alpha >= ContactShare && alpha < FollowThroughShare;
    }

    /// <summary>
    /// How far through the contact window the drawing is, 0 at the moment of the
    /// blow and 1 as it closes. A frame outside the window answers 1, so a
    /// captured frame drawn at alpha 1 sees the spark at its faintest rather than
    /// at a value the curve never reaches.
    /// </summary>
    public static double ContactAlpha(double tickAlpha)
    {
        var alpha = Clamp01(tickAlpha);
        return alpha <= ContactShare
            ? 0.0
            : Clamp01((alpha - ContactShare) / (FollowThroughShare - ContactShare));
    }

    private static Keyframe[]? ChainOf(StrikeRole role) => role switch
    {
        StrikeRole.Attacker => AttackerChain,
        StrikeRole.Target => TargetChain,
        _ => null,
    };

    private static (Keyframe Before, Keyframe After, double Share) Span(
        Keyframe[] chain,
        double alpha)
    {
        for (var index = 1; index < chain.Length; index++)
        {
            if (alpha <= chain[index].At)
            {
                var before = chain[index - 1];
                var after = chain[index];
                var width = after.At - before.At;
                return (before, after, width <= 0.0 ? 1.0 : (alpha - before.At) / width);
            }
        }

        return (chain[^1], chain[^1], 1.0);
    }

    private static PartPose PartOf(Keyframe key, string part) =>
        key.Parts.TryGetValue(part, out var pose) ? pose : PartPose.Rest;

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}
