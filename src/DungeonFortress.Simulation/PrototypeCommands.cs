namespace DungeonFortress.Simulation;

public abstract record PrototypeCommand(int Tick);

public sealed record ZonePaintCommand(
    int Tick,
    ZoneKind ZoneKind,
    IReadOnlyList<GridPoint> Tiles) : PrototypeCommand(Tick);

public sealed record ZoneEraseCommand(
    int Tick,
    ZoneKind ZoneKind,
    IReadOnlyList<GridPoint> Tiles) : PrototypeCommand(Tick);

public sealed record SetPriorityCommand(
    int Tick,
    JobKind JobKind,
    int Value) : PrototypeCommand(Tick);

public sealed record SetRuleCommand(
    int Tick,
    string RuleId,
    int Value) : PrototypeCommand(Tick);

public sealed record PrototypeCommandLog(
    string Scenario,
    ulong Seed,
    IReadOnlyList<PrototypeCommand> Commands);
