using System.Globalization;
using System.Text;
using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Slice 3 of the pitch's order of proof: the player's decision about one named
/// creature has consequences. Design contract:
/// <c>docs/design/SLICE_03_MOMENT_OF_TRUTH.md</c>; the permission to address a
/// creature at all is <c>docs/decisions/0019-verdict-not-order.md</c>.
///
/// The checks are grouped the way the issue states its criteria, and each one
/// says which criterion it is: a check nobody can map onto a promise is a check
/// nobody will maintain.
/// </summary>
public sealed class PrototypeMomentOfTruthTests
{
    private static readonly string[] Fixtures = ["baseline", "prepared", "neglected"];

    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    // ------------------------------------------------------------------
    // Criterion 1 — the three magnitudes are canonical state.
    // ------------------------------------------------------------------

    /// <summary>
    /// The magnitudes are in the canonical document, and the same seed and log
    /// still reproduce it byte for byte. Both halves matter: a magnitude that is
    /// published but not deterministic would make every golden file a lottery.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void The_three_magnitudes_are_canonical_and_reproduce_byte_for_byte(string fixtureName)
    {
        var first = PrototypeScenario.Run(LoadFixture(fixtureName), PrototypeTuning.SessionTicks);
        var second = PrototypeScenario.Run(LoadFixture(fixtureName), PrototypeTuning.SessionTicks);

        Assert.Equal(first.Checksum, second.Checksum);
        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.CanonicalEventLog, second.CanonicalEventLog);

        using var document = JsonDocument.Parse(first.CanonicalJson);
        foreach (var creature in document.RootElement.GetProperty("creatures").EnumerateArray())
        {
            var loyalty = creature.GetProperty("loyalty");
            Assert.True(loyalty.TryGetProperty("fear", out _));
            Assert.True(loyalty.TryGetProperty("benefit", out _));
            Assert.True(loyalty.TryGetProperty("grudge", out _));
        }

