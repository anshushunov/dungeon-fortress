using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

// The frame the game is shown in: window size, UI scale, the camera and
// the mapping between a point on screen and a cell in the world.
public partial class Main
{
    // ---------------------------------------------------------------------
    // HUD layout
    //
    // The HUD used to be four Labels authored at absolute pixels inside a fixed
    // 960x540 frame. Three of them lost text on every frame, and the summary
    // rectangle (18, 42, 620, 45) overlapped the time toolbar at y=74, so the
    // resource line was drawn over the 1x/4x/16x buttons. Neither is a font-size
    // problem: both follow from authoring a layout against a window constant.
    //
    // It is now one Control tree anchored to the viewport. Panel heights are a
    // share of the live frame, which is what ADR 0008 needs when the fixed frame
    // goes away, and it is what makes the overflow guard measure the layout the
    // player actually gets rather than a rectangle nobody has laid out.
    //
    // ADR 0008 now gives the map an explicit WorldViewport row. The camera
    // centers its world in that row, while the HUD continues to be ordinary
    // Control layout measured independently of map dimensions.
    // ---------------------------------------------------------------------
    private const int ToolbarStripTop = 74;

    /// <summary>
    /// A toolbar button. The icons are generated at 48x48 and drawn at 24x24 —
    /// exactly 2x, a clean downscale — so the button is that plus room to breathe.
    /// </summary>
    private const int ControlButtonSize = 28;

    /// <summary>The size an icon is resampled to and drawn at.</summary>
    private const int IconDrawSize = 24;

    private const int ControlStripPadding = 2;
    private const int ControlButtonSeparation = 2;
    private const int ControlStripHeight = ControlButtonSize + (ControlStripPadding * 2);
    private const int ControlStripSeparation = 4;
    private const int ControlStripsBandHeight =
        (ControlStripHeight * 2) + ControlStripSeparation + 4;
    private const int HudTopMargin = 8;
    private const int HudRightMargin = 16;
    private const int HudBottomMargin = 8;
    private const int HudLeftMargin = 16;
    private const int HudColumnSeparation = 10;
    private const int HudPanelSeparation = 6;
    private const int HudSidePanelMinimumWidth = 300;
    private const int HudMapColumnMinimumWidth = 480;

    private Vector2 MapPixelSize
    {
        get
        {
            var size = CameraView.MapSize(_tileSize);
            return new Vector2((float)size.Width, (float)size.Height);
        }
    }

    private float WorldVisualScale => (float)CameraView.WorldVisualScale(_tileSize);

    private float ScaleWorld(float referencePixels) => referencePixels * WorldVisualScale;

    private Vector2 ScaleWorld(float referenceX, float referenceY) =>
        new(ScaleWorld(referenceX), ScaleWorld(referenceY));

    private void CreateHud()
    {
        // The CanvasLayer is the structural boundary between world and HUD. A
        // Camera2D added to the world can move or scale Main without moving this
        // subtree; GUI input also reaches it before _UnhandledInput reaches the
        // map.
        _hudLayer = new CanvasLayer { Name = "HudLayer" };
        AddChild(_hudLayer);
        CreateWorldViewportMasks();

        // The root keeps top-left anchors and is resized explicitly. CanvasLayer
        // is not a Control and has no anchorable rectangle, so a full-rect anchor
        // would silently collapse to the HUD's minimum size on the first layout
        // pass after _Ready. Top-left anchors have no such dependency: the size
        // the viewport hands the HUD is the size it keeps.
        _hudRoot = new Control
        {
            Name = "Hud",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            TextureFilter = CanvasItem.TextureFilterEnum.Linear,
        };
        _hudLayer.AddChild(_hudRoot);
        _hudRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        GetViewport().SizeChanged += OnViewportResized;

        var margins = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hudRoot.AddChild(margins);
        margins.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        margins.AddThemeConstantOverride("margin_left", HudLeftMargin);
        margins.AddThemeConstantOverride("margin_top", HudTopMargin);
        margins.AddThemeConstantOverride("margin_right", HudRightMargin);
        margins.AddThemeConstantOverride("margin_bottom", HudBottomMargin);

        var columns = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        columns.AddThemeConstantOverride("separation", HudColumnSeparation);
        margins.AddChild(columns);
        columns.AddChild(CreateMapColumn());
        columns.AddChild(CreateSideColumn());
        LayoutHud(GetViewportRect().Size, _uiScale);
    }

