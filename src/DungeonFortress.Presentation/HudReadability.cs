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
    /// The smallest a player can actually read without leaning at the screen,
    /// rather than the smallest size the HUD happened to be authored with.
    ///
    /// <para>
    /// Issue #352: the previous value, 8, was exactly the smallest authored
    /// size (the legend rows), by definition — so the guard could never catch
    /// anything smaller than whatever the HUD already did, including the
    /// tooltip body the owner reported as unreadable at 10 px on the playtest
    /// of slice 3 (2026-08-09). Raising the floor to a size nothing was
    /// authored at yet is the point: <b>this constant no longer promises to
    /// equal today's smallest label</b>, and a HUD that has not caught up is
    /// exactly what is supposed to fail until it does.
    /// </para>
    ///
    /// <para>
    /// 12 is the owner's choice among three candidates named on the frame the
    /// defect was found on — 10, 12, 14 — with the price stated alongside it:
    /// a one-and-a-half-times rise from the previous floor, without
    /// re-laying-out the HUD, leaving the width guard of Issue #106 in force.
    /// Decision recorded 2026-08-10 in <c>docs/product/GATE_DECISIONS.md</c>.
    /// </para>
    ///
    /// <para>
    /// Applies to every HUD surface except <see cref="LegendReadabilityExemption"/>
    /// — see that member for why the legend alone does not have to clear this
    /// floor, and for why the exception is a named set rather than a second,
    /// softer rule.
    /// </para>
    /// </summary>
    public const double MinimumPhysicalTextPixels = 12.0;

    /// <summary>
    /// The one named exception to <see cref="MinimumPhysicalTextPixels"/>: the
    /// map legend's eight rows, by name, and nothing else.
    ///
    /// <para>
    /// <b>Decided by the owner, 2026-08-10 (Issue #352).</b> The 12 px floor
    /// covers everything a player reads while deciding something mid-party —
    /// tooltips, hotkeys, the roster, the inspector and its heading, the
    /// feedback line, the title, the summary. The legend is a symbol
    /// reference, read once at the start of a party and consulted for nothing
    /// a player decides afterward, so it was kept at its pre-#352 size instead
    /// of paying the one-and-a-half-times height growth 12 px would have cost
    /// it. Three alternatives were named and rejected with their own price:
    /// moving the legend behind a hotkey (new state a first-time player would
    /// not discover on their own); cutting it to five or six rows (every row
    /// belongs to Issues #52, #222 or #86, each for its own reason, and none
    /// of those reasons went away); a 10 px floor for everything (never
    /// measured to fit, and the owner had already named 12).
    /// </para>
    ///
    /// <para>
    /// <b>Named set, not a softer rule.</b> "Reference text may be smaller
    /// than the floor" cannot be checked by a machine — <c>reference</c> is a
    /// judgment call, and code that accepted it would accept it for anything
    /// the next author called reference. A set this class's own tests assert
    /// the exact membership of is the only shape that stays an exception
    /// instead of becoming a hole in the floor: <c>HudReadabilityTests</c>
    /// pins the count and every name, and reds if the legend's own row count
    /// changes without this set changing to match, or if anything that is not
    /// a legend row — <c>feedback</c> named literally, because that is the
    /// mutant the exception exists to catch — is ever added to it.
    /// </para>
    ///
    /// <para>
    /// <b>Exempt from <see cref="MinimumPhysicalTextPixels"/>, not from every
    /// floor.</b> A name in this set is held to
    /// <see cref="LegendReadabilityFloor"/> instead — the legend's own
    /// pre-#352 authored size, which it must still not be shrunk below. The
    /// first version of this exception skipped the floor check outright for
    /// these names, and that quietly disarmed Issue #86's own regression
    /// proof: <c>InjectHudReadabilityRegression</c> in
    /// <c>Main.Session.cs</c> re-authors <c>legend[0]</c> at 4 px specifically
    /// to prove the guard still says no to that, and a guard that no longer
    /// checks the name at all cannot. <c>Every_supported_frame_...</c> and
    /// <c>A_legend_row_re_authored_below_its_own_floor_still_fails_the_guard</c>
    /// in <c>HudReadabilityTests</c> are what would have caught it, had they
    /// existed before the mistake did.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> LegendReadabilityExemption = new HashSet<string>(
        ["legend[0]", "legend[1]", "legend[2]", "legend[3]", "legend[4]", "legend[5]", "legend[6]", "legend[7]"],
        StringComparer.Ordinal);

    /// <summary>
    /// The floor a <see cref="LegendReadabilityExemption"/> row is still held
    /// to, in place of <see cref="MinimumPhysicalTextPixels"/>: its own
    /// pre-#352 authored size, read from <see cref="HudFontSizes.LegendFontSize"/>
    /// rather than repeated as a second 8 — the same "one number, one place"
    /// this exception's own doc comment insists on for its membership. The
    /// reference runs the opposite direction from <c>HudFontSizes</c>'
    /// <c>HotkeyBadgeFontSize</c>, which reads <see cref="MinimumPhysicalTextPixels"/>
    /// from here; both are compile-time constants resolved against a literal
    /// in the other file, not against each other, so nothing about this is
    /// circular — only mutual, in the way two doors can each open onto the
    /// same room.
    /// </summary>
    public const double LegendReadabilityFloor = HudFontSizes.LegendFontSize;

    /// <summary>
    /// How much denser than the authored rectangle a frame may be before its
    /// HUD stops being readable. 1.25 is not a taste: it is the largest ratio
    /// between two neighbouring steps of <see cref="CameraView.AutomaticUiScales"/>,
    /// so a policy that always picks the largest step a frame allows can never
    /// exceed it — and a change to those steps that breaks the property is a
    /// failure rather than a surprise on someone's monitor.
    ///
    /// <para>
    /// It is a literal because a <c>const</c> cannot be computed from another
    /// type's array, and a literal is a number somebody can quietly raise. What
    /// stops that is <c>HudReadabilityTests</c>, which derives the same value
    /// from the steps and compares. Independent review measured the hole that
    /// closes: the runtime self-check below only refuses a ceiling above the
    /// 2.378 density of the defect frame, so 1.25 could have drifted anywhere up
    /// to that with every test and every stage still green.
    /// </para>
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
            var isExemptLegendRow = LegendReadabilityExemption.Contains(entry.Name);
            var floor = isExemptLegendRow ? LegendReadabilityFloor : MinimumPhysicalTextPixels;
            if (physical + 1e-9 < floor)
            {
                var floorDescription = isExemptLegendRow
                    ? $"{Format(floor)} px legend floor (Issue #352's named exception from the " +
                        "general floor, not an exemption from every floor)"
                    : $"{Format(floor)} px floor";
                failures.Add(
                    $"'{entry.Name}' is drawn at {Format(physical)} physical pixels on frame " +
                    $"{Format(frame)} at UI scale {Format(uiScale)}, under the {floorDescription}");
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
    ///
    /// <para>
    /// An empty measurement is refused rather than passed. Nothing to measure
    /// produces no violations, and the negative half below would still refuse
    /// the defect pair on density alone, so the whole call would return quietly
    /// having checked nothing. It became reachable when the adapter started
    /// walking the HUD subtree instead of listing named fields: a fuller source
    /// is also one that can empty out all at once, if the HUD is ever reparented
    /// away from the root the walk starts at.
    /// </para>
    /// </summary>
    public static void AssertReadable(IReadOnlyList<HudTextSize> text)
    {
        if (text.Count == 0)
        {
            throw new ArgumentException(
                "Readability is measured from the HUD's own labels; an empty measurement " +
                "would make every frame pass.",
                nameof(text));
        }

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
        //
        // It is a floor under the rules, not a pin on them. It fires only once
        // the ceiling has been relaxed past this frame's own density of 2.378,
        // so it does not by itself keep MaximumLogicalDensity honest — that is
        // the unit test which derives the number from the scale steps.
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
