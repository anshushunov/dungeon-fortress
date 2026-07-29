using Xunit;

namespace DungeonFortress.Presentation.Tests;

public sealed class WorldRenderOrderTests
{
    [Fact]
    public void Wall_occludes_a_body_behind_it_and_yields_to_one_in_front()
    {
        var behind = new WorldRenderItem(WorldRenderKind.Creature, 1, 100, 199.9);
        var wall = new WorldRenderItem(WorldRenderKind.Wall, 2, 100, 200);
        var front = new WorldRenderItem(WorldRenderKind.Creature, 3, 100, 200.1);

        Assert.Equal(
            [behind, wall, front],
            WorldRenderOrder.BackToFront([front, wall, behind]));
    }

    [Fact]
    public void Exact_depth_tie_keeps_tall_geometry_in_front_of_bodies()
    {
        var creature = new WorldRenderItem(WorldRenderKind.Creature, 3, 80, 200);
        var raider = new WorldRenderItem(WorldRenderKind.Raider, 2, 80, 200);
        var structure = new WorldRenderItem(WorldRenderKind.Structure, 1, 80, 200);
        var wall = new WorldRenderItem(WorldRenderKind.Wall, 0, 80, 200);

        Assert.Equal(
            [creature, raider, structure, wall],
            WorldRenderOrder.BackToFront([wall, structure, raider, creature]));
    }

    [Fact]
    public void Interpolated_y_not_tick_cell_decides_when_a_body_crosses_a_wall()
    {
        var wall = new WorldRenderItem(WorldRenderKind.Wall, 10, 100, 200);
        var beforeCrossing = new WorldRenderItem(WorldRenderKind.Creature, 7, 100, 199.75);
        var afterCrossing = beforeCrossing with { Y = 200.25 };

        Assert.Equal(
            [beforeCrossing, wall],
            WorldRenderOrder.BackToFront([wall, beforeCrossing]));
        Assert.Equal(
            [wall, afterCrossing],
            WorldRenderOrder.BackToFront([afterCrossing, wall]));
    }

    [Fact]
    public void Equal_items_have_deterministic_left_to_right_and_id_order()
    {
        var right = new WorldRenderItem(WorldRenderKind.Creature, 1, 101, 200);
        var leftSecond = new WorldRenderItem(WorldRenderKind.Creature, 2, 99, 200);
        var leftFirst = new WorldRenderItem(WorldRenderKind.Creature, 1, 99, 200);

        Assert.Equal(
            [leftFirst, leftSecond, right],
            WorldRenderOrder.BackToFront([right, leftSecond, leftFirst]));
    }

    [Fact]
    public void Non_finite_render_depth_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorldRenderOrder.BackToFront(
                [new WorldRenderItem(WorldRenderKind.Wall, 1, 0, double.NaN)]));
    }
}