    /// <summary>
    /// Camera2D transforms the complete world canvas, while the HUD reserves only
    /// one rectangle for that canvas. Four opaque HUD-layer rectangles cover the
    /// complement of the reserved rectangle. This is the presentation equivalent
    /// of a rectangular clip and keeps a zoomed map from showing through the
    /// transparent title, toolbars or roster.
    /// </summary>
    private void CreateWorldViewportMasks()
    {
        string[] names = ["WorldMaskTop", "WorldMaskBottom", "WorldMaskLeft", "WorldMaskRight"];
        foreach (var name in names)
        {
            var mask = new ColorRect
            {
                Name = name,
                Color = new Color("#07111d"),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _hudLayer!.AddChild(mask);
            _worldViewportMasks.Add(mask);
        }
    }

    private void LayoutWorldViewportMasks(Vector2 frameSize)
    {
        if (_worldViewportMasks.Count != 4 || _worldViewport is null)
        {
            return;
        }

        var world = WorldViewportScreenRect();
        SetMask(_worldViewportMasks[0], 0, 0, frameSize.X, world.Position.Y);
        SetMask(_worldViewportMasks[1], 0, world.End.Y, frameSize.X, frameSize.Y - world.End.Y);
        SetMask(_worldViewportMasks[2], 0, world.Position.Y, world.Position.X, world.Size.Y);
        SetMask(_worldViewportMasks[3], world.End.X, world.Position.Y, frameSize.X - world.End.X, world.Size.Y);
    }

    private static void SetMask(ColorRect mask, float x, float y, float width, float height)
    {
        mask.Position = new Vector2(x, y);
        mask.Size = new Vector2(Math.Max(0, width), Math.Max(0, height));
    }

    /// <summary>
    /// <c>canvas_items</c> treats the project window size as a design size. If
    /// only the native window is enlarged, a same-aspect 1600x900 window still
    /// exposes the 1280x720 design rectangle and merely scales it. A capture's
    /// explicit frame size is instead the logical rendering rectangle: this is
    /// what makes a larger deterministic frame reveal more world while the HUD
    /// keeps its authored pixel sizes.
    ///
    /// The project still owns the canvas-items/expand policy. An explicit frame
    /// fixes the logical rendering rectangle used by reproducible captures and
    /// <c>run-game.ps1</c>; an ordinary interactive resize synchronizes that
    /// rectangle to the new native window size in <see cref="OnViewportResized"/>.
    /// </summary>
    private void ConfigureRequestedFrame()
    {
        if (_requestedFrameSize is not { } requested)
        {
            return;
        }

        var frame = new Vector2I(
            checked((int)requested.Width),
            checked((int)requested.Height));
        var window = GetWindow();
        // With canvas_items/expand, Godot's --resolution is not applied to the
        // headless root window. Set both rectangles so the same explicit frame
        // is real in headless verification and in a visible capture.
        window.Size = frame;
        window.ContentScaleSize = frame;
    }

    // ---------------------------------------------------------------------
    // Startup frame and UI scale (Issue #100, Issue #86)
    //
    // The launcher used to hand every run a 1280x720 frame at UI scale 1. That
    // pair is the rectangle the HUD is authored against, not a description of
    // any real display, and on the owner's screen it opened a small window with
    // 8-15 px HUD text. Twice.
    //
    // The arithmetic of the replacement is engine-free and lives in
    // CameraView.AutomaticFrameSize / AutomaticUiScale, which is where ADR 0011
    // puts a rule of presentation. What is left here is the part that genuinely
    // needs the engine: asking the display server for a screen, moving the
    // window, and re-deriving the scale when the player resizes it.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The usable rectangle of the screen this window is on, or <c>null</c> when
    /// there is no display to ask (headless) or the display is smaller than the
    /// authored rectangle. Both cases fall back to the frame in
    /// <c>project.godot</c>, which is what every run did before.
    /// </summary>
    private ViewRect? ScreenUsableRect()
    {
        if (DisplayServer.GetName() == "headless")
        {
            return null;
        }

        var screen = DisplayServer.WindowGetCurrentScreen();
        var rect = DisplayServer.ScreenGetUsableRect(screen);
        if (rect.Size.X < CameraView.DesignFrameSize.Width ||
            rect.Size.Y < CameraView.DesignFrameSize.Height)
        {
            return null;
        }

        return new ViewRect(rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y);
    }

    private void ConfigureStartupFrame()
    {
        ViewSize frame;
        if (_requestedFrameSize is { } declared)
        {
            ConfigureRequestedFrame();
            frame = declared;
        }
        else if (ScreenUsableRect() is { } usable)
        {
            _screenUsableRect = usable;
            frame = CameraView.AutomaticFrameSize(new ViewSize(usable.Width, usable.Height));
            var size = new Vector2I(checked((int)frame.Width), checked((int)frame.Height));
            var window = GetWindow();
            window.Size = size;
            window.ContentScaleSize = size;
            // Centred in the usable rectangle rather than left wherever the
            // engine would have put a 1280x720 window: the remaining tenth of
            // the screen is split evenly, so the title bar has room and no edge
            // falls off.
            window.Position = new Vector2I(
                checked((int)usable.X) + ((checked((int)usable.Width) - size.X) / 2),
                checked((int)usable.Y) + ((checked((int)usable.Height) - size.Y) / 2));
            _autoFrameSize = frame;
        }
        else
        {
            // No screen to measure — headless, or a display smaller than the
            // authored rectangle. The project's own 1280x720 window stands and
            // an omitted --ui-scale keeps meaning 1, which is what every run did
            // before this policy existed.
            return;
        }

        if (_uiScaleIsAutomatic)
        {
            _uiScale = CameraView.AutomaticUiScale(
                frame,
                ViewLaunchOptions.MinimumLogicalFrameSize);
        }

        AssertLogicalFrameFits(frame, _uiScale);
    }

    /// <summary>
    /// Points the camera at the rectangle the HUD reserved for the world. It is
    /// two lines because everything that could be decided was decided in
    /// <see cref="CameraView.AutomaticZoom"/>; what is left is the measurement,
    /// which needs the engine.
    ///
    /// The world viewport is measured through the live canvas transform rather
    /// than as <c>_worldViewport.Size</c>: the HUD subtree is scaled by the UI
    /// scale, so the Control's own size is in logical pixels while the camera
    /// works in the frame's pixels, and mixing the two would zoom by the UI
    /// scale a second time.
    /// </summary>
    private void ApplyAutomaticCameraZoom()
    {
        if (!_cameraZoomIsAutomatic || _worldViewport is null)
        {
            return;
        }

        var world = WorldViewportScreenRect();
        if (world.Size.X <= 0 || world.Size.Y <= 0)
        {
            return;
        }

        _cameraZoom = CameraView.AutomaticZoom(
            new ViewSize(world.Size.X, world.Size.Y),
            _tileSize);
    }

    /// <summary>
    /// The rule <see cref="ViewLaunchOptions"/> applies to a declared frame,
    /// applied once more to the pair this run actually ended up with. It is not
    /// a duplicate: a frame derived from the screen is unknown until the display
    /// has been asked, and an explicit <c>--ui-scale 2</c> on a 1366x768 laptop
    /// reaches exactly here and nowhere else.
    /// </summary>
    private static void AssertLogicalFrameFits(ViewSize frame, double uiScale)
    {
        var minimum = ViewLaunchOptions.MinimumLogicalFrameSize;
        if (CameraView.FitsLogicalFrame(frame, uiScale, minimum))
        {
            return;
        }

        throw new ArgumentException(
            $"Frame {FormatSize(frame)} at UI scale {FormatNumber(uiScale)} provides only " +
            $"{FormatSize(CameraView.LogicalFrameSize(frame, uiScale))} logical pixels; " +
            $"at least {FormatSize(minimum)} are required.",
            "--ui-scale");
    }

    private void OnViewportResized()
    {
        // A player resizing an interactive window expects extra pixels to expose
        // extra world. Captures keep their declared logical frame frozen; this
        // synchronization is therefore deliberately disabled for screenshot
        // runs.
        if (_screenshotPath is null)
        {
            var window = GetWindow();
            if (window.ContentScaleSize != window.Size)
            {
                window.ContentScaleSize = window.Size;
            }
        }

        var size = GetViewportRect().Size;
        // Issue #86: maximizing used to hand the extra pixels to the world and
        // leave the HUD at its launch-time scale, so a 3044x1722 client area
        // still drew 8 px legend text. An automatic scale follows the window it
        // was derived from; an explicit one never moves.
        //
        // The assignment is unconditional on purpose. It used to be guarded by
        // "and the new pair fits the minimum logical frame", which turned a
        // window dragged below that minimum into a window that kept the scale of
        // the larger one it used to be — a second, quieter copy of the same
        // defect. The decision now lives whole in CameraView.AutomaticUiScale,
        // which answers for every frame including the ones under the minimum.
        if (_uiScaleIsAutomatic && _requestedFrameSize is null && _screenshotPath is null)
        {
            _uiScale = CameraView.AutomaticUiScale(
                new ViewSize(size.X, size.Y),
                ViewLaunchOptions.MinimumLogicalFrameSize);
        }

        LayoutHud(size, _uiScale);
        // The world viewport moved with the HUD, so a run that never chose its
        // own zoom re-derives it here. A run that did — because the player
        // turned the wheel, or because --camera-zoom was declared — keeps it.
        ApplyAutomaticCameraZoom();
        ApplyCameraView();
        QueueRedraw();
    }

    private void CreateCamera()
    {
        _camera = new Camera2D
        {
            Name = "WorldCamera",
            Enabled = true,
        };
        AddChild(_camera);
        ApplyCameraView();
    }

    private CameraFrame CurrentCameraFrame()
    {
        var viewport = GetViewportRect().Size;
        var world = WorldViewportScreenRect();
        return new CameraFrame(
            _cameraCenter,
            _cameraZoom,
            new ViewRect(world.Position.X, world.Position.Y, world.Size.X, world.Size.Y),
            new ViewSize(viewport.X, viewport.Y));
    }

    private void ApplyCameraView()
    {
        if (_camera is null || _worldViewport is null)
        {
            return;
        }

        _cameraCenter = CameraView.ClampCenterToMap(_cameraCenter, _tileSize);
        var frame = CurrentCameraFrame();
        var node = frame.CameraNodePosition;
        _camera.Position = new Vector2((float)node.X, (float)node.Y);
        _camera.Zoom = Vector2.One * (float)_cameraZoom;
        _camera.ForceUpdateScroll();
    }

    private void AssertCameraNodeMatchesFrame()
    {
        if (_camera is null || _worldViewport is null)
        {
            throw new InvalidOperationException(
                "Camera layout synchronization ran before the camera and world viewport existed.");
        }

        var expected = CurrentCameraFrame().CameraNodePosition;
        var actual = _camera.Position;
        if (Math.Abs(actual.X - expected.X) > 0.01 ||
            Math.Abs(actual.Y - expected.Y) > 0.01)
        {
            throw new InvalidOperationException(
                $"Camera2D did not follow deferred HUD layout: expected " +
                $"{FormatPoint(expected)}, actual {FormatVector(actual)}.");
        }
    }

    private Rect2 WorldViewportScreenRect()
    {
        var transform = _worldViewport!.GetGlobalTransformWithCanvas();
        var topLeft = transform * Vector2.Zero;
        var bottomRight = transform * _worldViewport.Size;
        return new Rect2(topLeft, bottomRight - topLeft);
    }

    private GridPoint WorldToCell(Vector2 world)
    {
        var cell = CameraView.WorldToCell(new ViewPoint(world.X, world.Y), _tileSize);
        return cell;
    }

    private GridPoint? ScreenToCell(Vector2 screen)
    {
        var worldViewport = WorldViewportScreenRect();
        if (!worldViewport.HasPoint(screen))
        {
            return null;
        }

        // InputEventMouse positions are viewport pixels. The inverse of the live
        // canvas transform is the authoritative screen-to-world conversion; only
        // the engine-free world-to-grid step remains in Presentation.
        var world = GetViewport().GetCanvasTransform().AffineInverse() * screen;
        var cell = WorldToCell(world);
        return IsMapCell(cell) ? cell : null;
    }

    private void PanCamera(ViewPoint screenDelta)
    {
        _cameraCenter = CameraView.PanByScreenDelta(_cameraCenter, screenDelta, _cameraZoom);
        ApplyCameraView();
        UpdatePointer(_lastPanPointer);
        QueueRedraw();
    }

    private void StepCameraZoom(int direction)
    {
        // The player has taken the zoom over. From here a resize may still move
        // the HUD scale, because that is legibility, but it may not move the
        // world scale, because that is a decision somebody made.
        _cameraZoomIsAutomatic = false;
        _cameraZoom = CameraView.StepZoom(_cameraZoom, direction);
        ApplyCameraView();
        QueueRedraw();
    }

    private void NudgeCamera(int horizontalTiles, int verticalTiles)
    {
        _cameraCenter = CameraView.MoveByTiles(
            _cameraCenter,
            horizontalTiles,
            verticalTiles,
            _tileSize);
        ApplyCameraView();
        QueueRedraw();
    }

    private void AssertRequestedFrameSize()
    {
        if (_requestedFrameSize is not { } requested)
        {
            return;
        }

        var actual = GetViewportRect().Size;
        if (!Mathf.IsEqualApprox(actual.X, (float)requested.Width) ||
            !Mathf.IsEqualApprox(actual.Y, (float)requested.Height))
        {
            throw new InvalidOperationException(
                $"Requested frame {FormatNumber(requested.Width)}x{FormatNumber(requested.Height)}, " +
                $"but Godot created {FormatNumber(actual.X)}x{FormatNumber(actual.Y)}.");
        }
    }

    /// <summary>
    /// Engine-level evidence for the input seam: an engine-free
    /// <see cref="CameraFrame"/> predicts where a world point belongs, and the
    /// live Camera2D canvas transform must independently place it there before
    /// the adapter inverts that predicted screen point back to a cell. The same
    /// smoke drives all zooms and requested positions, both map extremes and one
    /// real pan at every zoom. A point in the side HUD is rejected before the
    /// inverse can become a map click.
    /// </summary>
    private void VerifyCameraInputSmoke(bool injectTransformRegression)
    {
        var originalCenter = _cameraCenter;
        var originalZoom = _cameraZoom;
        var target = new GridPoint(14, 8);
        var targetWorld = CellCenter(target);
        var targetView = new ViewPoint(targetWorld.X, targetWorld.Y);
        ViewPoint[] centers =
        [
            CameraView.MapCenter(_tileSize),
            new ViewPoint(600, 340),
            new ViewPoint(520, 300),
        ];

        _cameraInputChecks = 0;
        _cameraBoundsChecks = 0;
        _cameraPanChecks = 0;
        _cameraTransformChecks = 0;
        foreach (var zoom in CameraView.ZoomLevels)
        {
            foreach (var center in centers)
            {
                _cameraZoom = zoom;
                _cameraCenter = center;
                ApplyCameraView();
                var expectedScreen = CurrentCameraFrame().WorldToScreen(targetView);
                if (injectTransformRegression && _cameraTransformChecks == 0)
                {
                    _camera!.Position += new Vector2(17, -11);
                    _camera.ForceUpdateScroll();
                }

                var actualScreen = GetViewport().GetCanvasTransform() * targetWorld;
                if (Math.Abs(actualScreen.X - expectedScreen.X) > 0.01 ||
                    Math.Abs(actualScreen.Y - expectedScreen.Y) > 0.01)
                {
                    throw new InvalidOperationException(
                        $"Camera2D transform disagrees with CameraFrame at zoom {FormatNumber(zoom)}: " +
                        $"expected screen {FormatPoint(expectedScreen)}, " +
                        $"actual {FormatVector(actualScreen)}, center {FormatPoint(center)}.");
                }

                _cameraTransformChecks++;
                var predictedScreen = new Vector2(
                    (float)expectedScreen.X,
                    (float)expectedScreen.Y);
                if (ScreenToCell(predictedScreen) != target)
                {
                    throw new InvalidOperationException(
                        $"Camera input mapped cell {target} incorrectly at zoom {FormatNumber(zoom)} " +
                        $"and center {FormatPoint(center)}.");
                }

                _cameraInputChecks++;
            }
        }

        ViewPoint[] outsideCenters =
        [
            new ViewPoint(-10_000, -10_000),
            new ViewPoint(10_000, 10_000),
        ];
        foreach (var zoom in CameraView.ZoomLevels)
        {
            foreach (var outsideCenter in outsideCenters)
            {
                _cameraZoom = zoom;
                _cameraCenter = outsideCenter;
                var expected = CameraView.ClampCenterToMap(outsideCenter, _tileSize);
                ApplyCameraView();
                if (Math.Abs(_cameraCenter.X - expected.X) > 0.001 ||
                    Math.Abs(_cameraCenter.Y - expected.Y) > 0.001)
                {
                    throw new InvalidOperationException(
                        $"Camera escaped map bounds at zoom {FormatNumber(zoom)}: " +
                        $"requested {FormatPoint(outsideCenter)}, " +
                        $"applied {FormatPoint(_cameraCenter)}, expected {FormatPoint(expected)}.");
                }

                _cameraBoundsChecks++;
            }
        }

        foreach (var zoom in CameraView.ZoomLevels)
        {
            _cameraZoom = zoom;
            _cameraCenter = CameraView.MapCenter(_tileSize);
            ApplyCameraView();
            var beforePan = _cameraCenter;
            PanCamera(new ViewPoint(40, -20));
            var expected = CameraView.ClampCenterToMap(
                CameraView.PanByScreenDelta(beforePan, new ViewPoint(40, -20), zoom),
                _tileSize);
            if (_cameraCenter == beforePan || _cameraCenter != expected)
            {
                throw new InvalidOperationException(
                    $"Camera pan was cancelled at zoom {FormatNumber(zoom)}: " +
                    $"before {FormatPoint(beforePan)}, applied {FormatPoint(_cameraCenter)}, " +
                    $"expected {FormatPoint(expected)}.");
            }

            _cameraPanChecks++;
        }

        var worldViewport = WorldViewportScreenRect();
        var hudPoint = new Vector2(
            Math.Min(GetViewportRect().Size.X - 1, worldViewport.End.X + 8),
            worldViewport.GetCenter().Y);
        _hudInputRejected = !worldViewport.HasPoint(hudPoint) && ScreenToCell(hudPoint) is null;
        if (!_hudInputRejected)
        {
            throw new InvalidOperationException("A point in the HUD reached map input.");
        }

        _cameraCenter = originalCenter;
        _cameraZoom = originalZoom;
        ApplyCameraView();
    }
}
