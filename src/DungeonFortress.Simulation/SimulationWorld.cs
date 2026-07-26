using System.Text.Json;

namespace DungeonFortress.Simulation;

public sealed class SimulationWorld
{
    public const int WorldWidth = 64;
    public const int WorldHeight = 36;
    public const int MaximumEnergy = 100;

    private readonly AgentState[] _agents;
    private readonly SimulationCommand[] _commands;
    private DeterministicRandom _random;
    private int _nextCommandIndex;

    public SimulationWorld(
        SimulationConfig config,
        IEnumerable<SimulationCommand>? commands = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        Config = config;
        _commands = commands?.ToArray() ?? [];
        ValidateCommands(_commands, config.AgentCount);
        _random = new DeterministicRandom(config.Seed);
        _agents = new AgentState[config.AgentCount];

        for (var id = 0; id < _agents.Length; id++)
        {
            _agents[id] = new AgentState
            {
                Id = id,
                X = _random.NextInt32(WorldWidth),
                Y = _random.NextInt32(WorldHeight),
                Energy = 40 + _random.NextInt32(MaximumEnergy - 39),
            };
        }
    }

    public SimulationConfig Config { get; }

    public int CurrentTick { get; private set; }

    public int CommandsApplied { get; private set; }

    public void RunTicks(int tickCount)
    {
        if (tickCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickCount));
        }

        if (tickCount > int.MaxValue - CurrentTick)
        {
            throw new ArgumentOutOfRangeException(nameof(tickCount), "Tick counter would overflow.");
        }

        for (var i = 0; i < tickCount; i++)
        {
            Step();
        }
    }

    public void Step()
    {
        if (CurrentTick == int.MaxValue)
        {
            throw new InvalidOperationException("The simulation tick counter is exhausted.");
        }

        ApplyCommandsForCurrentTick();

        for (var index = 0; index < _agents.Length; index++)
        {
            ref var agent = ref _agents[index];
            var roll = _random.NextUInt64();

            switch (roll & 3UL)
            {
                case 0:
                    agent.X = Wrap(agent.X + 1, WorldWidth);
                    break;
                case 1:
                    agent.X = Wrap(agent.X - 1, WorldWidth);
                    break;
                case 2:
                    agent.Y = Wrap(agent.Y + 1, WorldHeight);
                    break;
                default:
                    agent.Y = Wrap(agent.Y - 1, WorldHeight);
                    break;
            }

            if (agent.Energy == 0)
            {
                agent.Energy = 2 + (int)((roll >> 8) & 3UL);
            }
            else
            {
                agent.Energy--;
                agent.WorkCompleted += 1 + (long)((roll >> 4) & 3UL);
            }

            if (((roll >> 16) & 15UL) == 0)
            {
                agent.Energy = Math.Min(MaximumEnergy, agent.Energy + 2);
            }
        }

        CurrentTick++;
    }

    public IReadOnlyList<AgentSnapshot> GetAgentSnapshots()
    {
        var snapshots = new AgentSnapshot[_agents.Length];
        for (var index = 0; index < _agents.Length; index++)
        {
            ref readonly var agent = ref _agents[index];
            snapshots[index] = new AgentSnapshot(
                agent.Id,
                agent.X,
                agent.Y,
                agent.Energy,
                agent.WorkCompleted);
        }

        return snapshots;
    }

    internal void WriteAgents(Utf8JsonWriter writer)
    {
        foreach (ref readonly var agent in _agents.AsSpan())
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", agent.Id);
            writer.WriteNumber("x", agent.X);
            writer.WriteNumber("y", agent.Y);
            writer.WriteNumber("energy", agent.Energy);
            writer.WriteNumber("workCompleted", agent.WorkCompleted);
            writer.WriteEndObject();
        }
    }

    private static int Wrap(int value, int exclusiveMaximum)
    {
        if (value < 0)
        {
            return exclusiveMaximum - 1;
        }

        return value == exclusiveMaximum ? 0 : value;
    }

    private static void ValidateCommands(
        IReadOnlyList<SimulationCommand> commands,
        int agentCount)
    {
        var previousTick = -1;
        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index]
                ?? throw new ArgumentException($"Command {index} is null.", nameof(commands));

            if (command.Tick < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commands),
                    command.Tick,
                    $"Command {index} tick cannot be negative.");
            }

            if (command.Tick < previousTick)
            {
                throw new ArgumentException(
                    "Commands must be ordered by non-decreasing tick.",
                    nameof(commands));
            }

            if (command.AgentId < 0 || command.AgentId >= agentCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commands),
                    command.AgentId,
                    $"Command {index} targets an unknown agent.");
            }

            if (command.EnergyDelta is < -1_000 or > 1_000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commands),
                    command.EnergyDelta,
                    $"Command {index} energy delta must be between -1000 and 1000.");
            }

            previousTick = command.Tick;
        }
    }

    private void ApplyCommandsForCurrentTick()
    {
        while (_nextCommandIndex < _commands.Length
               && _commands[_nextCommandIndex].Tick == CurrentTick)
        {
            var command = _commands[_nextCommandIndex];
            ref var agent = ref _agents[command.AgentId];
            agent.Energy = (int)Math.Clamp(
                (long)agent.Energy + command.EnergyDelta,
                0,
                MaximumEnergy);
            _nextCommandIndex++;
            CommandsApplied++;
        }
    }

    private struct AgentState
    {
        public int Id;
        public int X;
        public int Y;
        public int Energy;
        public long WorkCompleted;
    }
}
