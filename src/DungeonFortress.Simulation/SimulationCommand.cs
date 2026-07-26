namespace DungeonFortress.Simulation;

public sealed record SimulationCommand(int Tick, int AgentId, int EnergyDelta);
