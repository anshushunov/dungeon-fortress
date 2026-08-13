using DungeonFortress.Presentation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #420 — <b>a localised wound has to be visible on the body</b>, not only
/// spelled out in words.
///
/// <para>This file is the measured half of that Issue. It starts with the channel
/// the previous slice already shipped — the limp — and asks the question the
/// Issue's criterion 5 asks: <em>how far apart, in world pixels of the working
/// zoom, do a limping body and a whole one actually draw?</em> The answer is
/// allowed to be "not far enough to read", and it is, which is why a mark on the
/// silhouette had to be added rather than the limp deepened.</para>
/// </summary>
public sealed class InjuryVisibilityTests(ITestOutputHelper output)
{
    /// <summary>
    /// The shipped grid, i.e. the working zoom of Issue #420's criteria: ADR 0008
    /// allows 32–48 and <c>scripts/run-game.ps1</c> defaults to this one.
    /// </summary>
    private const int WorkingTileSize = 40;

    /// <summary>
    /// How finely the two gait curves are compared. A hundredth of a cell is the
    /// same step the limp's own shape test sweeps at, and the separation below is
    /// a smooth function of the path, so the maximum is not something a finer
    /// sweep can move by a pixel.
    /// </summary>
    private const double SweepStep = 0.001;

    /// <summary>
    /// How far apart the two bodies are drawn, at the worst moment of the cycle,
    /// in world pixels of <paramref name="tileSize"/>.
    /// </summary>
    private static (double Separation, double Path) WidestGap(double limpDepth, int tileSize)
    {
        var scale = CameraView.WorldVisualScale(tileSize);
        var widest = 0.0;
        var at = 0.0;
        for (var path = 0.0; path <= BodyMotion.LimpPeriodCells + 1e-9; path += SweepStep)
        {
            // The limp is the ordinary bob multiplied by an envelope
            // (BodyMotion.BobOffsetRef), so a depth other than the shipped one is
            // expressed here the same way the runtime expresses the shipped one
            // rather than by re-deriving the curve.
            var whole = BodyMotion.BobOffsetRef(path, walking: true, limping: false);
            var limping = whole * (1.0 - (limpDepth *
                (1.0 - Math.Cos(Math.Tau * path / BodyMotion.LimpPeriodCells)) / 2.0));
            var gap = Math.Abs(limping - whole) * scale;
            if (gap > widest)
            {
                widest = gap;
                at = path;
            }
        }

        return (widest, at);
    }

    /// <summary>
    /// Criterion 5 of Issue #420, answered with a number instead of an opinion.
    ///
    /// <para><b>The limp separates the two bodies by 1.96 world pixels at the
    /// working zoom, and it cannot separate them by more than 3.27 whatever the
    /// depth is.</b> The envelope multiplies the ordinary bob, and the bob's whole
    /// height is <see cref="BodyMotion.BobHeightRef"/> = 1.8 reference px = 3.27
    /// world px at tile 40 — so the deepest limp expressible in this channel is
    /// "the bad step does not leave the ground at all", which
    /// <see cref="BodyMotion.LimpDepth"/>'s own docstring already refuses as «тело
    /// с одной ногой». The shipped 0.6 spends three fifths of that ceiling.</para>
    ///
    /// <para><b>What follows for this Issue.</b> 1.96 px is 3.2 % of the 61.82 px a
    /// body is drawn at, it exists only while the body is walking, and it is a
    /// difference between two moments of a cycle rather than a difference visible
    /// in any one frame — a captured screenshot draws every body at alpha 1, so a
    /// crowd frame shows none of it at all. The conclusion the Issue allows is
    /// therefore the conclusion the number supports: <b>the limp does not read,
    /// and deepening it is not what would make it read.</b> The mark on the
    /// silhouette is the channel that carries the wound, and this test exists so
    /// that the claim stays measured rather than remembered.</para>
    ///
    /// <para>The numbers are asserted, not merely printed, so that a future change
    /// to <see cref="BodyMotion.LimpDepth"/>, <see cref="BodyMotion.BobHeightRef"/>
    /// or the reference tile reddens here and has to restate the conclusion.</para>
    /// </summary>
    [Fact]
    public void The_limp_separates_the_two_gaits_by_less_than_two_world_pixels()
    {
        var shipped = WidestGap(BodyMotion.LimpDepth, WorkingTileSize);
        var ceiling = WidestGap(1.0, WorkingTileSize);
        var bodyHeight = CameraView.GoblinDrawSize(WorkingTileSize);

        output.WriteLine(
            $"LIMP-SEPARATION tile={WorkingTileSize} shipped={shipped.Separation:F4}px " +
            $"at path={shipped.Path:F2} cells; ceiling(depth=1)={ceiling.Separation:F4}px; " +
            $"body={bodyHeight:F2}px; shipped/body={shipped.Separation / bodyHeight:P2}");

        Assert.Equal(1.9636, shipped.Separation, 3);
        Assert.Equal(3.2727, ceiling.Separation, 3);
        Assert.Equal(61.82, bodyHeight, 2);

        // The whole channel is under a twentieth of the body it is drawn on, which
        // is the sentence "the limp does not read" as a comparison rather than as
        // an adjective.
        Assert.True(
            ceiling.Separation < bodyHeight / 15.0,
            $"the deepest limp this channel can express is {ceiling.Separation} px on a body " +
            $"drawn {bodyHeight} px tall, and the conclusion of Issue #420's criterion 5 was " +
            "written against that ratio.");
    }
}
