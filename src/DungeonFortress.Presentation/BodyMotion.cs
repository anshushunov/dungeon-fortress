namespace DungeonFortress.Presentation;

/// <summary>Which way a body is turned on screen.</summary>
public enum BodyFacing
{
    /// <summary>Turned towards decreasing X.</summary>
    Left,

    /// <summary>Turned towards increasing X.</summary>
    Right,
}

/// <summary>
/// How a body moves while the picture is drawn: which way it is turned, how it
/// rides up and down as it walks, how far it leans into the step and how a blow
/// squashes it.
///
/// <para>
/// <b>Nothing here reaches the simulation.</b> Every number below changes pixels
/// and only pixels: no value enters the canonical snapshot, the checksum or the
/// command log, and a build that answered <see cref="BodyFacing.Right"/>,
/// <c>0</c>, <c>0</c> and <c>1</c> everywhere would be the game exactly as it
/// was. That is the same standing of <see cref="BlowEffects"/> and the same
/// reason: a decision with cases belongs where the "Pure .NET" CI job can see it
/// (<see href="../../docs/decisions/0011-presentation-layer-without-engine.md">ADR
/// 0011</see>), and <c>Main.cs</c> only multiplies and translates.
/// </para>
///
/// <para>
/// <b>Why the walk phase is the path and not the clock.</b> A phase taken from
/// elapsed time keeps running while a body stands still, so a resting creature
/// would bob on the spot — and a captured frame, drawn at alpha 1 with time
/// stopped, would put every body at whatever phase the clock happened to hold.
/// The phase here is a function of <em>how far the body has walked</em>
/// (<see cref="PathCells"/>), so standing still means standing still by
/// construction, and the same tick of the same fixture draws the same picture
/// however long the process has been alive.
/// </para>
/// </summary>
public static class BodyMotion
{
    /// <summary>
    /// The direction the pack is drawn in. Every one of the six v2 states was
    /// authored facing the same way — <c>docs/art/goblin-v2-provenance.md</c>
    /// asks the generator in as many words to «preserve the same face,
    /// proportions, outfit, palette, handedness, and three-quarter facing
    /// direction across every pose» — so one constant covers the pack rather than
    /// a table with a row per pose.
    /// </summary>
    public const BodyFacing AuthoredFacing = BodyFacing.Right;

    /// <summary>
    /// Which way a body with no history yet is turned: the way the art already
    /// points, so a body that has never moved is drawn exactly as it was drawn
    /// before this existed.
    /// </summary>
    public const BodyFacing RestingFacing = AuthoredFacing;

    /// <summary>
    /// Where a body is turned after moving <paramref name="dx"/> to the side.
    ///
    /// <para>
    /// A step with no sideways part — straight up, straight down, or no step at
    /// all — keeps the facing the body already had. That memory is the whole of
    /// why this takes the current value instead of answering from
    /// <paramref name="dx"/> alone: a creature that walks left and then turns
    /// down the corridor would otherwise snap back to the right the moment its
    /// step stopped having a sideways part, and it would snap again on the tick
    /// it is blocked.
    /// </para>
    /// </summary>
    public static BodyFacing Turn(BodyFacing current, double dx) => dx switch
    {
        > 0 => BodyFacing.Right,
        < 0 => BodyFacing.Left,
        _ => current,
    };

    /// <summary>
    /// What to multiply the drawn width by so the body faces
    /// <paramref name="facing"/>: <c>1</c> when the art already points that way
    /// and <c>-1</c> when it has to be mirrored.
    ///
    /// <para>
    /// It is a factor on the sprite rather than a second set of files, which is
    /// the point of doing this in code: the flip costs no pixel of art, and it
    /// applies to the pose silhouette the side outline and the blow flash are
    /// drawn from just as it applies to the sprite.
    /// </para>
    /// </summary>
    public static double FlipScale(BodyFacing facing) =>
        facing == AuthoredFacing ? 1.0 : -1.0;
}
