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
/// <c>prepared</c> journal that turns 68 blows into 53 entries, up to three folded
/// into one. The question that matters for the picture is not how many entries
/// there are but whether a frame can still tell that <em>this</em> creature struck
/// on <em>this</em> tick — and the answer is yes, because a fold moves
/// <c>LastTick</c> to the tick of the latest repeat. Walking the party one tick at
/// a time and reading each snapshot the way the adapter reads it recovers every
/// one of the 68.
/// </para>
///
/// <para>
/// This is also why nothing in the simulation had to change, and why nothing in it
/// did: <c>src/DungeonFortress.Simulation</c> is untouched by Issue #210.
/// </para>
///
/// <para><b>Re-pinned by Issue #361, and the fold got weaker for a nameable
/// reason.</b> The three numbers were 136 blows, 52 entries and nine in the worst
/// fold. Both halves of the move follow from the damage jitter coming back to
/// life, and neither is a loosened claim:
/// <list type="bullet">
/// <item><description><b>136 -> 68 blows.</b> The party is fought differently and
/// more shortly: over the same journal <c>raidersDowned</c> falls 17 -> 9 and
/// combat ticks 117 -> 98 (<c>evidence/361-contract-numbers.json</c>), so there
/// are about half as many blows to strike.</description></item>
/// <item><description><b>nine -> three in the worst fold, and 52 -> 53
/// entries.</b> <c>RecordDecision</c> folds only when <c>DetailsEqual</c> holds,
/// and the details of <c>combat_attack</c> carry <c>damage</c>
/// (<c>PrototypeWorld.Combat.cs:198</c>). While the jitter was frozen a fighter
/// hitting the same raider twice produced literally the same entry and it folded;
/// with the jitter live the damage usually differs, so the same run of blows now
/// makes several entries instead of one. Fewer blows and less folding at once is
/// why the entry count went slightly <em>up</em> while the blow count
/// halved.</description></item>
/// </list>
/// The fold is still real — 68 blows in 53 entries — and what this test states is
/// unchanged: every blow is recoverable tick by tick, and the recovered total is
/// still tied to <c>Sum(Repeats)</c> by an equality rather than by a
/// bound.</para>
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

    private static PrototypeSnapshot PlayToTick(PrototypeCommandLog log, int tick)
    {
        var world = new PrototypeWorld(log);
        while (!world.IsComplete && world.CurrentTick < tick)
        {
            world.Step();
        }

        return world.GetSnapshot();
    }

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
            // A step is no longer always a tick (Issue #312): while the party
            // stands in a moment of truth it does not move, and reading the
            // blows of the frozen tick again would count every one of them once
            // per step of the window.
            var beforeStep = world.CurrentTick;
            world.RunTicks(1);
            if (world.CurrentTick == beforeStep)
            {
                continue;
            }

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

        // Played to a *tick* and not to a number of steps: a step stopped being
        // a tick when the party learned to stand still between two waves
        // (Issue #312), and `Run(log, n)` takes n steps.
        var final = PlayToTick(log, PartyTicks);
        var attackEntries = final.Events
            .Where(@event => @event.ReasonCode == BlowReadout.AttackReason)
            .ToArray();

        // The fold is real: fewer entries than blows, and at least one entry
        // holds more than one. If this stopped being true the claim below would
        // be about nothing.
        //
        // Stated as the property rather than as the two numbers it happened to
        // have. They were 53 entries over 68 blows when this was written and the
        // longer fight of Issue #333 moved both; a literal here would have to be
        // re-recorded every time the party is re-tuned while checking nothing the
        // property does not already check.
        Assert.NotEmpty(attackEntries);
        Assert.True(
            attackEntries.Length < recoveredCrewBlows,
            $"{attackEntries.Length} journal entries carried {recoveredCrewBlows} blows, so " +
            "nothing was folded and the claim below is about nothing.");
        Assert.True(
            attackEntries.Max(@event => @event.Repeats) > 1,
            "no journal entry holds more than one blow, so the fold this test is about does " +
            "not happen in this party at all.");

        // And it costs the picture nothing.
        Assert.Equal(attackEntries.Sum(@event => @event.Repeats), recoveredCrewBlows);
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
