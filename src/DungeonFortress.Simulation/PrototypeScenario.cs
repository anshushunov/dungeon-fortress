namespace DungeonFortress.Simulation;

public static class PrototypeScenario
{
    public static PrototypeRunResult Run(PrototypeCommandLog commandLog, int tickCount)
    {
        var world = new PrototypeWorld(commandLog);
        world.RunTicks(tickCount);
        return Capture(world);
    }

    public static PrototypeRunResult Capture(PrototypeWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var state = world.GetSnapshot();
        var canonicalJson = PrototypeCanonical.Serialize(state);
        var canonicalEvents = PrototypeCanonical.SerializeEvents(state);
        return new PrototypeRunResult(
            state.Tick,
            state.CommandsApplied,
            canonicalJson,
            canonicalEvents,
            PrototypeCanonical.ComputeChecksum(canonicalJson),
            state);
    }
}
