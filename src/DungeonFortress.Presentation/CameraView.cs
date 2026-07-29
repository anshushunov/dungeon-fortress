using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// A point in either world or viewport coordinates. The presentation assembly
/// deliberately owns this tiny engine-free value instead of taking a dependency
/// on Godot's <c>Vector2</c>.
/// </summary>
public readonly record struct ViewPoint(double X, double Y);

/// <summary>A size measured in presentation pixels.</summary>
public readonly record struct ViewSize(double Width, double Height);

/// <summary>
/// The screen rectangle reserved for the world. HUD pixels outside it are never
/// interpreted as map input.
/// </summary>
public readonly record struct ViewRect(double X, double Y, double Width, double Height)
{
    public ViewPoint Center => new(X + (Width / 2.0), Y + (Height / 2.0));

    public bool Contains(ViewPoint point) =>
        point.X >= X &&
        point.Y >= Y &&
        point.X < X + Width &&
        point.Y < Y + Height;
}

/// <summary>
/// One deterministic camera frame. <see cref="Center"/> names the world point
/// that appears at the center of <see cref="WorldViewport"/>. Godot's Camera2D
/// node itself is positioned relative to the center of the full frame, so
/// <see cref="CameraNodePosition"/> accounts for the HUD-owned offset.
/// </summary>
public readonly record struct CameraFrame(
    ViewPoint Center,
    double Zoom,
    ViewRect WorldViewport,
    ViewSize FullViewport)
{
    public ViewPoint CameraNodePosition
    {
        get
        {
            var frameCenter = new ViewPoint(FullViewport.Width / 2.0, FullViewport.Height / 2.0);
            var worldCenter = WorldViewport.Center;
            return new ViewPoint(
                Center.X - ((worldCenter.X - frameCenter.X) / Zoom),
                Center.Y - ((worldCenter.Y - frameCenter.Y) / Zoom));
        }
    }

    public ViewSize VisibleWorldSize =>
        new(WorldViewport.Width / Zoom, WorldViewport.Height / Zoom);

    public ViewPoint WorldToScreen(ViewPoint world)
    {
        var camera = CameraNodePosition;
        return new ViewPoint(
            ((world.X - camera.X) * Zoom) + (FullViewport.Width / 2.0),
            ((world.Y - camera.Y) * Zoom) + (FullViewport.Height / 2.0));
    }

    public ViewPoint ScreenToWorld(ViewPoint screen)
    {
        var camera = CameraNodePosition;
        return new ViewPoint(
            ((screen.X - (FullViewport.Width / 2.0)) / Zoom) + camera.X,
            ((screen.Y - (FullViewport.Height / 2.0)) / Zoom) + camera.Y);
    }
}

/// <summary>
/// Camera and grid arithmetic shared by the Godot adapter and pure .NET tests.
/// None of these values is canonical state: changing any of them can only change
/// which pixels are visible.
/// </summary>
public static class CameraView
{
    public const int DefaultTileSize = 40;
    public const int MinimumTileSize = 32;
    public const int MaximumTileSize = 48;
    public const double DefaultZoom = 1.0;
    public const double DefaultUiScale = 1.0;
    public const double MinimumUiScale = 0.75;
    public const double MaximumUiScale = 2.0;

    private const double ReferenceTileSize = 22.0;
    private const double ReferenceGoblinDrawSize = 20.0;
    private static readonly double[] DiscreteZoomLevels = [0.5, 0.75, 1.0, 1.5, 2.0];

    public static IReadOnlyList<double> ZoomLevels => DiscreteZoomLevels;

