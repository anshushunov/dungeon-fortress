using DungeonFortress.DomainMcp;

using Xunit;

namespace DungeonFortress.DomainMcp.Tests;

public sealed class ProjectRootTests
{
    [Fact]
    public void Repository_root_requires_all_sentinels()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "df-domain-mcp-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            var exception = Assert.Throws<InvalidDataException>(
                () => ProjectRoot.Resolve(["--root", temporaryRoot]));

            Assert.Contains("missing sentinel", exception.Message);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Command_document_rejects_traversal_and_absolute_paths()
    {
        var root = ProjectRoot.Resolve(["--root", FindRepositoryRoot()]);

        Assert.Throws<InvalidDataException>(
            () => root.ResolveCommandDocument("../outside.json"));
        Assert.Throws<InvalidDataException>(
            () => root.ResolveCommandDocument(
                Path.GetFullPath(Path.Combine(root.FullPath, "scenarios", "smoke.commands.json"))));
    }

    [Fact]
    public void Command_document_rejects_case_distinct_sibling_on_case_sensitive_systems()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var temporaryBase = CreateTemporaryBase();
        try
        {
            var rootPath = Path.Combine(temporaryBase, "Repo");
            var siblingPath = Path.Combine(temporaryBase, "repo");
            CreateRepositorySentinels(rootPath);
            Directory.CreateDirectory(siblingPath);
            File.WriteAllText(Path.Combine(siblingPath, "outside.json"), "{}");

            var root = ProjectRoot.Resolve(["--root", rootPath]);
            Assert.Throws<InvalidDataException>(
                () => root.ResolveCommandDocument("../repo/outside.json"));
        }
        finally
        {
            Directory.Delete(temporaryBase, recursive: true);
        }
    }

    [Fact]
    public void Command_document_rejects_symbolic_link_to_external_file()
    {
        var temporaryBase = CreateTemporaryBase();
        try
        {
            var rootPath = Path.Combine(temporaryBase, "root");
            CreateRepositorySentinels(rootPath);
            var outsidePath = Path.Combine(temporaryBase, "outside.json");
            File.WriteAllText(outsidePath, "{}");
            var linkPath = Path.Combine(rootPath, "linked.json");

            if (!TryCreateFileSymbolicLink(linkPath, outsidePath))
            {
                return;
            }

            var root = ProjectRoot.Resolve(["--root", rootPath]);
            var exception = Assert.Throws<InvalidDataException>(
                () => root.ResolveCommandDocument("linked.json"));
            Assert.Contains("symbolic link or junction", exception.Message);
        }
        finally
        {
            Directory.Delete(temporaryBase, recursive: true);
        }
    }

    [Fact]
    public void Command_document_rejects_directory_link_to_external_file()
    {
        var temporaryBase = CreateTemporaryBase();
        try
        {
            var rootPath = Path.Combine(temporaryBase, "root");
            var outsidePath = Path.Combine(temporaryBase, "outside");
            CreateRepositorySentinels(rootPath);
            Directory.CreateDirectory(outsidePath);
            File.WriteAllText(Path.Combine(outsidePath, "commands.json"), "{}");
            var linkPath = Path.Combine(rootPath, "linked");

            if (!TryCreateDirectorySymbolicLink(linkPath, outsidePath))
            {
                return;
            }

            var root = ProjectRoot.Resolve(["--root", rootPath]);
            var exception = Assert.Throws<InvalidDataException>(
                () => root.ResolveCommandDocument("linked/commands.json"));
            Assert.Contains("symbolic link or junction", exception.Message);
        }
        finally
        {
            Directory.Delete(temporaryBase, recursive: true);
        }
    }

    private static string CreateTemporaryBase()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "df-domain-mcp-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateRepositorySentinels(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(
            Path.Combine(rootPath, "src", "DungeonFortress.Simulation"));
        File.WriteAllText(Path.Combine(rootPath, "AGENTS.md"), "");
        File.WriteAllText(Path.Combine(rootPath, "DungeonFortress.sln"), "");
        File.WriteAllText(
            Path.Combine(
                rootPath,
                "src",
                "DungeonFortress.Simulation",
                "DungeonFortress.Simulation.csproj"),
            "");
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            Assert.False(
                OperatingSystem.IsLinux(),
                $"Linux test environment must support symbolic links: {exception.Message}");
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            Assert.False(
                OperatingSystem.IsLinux(),
                $"Linux test environment must support directory links: {exception.Message}");
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DungeonFortress.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find the repository root.");
    }
}
