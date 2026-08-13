using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// One wound mark, ready to be drawn: where on the body it goes, how large, and
/// in what colour.
/// </summary>
/// <param name="Part">The hurt part this mark is about.</param>
/// <param name="Severity">How bad it is.</param>
/// <param name="OffsetRef">
/// Where the mark's centre goes, in reference pixels from the body's render
/// centre — the space <c>Main.ScaleWorld</c> multiplies.
/// </param>
/// <param name="Color">The fill, as a hex string the adapter hands the engine.</param>
public readonly record struct InjuryMark(
    BodyPart Part,
    InjuryKind Severity,
    ViewPoint OffsetRef,
    string Color);

/// <summary>
/// <b>Where a localised wound is drawn on the silhouette, and in what colour.</b>
/// Issue #420.
///
/// <para><b>Why a mark on the part and not a glyph over the head.</b> The owner
/// chose the form on 2026-08-14 (record 19 of Issue #415): «отметина поверх
/// повреждённой части силуэта, тяжесть — насыщенностью». The two rejected
/// alternatives are named here so they are not re-proposed: a glyph over the head
/// is what <c>PITCH.md</c> 6.13 says word for word and is cheaper, but the part of
/// the body would then have to be learned from the legend rather than seen; and
/// both together would put two new elements on a crowd frame, against the
/// readability guard and against the name caption, which cost slice 5 three
/// attempts.</para>
///
/// <para><b>Why this channel had to exist at all.</b> Everything the previous
/// slice shipped needs the player to act first: the word over the head is drawn
/// only for the body under the cursor or the selected one
/// (<see cref="WorldLabels.Requests"/> keeps a creature only when
/// <c>focus.Hovered</c> or <c>focus.Selected</c> names it), the panel needs a
/// click, the story needs a click. The one always-on channel — the limp — is
/// 1.96 world px wide at the shipped tile and cannot exceed 3.27 whatever depth it
/// is given, which is measured in
/// <c>InjuryVisibilityTests.The_limp_separates_the_two_gaits_by_less_than_two_world_pixels</c>
/// and recorded in <c>evidence/420-before.json</c>. So on the quiet crowd frame
/// the owner was looking at, a localised wound was shown by nothing at all.</para>
///
/// <para><b>Nothing here reaches the simulation.</b> An offset, a radius and a
/// colour change pixels and only pixels: no value below enters the canonical
/// snapshot, the checksum or the command log, which is what
/// <see href="../../docs/decisions/0011-presentation-layer-without-engine.md">ADR
/// 0011</see> requires of everything the picture is made of. It is a decision with
/// cases, so it lives where the "Pure .NET" job can check it and the adapter only
/// multiplies by the tile scale and calls the engine.</para>
/// </summary>
public static class InjuryMarks
{
    /// <summary>
    /// <b>The anchors are measurements of the art, not choices.</b> Each pair is
    /// the alpha-weighted centre of mass of that part's own shipped PNG
    /// (<c>assets/generated/goblins/cutout_v1</c>), carried into reference pixels
    /// relative to a body's render centre. The measuring script and its output are
    /// committed — <c>evidence/420-measure-part-anchors.py</c> and
    /// <c>evidence/420-part-anchors.json</c> — and
    /// <c>InjuryMarkTests.Every_anchor_is_the_measured_centre_of_mass_of_its_part</c>
    /// reads that file and holds these four numbers to it, so the copy here cannot
    /// drift away from the art the way a second set of pivots would.
    ///
    /// <para><b>The centre of mass and deliberately not the joint.</b> The rig
    /// states a pivot per part, and a pivot is a shoulder or a hip — the
    /// <em>edge</em> of the part, where it hangs off its parent. A mark placed
    /// there sits on the neck, not on the head, and on the hip, not on the leg.
    /// The two sets of numbers lie side by side in the evidence file: for the near
    /// arm they are 4.4 reference px apart, which at the shipped tile is 8 world
    /// px on a body drawn 61.8 tall.</para>
    ///
    /// <para><b>The near limbs, because they are the ones drawn in front.</b> The
    /// goblin is drawn three-quarter, so it has a far arm and a near arm; the model
    /// has one <see cref="BodyPart.Arm"/>. <see cref="BodyRig.LayerOrder"/> puts
    /// <c>arm_near</c> and <c>leg_near</c> in front of the trunk and the far pair
    /// behind it, so the near ones are the pixels a mark can land on without the
    /// body drawing over it.</para>
    /// </summary>
    private static readonly Dictionary<BodyPart, ViewPoint> Anchors = new()
    {
        [BodyPart.Head] = new ViewPoint(0.4809, -12.9181),
        [BodyPart.Torso] = new ViewPoint(0.2920, -2.6291),
        [BodyPart.Arm] = new ViewPoint(-7.7320, -3.4187),
        [BodyPart.Leg] = new ViewPoint(-3.7601, 4.0302),
    };

