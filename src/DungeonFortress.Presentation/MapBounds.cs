using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// Whether a cell is on the map at all. The adapter needs this in two unrelated
/// places — hit testing a pointer and validating <c>--select-cell</c> — and only
/// the second one is engine-free, so the rule itself lives here and the pointer
/// path calls into it.
/// </summary>
public static class MapBounds
{
    public static bool Contains(GridPoint cell) =>
        cell.X is >= 0 and < PrototypeTuning.MapWidth &&
        cell.Y is >= 0 and < PrototypeTuning.MapHeight;
}
