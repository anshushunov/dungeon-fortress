namespace DungeonFortress.DomainMcp;

public sealed class ProjectRoot
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly string[] RequiredSentinels =
    [
        "AGENTS.md",
        "DungeonFortress.sln",
        Path.Combine(
            "src",
            "DungeonFortress.Simulation",
            "DungeonFortress.Simulation.csproj"),
    ];

    private ProjectRoot(string fullPath)
    {
        FullPath = fullPath;
        Prefix = fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public string FullPath { get; }

    public string[] Sentinels => [.. RequiredSentinels];

    private string Prefix { get; }

    public static ProjectRoot Resolve(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? candidate = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], "--root", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Unsupported server option '{args[index]}'. Only --root is accepted.");
            }

            if (candidate is not null || ++index == args.Length)
            {
                throw new ArgumentException("--root must be supplied exactly once with a value.");
            }

            candidate = args[index];
        }

        candidate ??= Environment.GetEnvironmentVariable("DUNGEON_FORTRESS_ROOT");
        candidate ??= Directory.GetCurrentDirectory();

        var fullPath = Path.GetFullPath(candidate);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                "The configured Dungeon Fortress repository root does not exist.");
        }

        foreach (var sentinel in RequiredSentinels)
        {
            if (!File.Exists(Path.Combine(fullPath, sentinel)))
            {
                throw new InvalidDataException(
                    $"Repository root validation failed: missing sentinel '{sentinel}'.");
            }
        }

        return new ProjectRoot(fullPath);
    }

    public string ResolveCommandDocument(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                "commandsPath must be a repository-relative path.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(FullPath, relativePath));
        if (!fullPath.StartsWith(Prefix, PathComparison))
        {
            throw new InvalidDataException(
                "commandsPath must remain inside the validated repository root.");
        }

        if (!string.Equals(
                Path.GetExtension(fullPath),
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("commandsPath must reference a .json document.");
        }

        RejectReparsePoints(fullPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The requested command document does not exist inside the repository.");
        }

        return fullPath;
    }

    private void RejectReparsePoints(string fullPath)
    {
        FileSystemInfo? current = new FileInfo(fullPath);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "commandsPath cannot pass through a symbolic link or junction.");
            }

            if (string.Equals(
                    current.FullName.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    FullPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    PathComparison))
            {
                return;
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }

        throw new InvalidDataException(
            "commandsPath could not be validated against the repository root.");
    }
}
