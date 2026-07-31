namespace DungeonFortress.Simulation;

/// <summary>
/// The authored dungeon of Prototype 1, and the only place its geometry is
/// written down.
///
/// It used to be a rule in code — "everything is floor except the border" —
/// plus six pillars and a pocket of rock, which is a hall and not a dungeon.
/// Issue #117 needs the difference: a creature can only be seen avoiding a
/// place if the place is separated from the one next to it, and on open floor
/// there is nothing to avoid. So the layout became a picture, because a picture
/// is the form a human can check and the form the design contract can carry
/// unchanged.
///
/// Legend: <c>#</c> rock, <c>.</c> floor, <c>m</c> mushroom bed, <c>K</c>
/// kitchen station, <c>L</c> larder tile, <c>q</c> bunk, <c>T</c> training
/// post, <c>G</c> gate, <c>d</c> the quarry face of the top-right pocket the
/// shipped demo fixtures dig. <c>d</c> is rock and behaves exactly like
/// <c>#</c>; it is spelled differently so that the corner those fixtures name
/// by coordinate can be found in the picture.
///
/// The rooms, west to east and north to south: the farm hall with all eight
/// beds, the north store, the kitchen, the larder, the quarters with the quarry
/// niche behind them, the east chamber, the cellar, the gym, the south chamber
/// and the gate hall. They are joined by the spine at <c>y = 10</c>, the
/// northern link at <c>y = 2</c>, the farm link at <c>y = 4</c> and single-tile
/// doors.
///
/// Section 4.1 of <c>docs/design/PROTOTYPE_01_PREPARE_FOR_RAID.md</c> prints
/// the same picture, and
/// <c>The_contract_prints_the_layout_the_simulation_actually_builds</c> keeps
/// the two equal. The contract used to say its own diagram was illustrative and
/// that the feature table was the truth; there is now one truth and it is this
/// array.
/// </summary>
public static class PrototypeLayout
{
    /// <summary>
    /// One string per row, top to bottom. Every row is
    /// <see cref="PrototypeTuning.MapWidth"/> characters long and there are
    /// <see cref="PrototypeTuning.MapHeight"/> of them; both are asserted by
    /// <c>PrototypeLayoutTests</c> rather than assumed.
    /// </summary>
    public static IReadOnlyList<string> Rows => Authored;

    private static readonly string[] Authored =
    [
        "############################",
        "#.m..m.##.....#####......dd#",
        "#......##.TT.............dd#",
        "#.m..m.##.TT..#####.qq...dd#",
        "#.............#####..qq.####",
        "#.m..m.####.#######......###",
        "#......##.........###......#",
        "#.m..m....KK..LL...........#",
        "#..........................#",
        "###..#####..###..####......#",
        "#..........................#",
        "###.###....##.####.##......#",
        "#.....#....#........#......#",
        "#.....#....#........#......G",
        "#.....#....#........#......#",
        "############################",
    ];

    /// <summary>
    /// The tiles carrying one legend character, in reading order — top to
    /// bottom, then left to right. Reading order is load-bearing rather than a
    /// convenience: bed ripeness is offset by index (contract 3.3) and the
    /// raiders walk to the first larder tile.
    /// </summary>
    public static GridPoint[] Read(char legend)
    {
        var tiles = new List<GridPoint>();
        for (var y = 0; y < Authored.Length; y++)
        {
            for (var x = 0; x < Authored[y].Length; x++)
            {
                if (Authored[y][x] == legend)
                {
                    tiles.Add(new GridPoint(x, y));
                }
            }
        }

        return [.. tiles];
    }
}
