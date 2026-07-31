using System.Globalization;

namespace DungeonFortress.Presentation;

/// <summary>
/// One piece of HUD text as the layout authors it: a name a failure can point
/// at, and the font size the label is given in logical pixels. What a player
/// actually reads is this multiplied by the UI scale, and the gap between those
/// two numbers is the whole of Issue #86.
/// </summary>
public readonly record struct HudTextSize(string Name, double LogicalPixels);

/// <summary>
/// Whether a frame is readable, as arithmetic rather than as an opinion.
///
/// <para>
/// The HUD overflow guard in the Godot adapter answers "is any text cut off",
/// and on the frame Issue #86 was opened about the answer was no: at 3044x1722
/// with UI scale 1 every line fitted its rectangle and the legend was drawn at
/// eight physical pixels. Fitting and being readable are different questions,
/// and only the first one had a check.
/// </para>
///
/// <para>
/// Two rules answer the second one, and they fail in different directions:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>A physical floor.</b> No HUD text may end up smaller than the smallest
/// size the HUD is authored with at the frame it is authored for. This is what
/// re-authoring a legend row at six pixels trips, and it is measured from the
/// live labels rather than from a constant repeated here.
/// </item>
/// <item>
/// <b>A density ceiling.</b> <see cref="LogicalDensity"/> is how many authored
/// rectangles the frame is worth divided by how far the HUD was scaled up. One
/// means the HUD occupies exactly the share of the frame it was authored to
/// occupy; the 3044x1722 defect measured 2.38. Past
/// <see cref="CameraView.MaximumUiScale"/> the scale cannot rise any further,
/// so a frame that reached the ceiling is excused by name instead of silently.
/// </item>
/// </list>
///
/// <para>
/// Deliberately engine-free, per ADR 0011: the adapter measures the fonts and
/// calls this, so the proof runs on a pull request rather than only on the
/// machine with the large monitor.
/// </para>
/// </summary>
public static class HudReadability
{
    /// <summary>
    /// The smallest text the HUD is authored with, on the frame it is authored
    /// for: the 8 px legend rows at 1280x720 and UI scale 1. It is a floor, not
    /// a target — it says automatic scaling may never make text smaller than
    /// the baseline, and it is deliberately not larger, because raising it
    /// would fail the authored frame itself.
    /// </summary>
    public const double MinimumPhysicalTextPixels = 8.0;

    /// <summary>
    /// How much denser than the authored rectangle a frame may be before its
    /// HUD stops being readable. 1.25 is not a taste: it is the largest ratio
    /// between two neighbouring steps of <see cref="CameraView.AutomaticUiScales"/>,
    /// so a policy that always picks the largest step a frame allows can never
    /// exceed it — and a change to those steps that breaks the property is a
    /// failure rather than a surprise on someone's monitor.
    /// </summary>
    public const double MaximumLogicalDensity = 1.25;

    /// <summary>
    /// The frames this policy is proven over: the displays in
    /// <see cref="CameraView.AssertStartupFramePolicy"/> plus the two window
    /// sizes Issue #86 was actually measured at. A size missing here is not
    /// unsupported, it is unmeasured.
    /// </summary>
    private static readonly ViewSize[] SupportedFrameMatrix =
    [
        new(1280, 720),
        new(1366, 768),
        new(1440, 900),
        new(1600, 900),
        new(1920, 1080),
        new(1920, 1200),
        new(2048, 1440),
        new(2560, 1440),
        // The owner's maximized window, and the client area a review measured
        // on the same screen. Both are Issue #86 verbatim.
        new(3044, 1722),
        new(3072, 1779),
        new(3440, 1440),
        new(3840, 2160),
    ];

    public static IReadOnlyList<ViewSize> SupportedFrames => SupportedFrameMatrix;

    /// <summary>
    /// The pair that opened Issue #86: the owner's maximized client area with
    /// the UI scale the launcher used to leave it at. It is kept next to the
    /// policy because it is what the policy has to say no to, and a guard that
    /// has never been seen refusing anything is not evidence.
    /// </summary>
    public static readonly ViewSize DefectFrame = new(3044, 1722);

    public const double DefectUiScale = 1.0;

    public static double PhysicalTextPixels(double logicalPixels, double uiScale) =>
        logicalPixels * uiScale;

