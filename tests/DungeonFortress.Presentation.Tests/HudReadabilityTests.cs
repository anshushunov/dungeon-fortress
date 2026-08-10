using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The readability policy, and the seam between it and the adapter.
///
/// Issue #86 was reported as "the HUD is unreadable when the window is
/// maximized" and the existing guard disagreed, correctly: every line fitted its
/// rectangle. What nothing measured was how large the text ended up in physical
/// pixels, and there was no frame in any check large enough for the question to
/// arise. Both halves are fixed here rather than in the adapter, so the proof
/// runs on a pull request instead of on the one machine with the large monitor.
/// </summary>
public sealed class HudReadabilityTests
{
    /// <summary>
    /// The HUD as <c>Main.cs</c> authors it today: the four panels, the heading,
    /// the eight legend rows, a toolbar button, a hotkey badge and — since Issue
    /// #127 — the tooltip's title and body. The adapter does not read this list —
    /// it walks its own HUD subtree and passes what it finds — so this is a copy,
    /// and what keeps the copy honest is the negative run
    /// <c>--smoke-hud-readability-regression</c> in the <c>godot</c> stage, which
    /// shrinks a real legend row and requires the engine to exit 1, plus its
    /// tooltip counterpart <c>--smoke-hud-tooltip-readability-regression</c>.
    ///
    /// <para>
    /// Every entry below is a <see cref="HudFontSizes"/> constant rather than a
    /// repeated literal, for the same reason <c>Main.Hud.cs</c> reads them from
    /// there instead of restating them: one number, one place. Issue #352
    /// widened this from the tooltip/toolbar/badge trio to all eleven —
    /// <c>roster</c>, <c>inspector</c> and the eight <c>legend</c> rows used to
    /// stay literal here because the authored text lived in
    /// <c>src/DungeonFortress.Game/Main.Hud.cs</c> outside that Issue's first
    /// partition; the partition was widened to the whole file once the guard's
    /// own fixture — this one — proved a floor the HUD itself failed at four
    /// unrelated call sites was not a floor.
    /// </para>
    ///
    /// The <c>heading</c> row below is why the adapter walks the tree. It used to
    /// list the nodes it held references to, and the heading is a local variable
    /// in <c>CreateSideColumn</c> held by nothing: this fixture claimed a
    /// coverage the adapter did not have, and review found it by re-authoring
    /// that heading at four pixels and watching the run exit 0.
    /// </summary>
    private static readonly HudTextSize[] AuthoredHud =
    [
        new("title", HudFontSizes.TitleFontSize),
        new("summary", HudFontSizes.SummaryFontSize),
        new("roster", HudFontSizes.RosterFontSize),
        new("inspector", HudFontSizes.InspectorFontSize),
        new("feedback", HudFontSizes.FeedbackFontSize),
        new("heading", HudFontSizes.InspectorHeadingFontSize),
        new("legend[0]", HudFontSizes.LegendFontSize),
        new("legend[1]", HudFontSizes.LegendFontSize),
        new("legend[2]", HudFontSizes.LegendFontSize),
        new("legend[3]", HudFontSizes.LegendFontSize),
        new("legend[4]", HudFontSizes.LegendFontSize),
        new("legend[5]", HudFontSizes.LegendFontSize),
        new("legend[6]", HudFontSizes.LegendFontSize),
        new("legend[7]", HudFontSizes.LegendFontSize),
        new("control[inspect]", HudFontSizes.ButtonLabelFontSize),
        new("hotkey[0]", HudFontSizes.HotkeyBadgeFontSize),
        new("tooltip.title", HudFontSizes.TooltipTitleFontSize),
        new("tooltip.body", HudFontSizes.TooltipBodyFontSize),
    ];

    [Fact]
    public void The_authored_HUD_stays_readable_on_every_supported_frame()
    {
        HudReadability.AssertReadable(AuthoredHud);
    }

    [Fact]
    public void The_smallest_authored_text_is_what_the_physical_floor_is_set_to()
    {
        // If the HUD is ever re-authored smaller than the floor, this is the test
        // that says the floor was chosen for a HUD that no longer exists, rather
        // than the guard quietly failing on every frame.
        Assert.Equal(
            HudReadability.MinimumPhysicalTextPixels,
            HudReadability.SmallestPhysicalTextPixels(AuthoredHud, 1.0));
    }

    [Fact]
    public void The_frame_the_defect_was_measured_on_is_refused_at_the_scale_it_was_measured_at()
    {
        var violations = HudReadability.Violations(
            HudReadability.DefectFrame,
            HudReadability.DefectUiScale,
            AuthoredHud);

        Assert.NotEmpty(violations);
        Assert.Contains(violations, message => message.Contains(
            "authored rectangles",
            StringComparison.Ordinal));
        Assert.False(HudReadability.IsReadable(
            HudReadability.DefectFrame,
            HudReadability.DefectUiScale,
            AuthoredHud));
    }

