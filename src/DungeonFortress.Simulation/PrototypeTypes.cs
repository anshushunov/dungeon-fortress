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

/// <summary>
/// Where a blow landed. Four parts and not two hundred: section 6.13 of
/// <c>docs/product/PITCH.md</c> names the budget in the same sentence it names
/// the parts — «не двести частей тела как в Dwarf Fortress, а четыре-пять с
/// бюджетом читаемости: голова, торс, рука, нога» — because the point of the
/// mechanic is that the player can tell one creature from another by what
/// happened to it, and a list nobody can hold in their head does the opposite.
///
/// <para>The order is the order the pitch lists them in, and it is the order
/// the canonical document sorts by, so «which part» never depends on the order
/// blows happened to land in.</para>
/// </summary>
public enum BodyPart
{
    Head,
    Torso,
    Arm,
    Leg,
}

/// <summary>
/// The four parts, once, in enum order. Everything that walks the body walks
/// this list, so a fifth part would be added in one place and would appear in
/// the canonical document, the panel and the label without any of them being
/// edited.
/// </summary>
public static class BodyParts
{
    public const int Count = 4;

    public static IReadOnlyList<BodyPart> All { get; } =
        [BodyPart.Head, BodyPart.Torso, BodyPart.Arm, BodyPart.Leg];
}

/// <summary>
/// One injured part of one creature: where, and how badly.
///
/// <para>Only parts that carry something are published. A creature nobody has
/// reached carries an empty list, which is the same shape as
/// <c>injury = none</c> and is what makes the two readings impossible to
/// disagree.</para>
/// </summary>
public sealed record PrototypeInjurySnapshot(BodyPart Part, InjuryKind Severity);

/// <summary>
/// What one wounded creature decided at the roll call, and out of what
/// (Issue #431, <c>docs/design/VERDICT_AND_THE_WOUNDED.md</c> §3.1 and §4).
///
/// <para><b>Why this is a published field of the creature and not a reason code
/// on the journal.</b> The panel builds its «why» line from
/// <c>lastDecision</c>, and <c>UpdateCombatParticipation</c> runs before
/// <c>GenerateJobs</c> and <c>MatchJobs</c> in the tick
/// (<c>PrototypeWorld.Step</c>), so the very next <c>RecordDecision</c> of the
/// same tick overwrites it. A decision to spare oneself would be gone from the
/// panel on the tick it was taken. This field is the decision itself and lives
/// until the next re-check overturns it or the wave it was about is over.</para>
/// </summary>
/// <param name="Code">
/// <c>spared</c> — the creature stayed out of the line and is free to look for a
/// bunk; <c>pressed</c> — it took the field wounded.
/// </param>
/// <param name="Part">
/// The part the sentence names: the worst-hurt one, and the earliest in
/// <see cref="BodyPart"/> order when two are equally hurt, so the wording never
/// depends on the order blows happened to land in.
/// </param>
/// <param name="VerdictDecided">
/// Whether the player's verdict is what settled it, by the causality rule of
/// §3.5: the contest is recomputed without the terms a verdict wrote — the
/// reward's own <c>benefit_rewarded</c> on the sparing side and the whole of
/// <c>fearOfTheDomain</c> on the pressing side — and this is true only when the
/// outcome flips. It is what stops the feed from crediting the player with an
/// outcome that a fed and tended domain would have produced anyway (§3.2).
/// </param>
public sealed record PrototypeWoundIntentSnapshot(
    string Code,
    int Tick,
    int Wave,
    int Spare,
    int Press,
    BodyPart Part,
    InjuryKind Severity,
    bool VerdictDecided);

/// <summary>
/// The closed enumeration of signs of judgement a player may pass on one
/// creature (ADR 0019). It is a list of <b>judgements</b> and not of actions:
/// every value here is walked through the five conditions of admissibility in
/// <c>docs/design/SLICE_03_MOMENT_OF_TRUTH.md</c>, and a value that is not in
/// that walkthrough may not be in this enum.
///
/// <para>Two values and no more. The minimum the pitch's section 6.11 asks for
/// is "наградить, наказать"; the third option it names — "проигнорировать" — is
/// deliberately <b>not</b> a value, because ignoring is the absence of a verdict
/// and a command for it would let the player ignore loudly (ADR 0019).</para>
/// </summary>
public enum VerdictKind
{
    /// <summary>Признан отличившимся. Судится прошлое: карточка уже показана.</summary>
    Reward,

    /// <summary>Признан провинившимся. Тот же разбор, обратный знак.</summary>
    Punish,
}

