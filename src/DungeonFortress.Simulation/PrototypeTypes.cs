namespace DungeonFortress.Simulation;

public readonly record struct GridPoint(int X, int Y) : IComparable<GridPoint>
{
    public int CompareTo(GridPoint other)
    {
        var y = Y.CompareTo(other.Y);
        return y != 0 ? y : X.CompareTo(other.X);
    }
}

public enum TileKind
{
    Rock,
    Floor,
    Bed,
    Kitchen,
    Larder,
    Bunk,
    Post,
    Gate,
}

public enum ZoneKind
{
    Farm,
    Kitchen,
    Larder,
    Quarters,
    TrainingGround,
    Watch,
    Forbidden,
}

public enum JobKind
{
    Harvest,
    Haul,
    Cook,
    Rest,
    Drill,
    Watch,
}

public enum ResourceKind
{
    RawMushroom,
    Meal,
}

public enum CreatureMode
{
    Waiting,
    Moving,
    Working,
    Eating,
    Resting,
    Mustering,
}

public enum InjuryKind
{
    None,
    Light,
    Heavy,
}

public sealed record PrototypeDecision(
    int Tick,
    string ReasonCode,
    IReadOnlyDictionary<string, int> Details,
    JobKind? JobKind = null,
    GridPoint? Target = null);

public sealed record PrototypeEvent(
    int FirstTick,
    int LastTick,
    int CreatureId,
    string ReasonCode,
    IReadOnlyDictionary<string, int> Details,
    int Repeats);

public sealed record PrototypeCreatureSnapshot(
    int Id,
    string Name,
    int Might,
    int Grit,
    IReadOnlyDictionary<JobKind, int> Affinities,
    int Satiety,
    int Fatigue,
    int MartialForm,
    InjuryKind Injury,
    GridPoint Position,
    CreatureMode Mode,
    long? CurrentJobId,
    PrototypeDecision LastDecision,
    int Readiness,
    int? ReadinessAtRaid);

public sealed record PrototypeJobSnapshot(
    long JobId,
    JobKind Kind,
    GridPoint Target,
    ResourceKind? Resource,
    int? ReservedBy,
    int RemainingTicks);

public sealed record PrototypeStockSnapshot(
    int RawMushroom,
    int Meals,
    int LooseRawMushroom,
    int LooseMeals,
    int Capacity,
    int MealsProduced,
    int MealsEaten);

public sealed record PrototypeThreatSnapshot(
    bool Announced,
    int AnnounceTick,
    int RaidTick,
    int RaiderCount,
    int TicksRemaining);

public sealed record PrototypeSnapshot(
    int SchemaVersion,
    ulong Seed,
    int Tick,
    int CommandsApplied,
    IReadOnlyList<PrototypeCreatureSnapshot> Creatures,
    IReadOnlyDictionary<ZoneKind, IReadOnlyList<GridPoint>> Zones,
    IReadOnlyDictionary<JobKind, int> Priorities,
    IReadOnlyDictionary<string, int> Rules,
    PrototypeStockSnapshot Stocks,
    IReadOnlyList<PrototypeJobSnapshot> Jobs,
    IReadOnlyList<PrototypeEvent> Events,
    PrototypeThreatSnapshot Threat);

public sealed record PrototypeRunResult(
    int Tick,
    int CommandsApplied,
    byte[] CanonicalJson,
    byte[] CanonicalEventLog,
    string Checksum,
    PrototypeSnapshot State);