    [Fact]
    public void The_owner_maximized_frame_leaves_no_HUD_text_in_the_eight_to_fifteen_pixel_band()
    {
        // The acceptance criterion of Issue #86, as a number: at 3044x1722 the
        // automatic policy has to reach scale 2, and the smallest authored text
        // has to become at least twice the band the owner could not read. Issue
        // #352 raised the smallest authored size itself from 8 (the legend) to
        // 12 (the floor, now shared by the legend, roster and inspector), so
        // the physical figure here follows: 24 rather than 16.
        var uiScale = CameraView.AutomaticUiScale(HudReadability.DefectFrame);
        Assert.Equal(2.0, uiScale);

        var smallest = HudReadability.SmallestPhysicalTextPixels(AuthoredHud, uiScale);
        Assert.Equal((double)(2 * HudFontSizes.LegendFontSize), smallest);
        Assert.True(
            smallest > 15.0,
            $"The smallest HUD text is {smallest} physical pixels, still inside the 8-15 band.");
        Assert.True(HudReadability.IsReadable(HudReadability.DefectFrame, uiScale, AuthoredHud));
    }

    [Fact]
    public void Every_supported_frame_gets_at_least_the_authored_physical_text_size()
    {
        foreach (var frame in HudReadability.SupportedFrames)
        {
            var uiScale = CameraView.AutomaticUiScale(frame);
            Assert.True(
                HudReadability.SmallestPhysicalTextPixels(AuthoredHud, uiScale) >=
                    HudReadability.MinimumPhysicalTextPixels,
                $"Frame {frame.Width}x{frame.Height} at UI scale {uiScale} drops HUD text under " +
                $"{HudReadability.MinimumPhysicalTextPixels} physical pixels.");
        }
    }

