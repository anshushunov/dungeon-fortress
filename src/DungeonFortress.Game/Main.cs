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
    // Те же шесть поз с каждым непрозрачным пикселем, выкрученным в белый.
    // Копия силуэта под телом должна быть плоской фигурой цвета стороны, а не
    // палитрой спрайта, умноженной на него: иначе контур унаследует
    // собственные свет и тень гоблина. Строится один раз при загрузке пака.
    private readonly Dictionary<string, Texture2D> _goblinSilhouettes = [];
    private readonly List<string> _loadedSpriteStates = [];
    private readonly List<string> _missingSpriteStates = [];
    // The cutout body of ADR 0020: the rig as the asset states it, one texture
    // per part and the same part whitened for the side outline and the flash.
    // All three are presentation and nothing else — see BodyRig.
    private BodyRig? _bodyRig;
    private readonly Dictionary<string, Texture2D> _rigParts = [];
    private readonly Dictionary<string, Texture2D> _rigPartSilhouettes = [];
    private readonly List<string> _missingRigParts = [];
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
    private readonly List<HudButton> _controlButtons = [];
    // A permanent, invisible sample of the tooltip HudButton draws, kept for the
    // HUD readability guard's subtree walk: see CreateControlStrips and
    // HudButton.MakeAuthoredTooltip. Issue #127.
    private Control? _tooltipReadabilitySample;
    private readonly List<Label> _hotkeyBadges = [];
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
    // Set only when this run sized its own window from the screen. A capture
    // never does, because a capture has to declare every pixel-affecting value.
    private ViewSize? _autoFrameSize;
    private bool _uiScaleIsAutomatic;
    // Same distinction as _uiScaleIsAutomatic, for the world rather than the
    // HUD. It stops being true the moment the player turns the wheel: after
    // that the zoom is theirs and no resize may take it back (Issue #86).
    private bool _cameraZoomIsAutomatic;
    private ViewRect? _screenUsableRect;
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
    // The blows of the moment being drawn, and the hit points they are measured
    // against. Both are presentation state of the same kind as the motion buffer
    // above: derived from canonical snapshots, never written back to one. What
    // they mean and why the hit points are needed at all is
    // DungeonFortress.Presentation.BlowReadout.
    private readonly Dictionary<int, int> _creatureHitPointsBefore = [];
    private BlowReading _blows = BlowReading.Empty;
    // Which way each body is turned. Presentation state of the same kind again:
    // it is written from the step a snapshot shows and from the blows read off
    // the canonical journal, and nothing reads it back. It has to be remembered
    // rather than recomputed per frame because a facing outlives the step that
    // set it — see BodyMotion.Turn.
    private readonly Dictionary<BodyRef, BodyFacing> _bodyFacing = [];
    // The frame the body being drawn stands in, kept so that every part of the
    // rig can be placed inside it: PushBodyPose sets it, and the parts multiply
    // their own joint transform onto it. It is a drawing state and is reset by
    // ClearBodyPose along with the canvas transform itself.
    private Transform2D _bodyFrame = Transform2D.Identity;
    // The duel scene of Issue #244, and the frame of the strike chain a paused
    // run is scrubbed to. Both are presentation: the pair is two bodies the
    // canonical journal already named, and the scrub only decides which moment
    // between two canonical snapshots is drawn.
    private (BodyRef Attacker, BodyRef Target)? _duelPair;
    private double? _strikeScrub;
    // Draws the flat pack where the rig would be drawn, on the same scene at the
    // same moment. It is the instrument ADR 0020's revision condition needs — «если
    // на пробе владелец скажет, что скелет из частей выглядит хуже нынешних поз» —
    // and it is what the "before" frames of this Issue are captured with, so the
    // two pictures differ in the body and in nothing else.
    private bool _flatBody;
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
            // The readability counterpart of the flag above: it re-authors a
            // legend row below the physical floor, so verify.ps1 can require the
            // readability policy to reject a HUD instead of trusting that it
            // would (Issues #86 and #49).
            var hudReadabilityRegression =
                arguments.Contains("--smoke-hud-readability-regression", StringComparer.Ordinal);
            // The tooltip's own regression flag (Issue #127): the guard above
            // never had a tooltip to reject, so this one shrinks the
            // readability sample CreateControlStrips keeps rather than a
            // legend row.
            var hudTooltipReadabilityRegression =
                arguments.Contains("--smoke-hud-tooltip-readability-regression", StringComparer.Ordinal);
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
            // An omitted --ui-scale is not the same as "--ui-scale 1". The first
            // asks this machine's screen what is readable; the second freezes a
            // number, which is what a reproducible frame needs and what
            // ViewLaunchOptions already demands from every capture.
            _uiScaleIsAutomatic = CommandLineArguments.Read(arguments, "--ui-scale") is null;
            _cameraZoomIsAutomatic = CommandLineArguments.Read(arguments, "--camera-zoom") is null;
            CameraView.AssertStartupFramePolicy(ViewLaunchOptions.MinimumLogicalFrameSize);
            ConfigureStartupFrame();
            var selectCreature = CommandLineArguments.ReadInt(arguments, "--select-creature");
            var selectCell = CommandLineArguments.Read(arguments, "--select-cell");
            var demoControls = arguments.Contains("--demo-controls", StringComparer.Ordinal);
            var demoDig = arguments.Contains("--demo-dig", StringComparer.Ordinal);
            var demoStone = arguments.Contains("--demo-stone", StringComparer.Ordinal);
            var demoBuild = arguments.Contains("--demo-build", StringComparer.Ordinal);
            // Issue #244 / ADR 0020: the duel scene. It runs the shipped raid
            // fixture forward to the first tick the canonical journal records a
            // blow on, points the camera at the two bodies it names and stops
            // there. Nothing about it is simulation: the search runs ordinary
            // ticks and stops on one, and the frame is chosen by a reading of the
            // journal the view already builds every tick.
            var demoDuel = arguments.Contains("--demo-duel", StringComparer.Ordinal);
            var duelFrame = CommandLineArguments.ReadInt(arguments, "--demo-duel-frame");
            _flatBody = arguments.Contains("--flat-body", StringComparer.Ordinal);
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
            // After the HUD, because the zoom is derived from the rectangle the
            // layout reserves for the world, and before the camera, so the node
            // is created at the zoom it will keep.
            ApplyAutomaticCameraZoom();
            CreateCamera();
            LoadGoblinSprites();
            LoadGoblinRig();
            if (requiresSprites)
            {
                AssertRequiredSpritesLoaded();
                AssertRigLoaded();
            }
            LoadFixture(
                fixture,
                demoControls || demoDig || demoStone || demoBuild || demoDuel ||
                controlsSmoke || _screenshotPath is null
                    ? 1
                    : screenshotTicks);
            if (hudGuardRegression)
            {
                InjectHudGuardRegression();
            }

            if (hudReadabilityRegression)
            {
                InjectHudReadabilityRegression();
            }

            if (hudTooltipReadabilityRegression)
            {
                InjectHudTooltipReadabilityRegression();
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

            if (demoDuel)
            {
                ApplyDemoDuel(duelFrame);
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
            AssertLabelsFit();
            AssertControlStripsFit();
            // Fitting and being readable are different questions, and until
            // Issue #86 only the first one had an answer. This one measures the
            // fonts the labels above were actually given and hands them to the
            // engine-free policy.
            AssertHudTextReadable();
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
            // Same re-derivation as on a resize, and for the same reason: this
            // is the first moment the world rectangle is the one a frame will
            // actually be drawn with.
            ApplyAutomaticCameraZoom();
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
    ///
    /// Hit-stop rides on this and on nothing else. On a tick a blow landed on, the
    /// drawing holds at the position the tick started from for the first share of
    /// it and then catches up — the tick itself is already over, and the remapping
    /// can only lower the alpha, so no body is ever drawn ahead of the simulation.
    /// The curve is <see cref="BlowEffects.HitStopAlpha"/>.
    /// </summary>
    private float MotionAlpha() =>
        (float)BlowEffects.HitStopAlpha(TickAlpha(), _blows.Landed);

    /// <summary>
    /// The share of the tick being drawn, before hit-stop has had its say.
    ///
    /// <para>
    /// <b>Why the strike chain reads this and not <see cref="MotionAlpha"/>.</b>
    /// Hit-stop maps the whole of the first <c>HitStopShare</c> of a blow's tick
    /// onto zero — that is what "the picture holds still" means — so a chain
    /// driven by it would spend its entire wind-up frozen in the stance and then
    /// jump. The two alphas answer different questions: <em>how far has the body
    /// travelled between two cells</em>, which must hold, and <em>how far through
    /// the blow are we</em>, which must not.
    /// </para>
    ///
    /// <para>
    /// <see cref="_strikeScrub"/> replaces it outright when a paused run is being
    /// stepped through a blow frame by frame. That is presentation twice over: it
    /// picks a moment between two canonical snapshots and runs no tick, so the
    /// checksum of a scrubbed run is the checksum of the same run untouched.
    /// </para>
    /// </summary>
    private float TickAlpha() =>
        _strikeScrub is { } scrub
            ? (float)scrub
            : !_interpolatesMotion || _paused
                ? 1f
                : (float)Math.Clamp(_tickAccumulator, 0.0, 1.0);

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
        // Replayed to the same **tick** rather than for the same number of steps:
        // a step stopped being a tick when the party learned to stand still
        // between two waves (Issue #312).
        var replayTarget = _state!.Tick;
        while (!replay.IsComplete && replay.CurrentTick < replayTarget)
        {
            replay.Step();
        }

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
            // One twelfth of the blow being drawn, without running a tick: the
            // frame-by-frame half of ADR 0020's duel scene.
            case Key.F:
                StepStrikeFrame();
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
            // The moment of truth (Issue #312). The creature is the one already
            // selected — the player clicks the name on the card — so the two keys
            // carry only the sign of the judgement and never a target, a place or
            // a moment. A verdict outside the window, or about somebody the domain
            // said nothing about, is refused by the simulation and reported on the
            // feedback line like any other rejected command.
            case Key.G:
                IssueVerdict(VerdictKind.Reward);
                break;
            case Key.H:
                IssueVerdict(VerdictKind.Punish);
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
}