/// <summary>
/// One named part of one loyalty magnitude: the code that says where it came
/// from and how much of the total it is. Negative amounts are ordinary — fading
/// and discharge are terms of the same ledger, so the breakdown always adds up
/// to the number beside it.
/// </summary>
public sealed record PrototypeLoyaltyTerm(string Code, int Amount);

/// <summary>
/// What one creature is worth to the domain and the domain to it: fear, benefit
/// and grudge, each with the named terms it was built from.
///
/// <see cref="GrudgeReleased"/> says whether the resentment is currently visible
/// in behaviour at all. Section 6.3 of the pitch makes a grudge the delayed price
/// of fear: it accumulates while fear is high and surfaces when fear falls, so
/// "how much" and "is it showing" are two different questions and both are
/// published.
///
/// <see cref="FearOfTheDomain"/> is appended for the same reason every section
/// added since v2 is appended: a new field at the end of the record cannot move
/// the meaning of anything before it. It is the part of <see cref="Fear"/> that
/// is about the player rather than about the fight (Issue #431), and it is
/// carried rather than derived because the fade of <see cref="Fear"/> is a term
/// that does not name which source it took from — see
/// <c>PrototypeWorld.Loyalty.cs</c>, <c>LoyaltyState.DomainFear</c>.
/// </summary>
public sealed record PrototypeLoyaltySnapshot(
    int Fear,
    int Benefit,
    int Grudge,
    IReadOnlyList<PrototypeLoyaltyTerm> FearTerms,
    IReadOnlyList<PrototypeLoyaltyTerm> BenefitTerms,
    IReadOnlyList<PrototypeLoyaltyTerm> GrudgeTerms,
    bool GrudgeReleased,
    int FearOfTheDomain);

/// <summary>
/// One card of the moment of truth: a creature the domain reports on after a
/// wave, its standing, and how much of that standing this wave is responsible
/// for.
///
/// <see cref="Verdict"/> is the answer already given, and <c>null</c> while the
/// card is unanswered. It is canonical state because the runtime authority for
/// "was there a card about this creature, and has it already been answered"
/// lives on the tick of the command (ADR 0019, «Форма команд вердикта»).
/// </summary>
public sealed record PrototypeMomentOfTruthCard(
    int CreatureId,
    string Name,
    PrototypeLoyaltySnapshot Loyalty,
    int FearThisWave,
    int BenefitThisWave,
    int GrudgeThisWave,
    int RaidersDowned,
    string DominantAxis,
    int Notability,
    string? Verdict);

/// <summary>
/// The pause between two waves. While <see cref="Open"/> the party does not
/// advance: <c>tick</c> stands still and <see cref="WaitedSteps"/> counts how
/// long the domain has been waiting for an answer.
/// </summary>
public sealed record PrototypeMomentOfTruthSnapshot(
    bool Open,
    int WaveNumber,
    int OpenedTick,
    int WaitedSteps,
    int WindowSteps,
    IReadOnlyList<PrototypeMomentOfTruthCard> Cards);

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
    IReadOnlyList<PrototypeRememberedPlace> RememberedPlaces,
    // Appended on purpose, like every section added since v2. Fear, benefit and
    // grudge with their named terms: what this creature is worth to the domain,
    // and out of what.
    PrototypeLoyaltySnapshot Loyalty,
    // Which parts of this creature are hurt and how badly, ordered by
    // BodyPart. <see cref="Injury"/> above is the worst entry of this list and
    // is derived from it, so the summary the rest of the simulation reads and
    // the localisation the player reads can never disagree.
    IReadOnlyList<PrototypeInjurySnapshot> Injuries,
    // Steps a hurt leg has taken away over the party, in the same family as
    // MoveCount and BlockedTicks above: nothing in the simulation reads it, and
    // it is what makes the limp measurable without guessing at how much walking
    // a wounded creature happened to have to do.
    int StepsLostToLimp,
    // Combat actions a hurt head has taken away over the party — the stun, in the
    // same family as StepsLostToLimp above and read by nothing but a measurement.
    int ActionsLostToStun,
    // Appended on purpose, like every section added since v2. What this creature
    // decided at the roll call about its own wound, and null for one that is
    // whole or that no wave has asked yet (Issue #431).
    PrototypeWoundIntentSnapshot? WoundIntent = null);

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
/// One object standing inside a room: where it is and what it is. It is read off
/// the live map, so a training post the player built and one the map fixture
/// authored are the same kind of content — the rule of contract 4.3 that nothing
/// distinguishes them holds here too.
/// </summary>
public sealed record PrototypeRoomObjectSnapshot(GridPoint Position, TileKind Kind);

