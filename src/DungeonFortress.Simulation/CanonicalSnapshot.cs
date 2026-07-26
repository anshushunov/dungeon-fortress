using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DungeonFortress.Simulation;

public static class CanonicalSnapshot
{
    public const int SchemaVersion = 1;

    public static byte[] Serialize(SimulationWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            });

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteNumber("seed", world.Config.Seed);
        writer.WriteNumber("tick", world.CurrentTick);
        writer.WriteNumber("commandsApplied", world.CommandsApplied);
        writer.WriteNumber("worldWidth", SimulationWorld.WorldWidth);
        writer.WriteNumber("worldHeight", SimulationWorld.WorldHeight);
        writer.WriteStartArray("agents");
        world.WriteAgents(writer);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    public static string ComputeChecksum(ReadOnlySpan<byte> canonicalJson)
    {
        return Convert.ToHexString(SHA256.HashData(canonicalJson)).ToLowerInvariant();
    }
}
