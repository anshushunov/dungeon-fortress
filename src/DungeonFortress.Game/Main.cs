using System.Text.Json;

using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

public partial class Main : Node2D
{
    private const ulong DefaultSeed = 424_242UL;
    private const int AgentCount = 48;
    private const int TickCount = 256;

    private IReadOnlyList<AgentSnapshot> _agents = [];
    private ScenarioResult? _result;
    private ulong _seed = DefaultSeed;
    private bool _visibleSmoke;
    private double _visibleSmokeElapsed;

    public override void _Ready()
    {
        try
        {
            var arguments = OS.GetCmdlineUserArgs();
            var headlessSmoke = arguments.Contains("--smoke", StringComparer.Ordinal);
            _visibleSmoke = arguments.Contains("--visible-smoke", StringComparer.Ordinal);
            _seed = ReadSeed(arguments);

            _result = RunScenario(_seed);
            _agents = _result.Agents;

            if (headlessSmoke || _visibleSmoke)
            {
                var repeated = RunScenario(_seed);
                if (!_result.CanonicalJson.AsSpan().SequenceEqual(repeated.CanonicalJson))
                {
                    throw new InvalidOperationException(
                        "Two independent Godot-hosted runs produced different snapshots.");
                }
            }

            if (headlessSmoke)
            {
                PrintSmokeResult("godot_headless_smoke", "ok", _seed, null);
                GetTree().Quit(0);
                return;
            }

            AddSummaryLabel(_seed);
            QueueRedraw();
        }
        catch (Exception exception)
        {
            PrintSmokeResult(
                _visibleSmoke ? "godot_visible_smoke" : "godot_headless_smoke",
                "error",
                DefaultSeed,
                exception);
            GD.PushError(exception.ToString());
            GetTree().Quit(1);
        }
    }

    public override void _Process(double delta)
    {
        if (!_visibleSmoke)
        {
            return;
        }

        _visibleSmokeElapsed += delta;
        if (_visibleSmokeElapsed < 0.75)
        {
            return;
        }

        PrintSmokeResult("godot_visible_smoke", "ok", _seed, null);
        GetTree().Quit(0);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(0, 0, 960, 540), new Color("#09101a"));
        DrawRect(new Rect2(28, 82, 904, 430), new Color("#111f2e"));

        foreach (var agent in _agents)
        {
            var position = new Vector2(
                42.0f + (agent.X * 13.7f),
                94.0f + (agent.Y * 11.4f));
            var energyRatio = agent.Energy / (float)SimulationWorld.MaximumEnergy;
            var color = new Color(
                0.25f + (0.45f * energyRatio),
                0.45f + (0.4f * energyRatio),
                0.72f,
                1.0f);
            DrawCircle(position, 3.5f, color);
        }
    }

    private static ScenarioResult RunScenario(ulong seed)
    {
        SimulationCommand[] commands =
        [
            new(0, 0, 20),
            new(16, 3, -12),
            new(16, 3, 4),
            new(64, 7, 100),
            new(128, 1, -100),
        ];

        return SimulationScenario.Run(
            new SimulationConfig(seed, AgentCount),
            TickCount,
            commands);
    }

    private static ulong ReadSeed(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--seed", StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= arguments.Count)
            {
                throw new ArgumentException("Missing value after --seed.");
            }

            return ulong.Parse(arguments[index + 1]);
        }

        return DefaultSeed;
    }

    private void AddSummaryLabel(ulong seed)
    {
        var label = new Label
        {
            Position = new Vector2(28, 18),
            Text =
                $"Dungeon Fortress simulation projection\n"
                + $"seed={seed}  agents={AgentCount}  ticks={_result!.Tick}  "
                + $"checksum={_result.Checksum[..12]}…",
        };
        label.AddThemeFontSizeOverride("font_size", 18);
        label.AddThemeColorOverride("font_color", new Color("#dbeafe"));
        AddChild(label);
    }

    private void PrintSmokeResult(
        string eventName,
        string status,
        ulong seed,
        Exception? exception)
    {
        GD.Print(JsonSerializer.Serialize(new
        {
            @event = eventName,
            status,
            seed,
            agentCount = AgentCount,
            ticks = _result?.Tick,
            checksum = _result?.Checksum,
            canonicalStateOwner = "DungeonFortress.Simulation",
            errorType = exception?.GetType().Name,
            message = exception?.Message,
        }));
    }
}
