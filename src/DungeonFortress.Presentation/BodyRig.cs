using System.Text.Json;

namespace DungeonFortress.Presentation;

/// <summary>
/// One part of the cutout body, exactly as
/// <c>assets/generated/goblins/cutout_v1/goblin_cutout_rig_v1.json</c> states
/// it. Every coordinate here is in the rig's own space — source-cell pixels of
/// the 512x512 cell the parts were cut from, origin top-left — because Issue
/// #243's provenance says in as many words that #244 "may convert these
/// source-space values to runtime scale; it must not retype or replace the
/// pivots".
/// </summary>
/// <param name="Name">The part's name in the rig.</param>
/// <param name="File">Its PNG, relative to the rig file.</param>
/// <param name="Parent">The part it hangs off, or <c>null</c> for the root.</param>
/// <param name="ZIndex">Ascending back-to-front draw order, the rig's own.</param>
/// <param name="Pivot">The joint, in the part PNG's own pixels.</param>
/// <param name="RestPosition">Where the part PNG's top-left sits in the source cell.</param>
/// <param name="VisibleInRest">Whether the part is drawn when nothing is posed.</param>
public sealed record BodyRigPart(
    string Name,
    string File,
    string? Parent,
    int ZIndex,
    ViewPoint Pivot,
    ViewPoint RestPosition,
    bool VisibleInRest)
{
    /// <summary>The joint itself, in source-cell pixels: where the part turns around.</summary>
    public ViewPoint Joint => new(
        RestPosition.X + Pivot.X,
        RestPosition.Y + Pivot.Y);
}

/// <summary>
/// The goblin as a set of parts with joints, read off the rig file rather than
/// copied out of it.
///
/// <para>
/// <b>Why the file is the source and this class is not.</b> The pivots are art,
/// measured once by the builder of Issue #243 against the pixels it cut. A
/// second copy of them in C# would be a second truth that nothing compares with
/// the first, and the failure mode is silent: a part rotates around a point that
/// is a few pixels off its own shoulder and the body looks broken for a reason
/// no test can name. So this parses, and
/// <c>BodyRigTests.The_shipped_rig_file_is_the_one_the_runtime_draws</c> parses
/// the shipped file.
/// </para>
///
/// <para>
/// <b>Nothing here reaches the simulation.</b> A part, a joint and a layer order
/// change pixels and only pixels: no value below enters the canonical snapshot,
/// the checksum or the command log, which is what
/// <see href="../../docs/decisions/0011-presentation-layer-without-engine.md">ADR
/// 0011</see> requires of everything the picture is made of, and what
/// <see href="../../docs/decisions/0020-body-animation-cutout-rig.md">ADR
/// 0020</see> repeats for the skeleton in particular.
/// </para>
/// </summary>
public sealed class BodyRig
{
    private readonly Dictionary<string, BodyRigPart> _byName;

    private BodyRig(
        IReadOnlyList<BodyRigPart> parts,
        ViewSize sourceCellSize,
        ViewRect sourceBodyBox,
        ViewSize runtimeTargetSize)
    {
        Parts = parts;
        SourceCellSize = sourceCellSize;
        SourceBodyBox = sourceBodyBox;
        RuntimeTargetSize = runtimeTargetSize;
        _byName = parts.ToDictionary(part => part.Name, StringComparer.Ordinal);
    }

    /// <summary>The rig file, relative to the Godot project's asset root.</summary>
    public const string AssetFolder = "assets/generated/goblins/cutout_v1";

    /// <inheritdoc cref="AssetFolder"/>
    public const string FileName = "goblin_cutout_rig_v1.json";

    /// <summary>The root part: the one every other part hangs off.</summary>
    public const string RootPart = "torso";

    /// <summary>The part that is equipment rather than body.</summary>
    public const string WeaponPart = "weapon";