    [Fact]
    public void A_HUD_re_authored_below_the_physical_floor_fails_the_guard()
    {
        // The negative case the policy exists for: nothing about the layout
        // changed, no line is clipped, and the guard has to say no anyway.
        HudTextSize[] shrunk = [.. AuthoredHud, new HudTextSize("legend[0]", 4)];

        var failure = Assert.Throws<InvalidOperationException>(
            () => HudReadability.AssertReadable(shrunk));

        Assert.Contains("legend[0]", failure.Message, StringComparison.Ordinal);
        Assert.Contains("physical pixels", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_density_ceiling_is_derived_from_the_scale_steps_rather_than_chosen()
    {
        // The test independent review asked for, and the one this file was
        // missing. Every other check repeated 1.25 or measured something the
        // number happened to permit, so the constant could be raised anywhere up
        // to 2.378 — the density of the defect frame — with all 347 tests and
        // the whole godot stage still green. Measured, not argued.
        //
        // Computing the value here instead of restating it closes both sides at
        // once: raising the ceiling fails, and changing the scale steps without
        // revisiting the ceiling fails too.
        var ratios = CameraView.AutomaticUiScales
            .Zip(CameraView.AutomaticUiScales.Skip(1), (smaller, larger) => larger / smaller)
            .ToArray();

        Assert.All(ratios, ratio => Assert.True(ratio > 1.0));
        // The analyser wants the constant on the left. The direction of the
        // comparison is not the point: whichever side it sits on, one number is
        // computed from CameraView and the other is the literal under test.
        Assert.Equal(HudReadability.MaximumLogicalDensity, ratios.Max(), 9);
    }

    [Fact]
    public void The_defect_pair_stays_refused_whatever_the_HUD_is_authored_at()
    {
        // AssertReadable ends by requiring the defect pair to still be refused.
        // That is a floor under the rules rather than a pin on them — it only
        // fires once the ceiling is past 2.378 — so it is checked for what it is
        // and not for what the ceiling test above covers.
        Assert.False(HudReadability.IsReadable(
            HudReadability.DefectFrame,
            HudReadability.DefectUiScale,
            [new HudTextSize("enormous", 96)]));
    }

    [Fact]
    public void Density_is_the_frame_measured_in_authored_rectangles_per_unit_of_scale()
    {
        Assert.Equal(1.0, HudReadability.LogicalDensity(CameraView.DesignFrameSize, 1.0));
        Assert.Equal(1.0, HudReadability.LogicalDensity(new ViewSize(1920, 1080), 1.5));
        Assert.Equal(
            2.378125,
            HudReadability.LogicalDensity(HudReadability.DefectFrame, 1.0),
            6);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HudReadability.LogicalDensity(CameraView.DesignFrameSize, 0));
    }

    [Fact]
    public void Readability_cannot_be_asserted_without_measuring_anything()
    {
        // An empty measurement would pass every frame, which is the failure mode
        // Issue #72 named for causal pairs and Issue #86 named for this number.
        //
        // The guard itself is the one that has to say no. Until review found it,
        // only the structured-output builder did: AssertReadable returned quietly
        // on an empty list, because nothing to measure produces no violations and
        // the negative half refuses the defect pair on density alone. _Ready would
        // have reported "ok" and the complaint would have arrived later, from a
        // different method, about a different thing.
        Assert.Throws<ArgumentException>(() => HudReadability.AssertReadable([]));
        Assert.Throws<ArgumentException>(
            () => HudReadability.SmallestPhysicalTextPixels([], 1.0));
    }

    [Fact]
    public void The_adapter_measures_its_own_fonts_and_hands_them_to_the_policy()
    {
        // The seam ADR 0011 asks for: the decision is in Presentation, and what
        // is left in the adapter is a measurement and a call. Checked as
        // structure, because no test project may reference the engine assembly.
        var ready = AdapterSource.Body("_Ready");
        Assert.Single(AdapterSource.CallsTo(ready, "AssertHudTextReadable"));

        var guard = AdapterSource.Body("AssertHudTextReadable");
        Assert.Contains("HudReadability.AssertReadable", guard, StringComparison.Ordinal);
        Assert.Contains("HudTextSizes", guard, StringComparison.Ordinal);

        // The measurement walks the HUD subtree rather than listing the nodes
        // this file keeps a reference to. A list can only be as complete as the
        // last person to remember it, and review proved that: the inspector
        // heading is held by nothing and was invisible to the guard.
        var measurement = AdapterSource.Body("HudTextSizes");
        Assert.Single(AdapterSource.CallsTo(measurement, "CollectHudTextSizes"));

        var walk = AdapterSource.Body("CollectHudTextSizes");
        Assert.Contains("GetThemeFontSize", walk, StringComparison.Ordinal);
        Assert.Contains("GetChildren", walk, StringComparison.Ordinal);
        // Depth first: the recursive call is what reaches a label nested inside
        // a panel inside a column, which is where every one of them lives.
        Assert.Single(AdapterSource.CallsTo(walk, "CollectHudTextSizes"));
    }

    [Fact]
    public void The_inert_strict_hud_fit_flag_is_gone_from_the_adapter()
    {
        // Issue #49: the flag was parsed and ignored for three Issues. The raw
        // source is read rather than AdapterSource.Masked, because the flag only
        // ever appeared inside a string literal and masking would hide it — and
        // the closing quote is part of the pattern for the same reason, so that
        // the doc comment recording why the flag is gone does not count as the
        // flag being back.
        var source = AdapterSource.Raw;

        Assert.DoesNotContain("\"--strict-hud-fit\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("strictHudFit", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_ = strict",
            AdapterSource.Body("AssertLabelsFit"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutHud_applies_the_live_UI_scale_to_every_tooltip_button()
    {
        // HudFontSizesTests proves the arithmetic HudFontSizes.ScaledSize does is
        // correct in isolation, but nothing proved LayoutHud still feeds it a live
        // number: delete this loop and every HudButton.UiScale stays at its
        // default 1.0 forever, which is Issue #127 verbatim, and every other
        // check here — including the 599 tests and both negative --smoke flags —
        // stays green, because they all exercise the arithmetic and not its
        // wiring. Same technique as
        // CameraViewTests.The_player_keeps_a_zoom_they_chose_and_the_HUD_scale_keeps_following_the_window
        // (CameraViewTests.cs), which proves OnViewportResized still calls
        // CameraView.AutomaticUiScale rather than a constant.
        var layoutHud = AdapterSource.Body("LayoutHud");
        Assert.Contains("button.UiScale = uiScale", layoutHud, StringComparison.Ordinal);
    }

    [Fact]
    public void MakeCustomTooltip_reads_the_live_UI_scale_rather_than_a_fixed_one()
    {
        // The other half of the same gap: AdapterSource only reads Main.cs (ADR
        // 0011 — no test project references DungeonFortress.Game), so it cannot
        // see HudButton.cs. Read raw, the same way
        // The_inert_strict_hud_fit_flag_is_gone_from_the_adapter reads Main.cs raw
        // instead of through AdapterSource.Masked. If _MakeCustomTooltip were
        // rewritten to call BuildTooltip(forText, 1.0) — a fixed baseline instead
        // of the button's own live scale — Issue #127 returns exactly, and only
        // this assertion would notice.
        var path = Path.Combine(
            PresentationFixtures.FindRepositoryRoot(), "src", "DungeonFortress.Game", "HudButton.cs");
        var source = File.ReadAllText(path);

        Assert.Contains(
            "_MakeCustomTooltip(string forText) => BuildTooltip(forText, UiScale)",
            source,
            StringComparison.Ordinal);
    }
}
