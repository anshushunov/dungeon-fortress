using System.Text.Json;

using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #420 — <b>the mark on the hurt part</b>, held to the three things it has
/// to be right about: it is there, it is on the right part, and it says how bad
/// the wound is.
///
/// <para>The anchors <see cref="InjuryMarks"/> states are measurements of the
/// shipped art, so the checks here compare them with that measurement rather than
/// with a second opinion: <c>evidence/420-part-anchors.json</c> is produced by
/// <c>evidence/420-measure-part-anchors.py</c> from the alpha channel of the
/// cutout PNGs, and it is read here the way <c>BodyRigTests</c> reads the shipped
/// rig file.</para>
/// </summary>
public sealed class InjuryMarkTests(ITestOutputHelper output)
{
    private const ulong OwnerSeed = 20_260_729UL;
    private const int LateInTheParty = 2_400;

    /// <summary>
    /// The measurement, as the committed evidence file states it. Reading the file
    /// rather than restating its numbers is the whole point: a second copy of the
    /// centroids in this test would be a second truth, and the failure mode would
    /// be silent — a mark drifting off the limb it names while every check stayed
    /// green.
    /// </summary>
    private static JsonElement Measurement { get; } = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(
            PresentationFixtures.FindRepositoryRoot(),
            "evidence",
            "420-part-anchors.json")))
        .RootElement;

    private static double[] Numbers(string rigPart, string field) =>
        Measurement
            .GetProperty("parts")
            .GetProperty(rigPart)
            .GetProperty("reference")
            .GetProperty(field)
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();

    /// <summary>
    /// <b>Every anchor is the measured centre of mass of its own part.</b> The four
    /// numbers in <see cref="InjuryMarks"/> are a copy of an art measurement, and
    /// this is what keeps the copy honest: change the art, re-run the script, and
    /// the constants have to move with it or this reddens.
    /// </summary>
    [Fact]
    public void Every_anchor_is_the_measured_centre_of_mass_of_its_part()
    {
        foreach (var part in BodyParts.All)
        {
            var measured = Numbers(InjuryMarks.RigPartOf(part), "centroid");
            var anchor = InjuryMarks.AnchorRef(part);
            output.WriteLine(
                $"ANCHOR {part} -> {InjuryMarks.RigPartOf(part)} " +
                $"stated=({anchor.X:F4}, {anchor.Y:F4}) measured=({measured[0]:F4}, {measured[1]:F4})");
            Assert.Equal(measured[0], anchor.X, 3);
            Assert.Equal(measured[1], anchor.Y, 3);
        }
    }

    /// <summary>
    /// <b>The mark of a part lands on that part's own pixels.</b> This is the half
    /// of the Issue that makes the mark carry information at all: the colour says
    /// how bad, the <em>place</em> says where, and a place that is not on the limb
    /// is a decoration.
    ///
    /// <para>Checked against the opaque bounding box each part actually occupies —
    /// an independent number from the same measurement, not the centroid restated —
    /// so swapping any two of the four anchors reddens the pair that was swapped
    /// and leaves the other two green. Verified as a mutant rather than asserted
    /// here: <c>evidence/420-mutants.json</c>.</para>
    /// </summary>
    [Fact]
    public void Every_mark_lands_inside_the_part_it_names()
    {
        foreach (var part in BodyParts.All)
        {
            var box = Numbers(InjuryMarks.RigPartOf(part), "bbox");
            var anchor = InjuryMarks.AnchorRef(part);
            output.WriteLine(
                $"INSIDE {part} anchor=({anchor.X:F3}, {anchor.Y:F3}) " +
                $"box=[{box[0]:F3}, {box[1]:F3}, {box[2]:F3}, {box[3]:F3}]");

            Assert.True(
                anchor.X >= box[0] && anchor.X <= box[2],
                $"the mark for {part} sits at x={anchor.X}, and the drawn pixels of " +
                $"{InjuryMarks.RigPartOf(part)} run from x={box[0]} to x={box[2]}.");
            Assert.True(
                anchor.Y >= box[1] && anchor.Y <= box[3],
                $"the mark for {part} sits at y={anchor.Y}, and the drawn pixels of " +
                $"{InjuryMarks.RigPartOf(part)} run from y={box[1]} to y={box[3]}.");
        }
    }

    /// <summary>
    /// Four marks have to be four <em>places</em>, or the position stops naming the
    /// part. The closest pair is measured and printed rather than assumed, and the
    /// bar is one whole mark: two discs whose centres are further apart than their
    /// own diameter cannot be mistaken for one another at a glance.
    /// </summary>
    [Fact]
    public void No_two_marks_can_be_taken_for_each_other()
    {
        var closest = double.MaxValue;
        var pair = string.Empty;
        foreach (var first in BodyParts.All)
        {
            foreach (var second in BodyParts.All.Where(other => other > first))
            {
                var a = InjuryMarks.AnchorRef(first);
                var b = InjuryMarks.AnchorRef(second);
                var distance = Math.Sqrt(
                    ((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
                if (distance < closest)
                {
                    closest = distance;
                    pair = $"{first}/{second}";
                }
            }
        }

        var diameter = 2.0 * InjuryMarks.RadiusRef;
        output.WriteLine(
            $"SPACING closest={closest:F3} ref px ({pair}); diameter={diameter:F3}; " +
            $"at tile 40 that is {closest * CameraView.WorldVisualScale(40):F2} world px apart");
        Assert.True(
            closest > diameter,
            $"the two closest marks ({pair}) are {closest} reference px apart and each is " +
            $"{diameter} across, so they touch.");
    }

    /// <summary>
    /// <b>Severity is saturation, and it is not lost on the way.</b> The owner chose
    /// the encoding; this is what holds the two colours to it — the same hue, and
    /// plainly more of it in the heavy one. A change that made the two colours equal
    /// (or told them apart by hue instead) reddens here.
    /// </summary>
    [Fact]
    public void A_heavy_wound_is_the_same_colour_with_more_of_it()
    {
        var heavy = Hsl(InjuryMarks.ColorOf(InjuryKind.Heavy));
        var light = Hsl(InjuryMarks.ColorOf(InjuryKind.Light));
        output.WriteLine(
            $"SEVERITY heavy={InjuryMarks.ColorOf(InjuryKind.Heavy)} " +
            $"h={heavy.Hue:F1} s={heavy.Saturation:P0} l={heavy.Lightness:P0} | " +
            $"light={InjuryMarks.ColorOf(InjuryKind.Light)} " +
            $"h={light.Hue:F1} s={light.Saturation:P0} l={light.Lightness:P0}");

        Assert.Equal(heavy.Hue, light.Hue, 1);
        Assert.True(
            heavy.Saturation > light.Saturation + 0.2,
            $"a heavy wound is drawn at saturation {heavy.Saturation} and a light one at " +
            $"{light.Saturation}; severity is supposed to be the difference between them.");

        // And the two are different marks in the first place, which the equality
        // above would not notice if both were the same string.
        Assert.NotEqual(
            InjuryMarks.ColorOf(InjuryKind.Heavy),
            InjuryMarks.ColorOf(InjuryKind.Light));
    }

    /// <summary>
    /// <b>Read off a played party, not off a hand-built creature.</b> What has to be
    /// true is that a party the owner can run produces bodies the frame tells apart:
    /// somebody wears marks, somebody wears none, and a body that wears them wears
    /// exactly one per hurt part — every part, not only the worst, because the
    /// caption already answers "the worst" and the body is where "what exactly"
    /// lives.
    /// </summary>
    [Fact]
    public void A_hurt_body_wears_one_mark_per_hurt_part_and_a_whole_one_wears_none()
    {
        var state = PrototypeScenario.Run(
            PresentationFixtures.LogOf("baseline") with { Seed = OwnerSeed },
            LateInTheParty).State;

        var marked = 0;
        var whole = 0;
        foreach (var creature in state.Creatures)
        {
            var marks = InjuryMarks.Of(creature);
            if (creature.Mode == CreatureMode.Downed)
            {
                // The anchors are places on a standing silhouette and the downed
                // pose is a body lying down, so a mark on it would float rather
                // than land. Stated here as a property of the answer, not left to
                // the party to happen to contain one.
                Assert.Empty(marks);
                continue;
            }
            output.WriteLine(
                $"{creature.Name}: injuries=[{string.Join(", ", creature.Injuries.Select(i => $"{i.Part}/{i.Severity}"))}] " +
                $"marks=[{string.Join(", ", marks.Select(m => $"{m.Part}{m.Color}"))}]");

            Assert.Equal(creature.Injuries.Count, marks.Count);
            foreach (var injury in creature.Injuries)
            {
                var mark = Assert.Single(marks, item => item.Part == injury.Part);
                Assert.Equal(injury.Severity, mark.Severity);
                Assert.Equal(InjuryMarks.ColorOf(injury.Severity), mark.Color);
                Assert.Equal(InjuryMarks.AnchorRef(injury.Part), mark.OffsetRef);
            }

            if (marks.Count > 0)
            {
                marked++;
            }
            else
            {
                whole++;
            }
        }

        Assert.True(marked > 0, "nobody in the owner's party wears a mark, so the frame says nothing.");
        Assert.True(whole > 0, "everybody in the owner's party wears one, so the mark marks nothing out.");

        // And the downed rule is a rule about something: a creature that is hurt
        // and on the ground wears nothing, whichever party it comes from.
        var floored = state.Creatures.First() with
        {
            Mode = CreatureMode.Downed,
            Injuries = [new PrototypeInjurySnapshot(BodyPart.Head, InjuryKind.Heavy)],
        };
        Assert.Empty(InjuryMarks.Of(floored));
        Assert.Single(InjuryMarks.Of(floored with { Mode = CreatureMode.Waiting }));
    }

    /// <summary>
    /// The half no pure value can prove: <b>the adapter actually draws it</b>, on
    /// the body's own frame, with every number taken from
    /// <see cref="InjuryMarks"/>.
    ///
    /// <para>Three separate claims, because three separate mistakes are possible and
    /// each of them draws a picture that compiles. Dropping the call from
    /// <c>DrawCreatureInformation</c> leaves a mark nobody ever sees; dropping
    /// <c>PushBodyPose</c> leaves the mark standing still while the limb it names
    /// walks away from it; and a literal colour or radius next to the engine call is
    /// a decision the "Pure .NET" job cannot see at all (ADR 0011), which is the
    /// class of defect <c>WorldDrawPassGuardTests</c> exists for.</para>
    /// </summary>
    [Fact]
    public void The_adapter_draws_the_mark_on_the_body_and_takes_every_number_from_the_policy()
    {
        var caller = AdapterSource.Body("DrawCreatureInformation");
        Assert.NotEmpty(AdapterSource.CalledRoutines(caller, ["DrawInjuryMarks"]));

        var body = AdapterSource.Body("DrawInjuryMarks");
        foreach (var required in new[]
                 {
                     "InjuryMarks.Of(",
                     "InjuryMarks.RadiusRef",
                     "InjuryMarks.RimWidthRef",
                     "InjuryMarks.RimColor",
                     "mark.Color",
                     "mark.OffsetRef",
                     "PushBodyPose(",
                     "ClearBodyPose(",
                 })
        {
            Assert.Contains(required, body, StringComparison.Ordinal);
        }

        // Two circles per mark: the fill and the rim on its edge. Asserted so that
        // losing the rim — which is what makes one mark out of green skin, a teal
        // tunic and a brown boot — is a red test and not a quieter picture.
        Assert.Equal(2, AdapterSource.CallsTo(body, "DrawCircle").Count);

        // And the routine is declared in the manifest with the pass and the reading
        // it actually has. WorldDrawPassGuardTests holds the manifest to the
        // adapter; this holds the entry to what the mark is.
        var declared = WorldDrawOrder.Find("DrawInjuryMarks");
        Assert.NotNull(declared);
        Assert.Equal(WorldDrawPass.Informational, declared!.Pass);
        Assert.Equal(OverlayMark.BodyState, declared.Mark);
    }

    private static (double Hue, double Saturation, double Lightness) Hsl(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
        var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
        var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2.0;
        if (Math.Abs(max - min) < 1e-9)
        {
            return (0.0, 0.0, lightness);
        }

        var delta = max - min;
        var saturation = lightness > 0.5
            ? delta / (2.0 - max - min)
            : delta / (max + min);
        var hue = max == r
            ? ((g - b) / delta) + (g < b ? 6.0 : 0.0)
            : max == g
                ? ((b - r) / delta) + 2.0
                : ((r - g) / delta) + 4.0;
        return (hue * 60.0, saturation, lightness);
    }
}
