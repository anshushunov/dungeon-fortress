using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// Where a run stands, as an address a replay can be driven to.
///
/// <para>A tick alone stopped being an address in Issue #312: while a moment of
/// truth is open the world takes steps that do not move
/// <see cref="PrototypeWorld.CurrentTick"/> at all, so one tick number now names
/// up to <c>MomentOfTruthWindowSteps</c> + 1 different states of the same
/// party.</para>
/// </summary>
/// <param name="Tick">The canonical tick.</param>
/// <param name="WaitedSteps">
/// How many steps of an open moment of truth have been spent. Zero whenever the
/// window is closed, which is every position outside one.
/// </param>
public readonly record struct WorldPosition(int Tick, int WaitedSteps);

/// <summary>
/// The one way the adapter puts a rebuilt world where the player left it.
///
/// <para><b>Why the adapter rebuilds at all.</b> A command the player issues is
/// proved rather than trusted: the whole log — the shipped journal plus every
/// command this session has added — is validated and replayed from tick 0, and
/// the world that replay produces becomes the live one. That is what makes the
/// running session a function of the log and nothing else, so a saved log
/// reproduces the session byte for byte.</para>
///
/// <para>It lives here rather than in <c>Main</c> because ADR 0011 keeps the
/// engine out of the test job: no test project references
/// <c>DungeonFortress.Game</c>, so a rule that lives there can only be read as
/// text. The rebuild is the seam every player command passes through, and until
/// Issue #351 nothing executed it in a test — which is exactly how a verdict
/// that never reached the screen survived two rounds of independent
/// review.</para>
/// </summary>
public static class WorldReplay
{
    /// <summary>Where a snapshot says its world stands.</summary>
    public static WorldPosition PositionOf(PrototypeSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new WorldPosition(
            state.Tick,
            state.MomentOfTruth.Open ? state.MomentOfTruth.WaitedSteps : 0);
    }

    /// <summary>Where a world stands.</summary>
    public static WorldPosition PositionOf(PrototypeWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return PositionOf(world.GetSnapshot());
    }

    /// <summary>
    /// A world built from <paramref name="log"/> and driven to
    /// <paramref name="target"/>.
    /// </summary>
    public static PrototypeWorld To(PrototypeCommandLog log, WorldPosition target)
    {
        ArgumentNullException.ThrowIfNull(log);
        var world = new PrototypeWorld(log);
        while (!world.IsComplete && IsBehind(world, target))
        {
            world.Step();
        }

        return world;
    }

    /// <summary>
    /// Whether the world still has to take a step to reach the target.
    ///
    /// <para>Addressed by the tick and by nothing else, which is the adapter's
    /// rule as Issue #351 found it.</para>
    /// </summary>
    private static bool IsBehind(PrototypeWorld world, WorldPosition target) =>
        world.CurrentTick < target.Tick;
}
