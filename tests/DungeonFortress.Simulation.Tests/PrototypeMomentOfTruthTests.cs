using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    /// Every magnitude is built out of the named sources the design contract
    /// lists, and each of the sources a four-wave party can reach is actually
    /// reached. The mutant M1 zeroes one accrual and this goes red, which is
    /// what stops a magnitude from being published, deterministic and empty.
    /// </summary>
    [Fact]
    public void Every_named_source_of_the_three_magnitudes_is_reached_over_the_matrix()
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                var state = PlayOut(LoadFixture(fixtureName) with { Seed = seed });
                foreach (var creature in state.Creatures)
                {
                    foreach (var term in creature.Loyalty.FearTerms
                                 .Concat(creature.Loyalty.BenefitTerms)
                                 .Concat(creature.Loyalty.GrudgeTerms))
                    {
                        seen.Add(term.Code);
                    }
                }
            }
        }

        // The sources a party reaches without a single verdict in its log. The
        // four terms a verdict writes are exercised by the tests above; they
        // cannot appear here, because no shipped fixture contains a verdict.
        string[] required =
        [
            "benefit_faded", "benefit_fed", "benefit_tended",
            "fear_ally_downed", "fear_faded", "fear_panic", "fear_wound",
            "grudge_ignored",
        ];
        var missing = required.Except(seen, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0,
            $"the whole matrix never credited [{string.Join(", ", missing)}], so those sources " +
            "of standing are documented and dead. Seen: " + string.Join(", ", seen));

        // And nothing was credited that the panel has no wording for: an
        // unreadable term on a card is worse than no breakdown at all.
        string[] known =
        [
            "benefit_faded", "benefit_fed", "benefit_rewarded",
            "benefit_tended", "fear_ally_downed", "fear_faded", "fear_panic",
            "fear_punished", "fear_wound", "grudge_hunger", "grudge_ignored",
            "grudge_punished_unfairly", "grudge_refused_place", "grudge_spent",
        ];
        var unknown = seen.Except(known, StringComparer.Ordinal).ToArray();
        Assert.True(
            unknown.Length == 0,
            $"the party credited [{string.Join(", ", unknown)}], which the contract does not " +
            "list and the panel cannot render.");
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
        var state = PrototypeScenario.Capture(RunToMomentOfTruth("baseline")).State;
        var cards = state.MomentOfTruth.Cards;

        // The three the rule chose really are the three most notable of the
        // nine, recomputed here from the published document alone. At the first
        // card of a party every delta is measured from zero, so the whole of the
        // rule is readable off the snapshot: deeds from the journal, standing
        // from the ledgers. This is what the mutant M3 breaks — a selection that
        // takes whoever comes first in the enumeration passes an ordering check
        // and fails this one.
        var notability = state.Creatures.ToDictionary(
            creature => creature.Id,
            creature =>
                state.Events
                    .Where(@event =>
                        @event.CreatureId == creature.Id &&
                        @event.ReasonCode == "combat_raider_downed")
                    .Sum(@event => @event.Repeats) * PrototypeTuning.MomentOfTruthDeedWeight +
                Math.Max(
                    creature.Loyalty.Benefit,
                    Math.Max(creature.Loyalty.Fear, creature.Loyalty.Grudge)));
        var expected = notability
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(PrototypeTuning.MomentOfTruthCards)
            .Select(pair => pair.Key)
            .ToArray();
        Assert.Equal(expected, cards.Select(card => card.CreatureId).ToArray());
        Assert.All(cards, card => Assert.Equal(notability[card.CreatureId], card.Notability));

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

    /// <summary>
    /// A card reports <b>this wave</b> and not the whole party. The rule is
    /// section 2.2 of the design contract, and until independent review of
    /// PR #328 it was held by nothing: review replaced every delta with the
    /// running total and all 334 tests stayed green, because the only check that
    /// touched the numbers ran on the <b>first</b> card of a party, where a delta
    /// and a total are the same number.
    ///
    /// <para>So this one runs on the <b>second</b> card and recomputes what every
    /// delta must be out of two published documents: where the creature stood
    /// when the last card about it was shown, and where it stands now. It is a
    /// check of the class and not of one substitution - any implementation that
    /// reports totals fails it on every creature whose standing moved between two
    /// cards, and the last assertion refuses to let the check pass at all unless
    /// at least one of them did.</para>
    /// </summary>
    [Fact]
    public void A_card_reports_the_wave_and_not_the_whole_party()
    {
        var cardsSeen = 0;
        var comparedWithAHistory = 0;
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                Walk(fixtureName, seed, ref cardsSeen, ref comparedWithAHistory);
            }
        }

        Assert.True(cardsSeen > 0, "the matrix never showed a card.");
        Assert.True(
            comparedWithAHistory > 0,
            $"of the {cardsSeen} cards the matrix showed, not one was about a creature the " +
            "domain had reported on before with a standing of its own — so every delta " +
            "compared here equalled its own total and the check compared nothing.");
    }

    private static void Walk(
        string fixtureName,
        ulong seed,
        ref int cardsSeen,
        ref int comparedWithAHistory)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var baselines = new Dictionary<int, (int Fear, int Benefit, int Grudge)>();
        var wasWaiting = false;

        while (!world.IsComplete)
        {
            world.Step();
            if (!world.IsAwaitingVerdict || wasWaiting)
            {
                wasWaiting = world.IsAwaitingVerdict;
                continue;
            }

            wasWaiting = true;
            var state = world.GetSnapshot();
            foreach (var card in state.MomentOfTruth.Cards)
            {
                cardsSeen++;
                var now = state.Creatures.Single(item => item.Id == card.CreatureId).Loyalty;
                var known = baselines.GetValueOrDefault(card.CreatureId);
                if (known != default)
                {
                    comparedWithAHistory++;
                }

                var expectedFear = now.Fear - known.Fear;
                var expectedBenefit = now.Benefit - known.Benefit;
                var expectedGrudge = now.Grudge - known.Grudge;
                Assert.True(
                    expectedFear == card.FearThisWave &&
                    expectedBenefit == card.BenefitThisWave &&
                    expectedGrudge == card.GrudgeThisWave,
                    $"{fixtureName}/{seed}, t{state.Tick}: the card about {card.Name} reports " +
                    $"{card.FearThisWave}/{card.BenefitThisWave}/{card.GrudgeThisWave} where " +
                    $"the wave moved it by {expectedFear}/{expectedBenefit}/{expectedGrudge} " +
                    $"(it stood at {known.Fear}/{known.Benefit}/{known.Grudge} when the domain " +
                    "last reported on it). A card that reports the whole party repeats the " +
                    "story the player has already answered, and the verdict is asked about " +
                    "the wrong thing.");

                baselines[card.CreatureId] = (now.Fear, now.Benefit, now.Grudge);
            }
        }
    }

    /// <summary>
    /// A domain that punishes whoever it is shown, wave after wave, is eventually
    /// refused. This is the one behaviour a grudge has left after independent
    /// review of PR #328 showed the other one to be structurally unreachable, and
    /// it is reached here by playing the story the mechanic is about rather than
    /// by moving a constant.
    /// </summary>
    [Fact]
    public void A_domain_that_punishes_without_cause_is_eventually_refused_the_line()
    {
        var state = PlayPunishingEveryCard("baseline");
        var refusals = state.Events
            .Where(item => item.ReasonCode == "combat_refused_grudge")
            .ToArray();

        Assert.True(
            refusals.Length > 0,
            "nobody ever refused to stand for a domain that punished every creature it was " +
            "shown, so the only behaviour a grudge has left is unreachable - which is exactly " +
            "what independent review found about the one that was removed. Best case over the " +
            $"party: {Contest(state)}.");

        var refusal = refusals[0];
        Assert.True(refusal.Details.ContainsKey("grudge"));
        Assert.True(refusal.Details.ContainsKey("holding"));
        Assert.True(
            refusal.Details["grudge"] > 0,
            "the refusal names a grudge of zero, so it is not a refusal by grudge.");
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
        var world = new PrototypeWorld(LoadFixture("baseline") with
        {
            Commands = [new VerdictCommand(5, 0, VerdictKind.Reward)],
        });
        for (var index = 0; index < 5; index++)
        {
            world.Step();
        }

        // Photographed on the tick the command is due and compared after the
        // refusal, which is the whole of what "before any mutation" claims.
        // Independent review of PR #328 found this test asserting only that
        // something was thrown; the checksum it did compare was of a world five
        // ticks older, so it was bound to differ whatever the command did.
        var beforeTheRefusal = PrototypeScenario.Capture(world).CanonicalJson;
        var refused = Assert.Throws<InvalidDataException>(world.Step);
        Assert.Contains("moment of truth is open", refused.Message, StringComparison.Ordinal);
        Assert.Equal(beforeTheRefusal, PrototypeScenario.Capture(world).CanonicalJson);

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
        while (!stray.IsAwaitingVerdict && !stray.IsComplete)
        {
            stray.Step();
        }

        var beforeTheStrayVerdict = PrototypeScenario.Capture(stray).CanonicalJson;
        var thrown = Assert.Throws<InvalidDataException>(stray.Step);
        Assert.Contains("reported no card", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(beforeTheStrayVerdict, PrototypeScenario.Capture(stray).CanonicalJson);
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
    // The contract and the code cannot drift apart on a number.
    // ------------------------------------------------------------------

    /// <summary>
    /// Every tuning value of the slice appears in the contract's own table with
    /// the value the code actually holds, and nothing appears in one and not the
    /// other.
    ///
    /// <para>Independent review of PR #328 found two numbers where the contract
    /// and the code disagreed, and one of them stood inside a formula the
    /// contract asks the reader to apply. A table maintained by hand drifts; the
    /// only fix that holds is to read both sides and compare them, which is what
    /// this does. The names are mapped mechanically — <c>loyalty_fear_wound</c>
    /// to <c>LoyaltyFearWound</c> — so a new constant cannot be added without a
    /// row, and a row cannot be written for a constant that does not exist.</para>
    /// </summary>
    [Fact]
    public void The_tuning_table_of_the_contract_carries_the_numbers_the_code_holds()
    {
        var root = FindRepositoryRoot();
        var contract = File.ReadAllText(Path.Combine(
            root, "docs", "design", "SLICE_03_MOMENT_OF_TRUTH.md"));
        var tuning = File.ReadAllText(Path.Combine(
            root, "src", "DungeonFortress.Simulation", "PrototypeTuning.cs"));

        var documented = Regex
            .Matches(contract, @"\|\s*`((?:loyalty|moment_of_truth)_[a-z_]+)`\s*\|\s*(-?\d+)\s*\|")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
        var declared = Regex
            .Matches(tuning, @"public const int ((?:Loyalty|MomentOfTruth)[A-Za-z]+) = (-?\d+);")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);

        Assert.NotEmpty(documented);
        Assert.NotEmpty(declared);

        var problems = new List<string>();
        foreach (var (name, value) in documented.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var constant = string.Concat(name.Split('_')
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
            if (!declared.TryGetValue(constant, out var actual))
            {
                problems.Add($"`{name}` is in the contract and there is no {constant} in the code");
            }
            else if (actual != value)
            {
                problems.Add($"`{name}`: contract says {value}, code holds {actual}");
            }
        }

        foreach (var (constant, value) in declared.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var name = Regex.Replace(constant, "(?<!^)(?=[A-Z])", "_").ToLowerInvariant();
            if (!documented.ContainsKey(name))
            {
                problems.Add($"{constant} = {value} is in the code and not in the contract table");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
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
    /// <para><b>Asked of both values of the enumeration.</b> Independent review
    /// of PR #328 found that it used to be asked of <c>punish</c> alone, and that
    /// <c>reward</c> had no second reading in behaviour at all — so criterion 7
    /// of Issue #312 held for one value out of two. The reward's own channel
    /// (<see cref="PrototypeWorld.LoyaltyReach"/>) is what this theory holds, and
    /// the mutant M7 zeroes it.</para>
    [Fact]
    public void A_verdict_makes_the_named_creature_behave_differently_in_the_next_wave()
    {
        var open = RunToMomentOfTruth("baseline");
        var atTick = open.CurrentTick;
        var subject = PrototypeScenario.Capture(open).State.MomentOfTruth.Cards[0].CreatureId;

        var silent = PlayOut(LoadFixture("baseline"));
        var punished = PlayOut(LoadFixture("baseline") with
        {
            Commands = [new VerdictCommand(atTick, subject, VerdictKind.Punish)],
        });
        var rewarded = PlayOut(LoadFixture("baseline") with
        {
            Commands = [new VerdictCommand(atTick, subject, VerdictKind.Reward)],
        });

        // Three arms and not two, and the third one is what makes the check say
        // something about the *verdict* rather than about the act of answering.
        // Answering at all already changes the world — an answered card is not
        // charged `grudge_ignored` — so "answered differs from unanswered" is
        // true even of a verdict with no effect of its own. Comparing the two
        // signs against each other removes that confound entirely: both arms
        // answered the same card on the same tick, and the only difference left
        // is what the player said.
        //
        // The verdict's own journal entry is excluded from every comparison for
        // the same reason: left in, the lists differ because one of them says
        // "was punished", which records that the command arrived and not that
        // anything came of it. The mutant M6 makes the effect of a verdict
        // nothing and M7 does the same to the reward's own channel; both are
        // caught here.
        var quiet = Decisions(silent, subject);
        var harsh = Decisions(punished, subject);
        var kind = Decisions(rewarded, subject);

        Assert.True(
            !harsh.SequenceEqual(kind),
            $"creature {subject} decided exactly the same things whether the player rewarded " +
            "it or punished it, so the sign of the verdict is not a decision about anything. " +
            $"Who moved between the two: {Moved(punished, rewarded)}.");
        Assert.True(
            !harsh.SequenceEqual(quiet),
            $"creature {subject} decided exactly the same things punished as ignored. " +
            $"Who moved: {Moved(punished, silent)}.");
        // A reward is asked of every creature the domain put on a card, and one
        // witness is enough. Which of the three the reach moves is a fact about
        // where they happen to be standing — a creature whose work nobody else
        // is competing for takes it whether it has been rewarded or not — and
        // demanding that all three move would be demanding that the mechanic
        // override the matching rather than lean on it.
        var witnesses = PrototypeScenario.Capture(open).State.MomentOfTruth.Cards
            .Select(card => card.CreatureId)
            .Where(id =>
            {
                var run = PlayOut(LoadFixture("baseline") with
                {
                    Commands = [new VerdictCommand(atTick, id, VerdictKind.Reward)],
                });
                return !Decisions(run, id).SequenceEqual(Decisions(silent, id));
            })
            .ToArray();
        Assert.True(
            witnesses.Length > 0,
            "not one of the three creatures the domain reported on did anything differently " +
            "for being rewarded, so `reward` is a command with no consequence and criterion 7 " +
            "holds for one value of the enumeration out of two. Who moved when the first card " +
            $"was rewarded: {Moved(rewarded, silent)}.");

        // And each verdict is in the journal, in words.
        foreach (var state in new[] { punished, rewarded })
        {
            Assert.Contains(
                state.Events,
                item => item.CreatureId == subject &&
                    item.ReasonCode.StartsWith("verdict_", StringComparison.Ordinal));
        }

        static List<string> Decisions(PrototypeSnapshot state, int creatureId) =>
        [
            .. state.Events
                .Where(item => item.CreatureId == creatureId &&
                    !item.ReasonCode.StartsWith("verdict_", StringComparison.Ordinal))
                .Select(item => string.Create(
                    CultureInfo.InvariantCulture,
                    $"t{item.FirstTick} {item.ReasonCode} x{item.Repeats}")),
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

    /// <summary>
    /// Which creatures decided anything differently between two parties. It is
    /// in the failure message rather than in a probe script, because "the verdict
    /// moved nobody" and "the verdict moved somebody else" are different defects
    /// and the message has to tell them apart.
    /// </summary>
    private static string Moved(PrototypeSnapshot with, PrototypeSnapshot without)
    {
        var lines = new List<string>();
        foreach (var creature in with.Creatures)
        {
            var mine = Story(with, creature.Id);
            var theirs = Story(without, creature.Id);
            if (!mine.SequenceEqual(theirs))
            {
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"#{creature.Id} {creature.Name} ({mine.Count} vs {theirs.Count} entries)"));
            }
        }

        return lines.Count == 0 ? "nobody at all" : string.Join(", ", lines);

        static List<string> Story(PrototypeSnapshot state, int creatureId) =>
        [
            .. state.Events
                .Where(item => item.CreatureId == creatureId &&
                    !item.ReasonCode.StartsWith("verdict_", StringComparison.Ordinal))
                .Select(item => string.Create(
                    CultureInfo.InvariantCulture,
                    $"t{item.FirstTick} {item.ReasonCode} x{item.Repeats}")),
        ];
    }

    /// <summary>
    /// The closest anybody in this party came to refusing the line, so that a
    /// failure says how far off the contest was instead of only that it never
    /// happened.
    /// </summary>
    private static string Contest(PrototypeSnapshot state)
    {
        var best = state.Creatures
            .Select(creature => new
            {
                creature.Name,
                Released = Math.Max(0, creature.Loyalty.Grudge - creature.Loyalty.Fear),
                Holding = creature.Loyalty.Benefit + creature.Loyalty.Fear +
                    creature.Grit * PrototypeTuning.LoyaltyRefuseGritWeight,
                creature.Loyalty.Fear,
                creature.Loyalty.Grudge,
            })
            .OrderByDescending(item => item.Released * PrototypeTuning.LoyaltyRefuseGrudgeWeight - item.Holding)
            .First();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{best.Name} released {best.Released} x{PrototypeTuning.LoyaltyRefuseGrudgeWeight} " +
            $"= {best.Released * PrototypeTuning.LoyaltyRefuseGrudgeWeight} against holding " +
            $"{best.Holding} (fear {best.Fear}, grudge {best.Grudge}) at the end of the party");
    }

    /// <summary>
    /// Plays on from an open moment of truth until the next one opens.
    /// </summary>
    private static void RunToNextMomentOfTruth(PrototypeWorld world, string fixtureName)
    {
        while (world.IsAwaitingVerdict && !world.IsComplete)
        {
            world.Step();
        }

        while (!world.IsComplete && !world.IsAwaitingVerdict)
        {
            world.Step();
        }

        Assert.True(
            world.IsAwaitingVerdict,
            $"{fixtureName} never opened a second moment of truth, so nothing here compares a " +
            "card with the one before it.");
    }

    /// <summary>
    /// A whole party in which the player punishes the first creature of every
    /// card the domain shows, whether or not it did anything wrong. The commands
    /// are built one pause at a time, because the tick a wave ends on and the
    /// creature a card is about are both emergent; each is an ordinary command of
    /// the dictionary, applied on its own tick like any other.
    /// </summary>
    private static PrototypeSnapshot PlayPunishingEveryCard(string fixtureName)
    {
        var log = LoadFixture(fixtureName);
        var issued = new List<PrototypeCommand>();
        for (var round = 0; round < PrototypeTuning.WaveCount; round++)
        {
            var world = new PrototypeWorld(log with { Commands = [.. log.Commands, .. issued] });
            var seen = 0;
            var added = false;
            while (!world.IsComplete && !added)
            {
                var wasWaiting = world.IsAwaitingVerdict;
                world.Step();
                if (!world.IsAwaitingVerdict || wasWaiting)
                {
                    continue;
                }

                seen++;
                if (seen <= round)
                {
                    continue;
                }

                var pause = world.GetSnapshot().MomentOfTruth;
                issued.Add(new VerdictCommand(
                    world.CurrentTick,
                    pause.Cards[0].CreatureId,
                    VerdictKind.Punish));
                added = true;
            }

            if (!added)
            {
                break;
            }
        }

        var final = new PrototypeWorld(log with { Commands = [.. log.Commands, .. issued] });
        while (!final.IsComplete)
        {
            final.Step();
        }

        return final.GetSnapshot();
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
