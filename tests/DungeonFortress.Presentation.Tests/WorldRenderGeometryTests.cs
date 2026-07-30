using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

public sealed class WorldRenderGeometryTests
{
    [Fact]
    public void Body_on_a_structure_cell_is_drawn_above_the_structure()
    {
        var cell = new GridPoint(4, 3);
        var structure = WorldRenderGeometry.ForCell(
            WorldRenderKind.Structure,
            10,
            cell,
            CameraView.DefaultTileSize);
        var body = WorldRenderGeometry.ForBody(
            WorldRenderKind.Creature,
            20,
            CameraView.CellCenter(cell, CameraView.DefaultTileSize));

        Assert.Equal([structure, body], WorldRenderOrder.BackToFront([body, structure]));
    }

    [Fact]
    public void Body_north_of_a_wall_is_behind_it()
    {
        var wallCell = new GridPoint(4, 4);
        var wall = WorldRenderGeometry.ForCell(
            WorldRenderKind.Wall,
            10,
            wallCell,
            CameraView.DefaultTileSize);
        var body = WorldRenderGeometry.ForBody(
            WorldRenderKind.Creature,
            20,
            CameraView.CellCenter(new GridPoint(4, 3), CameraView.DefaultTileSize));

        Assert.Equal([body, wall], WorldRenderOrder.BackToFront([wall, body]));
    }

    [Fact]
    public void Body_south_of_a_wall_is_in_front_of_it()
    {
        var wallCell = new GridPoint(4, 4);
        var wall = WorldRenderGeometry.ForCell(
            WorldRenderKind.Wall,
            10,
            wallCell,
            CameraView.DefaultTileSize);
        var body = WorldRenderGeometry.ForBody(
            WorldRenderKind.Creature,
            20,
            CameraView.CellCenter(new GridPoint(4, 5), CameraView.DefaultTileSize));

        Assert.Equal([wall, body], WorldRenderOrder.BackToFront([body, wall]));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(27, 0)]
    [InlineData(0, 13)]
    [InlineData(27, 13)]
    public void Cell_ids_round_trip_across_the_map(int x, int y)
    {
        var cell = new GridPoint(x, y);

        var stableId = GridCellId.Encode(cell, PrototypeTuning.MapWidth);

        Assert.Equal(cell, GridCellId.Decode(stableId, PrototypeTuning.MapWidth));
    }

    [Fact]
    public void Cell_id_rejects_a_coordinate_outside_its_row()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GridCellId.Encode(
                new GridPoint(PrototypeTuning.MapWidth, 0),
                PrototypeTuning.MapWidth));
    }
}
