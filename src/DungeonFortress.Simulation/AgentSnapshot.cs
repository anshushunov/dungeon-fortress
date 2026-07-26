namespace DungeonFortress.Simulation;

public readonly record struct AgentSnapshot(
    int Id,
    int X,
    int Y,
    int Energy,
    long WorkCompleted);
