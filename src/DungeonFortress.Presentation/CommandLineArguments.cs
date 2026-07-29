using DungeonFortress.Simulation;

using System.Globalization;

namespace DungeonFortress.Presentation;

/// <summary>
/// Reading the user arguments Godot hands the scene. Nothing here knows about
/// <c>OS.GetCmdlineUserArgs()</c>; it takes the list it is given, which is what
/// makes "a missing value is an error" and "--select-cell must be on the map"
/// checkable without starting an engine.
/// </summary>
public static class CommandLineArguments
{
    /// <summary>
    /// The value following <paramref name="name"/>, or <c>null</c> if the flag is
    /// absent. A flag present with nothing after it is an error rather than a
    /// silent default, because that is a typed command line, not a choice.
    /// </summary>
    public static string? Read(IReadOnlyList<string> arguments, string name)
    {
        var index = -1;
        for (var candidate = 0; candidate < arguments.Count; candidate++)
        {
            if (string.Equals(arguments[candidate], name, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        if (index == -1)
        {
            return null;
        }

        if (index + 1 >= arguments.Count)
        {
            throw new ArgumentException($"Missing value after {name}.");
        }

        return arguments[index + 1];
    }

    public static int? ReadInt(IReadOnlyList<string> arguments, string name)
    {
        var value = Read(arguments, name);
        return value is null ? null : int.Parse(value, CultureInfo.InvariantCulture);
    }

    public static double? ReadDouble(IReadOnlyList<string> arguments, string name)
    {
        var value = Read(arguments, name);
        return value is null ? null : double.Parse(value, CultureInfo.InvariantCulture);
    }

    public static ViewPoint ParsePoint(string value, string parameterName)
    {
        var parts = value.Split(',');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.IsFinite(x) ||
            !double.IsFinite(y))
        {
            throw new ArgumentException(
                $"{parameterName} expects finite X,Y, got '{value}'.",
                parameterName);
        }

        return new ViewPoint(x, y);
    }

    public static ViewSize ParseSize(string value, string parameterName)
    {
        var parts = value.Split('x');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
            width <= 0 ||
            height <= 0)
        {
            throw new ArgumentException(
                $"{parameterName} expects positive integer WIDTHxHEIGHT, got '{value}'.",
                parameterName);
        }

        return new ViewSize(width, height);
    }

    /// <summary>
    /// The <c>X,Y</c> form of <c>--select-cell</c>. A cell off the map is rejected
    /// here rather than being clamped, so a capture never silently inspects a
    /// different tile than the one the command line asked for.
    /// </summary>
    public static GridPoint ParseCell(string value)
    {
        var parts = value.Split(',');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var x) ||
            !int.TryParse(parts[1], out var y) ||
            !MapBounds.Contains(new GridPoint(x, y)))
        {
            throw new ArgumentException(
                $"--select-cell expects X,Y inside the map, got '{value}'.",
                "--select-cell");
        }

        return new GridPoint(x, y);
    }
}
