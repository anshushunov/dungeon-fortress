using DungeonFortress.Simulation;

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

    /// <summary>
    /// How far a body has walked, in cells, counted along its own path.
    ///
    /// <para>
    /// <b>Why the cell itself carries the count.</b> A body moves one cell per
    /// tick and only along an axis — <c>PrototypeMap.NeighborOffsets</c> is four
    /// entries and <c>PrototypeWorld.Move</c> takes one of them — so every single
    /// step changes <c>X + Y</c> by exactly one, whichever of the four it was.
    /// That sum is therefore the number of steps the body has taken, up to where
    /// it started from, and it is canonical: it is read off the position the
    /// snapshot states, needs no counter of its own, and is the same number for
    /// the same tick of the same fixture however the frame was reached.
    /// <see cref="BodyMotionTests.Every_single_step_advances_the_path_by_exactly_one_cell"/>
    /// is what holds the "exactly one" part.
    /// </para>
    ///
    /// <para>
    /// <paramref name="alpha"/> is the share of the step already drawn, so the
    /// count runs continuously between two cells instead of jumping at the tick
    /// boundary. A body that stands still has <paramref name="from"/> equal to
    /// <paramref name="to"/> and its count does not move at all — which is the
    /// first half of "a standing body does not bob", before
    /// <see cref="BobOffsetRef"/> says the second.
    /// </para>
    /// </summary>
    public static double PathCells(GridPoint from, GridPoint to, double alpha) =>
        StepsTo(from) + (Clamp01(alpha) * (StepsTo(to) - StepsTo(from)));

    /// <summary>
    /// How many cells of path one gait cycle takes: the body rises over one step
    /// and settles over the next.
    ///
    /// <para>
    /// Two is the shortest cycle a step-by-step walk can have that is visible at
    /// all. With a period of one cell every body would be at the same phase
    /// whenever it stands on a cell centre — and a cell centre is where every
    /// paused frame and every captured screenshot draws it, because those are
    /// drawn at alpha 1. The cycle would then exist only between two frames
    /// nobody can stop on.
    /// </para>
    /// </summary>
    public const double GaitPeriodCells = 2.0;

    /// <summary>
    /// How far a walking body rides above the line it stands on at the top of the
    /// cycle, in the reference pixels <c>Main.ScaleWorld</c> multiplies. 1.8 of
    /// them is 3.27 world px at the shipped 40 px tile against a body drawn 61.82
    /// px tall — about a twentieth of the body, which is a gait rather than a
    /// bounce.
    /// </summary>
    public const double BobHeightRef = 1.8;

    /// <summary>
    /// How far above its feet a body is drawn, in reference pixels, having walked
    /// <paramref name="pathCells"/>. Negative, because the view's Y grows
    /// downwards.
    ///
    /// <para>
    /// <b>A standing body does not bob, by construction.</b> Not "bobs slowly",
    /// not "bobs with a small amplitude": <paramref name="walking"/> is false and
    /// the answer is exactly zero, so a resting creature is drawn on the same line
    /// it has always been drawn on. That is the half of this Issue's second
    /// criterion a phase alone cannot give, because a phase frozen at some point
    /// of the cycle would leave the body hanging above the floor.
    /// </para>
    ///
    /// <para>
    /// The curve never goes below zero on purpose. A body sinking under its own
    /// foot line reads as a body sinking into the floor, and the ground a body
    /// stands on is not the drawing's to move — the same rule
    /// <see cref="CameraView.GoblinFootLine"/> is built on.
    /// </para>
    /// </summary>
    public static double BobOffsetRef(double pathCells, bool walking) =>
        walking
            ? -BobHeightRef *
              (1.0 + Math.Cos(Math.Tau * pathCells / GaitPeriodCells)) / 2.0
            : 0.0;

    private static int StepsTo(GridPoint cell) => cell.X + cell.Y;

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}
