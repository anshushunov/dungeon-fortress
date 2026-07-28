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

    /// <summary>
    /// The gate. The snapshot publishes which tiles are rock, which are diggable,
    /// which may hold material and which may hold a blueprint, but not which one
    /// is the gate — and the gate is the one tile no zone may cover.
    ///
    /// So this is a copy of a simulation fact, which the rest of this layer
    /// deliberately never keeps. It is here rather than in the adapter because it
    /// used to be written out three separate times over there, and because a copy
    /// that a unit test holds to the simulation is the cheaper of the two evils.
    /// <c>MapBoundsTests</c> is that test.
    /// </summary>
    public static GridPoint Gate { get; } = new(27, 13);
}