    /// <summary>
    /// How many authored rectangles this frame is worth, divided by how far the
    /// HUD was scaled up to cover them. Below one the HUD is larger than
    /// authored, which is a small window rather than a defect; above one it is
    /// denser, and how far above is exactly how much smaller everything reads.
    /// </summary>
    public static double LogicalDensity(ViewSize frame, double uiScale)
    {
        if (uiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(uiScale),
                uiScale,
                "UI scale must be positive.");
        }

        var fit = Math.Min(
            frame.Width / CameraView.DesignFrameSize.Width,
            frame.Height / CameraView.DesignFrameSize.Height);
        return fit / uiScale;
    }

    public static double SmallestPhysicalTextPixels(
        IReadOnlyList<HudTextSize> text,
        double uiScale)
    {
        if (text.Count == 0)
        {
            throw new ArgumentException(
                "Readability is measured from the HUD's own labels; an empty list " +
                "would make every frame pass.",
                nameof(text));
        }

        return text.Min(entry => PhysicalTextPixels(entry.LogicalPixels, uiScale));
    }

    /// <summary>
    /// Everything wrong with one frame and UI scale, as sentences a run can
    /// print. Empty means readable.
    /// </summary>
    public static IReadOnlyList<string> Violations(
        ViewSize frame,
        double uiScale,
        IReadOnlyList<HudTextSize> text)
    {
        var failures = new List<string>();
        foreach (var entry in text)
        {
            var physical = PhysicalTextPixels(entry.LogicalPixels, uiScale);
            if (physical + 1e-9 < MinimumPhysicalTextPixels)
            {
                failures.Add(
                    $"'{entry.Name}' is drawn at {Format(physical)} physical pixels on frame " +
                    $"{Format(frame)} at UI scale {Format(uiScale)}, under the " +
                    $"{Format(MinimumPhysicalTextPixels)} px floor");
            }
        }

        var density = LogicalDensity(frame, uiScale);
        // A frame past the largest supported scale has nothing left to give, so
        // it is excused by name. Below that ceiling the excuse does not apply
        // and the ratio is the rule.
        if (density > MaximumLogicalDensity + 1e-9 &&
            uiScale + 1e-9 < CameraView.MaximumUiScale)
        {
            failures.Add(
                $"frame {Format(frame)} at UI scale {Format(uiScale)} packs " +
                $"{Format(density)} authored rectangles into the HUD, over the " +
                $"{Format(MaximumLogicalDensity)} ceiling, while the scale could still rise " +
                $"to {Format(CameraView.MaximumUiScale)}");
        }

        return failures;
    }

    public static bool IsReadable(ViewSize frame, double uiScale, IReadOnlyList<HudTextSize> text) =>
        Violations(frame, uiScale, text).Count == 0;

    /// <summary>
    /// Proves that the automatic scale keeps <paramref name="text"/> readable on
    /// every frame in <see cref="SupportedFrames"/>, and that the same rules
    /// still refuse the pair Issue #86 was opened about.
    ///
    /// <para>
    /// It takes the measured font sizes rather than a copy of them, so it reacts
    /// to a change in the HUD and not only to a change in this file. The Godot
    /// adapter calls it on every entry point; a unit test calls it directly.
    /// </para>
    /// </summary>
    public static void AssertReadable(IReadOnlyList<HudTextSize> text)
    {
        var failures = new List<string>();
        foreach (var frame in SupportedFrameMatrix)
        {
            var uiScale = CameraView.AutomaticUiScale(frame);
            failures.AddRange(Violations(frame, uiScale, text));
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"HUD text is unreadable in {failures.Count} place(s): " +
                string.Join("; ", failures) +
                ". Text that formally fits its rectangle can still be too small to read, " +
                "which is what Issue #86 measured at 8-15 physical pixels.");
        }

        // The negative half, in the same call, for the same reason
        // CameraView.AssertStartupFramePolicy ends with one: a rule that has
        // never refused anything cannot be told apart from a rule that cannot.
        if (IsReadable(DefectFrame, DefectUiScale, text))
        {
            throw new InvalidOperationException(
                $"The readability rules accepted frame {Format(DefectFrame)} at UI scale " +
                $"{Format(DefectUiScale)}, which is the pair Issue #86 was opened about. " +
                "A guard that accepts the defect it was written for is not a guard.");
        }
    }

    private static string Format(double value) =>
        value.ToString("G17", CultureInfo.InvariantCulture);

    private static string Format(ViewSize size) =>
        $"{Format(size.Width)}x{Format(size.Height)}";
}
