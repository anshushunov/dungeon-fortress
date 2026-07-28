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
    private static readonly Vector2 MapOrigin = new(18, 118);
    private static readonly Color[] CreatureColors =
    [
        new("#fb7185"), new("#f59e0b"), new("#eab308"),
        new("#84cc16"), new("#22c55e"), new("#14b8a6"),
        new("#38bdf8"), new("#818cf8"), new("#c084fc"),
    ];

    private readonly List<RuntimeDiagnostic> _diagnostics = [];
    private readonly Dictionary<int, Label> _nameLabels = [];
    // The rectangle a label was authored with. Godot re-expands an unclipped
    // Label to its content on the first layout pass, so the live Size stops
    // being the layout the HUD was designed around; the guard needs the original.
    private readonly Dictionary<Label, Vector2> _authoredLabelSize = [];
    private readonly Dictionary<string, Texture2D> _goblinSprites = [];
    private readonly List<string> _loadedSpriteStates = [];
    private readonly List<string> _missingSpriteStates = [];
    private PrototypeWorld? _world;
    private PrototypeSnapshot? _state;
    private Label? _summary;
    private Label? _inspector;
    private Label? _feedback;
    private Label? _roster;
    private PrototypeCommandLog? _fixtureLog;
    private readonly List<PrototypeCommand> _playerCommands = [];
    private ZoneKind _brushZone = ZoneKind.Farm;
    private EditMode _editMode = EditMode.Inspect;
    private JobKind _selectedJob = JobKind.Harvest;
    private int _selectedRule;
    private bool _editingPriorities = true;
    private string _controlFeedback =
        "PAINT/ERASE [B/E] shape zones; DIG/CANCEL DIG [D/X] mark rock for excavation.";
    private string _fixture = "baseline";
    private string? _screenshotPath;
    private int? _selectedCreatureId;
    private GridPoint? _selectedCell;
    private int? _hoverCreatureId;
    private GridPoint? _hoverCell;
    private GridPoint? _lastBrushCell;
    private bool _brushPointerDown;
    private bool _paused = true;
    private bool _visibleSmoke;
    private double _visibleSmokeElapsed;
    private double _speed = 1.0;
    private double _tickAccumulator;
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
            // Holds the HUD to "every line fits", ignoring the deficit Issue #36
            // still owns. verify.ps1 runs it and requires it to fail: that is what
            // proves the guard reacts at all instead of passing on everything.
            var strictHudFit = arguments.Contains("--strict-hud-fit", StringComparer.Ordinal);
            var requiresSprites = !headlessSmoke && !controlsSmoke;

            CreateHud();
            LoadGoblinSprites();
            if (requiresSprites)
            {
                AssertRequiredSpritesLoaded();
            }
            LoadFixture(
                fixture,
                demoControls || demoDig || demoStone || controlsSmoke || _screenshotPath is null
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
        if (_screenshotPath is not null)
        {
            if (_screenshotFramesRemaining-- > 0)
            {
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

        if (!_paused)
        {
            _tickAccumulator += delta * TicksPerSecond * _speed;
            var steps = Math.Min(24, (int)_tickAccumulator);
            if (steps > 0)
            {
                _tickAccumulator -= steps;
                Advance(steps);
            }
        }
    }

    public override void _Input(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseMotion motion:
                UpdatePointer(motion.Position);
                if (_brushPointerDown && _editMode != EditMode.Inspect)
                {
                    ApplyBrushAt(motion.Position);
                }
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
                CancelBrush("right-click");
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                _brushPointerDown = false;
                _lastBrushCell = null;
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click:
                UpdatePointer(click.Position);
                if (TryHandleToolbarClick(click.Position))
                {
                    return;
                }

                if (_editMode == EditMode.Inspect)
                {
                    SelectAt(click.Position);
                    return;
                }

                _brushPointerDown = true;
                _lastBrushCell = null;
                ApplyBrushAt(click.Position);
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
                CancelBrush("I");
                break;
            case Key.B:
                _editMode = EditMode.Paint;
                RefreshState();
                break;
            case Key.E:
                _editMode = EditMode.Erase;
                RefreshState();
                break;
            case Key.D:
                SelectEditMode(EditMode.Dig);
                break;
            case Key.X:
                SelectEditMode(EditMode.CancelDig);
                break;
            case Key.M:
                SelectStockpileBrush();
                break;
            case Key.Z:
                _brushZone = (ZoneKind)(((int)_brushZone + 1) % Enum.GetValues<ZoneKind>().Length);
                RefreshState();
                break;
            case Key.J:
                _selectedJob = (JobKind)(((int)_selectedJob + 1) % Enum.GetValues<JobKind>().Length);
                _editingPriorities = true;
                RefreshState();
                break;
            case Key.K:
                _selectedRule = (_selectedRule + 1) % RuleIds.Length;
                _editingPriorities = false;
                RefreshState();
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
            case Key.Escape:
                CancelBrush("Esc");
                break;
        }
    }

    public override void _Draw()
    {
        if (_state is null)
        {
            return;
        }

        DrawRect(new Rect2(0, 0, 960, 540), new Color("#07111d"));
        DrawToolbar();
        DrawControlToolbar();
        DrawMap();
        DrawSidePanel();
    }

    private void CreateHud()
    {
        var title = MakeLabel(new Vector2(18, 8), new Vector2(620, 24), 18, new Color("#dbeafe"));
        title.Text = "DUNGEON FORTRESS  //  PROTOTYPE 1 GRAYBOX";

        _summary = MakeLabel(new Vector2(18, 42), new Vector2(620, 45), 13, new Color("#bfdbfe"));
        // Sized and clipped to end above the legend at y=330. The explanations grew
        // with stone logistics, and an unclipped label draws over the legend rather
        // than stopping at its own rectangle.
        _inspector = MakeLabel(new Vector2(664, 92), new Vector2(278, 232), 11, new Color("#e2e8f0"));
        _inspector.ClipText = true;
        // Moved down to clear the two stockpile legend rows added in Issue #26.
        _feedback = MakeLabel(new Vector2(664, 406), new Vector2(278, 122), 12, new Color("#94a3b8"));
        _roster = MakeLabel(new Vector2(18, 474), new Vector2(620, 60), 10, new Color("#cbd5e1"));
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

    private Label MakeLabel(Vector2 position, Vector2 size, int fontSize, Color color)
    {
        var label = new Label
        {
            Position = position,
            Size = size,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        AddChild(label);
        _authoredLabelSize[label] = size;
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
                label = MakeLabel(Vector2.Zero, new Vector2(98, 17), 10, CreatureColors[creature.Id]);
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
            label.Position = CellTopLeft(creature.Position) + new Vector2(2, -14);
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
    /// The four HUD panels the overflow guard and the golden UI state care about.
    /// One list drives both, so a panel cannot be added to one and forgotten in
    /// the other.
    /// </summary>
    private (string Name, Label? Label)[] HudLabels() =>
    [
        ("summary", _summary),
        ("inspector", _inspector),
        ("feedback", _feedback),
        ("roster", _roster),
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
    };

    /// <summary>
    /// How many wrapped lines each HUD panel is currently allowed to lose. Zero is
    /// the target and the only value a new panel may have.
    ///
    /// The non-zero entries are measured, pre-existing debt rather than a decision
    /// taken here: inside the fixed 960x540 frame the side panel is
    /// over-subscribed. At its worst frame the inspector needs 244 px and the
    /// event feedback 197 px, the legend block takes ~75 px, and the column is
    /// 456 px tall — no arrangement of the current four labels fits. Reflowing the
    /// HUD is Issue #36 and ADR 0008 additionally drops the fixed frame, so the
    /// deficit is theirs to clear. Until then the guard still fails the moment
    /// anything gets worse, which is the regression Issue #26 shipped twice.
    /// </summary>
    private static readonly Dictionary<string, int> KnownLineDeficit =
        new(StringComparer.Ordinal)
        {
            ["summary"] = 0,
            ["inspector"] = 1,
            ["feedback"] = 4,
            ["roster"] = 2,
        };

    /// <summary>
    /// A <see cref="Label"/> that does not fit its own rectangle silently loses
    /// text: unclipped it re-expands past the panel below it (the event feedback
    /// currently runs off the bottom of the window that way), clipped it drops the
    /// overflowing lines instead. Both happened in Issue #26 and both were found
    /// by eye on a PNG.
    ///
    /// The check is made against the label's authored rectangle and never against
    /// a window constant: ADR 0008 drops the fixed 960x540 frame, so any assertion
    /// pinned to it would be wrong the moment the camera lands.
    ///
    /// It must run before Godot's first layout pass, because that pass re-expands
    /// an unclipped label to its content and <c>GetVisibleLineCount()</c> then
    /// reports the grown rectangle instead of the designed one. <c>_Ready</c> still
    /// satisfies that in every mode, headless or windowed; a capture in
    /// <c>_Process</c> no longer does. See <see cref="LabelFit"/> for the evidence
    /// a run carries about which of the two rectangles it measured.
    /// </summary>
    /// <param name="strict">
    /// Ignore <see cref="KnownLineDeficit"/> and require every panel to hold all of
    /// its text. Used by <c>--strict-hud-fit</c>, which <c>verify.ps1</c> expects to
    /// fail today; the day it stops failing, Issue #36 has cleared the deficit and
    /// the recorded allowances are due for deletion.
    /// </param>
    private void AssertLabelsFit(bool strict = false)
    {
        foreach (var (name, label) in HudLabels())
        {
            if (label is null)
            {
                continue;
            }

            if (_authoredLabelSize.TryGetValue(label, out var authored) &&
                !label.Size.IsEqualApprox(authored))
            {
                throw new InvalidOperationException(
                    $"HUD label '{name}' has already been resized from {authored} to " +
                    $"{label.Size}, so a fit check here would measure the grown " +
                    "rectangle instead of the designed one. Run the guard before the " +
                    "first layout pass.");
            }

            var needed = label.GetLineCount();
            var shown = label.GetVisibleLineCount();
            var allowed = strict || !KnownLineDeficit.TryGetValue(name, out var known)
                ? 0
                : known;
            if (needed - shown > allowed)
            {
                throw new InvalidOperationException(
                    $"HUD label '{name}' needs {needed} lines but only {shown} fit in " +
                    $"{label.Size}: {needed - shown} lines lost, {allowed} is the " +
                    "deficit recorded for Issue #36. Text that does not fit its own " +
                    "rectangle is drawn over the panel below it.");
            }
        }
    }

    /// <summary>
    /// The measurements <see cref="AssertLabelsFit"/> acts on, published so a run
    /// states what the guard had to work with instead of the guard being trusted.
    /// <c>height</c> differing from <c>authoredHeight</c> is the tell that Godot
    /// has already re-expanded the label and that a fit check at this point would
    /// be vacuous.
    /// </summary>
    private object[] LabelFit() => HudLabels()
        .Where(entry => entry.Label is not null)
        .Select(entry => (object)new
        {
            name = entry.Name,
            neededLines = entry.Label!.GetLineCount(),
            visibleLines = entry.Label.GetVisibleLineCount(),
            hardLines = (entry.Label.Text ?? string.Empty).Split('\n').Length,
            authoredHeight = _authoredLabelSize.TryGetValue(entry.Label, out var authored)
                ? authored.Y
                : (float?)null,
            height = entry.Label.Size.Y,
        })
        .ToArray();

    private void DrawToolbar()
    {
        DrawRect(new Rect2(18, 74, 626, 20), new Color("#0f1d2d"));
        var buttons = new[]
        {
            (_paused ? "RUN [P]" : "PAUSE [P]", 18, 72),
            ("STEP [S]", 96, 70),
            ("0.5x [1]", 168, 72),
            ("1x [2]", 242, 62),
            ("4x [3]", 306, 62),
            ("16x [4]", 370, 66),
            ("BASE [R]", 438, 78),
            ("NEGLECT [N]", 518, 104),
        };
        foreach (var (text, x, width) in buttons)
        {
            var active = text.StartsWith("1x") && _speed == 1 ||
                text.StartsWith("0.5") && _speed == 0.5 ||
                text.StartsWith("4x") && _speed == 4 ||
                text.StartsWith("16x") && _speed == 16 ||
                text.StartsWith("BASE") && _fixture == "baseline" ||
                text.StartsWith("NEGLECT") && _fixture == "neglected";
            DrawRect(new Rect2(x, 74, width - 3, 18), active ? new Color("#1d4ed8") : new Color("#24364b"));
            DrawString(ThemeDB.FallbackFont, new Vector2(x + 4, 88), text, HorizontalAlignment.Left, -1, 10, new Color("#dbeafe"));
        }
    }

    /// <summary>
    /// One description of the control strip drives both drawing and hit testing,
    /// so a visible button and its click zone cannot drift apart.
    /// </summary>
    private (string Label, int X, int Width, bool Active)[] ControlButtons() =>
    [
        ("INSPECT [I]", 18, 56, _editMode == EditMode.Inspect),
        ("PAINT [B]", 74, 46, _editMode == EditMode.Paint),
        ("ERASE [E]", 120, 46, _editMode == EditMode.Erase),
        ("DIG [D]", 166, 40, _editMode == EditMode.Dig),
        ("CANCEL [X]", 206, 56, _editMode == EditMode.CancelDig),
        ("STOCK [M]", 262, 58,
            _editMode == EditMode.Paint && _brushZone == ZoneKind.MaterialStockpile),
        ($"zone {ShortZone(_brushZone)} [Z]", 320, 66, false),
        ($"job {_selectedJob} {_state!.Priorities[_selectedJob]} [J]", 386, 94, false),
        ($"{ShortRuleId(RuleIds[_selectedRule])} {_state.Rules[RuleIds[_selectedRule]]} [K]",
            480, 90, false),
        ("REPLAY [Y]", 570, 62, false),
    ];

    private static string ShortZone(ZoneKind zone) => zone switch
    {
        ZoneKind.Kitchen => "Kitch",
        ZoneKind.Quarters => "Quart",
        ZoneKind.TrainingGround => "Train",
        ZoneKind.Forbidden => "Forbid",
        ZoneKind.MaterialStockpile => "Stock",
        _ => zone.ToString(),
    };

    private static string ShortRuleId(string ruleId) => ruleId switch
    {
        "ration_reserve" => "ration",
        "drill_min_satiety" => "drillSat",
        _ => "muster",
    };

    private void DrawControlToolbar()
    {
        DrawRect(new Rect2(18, 96, 626, 20), new Color("#102338"));
        foreach (var (label, x, width, active) in ControlButtons())
        {
            DrawRect(
                new Rect2(x, 97, width - 3, 18),
                active ? new Color("#b45309") : new Color("#1b2f45"));
            DrawString(
                ThemeDB.FallbackFont,
                new Vector2(x + 3, 110),
                label,
                HorizontalAlignment.Left,
                width - 6,
                9,
                active ? new Color("#fef3c7") : new Color("#bae6fd"));
        }
    }

    private void DrawMap()
    {
        DrawRect(new Rect2(MapOrigin, new Vector2(PrototypeTuning.MapWidth * TileSize, PrototypeTuning.MapHeight * TileSize)), new Color("#111827"));
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

                // While the dig brush is active every legal target is outlined,
                // so the player never has to guess where a stroke would land.
                if (_editMode == EditMode.Dig && _state.Map.DiggableTiles.Contains(cell))
                {
                    DrawRect(rect.Grow(-2), new Color("#fbbf24") with { A = 0.75f }, false, 1.0f);
                }

                // Same affordance for the stockpile brush: every legal target is
                // outlined, so the player never has to guess where a stroke lands.
                if (_editMode == EditMode.Paint &&
                    _brushZone == ZoneKind.MaterialStockpile &&
                    IsStockpileFloor(cell) &&
                    !_state.StockpileCells.Any(item => item.Position == cell))
                {
                    DrawRect(rect.Grow(-2), new Color("#cbd5e1") with { A = 0.55f }, false, 1.0f);
                }

                if (_selectedCell == cell)
                {
                    DrawRect(rect.Grow(-1), new Color("#f8fafc"), false, 2.0f);
                }
            }
        }

        DrawDigDesignations();
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
            var center = CellCenter(creature.Position);
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
            var center = CellCenter(raider.Position);
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
        if (_editMode != EditMode.Inspect && _hoverCell is { } brushCell && IsMapCell(brushCell))
        {
            var preview = new Rect2(CellTopLeft(brushCell), new Vector2(TileSize - 1, TileSize - 1));
            var previewColor = _editMode switch
            {
                EditMode.Dig => _state.Map.DiggableTiles.Contains(brushCell) &&
                    !_state.DigDesignations.Any(item => item.Tile == brushCell)
                        ? new Color("#f59e0b")
                        : new Color("#ef4444"),
                EditMode.CancelDig => _state.DigDesignations.Any(item => item.Tile == brushCell)
                    ? new Color("#38bdf8")
                    : new Color("#ef4444"),
                EditMode.Paint when _brushZone == ZoneKind.MaterialStockpile =>
                    IsStockpileFloor(brushCell) &&
                    !_state.StockpileCells.Any(item => item.Position == brushCell)
                        ? ZoneColor(ZoneKind.MaterialStockpile)
                        : new Color("#ef4444"),
                _ => ZoneColor(_brushZone),
            };
            DrawRect(preview.Grow(-1), previewColor with { A = 0.32f });
            DrawRect(preview.Grow(-1), new Color("#f8fafc"), false, 1.5f);
        }
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

    private void DrawSidePanel()
    {
        DrawRect(new Rect2(654, 74, 290, 456), new Color("#0f1d2d"));
        DrawRect(new Rect2(654, 74, 290, 456), new Color("#334155"), false, 1);
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 88), "STATE / WHY", HorizontalAlignment.Left, -1, 13, new Color("#93c5fd"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 330), "LEGEND", HorizontalAlignment.Left, -1, 9, new Color("#cbd5e1"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 341), "teal crew / red-ring goblin / bar = HP / white X = downed", HorizontalAlignment.Left, -1, 7, new Color("#cbd5e1"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 350), "purple QUARTERS: rest at fatigue 50+", HorizontalAlignment.Left, -1, 7, new Color("#c4b5fd"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 359), "light warm block = diggable rock / dark = map edge", HorizontalAlignment.Left, -1, 7, new Color("#d6d3d1"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 368), "amber X = dig mark / yellow bar = dig progress", HorizontalAlignment.Left, -1, 7, new Color("#fcd34d"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 377), "red X = unreachable / pale tile = new floor / gray dot = loose stone", HorizontalAlignment.Left, -1, 7, new Color("#fca5a5"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 386), "[M] stockpile: cornered square = material cell / grey box on a crew = carried stone", HorizontalAlignment.Left, -1, 7, new Color("#e2e8f0"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 395), "filled pip = stored / hollow blue pip = booked by a carrier on the way", HorizontalAlignment.Left, -1, 7, new Color("#7dd3fc"));
        DrawLine(new Vector2(664, 400), new Vector2(934, 400), new Color("#334155"), 1);
    }

    private bool TryHandleToolbarClick(Vector2 position)
    {
        if (position.Y is >= 96 and <= 116)
        {
            var buttons = ControlButtons();
            for (var index = 0; index < buttons.Length; index++)
            {
                var (_, x, width, _) = buttons[index];
                if (position.X < x || position.X >= x + width)
                {
                    continue;
                }

                switch (index)
                {
                    case 0: SelectEditMode(EditMode.Inspect); return true;
                    case 1: SelectEditMode(EditMode.Paint); return true;
                    case 2: SelectEditMode(EditMode.Erase); return true;
                    case 3: SelectEditMode(EditMode.Dig); return true;
                    case 4: SelectEditMode(EditMode.CancelDig); return true;
                    case 5: SelectStockpileBrush(); return true;
                    case 6:
                        _brushZone = (ZoneKind)(((int)_brushZone + 1) % Enum.GetValues<ZoneKind>().Length);
                        break;
                    case 7:
                        _selectedJob = (JobKind)(((int)_selectedJob + 1) % Enum.GetValues<JobKind>().Length);
                        _editingPriorities = true;
                        break;
                    case 8:
                        _selectedRule = (_selectedRule + 1) % RuleIds.Length;
                        _editingPriorities = false;
                        break;
                    default:
                        ReplayCurrentLog();
                        break;
                }

                RefreshState();
                return true;
            }

            return true;
        }

        if (position.Y is < 74 or > 94)
        {
            return false;
        }

        if (position.X is >= 18 and < 93) TogglePause();
        else if (position.X is >= 96 and < 165) Advance(1);
        else if (position.X is >= 168 and < 239) SetSpeed(0.5);
        else if (position.X is >= 242 and < 303) SetSpeed(1.0);
        else if (position.X is >= 306 and < 367) SetSpeed(4.0);
        else if (position.X is >= 370 and < 435) SetSpeed(16.0);
        else if (position.X is >= 438 and < 515) LoadFixture("baseline", 1);
        else if (position.X is >= 518 and < 644) LoadFixture("neglected", 1);
        else return false;
        return true;
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

    private void ApplyBrushAt(Vector2 position)
    {
        var cell = ToCell(position);
        if (cell is not { } selected || !IsMapCell(selected))
        {
            return;
        }

        if (_lastBrushCell == selected)
        {
            return;
        }

        _lastBrushCell = selected;

        switch (_editMode)
        {
            case EditMode.Paint:
                ApplyZonePaintBrush(selected);
                break;
            case EditMode.Erase:
                TryApplyPlayerCommand(new ZoneEraseCommand(_state!.Tick, _brushZone, [selected]));
                break;
            case EditMode.Dig:
                ApplyDigBrush(selected);
                break;
            case EditMode.CancelDig:
                ApplyCancelDigBrush(selected);
                break;
        }
    }

    /// <summary>
    /// A stockpile stroke crosses rock, features and the gate on the way to the
    /// floor the player wants, so it is filtered the same way the dig brush is: a
    /// tile the simulation would reject never becomes a command, it becomes an
    /// explanation. Other zones keep their existing, more permissive behaviour.
    /// </summary>
    private void ApplyZonePaintBrush(GridPoint cell)
    {
        if (_brushZone == ZoneKind.MaterialStockpile)
        {
            if (_state!.StockpileCells.Any(item => item.Position == cell))
            {
                _controlFeedback = $"({cell.X},{cell.Y}) is already a material stockpile cell.";
                UpdateHud();
                QueueRedraw();
                return;
            }

            if (!IsStockpileFloor(cell))
            {
                _controlFeedback =
                    $"({cell.X},{cell.Y}) cannot store material: {UnstockpileableReason(cell)}.";
                UpdateHud();
                QueueRedraw();
                return;
            }
        }

        TryApplyPlayerCommand(new ZonePaintCommand(_state!.Tick, _brushZone, [cell]));
    }

    /// <summary>
    /// The simulation publishes which tiles may hold material, exactly as it
    /// publishes which rock may be dug, so the adapter never re-derives the rule.
    /// </summary>
    private bool IsStockpileFloor(GridPoint cell)
    {
        return _state!.Map.StockpileFloorTiles.Contains(cell);
    }

    // Adapter-side alias for the pure explanation. Kept so the brush reads the
    // same as before the seam landed.
    private string UnstockpileableReason(GridPoint cell) =>
        InspectorText.UnstockpileableReason(_state!, cell);

    /// <summary>
    /// A dragged stroke crosses tiles the player never meant to designate, so the
    /// brush only emits a command for a tile the simulation would accept. Refusals
    /// are explained in the feedback line instead of becoming rejected commands.
    /// </summary>
    private void ApplyDigBrush(GridPoint cell)
    {
        if (_state!.DigDesignations.Any(item => item.Tile == cell))
        {
            _controlFeedback = $"({cell.X},{cell.Y}) is already designated for digging.";
            UpdateHud();
            QueueRedraw();
            return;
        }

        if (!_state.Map.DiggableTiles.Contains(cell))
        {
            _controlFeedback =
                $"({cell.X},{cell.Y}) cannot be dug: {InspectorText.UndiggableReason(_state, cell)}.";
            UpdateHud();
            QueueRedraw();
            return;
        }

        TryApplyPlayerCommand(new DigDesignateCommand(_state.Tick, [cell]));
    }

    private void ApplyCancelDigBrush(GridPoint cell)
    {
        if (!_state!.DigDesignations.Any(item => item.Tile == cell))
        {
            _controlFeedback = $"({cell.X},{cell.Y}) carries no dig designation.";
            UpdateHud();
            QueueRedraw();
            return;
        }

        TryApplyPlayerCommand(new DigCancelCommand(_state.Tick, [cell]));
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
        SelectEditMode(EditMode.Paint);
        _controlFeedback =
            "STOCKPILE [M]: painting MaterialStockpile. Click or drag pre-existing floor; " +
            $"each cell holds {PrototypeTuning.StockpileCellCapacity} stone. " +
            "[E] erases and drops stored stone back on the tile. Esc returns to Inspect.";
        UpdateHud();
        QueueRedraw();
    }

    private void SelectEditMode(EditMode mode)
    {
        _editMode = mode;
        _brushPointerDown = false;
        _lastBrushCell = null;
        _controlFeedback = mode switch
        {
            EditMode.Dig =>
                "DIG: click or drag rock to mark it for excavation. A free creature " +
                "chooses the job on its own. Esc/right-click returns to Inspect.",
            EditMode.CancelDig =>
                "CANCEL DIG: click or drag a designation to withdraw it. " +
                "Esc/right-click returns to Inspect.",
            _ => _controlFeedback,
        };
        RefreshState();
    }

    private void CancelBrush(string source)
    {
        _editMode = EditMode.Inspect;
        _brushPointerDown = false;
        _lastBrushCell = null;
        _controlFeedback = $"Inspect mode ({source}); brush cancelled.";
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

    private static readonly string[] RuleIds = ["ration_reserve", "drill_min_satiety", "muster_lead_ticks"];

    private void AdjustSelectedControl(int delta)
    {
        if (_editingPriorities)
        {
            var priorityValue = Math.Clamp(_state!.Priorities[_selectedJob] + delta, PrototypeTuning.PriorityMinimum, PrototypeTuning.PriorityMaximum);
            TryApplyPlayerCommand(new SetPriorityCommand(_state.Tick, _selectedJob, priorityValue));
            return;
        }

        var ruleId = RuleIds[_selectedRule];
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
        _editMode = EditMode.Dig;
        foreach (var tile in new GridPoint[]
                 {
                     new(25, 1), new(25, 2), new(25, 3), new(26, 1), new(26, 3),
                 })
        {
            ApplyDigBrush(tile);
        }

        // A command issued at tick T is applied at the start of tick T, so the
        // designations only become visible to the brush after one step.
        Advance(1);
        _editMode = EditMode.CancelDig;
        ApplyCancelDigBrush(new GridPoint(26, 3));
        // Left holding the dig brush on purpose: the capture then also shows the
        // outline every still-diggable tile gets while the brush is active.
        _editMode = EditMode.Dig;
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
        _editMode = EditMode.Dig;
        foreach (var tile in new GridPoint[] { new(25, 1), new(25, 2), new(25, 3), new(26, 1) })
        {
            ApplyDigBrush(tile);
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

    private void VerifyControlsSmoke()
    {
        // This is an input seam rather than a simulation test: it asserts that a
        // brush stroke accepts multiple cells and that cancelling never leaves
        // the UI in a mouse-capturing edit mode.
        var strokeStart = _playerCommands.Count;
        _editMode = EditMode.Paint;
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
        if (_editMode != EditMode.Inspect || _brushPointerDown)
        {
            throw new InvalidOperationException("Brush smoke did not return to inspect mode.");
        }

        var beforeChecksum = _checksum;
        var beforeCount = _playerCommands.Count;
        TryApplyPlayerCommand(new ZonePaintCommand(_state!.Tick, ZoneKind.Forbidden, [new GridPoint(14, 7)]));
        if (_playerCommands.Count != beforeCount || _checksum != beforeChecksum)
        {
            throw new InvalidOperationException("Invalid indirect command changed the world or log.");
        }

        VerifyDigBrushSmoke();
        VerifyStockpileBrushSmoke();

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
    /// An input-seam check for the excavation brushes: a stroke marks several
    /// tiles, a stroke over floor and over the map boundary changes nothing, the
    /// cancel brush withdraws exactly one mark, and Esc leaves edit mode.
    /// </summary>
    private void VerifyDigBrushSmoke()
    {
        var strokeStart = _playerCommands.Count;
        _editMode = EditMode.Dig;
        foreach (var tile in new GridPoint[] { new(25, 1), new(26, 1), new(25, 2) })
        {
            ApplyDigBrush(tile);
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
        ApplyDigBrush(new GridPoint(12, 12));
        ApplyDigBrush(new GridPoint(0, 0));
        ApplyDigBrush(PrototypeMapGate);
        ApplyDigBrush(new GridPoint(25, 1));
        if (_playerCommands.Count != guardedCount || _checksum != guardedChecksum)
        {
            throw new InvalidOperationException(
                "The dig brush emitted a command for a tile the simulation forbids.");
        }

        _editMode = EditMode.CancelDig;
        ApplyCancelDigBrush(new GridPoint(26, 1));
        ApplyCancelDigBrush(new GridPoint(12, 12));
        Advance(1);
        if (_playerCommands.Count != guardedCount + 1 ||
            _state!.DigDesignations.Any(item => item.Tile == new GridPoint(26, 1)) ||
            _state.DigDesignations.Count != 2)
        {
            throw new InvalidOperationException("The cancel-dig brush did not withdraw one mark.");
        }

        CancelBrush("dig smoke");
        if (_editMode != EditMode.Inspect || _brushPointerDown)
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
        if (_editMode != EditMode.Paint || _brushZone != ZoneKind.MaterialStockpile)
        {
            throw new InvalidOperationException("[M] did not select MaterialStockpile and Paint.");
        }

        var guardedChecksum = _checksum;
        var guardedCount = _playerCommands.Count;
        ApplyZonePaintBrush(new GridPoint(9, 4));   // internal rock
        ApplyZonePaintBrush(new GridPoint(0, 0));   // map boundary
        ApplyZonePaintBrush(PrototypeMapGate);
        ApplyZonePaintBrush(new GridPoint(14, 7));  // larder feature
        ApplyZonePaintBrush(new GridPoint(2, 1));   // mushroom bed
        if (_playerCommands.Count != guardedCount || _checksum != guardedChecksum)
        {
            throw new InvalidOperationException(
                "The stockpile brush emitted a command for a tile the simulation forbids.");
        }

        var stockpile = new GridPoint[] { new(22, 1), new(23, 1) };
        foreach (var tile in stockpile)
        {
            _lastBrushCell = null;
            ApplyZonePaintBrush(tile);
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
        if (_editMode != EditMode.Inspect || _brushPointerDown)
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
                stockpileCapacity = _state.Stocks.StockpileCapacity,
                ui = UiText(),
                labelFit = LabelFit(),
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
            stockpileCapacity = _state?.Stocks.StockpileCapacity,
            ui = UiText(),
            labelFit = LabelFit(),
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

    private enum EditMode { Inspect, Paint, Erase, Dig, CancelDig }

    private sealed record RuntimeDiagnostic(string Scope, string Type, string Message);
}
