using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The rig, read from the file the runtime reads.
///
/// <para>
/// This is the same method <see cref="UiIconManifestTests"/> uses on the icon
/// pack and for the same reason: a contract is only a contract while something
/// compares it with the thing it describes. The pivots belong to Issue #243's
/// art and its provenance forbids retyping them, so what a test can hold is that
/// the runtime reads them, that the order it draws them in is the order the file
/// states, and that a file the runtime cannot draw is refused rather than drawn
/// wrong.
/// </para>
/// </summary>
public sealed class BodyRigTests
{
    private static string RigPath() => Path.Combine(
        PresentationFixtures.FindRepositoryRoot(),
        "src",
        "DungeonFortress.Game",
        "assets",
        "generated",
        "goblins",
        "cutout_v1",
        BodyRig.FileName);

    private static BodyRig Shipped() => BodyRig.Parse(File.ReadAllText(RigPath()));

    /// <summary>
    /// The file is where the runtime says it is, and it parses. Without this every
    /// check below would be a check about a file nobody delivered.
    /// </summary>
    [Fact]
    public void The_shipped_rig_file_is_the_one_the_runtime_draws()
    {
        Assert.True(File.Exists(RigPath()), $"{RigPath()} is the rig the adapter loads.");

        var rig = Shipped();
        Assert.Equal(BodyRig.LayerOrder.Count, rig.Parts.Count);
        Assert.Equal(new ViewSize(512, 512), rig.SourceCellSize);

        // Every part names a PNG that is actually next to the rig, because a part
        // whose file is missing is a hole in a body rather than a failed load.
        var folder = Path.GetDirectoryName(RigPath())!;
        foreach (var part in rig.Parts)
        {
            Assert.True(
                File.Exists(Path.Combine(folder, part.File)),
                $"Part '{part.Name}' names '{part.File}', which is not beside the rig.");
        }

        // The root is the torso and every other part hangs off something.
        Assert.Equal(
            BodyRig.RootPart,
            Assert.Single(rig.Parts.Where(part => part.Parent is null)).Name);
    }

    /// <summary>
    /// <b>The layer order is the rig's own, and this is the check the order
    /// mutant of this Issue runs into.</b>
    ///
    /// <para>
    /// <see cref="BodyRig.LayerOrder"/> is what the adapter walks, and it is
    /// written out by name so a human can read what is in front of what. That
    /// spelling is only safe while something compares it with the depth the art
    /// was cut at: swapping any two names draws an arm inside a chest or a spear
    /// behind the body holding it, which compiles, draws a whole fight and looks
    /// like a rendering bug rather than a wrong list.
    /// </para>
    /// </summary>
    [Fact]
    public void The_layer_order_is_the_rig_s_own_back_to_front_order()
    {
        var rig = Shipped();

        Assert.Equal(
            rig.Parts.OrderBy(part => part.ZIndex).Select(part => part.Name).ToArray(),
            BodyRig.LayerOrder);

        // And it really is a depth: no two parts share a z_index, so the order
        // above is a list rather than a tie broken by whoever sorted last.
        Assert.Equal(
            rig.Parts.Count,
            rig.Parts.Select(part => part.ZIndex).Distinct().Count());

        // The three readings the order is chosen for, stated as facts about the
        // list rather than as prose in a comment: the far side is behind the
        // trunk, the near side in front of it, and the weapon in front of
        // everything because the strike hand carries it.
        var depth = BodyRig.LayerOrder.ToArray();
        Assert.True(Array.IndexOf(depth, "leg_far") < Array.IndexOf(depth, BodyRig.RootPart));
        Assert.True(Array.IndexOf(depth, "arm_far") < Array.IndexOf(depth, BodyRig.RootPart));
        Assert.True(Array.IndexOf(depth, BodyRig.RootPart) < Array.IndexOf(depth, "leg_near"));
        Assert.True(Array.IndexOf(depth, "head") < Array.IndexOf(depth, "arm_near"));
        Assert.Equal(depth.Length - 1, Array.IndexOf(depth, BodyRig.WeaponPart));
    }