    /// <summary>
    /// The order the parts are drawn in, back to front, named here so that a
    /// human can read it and a test can hold the runtime to it.
    ///
    /// <para>
    /// <b>What the order says.</b> The goblin is drawn in three-quarter view, so
    /// the body has a far side and a near side and the order is the depth of the
    /// figure itself: the far leg and the far arm are behind the trunk, the trunk
    /// is the middle, and the near leg, the head and the near arm are in front of
    /// it. The head is above the trunk and below the near arm because the strike
    /// arm crosses the chest and the chin during the wind-up — put the head over
    /// it and the arm disappears into the face at exactly the frame the player is
    /// meant to read. The weapon is last because it is held in the near hand and
    /// swings in front of the whole figure.
    /// </para>
    ///
    /// <para>
    /// <b>It is the rig's order, not a second opinion.</b> Every entry is the
    /// rig's own <c>z_index</c> sequence, and
    /// <c>BodyRigTests.The_layer_order_is_the_rig_s_own_back_to_front_order</c>
    /// is what keeps the two the same list: swapping any two names here fails
    /// that check rather than quietly drawing a goblin whose arm is inside its
    /// own chest.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> LayerOrder { get; } =
    [
        "leg_far",
        "arm_far",
        "torso",
        "leg_near",
        "head",
        "arm_near",
        "weapon",
    ];

    /// <summary>
    /// The poses the rig draws. The other two states of the connected pack keep
    /// their flat sprite: <c>work</c> is a pick swing the rig has no chain for,
    /// and <c>downed</c> is a body on the ground whose parts would have to be
    /// laid out again from scratch. ADR 0020's probe is about the blow, so those
    /// two are deliberately out of it — see the Issue's non-goals.
    /// </summary>
    public static IReadOnlyList<string> RiggedStates { get; } =
        ["idle", "combat", "windup", "flinch"];

    /// <summary>
    /// The states the weapon layer is drawn in. It is hidden in <c>idle</c>
    /// because the rig says so (<c>visible_in_rest: false</c>) and because the
    /// flat pack it has to agree with draws no spear in idle either.
    /// </summary>
    public static IReadOnlyList<string> ArmedStates { get; } =
        ["combat", "windup", "flinch"];

    public IReadOnlyList<BodyRigPart> Parts { get; }

    /// <summary>The cell the parts were cut from: 512x512 source pixels.</summary>
    public ViewSize SourceCellSize { get; }

    /// <summary>The box the body occupies inside that cell.</summary>
    public ViewRect SourceBodyBox { get; }

    /// <summary>How large that box is drawn inside the pack's own 272x192 canvas.</summary>
    public ViewSize RuntimeTargetSize { get; }

    public BodyRigPart Part(string name) =>
        _byName.TryGetValue(name, out var part)
            ? part
            : throw new KeyNotFoundException($"The rig has no part named '{name}'.");

    public bool Has(string name) => _byName.ContainsKey(name);

    /// <summary>
    /// Source-cell pixels to canvas pixels: one number, not two.
    ///
    /// <para>
    /// The rig's own two ratios differ in the fourth digit — 116/228 against
    /// 168/330 — because the builder rounded a target size to whole pixels. Using
    /// both would make the drawn body 0.06 % wider than tall, which is invisible,
    /// and would make every part's rotation a shear, which is not: a limb turned
    /// under a non-uniform scale stops being the shape it was drawn as. The
    /// height is the one that is kept, because the height is what
    /// <see cref="CameraView.GoblinDrawSize"/> is built on and what the foot line
    /// is measured against.
    /// </para>
    /// </summary>
    public double SourceToCanvas => RuntimeTargetSize.Height / SourceBodyBox.Height;

    /// <summary>
    /// The top of the body inside the canvas: the first row any state of the
    /// connected pack has a pixel in. Taken from <see cref="CameraView"/> rather
    /// than restated, because it is a measurement of the flat pack and the whole
    /// point of the target box is that the rig lands where the flat pack stood.
    /// </summary>
    public static double CanvasTop => CameraView.SpriteOpaqueTop;

    /// <summary>And the row its feet end on.</summary>
    public static double CanvasBottom => CameraView.SpriteOpaqueBottom;

