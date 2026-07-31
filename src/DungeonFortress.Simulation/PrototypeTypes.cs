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

    // Appended for the same reason. Build is last, so a blueprint never outranks
    // the food chain or excavation on an otherwise equal score.
    Build,
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

/// <summary>
/// A place one creature will not forget, and why.
///
/// This is the first thing in Prototype 1 that makes a creature's past change
/// its future, and it is deliberately the cheapest such thing that could work:
/// a tile, the tick it was written on, and one of two causes — <c>panic</c>
/// when nerve failed there, <c>wound</c> when a raider put the creature down
/// there.
///
/// It is memory of a **place** and not of a creature or of the player, which is
/// what keeps it personal by construction: it is written at the position of the
/// one creature it happened to, so no second creature inherits it. Memory of
/// somebody else would be a relation, which
/// <c>docs/decisions/0006-defer-relations-from-prototype-1.md</c> defers; memory
/// of the player would be a grudge, and the player never addresses anybody by
/// name (ADR 0005), so the blame would land on everyone equally and the herd
/// Issue #101 removed would come back through the other door.
/// </summary>
public sealed record PrototypeRememberedPlace(GridPoint Place, int Tick, string Cause);

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
    int? ReadinessAtRaid,
    // Ticks already spent mending. It is canonical state and not a UI counter,
    // because "this one is halfway healed" is the answer the window between two
    // waves exists to give.
    int RecoveryTicks,
    // Where this creature broke or was put down, newest last, capped at
    // T.memory_places_max and ordered by tile so the canonical document does not
    // depend on the order events happened to arrive in.
    IReadOnlyList<PrototypeRememberedPlace> RememberedPlaces);

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
    // Only a Stone haul fills these: the destination this job holds space in —
    // a stockpile cell or a construction site — and how much of that
    // destination's room is booked while the job is alive.
    GridPoint? StoreCell,
    int StoreReserved,
    // Set only when the load is withdrawn from a stockpile cell instead of from a
    // loose pile. A cell can hold both at once, so the source is stated rather
    // than guessed from the tile.
    GridPoint? SourceCell = null);

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
    IReadOnlyList<GridPoint> StockpileFloorTiles,
    // Where a training-post blueprint may be placed right now. Unlike a stockpile
    // this list includes ground the player created by digging: a functional room
    // out of excavated space is the point of the step that introduced it.
    IReadOnlyList<GridPoint> BuildFloorTiles,
    // Posts that exist because they were built, not because the map fixture
    // authored them. Floor -> Post is the second mutation the map allows, so the
    // delta belongs in canonical state next to ExcavatedTiles.
    IReadOnlyList<GridPoint> BuiltPostTiles);

/// <summary>
/// A player intention to build a training post on one floor tile. It carries no
/// creature identity: <see cref="ReservedBy"/> is the simulation reporting who
/// volunteered, and <see cref="Delivered"/> is stone that physically arrived.
/// </summary>
public sealed record PrototypeBuildSiteSnapshot(
    GridPoint Tile,
    int Delivered,
    int Required,
    int IncomingReserved,
    long? JobId,
    int? ReservedBy,
    int ProgressTicks,
    int RequiredTicks,
    bool Reachable,
    string StatusCode);

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
    [property: JsonPropertyName("stoneSpilled")] int StoneSpilled,
    [property: JsonPropertyName("stoneDelivered")] int StoneDelivered,
    [property: JsonPropertyName("stoneConsumed")] int StoneConsumed,
    [property: JsonPropertyName("buildsCompleted")] int BuildsCompleted);

public sealed record PrototypeLaborSnapshot(
    [property: JsonPropertyName("totalCreatureTicks")] int TotalCreatureTicks,
    [property: JsonPropertyName("foodWorkTicks")] int FoodWorkTicks,
    [property: JsonPropertyName("restTicks")] int RestTicks,
    [property: JsonPropertyName("eatTicks")] int EatTicks,
    [property: JsonPropertyName("drillTicks")] int DrillTicks,
    [property: JsonPropertyName("watchTicks")] int WatchTicks,
    [property: JsonPropertyName("digTicks")] int DigTicks,
    [property: JsonPropertyName("stoneHaulTicks")] int StoneHaulTicks,
    [property: JsonPropertyName("buildTicks")] int BuildTicks,
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
    // Stone that reached a construction site and is waiting to be spent. It is a
    // fourth state of the same material, not a copy of StoredStone.
    int SiteStone,
    int ReservedStone,
    int StockpileCapacity,
    int Capacity,
    int MealsProduced,
    int MealsEaten);

