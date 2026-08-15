using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Issue #431, checkpoint 3 — the price of coercion and the two ends of the
/// loop it closes.
///
/// <list type="number">
/// <item><description><c>grudge_pressed_wounded</c> is credited only where the
/// fear of the domain <b>was the reason</b> a wounded creature took the field —
/// §3.4. Credited on every entry it would stop meaning unfairness and start
/// meaning participation;</description></item>
/// <item><description>the grudge the coercion feeds takes creatures out of the
/// line through the mechanism that is already there: the resentment surfaces as
/// the fear hiding it fades, and <c>combat_refused_grudge</c> refuses the line.
/// The closure <em>through this slice's own term</em> is a rare event accepted
/// by the owner on 2026-08-15 (record 38 of Issue #415) — what is asserted and
/// what is only recorded is set out on the test
/// itself;</description></item>
/// <item><description>§3.6 and the seventh amendment of the second review
/// round: a creature that mends its last part mid-wave <b>bypasses</b> the
/// contest at the next re-check, takes the existing <c>combat_joined</c> path,
/// and has its intent field cleared.</description></item>
/// </list>
/// </summary>
public sealed class PrototypePressedWoundedTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static readonly string[] Fixtures = ["baseline", "prepared"];

    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    /// <summary>
    /// The term is credited exactly where §3.4 says and nowhere else: the
    /// creature took the field, and the sparing side would have won without the
    /// fear of the domain. Checked against the published numbers of the very
    /// contest that credited it, so the rule is read off the document rather than
    /// off the code that wrote it.
    /// </summary>
    [Fact]
    public void The_price_of_coercion_is_charged_only_where_fear_of_the_domain_was_the_reason()
    {
        var charged = 0;
        var problems = new List<string>();

        foreach (var (cell, log) in EveryParty())
        {
            var world = new PrototypeWorld(log);
            var previous = new Dictionary<int, int>();
            while (!world.IsComplete)
            {
                world.Step();
                var state = world.GetSnapshot();
                foreach (var creature in state.Creatures)
                {
                    var now = creature.Loyalty.GrudgeTerms
                        .FirstOrDefault(term => term.Code == "grudge_pressed_wounded")?.Amount ?? 0;
                    var before = previous.GetValueOrDefault(creature.Id);
                    previous[creature.Id] = now;
                    if (now == before)
                    {
                        continue;
                    }

                    charged++;
                    if (now - before != PrototypeTuning.LoyaltyGrudgePressedWounded)
                    {
                        problems.Add(
                            $"{cell} t{state.Tick}: {creature.Name} was charged {now - before} " +
                            $"rather than T.loyalty_grudge_pressed_wounded = " +
                            $"{PrototypeTuning.LoyaltyGrudgePressedWounded}.");
                        continue;
                    }

                    if (creature.WoundIntent is not { } intent)
                    {
                        problems.Add(
                            $"{cell} t{state.Tick}: {creature.Name} was charged for being pressed " +
                            "into a fight and carries no decision about its wound at all.");
                        continue;
                    }

                    if (intent.Code != "pressed")
                    {
                        problems.Add(
                            $"{cell} t{state.Tick}: {creature.Name} was charged while its own " +
                            $"decision says `{intent.Code}`. The price is for being coerced into " +
                            "the line, not for staying out of it.");
                        continue;
                    }

                    // The rule itself, recomputed from the published numbers: the
                    // fear of the domain is exactly what the pressing side has
                    // above `grit x T.combat_press_grit_weight`, so the sparing
                    // side must beat what is left when it is taken away.
                    var withoutTheDomain = creature.Grit * PrototypeTuning.CombatPressGritWeight;
                    if (intent.Spare <= withoutTheDomain)
                    {
                        problems.Add(
                            $"{cell} t{state.Tick}: {creature.Name} was charged although sparing " +
                            $"({intent.Spare}) would have lost to the pressing side without the " +
                            $"fear of the domain ({withoutTheDomain}) anyway. It went in because " +
                            "it is steady, not because it is afraid of the player.");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(
            charged > 0,
            "nowhere in the matrix was anybody ever charged `grudge_pressed_wounded`, so the " +
            "second half of pitch 6.3 — coercion works now and is paid for later — has no " +
            "mechanic on this channel.");
    }

    /// <summary>
    /// The loop, closed and named. A punished wounded creature takes the field
    /// because it is afraid of the domain, is charged for it, and a wave or two
    /// later refuses to stand at all through <c>combat_refused_grudge</c> — which
    /// is the mechanism that was already there and which this slice deliberately
    /// does not duplicate.
    ///
    /// <para>One case is enough, and the failure message prints every case found
    /// so that «the loop closed» is a fact with a seed, a creature and two ticks
    /// beside it rather than an aggregate.</para>
    ///
    /// <para><b>The first fork, and how it was answered.</b> On the first
    /// measurement of the shipped matrix the closures were zero and the cause was
    /// structural rather than a constant: <c>HoldingTheLine</c> read the
    /// <b>total</b> fear, three of whose four sources are the fight, so the more
    /// frightening the raid the tighter it held a resentful creature in the line.
    /// The owner's decision of 2026-08-15 (record 37 of Issue #415) is that the
    /// refusal reads the fear of the domain instead, and it is in the tree.</para>
    ///
    /// <para><b>The refusal now works and the loop still does not close, and the
    /// two are separate facts.</b> The line is refused by grudge ten times over
    /// the matrix, and three of those ten the old reading of the formula would
    /// have held: the counterfactual is published for every refusal as
    /// <c>HoldingIfTheTotalFearWereRead</c>. What is left is the <b>other</b>
    /// half — the charge. Over the whole
    /// matrix <c>grudge_pressed_wounded</c> is credited exactly once, and that
    /// once falls in wave 4 of 4, so there is no later roll call for it to be paid
    /// at; the creature ends the party <c>Downed</c>. Refusals happen at the
    /// opening roll call of a wave, when the quiet between waves has let the fear
    /// fade below the grudge, so a charge has to land in wave 3 or earlier to be
    /// payable — and the sparing side of the contest only grows that high once a
    /// creature has accumulated enough hurt parts, which happens late.</para>
    ///
    /// <para><b>The second fork, and how the owner closed it (record 38 of Issue
    /// #415, 2026-08-15).</b> The state above was put to the owner with its
    /// numbers and <b>accepted as it stands</b>: the price of coercion remains a
    /// rare event, the slice goes to playtest, and the criterion is closed by an
    /// honest sentence rather than by a green test about nothing. Three ways round
    /// were named and refused with their cost — charging for every fight taken
    /// wounded would make the loop frequent and stop the grudge meaning
    /// unfairness; widening the window by weights would disturb a balance only
    /// just measured and confirmed by two mutants; charging by the severity of the
    /// wound is a new rule that is in neither the pitch nor the specification. The
    /// condition for revisiting it is a playtest at which the owner says
    /// punishment has no consequence he can feel.</para>
    ///
    /// <para><b>What this test therefore asserts, and what it deliberately does
    /// not.</b> It asserts the two halves that are true and load-bearing: the
    /// price of coercion is charged somewhere in the matrix, and the grudge does
    /// take creatures out of the line — the promise of pitch 6.3 is kept in the
    /// game, on the account of <c>grudge_punished_unfairly</c> rather than of
    /// <c>grudge_pressed_wounded</c>. It does <b>not</b> assert that the two never
    /// meet: a closure appearing later is the outcome the owner would want, not a
    /// regression, and a test that went red on it would be forbidding the thing it
    /// was written to want. The count of closures is published in
    /// <c>evidence/431-loop.json</c> instead, where the next measurement can read
    /// it.</para>
    ///
    /// <para>Numbers with their command, party by party, in
    /// <c>evidence/431-loop.json</c> (<c>census</c>) and
    /// <c>evidence/431-mutants.json</c> (M6).</para>
    /// </summary>
    [Fact]
    public void The_price_of_coercion_is_charged_and_the_grudge_does_take_creatures_out_of_the_line()
    {
        var census = Census();
        var charges = census.Sum(party => party.ChargesOfPressedWounded);
        var refusals = census.Sum(party => party.RefusalsByGrudge);
        var parties = census.Count(party => party.RefusalsByGrudge > 0);
        var paidForAnUnfairPunishment = census
            .SelectMany(party => party.Refusals)
            .Where(refusal => refusal.GrudgeFromAnUnfairPunishment > 0)
            .ToArray();
        var closures = FindClosures();

        Assert.True(
            charges > 0,
            "the price of coercion was never charged anywhere in the matrix, so §3.4 has no " +
            "mechanic at all rather than a rare one." + Environment.NewLine + NearMissReport());

        Assert.True(
            refusals > 0 && parties > 0,
            $"the line was refused by grudge {refusals} time(s) in {parties} part(y/ies) of the " +
            "matrix. With none, the second half of pitch 6.3 — «принуждение копит обиду, и обида " +
            "возвращается» — would be unreachable in the game by any term, and the acceptance of " +
            "record 38 of #415 rested on it being reachable by one.");

        Assert.True(
            paidForAnUnfairPunishment.Length > 0,
            "not one refusal of the line anywhere in the matrix was paid out of a grudge the " +
            "player's own verdict wrote. Record 38 accepts the rarity of " +
            "`grudge_pressed_wounded` precisely because the player's verdict still comes back at " +
            "him through `grudge_punished_unfairly`; without that this is not a rare channel but " +
            "an absent one.");

        // Recorded and not asserted, for the reason set out above.
        _output.WriteLine(
            $"charges {charges}; refusals {refusals} in {parties} part(y/ies), of which " +
            $"{paidForAnUnfairPunishment.Length} paid out of an unfair punishment; closures of " +
            $"the loop through `grudge_pressed_wounded` {closures.Count} " +
            "(rare by record 38 of #415, not forbidden by this test).");
    }

    /// <summary>
    /// §3.6 and the seventh amendment: mending the last part mid-wave takes a
    /// creature out of the contest altogether. The outcome is the same as before
    /// this slice existed — that is the point — but the branch, the reason code
    /// and the intent field are all different, so all three are checked.
    /// </summary>
    [Fact]
    public void A_creature_that_mends_mid_wave_bypasses_the_contest_and_clears_its_intent()
    {
        var cases = FindMendings();
        Assert.True(
            cases.Count > 0,
            "nowhere in the matrix did a creature that had been asked about its wound come back " +
            "to a later roll call whole, so the case §3.6 names — «залечился посреди волны» — was " +
            "never reached and the claim about the term of a decision is untested.");

        foreach (var mended in cases)
        {
            Assert.Equal("combat_joined", mended.ReasonCode);
            Assert.Null(mended.IntentAfter);
        }
    }

    [Fact]
    public void The_loop_and_the_mending_are_recorded()
    {
        var closures = FindClosures();
        var mendings = FindMendings();

        File.WriteAllText(
            Path.Combine(FindRepositoryRoot(), "evidence", "431-loop.json"),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    issue = "#431",
                    checkpoint = "3 — grudge_pressed_wounded, замыкание петли и «залечился посреди волны»",
                    command =
                        "dotnet test tests/DungeonFortress.Simulation.Tests " +
                        "--filter FullyQualifiedName~PrototypePressedWoundedTests",
                    loyaltyGrudgePressedWounded = PrototypeTuning.LoyaltyGrudgePressedWounded,
                    loop = new
                    {
                        what =
                            "Cases in which a creature charged `grudge_pressed_wounded` later " +
                            "refused the line by `combat_refused_grudge`. The refusal is the " +
                            "mechanism that was already in the tree; this slice only feeds it.",
                        count = closures.Count,
                        cases = closures,
                        ownersDecision =
                            "Запись 38 в Issue #415, 2026-08-15: цена принуждения принимается " +
                            "редким событием как есть, слайс идёт на playtest. Число ниже " +
                            "публикуется и НЕ утверждается тестом — его рост это желаемый исход, " +
                            "а не регресс. Условие пересмотра — playtest, на котором владелец " +
                            "скажет, что наказание не имеет ощутимых последствий.",
                    },
                    nearMisses = new
                    {
                        what =
                            "For every creature charged `grudge_pressed_wounded`, the closest " +
                            "the refusal `ReleasedGrudge x T.loyalty_refuse_grudge_weight > " +
                            "benefit + fearOfTheDomain + grit x T.loyalty_refuse_grit_weight` " +
                            "ever came to being true afterwards. A negative gap means the " +
                            "condition WAS true and the roll call still never asked — because " +
                            "the creature was Fighting at that moment. The holding side reads " +
                            "the fear of the domain from the owner's decision of 2026-08-15 " +
                            "(record 37 of #415) onwards; before it, the total fear.",
                        cases = NearMisses(),
                    },
                    census = new
                    {
                        what =
                            "Per party of the matrix: how often the line was refused by grudge " +
                            "at all, and every charge of `grudge_pressed_wounded` with the wave " +
                            "it landed in and how many roll calls of this party were still to " +
                            "come after it. `rollCallsLeft` is what decides whether the loop " +
                            "COULD close in that party, and is measured rather than assumed.",
                        waveCount = PrototypeTuning.WaveCount,
                        combatJoinRecheck = PrototypeTuning.CombatJoinRecheck,
                        cases = Census(),
                    },
                    mendedMidWave = new
                    {
                        what =
                            "Cases in which a creature that had already been asked about its " +
                            "wound came back to a later roll call whole. §3.6: it bypasses the " +
                            "contest, takes the `combat_joined` path and its intent is cleared.",
                        count = mendings.Count,
                        cases = mendings,
                    },
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }) + "\n",
            new UTF8Encoding(false));

        // `closures` is deliberately NOT asserted non-empty. It was the finding
        // this file escalated twice; the owner answered it on 2026-08-15
        // (record 38 of #415) by accepting the rarity as it stands. The number
        // stays published rather than asserted, so that the playtest the decision
        // hangs on has something to read and so that a closure appearing later
        // reads as the outcome the owner wants rather than as a red test.
        Assert.NotEmpty(mendings);
    }

    // ------------------------------------------------------------------
    // The measurements.
    // ------------------------------------------------------------------

    private sealed record Closure(
        string Cell,
        int CreatureId,
        string Name,
        int PressedAtTick,
        int PressedInWave,
        int GrudgeAfterBeingPressed,
        int RefusedAtTick,
        int RefusedInWave,
        int GrudgeAtRefusal,
        int FearAtRefusal,
        int FearOfTheDomainAtRefusal,
        int HoldingAtRefusal);

    private sealed record ChargeCase(
        int CreatureId,
        string Name,
        int Tick,
        int Wave,
        int AskedByTheRollCallAfterwards,
        string ModeAtEndOfParty);

    /// <summary>
    /// One refusal of the line by grudge, with both readings of the holding side
    /// beside it. <c>GrudgeAtDecision</c> and <c>HoldingAtDecision</c> are the
    /// numbers the refusal itself published — the snapshot a tick later has
    /// already had <c>SpendGrudge</c> applied to it, so reading the grudge off it
    /// says something else.
    ///
    /// <para><c>HoldingIfTheTotalFearWereRead</c> is the counterfactual the mutant
    /// of this checkpoint actually runs: what would have held this creature had
    /// <c>HoldingTheLine</c> kept reading the total fear.</para>
    /// </summary>
    private sealed record RefusalCase(
        int CreatureId,
        string Name,
        int Tick,
        int GrudgeAtDecision,
        int HoldingAtDecision,
        int Fear,
        int FearOfTheDomain,
        int Benefit,
        int Grit,
        int HoldingIfTheTotalFearWereRead,
        int GrudgeFromAnUnfairPunishment,
        int GrudgeFromCoercion);

    private sealed record PartyCensus(
        string Cell,
        int EndTick,
        int RefusalsByGrudge,
        int ChargesOfPressedWounded,
        IReadOnlyList<ChargeCase> Charges,
        IReadOnlyList<RefusalCase> Refusals);

    /// <summary>
    /// What the matrix holds of both halves of the loop, party by party: how
    /// often the line was refused by grudge at all, and every charge of
    /// <c>grudge_pressed_wounded</c> with the wave it landed in and how many more
    /// times the roll call asked that creature afterwards.
    ///
    /// <para><c>AskedByTheRollCallAfterwards</c> counts the creature's own
    /// <c>combat_*</c> entries after the charge, which is the observable form of
    /// «была ли ещё перекличка, на которой оно могло отказаться». A charge in the
    /// last wave has nowhere to be paid, and that has to be a measured number
    /// rather than an inference from the wave count.</para>
    /// </summary>
    private static List<PartyCensus> Census()
    {
        var census = new List<PartyCensus>();
        foreach (var (cell, log) in EveryParty())
        {
            var world = new PrototypeWorld(log);
            var charged = new Dictionary<int, int>();
            var pressed = new Dictionary<int, (int Tick, int Wave)>();
            var refusedSoFar = new Dictionary<int, int>();
            var refusals = new List<RefusalCase>();
            while (!world.IsComplete)
            {
                world.Step();
                var tick = world.GetSnapshot();
                foreach (var creature in tick.Creatures)
                {
                    var now = creature.Loyalty.GrudgeTerms
                        .FirstOrDefault(term => term.Code == "grudge_pressed_wounded")?.Amount ?? 0;
                    if (now > charged.GetValueOrDefault(creature.Id) &&
                        creature.WoundIntent is { } intent)
                    {
                        pressed[creature.Id] = (intent.Tick, intent.Wave);
                    }

                    charged[creature.Id] = now;

                    var refused = tick.Events
                        .Where(item => item.CreatureId == creature.Id &&
                            item.ReasonCode == "combat_refused_grudge")
                        .Sum(item => item.Repeats);
                    if (refused <= refusedSoFar.GetValueOrDefault(creature.Id))
                    {
                        continue;
                    }

                    refusedSoFar[creature.Id] = refused;
                    var decision = tick.Events
                        .Last(item => item.CreatureId == creature.Id &&
                            item.ReasonCode == "combat_refused_grudge");
                    var grit = creature.Grit * PrototypeTuning.LoyaltyRefuseGritWeight;
                    refusals.Add(new RefusalCase(
                        creature.Id,
                        creature.Name,
                        tick.Tick,
                        decision.Details.GetValueOrDefault("grudge"),
                        decision.Details.GetValueOrDefault("holding"),
                        creature.Loyalty.Fear,
                        creature.Loyalty.FearOfTheDomain,
                        creature.Loyalty.Benefit,
                        creature.Grit,
                        creature.Loyalty.Benefit + creature.Loyalty.Fear + grit,
                        // Which term of the ledger the refusal is actually being
                        // paid out of. The owner's decision of 2026-08-15
                        // (record 38 of #415) turns on this: coercion accumulates
                        // a grudge and the grudge comes back — pitch 6.3 — is
                        // happening in the game, but on the account of «наказан
                        // несправедливо» and not of «загнан в бой раненым».
                        creature.Loyalty.GrudgeTerms
                            .FirstOrDefault(term => term.Code == "grudge_punished_unfairly")?.Amount ?? 0,
                        creature.Loyalty.GrudgeTerms
                            .FirstOrDefault(term => term.Code == "grudge_pressed_wounded")?.Amount ?? 0));
                }
            }

            var state = world.GetSnapshot();
            var charges = pressed
                .OrderBy(entry => entry.Key)
                .Select(entry =>
                {
                    var creature = state.Creatures.Single(item => item.Id == entry.Key);
                    return new ChargeCase(
                        entry.Key,
                        creature.Name,
                        entry.Value.Tick,
                        entry.Value.Wave,
                        state.Events.Count(item =>
                            item.CreatureId == entry.Key &&
                            item.ReasonCode.StartsWith("combat_", StringComparison.Ordinal) &&
                            item.LastTick > entry.Value.Tick),
                        creature.Mode.ToString());
                })
                .ToList();

            census.Add(new PartyCensus(
                cell,
                state.Tick,
                state.Events
                    .Where(item => item.ReasonCode == "combat_refused_grudge")
                    .Sum(item => item.Repeats),
                charges.Count,
                charges,
                refusals));
        }

        return census;
    }

    private sealed record Mending(
        string Cell,
        int CreatureId,
        string Name,
        int AskedAtTick,
        string AskedOutcome,
        string WoundThen,
        int JoinedWholeAtTick,
        string ReasonCode,
        string? IntentAfter);

    private sealed record NearMiss(
        string Cell,
        int CreatureId,
        string Name,
        int Charged,
        int Tick,
        string ModeThen,
        int ReleasedTimesWeight,
        int Holding,
        int Gap,
        int Grudge,
        int Fear,
        int FearOfTheDomain);

    /// <summary>
    /// The near misses rendered for a failure message, so that «the loop never
    /// closed» arrives with the distance beside it rather than on its own.
    /// </summary>
    private static string NearMissReport()
    {
        var misses = NearMisses();
        return misses.Count == 0
            ? "Nobody in the matrix was ever charged `grudge_pressed_wounded` at all."
            : "Closest the refusal ever came, per charged creature: " + string.Join(
                "; ",
                misses.Select(miss => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{miss.Cell} #{miss.CreatureId} {miss.Name} t{miss.Tick} ({miss.ModeThen}) " +
                    $"released x weight {miss.ReleasedTimesWeight} against holding {miss.Holding}")));
    }

    /// <summary>
    /// How close the loop came. For each creature that was ever charged, the tick
    /// at which the refusal condition stood closest to firing, with the mode the
    /// creature was in at that tick — which is the whole of the finding.
    /// </summary>
    private static List<NearMiss> NearMisses()
    {
        var misses = new List<NearMiss>();
        foreach (var (cell, log) in EveryParty())
        {
            var world = new PrototypeWorld(log);
            var best = new Dictionary<int, NearMiss>();
            while (!world.IsComplete)
            {
                world.Step();
                foreach (var creature in world.GetSnapshot().Creatures)
                {
                    var charged = creature.Loyalty.GrudgeTerms
                        .FirstOrDefault(term => term.Code == "grudge_pressed_wounded")?.Amount ?? 0;
                    if (charged <= 0)
                    {
                        continue;
                    }

                    var released = Math.Max(0, creature.Loyalty.Grudge - creature.Loyalty.Fear) *
                        PrototypeTuning.LoyaltyRefuseGrudgeWeight;

                    // The holding side reads the fear of the domain and not the
                    // total — owner's decision of 2026-08-15, record 37 of #415.
                    // Recomputed here exactly as `HoldingTheLine` compares it,
                    // because a near miss measured against a different formula
                    // would report a distance nothing in the world walks.
                    var holding = creature.Loyalty.Benefit + creature.Loyalty.FearOfTheDomain +
                        creature.Grit * PrototypeTuning.LoyaltyRefuseGritWeight;
                    var miss = new NearMiss(
                        cell,
                        creature.Id,
                        creature.Name,
                        charged,
                        world.CurrentTick,
                        creature.Mode.ToString(),
                        released,
                        holding,
                        holding - released,
                        creature.Loyalty.Grudge,
                        creature.Loyalty.Fear,
                        creature.Loyalty.FearOfTheDomain);
                    if (!best.TryGetValue(creature.Id, out var known) || miss.Gap < known.Gap)
                    {
                        best[creature.Id] = miss;
                    }
                }
            }

            misses.AddRange(best.Values.OrderBy(item => item.CreatureId));
        }

        return misses;
    }

    private static List<Closure> FindClosures()
    {
        var closures = new List<Closure>();
        foreach (var (cell, log) in EveryParty())
        {
            var world = new PrototypeWorld(log);
            var pressed = new Dictionary<int, (int Tick, int Wave, int Grudge)>();
            var charged = new Dictionary<int, int>();
            var refusals = new Dictionary<int, int>();
            while (!world.IsComplete)
            {
                world.Step();
                var state = world.GetSnapshot();
                foreach (var creature in state.Creatures)
                {
                    var now = creature.Loyalty.GrudgeTerms
                        .FirstOrDefault(term => term.Code == "grudge_pressed_wounded")?.Amount ?? 0;
                    if (now > charged.GetValueOrDefault(creature.Id) &&
                        creature.WoundIntent is { } intent)
                    {
                        pressed[creature.Id] = (intent.Tick, intent.Wave, creature.Loyalty.Grudge);
                    }

                    charged[creature.Id] = now;

                    var refused = state.Events
                        .Where(item => item.CreatureId == creature.Id &&
                            item.ReasonCode == "combat_refused_grudge")
                        .Sum(item => item.Repeats);
                    var known = refusals.GetValueOrDefault(creature.Id);
                    refusals[creature.Id] = refused;
                    if (refused <= known || !pressed.TryGetValue(creature.Id, out var coercion))
                    {
                        continue;
                    }

                    var refusal = state.Events
                        .Last(item => item.CreatureId == creature.Id &&
                            item.ReasonCode == "combat_refused_grudge");
                    closures.Add(new Closure(
                        cell,
                        creature.Id,
                        creature.Name,
                        coercion.Tick,
                        coercion.Wave,
                        coercion.Grudge,
                        refusal.LastTick,
                        refusal.Details.GetValueOrDefault("wave"),
                        refusal.Details.GetValueOrDefault("grudge"),
                        creature.Loyalty.Fear,
                        creature.Loyalty.FearOfTheDomain,
                        refusal.Details.GetValueOrDefault("holding")));
                }
            }
        }

        return closures;
    }

    private static List<Mending> FindMendings()
    {
        var mendings = new List<Mending>();
        foreach (var (cell, log) in EveryParty())
        {
            var world = new PrototypeWorld(log);
            var asked = new Dictionary<int, (int Tick, string Code, string Wound)>();
            var joins = new Dictionary<int, int>();
            var before = world.GetSnapshot();
            while (!world.IsComplete)
            {
                world.Step();
                var after = world.GetSnapshot();
                foreach (var creature in after.Creatures)
                {
                    var joined = after.Events
                        .Where(item => item.CreatureId == creature.Id &&
                            item.ReasonCode == "combat_joined")
                        .Sum(item => item.Repeats);
                    var known = joins.GetValueOrDefault(creature.Id);
                    joins[creature.Id] = joined;

                    var was = before.Creatures.Single(item => item.Id == creature.Id);
                    if (joined > known &&
                        was.Injuries.Count == 0 &&
                        asked.TryGetValue(creature.Id, out var question))
                    {
                        mendings.Add(new Mending(
                            cell,
                            creature.Id,
                            creature.Name,
                            question.Tick,
                            question.Code,
                            question.Wound,
                            after.Tick - 1,
                            "combat_joined",
                            creature.WoundIntent?.Code));
                        asked.Remove(creature.Id);
                    }

                    if (creature.WoundIntent is { } intent && intent.Tick == after.Tick - 1)
                    {
                        asked[creature.Id] = (
                            intent.Tick,
                            intent.Code,
                            $"{intent.Part}:{intent.Severity}");
                    }
                }

                before = after;
            }
        }

        return mendings;
    }

    /// <summary>
    /// Every party the checks are asked of: the matrix, played with silence and
    /// with the two single-sign regimes, because a coercion needs a punishment
    /// somewhere in the log and a party nobody judged has none.
    /// </summary>
    private static IEnumerable<(string Cell, PrototypeCommandLog Log)> EveryParty()
    {
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                var log = LoadFixture(fixtureName) with { Seed = seed };
                yield return ($"{fixtureName}/{seed} silent", log);
                yield return ($"{fixtureName}/{seed} every-punish", AnswerEveryPause(log, VerdictKind.Punish));
                yield return ($"{fixtureName}/{seed} every-reward", AnswerEveryPause(log, VerdictKind.Reward));
            }
        }
    }

    private static PrototypeCommandLog AnswerEveryPause(PrototypeCommandLog baseLog, VerdictKind sign)
    {
        var issued = new List<PrototypeCommand>();
        for (var round = 0; round < PrototypeTuning.WaveCount; round++)
        {
            var world = new PrototypeWorld(baseLog with { Commands = [.. baseLog.Commands, .. issued] });
            var seen = 0;
            var added = false;
            while (!world.IsComplete && !added)
            {
                var wasWaiting = world.IsAwaitingVerdict;
                world.Step();
                if (!world.IsAwaitingVerdict || wasWaiting || ++seen <= round)
                {
                    continue;
                }

                foreach (var card in world.GetSnapshot().MomentOfTruth.Cards)
                {
                    issued.Add(new VerdictCommand(world.CurrentTick, card.CreatureId, sign));
                }

                added = true;
            }

            if (!added)
            {
                break;
            }
        }

        return baseLog with { Commands = [.. baseLog.Commands, .. issued] };
    }

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

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
