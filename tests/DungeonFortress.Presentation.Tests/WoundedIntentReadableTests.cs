using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #431, checkpoint 4 — the decision of a wounded creature is readable
/// without opening a log.
///
/// <para>Criterion 8, as the fifth amendment of the second review round rewrote
/// it, is about <b>three</b> channels and not about the panel alone: the feed
/// with both of its sentences, the panel line that survives the tick, and the
/// breakdown of a card of the moment of truth. A full <c>verify.ps1</c> would
/// stay green with none of them, which is why they are checked here one by
/// one.</para>
///
/// <para><b>Why the presentation tests run parties.</b> Two of the three claims
/// are about facts the simulation only produces under conditions no hand-built
/// snapshot can honestly assert: that the roll call's decision is <em>gone</em>
/// from <c>LastDecision</c> while the panel still shows it, and that a domain
/// which was never judged still spares its wounded. Both are claims about what
/// the world does, so both are asked of the world. Everything that is genuinely
/// a function of its input — the wording of a sentence, the rendering of a card
/// — is asked of the input directly.</para>
/// </summary>
public sealed class WoundedIntentReadableTests(ITestOutputHelper output)
{
    /// <summary>
    /// The cells the contest is looked for in, in order. The shipped journals on
    /// their own seed do not produce a refusal — measured, not assumed: the
    /// coverage guard
    /// <c>EventNarrationTests.Every_reason_code_the_matrix_produces_has_a_sentence</c>
    /// runs all three of them and would have thrown on an unworded code long
    /// before this file existed. The matrix seeds of the slice are where the
    /// scene lives, and the search stops at the first cell that has it.
    /// </summary>
    private static readonly (string Fixture, ulong Seed)[] Cells =
    [
        ("prepared", 20_260_726UL),
        ("prepared", 20_260_727UL),
        ("prepared", 20_260_728UL),
        ("baseline", 20_260_726UL),
        ("baseline", 20_260_727UL),
        ("baseline", 20_260_728UL),
    ];

    // ------------------------------------------------------------------
    // Channel 1 — the feed, both sentences.
    // ------------------------------------------------------------------

