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
    /// The grid and the cell are the same pixels they were before Issue #77, and
    /// the sprite is still centred horizontally on the render point. What the
    /// change moves is the size of the body — not the world, and not the ground
    /// the body stands on, which the next test measures.
    /// </summary>
    [Fact]
    public void Growing_the_body_moves_neither_the_grid_nor_the_cell_a_body_belongs_to()
    {
        var cell = new GridPoint(14, 8);

        foreach (var tileSize in new[] { 32, 40, 48 })
        {
            var centre = CameraView.CellCenter(cell, tileSize);

            // The grid: unchanged, and not a function of how large a body is.
            Assert.Equal(
                new ViewPoint((cell.X + 0.5) * tileSize, (cell.Y + 0.5) * tileSize),
                centre);
            Assert.Equal(cell, CameraView.WorldToCell(centre, tileSize));
            Assert.Equal(tileSize / 22.0, CameraView.WorldVisualScale(tileSize));

            var drawn = CameraView.GoblinDrawRect(centre, tileSize);

            Assert.Equal(centre.X, drawn.Center.X, 12);
            Assert.Equal(CameraView.GoblinDrawSize(tileSize), drawn.Height, 12);
            Assert.Equal(CameraView.GoblinDrawWidth(tileSize), drawn.Width, 12);
        }
    }

    /// <summary>
    /// The shape the connected pack is drawn in: 17:12, which is what
    /// <c>goblin_*_v2.png</c> is — 272x192 in all six states, re-measured for this
    /// change and recorded in <c>evidence/77-pack-before.json</c>.
    ///
    /// <para>
    /// The height is the one thing that does not move. It is
    /// <see cref="CameraView.GoblinDrawSize"/>, still the owner's 61.8 px at the
    /// shipped tile, because that is the canvas height the pack was authored for;
    /// the width is what the square was wrong about. Both ends are stated: the
    /// ratio, so that a body cannot be stretched, and the width in pixels at each
    /// tile size, so that the ratio cannot be right about a wrong body.
    /// </para>
    ///
    /// <para>
    /// 17/12 is written out rather than read from
    /// <see cref="CameraView.SpriteCanvasAspect"/>, for the same reason
    /// <see cref="A_body_is_drawn_at_the_owner_selected_170_percent_of_its_previous_size"/>
    /// writes out 1360/22: derived from the constant it guards, it would follow
    /// that constant anywhere and hold nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void A_body_is_drawn_in_the_seventeen_by_twelve_canvas_the_pack_was_authored_on()
    {
        Assert.Equal(272.0 / 192.0, CameraView.SpriteCanvasAspect, 12);
        Assert.Equal(17.0 / 12.0, CameraView.SpriteCanvasAspect, 12);

        foreach (var tileSize in new[] { 32, 40, 48 })
        {
            var centre = CameraView.CellCenter(new GridPoint(14, 8), tileSize);
            var drawn = CameraView.GoblinDrawRect(centre, tileSize);

            Assert.Equal(17.0 / 12.0, drawn.Width / drawn.Height, 12);
            Assert.NotEqual(drawn.Width, drawn.Height);
        }

        // The canvas is wider than it is tall, and the height is untouched: the
        // pack is connected at the size the owner picked, not at a new one.
        Assert.Equal(61.818182, CameraView.GoblinDrawSize(40), 6);
        Assert.Equal(87.575758, CameraView.GoblinDrawWidth(40), 6);
        Assert.Equal(70.060606, CameraView.GoblinDrawWidth(32), 6);
        Assert.Equal(105.090909, CameraView.GoblinDrawWidth(48), 6);

        // 87.55 is what docs/art/goblin-v2-provenance.md quotes, and it is 61.8
        // times 17/12 — the document's own rounding of the height carried through.
        // The code carries the ratio rather than that number, so the two agree to
        // the precision the document states and no further: the difference is
        // 0.026 px, which is where 61.8 stops being 61.81818.
        Assert.InRange(CameraView.GoblinDrawWidth(40), 87.55, 87.6);
    }

    /// <summary>
    /// The body grows upward out of the ground it stands on: at 170 % the drawn
    /// feet are on exactly the pixel they were on before Issue #77, at every tile
    /// size, and the whole of the growth goes up.
    ///
    /// <para>
    /// <b>«Where the feet are» is asked of two different packs, and that is the
    /// whole of this test.</b> The old body was a v1 sprite, whose last opaque row
    /// is 91 of 96 — content 92/96 down, read off all four PNGs rather than
    /// assumed. The body drawn today is a v2 sprite, whose last opaque row is 187
    /// of 192 in all six states — content 188/192 down, re-measured for this change
    /// and recorded in <c>evidence/77-pack-before.json</c>. So the two sides of the
    /// equation carry different fractions on purpose: each pack's own.
    /// </para>
    ///
    /// <para>
    /// Both are written out rather than read from
    /// <see cref="CameraView.SpriteSupportFraction"/>. Taken from the constant, the
    /// test would follow it to any value and would have stayed green through
    /// exactly the mistake this subtask was warned about: leaving the fraction at
    /// 92/96 while connecting a 188/192 pack, which draws every creature 1.29 px
    /// into the ground at the shipped tile.
    /// </para>
    ///
    /// <para>
    /// The rejected rules are kept here as numbers, because both were measured
    /// and one of them shipped for a round. A <b>centred</b> canvas would sink the
    /// feet from 16.67 px below the render centre to 29.62 — 12.95 px, 32 % of a
    /// 40 px cell, landing outside the cell the body stands on — and it is what
    /// the first round of Issue #77 shipped. Anchoring the canvas's <b>bottom
    /// edge</b> instead of the drawn feet is measured by
    /// <see cref="Anchoring_the_drawn_feet_and_the_canvas_disagree_by_a_measured_amount"/>;
    /// with the v1 pack the two rules were 1.06 px apart, and with this one they
    /// are 0.23.
    /// </para>
    /// </summary>
    [Fact]
    public void The_drawn_feet_do_not_move_when_the_body_grows()
    {
        // Each pack's own last opaque row, as a fraction of its own canvas height.
        const double v1Support = 92.0 / 96.0;
        const double v2Support = 188.0 / 192.0;

        foreach (var tileSize in new[] { 32, 40, 48 })
        {
            var centre = CameraView.CellCenter(new GridPoint(14, 8), tileSize);
            var sizeBefore = 20.0 * tileSize / 22.0;

            // Where the feet were: the pre-#77 v1 square, centred, 92/96 down.
            var before = (sizeBefore * v1Support) - (sizeBefore / 2.0);

            // Where they are: the v2 canvas as it is placed today, 188/192 down.
            var drawn = CameraView.GoblinDrawRect(centre, tileSize);
            var after = drawn.Y + (drawn.Height * v2Support) - centre.Y;

            Assert.Equal(before, after, 12);
            Assert.Equal(before, CameraView.GoblinFootLine(tileSize), 12);

            // Inside the cell, as they were: half a cell is tileSize/2.
            Assert.True(after < tileSize / 2.0);

            // And the growth goes upward: the canvas top rises by everything the
            // drawn canvas gained, less the part of it that is below the feet.
            var gained = CameraView.GoblinDrawSize(tileSize) - sizeBefore;
            var topBefore = centre.Y - (sizeBefore / 2.0);
            var tailBefore = sizeBefore * (1.0 - v1Support);
            var tailAfter = CameraView.GoblinDrawSize(tileSize) * (1.0 - v2Support);

            Assert.Equal(gained + tailBefore - tailAfter, topBefore - drawn.Y, 12);
        }
    }

    /// <summary>
    /// <b>The creature did not move up; its canvas did.</b> This is the number that
    /// separates the two, and the whole of the argument for measuring Issue #156's
    /// sweep against <see cref="CameraView.GoblinOpaqueRect"/> rather than against
    /// the drawn canvas.
    ///
    /// <para>
    /// The v1 sheet's body filled 84 of its 96 rows; the v2 canvas fills 168 of
    /// 192. Both are 0.875, which is not a coincidence — the pack was authored for
    /// it (<c>docs/art/goblin-v2-provenance.md</c>: «the body fills 168 of the 192
    /// rows»). So the topmost pixel any creature can have is in exactly the same
    /// place before and after, to the last binary place, while the canvas above it
    /// grew from 5.15 px of transparent header to 6.44.
    /// </para>
    ///
    /// <para>
    /// Sideways the honest answer is the opposite one and it is stated here too:
    /// 27.05 px each way with v1 against 42.82 with v2, because <c>combat</c> and
    /// <c>windup</c> hold a spear out. Any check that models a body by this
    /// rectangle therefore became 58 % <em>stricter</em> horizontally and did not
    /// move at all vertically — which is why the change of model is a change of
    /// unit and not a relaxation.
    /// </para>
    ///
    /// <para>
    /// The v1 numbers are written out, because the pack they were measured on is
    /// no longer loaded and a comparison against art nobody can re-read has to
    /// carry its own evidence: alpha bounds <c>x 6..89, y 8..91</c> over the four
    /// v1 states, against <c>x 26..268, y 20..187</c> over the six v2 states.
    /// </para>
    /// </summary>
    [Fact]
    public void The_pixels_a_creature_can_occupy_did_not_rise_when_the_canvas_did()
    {
        const double v1Canvas = 96.0;
        const double v1Support = 92.0 / 96.0;
        const double v1OpaqueTop = 8.0;
        const double v1OpaqueLeft = 6.0;
        const double v1OpaqueRight = 90.0;

        foreach (var tileSize in new[] { 32, 40, 48 })
        {
            var centre = CameraView.CellCenter(new GridPoint(14, 8), tileSize);
            var drawn = CameraView.GoblinDrawSize(tileSize);

            // The v1 pack as it would have been drawn today: same height, square,
            // feet on the same line, and its own alpha bounds.
            var v1CanvasTop = centre.Y + CameraView.GoblinFootLine(tileSize) - (drawn * v1Support);
            var v1OpaqueReach = centre.Y - (v1CanvasTop + (drawn * (v1OpaqueTop / v1Canvas)));

            var opaque = CameraView.GoblinOpaqueRect(centre, tileSize);
            var v2OpaqueReach = centre.Y - opaque.Y;

            Assert.Equal(v1OpaqueReach, v2OpaqueReach, 12);

            // And the canvas above those pixels really did grow, or the equality
            // above would be saying that nothing happened.
            var canvas = CameraView.GoblinDrawRect(centre, tileSize);
            Assert.True(centre.Y - canvas.Y > v2OpaqueReach);

            // Sideways: strictly wider than the pack it replaced.
            var v1HalfWidth = Math.Max(
                (drawn / 2.0) - (drawn * (v1OpaqueLeft / v1Canvas)),
                (drawn * (v1OpaqueRight / v1Canvas)) - (drawn / 2.0));
            var v2HalfWidth = Math.Max(centre.X - opaque.X, opaque.X + opaque.Width - centre.X);
            Assert.True(v2HalfWidth > v1HalfWidth);
        }

        Assert.Equal(37.424242, CameraView.CellCenter(new GridPoint(14, 8), 40).Y -
            CameraView.GoblinOpaqueRect(CameraView.CellCenter(new GridPoint(14, 8), 40), 40).Y, 6);
        Assert.Equal(42.821970, CameraView.GoblinDrawWidth(40) *
            ((CameraView.SpriteOpaqueRight - (CameraView.SpriteCanvasWidth / 2.0)) /
                CameraView.SpriteCanvasWidth), 6);

        // The opaque box is inside the canvas on every side, which is what makes it
        // a narrowing of the same rectangle rather than a different one.
        var probe = CameraView.CellCenter(new GridPoint(14, 8), 40);
        var box = CameraView.GoblinOpaqueRect(probe, 40);
        var frame = CameraView.GoblinDrawRect(probe, 40);
        Assert.True(box.X >= frame.X);
        Assert.True(box.Y >= frame.Y);
        Assert.True(box.X + box.Width <= frame.X + frame.Width);
        Assert.True(box.Y + box.Height <= frame.Y + frame.Height);

        // The feet stay in the box: the pack's support row is inside its own
        // opaque bounds, so a check that uses the box still sees a body on the
        // ground it stands on.
        Assert.Equal(
            probe.Y + CameraView.GoblinFootLine(40),
            frame.Y + (frame.Height * CameraView.SpriteSupportFraction),
            12);
        Assert.True(box.Y + box.Height >= probe.Y + CameraView.GoblinFootLine(40));
    }

    /// <summary>
    /// The ground under a creature is not a property of the creature's art, and
    /// this is the check that says so in numbers.
    ///
    /// <para>
    /// <see cref="CameraView.GoblinFootLine"/> was one expression with
    /// <see cref="CameraView.SpriteSupportFraction"/> while the game had one pack.
    /// Connecting the second one separates them: had the foot line followed the
    /// pack, it would have moved from 16.667 px below the render centre to 17.424
    /// — every creature in the game standing 0.758 px lower than the day before,
    /// at the shipped tile, because a canvas grew a shorter transparent tail.
    /// </para>
    ///
    /// <para>
    /// The rejected number is computed here rather than quoted, so that it stays
    /// the answer to «what if the foot line had followed the pack» even if the
    /// pack changes again.
    /// </para>
    /// </summary>
    [Fact]
    public void The_ground_a_body_stands_on_did_not_move_with_the_pack()
    {
        foreach (var tileSize in new[] { 32, 40, 48 })
        {
            var reference = 20.0 * tileSize / 22.0;
            var followedThePack = reference * (CameraView.SpriteSupportFraction - 0.5);

            Assert.Equal(reference * ((92.0 / 96.0) - 0.5), CameraView.GoblinFootLine(tileSize), 12);
            Assert.True(
                followedThePack > CameraView.GoblinFootLine(tileSize),
                "the v2 pack leaves less transparent tail than v1, so a foot line " +
                "built from it would sit lower — if this stops being true the " +
                "arithmetic below is measuring nothing.");
        }

        Assert.Equal(16.666667, CameraView.GoblinFootLine(40), 6);
        Assert.Equal(
            0.757576,
            (20.0 * 40.0 / 22.0 * (CameraView.SpriteSupportFraction - 0.5)) -
                CameraView.GoblinFootLine(40),
            6);
    }

    /// <summary>
    /// The two numbers that separate this placement rule from the one it was
    /// confused with, at the shipped tile size: how far a body reaches above its
    /// render centre, and by how much the two rules disagree.
    ///
    /// <para>
    /// <b>The gap between them is a property of the pack, and connecting v2
    /// shrank it and turned it round.</b> With the v1 pack, anchoring the drawn
    /// feet reached 42.58 px and anchoring the canvas reached 43.64 — the canvas
    /// rule 1.06 px higher, because a transparent tail of 4/96 grew with the
    /// square. The v2 canvas leaves 4/192, so the same two rules now reach 43.86
    /// and 43.64, and it is the <em>feet</em> rule that reaches 0.23 px higher.
    /// </para>
    ///
    /// <para>
    /// That the difference has become small is not an argument that it stopped
    /// mattering. The rule is chosen by what it holds still — the drawn feet, at
    /// 0.000000 px, which
    /// <see cref="The_drawn_feet_do_not_move_when_the_body_grows"/> measures —
    /// and not by how far the two candidate rules happen to be apart on the pack
    /// of the day.
    /// </para>
    /// </summary>
    [Fact]
    public void Anchoring_the_drawn_feet_and_the_canvas_disagree_by_a_measured_amount()
    {
        var centre = CameraView.CellCenter(new GridPoint(14, 8), 40);
        var drawn = CameraView.GoblinDrawRect(centre, 40);

        // This rule: the drawn feet held on their line.
        Assert.Equal(43.863636, centre.Y - drawn.Y, 6);
        Assert.Equal(17.954545, drawn.Y + drawn.Height - centre.Y, 6);

        // The rule it was confused with: the canvas's bottom edge held on the old
        // canvas's bottom edge, which is half the pre-#77 body below the centre.
        var canvasAnchored = CameraView.GoblinDrawSize(40) - (20.0 * 40.0 / 22.0 / 2.0);

        Assert.Equal(43.636364, canvasAnchored, 6);
        Assert.Equal(0.227273, (centre.Y - drawn.Y) - canvasAnchored, 6);

        // And the reason to prefer this one, restated as the cost of the other:
        // anchoring the canvas puts the drawn feet 0.23 px below their line.
        Assert.Equal(
            0.227273,
            (CameraView.GoblinDrawSize(40) * CameraView.SpriteSupportFraction) -
                canvasAnchored - CameraView.GoblinFootLine(40),
            6);
    }

    /// <summary>
    /// What the chosen scale asks of the art, at both ends of the zoom range.
    ///
    /// <para>
    /// The bound this test used to carry was «the 2x view must not upscale the
    /// 96 px source». At 170 % that stopped holding for the v1 pack — 61.8 x 2 =
    /// 123.6 px against 96 — and the body-scale subtask split it rather than
    /// deleting it: every zoom the game starts at inside the pack in use, and the
    /// deepest zoom pinned against the pack the scale was authored for. This
    /// subtask connected that pack, so the two halves close back into one: 123.6
    /// px now come out of a 192-row canvas, at 0.64 of its height, and no zoom in
    /// the range magnifies the source at all.
    /// </para>
    ///
    /// <para>
    /// The old sheet's 96 px stays here as the number the range is not measured
    /// against any more, because «the deepest zoom is inside the source» is a
    /// claim that was false for a while and is true again, and a check that
    /// forgets it was ever false cannot say which pack it is describing.
    /// </para>
    /// </summary>
    [Fact]
    public void The_selected_scale_states_what_it_asks_of_the_art_at_both_ends_of_the_zoom_range()
    {
        const double v1SourcePixels = 96.0;
        const double connectedCanvasPixels = 192.0;

        var worldSize = CameraView.GoblinDrawSize(CameraView.DefaultTileSize);
        var overviewPixels = worldSize * CameraView.ZoomLevels[0];
        var detailPixels = worldSize * CameraView.ZoomLevels[^1];

        Assert.InRange(overviewPixels, 30, 31);
        Assert.InRange(detailPixels, 123, 124);

        // The connected pack really is the one these numbers are about.
        Assert.Equal(connectedCanvasPixels, CameraView.SpriteCanvasHeight);
        Assert.Equal("v2", BodySprites.PackVersion);

        // Overview no longer shrinks a body below the 18 px it used to bottom
        // out at, which was the readability floor of the old scale.
        Assert.True(
            overviewPixels > 18,
            "The 0.5x overview must not make a body smaller than it was before Issue #77.");

        // Every zoom in the range, deepest included, takes fewer pixels than the
        // connected canvas has. This is the whole of what connecting v2 buys.
        foreach (var zoom in CameraView.ZoomLevels)
        {
            Assert.True(
                worldSize * zoom < connectedCanvasPixels,
                $"Zoom {zoom} draws {worldSize * zoom} px of canvas height from a " +
                $"{connectedCanvasPixels} px source.");
        }

        // The width has the same headroom, and it is asked separately because the
        // canvas is not square: 175.2 px at 2x from 272 source columns.
        var detailWidth = CameraView.GoblinDrawWidth(CameraView.DefaultTileSize) *
            CameraView.ZoomLevels[^1];
        Assert.InRange(detailWidth, 175, 176);
        Assert.True(detailWidth < CameraView.SpriteCanvasWidth);

        // And the number the range used to be measured against, kept as the
        // statement of what changed: the deepest zoom is past the old sheet.
        Assert.True(
            detailPixels > v1SourcePixels,
            "The 2x view asks for more than the retired 96 px v1 sheet held, which " +
            "is why the pack was connected.");
        Assert.Equal(1.2879, detailPixels / v1SourcePixels, 4);
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
