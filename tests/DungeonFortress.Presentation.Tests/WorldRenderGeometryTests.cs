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

    [Fact]
    public void Isolated_wall_geometry_maps_every_exposed_side_to_a_visible_segment()
    {
        var geometry = WallRenderGeometry.ForCell(
            new GridPoint(2, 3),
            WallTileVariant.Isolated,
            44);

        Assert.Equal(new ViewRect(88, 116, 44, 66), geometry.Bounds);
        Assert.Equal(new ViewRect(88, 116, 44, 44), geometry.Top);
        Assert.Equal(new ViewRect(88, 160, 44, 22), geometry.Facade);
        Assert.Collection(
            geometry.Strokes,
            stroke => Assert.Equal(
                new WallStroke(
                    new ViewPoint(88, 116),
                    new ViewPoint(132, 116),
                    WallStrokeKind.BrightEdge),
                stroke),
            stroke => Assert.Equal(
                new WallStroke(
                    new ViewPoint(88, 116),
                    new ViewPoint(88, 160),
                    WallStrokeKind.DarkEdge),
                stroke),
            stroke => Assert.Equal(
                new WallStroke(
                    new ViewPoint(132, 116),
                    new ViewPoint(132, 160),
                    WallStrokeKind.DarkEdge),
                stroke),
            stroke => Assert.Equal(
                new WallStroke(
                    new ViewPoint(88, 160),
                    new ViewPoint(132, 160),
                    WallStrokeKind.FacadeLip),
                stroke),
            stroke => Assert.Equal(
                new WallStroke(
                    new ViewPoint(88, 160),
                    new ViewPoint(88, 182),
                    WallStrokeKind.DarkEdge),
                stroke),
            stroke => Assert.Equal(
                new WallStroke(
                    new ViewPoint(132, 160),
                    new ViewPoint(132, 182),
                    WallStrokeKind.DarkEdge),
                stroke),
            stroke => Assert.Equal(
                new WallStroke(
                    new ViewPoint(88, 182),
                    new ViewPoint(132, 182),
                    WallStrokeKind.FacadeBottom),
                stroke));
        Assert.All(geometry.Strokes, stroke =>
        {
            AssertInsideOrOnEdge(geometry.Bounds, stroke.From);
            AssertInsideOrOnEdge(geometry.Bounds, stroke.To);
        });
    }

    [Fact]
    public void Wall_connected_to_the_south_has_no_front_facade_or_facade_strokes()
    {
        var geometry = WallRenderGeometry.ForCell(
            new GridPoint(2, 3),
            WallTileVariant.South,
            44);

        Assert.Equal(geometry.Top, geometry.Bounds);
        Assert.Null(geometry.Facade);
        Assert.DoesNotContain(
            geometry.Strokes,
            stroke => stroke.Kind is
                WallStrokeKind.FacadeLip or
                WallStrokeKind.FacadeBottom);
        Assert.All(geometry.Strokes, stroke =>
        {
            AssertInsideOrOnEdge(geometry.Bounds, stroke.From);
            AssertInsideOrOnEdge(geometry.Bounds, stroke.To);
        });
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

    private static void AssertInsideOrOnEdge(ViewRect bounds, ViewPoint point)
    {
        Assert.InRange(point.X, bounds.X, bounds.X + bounds.Width);
        Assert.InRange(point.Y, bounds.Y, bounds.Y + bounds.Height);
    }
}
