namespace DungeonFortress.Simulation;

public static class SimulationScenario
{
    public static ScenarioResult Run(
        SimulationConfig config,
        int tickCount,
        IEnumerable<SimulationCommand>? commands = null)
    {
        var world = new SimulationWorld(config, commands);
        world.RunTicks(tickCount);
        var canonicalJson = CanonicalSnapshot.Serialize(world);

        return new ScenarioResult(
            world.CurrentTick,
            world.CommandsApplied,
            canonicalJson,
            CanonicalSnapshot.ComputeChecksum(canonicalJson),
            world.GetAgentSnapshots());
    }
}

public sealed record ScenarioResult(
    int Tick,
    int CommandsApplied,
    byte[] CanonicalJson,
    string Checksum,
    IReadOnlyList<AgentSnapshot> Agents);
