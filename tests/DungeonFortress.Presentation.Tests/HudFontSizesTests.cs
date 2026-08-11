using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #127: the tooltip body was authored smaller than the button label it
/// explains, and neither its font sizes nor its width grew with the rest of
/// the interface. Both halves are pure arithmetic and proven here without an
/// engine; <see cref="HudReadabilityTests"/> covers the guard that now
/// measures the tooltip alongside every other HUD text surface.
/// </summary>
public sealed class HudFontSizesTests
{
    [Fact]
    public void The_tooltip_body_is_not_smaller_than_the_button_label_it_explains()
    {
        // The mutation this test exists to catch: HudFontSizes.TooltipBodyFontSize
        // set back to 9 (its pre-#127 value) fails this immediately, without
        // starting Godot.
        Assert.True(
            HudFontSizes.TooltipBodyFontSize >= HudFontSizes.ButtonLabelFontSize,
            $"Tooltip body is {HudFontSizes.TooltipBodyFontSize}px, button label is " +
            $"{HudFontSizes.ButtonLabelFontSize}px: the text explaining the button is " +
            "smaller than the button itself.");
    }

    [Fact]
    public void The_tooltip_title_is_larger_than_its_own_body()
    {
        Assert.True(HudFontSizes.TooltipTitleFontSize > HudFontSizes.TooltipBodyFontSize);
    }

    [Theory]
    [InlineData(1.0, 12)]
    [InlineData(1.5, 18)]
    [InlineData(2.0, 24)]
    public void Scaled_size_grows_exactly_with_UI_scale(double uiScale, int expected)
    {
        // 1.0, 1.5 and 2.0 are three of the five steps CameraView.AutomaticUiScales
        // exposes ([1.0, 1.25, 1.5, 1.75, 2.0]) and the pair Issue #127 named
        // explicitly (the owner's window reaches 1.5 and 2.0 automatically; see
        // evidence/127-tooltip-scale.json). Expected values follow
        // TooltipBodyFontSize, raised from 10 to 12 by Issue #352.
        Assert.Equal(expected, HudFontSizes.ScaledSize(HudFontSizes.TooltipBodyFontSize, uiScale));
    }

    [Fact]
    public void A_larger_UI_scale_never_produces_a_smaller_physical_size()
    {
        // The mutation this test exists to catch: a ScaledSize that ignores
        // uiScale and always returns authoredSize would pass every other test
        // in this file at scale 1 alone. This is the one that needs more than
        // one scale to fail.
        var atOne = HudFontSizes.ScaledSize(HudFontSizes.TooltipBodyFontSize, 1.0);
        var atOneAndAHalf = HudFontSizes.ScaledSize(HudFontSizes.TooltipBodyFontSize, 1.5);
        var atTwo = HudFontSizes.ScaledSize(HudFontSizes.TooltipBodyFontSize, 2.0);

        Assert.True(atOneAndAHalf > atOne);
        Assert.True(atTwo > atOneAndAHalf);
    }

    [Fact]
    public void Scaled_size_refuses_a_non_positive_scale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HudFontSizes.ScaledSize(10, 0));
    }
}
