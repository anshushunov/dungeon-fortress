namespace DungeonFortress.Presentation;

/// <summary>
/// Presentation-only launch parameters. Captures require every pixel-affecting
/// value explicitly, so repeating a command cannot inherit a window or camera
/// state from the previous run.
/// </summary>
public sealed record ViewLaunchOptions(
    int TileSize,
    double CameraZoom,
    ViewPoint CameraPosition,
    double UiScale,
    ViewSize? FrameSize)
{
    private static readonly string[] CaptureParameterNames =
    [
        "--tile-size",
        "--camera-zoom",
        "--camera-position",
        "--ui-scale",
        "--frame-size",
    ];

    public static ViewLaunchOptions Parse(
        IReadOnlyList<string> arguments,
        bool requireExplicitCaptureParameters)
    {
        if (requireExplicitCaptureParameters)
        {
            var missing = CaptureParameterNames
                .Where(name => CommandLineArguments.Read(arguments, name) is null)
                .ToArray();
            if (missing.Length > 0)
            {
                throw new ArgumentException(
                    "Screenshot capture requires explicit frame parameters: " +
                    string.Join(", ", missing) + ".");
            }
        }

        var tileSize = CameraView.ValidateTileSize(
            CommandLineArguments.ReadInt(arguments, "--tile-size") ?? CameraView.DefaultTileSize);
        var zoom = CameraView.ValidateZoom(
            CommandLineArguments.ReadDouble(arguments, "--camera-zoom") ?? CameraView.DefaultZoom);
        var uiScale = CameraView.ValidateUiScale(
            CommandLineArguments.ReadDouble(arguments, "--ui-scale") ?? CameraView.DefaultUiScale);
        var positionValue = CommandLineArguments.Read(arguments, "--camera-position");
        var position = positionValue is null
            ? CameraView.MapCenter(tileSize)
            : CommandLineArguments.ParsePoint(positionValue, "--camera-position");
        var frameValue = CommandLineArguments.Read(arguments, "--frame-size");
        ViewSize? frame = frameValue is null
            ? null
            : CommandLineArguments.ParseSize(frameValue, "--frame-size");

        return new ViewLaunchOptions(tileSize, zoom, position, uiScale, frame);
    }
}
