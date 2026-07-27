using System.Text.Json.Serialization;

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

    // Appended on purpose: enum order is the deterministic tie-break for job
    // diagnostics, so a new kind must never outrank the established ones.
    Dig,
}

public enum ResourceKind
{
    RawMushroom,
    Meal,

    // Appended on purpose: canonical loose-item ordering sorts by this value.
    Stone,
}

public enum CreatureMode
{
    Waiting,
    Moving,
    Working,
    Eating,
    Resting,
    Mustering,
    Fighting,
    Fled,
    Downed,
}

public enum RaiderMode
{
    Queued,
    Raiding,
    Downed,
    Escaped,
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
    int Repeats,
    JobKind? JobKind,
    GridPoint? Target);

public sealed record PrototypeCreatureSnapshot(
    int Id,
    string Name,
    int Might,
    int Grit,
    IReadOnlyDictionary<JobKind, int> Affinities,
    int Satiety,
    int Fatigue,
    int MartialForm,
    int Hp,
    int MaxHp,
    InjuryKind Injury,
    GridPoint Position,
    CreatureMode Mode,
    long? CurrentJobId,
    ResourceKind? Carrying,
    int CarryAmount,
    bool MealReserved,
    GridPoint? MealTarget,
    int MealTicksRemaining,
    bool IsMustering,
    bool MusterNeedsRation,
    GridPoint? MusterTarget,
    int WorkTicks,
    int WatchTicks,
    int MoveCount,
    int? LastMoveTick,
    int BlockedTicks,
    int YieldCount,
    int? LastYieldTick,
    PrototypeDecision LastDecision,
    int Readiness,
    int? ReadinessAtRaid);

public sealed record PrototypeJobSnapshot(
    long JobId,
    string Key,
    JobKind Kind,
    GridPoint Origin,
    GridPoint Target,
    ResourceKind? Resource,
    int Quantity,
    int? PersonalCreatureId,
    int? ReservedBy,
    int RemainingTicks,
    int ProgressTicks,
    bool PickedUp);

public sealed record PrototypeBedSnapshot(
    GridPoint Position,
    int GrowthProgress,
    bool IsRipe);

public sealed record PrototypeLooseItemSnapshot(
    GridPoint Position,
    ResourceKind Resource,
    int Quantity);

/// <summary>
/// The mutable part of the map. Only <see cref="TileKind.Rock"/> can change, and
/// only into <see cref="TileKind.Floor"/>, so the excavated delta plus the fixed
/// initial layout fully determines the terrain.
/// </summary>
public sealed record PrototypeMapSnapshot(
    IReadOnlyList<GridPoint> RockTiles,
    IReadOnlyList<GridPoint> DiggableTiles,
    IReadOnlyList<GridPoint> ExcavatedTiles);

/// <summary>
/// A player intention to excavate one rock tile. It carries no creature identity:
/// <see cref="ReservedBy"/> is the simulation reporting who volunteered.
/// </summary>
public sealed record PrototypeDigDesignationSnapshot(
    GridPoint Tile,
    long? JobId,
    int? ReservedBy,
    GridPoint? WorkTile,
    int ProgressTicks,
    int RequiredTicks,
    bool Reachable,
    string StatusCode);

public sealed record PrototypePendingCommandSnapshot(
    int Tick,
    string Kind,
    ZoneKind? ZoneKind,
    IReadOnlyList<GridPoint> Tiles,
    JobKind? JobKind,
    string? RuleId,
    int? Value);

public sealed record PrototypeEconomyCountersSnapshot(
    [property: JsonPropertyName("harvestsCompleted")] int HarvestsCompleted,
    [property: JsonPropertyName("rawHaulsCompleted")] int RawHaulsCompleted,
    [property: JsonPropertyName("cookBatchesCompleted")] int CookBatchesCompleted,
    [property: JsonPropertyName("mealHaulsCompleted")] int MealHaulsCompleted,
    [property: JsonPropertyName("mealsProduced")] int MealsProduced,
    [property: JsonPropertyName("mealsEaten")] int MealsEaten,
    [property: JsonPropertyName("digsCompleted")] int DigsCompleted,
    [property: JsonPropertyName("stoneProduced")] int StoneProduced);

