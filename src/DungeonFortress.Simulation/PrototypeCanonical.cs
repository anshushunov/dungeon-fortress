using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DungeonFortress.Simulation;

public static class PrototypeCanonical
{
    public const int SchemaVersion = 1;

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
        writer.WriteNumber("seed", state.Seed);
        writer.WriteNumber("tick", state.Tick);
        writer.WriteNumber("commandsApplied", state.CommandsApplied);

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
        writer.WriteStartObject("stocks");
        writer.WriteNumber("rawMushroom", state.Stocks.RawMushroom);
        writer.WriteNumber("meals", state.Stocks.Meals);
        writer.WriteNumber("looseRawMushroom", state.Stocks.LooseRawMushroom);
        writer.WriteNumber("looseMeals", state.Stocks.LooseMeals);
        writer.WriteNumber("capacity", state.Stocks.Capacity);
        writer.WriteNumber("mealsProduced", state.Stocks.MealsProduced);
        writer.WriteNumber("mealsEaten", state.Stocks.MealsEaten);
        writer.WriteEndObject();

        writer.WriteStartArray("jobs");
        foreach (var job in state.Jobs.OrderBy(job => job.JobId))
        {
            writer.WriteStartObject();
            writer.WriteNumber("jobId", job.JobId);
            writer.WriteString("kind", ToJson(job.Kind));
            WritePoint(writer, "target", job.Target);
            if (job.Resource is { } resource)
            {
                writer.WriteString("resource", ToJson(resource));
            }
            else
            {
                writer.WriteNull("resource");
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
        writer.WriteNumber("announceTick", state.Threat.AnnounceTick);
        writer.WriteNumber("raidTick", state.Threat.RaidTick);
        writer.WriteNumber("raiderCount", state.Threat.RaiderCount);
        writer.WriteNumber("ticksRemaining", state.Threat.TicksRemaining);
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
