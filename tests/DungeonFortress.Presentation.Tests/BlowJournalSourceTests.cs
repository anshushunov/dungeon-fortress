using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The trap Issue #210 named in advance, answered by walking a whole party rather
/// than by reading <c>RecordDecision</c>.
///
/// <para>
/// <c>PrototypeWorld.RecordDecision</c> folds an identical repeat into the
/// creature's previous entry instead of adding a second one. Over the shipped
/// <c>prepared</c> journal that turns 136 blows into 52 entries, up to nine folded
/// into one. The question that matters for the picture is not how many entries
/// there are but whether a frame can still tell that <em>this</em> creature struck
/// on <em>this</em> tick — and the answer is yes, because a fold moves
/// <c>LastTick</c> to the tick of the latest repeat. Walking the party one tick at
/// a time and reading each snapshot the way the adapter reads it recovers every
/// one of the 136.
/// </para>
///
/// <para>
/// This is also why nothing in the simulation had to change, and why nothing in it
/// did: <c>src/DungeonFortress.Simulation</c> is untouched by Issue #210.
/// </para>
/// </summary>
public sealed class BlowJournalSourceTests
{
    /// <summary>
    /// Where the first raider arrives and where the shipped party ends. Walking
    /// from the first arrival rather than from tick 0 keeps the test to the ticks
    /// that can hold a blow; the total it is compared against is taken from the
    /// whole party, so a blow struck outside this window would still fail it.
    /// </summary>
    private const int FirstArrival = 1300;

    private const int PartyTicks = 2400;

    [Fact]
    public void Every_blow_of_the_party_is_recoverable_tick_by_tick()
    {
        var log = PresentationFixtures.LogOf("prepared");
        var world = new PrototypeWorld(log);
        world.RunTicks(FirstArrival);

        var recoveredCrewBlows = 0;
        var recoveredDefendersDowned = 0;
        var recoveredKills = 0;
        while (world.CurrentTick < PartyTicks && !world.IsComplete)
        {
            world.RunTicks(1);
            foreach (var blow in BlowReadout.Of(world.GetSnapshot()).Blows)
            {
                if (blow.Target.Kind == BodyKind.Raider)
                {
                    recoveredCrewBlows++;
                    if (blow.Outcome == BlowOutcome.Downed)
                    {
                        recoveredKills++;
                    }
                }
                else
                {
                    recoveredDefendersDowned++;
                }
            }
        }

        var final = PrototypeScenario.Run(log, PartyTicks).State;
        var attackEntries = final.Events
            .Where(@event => @event.ReasonCode == BlowReadout.AttackReason)
            .ToArray();

        // The fold is real: far fewer entries than blows, and the worst of them
        // holds nine. If this stopped being true the claim below would be about
        // nothing.
        Assert.Equal(52, attackEntries.Length);
        Assert.Equal(9, attackEntries.Max(@event => @event.Repeats));

        // And it costs the picture nothing.
        Assert.Equal(attackEntries.Sum(@event => @event.Repeats), recoveredCrewBlows);
        Assert.Equal(136, recoveredCrewBlows);
        Assert.Equal(
            final.Events.Count(@event =>
                @event.ReasonCode == BlowReadout.RaiderDownedReason),
            recoveredKills);
        Assert.Equal(
            final.Events.Count(@event =>
                @event.ReasonCode == BlowReadout.DefenderDownedReason),
            recoveredDefendersDowned);
    }
}