/// <summary>
/// A room, in the sense
/// <see href="../../docs/decisions/0013-what-is-a-room.md">ADR 0013</see> chose:
/// one connected patch of one zone, together with the objects standing in it.
/// The six properties the ADR names are the six fields below.
///
/// <list type="bullet">
/// <item><see cref="Id"/> — идентификатор. Derived from the purpose and the
/// anchor, because a room is derived; see <c>PrototypeRooms.Identify</c>.</item>
/// <item><see cref="Purpose"/> — назначение.</item>
/// <item><see cref="Perimeter"/> — периметр: the painted cells this room consists
/// of, in reading order. The ADR calls it «периметр покрашенных клеток», and this
/// is that patch — the thing the drawn outline goes around. The outline itself is
/// edge geometry and is computed by the presentation layer
/// (<c>DungeonFortress.Presentation.RoomGeometry</c>), for the same reason
/// <c>WallTopology</c> lives there: topology over a set of tiles follows from the
/// published tiles and needs no tick to run (ADR 0011).</item>
/// <item><see cref="Contents"/> — состав объектов.</item>
/// <item><see cref="StatusCode"/> — состояние, as a reason code in the same
/// vocabulary the dig, build and stockpile ladders use.</item>
/// <item><see cref="Complete"/> — признак завершённости: whether the room covers
/// the feature its purpose requires (contract 12.3). A room can be complete and
/// still not working — that is what <see cref="StatusCode"/> is for — but an
/// incomplete one never works.</item>
/// </list>
/// </summary>
public sealed record PrototypeRoomSnapshot(
    string Id,
    ZoneKind Purpose,
    IReadOnlyList<GridPoint> Perimeter,
    IReadOnlyList<PrototypeRoomObjectSnapshot> Contents,
    string StatusCode,
    bool Complete);

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
    int? Value,
    // Appended for the verdict, the first command of the dictionary that names a
    // creature. Null for the eight commands that do not.
    int? CreatureId = null,
    string? Verdict = null);

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
    RaiderMode Mode,
    // Appended for slice 5 (Issue #358). Every raider has one, because a raider
    // the domain never touched still has to be the same raider if it comes back;
    // only the ones who have already been here are named on screen.
    string Name = "",
    // The wave this raider walked out of alive, and null for one arriving for the
    // first time. It is what turns the three fields below from decoration into a
    // claim the snapshot can be asked to prove.
    int? ReturnedFromWave = null,
    // What the previous raid left on this raider, derived from the damage it
    // actually took and never assigned: None for one nobody reached.
    InjuryKind Scar = InjuryKind.None,
    // Where it was hit hardest last time, in the same shape a creature remembers
    // a place in. Null for a raider with no scar — nobody got it, so there is no
    // place to remember.
    PrototypeRememberedPlace? RememberedPlace = null);

/// <summary>
/// One raider who walked out of the domain alive, and what the domain will get
/// back (pitch 6.8, Issue #358).
///
/// <para>This section exists because "there is no wave left to come back to" has
/// to be <b>visible</b> rather than silently dropped: a raider that escaped in the
/// last two waves of the party is a return the domain never has to answer, and a
/// slice that quietly forgot it would be indistinguishable from one whose return
/// rule is broken.</para>
/// </summary>
/// <param name="Status">
/// One of four: <c>awaiting</c> — the return wave has not arrived yet;
/// <c>returned</c> — it walked back in; <c>no_wave_left</c> — the return wave is
/// past the end of the party; <c>no_room_in_wave</c> — the return wave arrived
/// with fewer places in it than there were survivors to fill them, and this one
/// did not get one. The last one is the price of the composition rule: a returning
/// raider takes a place in the wave instead of being added to it.
/// </param>
public sealed record PrototypeSurvivorSnapshot(
    string Name,
    int EscapedWave,
    int EscapedTick,
    int ReturnWave,
    string Status,
    InjuryKind Scar,
    PrototypeRememberedPlace? RememberedPlace,
    int? ReturnedAsRaiderId);

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
    PrototypeSessionResultSnapshot SessionResult,
    // Appended on purpose, like every section added since v2: a new list at the
    // end of the record cannot move the meaning of anything before it. Rooms are
    // derived from Zones and Map on every snapshot (ADR 0013, variant C).
    IReadOnlyList<PrototypeRoomSnapshot> Rooms,
    // Appended for the same reason. The pause between two waves, its cards and
    // the answers already given.
    PrototypeMomentOfTruthSnapshot MomentOfTruth,
    // Appended for the same reason (Issue #358). Everybody who left the domain
    // alive, when they are due back, and what became of that debt.
    IReadOnlyList<PrototypeSurvivorSnapshot> Survivors);

public sealed record PrototypeRunResult(
    int Tick,
    int CommandsApplied,
    byte[] CanonicalJson,
    byte[] CanonicalEventLog,
    string Checksum,
    PrototypeSnapshot State);
