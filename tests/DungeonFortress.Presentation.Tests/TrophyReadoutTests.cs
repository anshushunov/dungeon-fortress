using DungeonFortress.Presentation;
using DungeonFortress.Simulation;
using Xunit;

namespace DungeonFortress.Presentation.Tests;

public sealed class TrophyReadoutTests
{
    private const int LateInTheParty = 1_400;

    private static PrototypeSnapshot Party() =>
        PrototypeScenario.Run(
            PresentationFixtures.LogOf("baseline") with { Seed = 20_260_726 },
            LateInTheParty).State;

    [Fact]
    public void The_panel_names_the_blade_and_its_bonus_or_says_nothing()
    {
        var state = Party();
        var armed = state.Creatures.Single(creature => creature.Id == 1) with
        {
            Weapon = new PrototypeWeaponSnapshot("Крюк", 40, 2, 3, 5),
        };
        var unarmed = state.Creatures.Single(creature => creature.Id == 2) with { Weapon = null };
        state = state with
        {
            Creatures = [.. state.Creatures.Select(creature =>
                creature.Id == 1 ? armed : creature.Id == 2 ? unarmed : creature)],
        };
        var view = state.Shown();

        Assert.Contains("wields blade of Крюк (+3 might)", InspectorText.Build(view, 1, null), StringComparison.Ordinal);
        Assert.Contains("wields nothing", InspectorText.Build(view, 2, null), StringComparison.Ordinal);
    }

    [Fact]
    public void Trophy_sentences_name_the_raider_when_the_snapshot_is_at_hand()
    {
        var state = Party();
        var raider = state.Raiders.First();
        var details = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["raiderId"] = raider.Id,
            ["bonus"] = 2,
            ["downedBy"] = 5,
            ["takenBy"] = 1,
        };

        Assert.Contains($"blade of {raider.Name}", EventNarration.Sentence("trophy_taken", details, JobKind.Claim, null, state), StringComparison.Ordinal);
        Assert.Contains("Кремень carries the blade", EventNarration.Sentence("trophy_lost", details, null, null, state), StringComparison.Ordinal);
        Assert.Contains($"raider {raider.Id}", EventNarration.Sentence("trophy_taken", details, JobKind.Claim, null), StringComparison.Ordinal);
    }

    [Fact]
    public void The_mark_shows_on_a_holder_and_on_nobody_else()
    {
        var state = Party();
        var armed = state.Creatures.First() with { Weapon = new PrototypeWeaponSnapshot("Крюк", 40, 2, 1, 5) };
        var downed = armed with { Mode = CreatureMode.Downed };
        var unarmed = state.Creatures.First() with { Weapon = null };

        Assert.NotNull(TrophyMarks.Of(armed));
        Assert.Null(TrophyMarks.Of(downed));
        Assert.Null(TrophyMarks.Of(unarmed));
        Assert.Equal(TrophyMarks.Color, TrophyMarks.Of(armed)!.Value.Color);
    }

    [Fact]
    public void The_mark_is_declared_in_the_draw_order()
    {
        var routine = WorldDrawOrder.Find("DrawTrophyMark")!;
        Assert.Equal(WorldDrawPass.Informational, routine.Pass);
        Assert.Equal(OverlayMark.BodyState, routine.Mark);
    }
}