    /// <summary>
    /// Both sentences exist, both name the creature, the wave and the part, and
    /// neither leaks its reason code — the same three things
    /// <c>EventNarration</c> promises about every other line of the feed.
    ///
    /// <para>Asked of the details directly, because a sentence is a pure function
    /// of them: what the simulation puts in those keys is checked by
    /// <c>PrototypeWoundedContestTests</c> and by the shape guard, and repeating
    /// that here would test the world twice and the wording never.</para>
    /// </summary>
    [Fact]
    public void The_feed_has_a_sentence_for_both_ends_of_the_contest()
    {
        var spared = EventNarration.Sentence("combat_spared_wound", Spared(), null, null);
        var pressed = EventNarration.Sentence("combat_pressed_wound", Pressed(), null, null);

        output.WriteLine(spared);
        output.WriteLine(pressed);

        Assert.DoesNotContain("combat_", spared, StringComparison.Ordinal);
        Assert.DoesNotContain("combat_", pressed, StringComparison.Ordinal);

        // «не встал: бережёт ногу»
        Assert.Contains("would not stand for wave 3", spared, StringComparison.Ordinal);
        Assert.Contains("leg", spared, StringComparison.Ordinal);
        Assert.Contains("7 against 4", spared, StringComparison.Ordinal);

        // «встал с разбитой ногой»
        Assert.Contains("stood for wave 3", pressed, StringComparison.Ordinal);
        Assert.Contains("leg", pressed, StringComparison.Ordinal);
        Assert.Contains("9 grudge", pressed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Criterion 5 at the surface it is actually about. The causality rule of
    /// §3.5 is a rule for the <b>feed</b>: a verdict may be named as the cause
    /// only where removing its term flips the contest. The same details with the
    /// flag off and on are the whole test of it, because the flag is the only
    /// thing the sentence is allowed to read.
    /// </summary>
    [Fact]
    public void The_feed_names_the_verdict_only_where_the_verdict_decided()
    {
        var undecided = Spared();
        var decided = Spared();
        decided["verdictDecided"] = 1;

        var silent = EventNarration.Sentence("combat_spared_wound", undecided, null, null);
        var credited = EventNarration.Sentence("combat_spared_wound", decided, null, null);

        output.WriteLine(silent);
        output.WriteLine(credited);

        Assert.DoesNotContain("reward", silent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("you", silent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("your reward", credited, StringComparison.Ordinal);

        // The two sentences are otherwise the same sentence: nothing but the
        // credit may depend on the flag, or the check above would be passing on
        // some other difference.
        Assert.StartsWith(
            "would not stand for wave 3: sparing a hurt leg",
            silent,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "would not stand for wave 3: sparing a hurt leg",
            credited,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The case criterion 5 names literally, asked of the world rather than of a
    /// dictionary: a party in which no card was ever answered still has wounded
    /// creatures sparing themselves, on benefit earned by being fed and tended,
    /// and not one line of the feed credits the player with it.
    ///
    /// <para>This is the half that a hand-built <c>details</c> cannot claim. The
    /// flag could be wired to something that happens to be false in a fixture and
    /// true everywhere else; here the party is the evidence that a well-run
    /// domain produces the outcome on its own.</para>
    /// </summary>
    [Fact]
    public void A_domain_that_judged_nobody_is_never_named_as_the_cause()
    {
        var found = FirstContest("spared", judged: false);
        var state = found.State;

        var spared = state.Events
            .Where(item => item.ReasonCode == "combat_spared_wound")
            .ToArray();
        Assert.NotEmpty(spared);

        foreach (var @event in spared)
        {
            Assert.Equal(0, @event.Details.GetValueOrDefault("verdictDecided"));
            var sentence = EventNarration.Describe(state, @event);
            Assert.DoesNotContain("your reward", sentence, StringComparison.OrdinalIgnoreCase);
        }

        // And the benefit really was earned rather than given, which is the
        // wording of the criterion: nobody in this party carries the term a
        // reward writes.
        Assert.DoesNotContain(
            state.Creatures.SelectMany(creature => creature.Loyalty.BenefitTerms),
            term => term.Code == "benefit_rewarded");

        output.WriteLine(
            $"{found.Cell}: {spared.Length} refusal(s) of the wounded, none of them credited " +
            "to a verdict.");
        foreach (var @event in spared)
        {
            output.WriteLine("  " + EventNarration.Describe(state, @event));
        }
    }

    // ------------------------------------------------------------------
    // Channel 2 — the panel line, and the tick it has to survive.
    // ------------------------------------------------------------------

    /// <summary>
    /// Criterion 8's own sentence: the line exists and <b>survives the tick</b>,
    /// and the proof that it is not built from <c>LastDecision</c> is that
    /// <c>LastDecision</c> has already moved on while the line is still there.
    ///
    /// <para>Both ends of the contest, because «полез с разбитой ногой» is half
    /// of the answer the playtest asks for and a panel that only reported
    /// refusals would leave the other half unreadable.</para>
    /// </summary>
    [Theory]
    [InlineData("spared")]
    [InlineData("pressed")]
    public void The_panel_shows_the_decision_after_the_tick_that_overwrote_the_last_decision(string code)
    {
        var found = FirstContest(code, judged: true);
        var world = found.World;
        var creatureId = found.CreatureId;
        var contestTick = found.IntentTick;

        var atTheRollCall = Panel(world.GetSnapshot(), creatureId);
        Assert.Contains("INTENT t" + contestTick.ToString(CultureInfo.InvariantCulture), atTheRollCall, StringComparison.Ordinal);

        // Walk on while the same decision stands and look for a tick on which the
        // panel's own `WHY` block is about something else entirely. That tick is
        // the whole claim: on it, a panel reading `LastDecision` would have
        // nothing to say about the wound.
        var survived = 0;
        string? later = null;
        string? overwrittenBy = null;
        for (var step = 0; step < 400 && !world.IsComplete; step++)
        {
            world.Step();
            var state = world.GetSnapshot();
            var creature = state.Creatures.SingleOrDefault(item => item.Id == creatureId);
            if (creature?.WoundIntent is not { } intent || intent.Tick != contestTick)
            {
                break;
            }

            survived++;
            if (creature.LastDecision.ReasonCode is "combat_spared_wound" or "combat_pressed_wound")
            {
                continue;
            }

            later = Panel(state, creatureId);
            overwrittenBy = creature.LastDecision.ReasonCode;
            break;
        }

        Assert.True(
            later is not null,
            $"{found.Cell}: {found.Name} decided `{code}` at t{contestTick} and the panel was never " +
            "observed carrying that decision on a tick where `lastDecision` had moved on. Either " +
            "the intent was cleared at once — in which case the line cannot survive a tick — or " +
            $"nothing ever overwrote the decision, in which case this check proves nothing. " +
            $"{survived} tick(s) were walked.");

        Assert.Contains("INTENT t" + contestTick.ToString(CultureInfo.InvariantCulture), later!, StringComparison.Ordinal);
        Assert.Contains(code == "spared" ? "sparing" : "standing on", later!, StringComparison.Ordinal);
        Assert.Contains($"WHY t", later!, StringComparison.Ordinal);
        output.WriteLine($"{found.Cell} t{contestTick} #{creatureId} {found.Name}, `{code}`:");
        output.WriteLine(later!);
        output.WriteLine($"last decision by then: {overwrittenBy}");
    }

    /// <summary>
    /// A whole creature carries no line at all. The contest does not ask it
    /// (§3.1) and the field is cleared when it mends its last part (§3.6), so a
    /// panel that kept saying «бережёт ногу» about a creature with no hurt leg
    /// would be the screen disagreeing with the canonical document.
    /// </summary>
    [Fact]
    public void A_whole_creature_carries_no_line_about_a_wound_it_does_not_have()
    {
        var state = PresentationFixtures.Baseline(120);
        var whole = state.Creatures.First(creature => creature.Injury == InjuryKind.None);

        Assert.Null(whole.WoundIntent);
        Assert.Equal(string.Empty, InspectorText.DescribeWoundIntent(whole));
        Assert.DoesNotContain("INTENT", Panel(state, whole.Id), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Channel 3 — the card of the moment of truth.
    // ------------------------------------------------------------------

    /// <summary>
    /// The price of coercion is printed in the breakdown of a card — on a
    /// <b>controlled</b> card, because which three creatures the pause reports on
    /// is chosen by notability (<c>PrototypeWorld.MomentOfTruth.cs</c>) and a
    /// check that waited for one particular creature to be among them would be
    /// asserting a fact about how a fight went.
    ///
    /// <para>The card is stated rather than found, which is stating the input and
    /// not faking the result: the rendering is a pure function of the card, and
    /// what the simulation puts in a card is the subject of
    /// <c>PrototypeMomentOfTruthTests</c>.</para>
    /// </summary>
    [Fact]
    public void The_card_prints_the_price_of_coercion_among_the_terms_of_the_grudge()
    {
        var card = CoercedCard();

        var line = HudText.MomentOfTruthCardLine(card);

        output.WriteLine(line);
        Assert.Contains("Дёготь", line, StringComparison.Ordinal);
        Assert.Contains("grudge 9", line, StringComparison.Ordinal);
        Assert.Contains("+6 sent into the line hurt", line, StringComparison.Ordinal);
        Assert.Contains("+3 punished for nothing", line, StringComparison.Ordinal);
        // The term is not printed as its code, which is the failure the wording
        // table exists to prevent.
        Assert.DoesNotContain("grudge_pressed_wounded", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the reason the check above is worth anything: a term nobody has worded
    /// is refused rather than printed raw. Without this the breakdown could
    /// silently start showing ledger codes to a player.
    /// </summary>
    [Fact]
    public void A_loyalty_term_nobody_worded_is_refused_rather_than_printed()
    {
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => HudText.TermName("grudge_something_nobody_wrote"));
        Assert.Contains("will not invent", thrown.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // The evidence file.
    // ------------------------------------------------------------------

    /// <summary>
    /// The three channels as they actually read, written down with the party they
    /// were read off. Criterion 10 asks for the measurement beside the command
    /// that produced it, and a rendered sentence is the measurement here.
    /// </summary>
    [Fact]
    public void The_three_channels_are_recorded()
    {
        var refusal = FirstContest("spared", judged: false);
        var refusalState = refusal.State;
        var refusalEvent = refusalState.Events.First(item => item.ReasonCode == "combat_spared_wound");

        var standing = FirstContest("pressed", judged: true);
        var standingState = standing.State;

        // Every term the two parties actually wrote has a wording. The card
        // channel is one `TermName` away from throwing in front of a player, and
        // nothing else in the repository checks the whole set.
        var unworded = refusalState.Creatures.Concat(standingState.Creatures)
            .SelectMany(creature => creature.Loyalty.FearTerms
                .Concat(creature.Loyalty.BenefitTerms)
                .Concat(creature.Loyalty.GrudgeTerms))
            .Select(term => term.Code)
            .Distinct(StringComparer.Ordinal)
            .Where(code => !IsWorded(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            unworded.Length == 0,
            $"the matrix wrote {unworded.Length} loyalty term(s) the moment of truth cannot say: " +
            string.Join(", ", unworded));

        var report = new
        {
            schemaVersion = 1,
            issue = "#431",
            checkpoint = "4 — подача: лента, панель, карточка",
            command =
                "dotnet test tests/DungeonFortress.Presentation.Tests " +
                "--filter FullyQualifiedName~WoundedIntentReadableTests",
            what =
                "Три канала подачи §4, каждый прочитан там, где он живёт: строка ленты — из " +
                "события настоящей партии, строка панели — из опубликованного `woundIntent` на " +
                "тике, где `lastDecision` уже другое, разбор карточки — на контролируемой " +
                "карточке, потому что попадание существа в тройку заметнейших не гарантировано.",
            feed = new
            {
                cell = refusal.Cell,
                spared = new
                {
                    tick = refusalEvent.FirstTick,
                    sentence = EventNarration.Describe(refusalState, refusalEvent),
                    count = refusalState.Events.Count(item => item.ReasonCode == "combat_spared_wound"),
                    verdictNamed = false,
                    why =
                        "Партия без единого вердикта: выгода набрана `benefit_fed` и " +
                        "`benefit_tended`, и правило причинности §3.5 запрещает называть " +
                        "вердикт причиной.",
                },
                pressed = new
                {
                    sentence = EventNarration.Sentence("combat_pressed_wound", Pressed(), null, null),
                    from = "details, не из партии",
                    why =
                        "`combat_pressed_wound` пишется только там, где страх перед владением " +
                        "и был причиной, а это, по записи 38 журнала #415, редкое событие: " +
                        "одно начисление на всю матрицу. Строка ленты наследует эту редкость. " +
                        "Общий случай «раненый полез» несёт панель, а не лента.",
                },
            },
            panel = new
            {
                cell = standing.Cell,
                tick = standing.IntentTick,
                creature = standing.Name,
                line = InspectorText.DescribeWoundIntent(
                    standingState.Creatures.Single(item => item.Id == standing.CreatureId)),
                source = "creatures[].woundIntent",
                notFrom = "lastDecision — перекличка идёт до GenerateJobs/MatchJobs",
            },
            card = new
            {
                line = HudText.MomentOfTruthCardLine(CoercedCard()),
                controlled = true,
                why =
                    "Отбор трёх заметнейших не гарантирует попадания конкретного существа " +
                    "(PrototypeWorld.MomentOfTruth.cs), поэтому карточка задана, а не найдена.",
            },
        };

        File.WriteAllText(
            Path.Combine(PresentationFixtures.FindRepositoryRoot(), "evidence", "431-presentation.json"),
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }) + Environment.NewLine);
    }

    // ------------------------------------------------------------------
    // Fixtures.
    // ------------------------------------------------------------------

    private static Dictionary<string, int> Spared() => new(StringComparer.Ordinal)
    {
        ["spare"] = 7,
        ["press"] = 4,
        ["part"] = (int)BodyPart.Leg,
        ["severity"] = (int)InjuryKind.Heavy,
        ["verdictDecided"] = 0,
        ["wave"] = 3,
    };

    private static Dictionary<string, int> Pressed() => new(StringComparer.Ordinal)
    {
        ["spare"] = 5,
        ["press"] = 6,
        ["part"] = (int)BodyPart.Leg,
        ["grudge"] = 9,
        ["wave"] = 3,
    };

    private static PrototypeMomentOfTruthCard CoercedCard() => new(
        CreatureId: 4,
        Name: "Дёготь",
        Loyalty: new PrototypeLoyaltySnapshot(
            Fear: 11,
            Benefit: 4,
            Grudge: 9,
            FearTerms: [new PrototypeLoyaltyTerm("fear_punished", 6), new PrototypeLoyaltyTerm("fear_wound", 5)],
            BenefitTerms: [new PrototypeLoyaltyTerm("benefit_fed", 4)],
            GrudgeTerms:
            [
                new PrototypeLoyaltyTerm("grudge_pressed_wounded", 6),
                new PrototypeLoyaltyTerm("grudge_punished_unfairly", 3),
            ],
            GrudgeReleased: true,
            FearOfTheDomain: 6),
        FearThisWave: 5,
        BenefitThisWave: 0,
        GrudgeThisWave: 6,
        RaidersDowned: 0,
        DominantAxis: "grudge",
        Notability: 9,
        Verdict: null);

    private static bool IsWorded(string code)
    {
        try
        {
            HudText.TermName(code);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string Panel(PrototypeSnapshot state, int creatureId) =>
        InspectorText.Build(state.Shown(), creatureId, null);

    private sealed record Contest(
        string Cell,
        PrototypeWorld World,
        PrototypeSnapshot State,
        int CreatureId,
        string Name,
        int IntentTick);

    /// <summary>
    /// The first roll call in the matrix at which somebody decided
    /// <paramref name="code"/>, with the world left standing on that tick so that
    /// a caller can walk it forward.
    ///
    /// <para><paramref name="judged"/> chooses between the two parties the checks
    /// need: a domain that answered no card at all — the case criterion 5 is
    /// about — and the shipped probe journal, which is the one journal that
    /// carries verdicts of both signs.</para>
    /// </summary>
    private static Contest FirstContest(string code, bool judged)
    {
        var cells = judged
            ? new[] { ("probe-verdicts", 20_260_726UL) }.Concat(Cells).ToArray()
            : Cells;
        var walked = new List<string>();
        foreach (var (fixtureName, seed) in cells)
        {
            var log = PresentationFixtures.LogOf(fixtureName) with { Seed = seed };
            var world = new PrototypeWorld(log);
            var seen = 0;
            while (!world.IsComplete)
            {
                world.Step();
                var state = world.GetSnapshot();
                var decided = state.Creatures.FirstOrDefault(creature =>
                    creature.WoundIntent is { } intent &&
                    intent.Tick == state.Tick - 1 &&
                    intent.Code == code);
                if (decided is null)
                {
                    if (state.Creatures.Any(creature => creature.WoundIntent is not null))
                    {
                        seen++;
                    }

                    continue;
                }

                return new Contest(
                    $"{fixtureName}/{seed}",
                    world,
                    state,
                    decided.Id,
                    decided.Name,
                    decided.WoundIntent!.Tick);
            }

            walked.Add($"{fixtureName}/{seed} (ticks with some intent: {seen})");
        }

        throw new InvalidOperationException(
            $"No party in the matrix reached a contest decided `{code}`, so the channel under " +
            "test has no subject. Walked: " + string.Join("; ", walked));
    }
}
