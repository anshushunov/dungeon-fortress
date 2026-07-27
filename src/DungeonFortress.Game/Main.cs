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
    private static readonly Vector2 MapOrigin = new(18, 98);
    private static readonly Color[] CreatureColors =
    [
        new("#fb7185"), new("#f59e0b"), new("#eab308"),
        new("#84cc16"), new("#22c55e"), new("#14b8a6"),
        new("#38bdf8"), new("#818cf8"), new("#c084fc"),
    ];

    private readonly List<RuntimeDiagnostic> _diagnostics = [];
    private readonly Dictionary<int, Label> _nameLabels = [];
    private PrototypeWorld? _world;
    private PrototypeSnapshot? _state;
    private Label? _summary;
    private Label? _inspector;
    private Label? _feedback;
    private Label? _roster;
    private string _fixture = "baseline";
    private string? _screenshotPath;
    private int? _selectedCreatureId;
    private GridPoint? _selectedCell;
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

            CreateHud();
            LoadFixture(fixture, _screenshotPath is null ? 1 : screenshotTicks);
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
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
        {
            return;
        }

        if (TryHandleToolbarClick(click.Position))
        {
            return;
        }

        var cell = ToCell(click.Position);
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
        QueueRedraw();
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
        _roster = MakeLabel(new Vector2(18, 458), new Vector2(620, 70), 12, new Color("#cbd5e1"));
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
        if (fixture is not ("baseline" or "neglected"))
        {
            throw new ArgumentException("Fixture must be baseline or neglected.", nameof(fixture));
        }

        var world = new PrototypeWorld(PrototypeCommandDocument.Load(FixturePath(fixture)));
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

            label.Text = creature.Name;
            label.Position = CellTopLeft(creature.Position) + new Vector2(8, 13);
        }
    }

    private void UpdateHud()
    {
        var stock = _state!.Stocks;
        _summary!.Text =
            $"fixture={_fixture}   tick={_state.Tick}   {(_paused ? "PAUSED" : $"{_speed:0.#}x")}" +
            $"\nTHREAT {(_state.Threat.Announced ? "ANNOUNCED" : "quiet")}  ·  raw {stock.RawMushroom}+{stock.LooseRawMushroom}" +
            $"  ·  meals {stock.Meals}+{stock.LooseMeals}  ·  jobs {_state.Jobs.Count}  ·  checksum {_checksum[..12]}…";

        _inspector!.Text = BuildInspectorText();
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
    }

    private string BuildInspectorText()
    {
        if (_selectedCreatureId is { } creatureId)
        {
            var creature = _state!.Creatures.Single(item => item.Id == creatureId);
            var job = creature.CurrentJobId is { } jobId
                ? _state.Jobs.SingleOrDefault(item => item.JobId == jobId)
                : null;
            var details = creature.LastDecision.Details.Count == 0
                ? "none"
                : string.Join(", ", creature.LastDecision.Details.Select(pair => $"{pair.Key}={pair.Value}"));
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
            var color = CreatureColors[creature.Id];
            if (creature.Id % 2 == 0)
            {
                DrawCircle(center, 7, color);
            }
            else
            {
                DrawRect(new Rect2(center - new Vector2(6, 6), new Vector2(12, 12)), color);
            }

            if (_selectedCreatureId == creature.Id)
            {
                DrawArc(center, 10, 0, Mathf.Tau, 16, new Color("#ffffff"), 2);
            }

            if (creature.Carrying is not null)
            {
                DrawCircle(center + new Vector2(6, -6), 2.5f, creature.Carrying == ResourceKind.Meal ? new Color("#fde68a") : new Color("#a3e635"));
            }
        }
    }

    private void DrawSidePanel()
    {
        DrawRect(new Rect2(654, 74, 290, 456), new Color("#0f1d2d"));
        DrawRect(new Rect2(654, 74, 290, 456), new Color("#334155"), false, 1);
        DrawString(ThemeDB.FallbackFont, new Vector2(664, 88), "STATE / WHY", HorizontalAlignment.Left, -1, 13, new Color("#93c5fd"));
        DrawLine(new Vector2(664, 377), new Vector2(934, 377), new Color("#334155"), 1);
    }

    private bool TryHandleToolbarClick(Vector2 position)
    {
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

    private string CreatureName(int id) => _state!.Creatures.SingleOrDefault(creature => creature.Id == id)?.Name ?? $"#{id}";

    private sealed record RuntimeDiagnostic(string Scope, string Type, string Message);
}
