using System.IO;
using System.Text;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Writes a measurement produced by a test into the ignored .artifacts/ tree,
/// creating the directory on demand, so no test ever leaves a file in Git.
/// </summary>
internal static class ArtifactFile
{
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    public static void WriteAllText(string path, string contents, Encoding encoding)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, encoding);
    }
}
