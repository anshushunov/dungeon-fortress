using System.Globalization;
using System.Text.Json;

using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

/// <summary>
/// A deliberately thin Godot adapter. PrototypeWorld remains the only mutable
/// game state; Nodes and drawing are replaced wholesale from its snapshots.
/// </summary>
public partial class Main : Node2D
{
    private const double TicksPerSecond = 6.0;
    private static readonly Color[] CreatureColors =
    [
        new("#fb7185"), new("#f59e0b"), new("#eab308"),
        new("#84cc16"), new("#22c55e"), new("#14b8a6"),
        new("#38bdf8"), new("#818cf8"), new("#c084fc"),
    ];

    private readonly List<RuntimeDiagnostic> _diagnostics = [];
    private readonly Dictionary<int, Label> _nameLabels = [];
    private readonly Dictionary<string, Texture2D> _goblinSprites = [];
    private readonly List<string> _loadedSpriteStates = [];
    private readonly List<string> _missingSpriteStates = [];
    private readonly Dictionary<string, Texture2D> _icons = [];
    private readonly List<string> _missingIcons = [];
    private Texture2D? _iconPlaceholder;
    private PrototypeWorld? _world;
    private PrototypeSnapshot? _state;
    // The snapshot as the map is allowed to show it: canonical state plus the
    // marking accepted for this tick and not applied yet. Rebuilt with _state and
    // never written back — see DungeonFortress.Presentation.MapProjection.
    private MapProjection? _projection;
    private Camera2D? _camera;
    private CanvasLayer? _hudLayer;
    private Control? _hudRoot;
    private Control? _worldViewport;
    private readonly List<ColorRect> _worldViewportMasks = [];
    private Label? _title;
    private Label? _summary;
    private Label? _inspector;
    private Label? _feedback;
    private Label? _roster;
    private Control? _timeStrip;
    private Control? _brushStrip;
    private readonly List<Button> _controlButtons = [];
    private readonly List<Label> _legendLines = [];
    private PrototypeCommandLog? _fixtureLog;
    private readonly List<PrototypeCommand> _playerCommands = [];
    private ZoneKind _brushZone = ZoneKind.Farm;
    private BrushMode _editMode = BrushMode.Inspect;
    private JobKind _selectedJob = JobKind.Harvest;
    private int _selectedRule;
    private bool _editingPriorities = true;
    private string _controlFeedback =
        "Pick a brush, then drag a rectangle on the map: the whole area is marked by " +
        "one command. Esc cancels a drag, then puts the brush away.";
    private string _fixture = "baseline";
    private string? _screenshotPath;
    private int _tileSize = CameraView.DefaultTileSize;
    private double _cameraZoom = CameraView.DefaultZoom;
    private ViewPoint _cameraCenter = CameraView.MapCenter(CameraView.DefaultTileSize);
    private double _uiScale = CameraView.DefaultUiScale;
    private ViewSize? _requestedFrameSize;
    private bool _cameraPanning;
    private Vector2 _lastPanPointer;
    private int? _selectedCreatureId;
    private GridPoint? _selectedCell;
    private int? _hoverCreatureId;
    private GridPoint? _hoverCell;
    // The rectangle being dragged right now. Nothing is emitted while it exists,
    // which is what makes a cancelled drag leave no trace in the command log.
    private GridPoint? _dragAnchor;
    private GridPoint? _dragCurrent;
    private bool _paused = true;
    private bool _visibleSmoke;
    private double _visibleSmokeElapsed;
    private double _speed = 1.0;
    private double _tickAccumulator;
    // Presentation-only motion buffer: where every body stood when the tick now
    // being drawn started. See RenderCenter for why it is read but never written
    // back into the simulation.
    private readonly Dictionary<int, GridPoint> _creatureMotionOrigin = [];
    private readonly Dictionary<int, GridPoint> _raiderMotionOrigin = [];
    private bool _motionOriginPending;
    private bool _interpolatesMotion;
    private string _checksum = string.Empty;
    private int _screenshotFramesRemaining;
    private int _fallbackSpriteDraws;
    private bool _spritesHaveMipmaps;
    private int _cameraInputChecks;
    private int _cameraBoundsChecks;
    private int _cameraPanChecks;
    private int _cameraTransformChecks;
    private bool _hudInputRejected;
    private bool _cameraSynchronizedAfterLayout;

    public override void _Ready()
    {
        var failureEventName = "godot_headless_smoke";
        try
        {
            var arguments = OS.GetCmdlineUserArgs();
            var headlessSmoke = arguments.Contains("--smoke", StringComparer.Ordinal);
            var visibleSmoke = arguments.Contains("--visible-smoke", StringComparer.Ordinal);
            var controlsSmoke = arguments.Contains("--smoke-controls", StringComparer.Ordinal);
            var cameraTransformRegression =
                arguments.Contains("--smoke-camera-transform-regression", StringComparer.Ordinal);
            var cameraSmoke =
                arguments.Contains("--smoke-camera", StringComparer.Ordinal) ||
                cameraTransformRegression;
            var hudGuardRegression =
                arguments.Contains("--smoke-hud-guard-regression", StringComparer.Ordinal);
            failureEventName = cameraSmoke
                ? "godot_camera_smoke"
                : controlsSmoke
                    ? "godot_controls_smoke"
                    : headlessSmoke
                        ? "godot_headless_smoke"
                        : visibleSmoke
                            ? "godot_visible_smoke"
                            : failureEventName;
            var fixture = CommandLineArguments.Read(arguments, "--fixture") ?? "baseline";
            var screenshotTicks = CommandLineArguments.ReadInt(arguments, "--screenshot-ticks") ?? 1;
            _screenshotPath = CommandLineArguments.Read(arguments, "--screenshot");
            _screenshotFramesRemaining = _screenshotPath is null ? 0 : 3;
            var view = ViewLaunchOptions.Parse(
                arguments,
                requireExplicitCaptureParameters: _screenshotPath is not null);
            _tileSize = view.TileSize;
            _cameraZoom = view.CameraZoom;
            _cameraCenter = view.CameraPosition;
            _uiScale = view.UiScale;
            _requestedFrameSize = view.FrameSize;
            ConfigureRequestedFrame();
            var selectCreature = CommandLineArguments.ReadInt(arguments, "--select-creature");
            var selectCell = CommandLineArguments.Read(arguments, "--select-cell");
            var demoControls = arguments.Contains("--demo-controls", StringComparer.Ordinal);
            var demoDig = arguments.Contains("--demo-dig", StringComparer.Ordinal);
            var demoStone = arguments.Contains("--demo-stone", StringComparer.Ordinal);
            var demoBuild = arguments.Contains("--demo-build", StringComparer.Ordinal);
            // Holds the HUD to "every line fits", ignoring the deficit Issue #36
            // still owns. verify.ps1 runs it and requires it to fail: that is what
            // proves the guard reacts at all instead of passing on everything.
            var strictHudFit = arguments.Contains("--strict-hud-fit", StringComparer.Ordinal);
            var requiresSprites = !headlessSmoke && !controlsSmoke && !cameraSmoke;

            // World sprites are sampled below 1x at the two overview zoom levels.
            // The imported image is rebuilt with mipmaps in LoadGoblinSprites;
            // this inherited filter makes the immediate DrawTextureRect calls use
            // those levels with linear sampling.
            TextureFilter = TextureFilterEnum.LinearWithMipmaps;

            // Before the HUD, because every toolbar button is created with the
            // texture it draws. A file the icon pack has not delivered yet becomes
            // a placeholder here and nowhere else, so dropping the real PNG in
            // changes no code at all.
            LoadIcons();
            CreateHud();
            CreateCamera();
            LoadGoblinSprites();
            if (requiresSprites)
            {
                AssertRequiredSpritesLoaded();
            }
            LoadFixture(
                fixture,
                demoControls || demoDig || demoStone || demoBuild || controlsSmoke ||
                _screenshotPath is null
                    ? 1
                    : screenshotTicks);
            if (hudGuardRegression)
            {
                InjectHudGuardRegression();
            }

            if (demoControls || controlsSmoke)
            {
                ApplyDemoControls();
                if (_screenshotPath is not null)
                {
                    Advance(Math.Max(0, screenshotTicks - _state!.Tick));
                }
            }

            if (demoDig)
            {
                ApplyDemoDig();
                Advance(Math.Max(0, screenshotTicks - _state!.Tick));
            }

            if (demoStone)
            {
                ApplyDemoStone();
                Advance(Math.Max(0, screenshotTicks - _state!.Tick));
            }

            if (demoBuild)
            {
                ApplyDemoBuild();
                Advance(Math.Max(0, screenshotTicks - _state!.Tick));
            }
            if (selectCreature is { } creatureId)
            {
                if (!_state!.Creatures.Any(creature => creature.Id == creatureId))
                {
                    throw new ArgumentOutOfRangeException(
                        "--select-creature",
                        $"Fixture '{fixture}' has no creature #{creatureId}.");
                }

                _selectedCreatureId = creatureId;
                _selectedCell = _state.Creatures.Single(creature => creature.Id == creatureId).Position;
                UpdateHud();
                QueueRedraw();
            }

            // Cell selection is the map counterpart of --select-creature: it makes
            // a capture point at the tile whose explanation the frame is about,
            // instead of relying on whatever a demo happened to select last.
            if (selectCell is not null)
            {
                _selectedCell = CommandLineArguments.ParseCell(selectCell);
                _selectedCreatureId = _state!.Creatures
                    .Where(creature => creature.Position == _selectedCell)
                    .Select(creature => (int?)creature.Id)
                    .FirstOrDefault();
                UpdateHud();
                QueueRedraw();
            }

            // Every entry point pays for the fit guard, because _Ready still runs
            // before Godot's first layout pass and the labels therefore still have
            // the rectangles the HUD was designed around. Later is too late: by the
            // time a frame is drawn an unclipped label has re-expanded to its own
            // content and the check would silently pass on anything.
            AssertLabelsFit(strictHudFit);
            AssertControlStripsFit();
            ApplyCameraView();
            AssertRequestedFrameSize();

            if (cameraSmoke)
            {
                VerifyCameraInputSmoke(cameraTransformRegression);
                VerifyDeterministicFixture(fixture);
                PrintResult("godot_camera_smoke", "ok", null);
                GetTree().Quit();
            }

            if (headlessSmoke || visibleSmoke)
            {
                VerifyDeterministicFixture(fixture);
                PrintResult(headlessSmoke ? "godot_headless_smoke" : "godot_visible_smoke", "ok", null);
                if (headlessSmoke)
                {
                    GetTree().Quit();
                }
                else
                {
                    _visibleSmoke = true;
                }
            }
            if (controlsSmoke)
            {
                VerifyControlsSmoke();
                PrintResult("godot_controls_smoke", "ok", null);
                GetTree().Quit();
            }
        }
        catch (Exception exception)
        {
            RecordDiagnostic("startup", exception);
            PrintResult(failureEventName, "error", exception);
            GD.PushError(exception.ToString());
            GetTree().Quit(1);
        }
    }

    public override void _Process(double delta)
    {
        if (!_framePacingArgumentsRead)
        {
            _framePacingArgumentsRead = true;
            BeginFramePacingProbe();
        }

        // Containers complete one deferred layout after _Ready. Re-derive the
        // Camera2D node from that real geometry once, so the live camera cannot
        // retain the position computed from a provisional world viewport.
        if (!_cameraSynchronizedAfterLayout && _camera is not null && _worldViewport is not null)
        {
            LayoutHud(GetViewportRect().Size, _uiScale);
            ApplyCameraView();
            AssertCameraNodeMatchesFrame();
            _cameraSynchronizedAfterLayout = true;
        }

        if (_screenshotPath is not null)
        {
            if (_screenshotFramesRemaining-- > 0)
            {
                return;
            }

            // A capture is the one moment the HUD can be measured after Godot's
            // own layout passes have run. _Ready proves the design; this proves
            // the frame that was actually drawn, and it is what caught the HUD
            // collapsing to its minimum size after _Ready returned.
            try
            {
                AssertLabelsFit();
                AssertControlStripsFit();
                ApplyCameraView();
            }
            catch (Exception exception)
            {
                RecordDiagnostic("hud_layout", exception);
                GD.PushError(exception.ToString());
                GetTree().Quit(1);
                return;
            }

            CaptureScreenshot(_screenshotPath);
            _screenshotPath = null;
            GetTree().Quit();
            return;
        }

        if (_visibleSmoke)
        {
            _visibleSmokeElapsed += delta;
            if (_visibleSmokeElapsed >= 0.75)
            {
                GetTree().Quit();
            }

            return;
        }

        if (_framePacingTargetTick is { } target)
        {
            MeasureFramePacingFrame();
            if (_state!.Tick >= target || _world!.IsComplete)
            {
                PrintFramePacingResult(target);
                GetTree().Quit();
                return;
            }
        }

        if (!_paused)
        {
            _tickAccumulator += delta * TicksPerSecond * _speed;
            var steps = Math.Min(24, (int)_tickAccumulator);
            if (_framePacingTargetTick is { } probeTarget)
            {
                // The probe stops on an exact tick, so two frame rates are compared
                // at the same point of the simulation instead of at whichever tick
                // each of them happened to overshoot to.
                steps = Math.Min(steps, probeTarget - _state!.Tick);
            }

            if (steps > 0)
            {
                _tickAccumulator -= steps;
                // Presentation only: remember where every body stood so the frames
                // between this tick and the next can lerp instead of teleport.
                RememberMotionOrigin();
                Advance(steps);
            }
            else if (_interpolatesMotion)
            {
                // No tick ran, but alpha moved. Redrawing here is what separates
                // rendering from the tick: at TicksPerSecond = 6.0 a frame-driven
                // redraw is the only thing that makes movement continuous.
                UpdateCreatureLabels();
                QueueRedraw();
            }
        }
    }

    /// <summary>
    /// Where every creature and raider stood when the tick now being drawn
    /// started. Rendering lerps from here to the canonical position, so a 6 Hz
    /// simulation stops teleporting bodies a whole tile at a time.
    ///
    /// This is presentation state and nothing else: it is written from snapshots
    /// and never read by <see cref="PrototypeWorld"/>.
    /// </summary>
    private void RememberMotionOrigin()
    {
        if (_state is null)
        {
            return;
        }

        _creatureMotionOrigin.Clear();
        foreach (var creature in _state.Creatures)
        {
            _creatureMotionOrigin[creature.Id] = creature.Position;
        }

        _raiderMotionOrigin.Clear();
        foreach (var raider in _state.Raiders)
        {
            _raiderMotionOrigin[raider.Id] = raider.Position;
        }

        _motionOriginPending = true;
    }

    /// <summary>
    /// How far the drawing has travelled from the previous tick towards the
    /// current one. The lerp deliberately runs one tick *behind* canonical state:
    /// alpha 0 draws the tile a creature came from and alpha 1 the tile it is
    /// already standing on, so the picture can never show a body in a tile the
    /// simulation has not moved it to.
    ///
    /// Paused, stepped, reloaded and command-edited states are drawn at alpha 1,
    /// which is canonical: STEP has to show the result of the step it just ran.
    /// </summary>
    private float MotionAlpha() =>
        !_interpolatesMotion || _paused ? 1f : (float)Math.Clamp(_tickAccumulator, 0.0, 1.0);

    private Vector2 RenderCenter(GridPoint position, Dictionary<int, GridPoint> origins, int id)
    {
        var alpha = MotionAlpha();
        if (alpha >= 1f || !origins.TryGetValue(id, out var origin) || origin == position)
        {
            return CellCenter(position);
        }

        return CellCenter(origin).Lerp(CellCenter(position), alpha);
    }

    private Vector2 CreatureRenderCenter(PrototypeCreatureSnapshot creature) =>
        RenderCenter(creature.Position, _creatureMotionOrigin, creature.Id);

    private Vector2 RaiderRenderCenter(PrototypeRaiderSnapshot raider) =>
        RenderCenter(raider.Position, _raiderMotionOrigin, raider.Id);

    // ---------------------------------------------------------------------
    // Frame pacing probe
    //
    // Interpolation is only allowed to change the picture, so the claim that
    // needs evidence is "the canonical state does not notice it". The probe
    // drives the very same _Process path a player's frames drive and reports the
    // canonical checksum, so Godot's --fixed-fps turns "does the frame rate
    // change the simulation?" into an ordinary headless comparison. It also
    // measures the two properties a human would otherwise have to judge from a
    // video: that no drawn body ever leads the simulation into a tile it has not
    // reached, and that no frame moves a body by a whole tile.
    // ---------------------------------------------------------------------
    private bool _framePacingArgumentsRead;
    private int? _framePacingTargetTick;
    private long _framePacingFrames;
    private long _framePacingInterpolatedFrames;
    private long _framePacingLeadViolations;
    private float _framePacingMaxRenderStep;
    private readonly Dictionary<int, Vector2> _framePacingLastRender = [];

