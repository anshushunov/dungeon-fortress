using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// docs/design/TROPHY_WEAPON.md §5: the two trophy sentences are the ones the
/// owner is meant to be able to retell — «Кремень взял клинок Гаральда» and
/// «Уголь свалил Гаральда и остался ни с чем». A sentence that says «took the
/// blade of raider 12» retells nothing, so the panel those sentences are ranked
/// a turning point in (<see cref="HudText.StoryWeight"/> gives both weight 3)
/// has to reach the names.
///
/// <para><b>Read off a played party</b>, the way
/// <c>LocalisedInjuryReadoutTests</c> is: a snapshot assembled here would prove
/// that the formatter formats. What has to be true is that a party the owner can
/// run produces a story panel with names in it — and the panel takes its state
/// from the caller, which is exactly what went missing.</para>
/// </summary>
public sealed class TrophyStoryTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    /// <summary>Past the first wave, so a raider has fallen and been robbed.</summary>
    private const int AfterTheFirstWave = 1_500;

    [Fact]
    public void The_story_panel_names_the_raider_whose_blade_was_taken()
    {
        var (state, @event) = FirstParty("trophy_taken");
        var line = TheTrophyLine(state, @event, "took the blade of");
        var raiderId = @event.Details["raiderId"];
        var raider = state.Raiders.Single(item => item.Id == raiderId);

        Assert.Contains(raider.Name, line, StringComparison.Ordinal);
        Assert.DoesNotContain($"raider {raiderId}", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_story_panel_names_the_creature_that_carried_the_blade_off()
    {
        var (state, @event) = FirstParty("trophy_lost");
        var line = TheTrophyLine(state, @event, "carries the blade");
        var raiderId = @event.Details["raiderId"];
        var takenBy = @event.Details["takenBy"];

        Assert.Contains(state.Raiders.Single(item => item.Id == raiderId).Name, line, StringComparison.Ordinal);
        Assert.Contains(HudText.CreatureName(state, takenBy), line, StringComparison.Ordinal);
        Assert.DoesNotContain($"raider {raiderId}", line, StringComparison.Ordinal);
        Assert.DoesNotContain($"#{takenBy}", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The line of the panel the trophy is told in. Asked of the same public
    /// entry the HUD calls, so the test cannot pass while the screen prints
    /// something else.
    /// </summary>
    private string TheTrophyLine(PrototypeSnapshot state, PrototypeEvent @event, string phrase)
    {
        var panel = HudText.CreatureStory(state, @event.CreatureId);
        output.WriteLine(panel);
        return Assert.Single(
            panel.Split('\n'),
            line => line.Contains(phrase, StringComparison.Ordinal));
    }

    /// <summary>
    /// The first party of the matrix in which somebody wrote
    /// <paramref name="reasonCode"/>, with that entry. A trophy is taken only
    /// after a raider has fallen, so the state is the party rather than a tick
    /// number chosen in advance.
    /// </summary>
    private static (PrototypeSnapshot State, PrototypeEvent Event) FirstParty(string reasonCode)
    {
        foreach (var seed in MatrixSeeds)
        {
            var state = PrototypeScenario.Run(
                PresentationFixtures.LogOf("baseline") with { Seed = seed },
                AfterTheFirstWave).State;
            var found = state.Events.FirstOrDefault(item => item.ReasonCode == reasonCode);
            if (found is not null)
            {
                return (state, found);
            }
        }

        throw new InvalidOperationException(
            $"No party of the matrix wrote `{reasonCode}` by tick {AfterTheFirstWave}, so this check " +
            "has no subject. Either the trophy rules stopped producing it or the parties moved.");
    }
}
