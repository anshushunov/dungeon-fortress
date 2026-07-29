using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

public sealed class WallTopologyTests
{
    private static readonly GridPoint Center = new(4, 4);

    public static TheoryData<WallNeighbors> EveryNeighborhood
    {
        get
        {
            var data = new TheoryData<WallNeighbors>();
            foreach (var value in Enumerable.Range(0, 16))
            {
                data.Add((WallNeighbors)value);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryNeighborhood))]
    public void Every_orthogonal_neighborhood_selects_one_stable_variant(WallNeighbors expected)
    {
        var rock = new HashSet<GridPoint> { Center };
        AddIfConnected(rock, expected, WallNeighbors.North, new GridPoint(4, 3));
        AddIfConnected(rock, expected, WallNeighbors.East, new GridPoint(5, 4));
        AddIfConnected(rock, expected, WallNeighbors.South, new GridPoint(4, 5));
        AddIfConnected(rock, expected, WallNeighbors.West, new GridPoint(3, 4));

        var variant = WallTopology.SelectVariant(Center, rock);

        Assert.Equal((WallTileVariant)expected, variant);
        Assert.Equal(
            !expected.HasFlag(WallNeighbors.South),
            WallTopology.HasFrontFacade(variant));
    }

    [Fact]
    public void Diagonal_rock_does_not_join_an_isolated_wall()
    {
        HashSet<GridPoint> rock =
        [
            Center,
            new GridPoint(3, 3),
            new GridPoint(5, 3),
            new GridPoint(3, 5),
            new GridPoint(5, 5),
        ];

        Assert.Equal(WallTileVariant.Isolated, WallTopology.SelectVariant(Center, rock));
    }

    [Fact]
    public void Map_edge_is_an_exposed_border_not_an_invented_neighbor()
    {
        var corner = new GridPoint(0, 0);
        HashSet<GridPoint> rock =
        [
            corner,
            new GridPoint(1, 0),
            new GridPoint(0, 1),
        ];

        Assert.Equal(WallTileVariant.EastSouth, WallTopology.SelectVariant(corner, rock));
    }

    [Fact]
    public void Selecting_a_variant_for_floor_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(
            () => WallTopology.SelectVariant(Center, new HashSet<GridPoint>()));

        Assert.Contains("not a rock tile", error.Message, StringComparison.Ordinal);
    }

    private static void AddIfConnected(
        ISet<GridPoint> rock,
        WallNeighbors neighbors,
        WallNeighbors side,
        GridPoint point)
    {
        if (neighbors.HasFlag(side))
        {
            rock.Add(point);
        }
    }
}