public sealed record PrototypeLaborSnapshot(
    [property: JsonPropertyName("totalCreatureTicks")] int TotalCreatureTicks,
    [property: JsonPropertyName("foodWorkTicks")] int FoodWorkTicks,
    [property: JsonPropertyName("restTicks")] int RestTicks,
    [property: JsonPropertyName("eatTicks")] int EatTicks,
    [property: JsonPropertyName("drillTicks")] int DrillTicks,
    [property: JsonPropertyName("watchTicks")] int WatchTicks,
    [property: JsonPropertyName("digTicks")] int DigTicks,
    [property: JsonPropertyName("musterTicks")] int MusterTicks,
    [property: JsonPropertyName("idleTicks")] int IdleTicks,
    [property: JsonPropertyName("foodWorkPercent")] int FoodWorkPercent,
    [property: JsonPropertyName("postOccupiedTicks")] int PostOccupiedTicks,
    [property: JsonPropertyName("postCapacityTicks")] int PostCapacityTicks,
    [property: JsonPropertyName("postOccupancyPercent")] int PostOccupancyPercent);

public sealed record PrototypeStationSnapshot(
    [property: JsonPropertyName("position")] GridPoint Position,
    [property: JsonPropertyName("kind")] TileKind Kind,
    [property: JsonPropertyName("occupiedBy")] int? OccupiedBy,
    [property: JsonPropertyName("occupiedTicks")] int OccupiedTicks);

public sealed record PrototypeStockSnapshot(
    int RawMushroom,
    int Meals,
    int LooseRawMushroom,
    int LooseMeals,
    int LooseStone,
    int Capacity,
    int MealsProduced,
    int MealsEaten);

public sealed record PrototypeThreatSnapshot(
    bool Announced,
    int AnnounceTick,
    int RaidTick,
    int RaiderCount,
    int TicksRemaining);

public sealed record PrototypeRaiderSnapshot(
    int Id,
    int Hp,
    int Might,
    GridPoint Position,
    int CarryingMeals,
    int StealTicks,
    bool ReturningToGate,
    RaiderMode Mode);

public sealed record PrototypeSessionResultSnapshot(
    string? Outcome,
    int? EndTick,
    bool Unresolved,
    int DefendersDowned,
    int DefendersFled,
    int RaidersDowned,
    int MealsStolen,
    int MealsLeft);

public sealed record PrototypeSnapshot(
    int SchemaVersion,
    long NextJobId,
    ulong Seed,
    int Tick,
    int CommandsApplied,
    IReadOnlyList<PrototypePendingCommandSnapshot> PendingCommands,
    IReadOnlyList<PrototypeCreatureSnapshot> Creatures,
    IReadOnlyDictionary<ZoneKind, IReadOnlyList<GridPoint>> Zones,
    IReadOnlyDictionary<JobKind, int> Priorities,
    IReadOnlyDictionary<string, int> Rules,
    PrototypeMapSnapshot Map,
    IReadOnlyList<PrototypeDigDesignationSnapshot> DigDesignations,
    IReadOnlyList<PrototypeBedSnapshot> Beds,
    IReadOnlyList<PrototypeLooseItemSnapshot> LooseItems,
    PrototypeStockSnapshot Stocks,
    IReadOnlyList<PrototypeJobSnapshot> Jobs,
    PrototypeEconomyCountersSnapshot Economy,
    PrototypeLaborSnapshot Labor,
    IReadOnlyList<PrototypeStationSnapshot> Stations,
    IReadOnlyList<PrototypeEvent> Events,
    PrototypeThreatSnapshot Threat,
    IReadOnlyList<PrototypeRaiderSnapshot> Raiders,
    PrototypeSessionResultSnapshot SessionResult);

public sealed record PrototypeRunResult(
    int Tick,
    int CommandsApplied,
    byte[] CanonicalJson,
    byte[] CanonicalEventLog,
    string Checksum,
    PrototypeSnapshot State);
