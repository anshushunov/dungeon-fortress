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
        // How much of this log the target position has already heard. Counted
        // once, off the log itself, so the loop below needs nothing from the
        // world but two public numbers.
        var due = log.Commands.Count(command => command.Tick <= target.Tick);
        var world = new PrototypeWorld(log);
        // The second half of the address, kept here rather than read back out of
        // a snapshot: a step that left the tick alone was a step of an open
        // window, and a step that moved it ended one.
        var waited = 0;
        while (!world.IsComplete && IsBehind(world, waited, due, target))
        {
            var tick = world.CurrentTick;
            world.Step();
            waited = world.CurrentTick == tick ? waited + 1 : 0;
        }

        return world;
    }

    /// <summary>
    /// Whether the world still has to take a step to reach the target.
    ///
    /// <para><b>Away from a moment of truth</b> this is the tick and nothing
    /// else, which is the rule the adapter has always had: a command dated at the
    /// tick the player is standing on stays pending and takes effect on the next
    /// one. That is what the feedback line has always promised, and nothing about
    /// it changes.</para>
    ///
    /// <para><b>Inside one</b> the tick names up to forty-one different states, so
    /// two more things have to be true before the replay may stop.</para>
    ///
    /// <list type="number">
    /// <item><description>The window must have spent as many steps as the live
    /// run had spent. Without this every press rewound the pause to the step it
    /// opened on, and with it every verdict already answered
    /// (Issue #351).</description></item>
    /// <item><description>Every command the log dates at or before this position
    /// must have been heard. This is the difference between a verdict that is
    /// «accepted» and a verdict that is <em>answered</em>: while the window is
    /// open the clock is stopped by design (Issue #331), so a command that waits
    /// for the next step waits for a step that will never come unless the player
    /// gives up the pause they are being asked to spend. It costs the window one
    /// of its forty steps — the world does have to take a step to hear anything —
    /// and it costs it once, because that one step hears every command dated at
    /// the frozen tick, this press's and every earlier one's.</description></item>
    /// </list>
    ///
    /// <para>Both clauses die with the window: once it closes the world runs the
    /// tick it was holding back, the tick passes the target and the loop
    /// stops. Nothing here changes a rule of the moment of truth — three cards,
    /// forty steps and what silence costs are all the simulation's, untouched.</para>
    /// </summary>
    private static bool IsBehind(PrototypeWorld world, int waited, int due, WorldPosition target)
    {
        if (world.CurrentTick != target.Tick)
        {
            return world.CurrentTick < target.Tick;
        }

        return world.IsAwaitingVerdict &&
            (waited < target.WaitedSteps || world.CommandsApplied < due);
    }
}
