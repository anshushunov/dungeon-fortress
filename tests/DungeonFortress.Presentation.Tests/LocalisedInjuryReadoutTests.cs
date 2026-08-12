using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Criteria 6 and 7 of Issue #409: <b>the wound is legible without the log.</b>
///
/// <para>The slice exists to make the owner recognise one creature by what
/// happened to it two waves ago, and until this file existed it could not:
/// <c>injuries</c> was in the canonical document, the four consequences were in
/// the fight, and the screen said nothing at all. The pitch prices the whole of
/// showing it at «иконка над головой и хромающая походка» (6.13), so there are
/// exactly two surfaces here and no third — the label over the body, and the
/// panel of the creature the player clicked.</para>
///
/// <para><b>Read off a played party and not off a hand-built creature.</b> A
/// snapshot assembled in the test would prove that the formatter formats; what
/// has to be true is that a party the owner can run produces creatures the
/// screen distinguishes, and that is a fact about the party as much as about the
/// text.</para>
/// </summary>
public sealed class LocalisedInjuryReadoutTests(ITestOutputHelper output)
{
    private const ulong OwnerSeed = 20_260_729UL;

    /// <summary>Late enough to have been through three waves, before the fuse.</summary>
    private const int LateInTheParty = 2_400;

    private static PrototypeSnapshot Party() =>
        PrototypeScenario.Run(
            PresentationFixtures.LogOf("baseline") with { Seed = OwnerSeed },
            LateInTheParty).State;

