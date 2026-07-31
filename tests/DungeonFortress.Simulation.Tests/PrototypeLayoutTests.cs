using System.Text;
using System.Text.RegularExpressions;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// The authored dungeon, and the two things that have to stay true about it.
///
/// The first is the rule of fact applied to a picture: section 4.1 of
/// <c>docs/design/PROTOTYPE_01_PREPARE_FOR_RAID.md</c> does not illustrate the
/// layout, it **is** the layout, and this is what stops the two drifting. The
/// contract used to carry a diagram marked "illustrative, the feature table is
/// the truth", and by the time Issue #117 opened it it had drifted from the code
/// it was drawn for.
///
/// The second is that the map is a map a domain can live in: connected, with
/// every feature the default zones require, and with the tiles the shipped
/// command journals name still being what those journals expect.
/// </summary>
public sealed class PrototypeLayoutTests(ITestOutputHelper output)
{
    [Fact]
    public void The_contract_prints_the_layout_the_simulation_actually_builds()
    {
        var contract = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "docs", "design", "PROTOTYPE_01_PREPARE_FOR_RAID.md"));
        var block = Regex.Match(
            contract,
            @"```text\r?\n    0         1         2\r?\n    0123456789012345678901234567\r?\n(?<rows>(?: ?\d{1,2}  [^\r\n]*\r?\n)+)```",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));
        Assert.True(
            block.Success,
            "Section 4.1 of the contract no longer contains the layout block this test reads. " +
            "It is the source of the picture a human checks; if it moved, this test moves with it.");

        var printed = block.Groups["rows"].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Select(line => line[4..])
            .ToArray();

        Assert.Equal(PrototypeLayout.Rows.Count, printed.Length);
        for (var y = 0; y < printed.Length; y++)
        {
            Assert.True(
                string.Equals(PrototypeLayout.Rows[y], printed[y], StringComparison.Ordinal),
                $"row {y} of the contract's picture is\n  {printed[y]}\nand the simulation builds\n" +
                $"  {PrototypeLayout.Rows[y]}\nThe picture is the layout, not a drawing of it.");
        }
    }

    [Fact]
    public void The_layout_is_rectangular_walled_and_reachable_end_to_end()
    {
        Assert.Equal(PrototypeTuning.MapHeight, PrototypeLayout.Rows.Count);
        Assert.All(
            PrototypeLayout.Rows,
            row => Assert.Equal(PrototypeTuning.MapWidth, row.Length));

        var state = PrototypeScenario.Run(Log(), 1).State;
        var rock = state.Map.RockTiles.ToHashSet();
        var passable = Enumerable
            .Range(0, PrototypeTuning.MapHeight)
            .SelectMany(y => Enumerable
                .Range(0, PrototypeTuning.MapWidth)
                .Select(x => new GridPoint(x, y)))
            .Where(tile => !rock.Contains(tile))
            .ToHashSet();

        // The border holds the dungeon in, and the gate is the one way through it.
        var gate = new GridPoint(27, 13);
        Assert.All(
            passable.Where(tile =>
                tile.X == 0 || tile.Y == 0 ||
                tile.X == PrototypeTuning.MapWidth - 1 ||
                tile.Y == PrototypeTuning.MapHeight - 1),
            tile => Assert.Equal(gate, tile));

        var seen = new HashSet<GridPoint> { gate };
        var queue = new Queue<GridPoint>([gate]);
        while (queue.TryDequeue(out var current))
        {
            foreach (var offset in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
            {
                var next = new GridPoint(current.X + offset.Item1, current.Y + offset.Item2);
                if (passable.Contains(next) && seen.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        Assert.Equal(passable.Count, seen.Count);
        // Counted rather than subtracted from a remembered border size: the
        // border is 83 tiles of rock and one gate, and the first version of this
        // line subtracted 82, so the diagnostic printed 85 internal rock tiles
        // where there are 84.
        var internalRock = rock.Count(tile =>
            tile.X > 0 && tile.Y > 0 &&
            tile.X < PrototypeTuning.MapWidth - 1 && tile.Y < PrototypeTuning.MapHeight - 1);
        output.WriteLine($"passable={passable.Count} internal rock={internalRock}");
    }

    /// <summary>
    /// The reference distances of contract 4.5, asserted rather than quoted. They
    /// are the numbers a reader uses to judge whether the food chain is a chain
    /// and whether the raiders have a walk, and on the dungeon they are what says
    /// the walls were built around the existing economy rather than across it.
    /// </summary>
    [Fact]
    public void The_reference_distances_of_4_5_hold()
    {
        var report = new StringBuilder();
        var pairs = new (string Name, GridPoint[] From, GridPoint[] To, int Low, int High)[]
        {
            ("beds -> kitchen", Beds, Kitchens, 5, 14),
            ("beds -> larder", Beds, Larders, 9, 18),
            ("kitchen -> larder", Kitchens, Larders, 3, 4),
            ("larder -> gate", Larders, [Gate], 18, 19),
            ("bunks -> larder", Bunks, Larders, 9, 11),
            ("posts -> larder", Posts, Larders, 7, 9),
            ("posts -> gate", Posts, [Gate], 26, 28),
            ("bunks -> gate", Bunks, [Gate], 14, 17),
            ("beds -> gate", Beds, [Gate], 28, 37),
        };

        foreach (var (name, from, to, low, high) in pairs)
        {
            var spans = from.Select(tile => to.Min(target => Distance(tile, target))).ToArray();
            report.AppendLine($"{name} {spans.Min()}-{spans.Max()} (contract {low}-{high})");
            Assert.Equal(low, spans.Min());
            Assert.Equal(high, spans.Max());
        }

        output.WriteLine(report.ToString());
    }

    private static readonly GridPoint[] Beds = Read('m');
    private static readonly GridPoint[] Kitchens = Read('K');
    private static readonly GridPoint[] Larders = Read('L');
    private static readonly GridPoint[] Bunks = Read('q');
    private static readonly GridPoint[] Posts = Read('T');
    private static readonly GridPoint Gate = Read('G').Single();

    private static GridPoint[] Read(char legend) => PrototypeLayout.Read(legend);

    private static int Distance(GridPoint from, GridPoint to)
    {
        var seen = new Dictionary<GridPoint, int> { [from] = 0 };
        var queue = new Queue<GridPoint>([from]);
        while (queue.TryDequeue(out var current))
        {
            if (current == to)
            {
                return seen[current];
            }

            foreach (var offset in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
            {
                var next = new GridPoint(current.X + offset.Item1, current.Y + offset.Item2);
                if (next.X < 0 || next.Y < 0 ||
                    next.X >= PrototypeTuning.MapWidth || next.Y >= PrototypeTuning.MapHeight ||
                    seen.ContainsKey(next))
                {
                    continue;
                }

                var glyph = PrototypeLayout.Rows[next.Y][next.X];
                if (glyph is '#' or 'd')
                {
                    continue;
                }

                seen[next] = seen[current] + 1;
                queue.Enqueue(next);
            }
        }

        throw new InvalidOperationException($"({to.X},{to.Y}) cannot be reached from ({from.X},{from.Y}).");
    }

    private static PrototypeCommandLog Log() =>
        new("custom", PrototypeTuning.DefaultSeed, []);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DungeonFortress.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