    public static int ValidateTileSize(int tileSize)
    {
        if (tileSize is < MinimumTileSize or > MaximumTileSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tileSize),
                tileSize,
                $"Tile size must be between {MinimumTileSize} and {MaximumTileSize}.");
        }

        return tileSize;
    }

    public static double ValidateZoom(double zoom)
    {
        if (!DiscreteZoomLevels.Contains(zoom))
        {
            throw new ArgumentOutOfRangeException(
                nameof(zoom),
                zoom,
                $"Zoom must be one of: {string.Join(", ", DiscreteZoomLevels)}.");
        }

        return zoom;
    }

    public static double ValidateUiScale(double uiScale)
    {
        if (!double.IsFinite(uiScale) ||
            uiScale < MinimumUiScale ||
            uiScale > MaximumUiScale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(uiScale),
                uiScale,
                $"UI scale must be between {MinimumUiScale} and {MaximumUiScale}.");
        }

        return uiScale;
    }

    /// <summary>
    /// Preserves the proportions of world-space primitives authored for the old
    /// 22 px grid while allowing the grid itself to be selected in ADR 0008's
    /// 32–48 px range.
    /// </summary>
    public static double WorldVisualScale(int tileSize) =>
        ValidateTileSize(tileSize) / ReferenceTileSize;

    public static double GoblinDrawSize(int tileSize) =>
        ReferenceGoblinDrawSize * WorldVisualScale(tileSize);

    public static ViewSize MapSize(int tileSize)
    {
        ValidateTileSize(tileSize);
        return new ViewSize(
            PrototypeTuning.MapWidth * tileSize,
            PrototypeTuning.MapHeight * tileSize);
    }

    public static ViewPoint MapCenter(int tileSize)
    {
        var size = MapSize(tileSize);
        return new ViewPoint(size.Width / 2.0, size.Height / 2.0);
    }

    public static ViewPoint CellTopLeft(GridPoint cell, int tileSize)
    {
        ValidateTileSize(tileSize);
        return new ViewPoint(cell.X * tileSize, cell.Y * tileSize);
    }

    public static ViewPoint CellCenter(GridPoint cell, int tileSize)
    {
        var topLeft = CellTopLeft(cell, tileSize);
        return new ViewPoint(topLeft.X + (tileSize / 2.0), topLeft.Y + (tileSize / 2.0));
    }

    public static GridPoint WorldToCell(ViewPoint world, int tileSize)
    {
        ValidateTileSize(tileSize);
        return new GridPoint(
            (int)Math.Floor(world.X / tileSize),
            (int)Math.Floor(world.Y / tileSize));
    }

    public static GridPoint? ScreenToCell(CameraFrame frame, ViewPoint screen, int tileSize)
    {
        ValidateZoom(frame.Zoom);
        if (!frame.WorldViewport.Contains(screen))
        {
            return null;
        }

        var cell = WorldToCell(frame.ScreenToWorld(screen), tileSize);
        return MapBounds.Contains(cell) ? cell : null;
    }

    /// <summary>
    /// Moves the camera while a middle-button drag keeps the grabbed world point
    /// under the cursor.
    /// </summary>
    public static ViewPoint PanByScreenDelta(ViewPoint center, ViewPoint screenDelta, double zoom)
    {
        ValidateZoom(zoom);
        return new ViewPoint(
            center.X - (screenDelta.X / zoom),
            center.Y - (screenDelta.Y / zoom));
    }

    /// <summary>
    /// Keeps the camera focus on the ownership map without cancelling overview
    /// panning. The focus may travel between the centers of the two edge tiles,
    /// so even when the whole map fits in the viewport a drag still moves it, but
    /// the camera can never wander into empty space beyond the map.
    /// </summary>
    public static ViewPoint ClampCenterToMap(ViewPoint center, int tileSize)
    {
        ValidateTileSize(tileSize);
        if (!double.IsFinite(center.X) ||
            !double.IsFinite(center.Y))
        {
            throw new ArgumentOutOfRangeException(
                nameof(center),
                center,
                "Camera center must be finite.");
        }

        var map = MapSize(tileSize);
        var halfTile = tileSize / 2.0;
        return new ViewPoint(
            Math.Clamp(center.X, halfTile, map.Width - halfTile),
            Math.Clamp(center.Y, halfTile, map.Height - halfTile));
    }

    public static ViewPoint MoveByTiles(
        ViewPoint center,
        int horizontalTiles,
        int verticalTiles,
        int tileSize)
    {
        ValidateTileSize(tileSize);
        return new ViewPoint(
            center.X + (horizontalTiles * tileSize),
            center.Y + (verticalTiles * tileSize));
    }

    public static double StepZoom(double current, int direction)
    {
        ValidateZoom(current);
        var index = Array.IndexOf(DiscreteZoomLevels, current);
        var next = Math.Clamp(index + Math.Sign(direction), 0, DiscreteZoomLevels.Length - 1);
        return DiscreteZoomLevels[next];
    }
}
