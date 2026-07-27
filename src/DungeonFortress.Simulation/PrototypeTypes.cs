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

    // Appended on purpose: zones are serialised in enum order, so a new kind must
    // not shift the established ones. MaterialStockpile stores only Stone in this
    // experiment; it is not the general stockpile design of the game.
    MaterialStockpile,
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
    bool PickedUp,
    // Only a Stone haul fills these: the stockpile cell this job holds space in,
    // and how much of that cell's capacity is booked while the job is alive.
    GridPoint? StoreCell,
    int StoreReserved);

public sealed record PrototypeBedSnapshot(
    GridPoint Position,
    int GrowthProgress,
    bool IsRipe);

public sealed record PrototypeLooseItemSnapshot(
    GridPoint Position,
    ResourceKind Resource,
    int Quantity);

/// <summary>
/// One cell of the <see cref="ZoneKind.MaterialStockpile"/> zone. Stored stone is
/// canonical per-cell state, not a UI counter: <see cref="Stored"/> plus
/// <see cref="IncomingReserved"/> can never exceed <see cref="Capacity"/>, which
/// is what stops two creatures from overfilling the same cell.
/// </summary>
public sealed record PrototypeStockpileCellSnapshot(
    GridPoint Position,
    int Stored,
    int Capacity,
    int IncomingReserved,
    bool Reachable,
    string StatusCode);

/// <summary>
/// The mutable part of the map. Only <see cref="TileKind.Rock"/> can change, and
/// only into <see cref="TileKind.Floor"/>, so the excavated delta plus the fixed
/// initial layout fully determines the terrain.
/// </summary>
public sealed record PrototypeMapSnapshot(
    IReadOnlyList<GridPoint> RockTiles,
    IReadOnlyList<GridPoint> DiggableTiles,
    IReadOnlyList<GridPoint> ExcavatedTiles,
    // Where a MaterialStockpile may be painted right now. Like DiggableTiles it
    // keeps the rule in the simulation, so no adapter re-derives which tiles are
    // plain pre-existing floor rather than a bed, a station, a bunk or the gate.
    IReadOnlyList<GridPoint> StockpileFloorTiles);

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
    [property: JsonPropertyName("stoneProduced")] int StoneProduced,
    [property: JsonPropertyName("stoneHaulsCompleted")] int StoneHaulsCompleted,
    [property: JsonPropertyName("stoneStored")] int StoneStored,
    [property: JsonPropertyName("stoneSpilled")] int StoneSpilled);

public sealed record PrototypeLaborSnapshot(
    [property: JsonPropertyName("totalCreatureTicks")] int TotalCreatureTicks,
    [property: JsonPropertyName("foodWorkTicks")] int FoodWorkTicks,
    [property: JsonPropertyName("restTicks")] int RestTicks,
    [property: JsonPropertyName("eatTicks")] int EatTicks,
    [property: JsonPropertyName("drillTicks")] int DrillTicks,
    [property: JsonPropertyName("watchTicks")] int WatchTicks,
    [property: JsonPropertyName("digTicks")] int DigTicks,
    [property: JsonPropertyName("stoneHaulTicks")] int StoneHaulTicks,
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
    int CarriedStone,
    int StoredStone,
    int ReservedStone,
    int StockpileCapacity,
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
    IReadOnlyList<PrototypeStockpileCellSnapshot> StockpileCells,
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
