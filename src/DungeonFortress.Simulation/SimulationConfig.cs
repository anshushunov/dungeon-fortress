namespace DungeonFortress.Simulation;

public sealed record SimulationConfig(ulong Seed, int AgentCount)
{
    public const int MaximumAgentCount = 1_000_000;

    internal void Validate()
    {
        if (AgentCount <= 0 || AgentCount > MaximumAgentCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AgentCount),
                AgentCount,
                $"Agent count must be between 1 and {MaximumAgentCount}.");
        }
    }
}