    /// <summary>
    /// A rig the runtime cannot draw is refused. A guard never seen to fail is
    /// not evidence, so each of the four refusals is made to fire.
    /// </summary>
    [Fact]
    public void A_rig_this_runtime_cannot_draw_is_refused()
    {
        var shipped = File.ReadAllText(RigPath());

        // A part the layer order draws and the rig does not have.
        Assert.Throws<ArgumentException>(() =>
            BodyRig.Parse(shipped.Replace("\"head\"", "\"skull\"", StringComparison.Ordinal)));

        // A parent that is not a part.
        Assert.Throws<ArgumentException>(() =>
            BodyRig.Parse(shipped.Replace(
                "\"parent\": \"torso\"",
                "\"parent\": \"spine\"",
                StringComparison.Ordinal)));

        // A depth order that is not the one this runtime draws in.
        Assert.Throws<ArgumentException>(() =>
            BodyRig.Parse(shipped
                .Replace("\"z_index\": 5", "\"z_index\": 9", StringComparison.Ordinal)
                .Replace("\"z_index\": 4", "\"z_index\": 5", StringComparison.Ordinal)
                .Replace("\"z_index\": 9", "\"z_index\": 4", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The rig lands inside the rectangle the flat pack was drawn into, which is
    /// what makes swapping one body for the other a change of body and not a
    /// change of scale, of place or of the ground it stands on.
    ///
    /// <para>
    /// The provenance of Issue #243 states the target in as many words — "the
    /// runtime target remains 116x168 inside the unchanged 272x192/170%
    /// presentation boundary" — and the two anchors are measurements of the flat
    /// pack that <see cref="CameraView"/> already owns.
    /// </para>
    /// </summary>
    [Fact]
    public void The_body_lands_where_the_flat_pack_stood()
    {
        var rig = Shipped();

        Assert.Equal(new ViewSize(116, 168), rig.RuntimeTargetSize);

        // The height of the target box is exactly the rows the flat pack fills:
        // the first row any state has a pixel in, down to the row its feet end on.
        Assert.Equal(
            rig.RuntimeTargetSize.Height,
            BodyRig.CanvasBottom - BodyRig.CanvasTop,
            10);

        // The body box maps onto that target: top-left on the top-left corner,
        // bottom-right on the bottom-right, to a tenth of a canvas pixel.
        var topLeft = rig.CanvasPointOf(new ViewPoint(
            rig.SourceBodyBox.X,
            rig.SourceBodyBox.Y));
        var bottomRight = rig.CanvasPointOf(new ViewPoint(
            rig.SourceBodyBox.X + rig.SourceBodyBox.Width,
            rig.SourceBodyBox.Y + rig.SourceBodyBox.Height));

        Assert.Equal(BodyRig.CanvasTop, topLeft.Y, 10);
        Assert.Equal(BodyRig.CanvasBottom, bottomRight.Y, 10);
        Assert.Equal(rig.CanvasLeft, topLeft.X, 10);

        // Horizontally centred on the canvas, which is where the flat pack's own
        // support centre is: GoblinDrawRect puts it on the render point.
        Assert.Equal(
            CameraView.SpriteCanvasWidth - bottomRight.X,
            topLeft.X,
            10);

        // One scale, not two. A part turned under a non-uniform scale stops being
        // the shape it was drawn as, and the rig's own two ratios differ in the
        // fourth digit because the builder rounded a target size to whole pixels.
        // Within a whole canvas pixel of the width the rig declares: the builder
        // rounded 116 and 168 independently, so one scale cannot reproduce both to
        // the last place, and 0.07 px of canvas is 0.02 world px at the shipped
        // tile.
        Assert.Equal(
            rig.RuntimeTargetSize.Width,
            rig.SourceBodyBox.Width * rig.SourceToCanvas,
            0);
    }

    /// <summary>
    /// Which states the rig draws, and which keep a flat sprite. Both lists are
    /// states the connected pack actually has, so a typo here is a body that
    /// silently keeps the old drawing rather than a missing texture.
    /// </summary>
    [Fact]
    public void The_rig_draws_the_states_of_the_blow_and_no_others()
    {
        Assert.All(
            BodyRig.RiggedStates,
            state => Assert.Contains(state, BodySprites.States));
        Assert.All(
            BodyRig.ArmedStates,
            state => Assert.Contains(state, BodyRig.RiggedStates));

        // The Issue's non-goals, as a fact about the list: work and downed are not
        // converted, and the weapon is hidden in the rest pose because the rig
        // says so.
        Assert.DoesNotContain("work", BodyRig.RiggedStates);
        Assert.DoesNotContain("downed", BodyRig.RiggedStates);
        Assert.DoesNotContain("idle", BodyRig.ArmedStates);
        Assert.False(Shipped().Part(BodyRig.WeaponPart).VisibleInRest);
    }
}
