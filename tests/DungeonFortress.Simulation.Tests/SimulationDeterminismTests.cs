using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

public sealed class SimulationDeterminismTests
{
    private static readonly SimulationCommand[] Commands =
    [
        new(0, 0, 20),
        new(4, 3, -7),
        new(4, 3, 2),
        new(19, 7, 100),
        new(90, 1, -100),
    ];

    [Fact]
    public void Same_seed_and_commands_produce_byte_identical_snapshots()
    {
        var config = new SimulationConfig(42, 16);

        var first = SimulationScenario.Run(config, 128, Commands);
        var second = SimulationScenario.Run(config, 128, Commands);

        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.Checksum, second.Checksum);
    }

    [Fact]
    public void Different_seed_changes_the_snapshot()
    {
        var first = SimulationScenario.Run(new SimulationConfig(42, 16), 128, Commands);
        var second = SimulationScenario.Run(new SimulationConfig(43, 16), 128, Commands);

        Assert.NotEqual(first.Checksum, second.Checksum);
    }

    [Fact]
    public void Known_scenario_has_stable_checksum()
    {
        SimulationCommand[] commands =
        [
            new(0, 0, 20),
            new(16, 3, -12),
            new(16, 3, 4),
            new(64, 7, 100),
            new(128, 1, -100),
        ];

        var result = SimulationScenario.Run(
            new SimulationConfig(424_242, 32),
            256,
            commands);

        Assert.Equal(
            "e65273aa102f4db01d2cf64ecc48b1556700544f5da0fe7c19378d1d089b6f6f",
            result.Checksum);
    }

    [Fact]
    public void Energy_and_position_invariants_hold_across_explicit_ticks()
    {
        var world = new SimulationWorld(new SimulationConfig(7, 64), Commands);

        for (var expectedTick = 1; expectedTick <= 500; expectedTick++)
        {
            world.Step();
            Assert.Equal(expectedTick, world.CurrentTick);

            foreach (var agent in world.GetAgentSnapshots())
            {
                Assert.InRange(agent.Energy, 0, SimulationWorld.MaximumEnergy);
                Assert.InRange(agent.X, 0, SimulationWorld.WorldWidth - 1);
                Assert.InRange(agent.Y, 0, SimulationWorld.WorldHeight - 1);
                Assert.True(agent.WorkCompleted >= 0);
            }
        }
    }

    [Fact]
    public void Commands_must_be_ordered_by_tick()
    {
        var commands = new[]
        {
            new SimulationCommand(2, 0, 1),
            new SimulationCommand(1, 0, 1),
        };

        var exception = Assert.Throws<ArgumentException>(
            () => new SimulationWorld(new SimulationConfig(1, 1), commands));

        Assert.Contains("non-decreasing tick", exception.Message);
    }

    [Fact]
    public void Command_tick_cannot_be_negative()
    {
        var commands = new[]
        {
            new SimulationCommand(-1, 0, 1),
        };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SimulationWorld(new SimulationConfig(1, 1), commands));

        Assert.Contains("cannot be negative", exception.Message);
    }
}