    /// <summary>The part of the rig each body part is marked on.</summary>
    public static string RigPartOf(BodyPart part) => part switch
    {
        BodyPart.Head => "head",
        BodyPart.Torso => "torso",
        BodyPart.Arm => "arm_near",
        BodyPart.Leg => "leg_near",
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, null),
    };

    /// <summary>
    /// Where the mark for <paramref name="part"/> goes, in reference pixels from
    /// the body's render centre. Y grows downwards, as everywhere in the view.
    /// </summary>
    public static ViewPoint AnchorRef(BodyPart part) =>
        Anchors.TryGetValue(part, out var anchor)
            ? anchor
            : throw new ArgumentOutOfRangeException(nameof(part), part, null);

    /// <summary>
    /// How large the mark is, in reference pixels: 4.73 world px at the shipped 40
    /// px tile, i.e. a disc 9.5 px across on a body drawn 61.8 px tall.
    ///
    /// <para>It is bounded on both sides by what it has to do. The narrowest part
    /// it lands on is the near arm, 5.5 reference px wide by its own alpha bounds,
    /// so a mark much wider than this would hang off the limb it is naming; and the
    /// state dot the legend already documents is 2.25 reference px in radius and is
    /// read at a glance, which is the smallest measured precedent in this project
    /// for a mark that is not text.</para>
    ///
    /// <para>Slightly larger than that dot on purpose: two round marks on one body
    /// are told apart by colour and place first, and a size that is plainly not the
    /// same size is the cheapest third difference.</para>
    /// </summary>
    public const double RadiusRef = 2.6;

    /// <summary>
    /// The dark rim around the disc, in reference pixels. A wound lands on green
    /// skin, on a teal tunic and on a brown boot, and only a rim makes one mark of
    /// the three: the same reason <c>DrawBlowDamage</c> draws the outline of a
    /// damage number before the number.
    /// </summary>
    public const double RimWidthRef = 1.0;

    /// <summary>
    /// The rim's colour: near-black red, so the rim reads as part of the wound
    /// rather than as a second mark.
    /// </summary>
    public const string RimColor = "#450a0a";

    /// <summary>
    /// <b>Severity is saturation</b>, which is the owner's own word for it. Both
    /// colours are hue 0 — the same red — and differ in how much of it there is:
    /// heavy is <c>hsl(0, 72 %, 51 %)</c> and light <c>hsl(0, 45 %, 62 %)</c>. A
    /// heavy wound is therefore the more insistent mark on a frame full of them,
    /// which is the reading the panel already gives in words («тяжело» against
    /// «легко») and the caption gives with its exclamation mark.
    ///
    /// <para>Red and not one of the state dot's five colours: blue, amber, pink,
    /// green and gray are spoken for by the legend row above it, and pink — the
    /// nearest of them — is <c>#f472b6</c>, far lighter and bluer than either
    /// value here. The raider's outline is red, but an outline is a fringe around a
    /// whole silhouette and no raider carries a localised wound in the snapshot at
    /// all, so the two cannot appear on the same body.</para>
    /// </summary>
    public static string ColorOf(InjuryKind severity) => severity switch
    {
        InjuryKind.Heavy => "#dc2626",
        InjuryKind.Light => "#c97272",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
    };

    /// <summary>
    /// Every mark one creature wears, in the pitch's own order of the parts.
    ///
    /// <para><b>Every hurt part, not the worst one.</b> The caption over the head
    /// names the worst part alone, because a caption is a line of text competing
    /// for a neighbour's name; the body has room for all four, and «у кого что
    /// повреждено» is the question this Issue exists to answer. A creature with a
    /// hurt arm and a hurt leg therefore wears two marks, and the frame says so
    /// without the player clicking anything.</para>
    ///
    /// <para>The order is <see cref="BodyParts.All"/>'s, so a frame is the same
    /// frame however the blows happened to land. The marks cannot overlap in any
    /// case: the closest two anchors are the torso's and the leg's, 7.8 reference
    /// px apart against a diameter of 5.2.</para>
    ///
    /// <para><b>A body on the ground wears none of them, and that is a limit rather
    /// than an oversight.</b> The anchors are places on the <em>standing</em>
    /// silhouette, measured off the idle cutout; the <c>downed</c> pose is a body
    /// lying down, and its opaque pixels start 104 rows into the same canvas
    /// (<see cref="CameraView"/>'s note on the pack). Marks left on would therefore
    /// float above a body instead of landing on its parts, which is worse than
    /// silence — and a downed body is not silent: it carries the white cross and its
    /// HP bar, both of which the legend already names. What it costs is real and is
    /// named here so it is not rediscovered: on the frame where a wounded creature
    /// is knocked down, the wound stops being visible until it is back on its
    /// feet.</para>
    /// </summary>
    public static IReadOnlyList<InjuryMark> Of(PrototypeCreatureSnapshot creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Mode == CreatureMode.Downed)
        {
            return [];
        }

        var marks = new List<InjuryMark>(BodyParts.Count);
        foreach (var part in BodyParts.All)
        {
            var injury = creature.Injuries.FirstOrDefault(item => item.Part == part);
            if (injury is null || injury.Severity == InjuryKind.None)
            {
                continue;
            }

            marks.Add(new InjuryMark(
                part,
                injury.Severity,
                AnchorRef(part),
                ColorOf(injury.Severity)));
        }

        return marks;
    }
}
