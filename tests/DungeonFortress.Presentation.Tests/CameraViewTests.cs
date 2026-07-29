using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

public sealed class CameraViewTests
{
    private static readonly ViewRect WorldViewport = new(16, 144, 864, 520);
    private static readonly ViewSize FrameSize = new(1280, 720);

    [Fact]
    public void The_selected_tile_size_is_a_parameter_in_the_ADR_0008_range()
    {
        Assert.Equal(40, CameraView.DefaultTileSize);
        Assert.Equal(1120, CameraView.MapSize(CameraView.DefaultTileSize).Width);
        Assert.Equal(640, CameraView.MapSize(CameraView.DefaultTileSize).Height);
        Assert.Equal(CameraView.MinimumTileSize, CameraView.ValidateTileSize(32));
        Assert.Equal(CameraView.MaximumTileSize, CameraView.ValidateTileSize(48));
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraView.ValidateTileSize(31));
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraView.ValidateTileSize(49));
    }

    [Fact]
    public void The_same_cell_round_trips_at_every_zoom_and_shifted_camera_position()
    {
        var target = new GridPoint(14, 8);
        var world = CameraView.CellCenter(target, CameraView.DefaultTileSize);
        ViewPoint[] centers =
        [
            CameraView.MapCenter(CameraView.DefaultTileSize),
            new ViewPoint(600, 340),
            new ViewPoint(520, 300),
        ];

        foreach (var zoom in CameraView.ZoomLevels)
        {
            foreach (var center in centers)
            {
                var frame = new CameraFrame(center, zoom, WorldViewport, FrameSize);
                var screen = frame.WorldToScreen(world);

                Assert.Equal(world, frame.ScreenToWorld(screen));
                Assert.Equal(
                    target,
                    CameraView.ScreenToCell(frame, screen, CameraView.DefaultTileSize));
            }
        }
    }

    [Fact]
    public void A_HUD_point_is_not_map_input_even_when_the_inverse_transform_reaches_a_map_cell()
    {
        var frame = new CameraFrame(
            CameraView.MapCenter(CameraView.DefaultTileSize),
            1.0,
            WorldViewport,
            FrameSize);
        var hudPoint = new ViewPoint(900, 300);

        Assert.True(MapBounds.Contains(CameraView.WorldToCell(
            frame.ScreenToWorld(hudPoint),
            CameraView.DefaultTileSize)));
        Assert.Null(CameraView.ScreenToCell(frame, hudPoint, CameraView.DefaultTileSize));
    }

    [Fact]
    public void The_camera_node_offset_places_the_requested_world_center_in_the_world_view()
    {
        var center = new ViewPoint(560, 320);
        var frame = new CameraFrame(center, 0.75, WorldViewport, FrameSize);

        Assert.Equal(WorldViewport.Center, frame.WorldToScreen(center));
        Assert.Equal(center, frame.ScreenToWorld(WorldViewport.Center));
    }

    [Fact]
    public void The_furthest_zoom_out_level_fits_the_whole_default_map()
    {
        var frame = new CameraFrame(
            CameraView.MapCenter(CameraView.DefaultTileSize),
            CameraView.ZoomLevels[0],
            WorldViewport,
            FrameSize);
        var map = CameraView.MapSize(CameraView.DefaultTileSize);

        Assert.True(frame.VisibleWorldSize.Width >= map.Width);
        Assert.True(frame.VisibleWorldSize.Height >= map.Height);
    }

    [Fact]
    public void Existing_goblin_art_stays_readable_across_the_selected_zoom_range()
    {
        var worldSize = CameraView.GoblinDrawSize(CameraView.DefaultTileSize);
        var overviewPixels = worldSize * CameraView.ZoomLevels[0];
        var detailPixels = worldSize * CameraView.ZoomLevels[^1];

        Assert.InRange(overviewPixels, 18, 20);
        Assert.InRange(detailPixels, 72, 73);
        Assert.True(detailPixels < 96, "The 2x view must not upscale the 96px source.");
    }

    [Fact]
    public void A_larger_world_view_shows_more_world_at_the_same_zoom()
    {
        var center = CameraView.MapCenter(CameraView.DefaultTileSize);
        var ordinary = new CameraFrame(center, 1.0, WorldViewport, FrameSize);
        var large = new CameraFrame(
            center,
            1.0,
            new ViewRect(16, 144, 1184, 700),
            new ViewSize(1600, 900));

        Assert.True(large.VisibleWorldSize.Width > ordinary.VisibleWorldSize.Width);
        Assert.True(large.VisibleWorldSize.Height > ordinary.VisibleWorldSize.Height);
    }

    [Fact]
    public void Middle_drag_pans_in_world_units_at_each_zoom()
    {
        var center = new ViewPoint(560, 320);

        Assert.Equal(
            new ViewPoint(520, 340),
            CameraView.PanByScreenDelta(center, new ViewPoint(20, -10), 0.5));
        Assert.Equal(
            new ViewPoint(550, 325),
            CameraView.PanByScreenDelta(center, new ViewPoint(20, -10), 2.0));
    }

    [Fact]
    public void Camera_stops_when_the_visible_world_reaches_a_map_edge()
    {
        var visible = new ViewSize(800, 400);

        Assert.Equal(
            new ViewPoint(400, 200),
            CameraView.ClampCenterToMap(
                new ViewPoint(-10_000, -10_000),
                visible,
                CameraView.DefaultTileSize));
        Assert.Equal(
            new ViewPoint(720, 440),
            CameraView.ClampCenterToMap(
                new ViewPoint(10_000, 10_000),
                visible,
                CameraView.DefaultTileSize));
    }

    [Fact]
    public void An_axis_that_fits_the_whole_map_stays_centered()
    {
        Assert.Equal(
            new ViewPoint(560, 320),
            CameraView.ClampCenterToMap(
                new ViewPoint(10_000, -10_000),
                new ViewSize(1_200, 700),
                CameraView.DefaultTileSize));
    }

    [Fact]
    public void Arrow_pan_moves_by_whole_tiles()
    {
        Assert.Equal(
            new ViewPoint(440, 400),
            CameraView.MoveByTiles(
                new ViewPoint(560, 320),
                horizontalTiles: -3,
                verticalTiles: 2,
                CameraView.DefaultTileSize));
    }

    [Fact]
    public void Zoom_steps_are_discrete_and_clamped()
    {
        Assert.Equal(0.5, CameraView.StepZoom(0.5, -1));
        Assert.Equal(0.75, CameraView.StepZoom(0.5, 1));
        Assert.Equal(1.5, CameraView.StepZoom(1.0, 1));
        Assert.Equal(2.0, CameraView.StepZoom(2.0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraView.ValidateZoom(1.1));
    }
}
