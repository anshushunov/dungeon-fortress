using System.Text.Json;

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
    private readonly Dictionary<string, Texture2D> _goblinSprites = [];
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
    private string _controlFeedback = "Select PAINT or ERASE, then click a passable map cell.";
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

    public override void _Ready()
    {
        try
        {
            var arguments = OS.GetCmdlineUserArgs();
            var fixture = ReadArgument(arguments, "--fixture") ?? "baseline";
            var screenshotTicks = ReadIntArgument(arguments, "--screenshot-ticks") ?? 1;
            _screenshotPath = ReadArgument(arguments, "--screenshot");
            _screenshotFramesRemaining = _screenshotPath is null ? 0 : 3;
            var selectCreature = ReadIntArgument(arguments, "--select-creature");
            var headlessSmoke = arguments.Contains("--smoke", StringComparer.Ordinal);
            var visibleSmoke = arguments.Contains("--visible-smoke", StringComparer.Ordinal);
            var controlsSmoke = arguments.Contains("--smoke-controls", StringComparer.Ordinal);
            var demoControls = arguments.Contains("--demo-controls", StringComparer.Ordinal);

            CreateHud();
            LoadGoblinSprites();
            LoadFixture(fixture, demoControls || controlsSmoke || _screenshotPath is null ? 1 : screenshotTicks);
            if (demoControls || controlsSmoke)
            {
                ApplyDemoControls();
                if (_screenshotPath is not null)
                {
                    Advance(Math.Max(0, screenshotTicks - _state!.Tick));
                }
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
        _inspector = MakeLabel(new Vector2(664, 92), new Vector2(278, 278), 14, new Color("#e2e8f0"));
        _feedback = MakeLabel(new Vector2(664, 388), new Vector2(278, 140), 12, new Color("#94a3b8"));
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
            }
        }
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

    private void UpdateHud()
    {
        var stock = _state!.Stocks;
        _summary!.Text =
            $"fixture={_fixture}   tick={_state.Tick}   {(_paused ? "PAUSED" : $"{_speed:0.#}x")}" +
            $"\nTHREAT {(_state.Threat.Announced ? $"RAID IN {_state.Threat.TicksRemaining}" : "quiet")}  ·  raid {(_state.SessionResult.Outcome ?? (_state.Raiders.Count > 0 ? "IN PROGRESS" : "waiting"))}" +
            $"  ·  raw {stock.RawMushroom}+{stock.LooseRawMushroom}" +
            $"  ·  meals {stock.Meals}+{stock.LooseMeals}  ·  jobs {_state.Jobs.Count}  ·  checksum {_checksum[..12]}…";

        _inspector!.Text = BuildInspectorText();
        // Keep the top line deliberately short: this remains legible at both
        // supported capture sizes instead of flowing into the control strip.
        _summary.Text =
            $"{_fixture.ToUpperInvariant()}  •  t{_state.Tick}  •  {(_paused ? "PAUSED" : $"{_speed:0.#}x")}" +
            $"\n{RaidPhase()}  •  food {stock.Meals}+{stock.LooseMeals}  •  raw {stock.RawMushroom}+{stock.LooseRawMushroom}" +
            $"  •  jobs {_state.Jobs.Count}  •  {_checksum[..8]}";
        var eventText = _state.Events.Count == 0
            ? "EVENT FEEDBACK\nNo events yet. Step or unpause to watch autonomous choices."
            : string.Join(
                "\n",
                _state.Events.TakeLast(3).Reverse().Select(@event =>
                    $"t{@event.LastTick} · {CreatureName(@event.CreatureId)}\n{@event.ReasonCode}"));
        _feedback!.Text =
            "EVENT FEEDBACK\n" + eventText +
            $"\n\nDiagnostics: {_diagnostics.Count} (structured JSON is emitted by smoke/capture).";
        _roster!.Text = "CREW · " + string.Join("   ·   ", _state.Creatures.Select(creature => creature.Name));
        _roster.Text += "\nINDIRECT: " + _controlFeedback;
        _roster.Text += "\nLOG " + (_playerCommands.Count == 0 ? "empty" : string.Join(" | ", _playerCommands.TakeLast(2).Select(DescribeCommand)));
        _roster.Text = "CREW  " + string.Join("  •  ", _state.Creatures.Select(creature => $"{creature.Name} {CreatureStateShort(creature)}")) +
            "\n" + _controlFeedback +
            "\nLOG " + (_playerCommands.Count == 0 ? "empty" : string.Join(" | ", _playerCommands.TakeLast(2).Select(DescribeCommand)));
    }

    private string BuildInspectorText()
    {
        if (_selectedCreatureId is { } creatureId)
        {
            var creature = _state!.Creatures.Single(item => item.Id == creatureId);
            creature = creature with
            {
                Name = $"{creature.Name} — {CreatureLifeState(creature)} HP {creature.Hp}/{creature.MaxHp}",
            };
            var job = creature.CurrentJobId is { } jobId
                ? _state.Jobs.SingleOrDefault(item => item.JobId == jobId)
                : null;
            var details = creature.LastDecision.Details.Count == 0
                ? "none"
                : string.Join(", ", creature.LastDecision.Details.Select(pair => $"{pair.Key}={pair.Value}"));
            details = $"STATUS {CreatureLifeState(creature)} • HP {creature.Hp}/{creature.MaxHp}\n" + details;
            return
                $"CREATURE #{creature.Id} · {creature.Name}\n\n" +
                $"satiety {creature.Satiety}   fatigue {creature.Fatigue}\n" +
                $"martial form {creature.MartialForm}   readiness {creature.Readiness}\n" +
                $"mode {creature.Mode}\n" +
                $"job {(job is null ? "none" : $"#{job.JobId} {job.Kind}")}\n" +
                $"carrying {(creature.Carrying is null ? "nothing" : $"{creature.CarryAmount} {creature.Carrying}")}\n\n" +
                $"WHY\nt{creature.LastDecision.Tick} · {creature.LastDecision.ReasonCode}\n" +
                $"{details}";
        }

        if (_selectedCell is { } cell)
        {
            var zones = _state!.Zones
                .Where(pair => pair.Value.Contains(cell))
                .Select(pair => pair.Key.ToString())
                .ToArray();
            if (zones.Contains(nameof(ZoneKind.Quarters), StringComparer.Ordinal))
            {
                zones = zones.Append("QUARTERS: rest only at fatigue 50+, free bunk").ToArray();
            }
            var jobs = _state.Jobs.Where(job => job.Origin == cell || job.Target == cell).ToArray();
            return
                $"CELL ({cell.X}, {cell.Y})\n\n" +
                $"tile {TileDescription(cell)}\n" +
                $"zones {(zones.Length == 0 ? "none" : string.Join(", ", zones))}\n" +
                $"jobs {(jobs.Length == 0 ? "none" : string.Join(", ", jobs.Select(job => $"#{job.JobId} {job.Kind}")))}\n\n" +
                "Click a named creature to inspect its autonomous decision.";
        }

        return
            "INSPECTOR\n\nClick a creature or map cell.\n\n" +
            "The world is a read-only projection of PrototypeWorld; Godot owns only selection, UI tempo and drawing.";
    }

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

    private void DrawControlToolbar()
    {
        DrawRect(new Rect2(18, 96, 626, 20), new Color("#102338"));
        var text = $"{_editMode.ToString().ToUpperInvariant()} [I/B/E]  zone={_brushZone} [Z]  job={_selectedJob} {_state!.Priorities[_selectedJob]} [J +/-]  rule={RuleIds[_selectedRule]}={_state.Rules[RuleIds[_selectedRule]]} [K +/-]  replay [Y]";
        DrawString(ThemeDB.FallbackFont, new Vector2(22, 110), text, HorizontalAlignment.Left, -1, 10, new Color("#bae6fd"));
        DrawRect(new Rect2(18, 96, 626, 20), new Color("#102338"));
        var mode = _editMode switch
        {
            EditMode.Paint => $"PAINT {_brushZone} — click/drag map • Esc/right-click: Inspect",
            EditMode.Erase => $"ERASE {_brushZone} — click/drag map • Esc/right-click: Inspect",
            _ => "INSPECT — hover for name, click creature/cell • B paint • E erase",
        };
        DrawString(ThemeDB.FallbackFont, new Vector2(22, 110), mode, HorizontalAlignment.Left, -1, 10, _editMode == EditMode.Inspect ? new Color("#bae6fd") : new Color("#fef08a"));
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
                DrawRect(rect, BaseTileColor(cell));
                foreach (var zone in _state!.Zones.Where(pair => pair.Value.Contains(cell)).Select(pair => pair.Key))
                {
                    DrawRect(rect.Grow(-3), ZoneColor(zone), false, 1.5f);
                }

                if (_selectedCell == cell)
                {
                    DrawRect(rect.Grow(-1), new Color("#f8fafc"), false, 2.0f);
                }
            }
        }

        foreach (var bed in _state!.Beds)
        {
            DrawCircle(CellCenter(bed.Position), 5, bed.IsRipe ? new Color("#bef264") : new Color("#4d7c0f"));
        }

        foreach (var loose in _state.LooseItems)
        {
            var color = loose.Resource == ResourceKind.Meal ? new Color("#fde68a") : new Color("#a3e635");
            DrawCircle(CellCenter(loose.Position), 3 + Math.Min(3, loose.Quantity), color);
        }

        foreach (var job in _state.Jobs)
        {
            var color = JobColor(job.Kind);
            DrawLine(CellCenter(job.Origin), CellCenter(job.Target), color with { A = 0.35f }, 1.0f);
            DrawCircle(CellCenter(job.Target), 3.2f, color);
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

            if (creature.Carrying is not null)
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
            DrawRect(preview.Grow(-1), ZoneColor(_brushZone) with { A = 0.32f });
            DrawRect(preview.Grow(-1), new Color("#f8fafc"), false, 1.5f);
        }
    }

    private void DrawSidePanel()
    {
        DrawRect(new Rect2(654, 74, 290, 456), new Color("#0f1d2d"));
        DrawRect(new Rect2(654, 74, 290, 456), new Color("#334155"), false, 1);
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 88), "STATE / WHY", HorizontalAlignment.Left, -1, 13, new Color("#93c5fd"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 334), "BATTLE", HorizontalAlignment.Left, -1, 9, new Color("#cbd5e1"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 345), "teal crew  /  red-ring goblin", HorizontalAlignment.Left, -1, 8, new Color("#cbd5e1"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 356), "bar = HP  /  white X = downed", HorizontalAlignment.Left, -1, 8, new Color("#cbd5e1"));
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 367), "purple QUARTERS: rest at fatigue 50+", HorizontalAlignment.Left, -1, 7, new Color("#c4b5fd"));
        DrawLine(new Vector2(664, 377), new Vector2(934, 377), new Color("#334155"), 1);
    }

    private bool TryHandleToolbarClick(Vector2 position)
    {
        if (position.Y is >= 96 and <= 116)
        {
            if (position.X < 80) _editMode = EditMode.Inspect;
            else if (position.X < 150) _editMode = EditMode.Paint;
            else if (position.X < 220) _editMode = EditMode.Erase;
            else if (position.X < 360) _brushZone = (ZoneKind)(((int)_brushZone + 1) % Enum.GetValues<ZoneKind>().Length);
            else if (position.X < 480) { _selectedJob = (JobKind)(((int)_selectedJob + 1) % Enum.GetValues<JobKind>().Length); _editingPriorities = true; }
            else if (position.X < 560) { _selectedRule = (_selectedRule + 1) % RuleIds.Length; _editingPriorities = false; }
            else ReplayCurrentLog();
            RefreshState();
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

        TryApplyPlayerCommand(_editMode == EditMode.Paint
            ? new ZonePaintCommand(_state!.Tick, _brushZone, [selected])
            : new ZoneEraseCommand(_state!.Tick, _brushZone, [selected]));
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
            _controlFeedback = $"accepted {DescribeCommand(command)}; activates on next tick";
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

    private static string DescribeCommand(PrototypeCommand command) => command switch
    {
        ZonePaintCommand paint => $"t{paint.Tick} paint {paint.ZoneKind} ({paint.Tiles.Count})",
        ZoneEraseCommand erase => $"t{erase.Tick} erase {erase.ZoneKind} ({erase.Tiles.Count})",
        SetPriorityCommand priority => $"t{priority.Tick} priority {priority.JobKind}={priority.Value}",
        SetRuleCommand rule => $"t{rule.Tick} rule {rule.RuleId}={rule.Value}",
        _ => command.GetType().Name,
    };

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

    private static string? ReadArgument(IReadOnlyList<string> arguments, string name)
    {
        var index = -1;
        for (var candidate = 0; candidate < arguments.Count; candidate++)
        {
            if (string.Equals(arguments[candidate], name, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        if (index == -1)
        {
            return null;
        }

        if (index + 1 >= arguments.Count)
        {
            throw new ArgumentException($"Missing value after {name}.");
        }

        return arguments[index + 1];
    }

    private static int? ReadIntArgument(IReadOnlyList<string> arguments, string name)
    {
        var value = ReadArgument(arguments, name);
        return value is null ? null : int.Parse(value);
    }

    private static bool IsMapCell(GridPoint cell) =>
        cell.X is >= 0 and < PrototypeTuning.MapWidth && cell.Y is >= 0 and < PrototypeTuning.MapHeight;

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
        if (cell.X is 0 or PrototypeTuning.MapWidth - 1 || cell.Y is 0 or PrototypeTuning.MapHeight - 1 ||
            cell is { X: 9, Y: 4 or 5 } or { X: 18, Y: 4 or 5 } or { X: 9 or 18, Y: 10 })
        {
            return new Color("#111827");
        }

        if (_state!.Beds.Any(bed => bed.Position == cell)) return new Color("#31572c");
        if (_state.Stations.Any(station => station.Position == cell && station.Kind == TileKind.Kitchen)) return new Color("#7c4a22");
        if (_state.Zones[ZoneKind.Larder].Contains(cell)) return new Color("#5b3a32");
        if (cell is { X: 20 or 21, Y: 3 } or { X: 21 or 22, Y: 4 }) return new Color("#3b4252");
        if (cell == new GridPoint(27, 13)) return new Color("#854d0e");
        return new Color("#243244");
    }

    private string TileDescription(GridPoint cell)
    {
        if (_state!.Beds.Any(bed => bed.Position == cell)) return "mushroom bed";
        if (_state.Stations.Any(station => station.Position == cell)) return _state.Stations.Single(station => station.Position == cell).Kind.ToString();
        if (cell == new GridPoint(27, 13)) return "gate";
        return "floor / rock projection";
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
        _ => new Color("#ffffff"),
    };

    private static Color JobColor(JobKind job) => job switch
    {
        JobKind.Harvest => new Color("#a3e635"),
        JobKind.Haul => new Color("#facc15"),
        JobKind.Cook => new Color("#fb923c"),
        JobKind.Rest => new Color("#a78bfa"),
        JobKind.Drill => new Color("#22d3ee"),
        JobKind.Watch => new Color("#f472b6"),
        _ => new Color("#ffffff"),
    };

    private string RaidPhase()
    {
        if (_state!.SessionResult.Outcome is { } outcome)
        {
            return $"RAID RESULT: {outcome}";
        }

        if (_state.Raiders.Count > 0)
        {
            return "RAID ACTIVE: teal crew vs red-ring goblins";
        }

        return _state.Threat.Announced
            ? $"RAID WARNING: {_state.Threat.TicksRemaining} ticks"
            : "RAID QUIET: warning begins at t300";
    }

    private string RaidLegend() =>
        "BATTLE LEGEND\n" +
        "teal = crew  •  red ring = raider\n" +
        "bar = HP  •  white X = DOWNED\n" +
        "dot: green work, amber combat,\n" +
        "gray downed, pink fled";

    private static string CreatureLifeState(PrototypeCreatureSnapshot creature) => creature.Mode switch
    {
        CreatureMode.Downed => "DOWNED",
        CreatureMode.Fled => "FLED",
        CreatureMode.Fighting => "ALIVE / FIGHTING",
        _ => "ALIVE",
    };

    private static string CreatureStateShort(PrototypeCreatureSnapshot creature) => creature.Mode switch
    {
        CreatureMode.Downed => "DOWN",
        CreatureMode.Fled => "FLED",
        CreatureMode.Fighting => "FIGHT",
        CreatureMode.Working => "WORK",
        CreatureMode.Moving => "MOVE",
        _ => "READY",
    };

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

    private string CreatureName(int id) => _state!.Creatures.SingleOrDefault(creature => creature.Id == id)?.Name ?? $"#{id}";

    private enum EditMode { Inspect, Paint, Erase }

    private sealed record RuntimeDiagnostic(string Scope, string Type, string Message);
}