    /// <summary>
    /// The left edge of the body inside the canvas. Centred, which is where the
    /// flat pack's own support centre is: <see cref="CameraView.GoblinDrawRect"/>
    /// says it sits on the canvas centre to within 0.16 px of the drawn width.
    /// </summary>
    public double CanvasLeft =>
        (CameraView.SpriteCanvasWidth - (SourceBodyBox.Width * SourceToCanvas)) / 2.0;

    /// <summary>
    /// Where a point of the rig's source cell is drawn inside the pack's 272x192
    /// canvas — the space <see cref="CameraView.GoblinDrawRect"/> already places
    /// on the map.
    /// </summary>
    public ViewPoint CanvasPointOf(ViewPoint source) =>
        new(
            CanvasLeft + ((source.X - SourceBodyBox.X) * SourceToCanvas),
            CanvasTop + ((source.Y - SourceBodyBox.Y) * SourceToCanvas));

    /// <summary>
    /// The rig as the JSON file states it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// When the file does not describe a rig this runtime can draw: an unknown
    /// part in <see cref="LayerOrder"/>, a parent that is not a part, a root that
    /// is not <see cref="RootPart"/>, or a layer order that is not the rig's own
    /// <c>z_index</c> order. Each of those would otherwise become a body drawn
    /// wrong rather than a run that refused to start.
    /// </exception>
    public static BodyRig Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var cell = Size(root, "source_cell_size");
        var box = Box(root, "source_body_bbox");
        var target = Size(root, "runtime_target_size");

        var parts = new List<BodyRigPart>();
        foreach (var element in root.GetProperty("parts").EnumerateArray())
        {
            var parent = element.GetProperty("parent");
            parts.Add(new BodyRigPart(
                element.GetProperty("name").GetString() ?? string.Empty,
                element.GetProperty("file").GetString() ?? string.Empty,
                parent.ValueKind == JsonValueKind.Null ? null : parent.GetString(),
                element.GetProperty("z_index").GetInt32(),
                Point(element, "pivot"),
                Point(element, "rest_position"),
                !element.TryGetProperty("visible_in_rest", out var visible) ||
                    visible.GetBoolean()));
        }

        var rig = new BodyRig(parts, cell, box, target);
        Validate(rig);
        return rig;
    }

    private static void Validate(BodyRig rig)
    {
        var names = rig.Parts.Select(part => part.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in LayerOrder)
        {
            if (!names.Contains(name))
            {
                throw new ArgumentException(
                    $"The rig has no part named '{name}', which the layer order draws.");
            }
        }

        if (names.Count != LayerOrder.Count)
        {
            throw new ArgumentException(
                "The rig has " + names.Count + " parts and the layer order draws " +
                LayerOrder.Count + "; every part of a body has to be drawn.");
        }

        foreach (var part in rig.Parts)
        {
            if (part.Parent is { } parent && !names.Contains(parent))
            {
                throw new ArgumentException(
                    $"Part '{part.Name}' hangs off '{parent}', which the rig does not have.");
            }

            if (part.Parent is null && part.Name != RootPart)
            {
                throw new ArgumentException(
                    $"Part '{part.Name}' has no parent, but the root is '{RootPart}'.");
            }
        }

        var byDepth = rig.Parts
            .OrderBy(part => part.ZIndex)
            .Select(part => part.Name)
            .ToArray();
        if (!byDepth.SequenceEqual(LayerOrder, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The layer order is " + string.Join(", ", LayerOrder) +
                ", and the rig's own back-to-front order is " +
                string.Join(", ", byDepth) + ".");
        }
    }

    private static ViewPoint Point(JsonElement element, string name)
    {
        var pair = element.GetProperty(name);
        return new ViewPoint(pair[0].GetDouble(), pair[1].GetDouble());
    }

    private static ViewSize Size(JsonElement element, string name)
    {
        var pair = element.GetProperty(name);
        return new ViewSize(pair[0].GetDouble(), pair[1].GetDouble());
    }

    private static ViewRect Box(JsonElement element, string name)
    {
        var box = element.GetProperty(name);
        var x0 = box[0].GetDouble();
        var y0 = box[1].GetDouble();
        return new ViewRect(x0, y0, box[2].GetDouble() - x0, box[3].GetDouble() - y0);
    }
}
