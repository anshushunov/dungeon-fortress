using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// <see cref="MapBounds.Gate"/> is the one simulation fact this layer copies,
/// because the snapshot does not publish it. A copy is only acceptable while
/// something holds it to the original, and this is that something: the checks
/// below ask the simulation itself, so moving the gate breaks them.
/// </summary>
public sealed class MapBoundsTests
{
    [Fact]
    public void The_gate_constant_really_is_the_gate()
    {
        // The one rule that names it: no zone may cover the gate. If the constant
        // pointed anywhere else, the validator would either accept the command or
        // refuse it for a different reason.
        var error = Assert.Throws<InvalidDataException>(() =>
            PrototypeCommandValidator.Validate(PresentationFixtures.Log(
                new ZonePaintCommand(0, ZoneKind.TrainingGround, [MapBounds.Gate]))));

        Assert.Equal("The gate cannot belong to a zone.", error.Message);
    }

    [Fact]
    public void The_gate_holds_neither_material_nor_a_blueprint()
    {
        var state = PresentationFixtures.Baseline(1);
        Assert.DoesNotContain(MapBounds.Gate, state.Map.StockpileFloorTiles);
        Assert.DoesNotContain(MapBounds.Gate, state.Map.BuildFloorTiles);
        Assert.DoesNotContain(MapBounds.Gate, state.Map.DiggableTiles);
    }

    [Fact]
    public void The_gate_is_on_the_map()
    {
        Assert.True(MapBounds.Contains(MapBounds.Gate));
        Assert.False(MapBounds.Contains(new GridPoint(PrototypeTuning.MapWidth, 0)));
        Assert.False(MapBounds.Contains(new GridPoint(0, -1)));
    }

    /// <summary>
    /// And the brush agrees: a stroke over the gate never becomes a command, so
    /// the refusal above stays a validator test rather than something a player can
    /// reach.
    /// </summary>
    [Fact]
    public void No_brush_stroke_ever_carries_the_gate()
    {
        var state = PresentationFixtures.Baseline(1);
        foreach (var mode in Enum.GetValues<BrushMode>())
        {
            Assert.False(
                BrushSelection.Accepts(state.Shown(), mode, ZoneKind.TrainingGround, MapBounds.Gate),
                $"The {mode} brush would carry the gate.");
        }
    }
}
