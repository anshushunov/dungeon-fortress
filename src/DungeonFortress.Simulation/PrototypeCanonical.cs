using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DungeonFortress.Simulation;

public static class PrototypeCanonical
{
    // 3: the session became a sequence of waves. `threat` names the wave in
    // hand, `waves` carries the whole sequence, `domain` carries renown and
    // domain strength, and `sessionResult` describes the end of the party rather
    // than the end of the single raid.
    public const int SchemaVersion = 3;

    public static byte[] Serialize(PrototypeSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteState(writer, state, includeEvents: true);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] SerializeEvents(PrototypeSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteStartArray("events");
        WriteEvents(writer, state.Events);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static string ComputeChecksum(ReadOnlySpan<byte> canonicalJson)
    {
        return Convert.ToHexString(SHA256.HashData(canonicalJson)).ToLowerInvariant();
    }

    private static void WriteState(
        Utf8JsonWriter writer,
        PrototypeSnapshot state,
        bool includeEvents)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteNumber("nextJobId", state.NextJobId);
        writer.WriteNumber("seed", state.Seed);
        writer.WriteNumber("tick", state.Tick);
        writer.WriteNumber("commandsApplied", state.CommandsApplied);
        writer.WriteStartArray("pendingCommands");
        foreach (var command in state.PendingCommands)
        {
            writer.WriteStartObject();
            writer.WriteNumber("tick", command.Tick);
            writer.WriteString("kind", command.Kind);
            WriteNullableEnum(writer, "zoneKind", command.ZoneKind);
            writer.WriteStartArray("tiles");
            foreach (var tile in command.Tiles.Order())
            {
                WritePointValue(writer, tile);
            }

            writer.WriteEndArray();
            WriteNullableEnum(writer, "jobKind", command.JobKind);
            if (command.RuleId is { } ruleId)
            {
                writer.WriteString("ruleId", ruleId);
            }
            else
            {
                writer.WriteNull("ruleId");
            }

            if (command.Value is { } value)
            {
                writer.WriteNumber("value", value);
            }
            else
            {
                writer.WriteNull("value");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("creatures");
        foreach (var creature in state.Creatures.OrderBy(creature => creature.Id))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", creature.Id);
            writer.WriteString("name", creature.Name);
            writer.WriteNumber("might", creature.Might);
            writer.WriteNumber("grit", creature.Grit);
            writer.WriteStartObject("affinities");
            foreach (var affinity in creature.Affinities.OrderBy(pair => pair.Key))
            {
                writer.WriteNumber(ToJson(affinity.Key), affinity.Value);
            }

            writer.WriteEndObject();
            writer.WriteNumber("satiety", creature.Satiety);
            writer.WriteNumber("fatigue", creature.Fatigue);
            writer.WriteNumber("martialForm", creature.MartialForm);
            writer.WriteNumber("hp", creature.Hp);
            writer.WriteNumber("maxHp", creature.MaxHp);
            writer.WriteString("injury", ToJson(creature.Injury));
            WritePoint(writer, "position", creature.Position);
            writer.WriteString("mode", ToJson(creature.Mode));
            if (creature.CurrentJobId is { } jobId)
            {
                writer.WriteNumber("currentJobId", jobId);
            }
            else
            {
                writer.WriteNull("currentJobId");
            }

            WriteNullableEnum(writer, "carrying", creature.Carrying);
            writer.WriteNumber("carryAmount", creature.CarryAmount);
            writer.WriteBoolean("mealReserved", creature.MealReserved);
            WriteNullablePoint(writer, "mealTarget", creature.MealTarget);
            writer.WriteNumber("mealTicksRemaining", creature.MealTicksRemaining);
            writer.WriteBoolean("isMustering", creature.IsMustering);
            writer.WriteBoolean("musterNeedsRation", creature.MusterNeedsRation);
            WriteNullablePoint(writer, "musterTarget", creature.MusterTarget);
            writer.WriteNumber("workTicks", creature.WorkTicks);
            writer.WriteNumber("watchTicks", creature.WatchTicks);
            writer.WriteNumber("moveCount", creature.MoveCount);
            if (creature.LastMoveTick is { } lastMoveTick)
            {
                writer.WriteNumber("lastMoveTick", lastMoveTick);
            }
            else
            {
                writer.WriteNull("lastMoveTick");
            }

            writer.WriteNumber("blockedTicks", creature.BlockedTicks);
            writer.WriteNumber("yieldCount", creature.YieldCount);
            if (creature.LastYieldTick is { } lastYieldTick)
            {
                writer.WriteNumber("lastYieldTick", lastYieldTick);
            }
            else
            {
                writer.WriteNull("lastYieldTick");
            }

            writer.WriteStartObject("lastDecision");
            writer.WriteNumber("tick", creature.LastDecision.Tick);
            if (creature.LastDecision.JobKind is { } kind)
            {
                writer.WriteString("jobKind", ToJson(kind));
            }
            else
            {
                writer.WriteNull("jobKind");
            }

            writer.WriteString("reasonCode", creature.LastDecision.ReasonCode);
            if (creature.LastDecision.Target is { } target)
            {
                WritePoint(writer, "target", target);
            }
            else
            {
                writer.WriteNull("target");
            }

            WriteDetails(writer, creature.LastDecision.Details);
            writer.WriteEndObject();
            writer.WriteNumber("readiness", creature.Readiness);
            if (creature.ReadinessAtRaid is { } readinessAtRaid)
            {
                writer.WriteNumber("readinessAtRaid", readinessAtRaid);
            }
            else
            {
                writer.WriteNull("readinessAtRaid");
            }

            writer.WriteNumber("recoveryTicks", creature.RecoveryTicks);

            // Memory of place (Issue #117). Additive: a new array on an existing
            // section, no field renamed, removed or re-pointed, so the schema
            // version does not move — see
            // docs/engineering/PROTOTYPE_HEADLESS.md, "Версионирование
            // канонического снапшота". Every frame's checksum does move, because
            // the array is present on every creature from tick 0, and that is
            // what a golden regeneration is for here.
            writer.WriteStartArray("rememberedPlaces");
            foreach (var place in creature.RememberedPlaces.OrderBy(place => place.Place))
            {
                writer.WriteStartObject();
                WritePoint(writer, "place", place.Place);
                writer.WriteNumber("tick", place.Tick);
                writer.WriteString("cause", place.Cause);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartObject("zones");
        foreach (var zone in state.Zones.OrderBy(pair => pair.Key))
        {
            writer.WriteStartArray(ToJson(zone.Key));
            foreach (var tile in zone.Value.Order())
            {
                WritePointValue(writer, tile);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteStartObject("priorities");
        foreach (var priority in state.Priorities.OrderBy(pair => pair.Key))
        {
            writer.WriteNumber(ToJson(priority.Key), priority.Value);
        }

        writer.WriteEndObject();
        writer.WriteStartObject("rules");
        foreach (var rule in state.Rules.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteNumber(rule.Key, rule.Value);
        }

        writer.WriteEndObject();

        // The map is canonical state: only Rock -> Floor can happen, so the
        // excavated delta plus the fixed initial layout reproduces the terrain.
        writer.WriteStartObject("map");
        writer.WriteStartArray("rockTiles");
        foreach (var tile in state.Map.RockTiles.Order())
        {
            WritePointValue(writer, tile);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("diggableTiles");
        foreach (var tile in state.Map.DiggableTiles.Order())
        {
            WritePointValue(writer, tile);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("excavatedTiles");
        foreach (var tile in state.Map.ExcavatedTiles.Order())
        {
            WritePointValue(writer, tile);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("stockpileFloorTiles");
        foreach (var tile in state.Map.StockpileFloorTiles.Order())
        {
            WritePointValue(writer, tile);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("buildFloorTiles");
        foreach (var tile in state.Map.BuildFloorTiles.Order())
        {
            WritePointValue(writer, tile);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("builtPostTiles");
        foreach (var tile in state.Map.BuiltPostTiles.Order())
        {
            WritePointValue(writer, tile);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteStartArray("digDesignations");
        foreach (var designation in state.DigDesignations.OrderBy(item => item.Tile))
        {
            writer.WriteStartObject();
            WritePoint(writer, "tile", designation.Tile);
            if (designation.JobId is { } digJobId)
            {
                writer.WriteNumber("jobId", digJobId);
            }
            else
            {
                writer.WriteNull("jobId");
            }

            if (designation.ReservedBy is { } digReservedBy)
            {
                writer.WriteNumber("reservedBy", digReservedBy);
            }
            else
            {
                writer.WriteNull("reservedBy");
            }

            WriteNullablePoint(writer, "workTile", designation.WorkTile);
            writer.WriteNumber("progressTicks", designation.ProgressTicks);
            writer.WriteNumber("requiredTicks", designation.RequiredTicks);
            writer.WriteBoolean("reachable", designation.Reachable);
            writer.WriteString("statusCode", designation.StatusCode);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // A blueprint and the stone that reached it are canonical state: the same
        // seed and log must reproduce which site holds how much and how far the
        // work on it got.
        writer.WriteStartArray("buildSites");
        foreach (var site in state.BuildSites.OrderBy(item => item.Tile))
        {
            writer.WriteStartObject();
            WritePoint(writer, "tile", site.Tile);
            writer.WriteNumber("delivered", site.Delivered);
            writer.WriteNumber("required", site.Required);
            writer.WriteNumber("incomingReserved", site.IncomingReserved);
            if (site.JobId is { } buildJobId)
            {
                writer.WriteNumber("jobId", buildJobId);
            }
            else
            {
                writer.WriteNull("jobId");
            }

            if (site.ReservedBy is { } buildReservedBy)
            {
                writer.WriteNumber("reservedBy", buildReservedBy);
            }
            else
            {
                writer.WriteNull("reservedBy");
            }

            writer.WriteNumber("progressTicks", site.ProgressTicks);
            writer.WriteNumber("requiredTicks", site.RequiredTicks);
            writer.WriteBoolean("reachable", site.Reachable);
            writer.WriteString("statusCode", site.StatusCode);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("beds");
        foreach (var bed in state.Beds.OrderBy(bed => bed.Position))
        {
            writer.WriteStartObject();
            WritePoint(writer, "position", bed.Position);
            writer.WriteNumber("growthProgress", bed.GrowthProgress);
            writer.WriteBoolean("ripe", bed.IsRipe);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("looseItems");
        foreach (var item in state.LooseItems
                     .OrderBy(item => item.Position)
                     .ThenBy(item => item.Resource))
        {
            writer.WriteStartObject();
            WritePoint(writer, "position", item.Position);
            writer.WriteString("resource", ToJson(item.Resource));
            writer.WriteNumber("quantity", item.Quantity);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // Stored stone is canonical state, not a derived counter: the same seed and
        // log must reproduce which cell holds which block.
        writer.WriteStartArray("materialStockpile");
        foreach (var cell in state.StockpileCells.OrderBy(cell => cell.Position))
        {
            writer.WriteStartObject();
            WritePoint(writer, "position", cell.Position);
            writer.WriteNumber("stored", cell.Stored);
            writer.WriteNumber("capacity", cell.Capacity);
            writer.WriteNumber("incomingReserved", cell.IncomingReserved);
            writer.WriteBoolean("reachable", cell.Reachable);
            writer.WriteString("statusCode", cell.StatusCode);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("stocks");
        writer.WriteNumber("rawMushroom", state.Stocks.RawMushroom);
        writer.WriteNumber("meals", state.Stocks.Meals);
        writer.WriteNumber("looseRawMushroom", state.Stocks.LooseRawMushroom);
        writer.WriteNumber("looseMeals", state.Stocks.LooseMeals);
        writer.WriteNumber("looseStone", state.Stocks.LooseStone);
        writer.WriteNumber("carriedStone", state.Stocks.CarriedStone);
        writer.WriteNumber("storedStone", state.Stocks.StoredStone);
        writer.WriteNumber("siteStone", state.Stocks.SiteStone);
        writer.WriteNumber("reservedStone", state.Stocks.ReservedStone);
        writer.WriteNumber("stockpileCapacity", state.Stocks.StockpileCapacity);
        writer.WriteNumber("capacity", state.Stocks.Capacity);
        writer.WriteNumber("mealsProduced", state.Stocks.MealsProduced);
        writer.WriteNumber("mealsEaten", state.Stocks.MealsEaten);
        writer.WriteEndObject();

        writer.WriteStartArray("jobs");
        foreach (var job in state.Jobs.OrderBy(job => job.JobId))
        {
            writer.WriteStartObject();
            writer.WriteNumber("jobId", job.JobId);
            writer.WriteString("key", job.Key);
            writer.WriteString("kind", ToJson(job.Kind));
            WritePoint(writer, "origin", job.Origin);
            WritePoint(writer, "target", job.Target);
            WriteNullableEnum(writer, "resource", job.Resource);
            writer.WriteNumber("quantity", job.Quantity);
            if (job.PersonalCreatureId is { } personalCreatureId)
            {
                writer.WriteNumber("personalCreatureId", personalCreatureId);
            }
            else
            {
                writer.WriteNull("personalCreatureId");
            }

            if (job.ReservedBy is { } reservedBy)
            {
                writer.WriteNumber("reservedBy", reservedBy);
            }
            else
            {
                writer.WriteNull("reservedBy");
            }

            writer.WriteNumber("remainingTicks", job.RemainingTicks);
            writer.WriteNumber("progressTicks", job.ProgressTicks);
            writer.WriteBoolean("pickedUp", job.PickedUp);
            WriteNullablePoint(writer, "storeCell", job.StoreCell);
            writer.WriteNumber("storeReserved", job.StoreReserved);
            WriteNullablePoint(writer, "sourceCell", job.SourceCell);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("economy");
        writer.WriteNumber("harvestsCompleted", state.Economy.HarvestsCompleted);
        writer.WriteNumber("rawHaulsCompleted", state.Economy.RawHaulsCompleted);
        writer.WriteNumber("cookBatchesCompleted", state.Economy.CookBatchesCompleted);
        writer.WriteNumber("mealHaulsCompleted", state.Economy.MealHaulsCompleted);
        writer.WriteNumber("mealsProduced", state.Economy.MealsProduced);
        writer.WriteNumber("mealsEaten", state.Economy.MealsEaten);
        writer.WriteNumber("digsCompleted", state.Economy.DigsCompleted);
        writer.WriteNumber("stoneProduced", state.Economy.StoneProduced);
        writer.WriteNumber("stoneHaulsCompleted", state.Economy.StoneHaulsCompleted);
        writer.WriteNumber("stoneStored", state.Economy.StoneStored);
        writer.WriteNumber("stoneSpilled", state.Economy.StoneSpilled);
        writer.WriteNumber("stoneDelivered", state.Economy.StoneDelivered);
        writer.WriteNumber("stoneConsumed", state.Economy.StoneConsumed);
        writer.WriteNumber("buildsCompleted", state.Economy.BuildsCompleted);
        writer.WriteEndObject();
        writer.WriteStartObject("labor");
        writer.WriteNumber("totalCreatureTicks", state.Labor.TotalCreatureTicks);
        writer.WriteNumber("foodWorkTicks", state.Labor.FoodWorkTicks);
        writer.WriteNumber("restTicks", state.Labor.RestTicks);
        writer.WriteNumber("eatTicks", state.Labor.EatTicks);
        writer.WriteNumber("drillTicks", state.Labor.DrillTicks);
        writer.WriteNumber("watchTicks", state.Labor.WatchTicks);
        writer.WriteNumber("digTicks", state.Labor.DigTicks);
        writer.WriteNumber("stoneHaulTicks", state.Labor.StoneHaulTicks);
        writer.WriteNumber("buildTicks", state.Labor.BuildTicks);
        writer.WriteNumber("musterTicks", state.Labor.MusterTicks);
        writer.WriteNumber("idleTicks", state.Labor.IdleTicks);
        writer.WriteNumber("foodWorkPercent", state.Labor.FoodWorkPercent);
        writer.WriteNumber("postOccupiedTicks", state.Labor.PostOccupiedTicks);
        writer.WriteNumber("postCapacityTicks", state.Labor.PostCapacityTicks);
        writer.WriteNumber("postOccupancyPercent", state.Labor.PostOccupancyPercent);
        writer.WriteEndObject();
        writer.WriteStartArray("stations");
        foreach (var station in state.Stations
                     .OrderBy(station => station.Kind)
                     .ThenBy(station => station.Position))
        {
            writer.WriteStartObject();
            WritePoint(writer, "position", station.Position);
            writer.WriteString("kind", ToJson(station.Kind));
            if (station.OccupiedBy is { } occupiedBy)
            {
                writer.WriteNumber("occupiedBy", occupiedBy);
            }
            else
            {
                writer.WriteNull("occupiedBy");
            }

            writer.WriteNumber("occupiedTicks", station.OccupiedTicks);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (includeEvents)
        {
            writer.WriteStartArray("events");
            WriteEvents(writer, state.Events);
            writer.WriteEndArray();
        }

        writer.WriteStartObject("threat");
        writer.WriteBoolean("announced", state.Threat.Announced);
        writer.WriteNumber("waveNumber", state.Threat.WaveNumber);
        writer.WriteNumber("waveCount", state.Threat.WaveCount);
        writer.WriteNumber("announceTick", state.Threat.AnnounceTick);
        writer.WriteNumber("arriveTick", state.Threat.ArriveTick);
        writer.WriteNumber("raiderCount", state.Threat.RaiderCount);
        writer.WriteNumber("raiderMight", state.Threat.RaiderMight);
        writer.WriteNumber("ticksRemaining", state.Threat.TicksRemaining);
        writer.WriteBoolean("active", state.Threat.Active);
        writer.WriteEndObject();

        // The whole sequence, not only the wave in hand: a replay has to be able
        // to say what the third wave was made of after the fourth has landed.
        writer.WriteStartArray("waves");
        foreach (var wave in state.Waves.OrderBy(wave => wave.Number))
        {
            writer.WriteStartObject();
            writer.WriteNumber("number", wave.Number);
            writer.WriteNumber("announceTick", wave.AnnounceTick);
            writer.WriteNumber("arriveTick", wave.ArriveTick);
            writer.WriteBoolean("announced", wave.Announced);
            writer.WriteBoolean("arrived", wave.Arrived);
            writer.WriteNumber("raiderCount", wave.RaiderCount);
            writer.WriteNumber("raiderMight", wave.RaiderMight);
            if (wave.Outcome is { } waveOutcome)
            {
                writer.WriteString("outcome", waveOutcome);
            }
            else
            {
                writer.WriteNull("outcome");
            }

            if (wave.EndTick is { } waveEndTick)
            {
                writer.WriteNumber("endTick", waveEndTick);
            }
            else
            {
                writer.WriteNull("endTick");
            }

            writer.WriteNumber("raidersDowned", wave.RaidersDowned);
            writer.WriteNumber("defendersDowned", wave.DefendersDowned);
            writer.WriteNumber("defendersFled", wave.DefendersFled);
            writer.WriteNumber("mealsStolen", wave.MealsStolen);
            writer.WriteNumber("renownAtAnnounce", wave.RenownAtAnnounce);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("domain");
        writer.WriteNumber("renown", state.Domain.Renown);
        writer.WriteNumber("strength", state.Domain.Strength);
        if (state.Domain.RenownAtPreviousWave is { } previousRenown)
        {
            writer.WriteNumber("renownAtPreviousWave", previousRenown);
        }
        else
        {
            writer.WriteNull("renownAtPreviousWave");
        }

        if (state.Domain.StrengthAtPreviousWave is { } previousStrength)
        {
            writer.WriteNumber("strengthAtPreviousWave", previousStrength);
        }
        else
        {
            writer.WriteNull("strengthAtPreviousWave");
        }

        writer.WriteNumber("livingCreatures", state.Domain.LivingCreatures);
        writer.WriteNumber("downedCreatures", state.Domain.DownedCreatures);
        writer.WriteNumber("injuredCreatures", state.Domain.InjuredCreatures);
        writer.WriteNumber("peakMeals", state.Domain.PeakMeals);
        writer.WriteNumber("wavesArrived", state.Domain.WavesArrived);
        writer.WriteNumber("wavesResolved", state.Domain.WavesResolved);
        writer.WriteNumber("waveCount", state.Domain.WaveCount);
        writer.WriteEndObject();

        writer.WriteStartArray("raiders");
        foreach (var raider in state.Raiders.OrderBy(raider => raider.Id))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", raider.Id);
            writer.WriteNumber("wave", raider.Wave);
            writer.WriteNumber("hp", raider.Hp);
            writer.WriteNumber("might", raider.Might);
            WritePoint(writer, "position", raider.Position);
            writer.WriteNumber("carryingMeals", raider.CarryingMeals);
            writer.WriteNumber("stealTicks", raider.StealTicks);
            writer.WriteBoolean("returningToGate", raider.ReturningToGate);
            writer.WriteString("mode", ToJson(raider.Mode));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartObject("sessionResult");
        if (state.SessionResult.Outcome is { } outcome) writer.WriteString("outcome", outcome); else writer.WriteNull("outcome");
        if (state.SessionResult.EndTick is { } endTick) writer.WriteNumber("endTick", endTick); else writer.WriteNull("endTick");
        writer.WriteBoolean("unresolved", state.SessionResult.Unresolved);
        if (state.SessionResult.LastWaveOutcome is { } lastWaveOutcome)
        {
            writer.WriteString("lastWaveOutcome", lastWaveOutcome);
        }
        else
        {
            writer.WriteNull("lastWaveOutcome");
        }

        writer.WriteNumber("wavesResolved", state.SessionResult.WavesResolved);
        writer.WriteNumber("wavesRepelled", state.SessionResult.WavesRepelled);
        writer.WriteNumber("waveCount", state.SessionResult.WaveCount);
        writer.WriteNumber("renown", state.SessionResult.Renown);
        writer.WriteNumber("strength", state.SessionResult.Strength);
        writer.WriteNumber("defendersDowned", state.SessionResult.DefendersDowned);
        writer.WriteNumber("defendersFled", state.SessionResult.DefendersFled);
        writer.WriteNumber("raidersDowned", state.SessionResult.RaidersDowned);
        writer.WriteNumber("mealsStolen", state.SessionResult.MealsStolen);
        writer.WriteNumber("mealsLeft", state.SessionResult.MealsLeft);

        // The score is written only for a party that ended, and is absent —
        // not null — for one that has not. That is the canonical form of "the
        // score is read at the end and never during the party" (ADR 0016), and
        // it is why the state of a party in progress is unchanged to the bit by
        // the arrival of a score: the same tick of the same log still hashes to
        // what it hashed to before, so a mid-party golden that moves is a leak
        // and not a chore.
        if (state.SessionResult.Score is { } score)
        {
            writer.WriteNumber("score", score);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteEvents(Utf8JsonWriter writer, IEnumerable<PrototypeEvent> events)
    {
        foreach (var @event in events)
        {
            writer.WriteStartObject();
            writer.WriteNumber("firstTick", @event.FirstTick);
            writer.WriteNumber("lastTick", @event.LastTick);
            writer.WriteNumber("creatureId", @event.CreatureId);
            writer.WriteString("reasonCode", @event.ReasonCode);
            WriteNullableEnum(writer, "jobKind", @event.JobKind);
            WriteNullablePoint(writer, "target", @event.Target);
            WriteDetails(writer, @event.Details);
            writer.WriteNumber("repeats", @event.Repeats);
            writer.WriteEndObject();
        }
    }

    private static void WriteDetails(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, int> details)
    {
        writer.WriteStartObject("details");
        foreach (var detail in details.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteNumber(detail.Key, detail.Value);
        }

        writer.WriteEndObject();
    }

    private static void WritePoint(Utf8JsonWriter writer, string name, GridPoint point)
    {
        writer.WritePropertyName(name);
        WritePointValue(writer, point);
    }

    private static void WriteNullablePoint(
        Utf8JsonWriter writer,
        string name,
        GridPoint? point)
    {
        if (point is { } value)
        {
            WritePoint(writer, name, value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullableEnum<T>(
        Utf8JsonWriter writer,
        string name,
        T? value)
        where T : struct, Enum
    {
        if (value is { } item)
        {
            writer.WriteString(name, ToJson(item));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WritePointValue(Utf8JsonWriter writer, GridPoint point)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(point.X);
        writer.WriteNumberValue(point.Y);
        writer.WriteEndArray();
    }

    private static string ToJson<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
