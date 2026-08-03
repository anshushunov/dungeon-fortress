using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #177: with the v2 sprite pack at 170 % body scale (61.8 px canvas
/// height, 87.6 px width at the shipped tile) the filled side-indicator circles
/// drawn BEFORE the sprite were entirely occluded, and a player could no longer
/// tell crew from raider. The fix draws a <c>DrawArc</c> stroke ring AFTER the
/// sprite, whose radius must stay outside the sprite canvas at every supported
/// tile size so the marker never gets swallowed again.
///
/// This file holds that claim executable (ADR 0011 — the engine is not run, the
/// adapter is read). Two halves:
///
/// <list type="bullet">
/// <item>the geometry half reads the ring's radius from
/// <see cref="SideMarker.RingRadiusRef"/> — the one constant <c>Main.cs</c>
/// draws the ring with — and checks the ring stays outside the sprite at every
/// supported tile size;</item>
/// <item>the structural half reads the bodies of <c>Main.DrawCreature</c> and
/// <c>Main.DrawRaider</c> as text (the same reader
/// <see cref="WorldDrawPassGuardTests"/> uses) and requires each to draw the
/// ring with the shared radius and its side's colour, after the sprite. If the
/// ring is dropped or moved back under the sprite — the mutation Issue #177's
/// brief assigns — this turns red.</item>
/// </list>
/// </summary>
public sealed class SideMarkerVisibilityTests
{
    /// <summary>
    /// A one-reference-pixel safety margin. The ring must sit past the sprite
    /// edge, not merely touch it — antialiasing and stroke width eat into
    /// proximity fast enough that a bare &gt; is not sufficient.
    /// </summary>
    private const double SafetyMarginRef = 1.0;

    /// <summary>
    /// The side-indicator ring stays outside the sprite at every tile size the
    /// shipped game supports: 32, 40 and 48 px (the ADR 0008 range).
    ///
    /// The radius is read from <see cref="SideMarker.RingRadiusRef"/>, so this
    /// test and the adapter draw with the same value — a shrink of the radius
    /// in <c>Main.cs</c> is a shrink of the radius this test measures.
    ///
    /// If this turns red the marker is occluded: the sprite canvas reaches past
    /// the ring radius and the player can no longer tell friend from raider.
    /// Fix by increasing the ring radius in SideMarker, or decreasing the sprite
    /// scale — not by adjusting this test.
    /// </summary>
    [Fact]
    public void The_side_marker_ring_exceeds_half_the_sprite_canvas_at_every_tile_size()
    {
        foreach (var tileSize in new[] { CameraView.MinimumTileSize, 40, CameraView.MaximumTileSize })
        {
            var halfWidth = CameraView.GoblinDrawWidth(tileSize) / 2.0;

            var ringRadiusPx = SideMarker.RingRadiusRef * CameraView.WorldVisualScale(tileSize);
            var marginPx = SafetyMarginRef * CameraView.WorldVisualScale(tileSize);

            Assert.True(
                ringRadiusPx > halfWidth + marginPx,
                $"Tile {tileSize}: ring radius {ringRadiusPx:f2} px <= sprite half-width " +
                $"{halfWidth:f2} px + margin {marginPx:f2} px. The side marker is occluded.");
        }
    }

    /// <summary>
    /// The structural half. <c>Main.DrawCreature</c> and <c>Main.DrawRaider</c>
    /// must each draw the side ring: exactly one <c>DrawArc</c>, whose radius is
    /// <see cref="SideMarker.RingRadiusRef"/> through
    /// <c>ScaleWorld(SideMarker.RingRadiusRef)</c>, filled with that side's ring
    /// colour, and placed AFTER <c>DrawGoblin</c> — a ring under the sprite is
    /// the defect Issue #177 exists.
    ///
    /// This is what makes the brief's mutation fail: an edit that reverts
    /// <c>Main.cs</c> to the pre-fix <c>DrawCircle</c>-under-sprite removes the
    /// ring call entirely, and this test goes red.
    /// </summary>
    [Fact]
    public void DrawCreature_and_DrawRaider_still_draw_the_side_marker_ring()
    {
        AssertSideRing(
            "DrawCreature",
            $"SideMarker.{nameof(SideMarker.CrewRingColor)}");
        AssertSideRing(
            "DrawRaider",
            $"SideMarker.{nameof(SideMarker.RaiderRingColor)}");
    }

    /// <summary>
    /// The two sides use different ring colours so the player can distinguish
    /// crew from raider. The colours are read from <see cref="SideMarker"/> —
    /// the constants <c>Main.cs</c> passes to the ring calls — and held to the
    /// teal/red the legend promises. A rebase of the two hues onto one Tailwind
    /// shade, or a swap between the sides, turns this red.
    /// </summary>
    [Fact]
    public void The_ring_colours_are_the_documented_teal_and_red()
    {
        Assert.Equal("#14b8a6", SideMarker.CrewRingColor);
        Assert.Equal("#dc2626", SideMarker.RaiderRingColor);
        Assert.NotEqual(SideMarker.CrewRingColor, SideMarker.RaiderRingColor);
    }

    /// <summary>
    /// One routine's side ring: its body has exactly one <c>DrawArc</c>, the
    /// radius argument is the shared <see cref="SideMarker.RingRadiusRef"/>
    /// through <c>ScaleWorld</c>, the colour argument is the routine's own side
    /// colour, and the ring is drawn after the sprite.
    /// </summary>
    private static void AssertSideRing(string routine, string ringColourMember)
    {
        var body = AdapterSource.Body(routine);

        var rings = AdapterSource.CallsTo(body, "DrawArc");
        Assert.True(
            rings.Count == 1,
            $"{routine} must draw the side-marker ring exactly once; found " +
            $"{rings.Count} DrawArc call(s). The ring is the team cue of Issue #177.");

        var ring = rings[0];
        // DrawArc(center, radius, startAngle, endAngle, pointCount, colour, width).
        Assert.Equal(
            $"ScaleWorld(SideMarker.{nameof(SideMarker.RingRadiusRef)})",
            ring.Arguments[1]);
        Assert.Contains(
            ringColourMember,
            ring.Arguments[5],
            StringComparison.Ordinal);

        // A ring drawn before the sprite is the defect Issue #177 removed.
        Assert.True(
            body.IndexOf("DrawArc(", StringComparison.Ordinal) >
            body.IndexOf("DrawGoblin(", StringComparison.Ordinal),
            $"{routine} draws the side ring before the sprite, so the sprite " +
            "occludes it again.");
    }
}
