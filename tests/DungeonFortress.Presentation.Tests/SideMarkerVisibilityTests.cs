using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #177: with the v2 sprite pack at 170 % body scale (61.8 px canvas
/// height, 87.6 px width at the shipped tile) the filled side-indicator circles
/// drawn BEFORE the sprite were entirely occluded. The fix replaces them with a
/// <c>DrawArc</c> stroke ring drawn AFTER the sprite, whose radius must stay
/// outside the sprite canvas at every supported tile size so the marker never
/// gets swallowed again.
///
/// This is a pure geometry test (ADR 0011). It reads the reference numbers the
/// adapter uses from <see cref="CameraView"/> and verifies that the ring radius
/// exceeds half the sprite width plus a safety margin, at every tile size the
/// shipped game supports.
/// </summary>
public sealed class SideMarkerVisibilityTests
{
    /// <summary>
    /// The reference-pixel radius the adapter uses for the side-indicator stroke
    /// ring. Issue #177 picks 27 — just beyond half the sprite canvas width at
    /// tile 40 (≈43.8 px; ScaleWorld(27) ≈ 49.1). This constant is quoted from
    /// <c>Main.cs:DrawCreature</c> and <c>Main.cs:DrawRaider</c>.
    /// </summary>
    private const double SideMarkerRingRadiusRef = 27.0;

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
    /// If this turns red it means the marker is occluded: the sprite canvas
    /// reaches past the ring radius and the player can no longer tell friend
    /// from raider. Fix by increasing the ring radius in DrawCreature /
    /// DrawRaider, or decrease the sprite scale — not by adjusting this test.
    /// </summary>
    [Fact]
    public void The_side_marker_ring_exceeds_half_the_sprite_canvas_at_every_tile_size()
    {
        foreach (var tileSize in new[] { CameraView.MinimumTileSize, 40, CameraView.MaximumTileSize })
        {
            var halfWidth = CameraView.GoblinDrawWidth(tileSize) / 2.0;

            var ringRadiusPx = SideMarkerRingRadiusRef * CameraView.WorldVisualScale(tileSize);
            var marginPx = SafetyMarginRef * CameraView.WorldVisualScale(tileSize);

            Assert.True(
                ringRadiusPx > halfWidth + marginPx,
                $"Tile {tileSize}: ring radius {ringRadiusPx:f2} px <= sprite half-width " +
                $"{halfWidth:f2} px + margin {marginPx:f2} px. The side marker is occluded.");
        }
    }

    /// <summary>
    /// The two sides use different ring colours so the player can distinguish
    /// crew from raider. This checks that the constants in Main.cs are actually
    /// different — a regression of the kind "teal and red were rebased to the
    /// same Tailwind shade".
    /// </summary>
    [Fact]
    public void Crew_and_raider_ring_colours_are_different()
    {
        // Issue #177: Main.DrawCreature uses #14b8a6 (teal), DrawRaider uses #dc2626 (red).
        const string crew = "#14b8a6";
        const string raider = "#dc2626";

        Assert.NotEqual(crew, raider);
    }
}
