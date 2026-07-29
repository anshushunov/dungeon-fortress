namespace DungeonFortress.Presentation;

/// <summary>
/// Elevated world primitives participating in one Y-order. Ground paint, routes
/// and selection overlays deliberately stay outside this list.
/// </summary>
public enum WorldRenderKind
{
    Creature,
    Raider,
    Structure,
    Wall,
}

/// <param name="Kind">Which adapter drawing routine owns the item.</param>
/// <param name="StableId">A deterministic adapter reference used only as a tie-break.</param>
/// <param name="X">Rendered X coordinate, after interpolation when applicable.</param>
/// <param name="Y">Rendered depth anchor, after interpolation when applicable.</param>
public readonly record struct WorldRenderItem(
    WorldRenderKind Kind,
    int StableId,
    double X,
    double Y);

/// <summary>
/// Pure painter's-order rule for three-quarter rendering. The adapter supplies
/// rendered coordinates, so moving bodies cross a wall at their interpolated Y
/// rather than jumping draw layers at a simulation tick.
/// </summary>
public static class WorldRenderOrder
{
    public static IReadOnlyList<WorldRenderItem> BackToFront(
        IEnumerable<WorldRenderItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var materialized = items.ToArray();
        foreach (var item in materialized)
        {
            if (!double.IsFinite(item.X) || !double.IsFinite(item.Y))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(items),
                    item,
                    "Render coordinates must be finite.");
            }
        }

        return
        [
            .. materialized
                .OrderBy(item => item.Y)
                .ThenBy(item => TiePriority(item.Kind))
                .ThenBy(item => item.X)
                .ThenBy(item => item.StableId),
        ];
    }

    private static int TiePriority(WorldRenderKind kind) => kind switch
    {
        WorldRenderKind.Creature => 0,
        WorldRenderKind.Raider => 1,
        WorldRenderKind.Structure => 2,
        // At the exact crossing depth the wall still occludes the body. It moves
        // in front only after its interpolated anchor has actually passed.
        WorldRenderKind.Wall => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
