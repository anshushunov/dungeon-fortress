using System.Globalization;

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
    public void Camera_frame_places_a_known_world_point_at_an_independently_calculated_pixel()
    {
        var frame = new CameraFrame(
            new ViewPoint(560, 320),
            0.75,
            WorldViewport,
            FrameSize);

        // These coordinates are worked from the camera equation, not from the
        // inverse method under test. Moving the Camera2D node by even one pixel
        // makes the engine smoke disagree with the same fixed expectation.
        Assert.Equal(new ViewPoint(463, 419), frame.WorldToScreen(new ViewPoint(580, 340)));
        Assert.Equal(new ViewPoint(580, 340), frame.ScreenToWorld(new ViewPoint(463, 419)));
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

    /// <summary>
    /// The owner's decision of 2026-08-01 on spike #142, in the only units it
    /// can be checked in: 61.8 px of body at the shipped 40 px tile.
    ///
    /// <para>
    /// The expectation is written as the whole number the gate log names, not as
    /// <c>ReferenceGoblinDrawSize * CameraView.BodyVisualScale * …</c>. Derived
    /// from the constant it guards, it would move with any edit of that constant
    /// and prove nothing; written out, changing 1.70 to anything else turns this
    /// test red.
    /// </para>
    /// </summary>
    [Fact]
    public void A_body_is_drawn_at_the_owner_selected_170_percent_of_its_previous_size()
    {
        // 1360 / 22: 20 reference px of body, taken to 170 %, carried onto the
        // 40 px grid by the same 40/22 every other world primitive uses.
        Assert.Equal(1360.0 / 22.0, CameraView.GoblinDrawSize(40), 12);
        Assert.Equal(61.8, CameraView.GoblinDrawSize(40), 1);

        // The same decision restated as the ratio it was made as. 800/22 is the
        // pre-#77 size the gate log calls 36.4 px, and it is spelled out here
        // rather than recovered from CameraView for the same reason as above.
        Assert.Equal(800.0 / 22.0, 36.36, 2);
        Assert.Equal(1.70, CameraView.GoblinDrawSize(40) / (800.0 / 22.0), 12);

        // Tile size stays a free parameter of ADR 0008: the factor applies at
        // every grid in the supported range, and only the grid changes the answer.
        Assert.Equal(1.70, CameraView.GoblinDrawSize(32) / (20.0 * 32.0 / 22.0), 12);
        Assert.Equal(1.70, CameraView.GoblinDrawSize(48) / (20.0 * 48.0 / 22.0), 12);
        Assert.Equal(49.45, CameraView.GoblinDrawSize(32), 2);
        Assert.Equal(74.18, CameraView.GoblinDrawSize(48), 2);
    }

    /// <summary>
    /// A body grows around the point it stands on. The square <c>DrawGoblin</c>
    /// draws into is centred on the render centre, so the foot pivot — the cell
    /// centre a body is placed at — is the same pixel before and after Issue #77,
    /// and the cell it belongs to is unchanged.
    /// </summary>
    [Fact]
    public void Growing_the_body_moves_neither_the_grid_nor_the_point_a_body_stands_on()
    {
        var cell = new GridPoint(14, 8);

        foreach (var tileSize in new[] { 32, 40, 48 })
        {
            var centre = CameraView.CellCenter(cell, tileSize);

            Assert.Equal(
                new ViewPoint((cell.X + 0.5) * tileSize, (cell.Y + 0.5) * tileSize),
                centre);
            Assert.Equal(cell, CameraView.WorldToCell(centre, tileSize));
            Assert.Equal(tileSize / 22.0, CameraView.WorldVisualScale(tileSize));

            // The square Main.DrawGoblin builds — top-left at centre minus half
            // the draw size, side equal to it — is still centred on that same
            // point, so the sprite spills equally in all four directions and the
            // body's anchor does not drift with the scale.
            var size = CameraView.GoblinDrawSize(tileSize);
            var drawn = new ViewRect(
                centre.X - (size / 2.0),
                centre.Y - (size / 2.0),
                size,
                size);

            Assert.Equal(centre, drawn.Center);
            Assert.Equal(size, drawn.Width);
            Assert.Equal(size, drawn.Height);
        }
    }

    /// <summary>
    /// What the chosen scale asks of the art, at both ends of the zoom range.
    ///
    /// <para>
    /// The bound this test used to carry was «the 2x view must not upscale the
    /// 96 px source», and at 170 % it no longer holds for the v1 pack the
    /// runtime loads: 61.8 x 2 = 123.6 px. That is a fact about the pack, not
    /// about the decision, and it is the entry condition of the next subtask of
    /// Issue #77: the v2 pack already in <c>main</c> is 272x192 and was authored
    /// for exactly this 61.8 px canvas height
    /// (<c>docs/art/goblin-v2-provenance.md</c>). So the bound is not deleted,
    /// it is split — every zoom the game actually starts at stays inside the
    /// pack in use, and the deepest zoom is pinned against the pack the scale
    /// was authored for.
    /// </para>
    /// </summary>
    [Fact]
    public void The_selected_scale_states_what_it_asks_of_the_art_at_both_ends_of_the_zoom_range()
    {
        const double v1SourcePixels = 96.0;
        const double v2CanvasPixels = 192.0;

        var worldSize = CameraView.GoblinDrawSize(CameraView.DefaultTileSize);
        var overviewPixels = worldSize * CameraView.ZoomLevels[0];
        var detailPixels = worldSize * CameraView.ZoomLevels[^1];

        Assert.InRange(overviewPixels, 30, 31);
        Assert.InRange(detailPixels, 123, 124);

        // Overview no longer shrinks a body below the 18 px it used to bottom
        // out at, which was the readability floor of the old scale.
        Assert.True(
            overviewPixels > 18,
            "The 0.5x overview must not make a body smaller than it was before Issue #77.");

        // 1x, 0.75x and 0.5x — every zoom a run can start at — still take fewer
        // pixels than the loaded v1 sheet has.
        foreach (var zoom in CameraView.ZoomLevels.Where(level => level <= 1.0))
        {
            Assert.True(
                worldSize * zoom < v1SourcePixels,
                $"Zoom {zoom} draws {worldSize * zoom} px from a {v1SourcePixels} px source.");
        }

        // Only the deepest zoom asks for more than the v1 sheet holds, and it
        // stays inside the canvas the 170 % pack was drawn on.
        Assert.True(detailPixels > v1SourcePixels);
        Assert.True(
            detailPixels < v2CanvasPixels,
            "The 2x view must stay inside the 272x192 canvas the v2 pack was authored on.");
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
    public void Camera_focus_stops_on_the_centers_of_the_edge_tiles()
    {
        Assert.Equal(
            new ViewPoint(20, 20),
            CameraView.ClampCenterToMap(
                new ViewPoint(-10_000, -10_000),
                CameraView.DefaultTileSize));
        Assert.Equal(
            new ViewPoint(1_100, 620),
            CameraView.ClampCenterToMap(
                new ViewPoint(10_000, 10_000),
                CameraView.DefaultTileSize));
    }

    [Fact]
    public void Overview_pan_is_not_cancelled_when_the_whole_map_fits()
    {
        var panned = CameraView.PanByScreenDelta(
            CameraView.MapCenter(CameraView.DefaultTileSize),
            new ViewPoint(40, -20),
            CameraView.ZoomLevels[0]);

        Assert.Equal(
            new ViewPoint(480, 360),
            CameraView.ClampCenterToMap(
                panned,
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

    [Fact]
    public void The_startup_frame_policy_holds_over_every_display_it_names()
    {
        CameraView.AssertStartupFramePolicy(ViewLaunchOptions.MinimumLogicalFrameSize);
    }

    [Fact]
    public void The_startup_frame_policy_fails_when_a_frame_stops_leaving_enough_logical_room()
    {
        // The first of two directions. Until PR #110 there was no test project
        // that could reference this at all, so the only evidence the guard was
        // alive was a count printed next to it — which is exactly the kind of
        // evidence Issue #86 asked to be replaced with this.
        var failure = Assert.Throws<InvalidOperationException>(
            () => CameraView.AssertStartupFramePolicy(new ViewSize(4000, 3000)));

        Assert.Contains("logical pixels", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_startup_frame_policy_fails_when_the_minimum_stops_rejecting_anything()
    {
        // The other direction: a minimum so small that the smallest window this
        // policy can open would pass at the largest scale. The guard's own
        // closing clause has to notice.
        var failure = Assert.Throws<InvalidOperationException>(
            () => CameraView.AssertStartupFramePolicy(new ViewSize(1, 1)));

        Assert.Contains("accepted", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shrinking_window_never_keeps_the_automatic_scale_of_the_larger_one()
    {
        // The resize sequence a review measured on the owner's screen, plus a
        // frame under the minimum logical rectangle at the end. That last one is
        // the defect: the adapter used to ask for a scale, find that the pair
        // did not fit, and leave the previous larger scale in place, so a window
        // dragged small halved the HUD's logical area a second time.
        var minimum = ViewLaunchOptions.MinimumLogicalFrameSize;
        (ViewSize Frame, double Scale)[] sequence =
        [
            (new ViewSize(2764, 1641), 2.0),
            (new ViewSize(3072, 1779), 2.0),
            (new ViewSize(1774, 1229), 1.25),
            (new ViewSize(1000, 700), 1.0),
            (new ViewSize(1280, 720), 1.0),
        ];

        foreach (var (frame, expected) in sequence)
        {
            Assert.Equal(expected, CameraView.AutomaticUiScale(frame, minimum));
        }

        // And the property that makes the sequence above impossible to break by
        // forgetting a branch: the scale is a function of the frame alone, so
        // there is no earlier value for a resize to keep.
        Assert.Equal(
            CameraView.AutomaticUiScale(new ViewSize(1000, 700), minimum),
            CameraView.AutomaticUiScale(new ViewSize(1000, 700), minimum));
    }

    [Fact]
    public void The_startup_zoom_shows_the_whole_map_in_the_world_viewport_it_is_given()
    {
        var map = CameraView.MapSize(CameraView.DefaultTileSize);

        for (var width = 400.0; width <= 3000.0; width += 37.0)
        {
            var viewport = new ViewSize(width, width * 0.6);
            var zoom = CameraView.AutomaticZoom(viewport, CameraView.DefaultTileSize);

            Assert.Contains(zoom, CameraView.ZoomLevels);
            if (zoom > CameraView.ZoomLevels[0])
            {
                Assert.True(
                    map.Width * zoom <= viewport.Width + 1e-9 &&
                        map.Height * zoom <= viewport.Height + 1e-9,
                    $"Zoom {zoom} pushes the map outside a {viewport.Width}x{viewport.Height} " +
                    "world viewport.");
                // And it is the largest such level, so a bigger window really
                // does draw a bigger world instead of leaving it in a corner.
                var next = CameraView.StepZoom(zoom, 1);
                Assert.True(
                    next == zoom ||
                        map.Width * next > viewport.Width + 1e-9 ||
                        map.Height * next > viewport.Height + 1e-9,
                    $"Zoom {zoom} is not the largest level a {viewport.Width}x{viewport.Height} " +
                    "world viewport allows.");
            }
        }
    }

    [Fact]
    public void The_startup_zoom_grows_with_the_window_instead_of_staying_at_one()
    {
        // Issue #86, second half. The world viewports below are the rectangles
        // the HUD reserves at 1280x720 and at the owner's maximized 3044x1722:
        // the launcher used to draw the map at 1:1 in both, so on the large one
        // a 1120x640 map sat in the middle of a viewport twice its size.
        var baseline = CameraView.AutomaticZoom(new ViewSize(864, 520), CameraView.DefaultTileSize);
        var maximized = CameraView.AutomaticZoom(
            new ViewSize(2212, 1322),
            CameraView.DefaultTileSize);

        Assert.Equal(0.75, baseline);
        Assert.Equal(1.5, maximized);
        Assert.True(maximized > baseline);
    }

    [Fact]
    public void A_world_viewport_smaller_than_the_map_falls_back_to_the_overview_level()
    {
        Assert.Equal(
            CameraView.ZoomLevels[0],
            CameraView.AutomaticZoom(new ViewSize(320, 200), CameraView.DefaultTileSize));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CameraView.AutomaticZoom(
                new ViewSize(double.NaN, 200),
                CameraView.DefaultTileSize));
    }

    [Fact]
    public void The_player_keeps_a_zoom_they_chose_and_the_HUD_scale_keeps_following_the_window()
    {
        // Two adapter rules that cannot be expressed as values, checked as the
        // structure of the routines that own them: the wheel turns the automatic
        // zoom off for good, and a resize re-derives both.
        var wheel = AdapterSource.Body("StepCameraZoom");
        Assert.Contains("_cameraZoomIsAutomatic = false", wheel, StringComparison.Ordinal);

        var automatic = AdapterSource.Body("ApplyAutomaticCameraZoom");
        Assert.Contains("_cameraZoomIsAutomatic", automatic, StringComparison.Ordinal);
        Assert.Contains("CameraView.AutomaticZoom", automatic, StringComparison.Ordinal);

        var resize = AdapterSource.Body("OnViewportResized");
        Assert.Single(AdapterSource.CallsTo(resize, "ApplyAutomaticCameraZoom"));
        Assert.Contains("CameraView.AutomaticUiScale", resize, StringComparison.Ordinal);
    }

    [Fact]
    public void View_validation_messages_do_not_depend_on_the_process_culture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");

            var failure = Assert.Throws<ArgumentOutOfRangeException>(
                () => CameraView.ValidateZoom(1.1));

            Assert.Contains("0.5, 0.75, 1, 1.5, 2", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("0,5", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
