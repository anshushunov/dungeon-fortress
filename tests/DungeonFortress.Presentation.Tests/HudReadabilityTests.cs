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
    /// the eight legend rows, a toolbar button and a hotkey badge. The adapter
    /// does not read this list — it measures its own labels and passes those —
    /// so this is a copy, and what keeps the copy honest is the negative run
    /// <c>--smoke-hud-readability-regression</c> in the <c>godot</c> stage, which
    /// shrinks a real legend row and requires the engine to exit 1.
    /// </summary>
    private static readonly HudTextSize[] AuthoredHud =
    [
        new("title", 15),
        new("summary", 12),
        new("roster", 10),
        new("inspector", 11),
        new("feedback", 12),
        new("heading", 13),
        new("legend[0]", 9),
        new("legend[1]", 8),
        new("legend[2]", 8),
        new("legend[3]", 8),
        new("legend[4]", 8),
        new("legend[5]", 8),
        new("legend[6]", 8),
        new("legend[7]", 8),
        new("control[inspect]", 10),
        new("hotkey[0]", 8),
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
        // automatic policy has to reach scale 2, and 8 px legend rows have to
        // become 16 physical pixels rather than staying in the band the owner
        // could not read.
        var uiScale = CameraView.AutomaticUiScale(HudReadability.DefectFrame);
        Assert.Equal(2.0, uiScale);

        var smallest = HudReadability.SmallestPhysicalTextPixels(AuthoredHud, uiScale);
        Assert.Equal(16.0, smallest);
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
    public void A_density_ceiling_that_stopped_refusing_the_defect_would_fail_the_guard()
    {
        // AssertReadable ends by requiring the defect pair to still be refused,
        // which is what stops the ceiling from being relaxed into decoration.
        // Nothing in a test can move a const, so the claim is checked directly:
        // the pair is unreadable no matter what text is measured.
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

        var measurement = AdapterSource.Body("HudTextSizes");
        Assert.Contains("GetThemeFontSize", measurement, StringComparison.Ordinal);
    }

    [Fact]
    public void The_inert_strict_hud_fit_flag_is_gone_from_the_adapter()
    {
        // Issue #49: the flag was parsed and ignored for three Issues. The raw
        // file is read rather than AdapterSource.Masked, because the flag only
        // ever appeared inside a string literal and masking would hide it — and
        // the closing quote is part of the pattern for the same reason, so that
        // the doc comment recording why the flag is gone does not count as the
        // flag being back.
        var source = File.ReadAllText(AdapterSource.FullPath());

        Assert.DoesNotContain("\"--strict-hud-fit\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("strictHudFit", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_ = strict",
            AdapterSource.Body("AssertLabelsFit"),
            StringComparison.Ordinal);
    }
}
