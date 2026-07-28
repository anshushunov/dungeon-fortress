using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The command line every capture, smoke run and golden UI frame is driven by.
/// A typo here used to be observable only as a Godot process exiting with 1.
/// </summary>
public sealed class CommandLineArgumentTests
{
    private static readonly string[] Sample =
    [
        "--smoke", "--fixture", "baseline", "--demo-stone",
        "--screenshot-ticks", "190", "--select-cell", "25,3",
    ];

    [Fact]
    public void A_flag_that_is_present_yields_the_value_after_it()
    {
        Assert.Equal("baseline", CommandLineArguments.Read(Sample, "--fixture"));
        Assert.Equal("25,3", CommandLineArguments.Read(Sample, "--select-cell"));
        Assert.Equal(190, CommandLineArguments.ReadInt(Sample, "--screenshot-ticks"));
    }

    [Fact]
    public void An_absent_flag_yields_null_so_the_caller_can_pick_its_own_default()
    {
        Assert.Null(CommandLineArguments.Read(Sample, "--screenshot"));
        Assert.Null(CommandLineArguments.ReadInt(Sample, "--select-creature"));
    }

    /// <summary>
    /// A prefix must not match: <c>--select-cell</c> and <c>--select-creature</c>
    /// live on the same command line.
    /// </summary>
    [Fact]
    public void Matching_is_exact_rather_than_by_prefix()
    {
        string[] arguments = ["--select-creature", "8", "--select-cell", "25,3"];

        Assert.Equal("8", CommandLineArguments.Read(arguments, "--select-creature"));
        Assert.Equal("25,3", CommandLineArguments.Read(arguments, "--select-cell"));
        Assert.Null(CommandLineArguments.Read(arguments, "--select"));
    }

    /// <summary>
    /// A typed command line with a dangling flag is a mistake, not a default.
    /// </summary>
    [Fact]
    public void A_flag_with_nothing_after_it_is_an_error()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => CommandLineArguments.Read(["--smoke", "--fixture"], "--fixture"));

        Assert.Contains("Missing value after --fixture.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_occurrence_wins_so_a_repeated_flag_is_not_silently_reordered()
    {
        Assert.Equal(
            "baseline",
            CommandLineArguments.Read(["--fixture", "baseline", "--fixture", "neglected"], "--fixture"));
    }

    [Fact]
    public void A_cell_inside_the_map_parses_to_that_cell()
    {
        Assert.Equal(new GridPoint(25, 3), CommandLineArguments.ParseCell("25,3"));
        Assert.Equal(new GridPoint(0, 0), CommandLineArguments.ParseCell("0,0"));
        Assert.Equal(
            new GridPoint(PrototypeTuning.MapWidth - 1, PrototypeTuning.MapHeight - 1),
            CommandLineArguments.ParseCell(
                $"{PrototypeTuning.MapWidth - 1},{PrototypeTuning.MapHeight - 1}"));
    }

    /// <summary>
    /// A cell off the map is rejected rather than clamped, so a capture can never
    /// silently inspect a different tile than the one that was asked for.
    /// </summary>
    [Theory]
    [InlineData("-1,0")]
    [InlineData("0,-1")]
    [InlineData("28,0")]
    [InlineData("0,16")]
    [InlineData("25")]
    [InlineData("25,3,1")]
    [InlineData("x,3")]
    [InlineData("25,y")]
    [InlineData("")]
    public void A_cell_that_is_not_on_the_map_is_refused_by_name(string value)
    {
        var failure = Assert.Throws<ArgumentException>(
            () => CommandLineArguments.ParseCell(value));

        Assert.Equal("--select-cell", failure.ParamName);
        Assert.Contains(
            $"--select-cell expects X,Y inside the map, got '{value}'.",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_map_bounds_are_taken_from_tuning_rather_than_from_a_copy()
    {
        Assert.True(MapBounds.Contains(new GridPoint(0, 0)));
        Assert.True(MapBounds.Contains(
            new GridPoint(PrototypeTuning.MapWidth - 1, PrototypeTuning.MapHeight - 1)));
        Assert.False(MapBounds.Contains(new GridPoint(PrototypeTuning.MapWidth, 0)));
        Assert.False(MapBounds.Contains(new GridPoint(0, PrototypeTuning.MapHeight)));
        Assert.False(MapBounds.Contains(new GridPoint(-1, -1)));
    }
}