/// <summary>
/// The wave the player is currently dealing with: the one that has arrived and
/// is not over, otherwise the next one that has not arrived yet. When every wave
/// is resolved it stays on the last one, so the panel never reports a wave that
/// will never come.
/// </summary>
public sealed record PrototypeThreatSnapshot(
    bool Announced,
    int WaveNumber,
    int WaveCount,
    int AnnounceTick,
    int ArriveTick,
    int RaiderCount,
    int RaiderMight,
    int TicksRemaining,
    bool Active);

/// <summary>
/// One wave of the session. Its composition is decided at
/// <see cref="AnnounceTick"/> from the renown standing at that moment, which is
/// why it is canonical state and not a function of the wave number.
/// </summary>
public sealed record PrototypeWaveSnapshot(
    int Number,
    int AnnounceTick,
    int ArriveTick,
    bool Announced,
    bool Arrived,
    int RaiderCount,
    int RaiderMight,
    string? Outcome,
    int? EndTick,
    int RaidersDowned,
    int DefendersDowned,
    int DefendersFled,
    int MealsStolen,
    int RenownAtAnnounce);

/// <summary>
/// The two numbers the player reads the session by. <see cref="Renown"/> says
/// how visible the domain is from outside and sets the strength of the next
/// wave; <see cref="Strength"/> says how ready it is and influences nothing. The
/// meaning lives in the gap between them, and the player is the one who reads
/// it — nothing here interprets it for them.
///
/// <see cref="Renown"/> is monotone by construction: every term it is built from
/// is a counter that only grows. Losing creatures, stock or buildings can never
/// improve the score, only weaken the answer to the next wave.
/// </summary>
public sealed record PrototypeDomainSnapshot(
    int Renown,
    int Strength,
    // The same two numbers as they stood when the previous wave arrived. The HUD
    // draws the trend arrow from these instead of keeping its own history, so a
    // headless check and the panel can never disagree about the direction.
    int? RenownAtPreviousWave,
    int? StrengthAtPreviousWave,
    int LivingCreatures,
    int DownedCreatures,
    int InjuredCreatures,
    int PeakMeals,
    int WavesArrived,
    int WavesResolved,
    int WaveCount);

public sealed record PrototypeRaiderSnapshot(
    int Id,
    int Wave,
    int Hp,
    int Might,
    GridPoint Position,
    int CarryingMeals,
    int StealTicks,
    bool ReturningToGate,
    RaiderMode Mode);

/// <summary>
/// The end of the party, not the end of a wave. <see cref="Outcome"/> is
/// <c>held</c> when every wave was actually repelled, <c>raided</c> when the
/// domain survived but at least one wave got through, <c>fallen</c> when nobody
/// is left who can work and defend, and <c>null</c> while the party is still
/// being played. <see cref="LastWaveOutcome"/> keeps the four wave outcomes
/// reachable from the same place they always were.
///
/// <see cref="Score"/> is the one field here that appears only once the party
/// has ended: an unfinished party has no score, and saying so with an absent
/// field rather than a zero is what keeps "how am I doing" and "how did I play"
/// two different questions (ADR 0016).
/// </summary>
public sealed record PrototypeSessionResultSnapshot(
    string? Outcome,
    int? EndTick,
    bool Unresolved,
    string? LastWaveOutcome,
    int WavesResolved,
    // Waves actually turned back, as opposed to waves that merely finished. The
    // two stopped being the same number when the end of a party grew its third
    // form, so the one the player is told is canonical rather than re-derived.
    int WavesRepelled,
    int WaveCount,
    int Renown,
    int Strength,
    int DefendersDowned,
    int DefendersFled,
    int RaidersDowned,
    int MealsStolen,
    int MealsLeft,
    // The party score, and <c>null</c> for as long as there is no party to
    // score: while it is still being played, or when the session fuse cut it
    // short without an outcome. See <see cref="PrototypePartyScore"/>.
    int? Score);

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
    IReadOnlyList<PrototypeBuildSiteSnapshot> BuildSites,
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
    IReadOnlyList<PrototypeWaveSnapshot> Waves,
    PrototypeDomainSnapshot Domain,
    IReadOnlyList<PrototypeRaiderSnapshot> Raiders,
    PrototypeSessionResultSnapshot SessionResult);

public sealed record PrototypeRunResult(
    int Tick,
    int CommandsApplied,
    byte[] CanonicalJson,
    byte[] CanonicalEventLog,
    string Checksum,
    PrototypeSnapshot State);
