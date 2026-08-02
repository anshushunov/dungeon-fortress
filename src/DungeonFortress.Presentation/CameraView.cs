using System.Globalization;

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

    /// <summary>
    /// The frame the HUD is authored against. It is not a description of anyone's
    /// monitor, and using it as a launch default is what Issues #86 and #100 were:
    /// the game opened a small window with 8-15 px text. It stays here as the unit
    /// the automatic UI scale counts in — "how many times does the authored
    /// rectangle fit into this window" — and as the fallback when there is no
    /// screen to ask.
    /// </summary>
    public static readonly ViewSize DesignFrameSize = new(1280, 720);

    /// <summary>
    /// How much of a screen's usable rectangle a fresh window takes. Not the whole
    /// rectangle: the window stays an ordinary movable, resizable window with room
    /// for its own decorations.
    /// </summary>
    public const double StartupFrameScreenShare = 0.9;

    private const double ReferenceTileSize = 22.0;
    private const double ReferenceGoblinDrawSize = 20.0;

    /// <summary>
    /// How large a body is drawn against the world it stands in, as a factor on
    /// the size bodies had before Issue #77.
    ///
    /// <para>
    /// 1.70 is the owner's decision of 2026-08-01 on spike #142, recorded in the
    /// gate log of <c>docs/product/ROADMAP.md</c>: he clicked through the sizes
    /// in a live scene and picked <em>170 % of the previous size</em>. 100 % was
    /// rejected outright and 200 % as too large; the trade he named is that at
    /// 150 % fighters in a corridor still have gaps between them and the grid
    /// shows through, at 170-175 they stand shoulder to shoulder while the floor
    /// still reads, and at 200 % the floor under a crowd disappears. The choice
    /// is for the readability of the creature over the readability of the
    /// formation, and it is reopened if slice 6 «space as a weapon» cannot show
    /// the mouth of a corridor.
    /// </para>
    ///
    /// <para>
    /// Written as a factor, not as a new reference size, so that the decision
    /// stays legible as what it was: 61.8 px at the shipped 40 px tile is
    /// <c>20 * 1.70 * 40 / 22</c>, not a number anybody fitted. Visual body size
    /// is presentation tuning under ADR 0010 — it reaches no canonical state, so
    /// no checksum, replay or command depends on it.
    /// </para>
    /// </summary>
    public const double BodyVisualScale = 1.70;
    private static readonly double[] DiscreteZoomLevels = [0.5, 0.75, 1.0, 1.5, 2.0];

    /// <summary>
    /// The scales a run may choose for itself. Bounded above by
    /// <see cref="MaximumUiScale"/>, which is also the largest value an explicit
    /// <c>--ui-scale</c> may name, and below by 1: a window that is already small
    /// must not have its interface made smaller still.
    /// </summary>
    private static readonly double[] AutomaticUiScaleSteps = [1.0, 1.25, 1.5, 1.75, 2.0];

    public static IReadOnlyList<double> ZoomLevels => DiscreteZoomLevels;

    public static IReadOnlyList<double> AutomaticUiScales => AutomaticUiScaleSteps;

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
                "Zoom must be one of: " +
                string.Join(
                    ", ",
                    DiscreteZoomLevels.Select(value =>
                        value.ToString("G17", CultureInfo.InvariantCulture))) +
                ".");
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
                "UI scale must be between " +
                MinimumUiScale.ToString("G17", CultureInfo.InvariantCulture) +
                " and " +
                MaximumUiScale.ToString("G17", CultureInfo.InvariantCulture) +
                ".");
        }

        return uiScale;
    }

    // -----------------------------------------------------------------------
    // Startup frame and UI scale (Issues #100 and #86)
    //
    // A run that declares --frame-size and --ui-scale keeps every guarantee it
    // had: a capture is required to declare both, so nothing reproducible can
    // reach the arithmetic below. A run that declares nothing is an interactive
    // launch, and an interactive launch asks the screen rather than a constant.
    //
    // Deliberately pure geometry, with no DPI query. A Godot window on Windows is
    // per-monitor DPI aware, so a display the system scales at 200 % already
    // reports twice as many physical pixels and the ratio below picks the larger
    // scale from that alone. Reading the DPI separately would count it twice.
    // -----------------------------------------------------------------------

    /// <summary>
    /// The window a fresh interactive run opens on a screen whose usable
    /// rectangle is <paramref name="usableScreen"/>. Never smaller than
    /// <see cref="DesignFrameSize"/> and never larger than the screen.
    /// </summary>
    public static ViewSize AutomaticFrameSize(ViewSize usableScreen) =>
        new(
            Math.Max(DesignFrameSize.Width, Math.Floor(usableScreen.Width * StartupFrameScreenShare)),
            Math.Max(DesignFrameSize.Height, Math.Floor(usableScreen.Height * StartupFrameScreenShare)));

    /// <summary>
    /// The largest step at which <paramref name="frame"/> still shows the whole
    /// authored rectangle. A frame smaller than that rectangle gets scale 1,
    /// which is exactly what it got before this policy existed.
    /// </summary>
    public static double AutomaticUiScale(ViewSize frame)
    {
        var fit = Math.Min(
            frame.Width / DesignFrameSize.Width,
            frame.Height / DesignFrameSize.Height);
        var chosen = AutomaticUiScaleSteps[0];
        foreach (var step in AutomaticUiScaleSteps)
        {
            // 1e-9, so that an exactly fitting frame such as 1920x1080 gets 1.5
            // instead of losing it to binary representation.
            if (step <= fit + 1e-9)
            {
                chosen = step;
            }
        }

        return chosen;
    }

    /// <summary>
    /// The whole automatic scale decision for one frame, including the refusal
    /// to leave less than <paramref name="minimumLogical"/> logical pixels.
    ///
    /// <para>
    /// This overload exists because the decision used to be half here and half
    /// in the Godot adapter: the adapter asked for a scale, asked whether it
    /// fitted, and simply <em>did nothing</em> when it did not — so a window
    /// that started at scale 2 and was then dragged under the minimum logical
    /// frame kept scale 2 and halved the HUD's logical area again. The result is
    /// now a function of the frame alone, so there is no previous value left for
    /// a resize to keep (Issue #86).
    /// </para>
    ///
    /// <para>
    /// The loop steps down rather than dropping straight to 1. With today's
    /// constants those are the same answer, because the minimum logical frame is
    /// smaller than the authored rectangle every step is chosen against; written
    /// this way the two constants stay independent of each other.
    /// </para>
    /// </summary>
    public static double AutomaticUiScale(ViewSize frame, ViewSize minimumLogical)
    {
        var fit = Math.Min(
            frame.Width / DesignFrameSize.Width,
            frame.Height / DesignFrameSize.Height);
        var chosen = DefaultUiScale;
        foreach (var step in AutomaticUiScaleSteps)
        {
            if (step <= fit + 1e-9 && FitsLogicalFrame(frame, step, minimumLogical))
            {
                chosen = step;
            }
        }

        return chosen;
    }

    /// <summary>
    /// The zoom a run starts at when it was not told one: the largest declared
    /// level at which the whole ownership map still fits in the world viewport
    /// it was given, and the smallest level when none of them does.
    ///
    /// <para>
    /// The launcher used to hand every run the same fixed number, which is the
    /// second half of Issue #86: the window grew, the world did not, and a map
    /// 1120x640 world pixels wide sat in the middle of a 2200 px viewport
    /// drawing at 1:1. Zoom is a pure function of the rectangle the map is drawn
    /// into, so it belongs here rather than in the adapter, and an explicit
    /// <c>--camera-zoom</c> never reaches it.
    /// </para>
    /// </summary>
    public static double AutomaticZoom(ViewSize worldViewport, int tileSize)
    {
        var map = MapSize(tileSize);
        if (!double.IsFinite(worldViewport.Width) || !double.IsFinite(worldViewport.Height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(worldViewport),
                "The world viewport must be finite.");
        }

        var fit = Math.Min(worldViewport.Width / map.Width, worldViewport.Height / map.Height);
        var chosen = DiscreteZoomLevels[0];
        foreach (var level in DiscreteZoomLevels)
        {
            if (level <= fit + 1e-9)
            {
                chosen = level;
            }
        }

        return chosen;
    }

    /// <summary>
    /// Whether a frame at a UI scale leaves at least <paramref name="minimumLogical"/>
    /// logical pixels. The minimum belongs to the launch options rather than to
    /// the camera, so it is passed in rather than reached for.
    /// </summary>
    public static bool FitsLogicalFrame(ViewSize frame, double uiScale, ViewSize minimumLogical) =>
        frame.Width / uiScale >= minimumLogical.Width &&
        frame.Height / uiScale >= minimumLogical.Height;

    public static ViewSize LogicalFrameSize(ViewSize frame, double uiScale) =>
        new(frame.Width / uiScale, frame.Height / uiScale);

    /// <summary>
    /// Proves the properties of the functions above over a fixed matrix of real
    /// display sizes.
    ///
    /// It exists because this arithmetic would otherwise only ever run against
    /// whichever single display is in front of it: the machine that developed it
    /// has a 3072x1920 screen and would never execute the laptop branches. The
    /// Godot adapter calls it on every entry point, so every headless smoke,
    /// golden UI capture and screenshot in <c>verify.ps1</c> runs it — and it is
    /// engine-free, so a unit test can call it directly.
    ///
    /// It used to return how many assertions it had made, and a run printed that
    /// number as <c>startupFramePolicyChecks</c>. Nothing compared it with
    /// anything: review of PR #107 switched an assertion off, the run stayed
    /// green and the number stayed 17. The guard is the throw; the evidence that
    /// the throw is live is <c>CameraViewTests</c>, which makes it fire from both
    /// directions. A count that only ever grows when the matrix grows was
    /// decoration on top of that, so it is gone (Issue #86).
    ///
    /// 1. a chosen scale is one of the declared steps and inside the range an
    ///    explicit <c>--ui-scale</c> may use;
    /// 2. the automatic window fits its screen and is never smaller than the
    ///    authored rectangle;
    /// 3. the logical frame never drops under <paramref name="minimumLogical"/>;
    /// 4. the chosen step is the largest one that still shows the authored
    ///    rectangle — the next step up would not;
    /// 5. a bigger screen never produces a smaller window or a smaller scale;
    /// 6. and <see cref="FitsLogicalFrame"/> rejects a pair that violates it,
    ///    because a guard never seen to fail is not evidence.
    /// </summary>
    public static void AssertStartupFramePolicy(ViewSize minimumLogical)
    {
        ViewSize[] screens =
        [
            new(1280, 720),
            new(1366, 768),
            new(1440, 900),
            new(1600, 900),
            new(1920, 1080),
            new(1920, 1200),
            new(2048, 1440),
            new(2560, 1440),
            new(3044, 1722),
            new(3440, 1440),
            new(3840, 2160),
        ];

        // Monotonicity needs a sequence that actually grows in both dimensions.
        // The matrix above deliberately does not: a 3440x1440 ultrawide is wider
        // and shorter than a 3044x1722 window, and comparing those two would
        // measure the order of the list rather than the policy.
        ViewSize[] ladder =
        [
            new(1280, 720),
            new(1600, 900),
            new(1920, 1080),
            new(2560, 1440),
            new(3840, 2160),
        ];

        foreach (var screen in screens)
        {
            var frame = AutomaticFrameSize(screen);
            var scale = AutomaticUiScale(frame);

            if (!AutomaticUiScaleSteps.Contains(scale) ||
                scale < MinimumUiScale ||
                scale > MaximumUiScale)
            {
                throw new InvalidOperationException(
                    $"Automatic UI scale {Format(scale)} for screen {Format(screen)} " +
                    "is not a supported step.");
            }

            if (frame.Width > screen.Width || frame.Height > screen.Height ||
                frame.Width < DesignFrameSize.Width || frame.Height < DesignFrameSize.Height)
            {
                throw new InvalidOperationException(
                    $"Automatic frame {Format(frame)} does not fit screen {Format(screen)} " +
                    "while holding the authored rectangle.");
            }

            if (!FitsLogicalFrame(frame, scale, minimumLogical))
            {
                throw new InvalidOperationException(
                    $"Automatic frame {Format(frame)} at UI scale {Format(scale)} leaves only " +
                    $"{Format(LogicalFrameSize(frame, scale))} logical pixels.");
            }

            var next = AutomaticUiScaleSteps.FirstOrDefault(step => step > scale, double.NaN);
            if (!double.IsNaN(next) &&
                LogicalFrameSize(frame, next) is { } larger &&
                larger.Width >= DesignFrameSize.Width &&
                larger.Height >= DesignFrameSize.Height)
            {
                throw new InvalidOperationException(
                    $"Automatic UI scale {Format(scale)} for frame {Format(frame)} is not the " +
                    "largest step that still shows the authored rectangle.");
            }
        }

        ViewSize? previousFrame = null;
        double? previousScale = null;
        foreach (var screen in ladder)
        {
            var frame = AutomaticFrameSize(screen);
            var scale = AutomaticUiScale(frame);
            if (previousFrame is { } earlierFrame && previousScale is { } earlierScale &&
                (frame.Width < earlierFrame.Width || frame.Height < earlierFrame.Height ||
                 scale < earlierScale))
            {
                throw new InvalidOperationException(
                    $"Screen {Format(screen)} produced frame {Format(frame)} at UI scale " +
                    $"{Format(scale)}, which is smaller than the frame or scale of the smaller " +
                    "screen before it.");
            }

            previousFrame = frame;
            previousScale = scale;
        }

        // The smallest window this policy can open, asked for at the largest
        // scale an explicit --ui-scale may name: 640x360 logical, far under any
        // sane minimum. If this starts fitting, the guard has stopped guarding.
        if (FitsLogicalFrame(DesignFrameSize, MaximumUiScale, minimumLogical))
        {
            throw new InvalidOperationException(
                $"The minimum-logical-frame guard accepted {Format(DesignFrameSize)} at UI scale " +
                $"{Format(MaximumUiScale)}, which leaves less than {Format(minimumLogical)} " +
                "logical pixels.");
        }
    }

    private static string Format(double value) =>
        value.ToString("G17", CultureInfo.InvariantCulture);

    private static string Format(ViewSize size) =>
        $"{Format(size.Width)}x{Format(size.Height)}";

    /// <summary>
    /// Preserves the proportions of world-space primitives authored for the old
    /// 22 px grid while allowing the grid itself to be selected in ADR 0008's
    /// 32–48 px range.
    /// </summary>
    public static double WorldVisualScale(int tileSize) =>
        ValidateTileSize(tileSize) / ReferenceTileSize;

    /// <summary>
    /// How tall a body's sprite canvas is drawn, in world pixels. Two factors,
    /// each answering its own question: <see cref="WorldVisualScale"/> carries
    /// the authored 22 px proportions onto the selected grid, and
    /// <see cref="BodyVisualScale"/> carries the owner's choice of how large a
    /// creature is against that grid. At the shipped 40 px tile that is
    /// <c>20 * 1.70 * 40 / 22 = 61.81…</c> px.
    ///
    /// <para>
    /// The <em>canvas</em> height, not the body's: the connected pack fills 168
    /// of its 192 rows in the tallest state, so the creature itself renders at
    /// about 54.1 px. That is the height
    /// <c>docs/art/goblin-v2-provenance.md</c> says the pack was drawn for.
    /// </para>
    ///
    /// <para>
    /// How tall, not how wide and not where: the width follows the pack's canvas
    /// through <see cref="GoblinDrawWidth"/>, and <see cref="GoblinDrawRect"/>
    /// places the result standing on the line a body's feet have always been
    /// drawn on.
    /// </para>
    /// </summary>
    public static double GoblinDrawSize(int tileSize) =>
        ReferenceGoblinDrawSize * BodyVisualScale * WorldVisualScale(tileSize);

    // -----------------------------------------------------------------------
    // The connected sprite pack, in the three numbers the camera needs from it.
    //
    // These are measurements of goblin_{state}_v2.png, not choices. All six
    // states are RGBA 272x192 and every one of them ends on the same last opaque
    // row y = 187, so the opaque content ends 188 rows down. Re-measured for
    // Issue #77 with the method docs/art/goblin-v2-provenance.md states — alpha
    // channel, support zone 172 <= y <= 187, support centre over alpha > 32 —
    // and recorded in evidence/77-pack-before.json.
    //
    // They live here rather than in the Godot adapter because everything that
    // follows from them is geometry, and geometry in this project is checked
    // without starting the engine (ADR 0011).
    // -----------------------------------------------------------------------

    /// <summary>The width of a state's canvas in the connected pack, in source pixels.</summary>
    public const double SpriteCanvasWidth = 272.0;

    /// <summary>The height of a state's canvas in the connected pack, in source pixels.</summary>
    public const double SpriteCanvasHeight = 192.0;

    /// <summary>
    /// The last opaque row of every state, plus one — i.e. how many rows down the
    /// canvas the opaque content ends.
    /// </summary>
    private const double SpriteSupportRows = 188.0;

    /// <summary>
    /// The shape of a state's canvas: <c>272 / 192 = 17 / 12</c> exactly.
    ///
    /// <para>
    /// Until Issue #77 connected this pack a body was drawn as a square, because
    /// the v1 pack was square. Drawing a 17:12 canvas into a square is not a
    /// smaller mistake than drawing it at the wrong size — it is the same mistake
    /// with the aspect ratio instead of the scale — so the rectangle is derived
    /// from the canvas here rather than written out as 87.55 px anywhere.
    /// </para>
    /// </summary>
    public const double SpriteCanvasAspect = SpriteCanvasWidth / SpriteCanvasHeight;

    /// <summary>
    /// Where the opaque content of a body's sprite ends, as a fraction of its
    /// canvas height — i.e. how far down the canvas the creature's feet are.
    ///
    /// <para>
    /// <b>This is a property of the sprite pack, not of the camera</b>, and it
    /// moved with the pack: the v1 pack ended 92 rows of 96 down, the v2 pack the
    /// runtime now loads ends 188 of 192 down. What it is used for is
    /// <em>placement</em> — <see cref="GoblinDrawRect"/> subtracts this much of
    /// the drawn canvas from the foot line, so that the pixels a player sees as
    /// feet land on that line. Left at 92/96 with a 188/192 pack, every creature
    /// would be drawn <c>61.82 * (188/192 - 92/96) = 1.29</c> px into the ground
    /// at the shipped tile, silently and all at once.
    /// </para>
    ///
    /// <para>
    /// It is deliberately <b>not</b> what <see cref="GoblinFootLine"/> is built
    /// from. Where the ground is, is history; how much transparent tail a pack
    /// leaves under the feet is the pack's business. The two were one expression
    /// while there was only one pack, and connecting the second one is what tells
    /// them apart.
    /// </para>
    /// </summary>
    public const double SpriteSupportFraction = SpriteSupportRows / SpriteCanvasHeight;

    /// <summary>
    /// The support fraction of the <b>v1</b> pack, kept because the ground line
    /// below was measured against it and a measurement does not move when the art
    /// does. All four v1 states had their last opaque row at 91 of 96, read off
    /// the PNGs (<c>y 8..91</c>, <c>y 20..91</c>, <c>y 8..91</c>, <c>y 47..91</c>).
    /// </summary>
    private const double GroundLineSupportFraction = 92.0 / 96.0;

    /// <summary>
    /// The line a body's feet stand on, below its render centre: 16.67 px at the
    /// shipped 40 px tile.
    ///
    /// <para>
    /// It is where the feet of the authored 20-reference-pixel body landed back
    /// when that square was centred on the render point — that is, where every
    /// creature in this game has stood since long before Issue #77 — and it is
    /// deliberately free of <see cref="BodyVisualScale"/>. That independence is
    /// the whole content of the rule: how large a creature is drawn is the owner's
    /// choice, but which ground it stands on is not, so a body may only grow
    /// upward out of this line.
    /// </para>
    ///
    /// <para>
    /// It is free of <see cref="SpriteSupportFraction"/> for the same reason and
    /// since the same issue's second subtask. This number was measured off the v1
    /// pack in the world of 2026-08-01, so it is stated with
    /// <see cref="GroundLineSupportFraction"/>, which cannot change again. Built
    /// from the current pack instead it would have moved 0.76 px the moment the
    /// v2 pack was connected — the ground under every creature in the game
    /// shifting because a canvas got taller, which is exactly what this rule
    /// exists to forbid.
    /// </para>
    /// </summary>
    public static double GoblinFootLine(int tileSize) =>
        ReferenceGoblinDrawSize * WorldVisualScale(tileSize) *
        (GroundLineSupportFraction - 0.5);

    /// <summary>
    /// How wide a body's sprite canvas is drawn, in world pixels: its drawn height
    /// in the shape the pack was authored in. 87.58 px at the shipped 40 px tile.
    /// </summary>
    public static double GoblinDrawWidth(int tileSize) =>
        GoblinDrawSize(tileSize) * SpriteCanvasAspect;

    // -----------------------------------------------------------------------
    // Where a creature can actually have pixels inside that canvas.
    //
    // The canvas is a frame shared by six poses and sized for the widest of
    // them, so it is a poor description of any one creature: `idle` fills 116 of
    // its 272 columns, `combat` reaches 269 because of the spear, and `downed`
    // starts 104 rows down. The union of the six alpha bounds is the smallest
    // box that is guaranteed to contain whatever pose is drawn — columns 26..268
    // and rows 20..187 inclusive, i.e. the exclusive bounds below.
    //
    // Measured, like the canvas above, and recorded with the command in
    // evidence/77-pack-before.json and evidence/77-pack-geometry.json.
    // -----------------------------------------------------------------------

    /// <summary>First column any state has an opaque pixel in: <c>flinch</c>.</summary>
    public const double SpriteOpaqueLeft = 26.0;

    /// <summary>First row any state has an opaque pixel in: <c>idle</c>.</summary>
    public const double SpriteOpaqueTop = 20.0;

    /// <summary>One past the last column any state reaches: <c>combat</c>'s spear.</summary>
    public const double SpriteOpaqueRight = 269.0;

    /// <summary>One past the last row, which every state shares.</summary>
    public const double SpriteOpaqueBottom = 188.0;

    /// <summary>
    /// The part of <see cref="GoblinDrawRect"/> a creature's own pixels can reach,
    /// for a body whose render centre is <paramref name="centre"/>.
    ///
    /// <para>
    /// It exists because the canvas stopped being a usable stand-in for the body
    /// when the pack changed. A check that asks «can this line land on somebody»
    /// has to mean somebody's pixels, and the two answers now differ by 6.44 px at
    /// the top of the canvas alone. What the pack did <em>not</em> change is where
    /// those pixels are: the v1 sheet's body filled 84 of its 96 rows and the v2
    /// canvas fills 168 of 192 — the same 0.875 — so the highest opaque pixel a
    /// creature can have is 37.424242 px above its render centre with either pack,
    /// to the last binary place. Sideways it is a different story and the honest
    /// one: 27.05 px each way with v1, 42.82 with v2, because <c>combat</c> and
    /// <c>windup</c> hold a spear out.
    /// </para>
    /// </summary>
    public static ViewRect GoblinOpaqueRect(ViewPoint centre, int tileSize)
    {
        var canvas = GoblinDrawRect(centre, tileSize);
        return new ViewRect(
            canvas.X + (canvas.Width * (SpriteOpaqueLeft / SpriteCanvasWidth)),
            canvas.Y + (canvas.Height * (SpriteOpaqueTop / SpriteCanvasHeight)),
            canvas.Width * ((SpriteOpaqueRight - SpriteOpaqueLeft) / SpriteCanvasWidth),
            canvas.Height * ((SpriteOpaqueBottom - SpriteOpaqueTop) / SpriteCanvasHeight));
    }

    /// <summary>
    /// The rectangle a body's sprite is drawn into, for a body whose render
    /// centre — the interpolated point the depth pass sorts it by — is
    /// <paramref name="centre"/>. Horizontally centred on that point, and standing
    /// on <see cref="GoblinFootLine"/> below it, so the body grows upward and the
    /// drawn feet do not move at all: 0.000000 px at every tile size, which
    /// <c>CameraViewTests</c> measures rather than asserts in prose.
    ///
    /// <para>
    /// This is the rule spike #142's own scene used — <c>creature_visual.gd</c>:
    /// «<c>position</c> is the creature's feet… the pivot never moves when
    /// <c>scale_multiplier</c> changes the draw size» — so it is also the picture
    /// the owner was looking at when he chose 170 %.
    /// </para>
    ///
    /// <para>
    /// <b>Why the first round of Issue #77 shipped a centred square instead, and
    /// why that was wrong.</b> A centred square grows in all four directions, so
    /// the drawn feet would sink from 16.67 px below the render centre to 28.33 —
    /// 11.67 px, 29 % of a 40 px cell — and land outside the cell the body stands
    /// on. That was accepted because growing upward was believed to undo part of
    /// Issue #156. Re-measurement after review of PR #176 found the cost belonged
    /// to a different rule: anchoring the square's <em>bottom edge</em> where the
    /// old square's bottom edge was reaches 43.64 px above the centre, does
    /// <em>not</em> hold the feet still either (it lifts them 1.06 px, because the
    /// transparent 4/96 grows with the square), and adds 2 crossings on the
    /// shipped map. Anchoring the <em>drawn feet</em>, which is this rule, reaches
    /// 42.58 px and adds none at any tile size. One rule was measured and the
    /// other described; the two are about a pixel apart, and that pixel was the
    /// whole of the argument.
    /// </para>
    ///
    /// <para>
    /// <b>A 17:12 rectangle since the pack it draws became one.</b> The height is
    /// <see cref="GoblinDrawSize"/> and the width is <see cref="GoblinDrawWidth"/>
    /// — 61.82 by 87.58 px at the shipped tile — because the connected canvas is
    /// 272x192 and a canvas drawn into the wrong shape is a stretched creature.
    /// Horizontal centring is what puts the pack's own support centre on the
    /// render point: that centre was placed at canvas <c>x = 135.5</c> in five of
    /// six states and 136.0 in the other two, i.e. on the canvas centre to within
    /// 0.5 of a source pixel, which is 0.16 px of the drawn width.
    /// </para>
    ///
    /// <para>
    /// The vertical placement is unchanged as a rule and changed as a number,
    /// which is the point: <see cref="SpriteSupportFraction"/> is the pack's, so
    /// it became 188/192 with the pack, while <see cref="GoblinFootLine"/> is the
    /// world's and did not move. The body reaches 43.86 px above its render centre
    /// and 17.95 below at tile 40, against 42.58 and 19.24 with the v1 pack.
    /// </para>
    /// </summary>
    public static ViewRect GoblinDrawRect(ViewPoint centre, int tileSize)
    {
        var height = GoblinDrawSize(tileSize);
        var width = GoblinDrawWidth(tileSize);
        var feet = centre.Y + GoblinFootLine(tileSize);
        return new ViewRect(
            centre.X - (width / 2.0),
            feet - (height * SpriteSupportFraction),
            width,
            height);
    }

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
