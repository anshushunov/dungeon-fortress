using Xunit;

namespace DungeonFortress.Presentation.Tests;

public sealed class ViewLaunchOptionsTests
{
    private static readonly string[] ExplicitCapture =
    [
        "--tile-size", "40",
        "--camera-zoom", "0.75",
        "--camera-position", "560,320",
        "--ui-scale", "1.25",
        "--frame-size", "1600x900",
    ];

    [Fact]
    public void A_capture_parses_every_pixel_affecting_parameter()
    {
        var options = ViewLaunchOptions.Parse(ExplicitCapture, requireExplicitCaptureParameters: true);

        Assert.Equal(40, options.TileSize);
        Assert.Equal(0.75, options.CameraZoom);
        Assert.Equal(new ViewPoint(560, 320), options.CameraPosition);
        Assert.Equal(1.25, options.UiScale);
        Assert.Equal(new ViewSize(1600, 900), options.FrameSize);
    }

    [Fact]
    public void An_interactive_run_has_deterministic_view_defaults()
    {
        var options = ViewLaunchOptions.Parse([], requireExplicitCaptureParameters: false);

        Assert.Equal(CameraView.DefaultTileSize, options.TileSize);
        Assert.Equal(CameraView.DefaultZoom, options.CameraZoom);
        Assert.Equal(CameraView.MapCenter(CameraView.DefaultTileSize), options.CameraPosition);
        Assert.Equal(CameraView.DefaultUiScale, options.UiScale);
        Assert.Null(options.FrameSize);
    }

    [Theory]
    [InlineData("--tile-size")]
    [InlineData("--camera-zoom")]
    [InlineData("--camera-position")]
    [InlineData("--ui-scale")]
    [InlineData("--frame-size")]
    public void A_capture_refuses_to_inherit_a_missing_frame_parameter(string missing)
    {
        var arguments = ExplicitCapture
            .Where((_, index) =>
            {
                var parameterIndex = Array.IndexOf(ExplicitCapture, missing);
                return index != parameterIndex && index != parameterIndex + 1;
            })
            .ToArray();

        var failure = Assert.Throws<ArgumentException>(
            () => ViewLaunchOptions.Parse(arguments, requireExplicitCaptureParameters: true));

        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("31")]
    [InlineData("49")]
    public void Tile_size_outside_the_ADR_range_is_rejected(string value)
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => ViewLaunchOptions.Parse(["--tile-size", value], false));

        Assert.Equal("tileSize", failure.ParamName);
    }

    [Theory]
    [InlineData("0.6")]
    [InlineData("1.1")]
    [InlineData("3")]
    public void Arbitrary_zoom_is_rejected(string value)
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => ViewLaunchOptions.Parse(["--camera-zoom", value], false));

        Assert.Equal("zoom", failure.ParamName);
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("2.1")]
    [InlineData("NaN")]
    public void Ui_scale_outside_the_supported_range_is_rejected(string value)
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => ViewLaunchOptions.Parse(["--ui-scale", value], false));

        Assert.Equal("uiScale", failure.ParamName);
    }

    [Theory]
    [InlineData("x,1")]
    [InlineData("1")]
    [InlineData("NaN,1")]
    public void Camera_position_requires_two_finite_coordinates(string value)
    {
        var failure = Assert.Throws<ArgumentException>(
            () => ViewLaunchOptions.Parse(["--camera-position", value], false));

        Assert.Equal("--camera-position", failure.ParamName);
    }

    [Theory]
    [InlineData("0x720")]
    [InlineData("1280x0")]
    [InlineData("1280")]
    [InlineData("widex720")]
    [InlineData("1280.5x720")]
    public void Frame_size_requires_two_positive_integer_dimensions(string value)
    {
        var failure = Assert.Throws<ArgumentException>(
            () => ViewLaunchOptions.Parse(["--frame-size", value], false));

        Assert.Equal("--frame-size", failure.ParamName);
    }

    [Fact]
    public void Ui_scale_is_rejected_when_the_declared_frame_cannot_hold_the_required_logical_HUD()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => ViewLaunchOptions.Parse(
                ["--ui-scale", "2", "--frame-size", "1280x720"],
                requireExplicitCaptureParameters: false));

        Assert.Equal("--ui-scale", failure.ParamName);
        Assert.Contains("640x360 logical pixels", failure.Message, StringComparison.Ordinal);
        Assert.Contains("1024x720", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ui_scale_two_is_supported_when_the_frame_preserves_the_logical_HUD()
    {
        var options = ViewLaunchOptions.Parse(
            ["--ui-scale", "2", "--frame-size", "2048x1440"],
            requireExplicitCaptureParameters: false);

        Assert.Equal(2, options.UiScale);
        Assert.Equal(new ViewSize(2048, 1440), options.FrameSize);
    }
}