    /// <summary>
    /// Criterion 6. Somebody in the owner's party wears a mark over their head
    /// that names the hurt part, and the mark is not worn by everybody: it is the
    /// difference between two bodies that makes one of them a creature the player
    /// can talk about.
    /// </summary>
    [Fact]
    public void The_label_over_a_hurt_creature_names_the_part_and_a_whole_one_carries_no_mark()
    {
        var state = Party();
        var marked = state.Creatures
            .Where(creature => HudText.CreatureInjuryShort(creature).Length > 0)
            .ToArray();
        var whole = state.Creatures
            .Where(creature => creature.Injuries.Count == 0)
            .ToArray();

        foreach (var creature in state.Creatures)
        {
            output.WriteLine($"{WorldLabels.CreatureLine(creature).Text}");
        }

        Assert.True(
            marked.Length > 0,
            "Nobody in the owner's party carries a wound by tick " + LateInTheParty +
            ", so there is nothing over anybody's head to read.");
        Assert.True(
            whole.Length > 0,
            "Everybody in the owner's party is hurt, so the mark marks nothing out.");

        foreach (var creature in marked)
        {
            var line = WorldLabels.CreatureLine(creature).Text;
            var worst = creature.Injuries
                .OrderByDescending(injury => injury.Severity)
                .ThenBy(injury => injury.Part)
                .First();
            Assert.Contains(
                HudText.BodyPartName(worst.Part),
                line,
                StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(creature.Name, line, StringComparison.Ordinal);

            // The wound takes the state token's place rather than standing beside
            // it, so a hurt label is no wider than the widest label the layout
            // already had to place. Asserted rather than left to the formatter,
            // because a third token is paid for in somebody else's name.
            Assert.DoesNotContain(
                HudText.CreatureStateShort(creature),
                line,
                StringComparison.Ordinal);
        }

        foreach (var creature in whole)
        {
            var line = WorldLabels.CreatureLine(creature).Text;
            Assert.Equal($"{creature.Name} {HudText.CreatureStateShort(creature)}", line);
        }
    }

    /// <summary>
    /// The mark says how bad it is as well as where, because «рука» and «рука!»
    /// are the difference between a creature that will be back next wave and one
    /// that has to be nursed. Asserted on both severities so a formatter that
    /// dropped the exclamation would redden rather than pass quietly.
    /// </summary>
    [Fact]
    public void The_mark_tells_a_bad_wound_from_a_light_one()
    {
        var light = new PrototypeInjurySnapshot(BodyPart.Arm, InjuryKind.Light);
        var heavy = new PrototypeInjurySnapshot(BodyPart.Arm, InjuryKind.Heavy);
        var state = Party();
        var subject = state.Creatures[0];

        Assert.Equal("рука", HudText.CreatureInjuryShort(subject with { Injuries = [light] }));
        Assert.Equal("рука!", HudText.CreatureInjuryShort(subject with { Injuries = [heavy] }));
        Assert.Equal(string.Empty, HudText.CreatureInjuryShort(subject with { Injuries = [] }));

        // The worst part wins the mark, and a tie goes to the pitch's own order of
        // the parts rather than to the order the blows landed in.
        Assert.Equal(
            "рука!",
            HudText.CreatureInjuryShort(subject with
            {
                Injuries = [new PrototypeInjurySnapshot(BodyPart.Leg, InjuryKind.Light), heavy],
            }));
        Assert.Equal(
            "торс",
            HudText.CreatureInjuryShort(subject with
            {
                Injuries =
                [
                    new PrototypeInjurySnapshot(BodyPart.Leg, InjuryKind.Light),
                    new PrototypeInjurySnapshot(BodyPart.Torso, InjuryKind.Light),
                ],
            }));
    }

    /// <summary>
    /// Criterion 7. The panel names <b>every</b> hurt part, not only the worst
    /// one, because the panel is where the player goes to ask rather than to
    /// glance — and it says «цел» for a whole creature, because "this one is
    /// whole" is an answer and a missing line is not.
    /// </summary>
    [Fact]
    public void The_panel_of_a_hurt_creature_names_every_part_it_carries()
    {
        var state = Party();
        var view = state.Shown();
        var subject = state.Creatures
            .Where(creature => creature.Injuries.Count > 0)
            .OrderByDescending(creature => creature.Injuries.Count)
            .FirstOrDefault();

        Assert.True(subject is not null, "Nobody in the owner's party is hurt at tick " + LateInTheParty);

        var panel = InspectorText.Build(view, subject!.Id, null);
        output.WriteLine(panel);
        foreach (var injury in subject.Injuries)
        {
            Assert.Contains(HudText.BodyPartName(injury.Part), panel, StringComparison.Ordinal);
        }

        Assert.Contains(
            injuryIsHeavy(subject) ? "тяжело" : "легко",
            panel,
            StringComparison.Ordinal);

        var untouched = state.Creatures.FirstOrDefault(creature => creature.Injuries.Count == 0);
        Assert.True(untouched is not null, "Nobody in the owner's party is whole.");
        Assert.Contains("wounds цел", InspectorText.Build(view, untouched!.Id, null), StringComparison.Ordinal);

        static bool injuryIsHeavy(PrototypeCreatureSnapshot creature) =>
            creature.Injuries.Any(injury => injury.Severity == InjuryKind.Heavy);
    }

    /// <summary>
    /// The two surfaces cannot disagree: whatever the mark over the head says, the
    /// panel of the same creature says too. They are two readings of one field, and
    /// a player who glanced and then clicked must not be told two different things.
    /// </summary>
    [Fact]
    public void The_mark_over_the_head_and_the_panel_never_disagree()
    {
        var state = Party();
        var view = state.Shown();
        foreach (var creature in state.Creatures.Where(item => item.Injuries.Count > 0))
        {
            var mark = HudText.CreatureInjuryShort(creature).TrimEnd('!');
            Assert.Contains(mark, InspectorText.Build(view, creature.Id, null), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// «Хромающая походка», as a property of the curve rather than of a picture: a
    /// limping body's two consecutive steps do not rise the same, and a whole
    /// body's two do.
    ///
    /// <para>The three things the ordinary gait already promises are asserted again
    /// here, because the limp multiplies that curve and could have spent any of
    /// them: a standing body does not move at all, the body never goes below the
    /// line it stands on, and the height is a function of the path alone.</para>
    /// </summary>
    [Fact]
    public void A_limping_body_rises_unevenly_and_a_whole_one_does_not()
    {
        // Swept over a whole limp cycle at a hundredth of a cell: the two
        // properties the ordinary gait already promises have to survive being
        // multiplied.
        for (var step = 0; step <= 400; step++)
        {
            var path = step / 100.0;
            Assert.True(
                BodyMotion.BobOffsetRef(path, walking: true, limping: true) <= 0.0,
                $"a limping body sank below its own feet at {path}");
            Assert.Equal(0.0, BodyMotion.BobOffsetRef(path, walking: false, limping: true));
        }

        // The steps of a walk are its peaks, and a limp is the difference between
        // two consecutive ones. They sit at whole multiples of GaitPeriodCells, so
        // the comparison is made there rather than over a bucket of the cycle —
        // measured the other way, the near-peak at the end of the second half
        // reads as tall as the first and the limp looks absent when it is not.
        double Rise(double path, bool limping) =>
            -BodyMotion.BobOffsetRef(path, walking: true, limping: limping);

        var good = Rise(0.0, limping: true);
        var bad = Rise(BodyMotion.GaitPeriodCells, limping: true);
        var next = Rise(BodyMotion.LimpPeriodCells, limping: true);
        output.WriteLine(
            $"LIMP goodStepRise={good} badStepRise={bad} nextGoodStepRise={next} " +
            $"wholeRise={Rise(0.0, limping: false)}/{Rise(BodyMotion.GaitPeriodCells, limping: false)}");

        Assert.True(
            good > bad && next > bad,
            $"a limping body rose {good} on one step, {bad} on the next and {next} on the one " +
            "after; a limp is the difference between two consecutive steps and there is none.");
        Assert.True(
            bad > 0.0,
            "the bad step does not leave the ground at all, which is a body with one leg " +
            "rather than a body favouring one.");
        Assert.Equal(good, next, 9);

        // A whole body's two steps are the same step, which is what makes the limp
        // readable as a limp rather than as the gait it has always had.
        Assert.Equal(Rise(0.0, limping: false), Rise(BodyMotion.GaitPeriodCells, limping: false), 9);
    }
}