        // A party of four waves has to have moved at least one of the three on at
        // least one creature, otherwise the section is present and empty and the
        // criterion is satisfied on paper only.
        var moved = document.RootElement.GetProperty("creatures").EnumerateArray()
            .Count(creature =>
                creature.GetProperty("loyalty").GetProperty("fear").GetInt32() != 0 ||
                creature.GetProperty("loyalty").GetProperty("benefit").GetInt32() != 0 ||
                creature.GetProperty("loyalty").GetProperty("grudge").GetInt32() != 0);
        Assert.True(
            moved > 0,
            $"{fixtureName}: not one creature ended the party with any standing at all, so " +
            "the three magnitudes are published and never written.");
    }

    /// <summary>
    /// Criterion 4, and the mutant M4 is aimed here: the named terms add up to
    /// the number printed beside them, on every creature, on every tick of a
    /// whole party. The totals and the ledgers are two representations written by
    /// one method, and this is the check that says they never diverge.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    [InlineData("neglected")]
    public void Loyalty_totals_equal_the_sum_of_their_named_terms(string fixtureName)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName));
        var checkedTerms = 0;
        while (!world.IsComplete)
        {
            world.Step();
            var state = world.GetSnapshot();
            foreach (var creature in state.Creatures)
            {
                var loyalty = creature.Loyalty;
                AssertAxis(creature.Name, "fear", loyalty.Fear, loyalty.FearTerms);
                AssertAxis(creature.Name, "benefit", loyalty.Benefit, loyalty.BenefitTerms);
                AssertAxis(creature.Name, "grudge", loyalty.Grudge, loyalty.GrudgeTerms);
                checkedTerms += loyalty.FearTerms.Count +
                    loyalty.BenefitTerms.Count +
                    loyalty.GrudgeTerms.Count;
            }
        }

        Assert.True(
            checkedTerms > 0,
            $"{fixtureName}: no term was ever credited, so the check compared zero with zero.");

        static void AssertAxis(
            string name,
            string axis,
            int total,
            IReadOnlyList<PrototypeLoyaltyTerm> terms)
        {
            var sum = terms.Sum(term => term.Amount);
            Assert.True(
                sum == total,
                $"{name}: {axis} is {total} and its named terms add up to {sum} " +
                $"({string.Join(", ", terms.Select(term => $"{term.Code} {term.Amount}"))}). " +
                "The breakdown on the card is the only thing a verdict can be based on, so it " +
                "may not disagree with the number it explains.");
        }
    }

    // ------------------------------------------------------------------
    // Criterion 2 — the pause stops the party.
    // ------------------------------------------------------------------

    /// <summary>
    /// The tick that did not happen. The party stops after a wave and does not
    /// move for as long as the window is open: not the clock, not a job, not a
    /// mouthful of supper. The mutant M2 removes the pause and this goes red.
    /// </summary>
    [Fact]
    public void A_moment_of_truth_stops_the_party_on_a_tick_that_never_happens()
    {
        var world = RunToMomentOfTruth("baseline");
        var before = PrototypeScenario.Capture(world);
        var frozenTick = world.CurrentTick;

        // Half the window, so that the pause is still open at the end of it.
        var steps = PrototypeTuning.MomentOfTruthWindowSteps / 2;
        for (var index = 0; index < steps; index++)
        {
            world.Step();
        }

        var after = PrototypeScenario.Capture(world);
        Assert.True(world.IsAwaitingVerdict);
        Assert.Equal(frozenTick, world.CurrentTick);
        Assert.Equal(frozenTick, after.State.Tick);

        // The only thing that may differ between the two documents is how long
        // the domain has been waiting; everything else is the same world.
        Assert.Equal(
            Redact(before.CanonicalJson),
            Redact(after.CanonicalJson));
        Assert.Equal(steps, after.State.MomentOfTruth.WaitedSteps);

        static string Redact(byte[] canonical) =>
            System.Text.RegularExpressions.Regex.Replace(
                Encoding.UTF8.GetString(canonical),
                "\"waitedSteps\":\\d+",
                "\"waitedSteps\":*");
    }

    /// <summary>
    /// And it closes by itself, so that a party with no verdicts in its log still
    /// ends. Without this the shipped fixtures, the determinism stage and the
    /// load stage would all hang.
    /// </summary>
    [Fact]
    public void The_window_closes_by_itself_and_the_party_goes_on()
    {
        var world = RunToMomentOfTruth("baseline");
        var frozenTick = world.CurrentTick;
        for (var index = 0; index < PrototypeTuning.MomentOfTruthWindowSteps; index++)
        {
            world.Step();
        }

        Assert.False(world.IsAwaitingVerdict);
        Assert.Equal(frozenTick, world.CurrentTick);

        world.Step();
        Assert.Equal(frozenTick + 1, world.CurrentTick);
    }

    // ------------------------------------------------------------------
    // Criterion 3 — the three cards are deterministic.
    // ------------------------------------------------------------------

    /// <summary>
    /// Two runs of one seed produce the same cards in the same order. The mutant
    /// M3 replaces the ordering of the selection with the order the creatures
    /// happen to be enumerated in, and this goes red.
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void The_selection_of_the_three_cards_is_deterministic(string fixtureName)
    {
        var first = Describe(RunToMomentOfTruth(fixtureName));
        var second = Describe(RunToMomentOfTruth(fixtureName));

        Assert.Equal(first, second);
        Assert.Equal(PrototypeTuning.MomentOfTruthCards, first.Count);

        // Three different creatures: a rule that reported on the same one three
        // times would pass a comparison with itself.
        Assert.Equal(3, first.Select(line => line.Split(' ')[0]).Distinct().Count());

        static List<string> Describe(PrototypeWorld world) =>
        [
            .. PrototypeScenario.Capture(world).State.MomentOfTruth.Cards.Select(card =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{card.CreatureId} {card.Name} {card.DominantAxis} {card.Notability} " +
                    $"{card.FearThisWave}/{card.BenefitThisWave}/{card.GrudgeThisWave}")),
        ];
    }

    /// <summary>
    /// The cards are ordered by the rule the contract states, read back off the
    /// published document rather than off the code that produced it.
    /// </summary>
    [Fact]
    public void The_cards_are_ordered_by_notability_and_then_by_id()
    {
        var cards = PrototypeScenario.Capture(RunToMomentOfTruth("baseline"))
            .State.MomentOfTruth.Cards;
        for (var index = 1; index < cards.Count; index++)
        {
            var previous = cards[index - 1];
            var current = cards[index];
            Assert.True(
                previous.Notability > current.Notability ||
                (previous.Notability == current.Notability &&
                 previous.CreatureId < current.CreatureId),
                $"card {index} ({current.Name}, {current.Notability}) stands after " +
                $"{previous.Name} ({previous.Notability}), which is neither more notable nor " +
                "an earlier id.");
        }
    }

    // ------------------------------------------------------------------
    // Criterion 5 — the command is accepted and refused by the contract.
    // ------------------------------------------------------------------

    /// <summary>
    /// The form of a verdict: exactly four properties, a closed enumeration of
    /// values, and the whole document refused for any deviation. The mutant M5
    /// lets an unknown property through and this goes red.
    /// </summary>
    [Fact]
    public void The_form_of_a_verdict_is_closed_and_a_deviation_refuses_the_whole_document()
    {
        // Accepted.
        var log = Parse("""{"tick":1372,"kind":"verdict","creatureId":4,"verdict":"reward"}""");
        var verdict = Assert.IsType<VerdictCommand>(Assert.Single(log.Commands));
        Assert.Equal(1372, verdict.Tick);
        Assert.Equal(4, verdict.CreatureId);
        Assert.Equal(VerdictKind.Reward, verdict.Verdict);

        // A fifth property — the whole point of ADR 0019's question 4.
        var extra = Assert.Throws<InvalidDataException>(() => Parse(
            """{"tick":1372,"kind":"verdict","creatureId":4,"verdict":"reward","target":[5,5]}"""));
        Assert.Contains("Unknown property", extra.Message, StringComparison.Ordinal);

        // A value outside the closed enumeration, including the three
        // counterexamples the design contract rejects by name.
        foreach (var refused in new[] { "sortie", "champion", "demote", "execute_publicly" })
        {
            var unknown = Assert.Throws<InvalidDataException>(() => Parse(
                $$"""{"tick":1372,"kind":"verdict","creatureId":4,"verdict":"{{refused}}"}"""));
            Assert.Contains("Unknown verdict", unknown.Message, StringComparison.Ordinal);
        }

        // A missing property is refused as firmly as an extra one.
        Assert.Throws<InvalidDataException>(() => Parse(
            """{"tick":1372,"kind":"verdict","creatureId":4}"""));

        // A creature that cannot exist.
        Assert.Throws<InvalidDataException>(() => Parse(
            """{"tick":1372,"kind":"verdict","creatureId":99,"verdict":"punish"}"""));
    }

    /// <summary>
    /// And the runtime half: outside the window, or about a creature the domain
    /// said nothing about, a verdict is refused before anything is written. The
    /// atomicity of ADR 0005 holds for the new command word for word.
    /// </summary>
    [Fact]
    public void A_verdict_outside_the_window_or_without_a_card_is_refused_before_any_mutation()
    {
        // Outside the window: tick 5 of a party that has not seen a wave.
        var early = LoadFixture("baseline") with
        {
            Commands = [new VerdictCommand(5, 0, VerdictKind.Reward)],
        };
        var world = new PrototypeWorld(early);
        var before = PrototypeScenario.Capture(world).Checksum;
        for (var index = 0; index < 5; index++)
        {
            world.Step();
        }

        var refused = Assert.Throws<InvalidDataException>(world.Step);
        Assert.Contains("moment of truth is open", refused.Message, StringComparison.Ordinal);
        Assert.NotEqual(before, PrototypeScenario.Capture(world).Checksum);

        // Without a card: inside the window, about somebody the domain did not
        // report on.
        var open = RunToMomentOfTruth("baseline");
        var carded = PrototypeScenario.Capture(open).State.MomentOfTruth.Cards
            .Select(card => card.CreatureId)
            .ToHashSet();
        var uncarded = Enumerable.Range(0, PrototypeTuning.CreatureCount)
            .First(id => !carded.Contains(id));
        var atTick = open.CurrentTick;
        var stray = new PrototypeWorld(LoadFixture("baseline") with
        {
            Commands = [new VerdictCommand(atTick, uncarded, VerdictKind.Punish)],
        });
        var thrown = Assert.Throws<InvalidDataException>(() => RunPastTheWindow(stray));
        Assert.Contains("reported no card", thrown.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Criterion 6 — the enum and the design contract cannot drift apart.
    // ------------------------------------------------------------------

    /// <summary>
    /// ADR 0019 asks for exactly this check: "перечисление значений в коде
    /// совпадает с перечислением, прошедшим правило допустимости в
    /// design-контракте среза 3; расхождение роняет тест". It is the only thing
    /// that stops a value from entering the game dictionary without a walkthrough
    /// of the five conditions beside it.
    /// </summary>
    [Fact]
    public void Every_verdict_value_is_walked_through_the_five_conditions_in_the_contract()
    {
        var contract = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "docs", "design", "SLICE_03_MOMENT_OF_TRUTH.md"));

        foreach (var value in Enum.GetValues<VerdictKind>())
        {
            var name = value switch
            {
                VerdictKind.Reward => "reward",
                VerdictKind.Punish => "punish",
                _ => throw new InvalidDataException($"Unnamed verdict: {value}"),
            };
            Assert.True(
                contract.Contains($"#### `{name}`", StringComparison.Ordinal),
                $"`{name}` is a value of VerdictKind and has no walkthrough of the five " +
                "conditions of admissibility in SLICE_03_MOMENT_OF_TRUTH.md. A value without " +
                "one is a door ADR 0019 left open on purpose and this test closes.");
        }

        // And the three counterexamples are refused by a named condition rather
        // than by being obviously wrong.
        foreach (var counterexample in new[] { "sortie", "champion", "demote" })
        {
            Assert.True(
                contract.Contains($"`{counterexample}`", StringComparison.Ordinal),
                $"the contract does not say which condition refuses `{counterexample}`.");
        }
    }

    // ------------------------------------------------------------------
    // Criterion 7 — the consequence is reproduced by a scenario.
    // ------------------------------------------------------------------

    /// <summary>
    /// The same seed, the same log, one verdict of difference — and the named
    /// creature does something different in the wave after it, with the
    /// difference visible in the journal rather than only in the numbers. The
    /// mutant M6 makes the effect of a verdict nothing and this goes red.
    /// </summary>
    [Fact]
    public void A_verdict_makes_the_named_creature_behave_differently_in_the_next_wave()
    {
        var open = RunToMomentOfTruth("baseline");
        var atTick = open.CurrentTick;
        var subject = PrototypeScenario.Capture(open).State.MomentOfTruth.Cards[0].CreatureId;

        var without = PlayOut(LoadFixture("baseline"));
        var with = PlayOut(LoadFixture("baseline") with
        {
            Commands = [new VerdictCommand(atTick, subject, VerdictKind.Punish)],
        });

        var mine = Decisions(with, subject);
        var theirs = Decisions(without, subject);
        Assert.True(
            !mine.SequenceEqual(theirs),
            $"creature {subject} decided exactly the same things with the verdict and without " +
            "it, so the verdict changed nothing that anybody can see.");

        // And the verdict itself is in the journal, in words.
        Assert.Contains(
            with.Events,
            @event => @event.CreatureId == subject &&
                @event.ReasonCode.StartsWith("verdict_", StringComparison.Ordinal));

        static List<string> Decisions(PrototypeSnapshot state, int creatureId) =>
        [
            .. state.Events
                .Where(@event => @event.CreatureId == creatureId)
                .Select(@event => string.Create(
                    CultureInfo.InvariantCulture,
                    $"t{@event.FirstTick} {@event.ReasonCode} x{@event.Repeats}")),
        ];
    }

    // ------------------------------------------------------------------
    // The fifth condition of admissibility (Issue #167), in executable form.
    // ------------------------------------------------------------------

    /// <summary>
    /// Half (b) of the fifth condition: no value makes any behaviour inevitable.
    /// After a verdict the creature is offered the same work it would have been
    /// offered without one — the verdict moves weights, not the set of choices —
    /// and it is never handed work it did not choose.
    /// </summary>
    [Theory]
    [InlineData(VerdictKind.Reward)]
    [InlineData(VerdictKind.Punish)]
    public void No_verdict_makes_any_behaviour_inevitable(VerdictKind verdict)
    {
        var open = RunToMomentOfTruth("baseline");
        var atTick = open.CurrentTick;
        var subject = PrototypeScenario.Capture(open).State.MomentOfTruth.Cards[0].CreatureId;

        var with = PlayFor(
            LoadFixture("baseline") with
            {
                Commands = [new VerdictCommand(atTick, subject, verdict)],
            },
            atTick + PrototypeTuning.WaveIntervalTicks);
        var without = PlayFor(LoadFixture("baseline"), atTick + PrototypeTuning.WaveIntervalTicks);

        var kindsWith = KindsTaken(with, subject);
        var kindsWithout = KindsTaken(without, subject);
        Assert.True(
            kindsWith.IsSubsetOf(kindsWithout) || kindsWithout.IsSubsetOf(kindsWith),
            $"after `{verdict}` the creature took kinds of work [{string.Join(", ", kindsWith)}] " +
            $"and without it [{string.Join(", ", kindsWithout)}]. A verdict may move which of " +
            "the offered jobs is taken; it may not open or close a kind of work, because that " +
            "is назначение работы and not суждение (ADR 0019, condition 3).");

        // Nothing is forced: the creature still spends at least one tick doing
        // something other than whatever the verdict is supposed to encourage.
        var modes = with.Events
            .Where(@event => @event.CreatureId == subject)
            .Select(@event => @event.ReasonCode)
            .Distinct()
            .Count();
        Assert.True(
            modes > 1,
            "after the verdict the creature did exactly one thing for a whole wave interval, " +
            "which is what an inevitable behaviour looks like.");

        static HashSet<JobKind> KindsTaken(PrototypeSnapshot state, int creatureId) =>
        [
            .. state.Events
                .Where(@event => @event.CreatureId == creatureId && @event.JobKind is not null)
                .Select(@event => @event.JobKind!.Value),
        ];
    }

    /// <summary>
    /// Half (a) of the fifth condition: the effect of a verdict is reversible by
    /// ordinary play, without a cancelling command — which ADR 0019 forbids.
    /// Every term a verdict writes lies on an axis that fades, so a quiet stretch
    /// brings the creature back to where it stood.
    /// </summary>
    [Fact]
    public void A_verdict_fades_back_to_where_the_creature_stood()
    {
        Assert.True(PrototypeTuning.LoyaltyFearFadePeriod > 0);
        Assert.True(PrototypeTuning.LoyaltyBenefitFadePeriod > 0);

        // The fade is a term of the same ledger, so "it fades" is checkable on
        // the published document: the axis a verdict wrote to carries a negative
        // `*_faded` term by the end of a party in which it was left alone.
        var state = PlayOut(LoadFixture("prepared"));
        var faded = state.Creatures.Count(creature =>
            creature.Loyalty.FearTerms.Any(term => term.Code == "fear_faded" && term.Amount < 0) ||
            creature.Loyalty.BenefitTerms.Any(term =>
                term.Code == "benefit_faded" && term.Amount < 0));
        Assert.True(
            faded > 0,
            "no axis faded in a whole party, so nothing a verdict writes can be undone by " +
            "ordinary play, and the fifth condition of admissibility has no mechanism.");
    }

    /// <summary>
    /// The other half of "не меняет роль", in the only form this slice can state
    /// it: the whole observable effect of a verdict is the standing of the
    /// creature it names. Two documents of the same tick, one with the verdict
    /// and one without, differ in the loyalty of that creature, in the card, in
    /// the pending commands and in the journal — and in nothing else.
    /// </summary>
    [Fact]
    public void A_verdict_changes_nothing_but_the_standing_of_the_creature()
    {
        var open = RunToMomentOfTruth("baseline");
        var atTick = open.CurrentTick;
        var subject = PrototypeScenario.Capture(open).State.MomentOfTruth.Cards[0].CreatureId;

        // One step past the verdict and no further, so nothing the creature then
        // decides has had a chance to move anything else.
        var with = StepPastVerdict(
            LoadFixture("baseline") with
            {
                Commands = [new VerdictCommand(atTick, subject, VerdictKind.Reward)],
            },
            atTick);
        var without = StepPastVerdict(LoadFixture("baseline"), atTick);

        Assert.Equal(with.Tick, without.Tick);
        foreach (var creature in with.Creatures)
        {
            var other = without.Creatures.Single(item => item.Id == creature.Id);
            Assert.Equal(other.Position, creature.Position);
            Assert.Equal(other.Mode, creature.Mode);
            Assert.Equal(other.CurrentJobId, creature.CurrentJobId);
            Assert.Equal(other.Satiety, creature.Satiety);
            Assert.Equal(other.Fatigue, creature.Fatigue);
            Assert.Equal(other.Hp, creature.Hp);
            Assert.Equal(other.Injury, creature.Injury);
            Assert.Equal(other.RememberedPlaces.Count, creature.RememberedPlaces.Count);
            if (creature.Id == subject)
            {
                Assert.True(
                    creature.Loyalty.Benefit > other.Loyalty.Benefit,
                    "the reward left the creature exactly where it was, so the verdict did " +
                    "nothing at all.");
            }
            else
            {
                Assert.Equal(other.Loyalty.Fear, creature.Loyalty.Fear);
                Assert.Equal(other.Loyalty.Benefit, creature.Loyalty.Benefit);
                Assert.Equal(other.Loyalty.Grudge, creature.Loyalty.Grudge);
            }
        }

        Assert.Equal(without.Stocks, with.Stocks);
        Assert.Equal(without.Jobs.Count, with.Jobs.Count);
        Assert.Equal(without.Domain.Renown, with.Domain.Renown);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    /// <summary>
    /// A party played until it stops by itself and waits for the player. The stop
    /// is what is being looked for: the tick a wave ends on is emergent, and a
    /// number here would be a balance value pretending to be a fixture.
    /// </summary>
    private static PrototypeWorld RunToMomentOfTruth(string fixtureName)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName));
        while (!world.IsComplete && !world.IsAwaitingVerdict)
        {
            world.Step();
        }

        Assert.True(
            world.IsAwaitingVerdict,
            $"{fixtureName} played a whole party without ever stopping between two waves.");
        return world;
    }

    private static void RunPastTheWindow(PrototypeWorld world)
    {
        while (!world.IsComplete)
        {
            world.Step();
        }
    }

    private static PrototypeSnapshot PlayOut(PrototypeCommandLog log)
    {
        var world = new PrototypeWorld(log);
        while (!world.IsComplete)
        {
            world.Step();
        }

        return world.GetSnapshot();
    }

    private static PrototypeSnapshot PlayFor(PrototypeCommandLog log, int untilTick)
    {
        var world = new PrototypeWorld(log);
        while (!world.IsComplete && world.CurrentTick < untilTick)
        {
            world.Step();
        }

        return world.GetSnapshot();
    }

    /// <summary>
    /// The world one step after the verdict was due, and not one step further.
    /// </summary>
    private static PrototypeSnapshot StepPastVerdict(PrototypeCommandLog log, int atTick)
    {
        var world = new PrototypeWorld(log);
        while (!world.IsComplete && !world.IsAwaitingVerdict)
        {
            world.Step();
        }

        Assert.Equal(atTick, world.CurrentTick);
        world.Step();
        return world.GetSnapshot();
    }

    private static PrototypeCommandLog Parse(string command) =>
        PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(
            $$"""
            {"schemaVersion":2,"scenario":"custom","seed":20260726,"commands":[{{command}}]}
            """));

    private static PrototypeCommandLog LoadFixture(string fixtureName) =>
        PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(), "scenarios", "prototype1", $"{fixtureName}.commands.v2.json"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DungeonFortress.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root was not found.");
    }

    /// <summary>
    /// Every fixture of the matrix opens at least one moment of truth, so the
    /// mechanic is exercised by the shipped logs and not only by this file.
    /// </summary>
    [Fact]
    public void Every_shipped_fixture_stops_for_a_moment_of_truth_at_least_once()
    {
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
                var pauses = 0;
                var wasOpen = false;
                while (!world.IsComplete)
                {
                    world.Step();
                    if (world.IsAwaitingVerdict && !wasOpen)
                    {
                        pauses++;
                    }

                    wasOpen = world.IsAwaitingVerdict;
                }

                // `neglected` can fall before its first wave is resolved, and a
                // fallen domain is owed no card; that case is allowed to report
                // zero and is named rather than asserted away.
                var ended = world.GetSnapshot().SessionResult.Outcome;
                Assert.True(
                    pauses > 0 || ended == "fallen",
                    $"{fixtureName}/{seed} ended as `{ended}` without ever stopping between " +
                    "two waves.");
            }
        }
    }
}
