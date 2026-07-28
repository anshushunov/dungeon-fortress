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
    private const int TileSize = 22;
    private const double TicksPerSecond = 6.0;
    // The map hangs below the two control strips, so its origin is derived from
    // the band they occupy rather than from a number nobody can re-derive. The
    // strips grew when they stopped being 9pt text and became icon buttons.
    private static readonly Vector2 MapOrigin = new(18, ToolbarStripTop + ControlStripsBandHeight);
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
    private Control? _hudRoot;
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

    public override void _Ready()
    {
        try
        {
            var arguments = OS.GetCmdlineUserArgs();
            var fixture = CommandLineArguments.Read(arguments, "--fixture") ?? "baseline";
            var screenshotTicks = CommandLineArguments.ReadInt(arguments, "--screenshot-ticks") ?? 1;
            _screenshotPath = CommandLineArguments.Read(arguments, "--screenshot");
            _screenshotFramesRemaining = _screenshotPath is null ? 0 : 3;
            var selectCreature = CommandLineArguments.ReadInt(arguments, "--select-creature");
            var selectCell = CommandLineArguments.Read(arguments, "--select-cell");
            var headlessSmoke = arguments.Contains("--smoke", StringComparer.Ordinal);
            var visibleSmoke = arguments.Contains("--visible-smoke", StringComparer.Ordinal);
            var controlsSmoke = arguments.Contains("--smoke-controls", StringComparer.Ordinal);
            var demoControls = arguments.Contains("--demo-controls", StringComparer.Ordinal);
            var demoDig = arguments.Contains("--demo-dig", StringComparer.Ordinal);
            var demoStone = arguments.Contains("--demo-stone", StringComparer.Ordinal);
            var demoBuild = arguments.Contains("--demo-build", StringComparer.Ordinal);
            // Holds the HUD to "every line fits", ignoring the deficit Issue #36
            // still owns. verify.ps1 runs it and requires it to fail: that is what
            // proves the guard reacts at all instead of passing on everything.
            var strictHudFit = arguments.Contains("--strict-hud-fit", StringComparer.Ordinal);
            var requiresSprites = !headlessSmoke && !controlsSmoke;

            // Before the HUD, because every toolbar button is created with the
            // texture it draws. A file the icon pack has not delivered yet becomes
            // a placeholder here and nowhere else, so dropping the real PNG in
            // changes no code at all.
            LoadIcons();
            CreateHud();
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
            PrintResult("godot_headless_smoke", "error", exception);
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
            var drawnCell = ToCell(render);
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
            tileSize = TileSize,
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
            case InputEventMouseMotion motion:
                UpdatePointer(motion.Position);
                if (_dragAnchor is not null && ToCell(motion.Position) is { } dragged &&
                    IsMapCell(dragged) && _dragCurrent != dragged)
                {
                    _dragCurrent = dragged;
                    UpdateHud();
                    QueueRedraw();
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

                if (ToCell(click.Position) is { } start && IsMapCell(start))
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
    // One thing is deliberately still pinned to constants: the map keeps
    // MapOrigin and TileSize. Replacing that belongs to ADR 0008's camera, not
    // here. The control strips no longer are: they are Buttons in a container, so
    // the band below reserves the height they lay themselves out into instead of
    // repeating the offsets a hit test used to check.
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
    private const int HudColumnSeparation = 10;
    private const int HudPanelSeparation = 6;
    private const int HudSidePanelMinimumWidth = 240;

    private static Vector2 MapPixelSize =>
        new(PrototypeTuning.MapWidth * TileSize, PrototypeTuning.MapHeight * TileSize);

    private void CreateHud()
    {
        // The root keeps top-left anchors and is resized explicitly, and that is
        // load-bearing rather than a style choice. A Control anchored to the full
        // rect measures itself against its parent's anchorable rect, and the
        // parent here is a Node2D, whose anchorable rect is empty — the HUD would
        // silently collapse to its own minimum size on the first layout pass
        // after _Ready. Top-left anchors have no such dependency, so the size the
        // viewport hands the HUD is the size it keeps.
        _hudRoot = new Control
        {
            Name = "Hud",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(_hudRoot);
        _hudRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        GetViewport().SizeChanged += OnViewportResized;

        var margins = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hudRoot.AddChild(margins);
        margins.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        margins.AddThemeConstantOverride("margin_left", (int)MapOrigin.X);
        margins.AddThemeConstantOverride("margin_top", HudTopMargin);
        margins.AddThemeConstantOverride("margin_right", HudRightMargin);
        margins.AddThemeConstantOverride("margin_bottom", HudBottomMargin);

        var columns = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        columns.AddThemeConstantOverride("separation", HudColumnSeparation);
        margins.AddChild(columns);
        columns.AddChild(CreateMapColumn());
        columns.AddChild(CreateSideColumn());
        LayoutHud(GetViewportRect().Size);
    }

    private void OnViewportResized() => LayoutHud(GetViewportRect().Size);

    /// <summary>
    /// The left column: the header, the band the two control strips are drawn
    /// in, the band the map is drawn in, and the roster underneath. Only the
    /// roster expands, because everything above it is drawn at a fixed pixel
    /// geometry that the camera work of ADR 0008 owns.
    /// </summary>
    private Control CreateMapColumn()
    {
        var column = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            CustomMinimumSize = new Vector2(MapPixelSize.X + 10, 0),
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
        column.AddChild(new Control
        {
            Name = "Map",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = MapPixelSize,
        });

        _roster = MakeHudLabel(10, new Color("#cbd5e1"));
        _roster.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
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
    /// The measurable claim of this step: the brush strip is narrower than the map
    /// it marks. It used to end at 676px against a 616px map, which is what a row
    /// of eleven text buttons costs.
    ///
    /// Measured at every frame size the HUD guard uses, and for the same reason: a
    /// check that only ever saw one size cannot tell a layout that fits from one
    /// that happens to.
    /// </summary>
    private void AssertControlStripsFit()
    {
        var live = GetViewportRect().Size;
        var failures = new List<string>();
        foreach (var viewport in HudFitViewports())
        {
            LayoutHud(viewport);
            foreach (var (name, strip) in ControlStrips())
            {
                if (strip is null || strip.Size.X <= MapPixelSize.X)
                {
                    continue;
                }

                failures.Add(
                    $"'{name}' is {strip.Size.X}px wide at viewport {viewport}, " +
                    $"wider than the {MapPixelSize.X}px map it marks");
            }
        }

        LayoutHud(live);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"A control strip is wider than the map in {failures.Count} place(s): " +
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
    private object ControlStripFit() => new
    {
        mapWidth = MapPixelSize.X,
        widths = ControlStrips()
            .Where(entry => entry.Strip is not null)
            .Select(entry => (object)new { name = entry.Name, width = entry.Strip!.Size.X })
            .ToArray(),
        iconDrawSize = IconDrawSize,
        loadedIcons = _icons.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        placeholderIcons = _missingIcons,
    };

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
        foreach (var state in new[] { "idle", "work", "combat", "downed" })
        {
            var path = $"res://assets/generated/goblins/goblin_{state}_v1.png";
            if (ResourceLoader.Exists(path) && GD.Load<Texture2D>(path) is { } texture)
            {
                _goblinSprites.Add(state, texture);
                _loadedSpriteStates.Add(state);
            }
            else
            {
                _missingSpriteStates.Add(state);
            }
        }
    }

    private void AssertRequiredSpritesLoaded()
    {
        if (_missingSpriteStates.Count == 0)
        {
            return;
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
                label = MakeMapLabel(new Vector2(98, 17), 10, CreatureColors[creature.Id]);
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
                new Vector2(2 - (TileSize / 2f), -14 - (TileSize / 2f));
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
        var panels = HudText.Build(CurrentHudView());
        _inspector!.Text = panels.Inspector;
        _summary!.Text = panels.Summary;
        _feedback!.Text = panels.Feedback;
        _roster!.Text = panels.Roster;
        RefreshControls();
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
    };

    /// <summary>
    /// Viewport sizes the HUD is required to hold all of its text at. The live
    /// frame is always one of them; the rest exist so that "the layout follows
    /// the viewport" is a checked claim rather than an intention. ADR 0008 turns
    /// the frame into an input, and a guard that only ever saw one size could not
    /// tell a responsive layout from a lucky one.
    ///
    /// A size that is missing here is not "unsupported": it is unmeasured. The
    /// current text does not fit the old 960x540 frame at readable sizes — the
    /// side column needs about 33 lines and that frame offers about 29 — which is
    /// exactly the deficit Issue #28 measured and this Issue had to clear.
    /// </summary>
    private Vector2[] HudFitViewports() =>
        new[]
        {
            GetViewportRect().Size,
            new Vector2(1280, 720),
            new Vector2(1366, 768),
            new Vector2(1600, 900),
            new Vector2(1024, 768),
        }.Distinct().ToArray();

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
    private void LayoutHud(Vector2 size)
    {
        _hudRoot!.Size = size;
        _hudRoot.PropagateNotification((int)Container.NotificationSortChildren);
        foreach (var line in _legendLines)
        {
            line.CustomMinimumSize = new Vector2(0, HudTextHeight(line, line.GetLineCount()));
        }

        _hudRoot.PropagateNotification((int)Container.NotificationSortChildren);
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
        foreach (var viewport in HudFitViewports())
        {
            LayoutHud(viewport);
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
                        $"{label.Size} at viewport {viewport}");
                }
            }
        }

        LayoutHud(live);

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
            checkedViewports = HudFitViewports()
                .Select(size => new[] { size.X, size.Y })
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
        };
    }

    private void DrawMap()
    {
        DrawRect(new Rect2(MapOrigin, MapPixelSize), new Color("#111827"));
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                var cell = new GridPoint(x, y);
                var rect = new Rect2(CellTopLeft(cell), new Vector2(TileSize - 1, TileSize - 1));
                if (_state!.Map.RockTiles.Contains(cell))
                {
                    // Rock fills the grid gap as well, so a wall reads as one
                    // solid mass. Colour alone did not separate it from floor.
                    DrawRect(
                        new Rect2(CellTopLeft(cell), new Vector2(TileSize, TileSize)),
                        BaseTileColor(cell));
                }
                else
                {
                    DrawRect(rect, BaseTileColor(cell));
                }

                foreach (var zone in _state.Zones.Where(pair => pair.Value.Contains(cell)).Select(pair => pair.Key))
                {
                    DrawRect(rect.Grow(-3), ZoneColor(zone), false, 1.5f);
                }

                // While a marking brush is held every legal target is outlined, so
                // the player never has to guess where a stroke would land. The
                // rule is read from the same function the stroke itself uses, so
                // an outlined cell and an accepted cell cannot be different sets.
                if (LegalTargetOutline() is { } outline &&
                    BrushSelection.Accepts(_state, _editMode, _brushZone, cell))
                {
                    DrawRect(rect.Grow(-2), outline, false, 1.0f);
                }

                if (_selectedCell == cell)
                {
                    DrawRect(rect.Grow(-1), new Color("#f8fafc"), false, 2.0f);
                }
            }
        }

        DrawDigDesignations();
        DrawBuildSites();
        DrawBuiltPosts();
        DrawStockpileCells();

        foreach (var bed in _state!.Beds)
        {
            DrawCircle(CellCenter(bed.Position), 5, bed.IsRipe ? new Color("#bef264") : new Color("#4d7c0f"));
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
            DrawCircle(center, 3 + Math.Min(3, loose.Quantity), color);
            if (loose.Resource == ResourceKind.Stone)
            {
                // A dark rim separates loose stone from a pale meal at a glance.
                DrawArc(center, 4.5f, 0, Mathf.Tau, 12, new Color("#475569"), 1.5f);
            }
        }

        foreach (var job in _state.Jobs)
        {
            var color = HaulRouteColor(job);
            DrawLine(CellCenter(job.Origin), CellCenter(job.Target), color with { A = 0.35f }, 1.0f);
            DrawCircle(CellCenter(job.Target), 3.2f, color);

            // A booked stockpile cell is part of the route even before pickup, so
            // the player can see where this pile is going.
            if (job.StoreCell is { } storeCell && storeCell != job.Target)
            {
                DrawLine(
                    CellCenter(job.Target),
                    CellCenter(storeCell),
                    color with { A = 0.25f },
                    1.0f);
            }
        }

        foreach (var creature in _state.Creatures)
        {
            // Interpolated, not canonical: the body, its carried item, its health
            // bar and its selection ring all hang off this one point, so lerping
            // it is what turns six canonical steps a second into motion.
            var center = CreatureRenderCenter(creature);
            var color = DefenderColor(creature);
            // The generated character states serve both factions; the outline is
            // the stable team cue (teal crew, red-raider ring).
            DrawCircle(center, 9, color);
            DrawGoblin(center, CrewSpriteKey(creature));
            if (creature.Mode == CreatureMode.Downed)
            {
                DrawLine(center + new Vector2(-5, -5), center + new Vector2(5, 5), new Color("#f8fafc"), 2);
                DrawLine(center + new Vector2(5, -5), center + new Vector2(-5, 5), new Color("#f8fafc"), 2);
            }

            DrawHpBar(center + new Vector2(-7, 8), creature.Hp, creature.MaxHp, color);
            DrawCircle(center + new Vector2(7, -7), 2.25f, CreatureStateColor(creature));

            if (_selectedCreatureId == creature.Id)
            {
                DrawArc(center, 10, 0, Mathf.Tau, 16, new Color("#ffffff"), 2);
            }

            if (creature.Carrying is ResourceKind.Stone)
            {
                // Stone rides as a rimmed grey square, the same shape a stockpile
                // pip uses, so "carrying" and "stored" read as the same material.
                DrawRect(
                    new Rect2(center + new Vector2(3, -9), new Vector2(6, 6)),
                    new Color("#e2e8f0"));
                DrawRect(
                    new Rect2(center + new Vector2(3, -9), new Vector2(6, 6)),
                    new Color("#0f172a"),
                    false,
                    1.0f);
            }
            else if (creature.Carrying is not null)
            {
                DrawCircle(center + new Vector2(6, -6), 2.5f, creature.Carrying == ResourceKind.Meal ? new Color("#fde68a") : new Color("#a3e635"));
            }
        }

        foreach (var raider in _state.Raiders)
        {
            if (raider.Mode == RaiderMode.Escaped) continue;
            var center = RaiderRenderCenter(raider);
            DrawCircle(center, 9, new Color("#7f1d1d"));
            DrawGoblin(center, RaiderSpriteKey(raider));
            DrawHpBar(center + new Vector2(-7, 9), raider.Hp, PrototypeTuning.RaiderHp, new Color("#fb7185"));
            if (raider.Mode == RaiderMode.Downed)
            {
                DrawLine(center + new Vector2(-5, -5), center + new Vector2(5, 5), new Color("#f8fafc"), 2);
                DrawLine(center + new Vector2(5, -5), center + new Vector2(-5, 5), new Color("#f8fafc"), 2);
            }
            else
            {
                DrawCircle(center + new Vector2(6, -6), 2, new Color("#fecaca"));
            }
        }

        DrawZoneLabels();
        DrawBrushPreview();
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
    private void DrawBrushPreview()
    {
        if (_editMode == BrushMode.Inspect || _state is null)
        {
            return;
        }

        if (PendingStroke() is { } stroke && _dragAnchor is { } anchor)
        {
            var corner = _dragCurrent ?? anchor;
            var accepted = stroke.Tiles.ToHashSet();
            foreach (var cell in BrushSelection.Rectangle(anchor, corner))
            {
                var color = accepted.Contains(cell) ? BrushAccent() : new Color("#ef4444");
                var tile = new Rect2(CellTopLeft(cell), new Vector2(TileSize - 1, TileSize - 1));
                DrawRect(tile.Grow(-1), color with { A = 0.32f });
            }

            var topLeft = CellTopLeft(new GridPoint(
                Math.Min(anchor.X, corner.X),
                Math.Min(anchor.Y, corner.Y)));
            var size = new Vector2(
                (Math.Abs(anchor.X - corner.X) + 1) * TileSize,
                (Math.Abs(anchor.Y - corner.Y) + 1) * TileSize);
            DrawRect(new Rect2(topLeft, size - new Vector2(1, 1)), new Color("#f8fafc"), false, 1.5f);
            DrawSelectionCount(topLeft, stroke);
            return;
        }

        if (_hoverCell is not { } hovered || !IsMapCell(hovered))
        {
            return;
        }

        var preview = new Rect2(CellTopLeft(hovered), new Vector2(TileSize - 1, TileSize - 1));
        var previewColor = BrushSelection.Accepts(_state, _editMode, _brushZone, hovered)
            ? BrushAccent()
            : new Color("#ef4444");
        DrawRect(preview.Grow(-1), previewColor with { A = 0.32f });
        DrawRect(preview.Grow(-1), new Color("#f8fafc"), false, 1.5f);
    }

    /// <summary>
    /// The number of cells the command will carry, drawn on the selection itself.
    /// It is the accepted count and not the area of the rectangle: a drag across
    /// floor and rock states how much of it the brush will actually take.
    /// </summary>
    private void DrawSelectionCount(Vector2 topLeft, BrushStroke stroke)
    {
        const float width = 58;
        const float height = 14;
        var text = stroke.Tiles.Count == 1 ? "1 cell" : $"{stroke.Tiles.Count} cells";

        // Kept inside the map on both axes. Above the selection when there is room
        // and inside its first cell when there is not: the control strips are
        // Control nodes and draw over the canvas, so a caption that overhangs the
        // top of the map would be covered by the strip rather than clipped.
        var box = new Vector2(
            Math.Clamp(topLeft.X, MapOrigin.X, MapOrigin.X + MapPixelSize.X - width),
            topLeft.Y - height - 3 >= MapOrigin.Y ? topLeft.Y - height - 3 : topLeft.Y + 3);

        DrawRect(new Rect2(box, new Vector2(width, height)), new Color("#0b1622"));
        DrawString(
            ThemeDB.FallbackFont,
            box + new Vector2(3, height - 3),
            text,
            HorizontalAlignment.Left,
            width - 6,
            11,
            stroke.Tiles.Count == 0 ? new Color("#fca5a5") : new Color("#f8fafc"));
    }

    /// <summary>
    /// Three distinct readings the player must get without opening the log: an
    /// intention that is waiting, an intention nobody can reach, and work in
    /// progress with how far along it is.
    /// </summary>
    private void DrawDigDesignations()
    {
        foreach (var designation in _state!.DigDesignations)
        {
            var rect = new Rect2(
                CellTopLeft(designation.Tile),
                new Vector2(TileSize - 1, TileSize - 1));
            var accent = designation.StatusCode switch
            {
                "dig_in_progress" => new Color("#fbbf24"),
                "dig_unreachable" => new Color("#f87171"),
                "dig_blocked_priority" => new Color("#94a3b8"),
                _ => new Color("#f59e0b"),
            };

            DrawRect(rect.Grow(-1), accent with { A = 0.26f });
            DrawRect(rect.Grow(-1), accent, false, 1.5f);

            // The crossed pick reads as "marked for excavation" at tile size.
            var center = CellCenter(designation.Tile);
            DrawLine(center + new Vector2(-5, -5), center + new Vector2(5, 5), accent, 1.5f);
            DrawLine(center + new Vector2(5, -5), center + new Vector2(-5, 5), accent, 1.5f);

            if (designation.StatusCode == "dig_unreachable")
            {
                continue;
            }

            if (designation.WorkTile is { } workTile)
            {
                DrawLine(CellCenter(workTile), center, accent with { A = 0.55f }, 1.0f);
            }

            if (designation.ProgressTicks <= 0 || designation.RequiredTicks <= 0)
            {
                continue;
            }

            var fraction = Math.Clamp(
                designation.ProgressTicks / (float)designation.RequiredTicks,
                0f,
                1f);

            // The rock is eaten away from the bottom up, so progress is readable
            // at tile size without hunting for a thin bar.
            var eaten = (TileSize - 3) * fraction;
            DrawRect(
                new Rect2(
                    CellTopLeft(designation.Tile) + new Vector2(1, TileSize - 2 - eaten),
                    new Vector2(TileSize - 3, eaten)),
                new Color("#fbbf24") with { A = 0.6f });

            var barTopLeft = CellTopLeft(designation.Tile) + new Vector2(2, TileSize - 7);
            DrawRect(new Rect2(barTopLeft, new Vector2(TileSize - 5, 4)), new Color("#0f172a"));
            DrawRect(
                new Rect2(barTopLeft, new Vector2((TileSize - 5) * fraction, 4)),
                new Color("#fde047"));
        }
    }

    /// <summary>
    /// A blueprint has to answer three questions at tile size: is this an
    /// intention rather than a building, how much of its material has arrived, and
    /// is anything actually happening. Delivered blocks are drawn as discrete pips
    /// so "1 of 2" is countable, and the caption keeps the graybox primitive
    /// readable without an asset — ADR 0008 is accepted but not implemented.
    /// </summary>
    private void DrawBuildSites()
    {
        foreach (var site in _state!.BuildSites)
        {
            var rect = new Rect2(
                CellTopLeft(site.Tile),
                new Vector2(TileSize - 1, TileSize - 1));
            var accent = site.StatusCode switch
            {
                "build_in_progress" => new Color("#5eead4"),
                "build_unreachable" => new Color("#f87171"),
                "build_blocked_priority" or "build_haul_blocked" => new Color("#94a3b8"),
                "build_no_stone" or "build_stone_reserved" => new Color("#fbbf24"),
                _ => new Color("#2dd4bf"),
            };

            DrawRect(rect.Grow(-1), accent with { A = 0.22f });
            DrawRect(rect.Grow(-1), accent, false, 1.5f);

            var topLeft = CellTopLeft(site.Tile);
            DrawString(
                ThemeDB.FallbackFont,
                topLeft + new Vector2(2, 8),
                "POST?",
                HorizontalAlignment.Left,
                TileSize - 3,
                6,
                accent);

            for (var index = 0; index < site.Required; index++)
            {
                var pip = new Rect2(
                    topLeft + new Vector2(3 + index * 7, TileSize - 9),
                    new Vector2(5, 5));
                if (index < site.Delivered)
                {
                    DrawRect(pip, new Color("#e2e8f0"));
                    DrawRect(pip, new Color("#475569"), false, 1.0f);
                }
                else if (index < site.Delivered + site.IncomingReserved)
                {
                    DrawRect(pip, new Color("#7dd3fc"), false, 1.0f);
                }
                else
                {
                    DrawRect(pip, accent with { A = 0.45f }, false, 1.0f);
                }
            }

            if (site.ProgressTicks <= 0 || site.RequiredTicks <= 0)
            {
                continue;
            }

            var fraction = Math.Clamp(
                site.ProgressTicks / (float)site.RequiredTicks,
                0f,
                1f);
            var barTopLeft = topLeft + new Vector2(2, 2);
            DrawRect(new Rect2(barTopLeft, new Vector2(TileSize - 5, 3)), new Color("#0f172a"));
            DrawRect(
                new Rect2(barTopLeft, new Vector2((TileSize - 5) * fraction, 3)),
                new Color("#5eead4"));
        }
    }

    /// <summary>
    /// The end of the chain, drawn as a graybox primitive with a caption: a solid
    /// teal block so a built post reads as a built thing rather than as floor, and
    /// the word itself because a 22 px square cannot say "training post" on its
    /// own. The authored posts are drawn the same way, so the player cannot tell
    /// them apart — which is the claim the step is making.
    /// </summary>
    private void DrawBuiltPosts()
    {
        foreach (var station in _state!.Stations.Where(item => item.Kind == TileKind.Post))
        {
            var topLeft = CellTopLeft(station.Position);
            var rect = new Rect2(topLeft, new Vector2(TileSize - 1, TileSize - 1));
            DrawRect(rect.Grow(-3), new Color("#0f766e"));
            DrawRect(rect.Grow(-3), new Color("#5eead4"), false, 1.0f);
            DrawString(
                ThemeDB.FallbackFont,
                topLeft + new Vector2(2, 9),
                "POST",
                HorizontalAlignment.Left,
                TileSize - 3,
                6,
                new Color("#ccfbf1"));
        }
    }

    /// <summary>
    /// A stockpile cell has to answer three questions at tile size: is this a
    /// storage slot at all, how full is it, and is its remaining room already
    /// promised to someone on the way. Stored blocks are drawn as discrete pips so
    /// "2 of 2" is countable rather than inferred from a bar.
    /// </summary>
    private void DrawStockpileCells()
    {
        foreach (var cell in _state!.StockpileCells)
        {
            var rect = new Rect2(
                CellTopLeft(cell.Position),
                new Vector2(TileSize - 1, TileSize - 1));
            var accent = cell.StatusCode switch
            {
                "stockpile_unreachable" => new Color("#f87171"),
                "stockpile_full" => new Color("#e2e8f0"),
                "stockpile_incoming" => new Color("#7dd3fc"),
                _ => new Color("#94a3b8"),
            };

            DrawRect(rect.Grow(-1), new Color("#1f2937"));
            DrawRect(rect.Grow(-1), accent, false, 1.5f);

            // Corner ticks read as "a marked-out storage square" instead of just
            // another zone outline.
            var topLeft = CellTopLeft(cell.Position);
            foreach (var corner in new[]
                     {
                         (new Vector2(2, 2), new Vector2(6, 2), new Vector2(2, 6)),
                         (new Vector2(TileSize - 3, 2), new Vector2(TileSize - 7, 2), new Vector2(TileSize - 3, 6)),
                     })
            {
                DrawLine(topLeft + corner.Item1, topLeft + corner.Item2, accent, 1.0f);
                DrawLine(topLeft + corner.Item1, topLeft + corner.Item3, accent, 1.0f);
            }

            for (var index = 0; index < cell.Stored; index++)
            {
                DrawRect(
                    new Rect2(
                        topLeft + new Vector2(4 + index * 7, TileSize - 10),
                        new Vector2(6, 6)),
                    new Color("#e2e8f0"));
                DrawRect(
                    new Rect2(
                        topLeft + new Vector2(4 + index * 7, TileSize - 10),
                        new Vector2(6, 6)),
                    new Color("#475569"),
                    false,
                    1.0f);
            }

            // A hollow pip per booked slot: the player sees the room is taken even
            // though the carrier has not arrived yet.
            for (var index = cell.Stored; index < cell.Stored + cell.IncomingReserved; index++)
            {
                DrawRect(
                    new Rect2(
                        topLeft + new Vector2(4 + index * 7, TileSize - 10),
                        new Vector2(6, 6)),
                    new Color("#7dd3fc"),
                    false,
                    1.0f);
            }
        }
    }

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
        _hoverCell = ToCell(position) is { } cell && IsMapCell(cell) ? cell : null;
        _hoverCreatureId = _hoverCell is { } hovered
            ? _state!.Creatures.FirstOrDefault(creature => creature.Position == hovered)?.Id
            : null;
        UpdateCreatureLabels();
        QueueRedraw();
    }

    private void SelectAt(Vector2 position)
    {
        var cell = ToCell(position);
        if (cell is not { } selected || !IsMapCell(selected))
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
        _state is null || _dragAnchor is not { } anchor
            ? null
            : BrushSelection.Resolve(
                _state,
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
        if (_state is null)
        {
            return;
        }

        var stroke = BrushSelection.Resolve(_state, _editMode, _brushZone, from, to);
        if (BrushSelection.ToCommand(stroke, _state.Tick) is { } command)
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

        // A command issued at tick T is applied at the start of tick T, so the
        // designations only become visible to the brush after one step.
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
                tick = _state!.Tick,
                checksum = _checksum,
                path = resolved,
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
        GD.Print(JsonSerializer.Serialize(new
        {
            @event = eventName,
            status,
            fixture = _fixture,
            seed = _state?.Seed,
            tick = _state?.Tick,
            checksum = _checksum,
            canonicalStateOwner = "DungeonFortress.Simulation.PrototypeWorld",
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
            ui = UiText(),
            labelFit = LabelFit(),
            controlStrips = ControlStripFit(),
            loadedSpriteStates = _loadedSpriteStates,
            missingSpriteStates = _missingSpriteStates,
            fallbackSpriteDraws = _fallbackSpriteDraws,
            runtimeDiagnostics = _diagnostics,
            errorType = exception?.GetType().Name,
            message = exception?.Message,
        }));
    }

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

    private static GridPoint? ToCell(Vector2 position)
    {
        var x = (int)((position.X - MapOrigin.X) / TileSize);
        var y = (int)((position.Y - MapOrigin.Y) / TileSize);
        return new GridPoint(x, y);
    }

    private static Vector2 CellTopLeft(GridPoint cell) => MapOrigin + new Vector2(cell.X * TileSize, cell.Y * TileSize);

    private static Vector2 CellCenter(GridPoint cell) => CellTopLeft(cell) + new Vector2(TileSize / 2f, TileSize / 2f);

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
        if (_state.Zones[ZoneKind.Larder].Contains(cell)) return new Color("#5b3a32");
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
            DrawTextureRect(sprite, new Rect2(center - new Vector2(10, 10), new Vector2(20, 20)), false);
            return;
        }

        // Missing exploratory art must not prevent a deterministic playable build.
        _fallbackSpriteDraws++;
        DrawCircle(center, 6, new Color("#84cc16"));
    }

    private void DrawHpBar(Vector2 topLeft, int hp, int maxHp, Color color)
    {
        const float width = 14;
        DrawRect(new Rect2(topLeft, new Vector2(width, 3)), new Color("#0f172a"));
        DrawRect(new Rect2(topLeft, new Vector2(width * Math.Clamp(hp / (float)maxHp, 0, 1), 3)), color);
    }

    private void DrawZoneLabels()
    {
        DrawZoneLabel(ZoneKind.Farm, new GridPoint(1, 1), "FARM");
        DrawZoneLabel(ZoneKind.Kitchen, new GridPoint(9, 6), "KITCHEN");
        DrawZoneLabel(ZoneKind.Larder, new GridPoint(13, 6), "LARDER");
        DrawZoneLabel(ZoneKind.Quarters, new GridPoint(19, 2), "QUARTERS");
        if (_state!.Zones[ZoneKind.TrainingGround].Count > 0)
        {
            DrawZoneLabel(ZoneKind.TrainingGround, new GridPoint(7, 11), "TRAIN");
        }
    }

    private void DrawZoneLabel(ZoneKind zone, GridPoint anchor, string text)
    {
        if (!_state!.Zones[zone].Contains(anchor))
        {
            return;
        }

        DrawString(ThemeDB.FallbackFont, CellTopLeft(anchor) + new Vector2(2, 10), text, HorizontalAlignment.Left, -1, 7, ZoneColor(zone));
    }

    // EditMode used to be declared here. It is DungeonFortress.Presentation's
    // BrushMode now, because everything that has to be said about a brush — its
    // name, its tooltip, which cells a stroke over it would take — is text, and
    // text on this side of the seam is text no test in CI can read.

    private sealed record RuntimeDiagnostic(string Scope, string Type, string Message);
}
