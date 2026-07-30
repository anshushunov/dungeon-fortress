using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

public sealed class WallTopologyTests
{
    private static readonly GridPoint Center = new(4, 4);

    public static TheoryData<WallNeighbors, WallTileVariant, byte> EveryNeighborhood
    {
        get
        {
            var data = new TheoryData<WallNeighbors, WallTileVariant, byte>
            {
                { WallNeighbors.None, WallTileVariant.Isolated, 0 },
                { WallNeighbors.North, WallTileVariant.North, 1 },
                { WallNeighbors.East, WallTileVariant.East, 2 },
                { WallNeighbors.North | WallNeighbors.East, WallTileVariant.NorthEast, 3 },
                { WallNeighbors.South, WallTileVariant.South, 4 },
                { WallNeighbors.North | WallNeighbors.South, WallTileVariant.NorthSouth, 5 },
                { WallNeighbors.East | WallNeighbors.South, WallTileVariant.EastSouth, 6 },
                { WallNeighbors.North | WallNeighbors.East | WallNeighbors.South, WallTileVariant.NorthEastSouth, 7 },
                { WallNeighbors.West, WallTileVariant.West, 8 },
                { WallNeighbors.North | WallNeighbors.West, WallTileVariant.NorthWest, 9 },
                { WallNeighbors.East | WallNeighbors.West, WallTileVariant.EastWest, 10 },
                { WallNeighbors.North | WallNeighbors.East | WallNeighbors.West, WallTileVariant.NorthEastWest, 11 },
                { WallNeighbors.South | WallNeighbors.West, WallTileVariant.SouthWest, 12 },
                { WallNeighbors.North | WallNeighbors.South | WallNeighbors.West, WallTileVariant.NorthSouthWest, 13 },
                { WallNeighbors.East | WallNeighbors.South | WallNeighbors.West, WallTileVariant.EastSouthWest, 14 },
                { WallNeighbors.North | WallNeighbors.East | WallNeighbors.South | WallNeighbors.West, WallTileVariant.Surrounded, 15 },
            };
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryNeighborhood))]
    public void Every_orthogonal_neighborhood_selects_one_stable_variant(
        WallNeighbors neighbors,
        WallTileVariant expected,
        byte stableValue)
    {
        var rock = new HashSet<GridPoint> { Center };
        AddIfConnected(rock, neighbors, WallNeighbors.North, new GridPoint(4, 3));
        AddIfConnected(rock, neighbors, WallNeighbors.East, new GridPoint(5, 4));
        AddIfConnected(rock, neighbors, WallNeighbors.South, new GridPoint(4, 5));
        AddIfConnected(rock, neighbors, WallNeighbors.West, new GridPoint(3, 4));

        var variant = WallTopology.SelectVariant(Center, rock);

        Assert.Equal(expected, variant);
        Assert.Equal(stableValue, (byte)variant);
        Assert.Equal(
            !neighbors.HasFlag(WallNeighbors.South),
            WallTopology.HasFrontFacade(variant));
        Assert.Equal(
            ((WallNeighbors)15 & ~neighbors),
            WallTopology.ExposedSides(variant));
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

    [Fact]
    public void Compound_connection_side_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WallTopology.Connects(
                WallTileVariant.Surrounded,
                WallNeighbors.North | WallNeighbors.East));
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
