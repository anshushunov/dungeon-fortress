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

    /// <summary>
    /// All sixteen variants, checked against <see cref="WallTopology.ExposedSides"/>
    /// rather than against a list of expected strokes.
    ///
    /// The third review round of Issue #83 left exactly this hole: two variants
    /// were covered and both had North and West exposed at the same time, so
    /// swapping the two conditions inside the geometry changed nothing either test
    /// could see. Here every variant states how many strokes of each kind it must
    /// have and which edge each one lies on, so North and West are no longer
    /// interchangeable — and neither are East and West, or the top edges and the
    /// facade ones.
    /// </summary>
    [Theory]
    [MemberData(nameof(WallTopologyTests.EveryNeighborhood), MemberType = typeof(WallTopologyTests))]
    public void Every_wall_variant_draws_the_segments_its_exposed_sides_require(
        WallNeighbors neighbors,
        WallTileVariant variant,
        byte stableValue)
    {
        Assert.Equal(stableValue, (byte)variant);
        const int TileSize = 44;
        var cell = new GridPoint(2, 3);
        var exposed = WallTopology.ExposedSides(variant);
        var hasFacade = exposed.HasFlag(WallNeighbors.South);
        Assert.Equal(hasFacade, WallTopology.HasFrontFacade(variant));
        Assert.Equal(((WallNeighbors)15 & ~neighbors), exposed);

        var geometry = WallRenderGeometry.ForCell(cell, variant, TileSize);

        var left = geometry.Top.X;
        var right = geometry.Top.X + TileSize;
        var top = geometry.Top.Y;
        var bottom = geometry.Bounds.Y + geometry.Bounds.Height;
        var lip = hasFacade ? geometry.Facade!.Value.Y : double.NaN;

        // How many of each kind the exposed sides ask for. A facade adds its own
        // lip and bottom, plus one more vertical stroke on each exposed side,
        // because the facade hangs below the top mass.
        Assert.Equal(
            exposed.HasFlag(WallNeighbors.North) ? 1 : 0,
            Count(geometry, WallStrokeKind.BrightEdge));
        Assert.Equal(hasFacade ? 1 : 0, Count(geometry, WallStrokeKind.FacadeLip));
        Assert.Equal(hasFacade ? 1 : 0, Count(geometry, WallStrokeKind.FacadeBottom));
        Assert.Equal(
            (exposed.HasFlag(WallNeighbors.West) ? (hasFacade ? 2 : 1) : 0) +
            (exposed.HasFlag(WallNeighbors.East) ? (hasFacade ? 2 : 1) : 0),
            Count(geometry, WallStrokeKind.DarkEdge));

        foreach (var stroke in geometry.Strokes)
        {
            AssertInsideOrOnEdge(geometry.Bounds, stroke.From);
            AssertInsideOrOnEdge(geometry.Bounds, stroke.To);
            switch (stroke.Kind)
            {
                case WallStrokeKind.BrightEdge:
                    AssertHorizontal(stroke, top, left, right);
                    break;
                case WallStrokeKind.FacadeLip:
                    AssertHorizontal(stroke, lip, left, right);
                    break;
                case WallStrokeKind.FacadeBottom:
                    AssertHorizontal(stroke, bottom, left, right);
                    break;
                case WallStrokeKind.DarkEdge:
                    Assert.Equal(stroke.From.X, stroke.To.X);
                    Assert.True(
                        stroke.From.X == left || stroke.From.X == right,
                        $"{variant}: a dark edge at x={stroke.From.X} lies on neither side.");
                    Assert.True(
                        stroke.From.X != left || exposed.HasFlag(WallNeighbors.West),
                        $"{variant}: a west edge is drawn although west is connected.");
                    Assert.True(
                        stroke.From.X != right || exposed.HasFlag(WallNeighbors.East),
                        $"{variant}: an east edge is drawn although east is connected.");
                    break;
                default:
                    throw new InvalidOperationException($"unhandled stroke {stroke.Kind}");
            }
        }

        if (!hasFacade)
        {
            Assert.Null(geometry.Facade);
            Assert.Equal(geometry.Top, geometry.Bounds);
        }
    }

    private static int Count(WallVisualMass geometry, WallStrokeKind kind) =>
        geometry.Strokes.Count(stroke => stroke.Kind == kind);

    private static void AssertHorizontal(WallStroke stroke, double y, double left, double right)
    {
        Assert.Equal(y, stroke.From.Y);
        Assert.Equal(y, stroke.To.Y);
        Assert.Equal(left, Math.Min(stroke.From.X, stroke.To.X));
        Assert.Equal(right, Math.Max(stroke.From.X, stroke.To.X));
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
