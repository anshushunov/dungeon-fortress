namespace DungeonFortress.Presentation;

/// <summary>
/// The ring that tells crew from raider on the map, in the three numbers the
/// adapter draws it with.
///
/// Issue #177: at the owner-chosen 170 % body scale the filled circle that used
/// to sit under each sprite was entirely covered by it (a ~32.7 px disc under a
/// 61.8 × 87.6 px canvas at the shipped tile), so a player could no longer tell
/// who was crew and who was raider. The fix draws a stroke ring AFTER the
/// sprite. The ring's radius and the two ring colours are the whole decision,
/// and they live here rather than in <c>Main.cs</c> for the same reason every
/// other rule in this folder does: <c>Main.cs</c> is not built by the "Pure
/// .NET" CI job (ADR 0011), so a value decided there is decided where nothing
/// can check it. The adapter reads the same constants the test does, and
/// <c>SideMarkerVisibilityTests</c> holds the geometry the visibility claim is
/// made of and reads the bodies of <c>DrawCreature</c>/<c>DrawRaider</c> to
/// make sure the ring is still drawn with them.
/// </summary>
public static class SideMarker
{
    /// <summary>
    /// The reference-pixel radius of the side ring, in the authored 22 px grid
    /// <see cref="CameraView"/> scales from. Picked just beyond half the sprite
    /// canvas width at the shipped tile so the ring never sits on the sprite
    /// again: <c>GoblinDrawWidth(40) / 2 ≈ 43.8</c> and
    /// <c>ScaleWorld(27) ≈ 49.1</c>, leaving a clear gap around every pose in
    /// the pack's opaque envelope.
    /// </summary>
    public const float RingRadiusRef = 27f;

    /// <summary>
    /// The ring colour for crew, teal. The legend promises "teal ring = crew".
    /// </summary>
    public const string CrewRingColor = "#14b8a6";

    /// <summary>
    /// The ring colour for raiders, red. The legend promises
    /// "red ring = raider".
    /// </summary>
    public const string RaiderRingColor = "#dc2626";
}