    private void BeginFramePacingProbe()
    {
        var arguments = OS.GetCmdlineUserArgs();
        var index = Array.IndexOf(arguments, "--frame-pacing");
        if (index < 0)
        {
            return;
        }

        if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out var target))
        {
            throw new ArgumentException("--frame-pacing expects a target tick.", "--frame-pacing");
        }

        _framePacingTargetTick = target;
        _paused = false;
        _speed = 1.0;
    }

    private void MeasureFramePacingFrame()
    {
        _framePacingFrames++;
        if (MotionAlpha() < 1f)
        {
            _framePacingInterpolatedFrames++;
        }

        foreach (var creature in _state!.Creatures)
        {
            var render = CreatureRenderCenter(creature);
            var drawnCell = WorldToCell(render);
            var origin = _creatureMotionOrigin.TryGetValue(creature.Id, out var from)
                ? from
                : creature.Position;
            if (drawnCell != creature.Position && drawnCell != origin)
            {
                _framePacingLeadViolations++;
            }

            if (_framePacingLastRender.TryGetValue(creature.Id, out var previous))
            {
                _framePacingMaxRenderStep = Math.Max(
                    _framePacingMaxRenderStep,
                    previous.DistanceTo(render));
            }

            _framePacingLastRender[creature.Id] = render;
        }
    }

    private void PrintFramePacingResult(int targetTick)
    {
        // The same command log replayed in one shot, with no frames at all. If a
        // frame-driven run and a frameless replay agree, the render loop added
        // nothing to canonical state.
        var replay = new PrototypeWorld(BuildFullLog(_playerCommands));
        replay.RunTicks(_state!.Tick);

        GD.Print(JsonSerializer.Serialize(new
        {
            @event = "godot_frame_pacing",
            status = "ok",
            fixture = _fixture,
            seed = _state.Seed,
            targetTick,
            tick = _state.Tick,
            checksum = _checksum,
            replayChecksum = PrototypeScenario.Capture(replay).Checksum,
            frames = _framePacingFrames,
            interpolatedFrames = _framePacingInterpolatedFrames,
            interpolationLeadViolations = _framePacingLeadViolations,
            maxRenderStepPixels = Math.Round((double)_framePacingMaxRenderStep, 3),
            tileSize = _tileSize,
            ticksPerSecond = TicksPerSecond,
            runtimeDiagnostics = _diagnostics,
        }));
    }

    /// <summary>
    /// The map's share of the mouse — and only the map's.
    ///
    /// This used to be <c>_Input</c>, which runs <em>before</em> the GUI, so every
    /// click had to be hit-tested against the drawn button rectangles by hand and
    /// swallowed if it landed on one. Two descriptions of where a button is, kept
    /// in step by nothing but care.
    ///
    /// Godot offers the event to the Control tree first and calls this only for
    /// what nothing consumed, so a click on a button never reaches the map by
    /// construction rather than by arithmetic. Ownership of a click is now a
    /// property of the node tree.
    /// </summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseMotion motion when _cameraPanning:
                var panDelta = motion.Position - _lastPanPointer;
                _lastPanPointer = motion.Position;
                PanCamera(new ViewPoint(panDelta.X, panDelta.Y));
                break;

            case InputEventMouseMotion motion:
                UpdatePointer(motion.Position);
                if (_dragAnchor is not null &&
                    ScreenToCell(motion.Position) is { } dragged &&
                    _dragCurrent != dragged)
                {
                    _dragCurrent = dragged;
                    UpdateHud();
                    QueueRedraw();
                }

                break;

            case InputEventMouseButton
            {
                ButtonIndex: MouseButton.Middle,
                Pressed: true,
            } middle:
                if (WorldViewportScreenRect().HasPoint(middle.Position))
                {
                    _cameraPanning = true;
                    _lastPanPointer = middle.Position;
                }

                break;

            case InputEventMouseButton
            {
                ButtonIndex: MouseButton.Middle,
                Pressed: false,
            }:
                _cameraPanning = false;
                break;

            case InputEventMouseButton
            {
                ButtonIndex: MouseButton.WheelUp,
                Pressed: true,
            } wheelUp:
                if (WorldViewportScreenRect().HasPoint(wheelUp.Position))
                {
                    StepCameraZoom(1);
                }

                break;

            case InputEventMouseButton
            {
                ButtonIndex: MouseButton.WheelDown,
                Pressed: true,
            } wheelDown:
                if (WorldViewportScreenRect().HasPoint(wheelDown.Position))
                {
                    StepCameraZoom(-1);
                }

                break;

            // During a drag the right button cancels the selection and nothing
            // else: putting the brush away as well would punish a misdrag twice.
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
                if (_dragAnchor is not null)
                {
                    CancelDrag("right-click");
                }
                else
                {
                    CancelBrush("right-click");
                }

                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                if (_dragAnchor is { } anchor)
                {
                    var release = _dragCurrent ?? anchor;
                    _dragAnchor = null;
                    _dragCurrent = null;
                    ApplyBrushStroke(anchor, release);
                }

                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click:
                UpdatePointer(click.Position);
                if (_editMode == BrushMode.Inspect)
                {
                    SelectAt(click.Position);
                    break;
                }

                if (ScreenToCell(click.Position) is { } start)
                {
                    _dragAnchor = start;
                    _dragCurrent = start;
                    UpdateHud();
                    QueueRedraw();
                }

                break;
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        switch (key.Keycode)
        {
            case Key.Left:
                NudgeCamera(-3, 0);
                break;
            case Key.Right:
                NudgeCamera(3, 0);
                break;
            case Key.Up:
                NudgeCamera(0, -3);
                break;
            case Key.Down:
                NudgeCamera(0, 3);
                break;
            case Key.Space:
            case Key.P:
                TogglePause();
                break;
            case Key.S:
                Advance(1);
                break;
            case Key.Key1:
                SetSpeed(0.5);
                break;
            case Key.Key2:
                SetSpeed(1.0);
                break;
            case Key.Key3:
                SetSpeed(4.0);
                break;
            case Key.Key4:
                SetSpeed(16.0);
                break;
            case Key.R:
                LoadFixture("baseline", 1);
                break;
            case Key.N:
                LoadFixture("neglected", 1);
                break;
            case Key.I:
                SelectEditMode(BrushMode.Inspect);
                break;
            case Key.B:
                SelectEditMode(BrushMode.Paint);
                break;
            case Key.E:
                SelectEditMode(BrushMode.Erase);
                break;
            case Key.D:
                SelectEditMode(BrushMode.Dig);
                break;
            case Key.X:
                SelectEditMode(BrushMode.CancelDig);
                break;
            case Key.M:
                SelectStockpileBrush();
                break;
            case Key.C:
                SelectEditMode(BrushMode.Build);
                break;
            case Key.V:
                SelectEditMode(BrushMode.CancelBuild);
                break;
            case Key.Z:
                CycleZone();
                break;
            case Key.J:
                CycleJob();
                break;
            case Key.K:
                CycleRule();
                break;
            case Key.Plus:
            case Key.Equal:
                AdjustSelectedControl(1);
                break;
            case Key.Minus:
                AdjustSelectedControl(-1);
                break;
            case Key.Y:
                ReplayCurrentLog();
                break;
            // Two jobs, in the order a player expects them: while a rectangle is
            // being dragged Esc withdraws the rectangle; with no rectangle in
            // progress it puts the brush away. The brush stays held across strokes
            // otherwise, which is what a toggled tool means.
            case Key.Escape:
                if (_dragAnchor is not null)
                {
                    CancelDrag("Esc");
                }
                else
                {
                    CancelBrush("Esc");
                }

                break;
        }
    }

    public override void _Draw()
    {
        if (_state is null)
        {
            return;
        }

        // The two control strips used to be drawn here. They are Control nodes
        // now, so the only thing left on the canvas is the map.
        DrawRect(new Rect2(Vector2.Zero, GetViewportRect().Size), new Color("#07111d"));
        DrawMap();
    }

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

        LayoutHud(GetViewportRect().Size, _uiScale);
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

    /// <summary>
    /// The left column: header, controls, an explicit world viewport and roster.
    /// Its width is negotiated with the side panel and never derived from the
    /// map's pixel width. A larger frame therefore grows this viewport and shows
    /// more world at the same camera zoom.
    /// </summary>
    private Control CreateMapColumn()
    {
        var column = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 2.2f,
            CustomMinimumSize = new Vector2(HudMapColumnMinimumWidth, 0),
        };
        column.AddThemeConstantOverride("separation", 0);

        var header = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.Fill,
            // Ends exactly where the time toolbar starts. The old summary label
            // ran 45px from y=42 and drew its second line over the buttons.
            CustomMinimumSize = new Vector2(0, ToolbarStripTop - HudTopMargin),
        };
        header.AddThemeConstantOverride("separation", 2);
        column.AddChild(header);

        _title = MakeHudLabel(15, new Color("#dbeafe"));
        _title.AutowrapMode = TextServer.AutowrapMode.Off;
        _title.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _title.Text = "DUNGEON FORTRESS  //  PROTOTYPE 1 GRAYBOX";
        header.AddChild(_title);

        _summary = MakeHudLabel(12, new Color("#bfdbfe"));
        header.AddChild(_summary);
        // The summary is always exactly two lines, and it is the one panel that
        // must never be squeezed: the line under it is the time toolbar.
        _summary.CustomMinimumSize = new Vector2(0, HudTextHeight(_summary, 2));

        column.AddChild(CreateControlStrips());
        _worldViewport = new Control
        {
            Name = "WorldViewport",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 3f,
        };
        column.AddChild(_worldViewport);

        _roster = MakeHudLabel(10, new Color("#cbd5e1"));
        _roster.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _roster.SizeFlagsStretchRatio = 1f;
        column.AddChild(_roster);
        return column;
    }

    // ---------------------------------------------------------------------
    // The two control strips
    //
    // They used to be DrawRect plus DrawString in _Draw(), with a parallel table
    // of rectangles that TryHandleToolbarClick hit-tested by hand. Two
    // descriptions of the same button, and the only thing keeping them in step
    // was that one function produced both. That is a defect by construction, and
    // deleting it is worth more than decorating it with icons.
    //
    // Every button is now a Button with an icon, a hotkey badge and a tooltip. It
    // owns its own click, so the map cannot receive one that landed on a button,
    // and it owns its own rectangle, so nothing has to agree with it.
    //
    // What each button says is decided in DungeonFortress.Presentation, which
    // does not reference Godot and is covered by unit tests running in CI. This
    // side does layout and dispatch and nothing else.
    // ---------------------------------------------------------------------
    private Control CreateControlStrips()
    {
        var band = new VBoxContainer
        {
            Name = "ControlStrips",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0, ControlStripsBandHeight),
        };
        band.AddThemeConstantOverride("separation", ControlStripSeparation);

        var time = CreateStrip(band, "TimeStrip", "#0f1d2d");
        var brush = CreateStrip(band, "BrushStrip", "#102338");
        _timeStrip = (Control)time.GetParent();
        _brushStrip = (Control)brush.GetParent();

        var controls = UiControls.Build(CurrentControlsView());
        for (var index = 0; index < controls.Count; index++)
        {
            var control = controls[index];
            var button = CreateControlButton(control, index);
            (control.Strip == UiControlStrip.Time ? time : brush).AddChild(button);
            _controlButtons.Add(button);
        }

        RefreshControls();
        return band;
    }

    /// <summary>
    /// One strip: a panel that hugs its buttons. It shrinks to its content on
    /// purpose — a strip stretched to the column would make "does the brush strip
    /// fit inside the map?" unmeasurable, and that question is the whole point of
    /// moving eight labelled rectangles to eight icons.
    /// </summary>
    private static HBoxContainer CreateStrip(Control parent, string name, string background)
    {
        var panel = new PanelContainer
        {
            Name = name,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.Fill,
        };
        var style = new StyleBoxFlat { BgColor = new Color(background) };
        style.SetContentMarginAll(ControlStripPadding);
        panel.AddThemeStyleboxOverride("panel", style);
        parent.AddChild(panel);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", ControlButtonSeparation);
        panel.AddChild(row);
        return row;
    }

    /// <summary>
    /// A toolbar button: the icon it is, the hotkey badge in the corner and the
    /// tooltip that names it. All three together, because a row of unlabelled
    /// symbols would be <em>less</em> friendly than the text it replaces — which
    /// is why RimWorld and Prison Architect ship all three too.
    /// </summary>
    private Button CreateControlButton(UiControl control, int index)
    {
        var accent = control.Strip == UiControlStrip.Time ? "#1d4ed8" : "#b45309";
        // HudButton rather than Button: the default theme draws a tooltip at font
        // size 16 with no width limit, which made it larger than the title of the
        // game and wide enough to cover the map. See HudButton.
        var button = new HudButton
        {
            Name = control.Id,
            ToggleMode = true,
            FocusMode = Control.FocusModeEnum.None,
            ClipText = false,
            TooltipText = control.Tooltip,
            CustomMinimumSize = new Vector2(ControlButtonSize, ControlButtonSize),
        };
        button.AddThemeFontSizeOverride("font_size", 10);
        button.AddThemeColorOverride("font_color", new Color("#dbeafe"));
        button.AddThemeColorOverride("font_pressed_color", new Color("#fef3c7"));
        button.AddThemeColorOverride("font_hover_color", new Color("#f8fafc"));
        button.AddThemeStyleboxOverride("normal", ControlButtonStyle("#1b2f45", "#24364b"));
        button.AddThemeStyleboxOverride("hover", ControlButtonStyle("#25415f", "#3b82f6"));
        button.AddThemeStyleboxOverride("pressed", ControlButtonStyle(accent, "#f8fafc"));
        button.AddThemeStyleboxOverride("disabled", ControlButtonStyle("#151f2b", "#1f2c3a"));
        button.Pressed += () => HandleControlPressed(index);

        // Full-rect anchors rather than a corner offset: the badge then sits in the
        // corner of whatever size the layout gives the button, instead of the size
        // it was authored against.
        var badge = new Label
        {
            Text = control.Hotkey,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        badge.AddThemeFontSizeOverride("font_size", 8);
        badge.AddThemeColorOverride("font_color", new Color("#e0f2fe"));
        // The badge sits on top of the icon, so it needs to be legible against
        // whatever the icon happens to put in that corner rather than against the
        // button background.
        badge.AddThemeColorOverride("font_outline_color", new Color("#0b1622"));
        badge.AddThemeConstantOverride("outline_size", 3);
        button.AddChild(badge);
        badge.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        badge.OffsetRight = -2;
        return button;
    }

    private static StyleBoxFlat ControlButtonStyle(string fill, string border)
    {
        var style = new StyleBoxFlat { BgColor = new Color(fill), BorderColor = new Color(border) };
        style.SetBorderWidthAll(1);
        style.SetContentMarginAll(2);
        style.SetCornerRadiusAll(2);
        return style;
    }

    /// <summary>
    /// Puts the current state on the buttons. Nothing about which buttons exist or
    /// what they say is decided here: this walks the list
    /// <see cref="UiControls.Build"/> returns, in the order it returns it, so the
    /// toolbar a player sees and the <c>ui.controls</c> an automated check reads
    /// are the same list.
    /// </summary>
    private void RefreshControls()
    {
        if (_controlButtons.Count == 0)
        {
            return;
        }

        var controls = UiControls.Build(CurrentControlsView());
        // Buttons are matched to controls by position, and a press dispatches on
        // the id at that position, so a list whose length depends on the state
        // would silently wire buttons to the wrong actions. It does not; this is
        // what says so out loud if it ever starts to.
        if (controls.Count != _controlButtons.Count)
        {
            throw new InvalidOperationException(
                $"The toolbar has {_controlButtons.Count} buttons but UiControls.Build " +
                $"returned {controls.Count} controls.");
        }

        for (var index = 0; index < _controlButtons.Count; index++)
        {
            var control = controls[index];
            var button = _controlButtons[index];
            button.Text = control.Label;
            button.TooltipText = control.Tooltip;
            button.Icon = control.Icon is null ? null : IconTexture(control.Icon);
            button.Disabled = !control.Enabled;
            // No signal: the state on the button is a projection of the game, not
            // an input to it, exactly like every other thing this adapter draws.
            button.SetPressedNoSignal(control.Active);
        }
    }

    /// <summary>
    /// The adapter state the toolbar is allowed to depend on. It is readable
    /// before a fixture exists, because <c>_Ready</c> builds the HUD first and
    /// loads the world after.
    /// </summary>
    private UiControlsViewState CurrentControlsView() => new(
        _editMode,
        _brushZone,
        _selectedJob,
        _state?.Priorities[_selectedJob] ?? 0,
        UiControls.RuleIds[_selectedRule],
        _state?.Rules[UiControls.RuleIds[_selectedRule]] ?? 0,
        _paused,
        _speed,
        _fixture,
        _state is not null && _state.Tick >= PrototypeTuning.SessionTicks);

    /// <summary>
    /// What a press does. Dispatch is by control id read from the current list,
    /// not by a number baked into the handler, so the one button whose identity
    /// changes with the state — run versus pause — needs no special case.
    /// </summary>
    private void HandleControlPressed(int index)
    {
        var controls = UiControls.Build(CurrentControlsView());
        if (index < 0 || index >= controls.Count)
        {
            return;
        }

        switch (controls[index].Id)
        {
            case UiControlIds.Run:
            case UiControlIds.Pause:
                TogglePause();
                break;
            case UiControlIds.Step:
                Advance(1);
                break;
            case UiControlIds.Speed0_5:
                SetSpeed(0.5);
                break;
            case UiControlIds.Speed1:
                SetSpeed(1.0);
                break;
            case UiControlIds.Speed4:
                SetSpeed(4.0);
                break;
            case UiControlIds.Speed16:
                SetSpeed(16.0);
                break;
            case UiControlIds.FixtureBaseline:
                LoadFixture("baseline", 1);
                break;
            case UiControlIds.FixtureNeglected:
                LoadFixture("neglected", 1);
                break;
            case UiControlIds.Replay:
                ReplayCurrentLog();
                break;
            case UiControlIds.Inspect:
                SelectEditMode(BrushMode.Inspect);
                break;
            case UiControlIds.Paint:
                SelectEditMode(BrushMode.Paint);
                break;
            case UiControlIds.Erase:
                SelectEditMode(BrushMode.Erase);
                break;
            case UiControlIds.Dig:
                SelectEditMode(BrushMode.Dig);
                break;
            case UiControlIds.DigCancel:
                SelectEditMode(BrushMode.CancelDig);
                break;
            case UiControlIds.Stockpile:
                SelectStockpileBrush();
                break;
            case UiControlIds.Build:
                SelectEditMode(BrushMode.Build);
                break;
            case UiControlIds.BuildCancel:
                SelectEditMode(BrushMode.CancelBuild);
                break;
            case UiControlIds.Zone:
                CycleZone();
                break;
            case UiControlIds.Priority:
                CycleJob();
                break;
            case UiControlIds.Rule:
                CycleRule();
                break;
        }
    }

    /// <summary>
    /// The icon pack of Issue #54, loaded by name from
    /// <see cref="UiIconManifest"/>. A file that has not been generated yet gets a
    /// placeholder, so the toolbar is complete and clickable before the art
    /// exists and dropping the real PNG in requires no code change.
    ///
    /// Every icon is resampled to its drawn size here rather than being scaled by
    /// the button. That is the lesson of the goblin pack: 96x96 art squeezed into
    /// a 20x20 rectangle by the renderer turned into mush, and nothing measured
    /// it. At the manifest's 48x48 this is an exact 2x downscale.
    /// </summary>
    private void LoadIcons()
    {
        foreach (var icon in UiIconManifest.Toolbar)
        {
            var path = UiIconManifest.ResourcePath(icon.FileName);
            if (!ResourceLoader.Exists(path) || GD.Load<Texture2D>(path) is not { } texture)
            {
                _missingIcons.Add(icon.FileName);
                continue;
            }

            _icons[icon.FileName] = Resample(texture);
        }
    }

    private static Texture2D Resample(Texture2D texture)
    {
        var image = texture.GetImage();
        if (image is null)
        {
            return texture;
        }

        if (image.GetWidth() != IconDrawSize || image.GetHeight() != IconDrawSize)
        {
            image.Resize(IconDrawSize, IconDrawSize, Image.Interpolation.Bilinear);
        }

        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// The texture a button draws: the generated icon, or the shared placeholder
    /// while the pack is still being produced. A placeholder button keeps its
    /// hotkey badge and its tooltip, so it still says what it is.
    /// </summary>
    private Texture2D IconTexture(string fileName) =>
        _icons.TryGetValue(fileName, out var texture) ? texture : PlaceholderIcon();

    private Texture2D PlaceholderIcon()
    {
        if (_iconPlaceholder is not null)
        {
            return _iconPlaceholder;
        }

        var image = Image.CreateEmpty(IconDrawSize, IconDrawSize, false, Image.Format.Rgba8);
        var fill = new Color("#334155");
        var edge = new Color("#7dd3fc");
        for (var y = 0; y < IconDrawSize; y++)
        {
            for (var x = 0; x < IconDrawSize; x++)
            {
                var border = x is 2 or IconDrawSize - 3 || y is 2 or IconDrawSize - 3;
                var inside = x is >= 2 and <= IconDrawSize - 3 && y is >= 2 and <= IconDrawSize - 3;
                image.SetPixel(x, y, inside ? (border ? edge : fill) : new Color(0, 0, 0, 0));
            }
        }

        _iconPlaceholder = ImageTexture.CreateFromImage(image);
        return _iconPlaceholder;
    }

    /// <summary>
    /// The measurable claim of this step: the brush strip is narrower than the
    /// explicit world viewport it controls. The viewport is layout, not map pixel
    /// width, so changing tile size cannot silently push the buttons off-screen.
    ///
    /// Measured at every frame size the HUD guard uses, and for the same reason: a
    /// check that only ever saw one size cannot tell a layout that fits from one
    /// that happens to.
    /// </summary>
    private void AssertControlStripsFit()
    {
        var live = GetViewportRect().Size;
        var failures = new List<string>();
        foreach (var frame in HudFitFrames())
        {
            LayoutHud(frame.Viewport, frame.UiScale);
            var worldWidth = _worldViewport!.Size.X;
            var allowedWidth = Math.Min(worldWidth, MapPixelSize.X);
            foreach (var (name, strip) in ControlStrips())
            {
                if (strip is null || strip.Size.X <= allowedWidth)
                {
                    continue;
                }

                failures.Add(
                    $"'{name}' is {FormatNumber(strip.Size.X)}px wide at viewport " +
                    $"{FormatVector(frame.Viewport)} and UI scale {FormatNumber(frame.UiScale)}, " +
                    $"wider than the {FormatNumber(allowedWidth)}px usable world width");
            }
        }

        LayoutHud(live, _uiScale);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"A control strip is wider than the world viewport in {failures.Count} place(s): " +
                string.Join("; ", failures) +
                ". A strip that overhangs the map puts controls where the player is " +
                "not looking and breaks the column the HUD is laid out in.");
        }
    }

    private (string Name, Control? Strip)[] ControlStrips() =>
    [
        ("time", _timeStrip),
        ("brush", _brushStrip),
    ];

    /// <summary>
    /// The strip measurements published next to <see cref="LabelFit"/>, so a run
    /// states the widths instead of the guard being trusted, and the icons that
    /// are still placeholders are named rather than merely absent from a picture.
    /// </summary>
    private object? ControlStripFit()
    {
        if (_worldViewport is null)
        {
            return null;
        }

        var usableWidth = Math.Min(_worldViewport.Size.X, MapPixelSize.X);
        return new
        {
            mapWidth = MapPixelSize.X,
            worldViewportWidth = _worldViewport.Size.X,
            usableWorldWidth = usableWidth,
            widths = ControlStrips()
                .Where(entry => entry.Strip is not null)
                .Select(entry => (object)new { name = entry.Name, width = entry.Strip!.Size.X })
                .ToArray(),
            iconDrawSize = IconDrawSize,
            loadedIcons = _icons.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            placeholderIcons = _missingIcons,
        };
    }

    /// <summary>
    /// The side panel: heading, inspector, legend, event feedback. The legend is
    /// static text and takes exactly the height it needs; the two panels that
    /// grow with the session share what is left in a 3:2 ratio, so the column
    /// cannot become over-subscribed the way the authored 456px one was.
    /// </summary>
    private Control CreateSideColumn()
    {
        var panel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1f,
            CustomMinimumSize = new Vector2(HudSidePanelMinimumWidth, 0),
        };
        var background = new StyleBoxFlat { BgColor = new Color("#0f1d2d"), BorderColor = new Color("#334155") };
        background.SetBorderWidthAll(1);
        background.SetContentMarginAll(10);
        panel.AddThemeStyleboxOverride("panel", background);

        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", HudPanelSeparation);
        panel.AddChild(column);

        var heading = MakeHudLabel(13, new Color("#93c5fd"));
        heading.AutowrapMode = TextServer.AutowrapMode.Off;
        heading.Text = "STATE / WHY";
        column.AddChild(heading);
        heading.CustomMinimumSize = new Vector2(0, HudTextHeight(heading, 1));

        _inspector = MakeHudLabel(11, new Color("#e2e8f0"));
        _inspector.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _inspector.SizeFlagsStretchRatio = 3;
        column.AddChild(_inspector);

        column.AddChild(CreateLegend());
        // The rule the absolute layout drew at y=400: it is what separates the
        // static map key from the live event feed above and below it.
        column.AddChild(new HSeparator { MouseFilter = Control.MouseFilterEnum.Ignore });

        _feedback = MakeHudLabel(12, new Color("#94a3b8"));
        _feedback.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _feedback.SizeFlagsStretchRatio = 2;
        column.AddChild(_feedback);
        return panel;
    }

    /// <summary>
    /// The map legend. It used to be eight <c>DrawString</c> calls at fixed
    /// offsets with no width limit, so the long stockpile rows ran past the right
    /// edge of the panel and nothing measured it. One coloured Label per row
    /// keeps the colour cue, reserves the height in the column, and puts every
    /// row under the same overflow guard as the four panels.
    /// </summary>
    private Control CreateLegend()
    {
        var legend = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.Fill,
        };
        legend.AddThemeConstantOverride("separation", 0);

        foreach (var (text, size, color) in new (string Text, int Size, string Color)[]
                 {
                     ("LEGEND", 9, "#cbd5e1"),
                     ("teal crew / red-ring goblin / bar = HP / white X = downed", 8, "#cbd5e1"),
                     ("purple QUARTERS: rest at fatigue 50+", 8, "#c4b5fd"),
                     ("light warm block = diggable rock / dark = map edge", 8, "#d6d3d1"),
                     ("amber X = dig mark / yellow bar = dig progress", 8, "#fcd34d"),
                     ("red X = unreachable / pale tile = new floor / gray dot = loose stone", 8, "#fca5a5"),
                     ("[M] stockpile: cornered square = material cell / grey box on a crew = carried stone", 8, "#e2e8f0"),
                     ("filled pip = stored / hollow blue pip = booked by a carrier on the way", 8, "#7dd3fc"),
                 })
        {
            var line = MakeHudLabel(size, new Color(color));
            line.Text = text;
            legend.AddChild(line);
            line.CustomMinimumSize = new Vector2(0, HudTextHeight(line, 1));
            _legendLines.Add(line);
        }

        return legend;
    }

    /// <summary>
    /// A HUD panel. Clipping is on for all of them: a Label that does not fit its
    /// rectangle either drops lines or draws over the panel below it, and only
    /// the first of the two is detectable, so the guard is given something it can
    /// actually see.
    /// </summary>
    private static Label MakeHudLabel(int fontSize, Color color)
    {
        var label = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ClipText = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>
    /// The height a label needs for a known number of lines, read from the font
    /// it will actually be drawn with rather than from a guessed constant.
    /// </summary>
    private static float HudTextHeight(Label label, int lines)
    {
        var fontSize = label.GetThemeFontSize("font_size");
        var lineHeight = label.GetThemeFont("font").GetHeight(fontSize) +
            label.GetThemeConstant("line_spacing");
        return Mathf.Ceil(lineHeight * lines);
    }

    private void LoadGoblinSprites()
    {
        _spritesHaveMipmaps = true;
        foreach (var state in new[] { "idle", "work", "combat", "downed" })
        {
            var path = $"res://assets/generated/goblins/goblin_{state}_v1.png";
            if (ResourceLoader.Exists(path) && GD.Load<Texture2D>(path) is { } imported)
            {
                // Overview zooms sample a sprite below its authored draw size.
                // The repository intentionally ignores generated *.import files,
                // so mipmaps are created from the imported image here instead of
                // depending on one editor profile's local import options.
                var image = imported.GetImage();
                var mipmapResult = image.GenerateMipmaps();
                if (mipmapResult != Error.Ok || !image.HasMipmaps())
                {
                    throw new InvalidOperationException(
                        $"Could not generate mipmaps for '{path}': {mipmapResult}.");
                }

                var texture = ImageTexture.CreateFromImage(image);
                _spritesHaveMipmaps &= texture.GetImage().HasMipmaps();
                _goblinSprites.Add(state, texture);
                _loadedSpriteStates.Add(state);
            }
            else
            {
                _spritesHaveMipmaps = false;
                _missingSpriteStates.Add(state);
            }
        }
    }

    private void AssertRequiredSpritesLoaded()
    {
        if (_missingSpriteStates.Count == 0)
        {
            if (_spritesHaveMipmaps)
            {
                return;
            }

            throw new InvalidOperationException(
                "Required goblin sprites loaded without mipmaps, but the camera has zoom levels below 1x.");
        }

        throw new InvalidOperationException(
            "Required goblin sprites could not be loaded: " + string.Join(", ", _missingSpriteStates) +
            ". Run scripts/run-game.ps1 so its Godot asset-import preflight can create the local .godot/import cache.");
    }

    /// <summary>
    /// A name tag pinned to a map cell. These are map annotations rather than
    /// HUD: they follow a creature, so they are positioned in map pixels and are
    /// not part of the Control layout.
    /// </summary>
    private Label MakeMapLabel(Vector2 size, int fontSize, Color color)
    {
        var label = new Label
        {
            Size = size,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        AddChild(label);
        return label;
    }

    private void LoadFixture(string fixture, int ticks)
    {
        if (fixture is not ("baseline" or "prepared" or "neglected"))
        {
            throw new ArgumentException("Fixture must be baseline, prepared or neglected.", nameof(fixture));
        }

        _fixtureLog = PrototypeCommandDocument.Load(FixturePath(fixture));
        _playerCommands.Clear();
        var world = new PrototypeWorld(_fixtureLog);
        world.RunTicks(Math.Clamp(ticks, 0, PrototypeTuning.SessionTicks));
        _world = world;
        _fixture = fixture;
        _paused = true;
        _tickAccumulator = 0;
        _selectedCreatureId = null;
        _selectedCell = null;
        RefreshState();
    }

    private void Advance(int ticks)
    {
        if (_world is null || _world.IsComplete)
        {
            return;
        }

        _world.RunTicks(Math.Min(ticks, PrototypeTuning.SessionTicks - _world.CurrentTick));
        RefreshState();
    }

    private void RefreshState()
    {
        _state = _world!.GetSnapshot();
        // One projection per state. Everything that draws or reads the map takes
        // this instance — the map, the brush, the HUD panels and the structured
        // output — so they cannot be looking at four moments of the same tick.
        _projection = MapProjection.Of(_state);
        // Only a refresh that follows RememberMotionOrigin has something to lerp
        // from. Everything else — loading a fixture, a single STEP, an accepted
        // command, a replay — is drawn at the canonical position straight away.
        _interpolatesMotion = _motionOriginPending;
        _motionOriginPending = false;
        _checksum = PrototypeScenario.Capture(_world).Checksum;
        UpdateHud();
        UpdateCreatureLabels();
        QueueRedraw();
    }

    private void UpdateCreatureLabels()
    {
        foreach (var creature in _state!.Creatures)
        {
            if (!_nameLabels.TryGetValue(creature.Id, out var label))
            {
                label = MakeMapLabel(
                    ScaleWorld(98, 17),
                    Math.Max(1, (int)Math.Round(ScaleWorld(10))),
                    CreatureColors[creature.Id]);
                _nameLabels.Add(creature.Id, label);
            }

            // A name per overlapping creature made the economy unreadable. Names are
            // now an intentional inspection affordance: selected or hovered only.
            var visible = creature.Id == _selectedCreatureId || creature.Id == _hoverCreatureId;
            label.Visible = visible;
            if (!visible)
            {
                continue;
            }

            label.Text = $"{creature.Name} {CreatureStateShort(creature)}";
            // Follows the interpolated body, not the canonical tile, so the tag
            // does not snap a whole tile ahead of the creature it names.
            label.Position = CreatureRenderCenter(creature) +
                new Vector2(
                    ScaleWorld(2) - (_tileSize / 2f),
                    -ScaleWorld(14) - (_tileSize / 2f));
        }
    }

    /// <summary>
    /// What the four panels currently say is decided in
    /// <c>DungeonFortress.Presentation</c>, which does not reference Godot and is
    /// covered by unit tests running in CI. All this node does is put the
    /// resulting strings on the labels, so the adapter can no longer be the only
    /// place a wording change is observable.
    /// </summary>
    private void UpdateHud()
    {
        var panels = HudText.Build(CurrentHudView(), _projection);
        _inspector!.Text = panels.Inspector;
        _summary!.Text = panels.Summary;
        _feedback!.Text = panels.Feedback;
        _roster!.Text = panels.Roster;
        RefreshControls();
    }

    /// <summary>
    /// Verification-only fault injection. It changes no game state and exists so
    /// the HUD guard proves it rejects a real overflow at the required logical
    /// width instead of merely reporting that today's layout happens to fit.
    /// </summary>
    private void InjectHudGuardRegression()
    {
        _inspector!.Text = string.Join(
            "\n",
            Enumerable.Range(1, 80).Select(line => $"HUD guard regression line {line}"));
    }

    /// <summary>
    /// The adapter state the HUD text is allowed to depend on, gathered in one
    /// place. Everything else about this node — labels, viewport, brushes,
    /// pointer — stays on this side of the seam on purpose.
    /// </summary>
    private HudViewState CurrentHudView() => new(
        _state!,
        _fixture,
        _checksum,
        _paused,
        _speed,
        _selectedCreatureId,
        _selectedCell,
        _controlFeedback,
        _playerCommands,
        _diagnostics.Count);

    /// <summary>
    /// The summary as it will read when the party is over, one string per end.
    ///
    /// No entry point ever draws this frame: a smoke run stops at tick 1 and a
    /// screenshot run stops wherever it was told to, so the one frame the owner
    /// actually reads their result in was the one frame nothing measured. It is
    /// also the widest the line ever gets — `DOMAIN RAIDED · N/4 repelled` runs
    /// eight characters past the longest countdown — so it is precisely the case
    /// that would wrap onto a third line over the time toolbar.
    ///
    /// The strings come from the real <see cref="HudText"/> on the real snapshot
    /// with only the session result substituted, so this measures the shipping
    /// wording rather than a hand-written imitation of it.
    /// </summary>
    private (string Outcome, string Text)[] TerminalSummaries()
    {
        if (_state is null)
        {
            return [];
        }

        var view = CurrentHudView();
        return new[] { ("held", _state.Waves.Count), ("raided", 1), ("fallen", 0) }
            .Select(end => (end.Item1, HudText.Summary(view with
            {
                Snapshot = _state with
                {
                    SessionResult = _state.SessionResult with
                    {
                        Outcome = end.Item1,
                        WavesRepelled = end.Item2,
                    },
                },
            })))
            .ToArray();
    }

    /// <summary>
    /// Puts a candidate summary into the real label and measures it at a given
    /// frame size. The text is left in place for the caller to keep measuring
    /// and is put back by <see cref="RestoreSummary"/>.
    /// </summary>
    private (int Needed, int Shown) MeasureSummary(string text, HudFitFrame frame)
    {
        _summary!.Text = text;
        LayoutHud(frame.Viewport, frame.UiScale);
        return (_summary.GetLineCount(), _summary.GetVisibleLineCount());
    }

    private void RestoreSummary()
    {
        if (_state is not null)
        {
            _summary!.Text = HudText.Summary(CurrentHudView());
        }
    }

    /// <summary>
    /// Every piece of HUD text the overflow guard measures. The four panels the
    /// golden UI state records come first; the header and the legend rows are
    /// here too, because a Control layout can squeeze them just as easily and
    /// nothing else would notice.
    /// </summary>
    private (string Name, Label? Label)[] HudLabels() =>
    [
        ("summary", _summary),
        ("inspector", _inspector),
        ("feedback", _feedback),
        ("roster", _roster),
        ("title", _title),
        .. _legendLines.Select((label, index) => ($"legend[{index}]", (Label?)label)),
    ];

    /// <summary>
    /// What the HUD currently says, as text rather than as pixels. Every branch of
    /// the inspector then becomes an ordinary testable artifact: pick the frame
    /// with <c>--screenshot-ticks</c>, point at a tile with <c>--select-cell</c>
    /// and assert a substring of <c>ui.inspector</c>.
    ///
    /// Nothing here depends on the camera. Pixel positions, the visible tile range
    /// and the viewport size are deliberately absent, because ADR 0008 drops the
    /// fixed 960x540 frame and those values stop being stable.
    /// </summary>
    private object UiText() => new
    {
        summary = _summary?.Text,
        inspector = _inspector?.Text,
        feedback = _feedback?.Text,
        roster = _roster?.Text,
        controlFeedback = _controlFeedback,
        editMode = _editMode.ToString(),
        brushZone = _brushZone.ToString(),
        selectedCell = _selectedCell is { } selected ? new[] { selected.X, selected.Y } : null,
        selectedCreatureId = _selectedCreatureId,
        // The toolbar as text. "Which brushes are available, what do they do and
        // which one is held" stops being a question only a screenshot answers,
        // which is what makes the icon pass checkable at all.
        controls = UiControls.Build(CurrentControlsView())
            .Select(control => (object)new
            {
                id = control.Id,
                label = control.Label,
                hotkey = control.Hotkey,
                tooltip = control.Tooltip,
                active = control.Active,
                enabled = control.Enabled,
                icon = control.Icon,
            })
            .ToArray(),
        // The rectangle in progress, so a drag is observable without a picture.
        // It is null unless the button is actually down, which is also the claim
        // "a cancelled drag left nothing behind".
        selection = PendingStroke() is { } stroke
            ? (object)new
            {
                mode = stroke.Mode.ToString(),
                tiles = stroke.Tiles.Count,
                rectangle = stroke.RectangleTiles,
                refusal = stroke.Refusal,
            }
            : null,
        // Intent accepted for this tick that the tick has not applied yet: the
        // marking the map draws over canonical state, and the priority changes
        // that decide how those marks read. "It showed up straight away" is then a
        // field in a structured run rather than something judged from a
        // screenshot. Null whenever nothing waits, which is every frame of
        // free-running time.
        pending = _projection is { HasPendingIntent: true } waiting
            ? (object)new
            {
                tick = _state!.Tick,
                commands = waiting.PendingCommandCount,
                digMarks = Tiles(waiting.PendingDigMarks),
                digWithdrawals = Tiles(waiting.PendingDigWithdrawals),
                buildMarks = Tiles(waiting.PendingBuildMarks),
                buildWithdrawals = Tiles(waiting.PendingBuildWithdrawals),
                stockpileCells = Tiles(waiting.PendingStockpileCells),
                priorities = waiting.PendingPriorities
                    .OrderBy(pair => pair.Key)
                    .Select(pair => (object)new { job = pair.Key.ToString(), value = pair.Value })
                    .ToArray(),
            }
            : null,
    };

    private static int[][] Tiles(IReadOnlyList<GridPoint> tiles) =>
        [.. tiles.Select(tile => new[] { tile.X, tile.Y })];

    /// <summary>
    /// Frame and UI-scale pairs the HUD is required to hold all of its text at.
    /// The live pair is always included. Larger UI scales are paired with larger
    /// frames because a scale that cannot fit is rejected rather than clipped.
    ///
    /// A size that is missing here is not "unsupported": it is unmeasured. The
    /// current text does not fit the old 960x540 frame at readable sizes — the
    /// side column needs about 33 lines and that frame offers about 29 — which is
    /// exactly the deficit Issue #28 measured and this Issue had to clear.
    /// </summary>
    private readonly record struct HudFitFrame(Vector2 Viewport, double UiScale)
    {
        public Vector2 LogicalViewport => Viewport / (float)UiScale;
    }

    private HudFitFrame[] HudFitFrames() =>
        new[]
        {
            new HudFitFrame(GetViewportRect().Size, _uiScale),
            new HudFitFrame(new Vector2(1280, 720), 1.0),
            new HudFitFrame(new Vector2(1366, 768), 1.0),
            new HudFitFrame(new Vector2(1600, 900), 1.0),
            new HudFitFrame(new Vector2(1024, 768), 1.0),
            new HudFitFrame(new Vector2(1920, 1080), 1.25),
            new HudFitFrame(new Vector2(2048, 1440), 2.0),
        }
        .DistinctBy(frame => (frame.LogicalViewport.X, frame.LogicalViewport.Y))
        .ToArray();

    /// <summary>
    /// Lays the HUD out at a given frame size and waits for nothing. Godot sorts
    /// containers on a deferred pass, so a guard that ran in <c>_Ready</c> without
    /// this would measure rectangles nobody had laid out yet. Notifying the
    /// subtree runs every container's sort synchronously, parent first, which is
    /// the same placement a frame would produce.
    ///
    /// Two passes, because a wrapped legend row's height depends on the width the
    /// first pass hands it. Asking for that height back is what makes a narrow
    /// frame take space away from the panels that can spare it instead of
    /// quietly clipping the legend.
    /// </summary>
    private void LayoutHud(Vector2 size, double uiScale)
    {
        _hudRoot!.Scale = Vector2.One * (float)uiScale;
        _hudRoot.Size = size / (float)uiScale;
        _hudRoot.PropagateNotification((int)Container.NotificationSortChildren);
        foreach (var line in _legendLines)
        {
            line.CustomMinimumSize = new Vector2(0, HudTextHeight(line, line.GetLineCount()));
        }

        _hudRoot.PropagateNotification((int)Container.NotificationSortChildren);
        LayoutWorldViewportMasks(size);
    }

    /// <summary>
    /// A <see cref="Label"/> that does not fit its rectangle silently loses text:
    /// unclipped it draws over the panel below it, clipped it drops the
    /// overflowing lines. Both happened in Issue #26 and both were found by eye on
    /// a PNG.
    ///
    /// The check is made against the rectangle the layout produced and never
    /// against a window constant, because ADR 0008 drops the fixed 960x540 frame.
    /// Since the HUD became a Control tree the measurement is only meaningful
    /// *after* a layout pass, which is the opposite of what the absolute layout
    /// needed: a container gives a label its size, so the size is the designed one
    /// and an unclipped label can no longer re-expand to its own content.
    /// <see cref="LayoutHud"/> forces that pass, so <c>_Ready</c> is still a valid
    /// place to run this on every entry point.
    /// </summary>
    /// <param name="strict">
    /// Kept so that the <c>--strict-hud-fit</c> flag parsed in <c>_Ready</c> still
    /// compiles. There is no longer a recorded deficit to ignore — every panel
    /// must hold all of its text on every run — so strict and ordinary runs are
    /// the same check. Removing the now-inert flag touches <c>_Ready</c>, which
    /// Issue #39 owns in parallel.
    /// </param>
    private void AssertLabelsFit(bool strict = false)
    {
        _ = strict;
        var live = GetViewportRect().Size;
        var failures = new List<string>();
        var terminal = TerminalSummaries();
        foreach (var frame in HudFitFrames())
        {
            LayoutHud(frame.Viewport, frame.UiScale);
            foreach (var (name, label) in HudLabels())
            {
                if (label is null)
                {
                    continue;
                }

                var needed = label.GetLineCount();
                var shown = label.GetVisibleLineCount();
                if (needed > shown)
                {
                    failures.Add(
                        $"'{name}' needs {needed} lines but only {shown} fit in " +
                        $"{FormatVector(label.Size)} at viewport {FormatVector(frame.Viewport)}, " +
                        $"UI scale {FormatNumber(frame.UiScale)}");
                }
            }

            foreach (var (outcome, text) in terminal)
            {
                var (needed, shown) = MeasureSummary(text, frame);
                if (needed > shown)
                {
                    failures.Add(
                        $"summary at the end of a party ('{outcome}') needs {needed} " +
                        $"lines but only {shown} fit at viewport {FormatVector(frame.Viewport)}, " +
                        $"UI scale {FormatNumber(frame.UiScale)}");
                }
            }
        }

        RestoreSummary();
        LayoutHud(live, _uiScale);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"The HUD loses text in {failures.Count} place(s): " +
                string.Join("; ", failures) +
                ". Text that does not fit its rectangle is dropped or drawn over " +
                "the panel below it.");
        }
    }

    /// <summary>
    /// The measurements <see cref="AssertLabelsFit"/> acts on, published so a run
    /// states what the guard had to work with instead of the guard being trusted.
    /// The viewport is reported next to them, because every height below is a
    /// share of it rather than a constant.
    /// </summary>
    private object LabelFit()
    {
        var viewport = GetViewportRect().Size;
        return new
        {
            viewport = new[] { viewport.X, viewport.Y },
            uiScale = _uiScale,
            checkedViewports = HudFitFrames()
                .Select(frame => new[] { frame.Viewport.X, frame.Viewport.Y })
                .Distinct()
                .ToArray(),
            checkedFrames = HudFitFrames()
                .Select(frame => (object)new
                {
                    viewport = new[] { frame.Viewport.X, frame.Viewport.Y },
                    logicalViewport = new[]
                    {
                        frame.LogicalViewport.X,
                        frame.LogicalViewport.Y,
                    },
                    uiScale = frame.UiScale,
                })
                .ToArray(),
            labels = HudLabels()
                .Where(entry => entry.Label is not null)
                .Select(entry => (object)new
                {
                    name = entry.Name,
                    neededLines = entry.Label!.GetLineCount(),
                    visibleLines = entry.Label.GetVisibleLineCount(),
                    hardLines = (entry.Label.Text ?? string.Empty).Split('\n').Length,
                    width = entry.Label.Size.X,
                    height = entry.Label.Size.Y,
                })
                .ToArray(),
            // The frame the owner reads their result in, stated rather than
            // trusted: what each end of a party needs and what fits, at the
            // narrowest frame the guard checks.
            terminalSummaries = TerminalSummaryFit(),
        };
    }

    private object[] TerminalSummaryFit()
    {
        var live = GetViewportRect().Size;
        var narrowest = HudFitFrames()
            .OrderBy(frame => frame.Viewport.X / frame.UiScale)
            .First();
        var measured = TerminalSummaries()
            .Select(end =>
            {
                var (needed, shown) = MeasureSummary(end.Text, narrowest);
                return (object)new
                {
                    outcome = end.Outcome,
                    viewport = new[] { narrowest.Viewport.X, narrowest.Viewport.Y },
                    uiScale = narrowest.UiScale,
                    neededLines = needed,
                    visibleLines = shown,
                    text = end.Text,
                };
            })
            .ToArray();
        RestoreSummary();
        LayoutHud(live, _uiScale);
        return measured;
    }

    private object ViewState()
    {
        var viewport = GetViewportRect().Size;
        var world = _worldViewport is null ? (Rect2?)null : WorldViewportScreenRect();
        var camera = _worldViewport is null ? (CameraFrame?)null : CurrentCameraFrame();
        var cameraNodePosition = _camera?.Position;
        return new
        {
            frameSize = new[] { viewport.X, viewport.Y },
            requestedFrameSize = _requestedFrameSize is { } requested
                ? new[] { requested.Width, requested.Height }
                : null,
            worldViewport = world is { } worldRect
                ? new[]
                {
                    worldRect.Position.X,
                    worldRect.Position.Y,
                    worldRect.Size.X,
                    worldRect.Size.Y,
                }
                : null,
            tileSize = _tileSize,
            goblinWorldSize = CameraView.GoblinDrawSize(_tileSize),
            goblinScreenSize = CameraView.GoblinDrawSize(_tileSize) * _cameraZoom,
            cameraPosition = new[] { _cameraCenter.X, _cameraCenter.Y },
            cameraNodePosition = cameraNodePosition is { } nodePosition
                ? new[] { nodePosition.X, nodePosition.Y }
                : null,
            cameraZoom = _cameraZoom,
            zoomLevel = Array.IndexOf(CameraView.ZoomLevels.ToArray(), _cameraZoom),
            visibleWorldSize = camera is { } frame
                ? new[]
                {
                    frame.VisibleWorldSize.Width,
                    frame.VisibleWorldSize.Height,
                }
                : null,
            uiScale = _uiScale,
            displayServer = DisplayServer.GetName(),
            textureFilter = TextureFilter.ToString(),
            spriteMipmaps = _spritesHaveMipmaps,
            cameraInputChecks = _cameraInputChecks,
            cameraBoundsChecks = _cameraBoundsChecks,
            cameraPanChecks = _cameraPanChecks,
            cameraTransformChecks = _cameraTransformChecks,
            cameraSynchronizedAfterLayout = _cameraSynchronizedAfterLayout,
            hudInputRejected = _hudInputRejected,
        };
    }

    private void DrawMap()
    {
        DrawRect(new Rect2(Vector2.Zero, MapPixelSize), new Color("#111827"));
        var rockTiles = _state!.Map.RockTiles.ToHashSet();
        var diggableTiles = _state.Map.DiggableTiles.ToHashSet();
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                var cell = new GridPoint(x, y);
                var rect = new Rect2(CellTopLeft(cell), new Vector2(_tileSize - 1, _tileSize - 1));
                if (!rockTiles.Contains(cell))
                {
                    DrawRect(rect, BaseTileColor(cell));
                }
            }
        }

        DrawBuildSites();
        DrawStockpileCells();

        foreach (var bed in _state!.Beds)
        {
            DrawCircle(
                CellCenter(bed.Position),
                ScaleWorld(5),
                bed.IsRipe ? new Color("#bef264") : new Color("#4d7c0f"));
        }

        foreach (var loose in _state.LooseItems)
        {
            var color = loose.Resource switch
            {
                ResourceKind.Meal => new Color("#fde68a"),
                ResourceKind.Stone => new Color("#cbd5e1"),
                _ => new Color("#a3e635"),
            };
            var center = CellCenter(loose.Position);
            DrawCircle(center, ScaleWorld(3 + Math.Min(3, loose.Quantity)), color);
            if (loose.Resource == ResourceKind.Stone)
            {
                // A dark rim separates loose stone from a pale meal at a glance.
                DrawArc(
                    center,
                    ScaleWorld(4.5f),
                    0,
                    Mathf.Tau,
                    12,
                    new Color("#475569"),
                    ScaleWorld(1.5f));
            }
        }

        DrawElevatedWorld(rockTiles, diggableTiles);
        // Flat informational marks are projected above elevated geometry. A wall
        // must not erase one side of a zone or the destination of an active job.
        DrawZoneOutlines();
        DrawJobRoutes();
        // A dig mark is a player-intent overlay on the wall, not wall material.
        // Drawing it after the depth pass keeps it readable on both top and face.
        DrawDigDesignations(rockTiles);
        DrawBodyInformationOverlays();
        DrawCellInteractionOverlays(rockTiles);
        DrawZoneLabels();
        DrawBrushPreview(rockTiles);
    }

    private void DrawZoneOutlines()
    {
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                var cell = new GridPoint(x, y);
                var rect = new Rect2(
                    CellTopLeft(cell),
                    new Vector2(_tileSize - 1, _tileSize - 1));
                foreach (var zone in _projection!.ZonesAt(cell))
                {
                    DrawRect(rect.Grow(-3), ZoneColor(zone), false, 1.5f);
                }
            }
        }
    }

    private void DrawJobRoutes()
    {
        foreach (var job in _state!.Jobs)
        {
            var color = HaulRouteColor(job);
            DrawLine(
                CellCenter(job.Origin),
                CellCenter(job.Target),
                color with { A = 0.35f },
                ScaleWorld(1.0f));
            DrawCircle(CellCenter(job.Target), ScaleWorld(3.2f), color);

            // A booked stockpile cell is part of the route even before pickup, so
            // the player can see where this pile is going.
            if (job.StoreCell is { } storeCell && storeCell != job.Target)
            {
                DrawLine(
                    CellCenter(job.Target),
                    CellCenter(storeCell),
                    color with { A = 0.25f },
                    ScaleWorld(1.0f));
            }
        }
    }

    /// <summary>
    /// Walls, bodies and tall structures share one painter's-order pass. The
    /// <see cref="WorldRenderItem"/> for a body is built from its interpolated
    /// center, so changing alpha can change depth without changing a tick or the
    /// canonical snapshot.
    /// </summary>
    private void DrawElevatedWorld(
        IReadOnlySet<GridPoint> rockTiles,
        IReadOnlySet<GridPoint> diggableTiles)
    {
        var items = new List<WorldRenderItem>();
        foreach (var cell in rockTiles)
        {
            items.Add(WorldRenderGeometry.ForCell(
                WorldRenderKind.Wall,
                GridCellId.Encode(cell, PrototypeTuning.MapWidth),
                cell,
                _tileSize));
        }

        var structures = _state!.Stations
            .Where(station => station.Kind == TileKind.Post)
            .ToDictionary(station =>
                GridCellId.Encode(station.Position, PrototypeTuning.MapWidth));
        foreach (var (stableId, station) in structures)
        {
            items.Add(WorldRenderGeometry.ForCell(
                WorldRenderKind.Structure,
                stableId,
                station.Position,
                _tileSize));
        }

        var creatureCenters = _state.Creatures.ToDictionary(
            creature => creature.Id,
            CreatureRenderCenter);
        foreach (var creature in _state.Creatures)
        {
            var center = creatureCenters[creature.Id];
            items.Add(WorldRenderGeometry.ForBody(
                WorldRenderKind.Creature,
                creature.Id,
                new ViewPoint(center.X, center.Y)));
        }

        var raiderCenters = _state.Raiders
            .Where(raider => raider.Mode != RaiderMode.Escaped)
            .ToDictionary(raider => raider.Id, RaiderRenderCenter);
        foreach (var raider in _state.Raiders.Where(item => item.Mode != RaiderMode.Escaped))
        {
            var center = raiderCenters[raider.Id];
            items.Add(WorldRenderGeometry.ForBody(
                WorldRenderKind.Raider,
                raider.Id,
                new ViewPoint(center.X, center.Y)));
        }

        var creatures = _state.Creatures.ToDictionary(creature => creature.Id);
        var raiders = _state.Raiders.ToDictionary(raider => raider.Id);
        foreach (var item in WorldRenderOrder.BackToFront(items))
        {
            switch (item.Kind)
            {
                case WorldRenderKind.Wall:
                    var cell = GridCellId.Decode(item.StableId, PrototypeTuning.MapWidth);
                    DrawWall(
                        cell,
                        WallTopology.SelectVariant(cell, rockTiles),
                        diggableTiles.Contains(cell));
                    break;
                case WorldRenderKind.Structure:
                    DrawBuiltPost(structures[item.StableId]);
                    break;
                case WorldRenderKind.Creature:
                    DrawCreature(creatures[item.StableId], creatureCenters[item.StableId]);
                    break;
                case WorldRenderKind.Raider:
                    DrawRaider(raiders[item.StableId], raiderCenters[item.StableId]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(item.Kind), item.Kind, null);
            }
        }
    }

    /// <summary>
    /// Graybox three-quarter wall geometry. The full tile is the connected top
    /// mass; an exposed observer-facing side replaces its lower strip with a dark
    /// facade. Missing cardinal neighbours add only outer seams, so connected
    /// rock has no internal checkerboard grid.
    /// </summary>
    private void DrawWall(GridPoint cell, WallTileVariant variant, bool isDiggable)
    {
        var topLeft = CellTopLeft(cell);
        var facadeHeight = ScaleWorld(8);
        var facadeOverhang = ScaleWorld(3);
        // The cell is the wall's footprint. Its top rises into the cell behind,
        // while the facade ends at the footprint's lower edge. This overlap is
        // what gives Y-order something visible to occlude.
        var visualTopLeft = topLeft - new Vector2(0, facadeHeight);
        var tile = new Vector2(_tileSize, _tileSize);
        DrawRect(new Rect2(visualTopLeft, tile), WallTopColor(isDiggable));

        var edgeWidth = ScaleWorld(1.25f);
        var brightEdge = WallEdgeColor(isDiggable);
        var darkEdge = WallFacadeColor(isDiggable);
        var exposed = WallTopology.ExposedSides(variant);
        if (exposed.HasFlag(WallNeighbors.North))
        {
            DrawLine(
                visualTopLeft,
                visualTopLeft + new Vector2(_tileSize, 0),
                brightEdge,
                edgeWidth);
        }

        if (exposed.HasFlag(WallNeighbors.West))
        {
            DrawLine(
                visualTopLeft,
                topLeft + new Vector2(0, _tileSize),
                darkEdge,
                edgeWidth);
        }

        if (exposed.HasFlag(WallNeighbors.East))
        {
            DrawLine(
                visualTopLeft + new Vector2(_tileSize, 0),
                topLeft + tile,
                darkEdge,
                edgeWidth);
        }

        if (!WallTopology.HasFrontFacade(variant))
        {
            return;
        }

        var facadeTop = topLeft.Y + _tileSize - facadeHeight;
        DrawRect(
            new Rect2(
                new Vector2(topLeft.X, facadeTop),
                new Vector2(_tileSize, facadeHeight + facadeOverhang)),
            WallFacadeColor(isDiggable));
        DrawLine(
            new Vector2(topLeft.X, facadeTop),
            new Vector2(topLeft.X + _tileSize, facadeTop),
            WallLipColor(isDiggable),
            ScaleWorld(2));
        if (exposed.HasFlag(WallNeighbors.West))
        {
            DrawLine(
                topLeft + new Vector2(0, _tileSize),
                topLeft + new Vector2(0, _tileSize + facadeOverhang),
                darkEdge,
                edgeWidth);
        }

        if (exposed.HasFlag(WallNeighbors.East))
        {
            DrawLine(
                topLeft + tile,
                topLeft + new Vector2(_tileSize, _tileSize + facadeOverhang),
                darkEdge,
                edgeWidth);
        }

        DrawLine(
            topLeft + new Vector2(0, _tileSize + facadeOverhang),
            topLeft + new Vector2(_tileSize, _tileSize + facadeOverhang),
            new Color("#100d0c"),
            edgeWidth);
    }

    private static Color WallTopColor(bool isDiggable) =>
        isDiggable
            ? new Color("#6b6157")
            : new Color("#2a2522");

    private static Color WallFacadeColor(bool isDiggable) =>
        isDiggable
            ? new Color("#403832")
            : new Color("#171310");

    private static Color WallEdgeColor(bool isDiggable) =>
        isDiggable
            ? new Color("#a99682")
            : new Color("#55483f");

    private static Color WallLipColor(bool isDiggable) =>
        isDiggable
            ? new Color("#8b7968")
            : new Color("#3c332e");

    private Rect2 CellInteractionRect(
        GridPoint cell,
        IReadOnlySet<GridPoint> rockTiles)
    {
        if (!rockTiles.Contains(cell))
        {
            return new Rect2(
                CellTopLeft(cell),
                new Vector2(_tileSize - 1, _tileSize - 1));
        }

        return WallVisualRect(cell, WallTopology.SelectVariant(cell, rockTiles));
    }

    private Rect2 WallVisualRect(GridPoint cell, WallTileVariant variant)
    {
        var facadeHeight = ScaleWorld(8);
        var facadeOverhang = ScaleWorld(3);
        var height = _tileSize +
            (WallTopology.HasFrontFacade(variant)
                ? facadeHeight + facadeOverhang
                : 0);
        return new Rect2(
            CellTopLeft(cell) - new Vector2(0, facadeHeight),
            new Vector2(_tileSize - 1, height - 1));
    }

    private void DrawCreature(PrototypeCreatureSnapshot creature, Vector2 center)
    {
        // The body and its carried item hang off the interpolated point supplied
        // to Y-order. Informational affordances are projected in a later pass so
        // wall volume can occlude the body without erasing its state.
        var color = DefenderColor(creature);
        // The generated character states serve both factions; the outline is the
        // stable team cue (teal crew, red-raider ring).
        DrawCircle(center, ScaleWorld(9), color);
        DrawGoblin(center, CrewSpriteKey(creature));
        if (creature.Carrying is ResourceKind.Stone)
        {
            // Stone rides as a rimmed grey square, the same shape a stockpile pip
            // uses, so "carrying" and "stored" read as the same material.
            DrawRect(
                new Rect2(center + ScaleWorld(3, -9), ScaleWorld(6, 6)),
                new Color("#e2e8f0"));
            DrawRect(
                new Rect2(center + ScaleWorld(3, -9), ScaleWorld(6, 6)),
                new Color("#0f172a"),
                false,
                ScaleWorld(1.0f));
        }
        else if (creature.Carrying is not null)
        {
            DrawCircle(
                center + ScaleWorld(6, -6),
                ScaleWorld(2.5f),
                creature.Carrying == ResourceKind.Meal
                    ? new Color("#fde68a")
                    : new Color("#a3e635"));
        }
    }

    private void DrawRaider(PrototypeRaiderSnapshot raider, Vector2 center)
    {
        DrawCircle(center, ScaleWorld(9), new Color("#7f1d1d"));
        DrawGoblin(center, RaiderSpriteKey(raider));
    }

    private void DrawBodyInformationOverlays()
    {
        foreach (var creature in _state!.Creatures)
        {
            DrawCreatureInformation(creature, CreatureRenderCenter(creature));
        }

        foreach (var raider in _state.Raiders.Where(item => item.Mode != RaiderMode.Escaped))
        {
            DrawRaiderInformation(raider, RaiderRenderCenter(raider));
        }
    }

    private void DrawCreatureInformation(
        PrototypeCreatureSnapshot creature,
        Vector2 center)
    {
        var color = DefenderColor(creature);
        if (creature.Mode == CreatureMode.Downed)
        {
            DrawDownedMark(center);
        }

        DrawHpBar(center + ScaleWorld(-7, 8), creature.Hp, creature.MaxHp, color);
        DrawCircle(
            center + ScaleWorld(7, -7),
            ScaleWorld(2.25f),
            CreatureStateColor(creature));

        if (_selectedCreatureId == creature.Id)
        {
            DrawArc(
                center,
                ScaleWorld(10),
                0,
                Mathf.Tau,
                16,
                new Color("#ffffff"),
                ScaleWorld(2));
        }
    }

    private void DrawRaiderInformation(PrototypeRaiderSnapshot raider, Vector2 center)
    {
        DrawHpBar(
            center + ScaleWorld(-7, 9),
            raider.Hp,
            PrototypeTuning.RaiderHp,
            new Color("#fb7185"));
        if (raider.Mode == RaiderMode.Downed)
        {
            DrawDownedMark(center);
        }
        else
        {
            DrawCircle(center + ScaleWorld(6, -6), ScaleWorld(2), new Color("#fecaca"));
        }
    }

    private void DrawDownedMark(Vector2 center)
    {
        DrawLine(
            center + ScaleWorld(-5, -5),
            center + ScaleWorld(5, 5),
            new Color("#f8fafc"),
            ScaleWorld(2));
        DrawLine(
            center + ScaleWorld(5, -5),
            center + ScaleWorld(-5, 5),
            new Color("#f8fafc"),
            ScaleWorld(2));
    }

    /// <summary>
    /// Input affordances belong above world depth: a selected wall and the legal
    /// targets of a held brush must remain visible even when the wall itself was
    /// deliberately drawn after a body behind it.
    /// </summary>
    private void DrawCellInteractionOverlays(IReadOnlySet<GridPoint> rockTiles)
    {
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                var cell = new GridPoint(x, y);
                var rect = CellInteractionRect(cell, rockTiles);

                // The rule is read from the same function the stroke uses, so an
                // outlined cell and an accepted cell cannot be different sets.
                if (LegalTargetOutline() is { } outline &&
                    BrushSelection.Accepts(_projection!, _editMode, _brushZone, cell))
                {
                    DrawRect(rect.Grow(-2), outline, false, 1.0f);
                }

                if (_selectedCell == cell)
                {
                    DrawRect(rect.Grow(-1), new Color("#f8fafc"), false, 2.0f);
                }
            }
        }
    }

    /// <summary>
    /// The colour every legal target of the held brush is outlined with, or
    /// <c>null</c> for a brush whose targets are already obvious on the map — a
    /// dig mark and a blueprint are drawn as themselves, so outlining them again
    /// would add noise rather than an affordance.
    /// </summary>
    private Color? LegalTargetOutline() => _editMode switch
    {
        BrushMode.Dig => new Color("#fbbf24") with { A = 0.75f },
        BrushMode.Build => new Color("#5eead4") with { A = 0.55f },
        BrushMode.Paint when _brushZone == ZoneKind.MaterialStockpile =>
            new Color("#cbd5e1") with { A = 0.55f },
        _ => null,
    };

    /// <summary>The colour a brush marks with when the cell is a legal target.</summary>
    private Color BrushAccent() => _editMode switch
    {
        BrushMode.Dig => new Color("#f59e0b"),
        BrushMode.CancelDig => new Color("#38bdf8"),
        BrushMode.Build => new Color("#2dd4bf"),
        BrushMode.CancelBuild => new Color("#38bdf8"),
        _ => ZoneColor(_brushZone),
    };

    /// <summary>
    /// What the brush would do if the button were released now.
    ///
    /// While a rectangle is being dragged this is the whole selection, cell by
    /// cell — accepted cells in the brush colour, cells the command will skip in
    /// red — plus the count, because "how many cells will this affect?" is the one
    /// question a highlighted area does not answer. With no drag in progress it is
    /// the single cell under the cursor, which is the same thing with one cell in
    /// it.
    /// </summary>
    private void DrawBrushPreview(IReadOnlySet<GridPoint> rockTiles)
    {
        if (_editMode == BrushMode.Inspect || _state is null)
        {
            return;
        }

        if (PendingStroke() is { } stroke && _dragAnchor is { } anchor)
        {
            var corner = _dragCurrent ?? anchor;
            var accepted = stroke.Tiles.ToHashSet();
            var hasBounds = false;
            var selectionBounds = default(Rect2);
            foreach (var cell in BrushSelection.Rectangle(anchor, corner))
            {
                var color = accepted.Contains(cell) ? BrushAccent() : new Color("#ef4444");
                var tile = CellInteractionRect(cell, rockTiles);
                DrawRect(tile.Grow(-ScaleWorld(1)), color with { A = 0.32f });
                selectionBounds = hasBounds ? selectionBounds.Merge(tile) : tile;
                hasBounds = true;
            }

            DrawRect(
                selectionBounds.Grow(-ScaleWorld(1)),
                new Color("#f8fafc"),
                false,
                ScaleWorld(1.5f));
            DrawSelectionCount(selectionBounds.Position, stroke);
            return;
        }

        if (_hoverCell is not { } hovered || !IsMapCell(hovered))
        {
            return;
        }

        var preview = CellInteractionRect(hovered, rockTiles);
        var previewColor = BrushSelection.Accepts(_projection!, _editMode, _brushZone, hovered)
            ? BrushAccent()
            : new Color("#ef4444");
        DrawRect(preview.Grow(-ScaleWorld(1)), previewColor with { A = 0.32f });
        DrawRect(
            preview.Grow(-ScaleWorld(1)),
            new Color("#f8fafc"),
            false,
            ScaleWorld(1.5f));
    }

    /// <summary>
    /// The number of cells the command will carry, drawn on the selection itself.
    /// It is the accepted count and not the area of the rectangle: a drag across
    /// floor and rock states how much of it the brush will actually take.
    /// </summary>
    private void DrawSelectionCount(Vector2 topLeft, BrushStroke stroke)
    {
        var width = ScaleWorld(58);
        var height = ScaleWorld(14);
        var text = stroke.Tiles.Count == 1 ? "1 cell" : $"{stroke.Tiles.Count} cells";

        // Kept inside the map on both axes. Above the selection when there is room
        // and inside its first cell when there is not: HUD masks cover every
        // canvas pixel outside the explicit world viewport, so an overhanging
        // caption would be hidden rather than appear inside the HUD.
        var preferredY = topLeft.Y - height - ScaleWorld(3) >= 0
            ? topLeft.Y - height - ScaleWorld(3)
            : topLeft.Y + ScaleWorld(3);
        var box = new Vector2(
            Math.Clamp(topLeft.X, 0, MapPixelSize.X - width),
            Math.Clamp(preferredY, 0, MapPixelSize.Y - height));

        DrawRect(new Rect2(box, new Vector2(width, height)), new Color("#0b1622"));
        DrawString(
            ThemeDB.FallbackFont,
            box + new Vector2(ScaleWorld(3), height - ScaleWorld(3)),
            text,
            HorizontalAlignment.Left,
            width - ScaleWorld(6),
            Math.Max(1, (int)Math.Round(ScaleWorld(11))),
            stroke.Tiles.Count == 0 ? new Color("#fca5a5") : new Color("#f8fafc"));
    }

    /// <summary>
    /// Three distinct readings the player must get without opening the log: an
    /// intention that is waiting, an intention nobody can reach, and work in
    /// progress with how far along it is.
    /// </summary>
    private void DrawDigDesignations(IReadOnlySet<GridPoint> rockTiles)
    {
        // Accepted on this tick and not applied yet. Drawn first and drawn as the
        // designation it is about to become, accent included: the picture must not
        // change when the tick that records it runs.
        var pendingAccent = DigColor(MapAccents.PendingDig(_projection!));
        foreach (var tile in _projection!.PendingDigMarks)
        {
            DrawDigMark(tile, pendingAccent, rockTiles);
        }

        foreach (var designation in _projection.DigDesignations)
        {
            var accent = DigColor(MapAccents.Dig(_projection, designation));
            DrawDigMark(designation.Tile, accent, rockTiles);
            var center = CellCenter(designation.Tile);

            if (designation.StatusCode == "dig_unreachable")
            {
                continue;
            }

            if (designation.WorkTile is { } workTile)
            {
                DrawLine(
                    CellCenter(workTile),
                    center,
                    accent with { A = 0.55f },
                    ScaleWorld(1.0f));
            }

            if (designation.ProgressTicks <= 0 || designation.RequiredTicks <= 0)
            {
                continue;
            }

            var fraction = Math.Clamp(
                designation.ProgressTicks / (float)designation.RequiredTicks,
                0f,
                1f);
            var wallRect = CellInteractionRect(designation.Tile, rockTiles);

            // The visible wall mass is eaten from the bottom up. The progress fill
            // and bar use the same raised bounds as the wall, not its flat grid
            // footprint.
            var eaten = (wallRect.Size.Y - ScaleWorld(2)) * fraction;
            DrawRect(
                new Rect2(
                    wallRect.Position +
                    new Vector2(
                        ScaleWorld(1),
                        wallRect.Size.Y - ScaleWorld(1) - eaten),
                    new Vector2(wallRect.Size.X - ScaleWorld(2), eaten)),
                new Color("#fbbf24") with { A = 0.6f });

            var barWidth = wallRect.Size.X - ScaleWorld(5);
            var barHeight = ScaleWorld(4);
            var barTopLeft = wallRect.End -
                new Vector2(wallRect.Size.X - ScaleWorld(2), ScaleWorld(7));
            DrawRect(
                new Rect2(barTopLeft, new Vector2(barWidth, barHeight)),
                new Color("#0f172a"));
            DrawRect(
                new Rect2(barTopLeft, new Vector2(barWidth * fraction, barHeight)),
                new Color("#fde047"));
        }
    }

    /// <summary>
    /// The mark itself: a tinted cell and the crossed pick that reads as "marked
    /// for excavation" at tile size. One routine for a designation the world holds
    /// and for one still waiting for its tick, so the two cannot drift apart and
    /// the moment of application cannot be seen.
    /// </summary>
    private void DrawDigMark(
        GridPoint tile,
        Color accent,
        IReadOnlySet<GridPoint> rockTiles)
    {
        var rect = CellInteractionRect(tile, rockTiles);
        DrawRect(rect.Grow(-ScaleWorld(1)), accent with { A = 0.26f });
        DrawRect(rect.Grow(-ScaleWorld(1)), accent, false, ScaleWorld(1.5f));

        var center = rect.GetCenter();
        DrawLine(
            center + ScaleWorld(-5, -5),
            center + ScaleWorld(5, 5),
            accent,
            ScaleWorld(1.5f));
        DrawLine(
            center + ScaleWorld(5, -5),
            center + ScaleWorld(-5, 5),
            accent,
            ScaleWorld(1.5f));
    }

    /// <summary>
    /// The palette, and nothing else. Which reading a mark has is decided in
    /// <c>DungeonFortress.Presentation.MapAccents</c>, where a unit test can
    /// compare the waiting reading against the applied one; this file is not
    /// built by the "Pure .NET" CI job, so a decision made here is a decision
    /// nothing checks.
    /// </summary>
    private static Color DigColor(DigMarkAccent accent) => accent switch
    {
        DigMarkAccent.InProgress => new Color("#fbbf24"),
        DigMarkAccent.Unreachable => new Color("#f87171"),
        DigMarkAccent.BlockedByPriority => new Color("#94a3b8"),
        _ => new Color("#f59e0b"),
    };

    /// <summary>
    /// A blueprint has to answer three questions at tile size: is this an
    /// intention rather than a building, how much of its material has arrived, and
    /// is anything actually happening. Delivered blocks are drawn as discrete pips
    /// so "1 of 2" is countable, and the caption keeps the graybox primitive
    /// readable without an asset — ADR 0008 is accepted but not implemented.
    /// </summary>
    private void DrawBuildSites()
    {
        // A blueprint the player marked on this tick, drawn as the blueprint it
        // becomes: nothing delivered, nothing booked, the full cost as hollow
        // pips, and the accent the same facts give it. BuildStoneCost is the same
        // tuning value the world charges.
        foreach (var tile in _projection!.PendingBuildMarks)
        {
            DrawBlueprint(
                tile,
                BuildColor(MapAccents.PendingBlueprint(_projection, tile)),
                0,
                0,
                PrototypeTuning.BuildStoneCost);
        }

        foreach (var site in _projection.BuildSites)
        {
            var accent = BuildColor(MapAccents.Blueprint(_projection, site));
            DrawBlueprint(site.Tile, accent, site.Delivered, site.IncomingReserved, site.Required);

            if (site.ProgressTicks <= 0 || site.RequiredTicks <= 0)
            {
                continue;
            }

            var fraction = Math.Clamp(
                site.ProgressTicks / (float)site.RequiredTicks,
                0f,
                1f);
            var barWidth = _tileSize - ScaleWorld(5);
            var barHeight = ScaleWorld(3);
            var barTopLeft = CellTopLeft(site.Tile) + ScaleWorld(2, 2);
            DrawRect(
                new Rect2(barTopLeft, new Vector2(barWidth, barHeight)),
                new Color("#0f172a"));
            DrawRect(
                new Rect2(barTopLeft, new Vector2(barWidth * fraction, barHeight)),
                new Color("#5eead4"));
        }
    }

    /// <summary>
    /// The blueprint itself. One routine for a site the world holds and for one
    /// accepted on this tick, so applying the command changes the pips rather than
    /// making the blueprint appear.
    /// </summary>
    private void DrawBlueprint(
        GridPoint tile,
        Color accent,
        int delivered,
        int incomingReserved,
        int required)
    {
        var rect = new Rect2(CellTopLeft(tile), new Vector2(_tileSize - 1, _tileSize - 1));
        DrawRect(rect.Grow(-ScaleWorld(1)), accent with { A = 0.22f });
        DrawRect(rect.Grow(-ScaleWorld(1)), accent, false, ScaleWorld(1.5f));

        var topLeft = CellTopLeft(tile);
        DrawString(
            ThemeDB.FallbackFont,
            topLeft + ScaleWorld(2, 8),
            "POST?",
            HorizontalAlignment.Left,
            _tileSize - ScaleWorld(3),
            Math.Max(1, (int)Math.Round(ScaleWorld(6))),
            accent);

        for (var index = 0; index < required; index++)
        {
            var pip = new Rect2(
                topLeft + new Vector2(ScaleWorld(3 + (index * 7)), _tileSize - ScaleWorld(9)),
                ScaleWorld(5, 5));
            if (index < delivered)
            {
                DrawRect(pip, new Color("#e2e8f0"));
                DrawRect(pip, new Color("#475569"), false, ScaleWorld(1.0f));
            }
            else if (index < delivered + incomingReserved)
            {
                DrawRect(pip, new Color("#7dd3fc"), false, ScaleWorld(1.0f));
            }
            else
            {
                DrawRect(pip, accent with { A = 0.45f }, false, ScaleWorld(1.0f));
            }
        }
    }

    /// <summary>The palette for a blueprint; the reading comes from MapAccents.</summary>
    private static Color BuildColor(BlueprintAccent accent) => accent switch
    {
        BlueprintAccent.InProgress => new Color("#5eead4"),
        BlueprintAccent.Unreachable => new Color("#f87171"),
        BlueprintAccent.BlockedByPriority => new Color("#94a3b8"),
        BlueprintAccent.WaitingForMaterial => new Color("#fbbf24"),
        _ => new Color("#2dd4bf"),
    };

    /// <summary>
    /// The end of the chain, drawn as a graybox primitive with a caption: a solid
    /// teal block so a built post reads as a built thing rather than as floor, and
    /// the word itself because the old small square cannot say "training post" on its
    /// own. The authored posts are drawn the same way, so the player cannot tell
    /// them apart — which is the claim the step is making. One post is drawn at a
    /// time because tall structures participate in the same Y-order as walls and
    /// bodies.
    /// </summary>
    private void DrawBuiltPost(PrototypeStationSnapshot station)
    {
        var topLeft = CellTopLeft(station.Position);
        var rect = new Rect2(topLeft, new Vector2(_tileSize - 1, _tileSize - 1));
        DrawRect(rect.Grow(-ScaleWorld(3)), new Color("#0f766e"));
        DrawRect(
            rect.Grow(-ScaleWorld(3)),
            new Color("#5eead4"),
            false,
            ScaleWorld(1.0f));
        DrawString(
            ThemeDB.FallbackFont,
            topLeft + ScaleWorld(2, 9),
            "POST",
            HorizontalAlignment.Left,
            _tileSize - ScaleWorld(3),
            Math.Max(1, (int)Math.Round(ScaleWorld(6))),
            new Color("#ccfbf1"));
    }

    /// <summary>
    /// A stockpile cell has to answer three questions at tile size: is this a
    /// storage slot at all, how full is it, and is its remaining room already
    /// promised to someone on the way. Stored blocks are drawn as discrete pips so
    /// "2 of 2" is countable rather than inferred from a bar.
    /// </summary>
    private void DrawStockpileCells()
    {
        // Painted on this tick and not applied yet: an empty cell, which is what
        // the world creates when it applies the paint.
        foreach (var tile in _projection!.PendingStockpileCells)
        {
            DrawStockpileCell(tile, StockpileColor(MapAccents.PendingStockpile(_projection, tile)), 0, 0);
        }

        foreach (var cell in _projection.StockpileCells)
        {
            DrawStockpileCell(
                cell.Position,
                StockpileColor(MapAccents.Stockpile(_projection, cell)),
                cell.Stored,
                cell.IncomingReserved);
        }
    }

    /// <summary>
    /// One storage square. Shared by a cell the world holds and by one accepted on
    /// this tick, so painting a stockpile while paused draws the same square the
    /// tick would draw.
    /// </summary>
    private void DrawStockpileCell(GridPoint position, Color accent, int stored, int incomingReserved)
    {
        var rect = new Rect2(CellTopLeft(position), new Vector2(_tileSize - 1, _tileSize - 1));
        DrawRect(rect.Grow(-ScaleWorld(1)), new Color("#1f2937"));
        DrawRect(rect.Grow(-ScaleWorld(1)), accent, false, ScaleWorld(1.5f));

        // Corner ticks read as "a marked-out storage square" instead of just
        // another zone outline.
        var topLeft = CellTopLeft(position);
        foreach (var corner in new[]
                 {
                     (ScaleWorld(2, 2), ScaleWorld(6, 2), ScaleWorld(2, 6)),
                     (
                         new Vector2(_tileSize - ScaleWorld(3), ScaleWorld(2)),
                         new Vector2(_tileSize - ScaleWorld(7), ScaleWorld(2)),
                         new Vector2(_tileSize - ScaleWorld(3), ScaleWorld(6))),
                 })
        {
            DrawLine(
                topLeft + corner.Item1,
                topLeft + corner.Item2,
                accent,
                ScaleWorld(1.0f));
            DrawLine(
                topLeft + corner.Item1,
                topLeft + corner.Item3,
                accent,
                ScaleWorld(1.0f));
        }

        for (var index = 0; index < stored; index++)
        {
            DrawRect(
                new Rect2(
                    topLeft + new Vector2(ScaleWorld(4 + (index * 7)), _tileSize - ScaleWorld(10)),
                    ScaleWorld(6, 6)),
                new Color("#e2e8f0"));
            DrawRect(
                new Rect2(
                    topLeft + new Vector2(ScaleWorld(4 + (index * 7)), _tileSize - ScaleWorld(10)),
                    ScaleWorld(6, 6)),
                new Color("#475569"),
                false,
                ScaleWorld(1.0f));
        }

        // A hollow pip per booked slot: the player sees the room is taken even
        // though the carrier has not arrived yet.
        for (var index = stored; index < stored + incomingReserved; index++)
        {
            DrawRect(
                new Rect2(
                    topLeft + new Vector2(ScaleWorld(4 + (index * 7)), _tileSize - ScaleWorld(10)),
                    ScaleWorld(6, 6)),
                new Color("#7dd3fc"),
                false,
                ScaleWorld(1.0f));
        }
    }

    /// <summary>The palette for a stockpile cell; the reading comes from MapAccents.</summary>
    private static Color StockpileColor(StockpileCellAccent accent) => accent switch
    {
        StockpileCellAccent.Unreachable => new Color("#f87171"),
        StockpileCellAccent.Full => new Color("#e2e8f0"),
        StockpileCellAccent.Incoming => new Color("#7dd3fc"),
        _ => new Color("#94a3b8"),
    };

    private void CycleZone()
    {
        _brushZone = (ZoneKind)(((int)_brushZone + 1) % Enum.GetValues<ZoneKind>().Length);
        RefreshState();
    }

    private void CycleJob()
    {
        _selectedJob = (JobKind)(((int)_selectedJob + 1) % Enum.GetValues<JobKind>().Length);
        _editingPriorities = true;
        RefreshState();
    }

    private void CycleRule()
    {
        _selectedRule = (_selectedRule + 1) % UiControls.RuleIds.Count;
        _editingPriorities = false;
        RefreshState();
    }

    private void UpdatePointer(Vector2 position)
    {
        _lastPanPointer = position;
        _hoverCell = ScreenToCell(position);
        _hoverCreatureId = _hoverCell is { } hovered
            ? _state!.Creatures.FirstOrDefault(creature => creature.Position == hovered)?.Id
            : null;
        UpdateCreatureLabels();
        QueueRedraw();
    }

    private void SelectAt(Vector2 position)
    {
        var cell = ScreenToCell(position);
        if (cell is not { } selected)
        {
            return;
        }

        _selectedCell = selected;
        _selectedCreatureId = _state!.Creatures
            .Where(creature => creature.Position == selected)
            .Select(creature => (int?)creature.Id)
            .FirstOrDefault();
        UpdateHud();
        UpdateCreatureLabels();
        QueueRedraw();
    }

    /// <summary>
    /// What the rectangle the player is dragging would do right now. It is the
    /// same value the release applies, so the highlighted area, the cell count
    /// above the cursor and the command that lands cannot disagree.
    /// </summary>
    private BrushStroke? PendingStroke() =>
        _projection is null || _dragAnchor is not { } anchor
            ? null
            : BrushSelection.Resolve(
                _projection,
                _editMode,
                _brushZone,
                anchor,
                _dragCurrent ?? anchor);

    /// <summary>
    /// A released rectangle. Every cell the simulation would accept goes into
    /// <em>one</em> command, and a cell it would refuse never becomes a command at
    /// all — it becomes an explanation in the feedback line.
    ///
    /// One command rather than one per cell is what makes partially applied
    /// marking impossible: the world validates the whole tile list before it
    /// records the first designation, so a rejected rectangle changes nothing.
    ///
    /// A single click is a 1x1 rectangle and goes through exactly this path, so
    /// the click and the drag cannot drift apart either.
    /// </summary>
    private void ApplyBrushStroke(GridPoint from, GridPoint to)
    {
        if (_projection is null)
        {
            return;
        }

        // Resolved against the projection, so a cell that already carries a mark
        // the world has not applied yet is not marked a second time. Paused, that
        // is the difference between one command and one per click.
        var stroke = BrushSelection.Resolve(_projection, _editMode, _brushZone, from, to);
        if (BrushSelection.ToCommand(stroke, _projection.State.Tick) is { } command)
        {
            TryApplyPlayerCommand(command);
            return;
        }

        _controlFeedback = stroke.Refusal ?? "Nothing to mark there.";
        UpdateHud();
        QueueRedraw();
    }

    private void CancelDrag(string source)
    {
        _dragAnchor = null;
        _dragCurrent = null;
        // Nothing was emitted while the rectangle was being dragged, so there is
        // nothing to undo: a cancelled selection leaves no entry in the log.
        _controlFeedback = $"Selection cancelled ({source}); nothing was marked.";
        UpdateHud();
        QueueRedraw();
    }

    /// <summary>
    /// One key for the whole intent "I want a material stockpile here": it picks
    /// the zone and the Paint mode together, because cycling zones with [Z] to
    /// find MaterialStockpile is the step players lose the thread on. It stays an
    /// ordinary <c>zone_paint</c> — no new command and no new selection framework.
    /// </summary>
    private void SelectStockpileBrush()
    {
        _brushZone = ZoneKind.MaterialStockpile;
        SelectEditMode(BrushMode.Paint);
        _controlFeedback =
            "STOCKPILE [M]: painting MaterialStockpile. Drag a rectangle over pre-existing " +
            $"floor; each cell holds {PrototypeTuning.StockpileCellCapacity} stone. " +
            "[E] erases and drops stored stone back on the tile. Esc puts the brush away.";
        UpdateHud();
        QueueRedraw();
    }

    private void SelectEditMode(BrushMode mode)
    {
        _editMode = mode;
        // A brush change abandons whatever rectangle was in progress, and abandons
        // it the same way Esc does: nothing was emitted, so nothing is undone.
        _dragAnchor = null;
        _dragCurrent = null;
        _controlFeedback = mode switch
        {
            BrushMode.Dig =>
                "DIG: drag a rectangle over rock to mark it for excavation in one command. " +
                "A free creature chooses the job on its own. Esc cancels a drag, then the brush.",
            BrushMode.CancelDig =>
                "CANCEL DIG: drag a rectangle over designations to withdraw them. " +
                "Esc cancels a drag, then the brush.",
            BrushMode.Build =>
                "BUILD [C]: drag a rectangle over plain floor — including ground you dug — to " +
                $"mark training posts. Each costs {PrototypeTuning.BuildStoneCost} stone, " +
                "which the crew brings on its own. Esc cancels a drag, then the brush.",
            BrushMode.CancelBuild =>
                "UNBUILD [V]: drag a rectangle over blueprints to withdraw them. Stone already " +
                "delivered drops back onto that tile. Esc cancels a drag, then the brush.",
            BrushMode.Inspect => "Inspect mode; brush put away.",
            _ => _controlFeedback,
        };
        RefreshState();
    }

    private void CancelBrush(string source)
    {
        _editMode = BrushMode.Inspect;
        _dragAnchor = null;
        _dragCurrent = null;
        _controlFeedback = $"Inspect mode ({source}); brush put away.";
        RefreshState();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        UpdateHud();
        QueueRedraw();
    }

    private void SetSpeed(double speed)
    {
        _speed = speed;
        _paused = false;
        UpdateHud();
        QueueRedraw();
    }

    private void AdjustSelectedControl(int delta)
    {
        if (_editingPriorities)
        {
            var priorityValue = Math.Clamp(_state!.Priorities[_selectedJob] + delta, PrototypeTuning.PriorityMinimum, PrototypeTuning.PriorityMaximum);
            TryApplyPlayerCommand(new SetPriorityCommand(_state.Tick, _selectedJob, priorityValue));
            return;
        }

        var ruleId = UiControls.RuleIds[_selectedRule];
        var maximum = ruleId switch
        {
            "ration_reserve" => PrototypeTuning.RationReserveMaximum,
            "drill_min_satiety" => PrototypeTuning.DrillMinimumSatietyMaximum,
            _ => PrototypeTuning.MusterLeadMaximum,
        };
        var value = Math.Clamp(_state!.Rules[ruleId] + delta, 0, maximum);
        TryApplyPlayerCommand(new SetRuleCommand(_state.Tick, ruleId, value));
    }

    private void TryApplyPlayerCommand(PrototypeCommand command)
    {
        try
        {
            var candidateCommands = _playerCommands.Append(command).ToArray();
            var candidateLog = BuildFullLog(candidateCommands);
            PrototypeCommandValidator.Validate(candidateLog);
            var candidateWorld = new PrototypeWorld(candidateLog);
            candidateWorld.RunTicks(_state!.Tick);
            _playerCommands.Add(command);
            _world = candidateWorld;
            _controlFeedback = $"accepted {HudText.DescribeCommand(command)}; activates on next tick";
            RefreshState();
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            RecordDiagnostic("indirect_command", exception);
            _controlFeedback = $"rejected {command.GetType().Name}: {exception.Message}";
            UpdateHud();
            QueueRedraw();
        }
    }

    private PrototypeCommandLog BuildFullLog(IEnumerable<PrototypeCommand> playerCommands)
    {
        var ordered = _fixtureLog!.Commands
            .Concat(playerCommands)
            .OrderBy(command => command.Tick)
            .ToArray();
        return new PrototypeCommandLog(_fixtureLog.Scenario, _fixtureLog.Seed, ordered);
    }

    private void ReplayCurrentLog()
    {
        var replay = new PrototypeWorld(BuildFullLog(_playerCommands));
        replay.RunTicks(_state!.Tick);
        var checksum = PrototypeScenario.Capture(replay).Checksum;
        _controlFeedback = checksum == _checksum ? "replay checksum matches" : "replay checksum MISMATCH";
        if (checksum == _checksum)
        {
            _world = replay;
            RefreshState();
        }
        else
        {
            RecordDiagnostic("replay", new InvalidOperationException(_controlFeedback));
            UpdateHud();
        }
    }

    private void ApplyDemoControls()
    {
        TryApplyPlayerCommand(new ZonePaintCommand(_state!.Tick, ZoneKind.TrainingGround, [new GridPoint(7, 11)]));
        TryApplyPlayerCommand(new SetPriorityCommand(_state!.Tick, JobKind.Drill, 4));
        TryApplyPlayerCommand(new SetRuleCommand(_state!.Tick, "ration_reserve", 4));
    }

    /// <summary>
    /// The reproducible excavation capture: mark four rock tiles with the DIG
    /// brush, withdraw one with CANCEL DIG, then let --screenshot-ticks pick the
    /// before/during/after moment. It uses the same brush path as a human.
    /// </summary>
    private void ApplyDemoDig()
    {
        _editMode = BrushMode.Dig;
        foreach (var tile in new GridPoint[]
                 {
                     new(25, 1), new(25, 2), new(25, 3), new(26, 1), new(26, 3),
                 })
        {
            ApplyBrushStroke(tile, tile);
        }

        // The withdrawal deliberately lands on the next tick, which is what keeps
        // this session's log the same shape as
        // scenarios/prototype1/dig-demo.commands.v2.json. The brush no longer
        // needs the step to see the marks: since Issue #58 a mark is part of the
        // projection the moment it is accepted.
        Advance(1);
        _editMode = BrushMode.CancelDig;
        ApplyBrushStroke(new GridPoint(26, 3), new GridPoint(26, 3));
        // Left holding the dig brush on purpose: the capture then also shows the
        // outline every still-diggable tile gets while the brush is active.
        _editMode = BrushMode.Dig;
        _selectedCell = new GridPoint(25, 3);
        _selectedCreatureId = null;
        _controlFeedback =
            "Demo: DIG marked (25,1) (25,2) (25,3) (26,1); CANCEL DIG withdrew (26,3). " +
            "(26,1) is walled in until a neighbour is dug.";
        RefreshState();
    }

    /// <summary>
    /// The reproducible stone-logistics capture. It uses the same brush path a
    /// human uses — [D] to mark rock, [M] to paint a stockpile — and schedules the
    /// stockpile for a later tick so that <c>--screenshot-ticks</c> alone selects
    /// the "loose stone, no stockpile", "stone in transit" or "stockpile full"
    /// moment. Nothing here addresses a creature.
    /// </summary>
    private void ApplyDemoStone()
    {
        _editMode = BrushMode.Dig;
        foreach (var tile in new GridPoint[] { new(25, 1), new(25, 2), new(25, 3), new(26, 1) })
        {
            ApplyBrushStroke(tile, tile);
        }

        // The stockpile is painted at a fixed future tick, after the pocket is
        // excavated, so the earlier frames legitimately show stone with nowhere
        // to go instead of a stockpile that has not been drawn yet.
        SelectStockpileBrush();
        TryApplyPlayerCommand(
            new ZonePaintCommand(
                DemoStoneZoneTick,
                ZoneKind.MaterialStockpile,
                [new GridPoint(22, 1), new GridPoint(23, 1)]));

        _selectedCell = new GridPoint(23, 1);
        _selectedCreatureId = null;
        _controlFeedback =
            "Demo: DIG marked (25,1) (25,2) (25,3) (26,1); [M] paints the material " +
            $"stockpile (22,1) (23,1) at tick {DemoStoneZoneTick}. Nobody was ordered to carry anything.";
        RefreshState();
    }

    private const int DemoStoneZoneTick = 200;
    private const int DemoBuildBlueprintTick = 1_000;
    private static GridPoint DemoBuildSite => new(25, 2);

    /// <summary>
    /// The reproducible functional-room capture, and the whole Issue #48 chain in
    /// one brush session: [D] marks the pocket, [M] paints the stockpile, and at a
    /// fixed later tick [C] marks a blueprint on ground that did not exist at tick
    /// 0, [B] zones it as a TrainingGround and [J] switches Drill on. Nothing here
    /// addresses a creature; every stone that reaches the post is fetched back out
    /// of the stockpile by whoever is free.
    /// </summary>
    private void ApplyDemoBuild()
    {
        _editMode = BrushMode.Dig;
        foreach (var tile in new GridPoint[] { new(25, 1), new(25, 2), new(25, 3), new(26, 1) })
        {
            ApplyBrushStroke(tile, tile);
        }

        SelectStockpileBrush();
        TryApplyPlayerCommand(
            new ZonePaintCommand(
                DemoStoneZoneTick,
                ZoneKind.MaterialStockpile,
                [new GridPoint(22, 1), new GridPoint(23, 1)]));

        // Scheduled for a tick at which the pocket is dug and its stone is already
        // put away, so the blueprint has to pull the material back out again.
        TryApplyPlayerCommand(new BuildDesignateCommand(DemoBuildBlueprintTick, [DemoBuildSite]));
        TryApplyPlayerCommand(
            new ZonePaintCommand(
                DemoBuildBlueprintTick,
                ZoneKind.TrainingGround,
                [DemoBuildSite]));
        TryApplyPlayerCommand(
            new SetPriorityCommand(DemoBuildBlueprintTick, JobKind.Drill, 3));

        _editMode = BrushMode.Build;
        _brushZone = ZoneKind.TrainingGround;
        _selectedCell = DemoBuildSite;
        _selectedCreatureId = null;
        _controlFeedback =
            "Demo: DIG marked (25,1) (25,2) (25,3) (26,1); [M] paints the material " +
            $"stockpile (22,1) (23,1) at tick {DemoStoneZoneTick}; [C] marks a training " +
            $"post on (25,2) at tick {DemoBuildBlueprintTick}, [B] zones it TrainingGround " +
            "and Drill is switched on. Nobody was ordered to carry or build anything.";
        RefreshState();
    }

    private void VerifyControlsSmoke()
    {
        // This is an input seam rather than a simulation test: it asserts that a
        // brush stroke accepts multiple cells and that cancelling never leaves
        // the UI in a mouse-capturing edit mode.
        var strokeStart = _playerCommands.Count;
        _editMode = BrushMode.Paint;
        TryApplyPlayerCommand(new ZonePaintCommand(_state!.Tick, ZoneKind.TrainingGround, [new GridPoint(10, 11)]));
        TryApplyPlayerCommand(new ZonePaintCommand(_state!.Tick, ZoneKind.TrainingGround, [new GridPoint(11, 11)]));
        Advance(1); // Commands at the current tick become visible on the next simulation tick.
        if (_playerCommands.Count != strokeStart + 2 ||
            !_state!.Zones[ZoneKind.TrainingGround].Contains(new GridPoint(10, 11)) ||
            !_state.Zones[ZoneKind.TrainingGround].Contains(new GridPoint(11, 11)))
        {
            throw new InvalidOperationException("Brush smoke did not apply two independent cells.");
        }
        CancelBrush("smoke");
        if (_editMode != BrushMode.Inspect)
        {
            throw new InvalidOperationException("Brush smoke did not return to inspect mode.");
        }

        VerifyRectangleSelectionSmoke();

        var beforeChecksum = _checksum;
        var beforeCount = _playerCommands.Count;
        TryApplyPlayerCommand(new ZonePaintCommand(_state!.Tick, ZoneKind.Forbidden, [new GridPoint(14, 7)]));
        if (_playerCommands.Count != beforeCount || _checksum != beforeChecksum)
        {
            throw new InvalidOperationException("Invalid indirect command changed the world or log.");
        }

        VerifyPausedMarkingSmoke();
        VerifyDigBrushSmoke();
        VerifyStockpileBrushSmoke();
        VerifyBuildBrushSmoke();

        Advance(40);
        var first = PrototypeScenario.Capture(_world!).Checksum;
        var replay = new PrototypeWorld(BuildFullLog(_playerCommands));
        for (var index = 0; index < _state!.Tick; index += 3)
        {
            replay.RunTicks(Math.Min(3, _state.Tick - replay.CurrentTick));
        }
        if (PrototypeScenario.Capture(replay).Checksum != first)
        {
            throw new InvalidOperationException("Command replay differs across update pacing.");
        }
    }

    /// <summary>
    /// The input seam of the rectangle brush, checked through the same path the
    /// mouse drives.
    ///
    /// Two claims, and they are the two the whole step rests on: a released
    /// rectangle emits <em>exactly one</em> command carrying every cell of the
    /// selection, and a cancelled rectangle emits none and changes nothing. The
    /// second is why nothing is emitted until the button comes back up.
    /// </summary>
    private void VerifyRectangleSelectionSmoke()
    {
        // Plain floor clear of every authored feature, of the internal rock and of
        // the two cells the zone stroke above already painted.
        var from = new GridPoint(12, 10);
        var to = new GridPoint(15, 12);
        _brushZone = ZoneKind.TrainingGround;
        SelectEditMode(BrushMode.Paint);

        var cancelledCount = _playerCommands.Count;
        var cancelledChecksum = _checksum;
        _dragAnchor = from;
        _dragCurrent = to;
        CancelDrag("smoke");
        if (_dragAnchor is not null ||
            _playerCommands.Count != cancelledCount ||
            _checksum != cancelledChecksum)
        {
            throw new InvalidOperationException(
                "A cancelled selection left a trace in the command log or in the world.");
        }

        var before = _playerCommands.Count;
        ApplyBrushStroke(from, to);
        Advance(1);
        if (_playerCommands.Count != before + 1)
        {
            throw new InvalidOperationException(
                $"A 4x3 drag emitted {_playerCommands.Count - before} commands instead of one.");
        }

        if (_playerCommands[^1] is not ZonePaintCommand { Tiles.Count: 12 })
        {
            throw new InvalidOperationException(
                "The rectangle command did not carry all twelve cells of the selection.");
        }

        // Partially applied marking must not exist: either the whole rectangle is
        // zoned or none of it is.
        foreach (var tile in BrushSelection.Rectangle(from, to))
        {
            if (!_state!.Zones[ZoneKind.TrainingGround].Contains(tile))
            {
                throw new InvalidOperationException(
                    $"({tile.X},{tile.Y}) is inside the applied rectangle but is not zoned.");
            }
        }

        CancelBrush("rectangle smoke");
    }

    /// <summary>
    /// Issue #58, through the adapter rather than through the unit tests: a mark
    /// accepted while time is stopped is on the map at once, a withdrawal is off
    /// it at once, and the tick that finally records either of them does not
    /// change what is drawn.
    ///
    /// The last claim is the one a picture cannot make. Marking is only useful
    /// while paused if unpausing does not visibly redo it, so the check compares
    /// the set of cells that read as designated across the very tick that applies
    /// the command and requires it to be the same set.
    ///
    /// It leaves the world exactly as it found it — mark, apply, withdraw, apply —
    /// so the excavation smoke below still starts from no designations.
    /// </summary>
    private void VerifyPausedMarkingSmoke()
    {
        // In the excavation pocket and used by no other smoke in this file.
        var tile = new GridPoint(26, 3);
        var commandsBefore = _playerCommands.Count;
        var tickBefore = _state!.Tick;

        SelectEditMode(BrushMode.Dig);
        ApplyBrushStroke(tile, tile);

        if (_state!.Tick != tickBefore)
        {
            throw new InvalidOperationException(
                "Accepting a brush stroke advanced the simulation. Marking is not a time control.");
        }

        if (_playerCommands.Count != commandsBefore + 1)
        {
            throw new InvalidOperationException("The paused stroke did not emit exactly one command.");
        }

        if (_state.DigDesignations.Any(item => item.Tile == tile))
        {
            throw new InvalidOperationException(
                "Canonical state applied a command before its own tick ran.");
        }

        if (!_projection!.IsDesignatedForDigging(tile) ||
            !_projection.PendingDigMarks.Contains(tile))
        {
            throw new InvalidOperationException(
                "A designation accepted while paused is not on the map until time moves (Issue #58).");
        }

        if (BrushSelection.Accepts(_projection, BrushMode.Dig, _brushZone, tile))
        {
            throw new InvalidOperationException(
                "The dig brush offered a cell that already carries a mark waiting for its tick.");
        }

        var drawnBefore = DesignatedTiles();
        Advance(1);
        if (!_state!.DigDesignations.Any(item => item.Tile == tile) ||
            _projection!.PendingDigMarks.Count != 0)
        {
            throw new InvalidOperationException("The tick did not apply the paused designation.");
        }

        if (!drawnBefore.SequenceEqual(DesignatedTiles()))
        {
            throw new InvalidOperationException(
                "The cells drawn as designated changed when the command was applied: " +
                "unpausing redraws the marking instead of refining it.");
        }

        SelectEditMode(BrushMode.CancelDig);
        ApplyBrushStroke(tile, tile);
        if (_projection!.IsDesignatedForDigging(tile))
        {
            throw new InvalidOperationException(
                "A withdrawal accepted while paused stayed on the map until the next tick.");
        }

        if (!_state!.DigDesignations.Any(item => item.Tile == tile))
        {
            throw new InvalidOperationException(
                "The withdrawal reached canonical state before its own tick ran.");
        }

        Advance(1);
        if (_state!.DigDesignations.Any(item => item.Tile == tile) ||
            _projection!.HasPendingMarking)
        {
            throw new InvalidOperationException("The tick did not apply the paused withdrawal.");
        }

        CancelBrush("paused marking smoke");
    }

    /// <summary>Every cell that currently reads as designated, drawn or waiting.</summary>
    private IReadOnlyList<GridPoint> DesignatedTiles() =>
    [
        .. _projection!.DigDesignations
            .Select(item => item.Tile)
            .Concat(_projection.PendingDigMarks)
            .Order(),
    ];

    /// <summary>
    /// An input-seam check for the excavation brushes: a stroke marks several
    /// tiles, a stroke over floor and over the map boundary changes nothing, the
    /// cancel brush withdraws exactly one mark, and Esc leaves edit mode.
    /// </summary>
    private void VerifyDigBrushSmoke()
    {
        var strokeStart = _playerCommands.Count;
        _editMode = BrushMode.Dig;
        foreach (var tile in new GridPoint[] { new(25, 1), new(26, 1), new(25, 2) })
        {
            ApplyBrushStroke(tile, tile);
        }

        Advance(1);
        if (_playerCommands.Count != strokeStart + 3)
        {
            throw new InvalidOperationException("The dig brush did not mark three tiles.");
        }

        foreach (var tile in new GridPoint[] { new(25, 1), new(26, 1), new(25, 2) })
        {
            if (!_state!.DigDesignations.Any(item => item.Tile == tile))
            {
                throw new InvalidOperationException(
                    $"The dig brush did not designate ({tile.X},{tile.Y}).");
            }
        }

        var guardedChecksum = _checksum;
        var guardedCount = _playerCommands.Count;
        ApplyBrushStroke(new GridPoint(12, 12), new GridPoint(12, 12));
        ApplyBrushStroke(new GridPoint(0, 0), new GridPoint(0, 0));
        ApplyBrushStroke(PrototypeMapGate, PrototypeMapGate);
        ApplyBrushStroke(new GridPoint(25, 1), new GridPoint(25, 1));
        if (_playerCommands.Count != guardedCount || _checksum != guardedChecksum)
        {
            throw new InvalidOperationException(
                "The dig brush emitted a command for a tile the simulation forbids.");
        }

        _editMode = BrushMode.CancelDig;
        ApplyBrushStroke(new GridPoint(26, 1), new GridPoint(26, 1));
        ApplyBrushStroke(new GridPoint(12, 12), new GridPoint(12, 12));
        Advance(1);
        if (_playerCommands.Count != guardedCount + 1 ||
            _state!.DigDesignations.Any(item => item.Tile == new GridPoint(26, 1)) ||
            _state.DigDesignations.Count != 2)
        {
            throw new InvalidOperationException("The cancel-dig brush did not withdraw one mark.");
        }

        CancelBrush("dig smoke");
        if (_editMode != BrushMode.Inspect)
        {
            throw new InvalidOperationException("The dig brush did not return to inspect mode.");
        }

        // The whole point of the step: nobody was ordered, yet the rock changes.
        for (var guard = 0; guard < 400 && _state!.Economy.DigsCompleted == 0; guard++)
        {
            Advance(1);
        }

        if (_state!.Economy.DigsCompleted == 0 || _state.Stocks.LooseStone == 0)
        {
            throw new InvalidOperationException(
                "No designation was excavated autonomously inside the smoke budget.");
        }
    }

    /// <summary>
    /// An input-seam check for the [M] shortcut and the stockpile brush: one key
    /// selects both the zone and Paint, a stroke over rock, a feature and the gate
    /// emits nothing, painting works on plain floor, and the whole loose → carried
    /// → stored chain then runs without a single order.
    /// </summary>
    private void VerifyStockpileBrushSmoke()
    {
        SelectStockpileBrush();
        if (_editMode != BrushMode.Paint || _brushZone != ZoneKind.MaterialStockpile)
        {
            throw new InvalidOperationException("[M] did not select MaterialStockpile and Paint.");
        }

        var guardedChecksum = _checksum;
        var guardedCount = _playerCommands.Count;
        ApplyBrushStroke(new GridPoint(9, 4), new GridPoint(9, 4));   // internal rock
        ApplyBrushStroke(new GridPoint(0, 0), new GridPoint(0, 0));   // map boundary
        ApplyBrushStroke(PrototypeMapGate, PrototypeMapGate);
        ApplyBrushStroke(new GridPoint(14, 7), new GridPoint(14, 7));  // larder feature
        ApplyBrushStroke(new GridPoint(2, 1), new GridPoint(2, 1));   // mushroom bed
        if (_playerCommands.Count != guardedCount || _checksum != guardedChecksum)
        {
            throw new InvalidOperationException(
                "The stockpile brush emitted a command for a tile the simulation forbids.");
        }

        var stockpile = new GridPoint[] { new(22, 1), new(23, 1) };
        foreach (var tile in stockpile)
        {
            ApplyBrushStroke(tile, tile);
        }

        Advance(1);
        if (_playerCommands.Count != guardedCount + stockpile.Length)
        {
            throw new InvalidOperationException("The stockpile brush did not paint two cells.");
        }

        foreach (var tile in stockpile)
        {
            if (!_state!.StockpileCells.Any(cell => cell.Position == tile))
            {
                throw new InvalidOperationException(
                    $"The stockpile brush did not create a cell at ({tile.X},{tile.Y}).");
            }
        }

        CancelBrush("stockpile smoke");
        if (_editMode != BrushMode.Inspect)
        {
            throw new InvalidOperationException("The stockpile brush did not return to inspect mode.");
        }

        // The point of the step: nobody is addressed, yet the stone moves and the
        // total amount of stone in the world never changes.
        var produced = _state!.Economy.StoneProduced;
        for (var guard = 0; guard < 900 && _state.Stocks.StoredStone == 0; guard++)
        {
            Advance(1);
            var stocks = _state.Stocks;
            if (stocks.LooseStone + stocks.CarriedStone + stocks.StoredStone !=
                _state.Economy.StoneProduced)
            {
                throw new InvalidOperationException(
                    $"Stone conservation broke at tick {_state.Tick}: produced " +
                    $"{_state.Economy.StoneProduced}, loose {stocks.LooseStone}, " +
                    $"carried {stocks.CarriedStone}, stored {stocks.StoredStone}.");
            }
        }

        if (_state.Stocks.StoredStone == 0 || produced == 0)
        {
            throw new InvalidOperationException(
                "No stone reached the material stockpile inside the smoke budget.");
        }
    }

    /// <summary>
    /// An input-seam check for [C] and [V]: a stroke over rock, a feature and the
    /// gate emits nothing, a blueprint lands on ground the player dug, withdrawing
    /// it works, and then the whole chain — deliver, build, drill — runs with no
    /// order given and no stone lost.
    /// </summary>
    private void VerifyBuildBrushSmoke()
    {
        SelectEditMode(BrushMode.Build);
        if (_editMode != BrushMode.Build)
        {
            throw new InvalidOperationException("[C] did not select the build brush.");
        }

        var guardedChecksum = _checksum;
        var guardedCount = _playerCommands.Count;
        ApplyBrushStroke(new GridPoint(9, 4), new GridPoint(9, 4));   // internal rock
        ApplyBrushStroke(new GridPoint(0, 0), new GridPoint(0, 0));   // map boundary
        ApplyBrushStroke(PrototypeMapGate, PrototypeMapGate);
        ApplyBrushStroke(new GridPoint(14, 7), new GridPoint(14, 7));  // larder feature
        ApplyBrushStroke(new GridPoint(8, 12), new GridPoint(8, 12));  // an existing post
        ApplyBrushStroke(new GridPoint(22, 1), new GridPoint(22, 1));  // a stockpile cell
        if (_playerCommands.Count != guardedCount || _checksum != guardedChecksum)
        {
            throw new InvalidOperationException(
                "The build brush emitted a command for a tile the simulation forbids.");
        }

        // (25,1) and (25,2) are floor only because the dig smoke above excavated
        // them, which is the claim this step makes: a room out of carved space.
        var site = new GridPoint(25, 2);
        ApplyBrushStroke(new GridPoint(25, 1), new GridPoint(25, 1));
        ApplyBrushStroke(site, site);
        Advance(1);
        if (_playerCommands.Count != guardedCount + 2 ||
            !_state!.BuildSites.Any(item => item.Tile == site))
        {
            throw new InvalidOperationException("The build brush did not mark two blueprints.");
        }

        SelectEditMode(BrushMode.CancelBuild);
        ApplyBrushStroke(new GridPoint(25, 1), new GridPoint(25, 1));
        ApplyBrushStroke(new GridPoint(12, 12), new GridPoint(12, 12));
        Advance(1);
        if (_playerCommands.Count != guardedCount + 3 ||
            _state!.BuildSites.Count != 1)
        {
            throw new InvalidOperationException("The unbuild brush did not withdraw one blueprint.");
        }

        _editMode = BrushMode.Paint;
        _brushZone = ZoneKind.TrainingGround;
        ApplyBrushStroke(site, site);
        TryApplyPlayerCommand(new SetPriorityCommand(_state!.Tick, JobKind.Drill, 3));
        CancelBrush("build smoke");
        if (_editMode != BrushMode.Inspect)
        {
            throw new InvalidOperationException("The build brush did not return to inspect mode.");
        }

        // Nobody is addressed, yet the post appears and stone stops being a number.
        for (var guard = 0; guard < 900 && _state!.Economy.BuildsCompleted == 0; guard++)
        {
            Advance(1);
            var stocks = _state.Stocks;
            if (stocks.LooseStone + stocks.CarriedStone + stocks.StoredStone +
                stocks.SiteStone + _state.Economy.StoneConsumed !=
                _state.Economy.StoneProduced)
            {
                throw new InvalidOperationException(
                    $"Stone conservation broke at tick {_state.Tick}: produced " +
                    $"{_state.Economy.StoneProduced}, loose {stocks.LooseStone}, " +
                    $"carried {stocks.CarriedStone}, stored {stocks.StoredStone}, " +
                    $"on site {stocks.SiteStone}, consumed {_state.Economy.StoneConsumed}.");
            }
        }

        if (_state!.Economy.BuildsCompleted == 0 ||
            !_state.Map.BuiltPostTiles.Contains(site))
        {
            throw new InvalidOperationException(
                "No training post was built autonomously inside the smoke budget.");
        }

        for (var guard = 0; guard < 200 &&
             !_state.Jobs.Any(job => job.Kind == JobKind.Drill && job.Origin == site);
             guard++)
        {
            Advance(1);
        }

        if (!_state.Jobs.Any(job => job.Kind == JobKind.Drill && job.Origin == site))
        {
            throw new InvalidOperationException(
                "The built post produced no Drill job inside the smoke budget.");
        }
    }

    private static GridPoint PrototypeMapGate => new(27, 13);

    private void CaptureScreenshot(string path)
    {
        try
        {
            var resolved = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            var result = GetViewport().GetTexture().GetImage().SavePng(resolved);
            if (result != Error.Ok)
            {
                throw new IOException($"SavePng returned {result}.");
            }

            GD.Print(JsonSerializer.Serialize(new
            {
                @event = "godot_graybox_screenshot",
                status = "ok",
                fixture = _fixture,
                seed = _state!.Seed,
                tick = _state!.Tick,
                checksum = _checksum,
                path = resolved,
                view = ViewState(),
                // The frame carries its own conservation evidence, so a reviewer
                // never has to trust the picture alone.
                stoneProduced = _state.Economy.StoneProduced,
                looseStone = _state.Stocks.LooseStone,
                carriedStone = _state.Stocks.CarriedStone,
                storedStone = _state.Stocks.StoredStone,
                siteStone = _state.Stocks.SiteStone,
                stoneConsumed = _state.Economy.StoneConsumed,
                stockpileCapacity = _state.Stocks.StockpileCapacity,
                buildsCompleted = _state.Economy.BuildsCompleted,
                ui = UiText(),
                labelFit = LabelFit(),
                controlStrips = ControlStripFit(),
                loadedSpriteStates = _loadedSpriteStates,
                missingSpriteStates = _missingSpriteStates,
                fallbackSpriteDraws = _fallbackSpriteDraws,
                runtimeDiagnostics = _diagnostics,
            }));
        }
        catch (Exception exception)
        {
            RecordDiagnostic("screenshot", exception);
            GD.PushError(exception.ToString());
            GetTree().Quit(1);
        }
    }

    private void VerifyDeterministicFixture(string fixture)
    {
        var first = PrototypeScenario.Run(PrototypeCommandDocument.Load(FixturePath(fixture)), _state!.Tick);
        var second = PrototypeScenario.Run(PrototypeCommandDocument.Load(FixturePath(fixture)), _state.Tick);
        if (!first.CanonicalJson.AsSpan().SequenceEqual(second.CanonicalJson))
        {
            throw new InvalidOperationException("Fixture replay produced different canonical state.");
        }
    }

    private void PrintResult(string eventName, string status, Exception? exception)
    {
        try
        {
            GD.Print(JsonSerializer.Serialize(new
            {
                @event = eventName,
                status,
                fixture = _fixture,
                seed = _state?.Seed,
                tick = _state?.Tick,
                checksum = _checksum,
                canonicalStateOwner = "DungeonFortress.Simulation.PrototypeWorld",
                view = ViewState(),
                // The same conservation evidence a screenshot carries. A headless run
                // is now a complete frame report, so the golden UI state does not need
                // a window to be captured.
                stoneProduced = _state?.Economy.StoneProduced,
                looseStone = _state?.Stocks.LooseStone,
                carriedStone = _state?.Stocks.CarriedStone,
                storedStone = _state?.Stocks.StoredStone,
                siteStone = _state?.Stocks.SiteStone,
                stoneConsumed = _state?.Economy.StoneConsumed,
                stockpileCapacity = _state?.Stocks.StockpileCapacity,
                buildsCompleted = _state?.Economy.BuildsCompleted,
                ui = _state is null ? null : UiText(),
                labelFit = _state is null || _hudRoot is null ? null : LabelFit(),
                controlStrips = ControlStripFit(),
                loadedSpriteStates = _loadedSpriteStates,
                missingSpriteStates = _missingSpriteStates,
                fallbackSpriteDraws = _fallbackSpriteDraws,
                runtimeDiagnostics = _diagnostics,
                errorType = exception?.GetType().Name,
                message = exception?.Message,
            }));
        }
        catch (Exception reportingException) when (exception is not null)
        {
            // Error reporting must not hide the original startup failure. Keep
            // this fallback independent of nodes and snapshots that may not exist.
            GD.Print(JsonSerializer.Serialize(new
            {
                @event = eventName,
                status = "error",
                fixture = _fixture,
                errorType = exception.GetType().Name,
                message = exception.Message,
                reportingErrorType = reportingException.GetType().Name,
                reportingMessage = reportingException.Message,
            }));
        }
    }

    private static string FormatNumber(double value) =>
        value.ToString("G17", CultureInfo.InvariantCulture);

    private static string FormatPoint(ViewPoint point) =>
        $"({FormatNumber(point.X)}, {FormatNumber(point.Y)})";

    private static string FormatVector(Vector2 vector) =>
        $"({FormatNumber(vector.X)}, {FormatNumber(vector.Y)})";

    private void RecordDiagnostic(string scope, Exception exception)
    {
        _diagnostics.Add(new RuntimeDiagnostic(scope, exception.GetType().Name, exception.Message));
        if (_diagnostics.Count > 12)
        {
            _diagnostics.RemoveAt(0);
        }
    }

    private static string FixturePath(string fixture)
    {
        foreach (var startingDirectory in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(startingDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "scenarios", "prototype1", $"{fixture}.commands.v2.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"Could not locate prototype fixture '{fixture}'.");
    }

    // Adapter-side alias for the pure bounds check, so hit testing and drawing
    // read the same as before the seam landed.
    private static bool IsMapCell(GridPoint cell) => MapBounds.Contains(cell);

    private Vector2 CellTopLeft(GridPoint cell)
    {
        var point = CameraView.CellTopLeft(cell, _tileSize);
        return new Vector2((float)point.X, (float)point.Y);
    }

    private Vector2 CellCenter(GridPoint cell)
    {
        var point = CameraView.CellCenter(cell, _tileSize);
        return new Vector2((float)point.X, (float)point.Y);
    }

    private Color BaseTileColor(GridPoint cell)
    {
        // Rock is read from the snapshot, never from a hardcoded list: the map is
        // mutable canonical state and Godot only projects it.
        //
        // Rock is deliberately a warm stone grey, well above the cool blue floor
        // in both hue and brightness: the earlier near-black rock was reported as
        // indistinguishable from floor on the owner's display.
        if (_state!.Map.RockTiles.Contains(cell))
        {
            return _state.Map.DiggableTiles.Contains(cell)
                ? new Color("#6b6157")
                : new Color("#2a2522");
        }

        // Freshly excavated ground reads as new: brighter than the original floor.
        if (_state.Map.ExcavatedTiles.Contains(cell)) return new Color("#3b5a7a");

        if (_state!.Beds.Any(bed => bed.Position == cell)) return new Color("#31572c");
        if (_state.Stations.Any(station => station.Position == cell && station.Kind == TileKind.Kitchen)) return new Color("#7c4a22");
        if (_state.Stations.Any(station => station.Position == cell && station.Kind == TileKind.Post)) return new Color("#134e4a");
        if (_projection!.IsInZone(ZoneKind.Larder, cell)) return new Color("#5b3a32");
        if (cell is { X: 20 or 21, Y: 3 } or { X: 21 or 22, Y: 4 }) return new Color("#3b4252");
        if (cell == new GridPoint(27, 13)) return new Color("#854d0e");
        return new Color("#243244");
    }

    private static Color ZoneColor(ZoneKind zone) => zone switch
    {
        ZoneKind.Farm => new Color("#84cc16"),
        ZoneKind.Kitchen => new Color("#fb923c"),
        ZoneKind.Larder => new Color("#facc15"),
        ZoneKind.Quarters => new Color("#a78bfa"),
        ZoneKind.TrainingGround => new Color("#22d3ee"),
        ZoneKind.Watch => new Color("#f472b6"),
        ZoneKind.Forbidden => new Color("#ef4444"),
        ZoneKind.MaterialStockpile => new Color("#cbd5e1"),
        _ => new Color("#ffffff"),
    };

    /// <summary>
    /// Food and stone share one <c>Haul</c> kind but not one destination, so they
    /// must not share one route colour on the map.
    /// </summary>
    private static Color HaulRouteColor(PrototypeJobSnapshot job)
    {
        return job is { Kind: JobKind.Haul, Resource: ResourceKind.Stone }
            ? new Color("#cbd5e1")
            : JobColor(job.Kind);
    }

    private static Color JobColor(JobKind job) => job switch
    {
        JobKind.Harvest => new Color("#a3e635"),
        JobKind.Haul => new Color("#facc15"),
        JobKind.Cook => new Color("#fb923c"),
        JobKind.Rest => new Color("#a78bfa"),
        JobKind.Drill => new Color("#22d3ee"),
        JobKind.Watch => new Color("#f472b6"),
        JobKind.Dig => new Color("#f59e0b"),
        JobKind.Build => new Color("#2dd4bf"),
        _ => new Color("#ffffff"),
    };

    private string RaidLegend() =>
        "BATTLE LEGEND\n" +
        "teal = crew  •  red ring = raider\n" +
        "bar = HP  •  white X = DOWNED\n" +
        "dot: green work, amber combat,\n" +
        "gray downed, pink fled";

    // Adapter-side alias for the pure state abbreviation, so the map name labels
    // read the same as before the seam landed.
    private static string CreatureStateShort(PrototypeCreatureSnapshot creature) =>
        HudText.CreatureStateShort(creature);

    private static Color DefenderColor(PrototypeCreatureSnapshot creature) => creature.Mode switch
    {
        CreatureMode.Fighting => new Color("#fbbf24"),
        CreatureMode.Fled => new Color("#f472b6"),
        CreatureMode.Downed => new Color("#64748b"),
        CreatureMode.Working => new Color("#22d3ee"),
        _ => new Color("#38bdf8"),
    };

    private static Color CreatureStateColor(PrototypeCreatureSnapshot creature) => creature.Mode switch
    {
        CreatureMode.Downed => new Color("#94a3b8"),
        CreatureMode.Fled => new Color("#f472b6"),
        CreatureMode.Fighting => new Color("#fbbf24"),
        CreatureMode.Working => new Color("#4ade80"),
        _ => new Color("#bfdbfe"),
    };

    private static string RaiderSpriteKey(PrototypeRaiderSnapshot raider) => raider.Mode switch
    {
        RaiderMode.Downed => "downed",
        RaiderMode.Raiding when raider.ReturningToGate => "work",
        RaiderMode.Raiding => "combat",
        _ => "idle",
    };

    private static string CrewSpriteKey(PrototypeCreatureSnapshot creature) => creature.Mode switch
    {
        CreatureMode.Working => "work",
        CreatureMode.Fighting => "combat",
        CreatureMode.Downed => "downed",
        _ => "idle",
    };

    private void DrawGoblin(Vector2 center, string key)
    {
        if (_goblinSprites.TryGetValue(key, out var sprite))
        {
            var drawSize = (float)CameraView.GoblinDrawSize(_tileSize);
            var halfSize = drawSize / 2f;
            DrawTextureRect(
                sprite,
                new Rect2(
                    center - new Vector2(halfSize, halfSize),
                    new Vector2(drawSize, drawSize)),
                false);
            return;
        }

        // Missing exploratory art must not prevent a deterministic playable build.
        _fallbackSpriteDraws++;
        DrawCircle(center, ScaleWorld(6), new Color("#84cc16"));
    }

    private void DrawHpBar(Vector2 topLeft, int hp, int maxHp, Color color)
    {
        var width = ScaleWorld(14);
        var height = ScaleWorld(3);
        DrawRect(new Rect2(topLeft, new Vector2(width, height)), new Color("#0f172a"));
        DrawRect(
            new Rect2(
                topLeft,
                new Vector2(width * Math.Clamp(hp / (float)maxHp, 0, 1), height)),
            color);
    }

    private void DrawZoneLabels()
    {
        DrawZoneLabel(ZoneKind.Farm, new GridPoint(1, 1), "FARM");
        DrawZoneLabel(ZoneKind.Kitchen, new GridPoint(9, 6), "KITCHEN");
        DrawZoneLabel(ZoneKind.Larder, new GridPoint(13, 6), "LARDER");
        DrawZoneLabel(ZoneKind.Quarters, new GridPoint(19, 2), "QUARTERS");
        if (_projection!.Zone(ZoneKind.TrainingGround).Count > 0)
        {
            DrawZoneLabel(ZoneKind.TrainingGround, new GridPoint(7, 11), "TRAIN");
        }
    }

    private void DrawZoneLabel(ZoneKind zone, GridPoint anchor, string text)
    {
        if (!_projection!.IsInZone(zone, anchor))
        {
            return;
        }

        DrawString(
            ThemeDB.FallbackFont,
            CellTopLeft(anchor) + ScaleWorld(2, 10),
            text,
            HorizontalAlignment.Left,
            -1,
            Math.Max(1, (int)Math.Round(ScaleWorld(7))),
            ZoneColor(zone));
    }

    // EditMode used to be declared here. It is DungeonFortress.Presentation's
    // BrushMode now, because everything that has to be said about a brush — its
    // name, its tooltip, which cells a stroke over it would take — is text, and
    // text on this side of the seam is text no test in CI can read.

    private sealed record RuntimeDiagnostic(string Scope, string Type, string Message);
}
