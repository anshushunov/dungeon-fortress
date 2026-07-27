using System.Globalization;

namespace DungeonFortress.Scenarios;

public sealed record ScenarioOptions(
    ulong Seed,
    bool SeedSpecified,
    int AgentCount,
    bool AgentCountSpecified,
    int TickCount,
    string? CommandsPath,
    string? SnapshotPath,
    bool PrintSnapshot,
    bool Prototype)
{
    public const ulong DefaultSeed = 424_242UL;
    public const int DefaultAgentCount = 32;
    public const int DefaultTickCount = 256;

    public static ScenarioOptions Parse(string[] args)
    {
        var seed = DefaultSeed;
        var seedSpecified = false;
        var agentCount = DefaultAgentCount;
        var agentCountSpecified = false;
        var tickCount = DefaultTickCount;
        string? commandsPath = null;
        string? snapshotPath = null;
        var printSnapshot = false;
        var prototype = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--seed":
                    seedSpecified = true;
                    seed = ulong.Parse(
                        RequireValue(args, ref index, "--seed"),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture);
                    break;
                case "--agents":
                    agentCountSpecified = true;
                    agentCount = int.Parse(
                        RequireValue(args, ref index, "--agents"),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture);
                    break;
                case "--ticks":
                    tickCount = int.Parse(
                        RequireValue(args, ref index, "--ticks"),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture);
                    break;
                case "--commands":
                    commandsPath = RequireValue(args, ref index, "--commands");
                    break;
                case "--snapshot":
                    snapshotPath = RequireValue(args, ref index, "--snapshot");
                    break;
                case "--print-snapshot":
                    printSnapshot = true;
                    break;
                case "--prototype":
                    prototype = true;
                    break;
                case "--help":
                case "-h":
                    throw new HelpRequestedException();
                default:
                    throw new ArgumentException($"Unknown option: {args[index]}");
            }
        }

        if (tickCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Tick count cannot be negative.");
        }

        return new ScenarioOptions(
            seed,
            seedSpecified,
            agentCount,
            agentCountSpecified,
            tickCount,
            commandsPath,
            snapshotPath,
            printSnapshot,
            prototype);
    }

    public static string Usage =>
        """
        Usage:
          dotnet run --project tests/DungeonFortress.Scenarios -- [options]

        Options:
          --seed <ulong>       Legacy simulation seed; rejected with --prototype
          --agents <int>       Legacy agent count; rejected with --prototype
          --ticks <int>        Number of explicit fixed ticks (default: 256)
          --commands <path>    Ordered JSON command sequence
          --snapshot <path>    Write canonical UTF-8 JSON snapshot
          --print-snapshot     Print canonical JSON before the result event
          --prototype          Run gameplay schema v2; commands supplies seed/population
          --help               Show this help
        """;

    private static string RequireValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[index];
    }
}

internal sealed class HelpRequestedException : Exception;
