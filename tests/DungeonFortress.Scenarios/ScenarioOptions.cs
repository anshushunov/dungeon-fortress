using System.Globalization;

namespace DungeonFortress.Scenarios;

internal sealed record ScenarioOptions(
    ulong Seed,
    int AgentCount,
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
        var agentCount = DefaultAgentCount;
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
                    seed = ulong.Parse(
                        RequireValue(args, ref index, "--seed"),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture);
                    break;
                case "--agents":
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
            agentCount,
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
          --seed <ulong>       Deterministic seed (default: 424242)
          --agents <int>       Number of lightweight agents (default: 32)
          --ticks <int>        Number of explicit fixed ticks (default: 256)
          --commands <path>    Ordered JSON command sequence
          --snapshot <path>    Write canonical UTF-8 JSON snapshot
          --print-snapshot     Print canonical JSON before the result event
          --prototype          Run gameplay schema v2; --commands is required
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
