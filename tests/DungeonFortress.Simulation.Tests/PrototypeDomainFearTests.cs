using System.Text;
using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Issue #431, checkpoint 1 — the derived magnitude <b>fear of the domain</b>.
///
/// <para>The whole formula of the contest rests on it, which is why it is a
/// checkpoint of its own and the first of the work. The three checks below are
/// the three <c>docs/design/VERDICT_AND_THE_WOUNDED.md</c> §3.3 names as
/// mandatory, one test apiece, and each says which of the three it is:</para>
///
/// <list type="number">
/// <item><description>without a single punishment the magnitude is nought at any
/// accumulated combat fear;</description></item>
/// <item><description>a punishment increases it;</description></item>
/// <item><description>after the term of the fade it is nought again, however
/// much combat fear was accumulated at the same time.</description></item>
/// </list>
///
/// <para>The first and the third are the ones that carry the argument. A
/// magnitude that merely mirrored <c>fear</c> would pass the second on its own,
/// and it is the two boundary readings that say the number is about the player
/// rather than about the fight.</para>
/// </summary>
public sealed class PrototypeDomainFearTests
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    /// <summary>
    /// Check 1 of §3.3. A whole party of four waves, played without a verdict:
    /// creatures are wounded, allies fall in front of them and nerves break, so
    /// combat fear is accumulated in quantity — and the fear <b>of the domain</b>
    /// stays at nought on every creature on every tick.
    ///
    /// <para>The last assertion is what stops the check from passing on an empty
    /// party: if nobody was ever frightened at all, the first half compared
    /// nought with nought and says nothing.</para>
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    public void Without_a_punishment_the_fear_of_the_domain_is_nought_at_any_combat_fear(
        string fixtureName)
    {
        var highestCombatFear = 0;
        foreach (var seed in MatrixSeeds)
        {
            var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
            while (!world.IsComplete)
            {
                world.Step();
                foreach (var creature in world.GetSnapshot().Creatures)
                {
                    highestCombatFear = Math.Max(highestCombatFear, creature.Loyalty.Fear);
                    Assert.True(
                        creature.Loyalty.FearOfTheDomain == 0,
                        $"{fixtureName}/{seed}, t{world.CurrentTick}: {creature.Name} is " +
                        $"afraid of the domain by {creature.Loyalty.FearOfTheDomain} in a " +
                        "party that contains no verdict at all. The magnitude is reading " +
                        "the fight, which is the one thing §3.3 exists to stop.");
                }
            }
        }

        Assert.True(
            highestCombatFear > 0,
            $"{fixtureName}: nobody in three parties was ever frightened by anything, so the " +
            "check compared nought with nought.");
    }

    /// <summary>
    /// Check 2 of §3.3, and the only one of the three that is about the accrual
    /// rather than about the boundary. The same party, the same tick, one verdict
    /// of difference — and it is compared against the <b>reward</b> arm rather
    /// than against silence, so what is measured is the sign of the judgement and
    /// not the act of answering.
    /// </summary>
    [Fact]
    public void A_punishment_increases_the_fear_of_the_domain_and_a_reward_does_not()
    {
        var open = RunToMomentOfTruth("baseline");
        var atTick = open.CurrentTick;
        var subject = open.GetSnapshot().MomentOfTruth.Cards[0].CreatureId;

        var silent = StepPastVerdict(LoadFixture("baseline"), atTick, subject);
        var punished = StepPastVerdict(
            WithVerdict(LoadFixture("baseline"), atTick, subject, VerdictKind.Punish),
            atTick,
            subject);
        var rewarded = StepPastVerdict(
            WithVerdict(LoadFixture("baseline"), atTick, subject, VerdictKind.Reward),
            atTick,
            subject);

        Assert.Equal(0, silent.Loyalty.FearOfTheDomain);
        Assert.Equal(0, rewarded.Loyalty.FearOfTheDomain);
        Assert.Equal(PrototypeTuning.LoyaltyVerdictPunishFear, punished.Loyalty.FearOfTheDomain);

        // And it moved the derived magnitude by exactly as much as it moved the
        // total, which is the claim that it is a *share* of the fear and not a
        // second, larger fright invented beside it.
        Assert.Equal(
            punished.Loyalty.Fear - silent.Loyalty.Fear,
            punished.Loyalty.FearOfTheDomain - silent.Loyalty.FearOfTheDomain);
    }

    /// <summary>
    /// Check 3 of §3.3, and the load-bearing one: after the term of the fade the
    /// magnitude is nought again <b>independently of the combat fear accumulated
    /// at the same time</b>.
    ///
    /// <para>The independence is the point and is measured rather than asserted.
    /// The punished creature is followed through the wave that arrives after its
    /// verdict — so its combat fear is still being credited while the domain fear
    /// is being forgotten — and the two are read side by side at the moment the
    /// term runs out. A fade that waited for quiet the way
    /// <c>FadeFear</c> does would still be holding the whole ten there, and this
    /// is the check that says it does not.</para>
    /// </summary>
    [Fact]
    public void After_the_term_of_the_fade_it_is_nought_whatever_the_fight_did()
    {
        var open = RunToMomentOfTruth("prepared");
        var atTick = open.CurrentTick;
        var subject = open.GetSnapshot().MomentOfTruth.Cards[0].CreatureId;

        var term = PrototypeTuning.LoyaltyVerdictPunishFear *
            PrototypeTuning.LoyaltyDomainFearFadePeriod;
        var world = new PrototypeWorld(
            WithVerdict(LoadFixture("prepared"), atTick, subject, VerdictKind.Punish));

        var readings = new List<(int Tick, int DomainFear, int Fear)>();
        var frightenedAfterTheVerdict = false;
        var combatFearAtTheVerdict = int.MinValue;
        while (!world.IsComplete && world.CurrentTick <= atTick + term)
        {
            world.Step();
            var creature = world.GetSnapshot().Creatures.Single(item => item.Id == subject);
            if (world.CurrentTick == atTick + 1)
            {
                combatFearAtTheVerdict = creature.Loyalty.Fear;
            }

            if (combatFearAtTheVerdict != int.MinValue &&
                creature.Loyalty.Fear > combatFearAtTheVerdict)
            {
                frightenedAfterTheVerdict = true;
            }

            // From past the frozen tick only. The window is open on `atTick` and
            // the world steps inside it without the clock moving, so the readings
            // of that tick straddle the command itself: the step before the
            // verdict arrives reads nought and the step after reads ten, which is
            // a rise and is the one rise this magnitude is allowed.
            if (world.CurrentTick > atTick)
            {
                readings.Add((world.CurrentTick, creature.Loyalty.FearOfTheDomain, creature.Loyalty.Fear));
            }
        }

        var last = readings[^1];
        Assert.True(
            last.DomainFear == 0,
            $"{term} ticks after the punishment the creature is still afraid of the domain " +
            $"by {last.DomainFear} (combat fear {last.Fear}). The term of the fade is " +
            "T.loyalty_verdict_punish_fear x T.loyalty_domain_fear_fade_period and nothing " +
            "may extend it.");

        // Monotone all the way down: the magnitude only ever falls between the
        // verdict and nought, so "it reached nought" is a fade and not a reset.
        for (var index = 1; index < readings.Count; index++)
        {
            Assert.True(
                readings[index].DomainFear <= readings[index - 1].DomainFear,
                $"t{readings[index].Tick}: the fear of the domain rose from " +
                $"{readings[index - 1].DomainFear} to {readings[index].DomainFear} with no " +
                "verdict between the two readings.");
        }

        // The independence claim, and the reason this is not the same test as
        // check 2 with a longer loop: the creature really was frightened by the
        // fight while the domain was being forgotten.
        Assert.True(
            frightenedAfterTheVerdict,
            "the punished creature's combat fear never moved after the verdict, so the third " +
            "check never faced the case it exists for — a fade running while the fight is still " +
            "crediting fright. Point it at a party whose next wave reaches this creature.");
    }

    /// <summary>
    /// The evidence file for checkpoint 1: the three checks above as numbers
    /// rather than as a green tick, so the PR body can name what was measured.
    /// </summary>
    [Fact]
    public void The_fade_curve_of_the_derived_magnitude_is_recorded()
    {
        var root = FindRepositoryRoot();
        var open = RunToMomentOfTruth("prepared");
        var atTick = open.CurrentTick;
        var subject = open.GetSnapshot().MomentOfTruth.Cards[0].CreatureId;
        var term = PrototypeTuning.LoyaltyVerdictPunishFear *
            PrototypeTuning.LoyaltyDomainFearFadePeriod;

        var world = new PrototypeWorld(
            WithVerdict(LoadFixture("prepared"), atTick, subject, VerdictKind.Punish));
        // Keyed by tick and overwritten, because the window is open on `atTick`
        // and the world steps inside it forty times without the clock moving: a
        // list would carry forty copies of the same reading and the curve would be
        // unreadable. The last writer wins, so the row for the frozen tick is the
        // one the verdict had already been applied on.
        var samples = new SortedDictionary<int, object>();
        while (!world.IsComplete && world.CurrentTick <= atTick + term)
        {
            world.Step();
            if (world.CurrentTick < atTick ||
                (world.CurrentTick - atTick) % PrototypeTuning.LoyaltyDomainFearFadePeriod != 0)
            {
                continue;
            }

            var creature = world.GetSnapshot().Creatures.Single(item => item.Id == subject);
            samples[world.CurrentTick] = new
            {
                tick = world.CurrentTick,
                sinceVerdict = world.CurrentTick - atTick,
                fearOfTheDomain = creature.Loyalty.FearOfTheDomain,
                fear = creature.Loyalty.Fear,
                fearTerms = creature.Loyalty.FearTerms.ToDictionary(t => t.Code, t => t.Amount),
            };
        }

        File.WriteAllText(
            Path.Combine(root, "evidence", "431-domain-fear.json"),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    issue = "#431",
                    checkpoint = "1 — производная величина «страх перед владением»",
                    command =
                        "dotnet test tests/DungeonFortress.Simulation.Tests " +
                        "--filter FullyQualifiedName~PrototypeDomainFearTests",
                    what =
                        "The fade curve of the derived magnitude on prepared/20260726 after a " +
                        "single punishment, sampled every T.loyalty_domain_fear_fade_period " +
                        "ticks, with the combat fear of the same creature beside it. The two " +
                        "columns are what check 3 of §3.3 compares: the derived magnitude walks " +
                        "to nought on its own clock while `fear` does whatever the fight makes " +
                        "it do.",
                    fixture = "prepared",
                    seed = 20260726,
                    subject,
                    verdictTick = atTick,
                    verdict = "punish",
                    loyaltyVerdictPunishFear = PrototypeTuning.LoyaltyVerdictPunishFear,
                    loyaltyDomainFearFadePeriod = PrototypeTuning.LoyaltyDomainFearFadePeriod,
                    termOfTheFade = term,
                    samples = samples.Values,
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }) + "\n",
            new UTF8Encoding(false));

        Assert.NotEmpty(samples);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private static PrototypeWorld RunToMomentOfTruth(string fixtureName)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName));
        while (!world.IsComplete && !world.IsAwaitingVerdict)
        {
            world.Step();
        }

        Assert.True(world.IsAwaitingVerdict, $"{fixtureName} never stopped between two waves.");
        return world;
    }

    private static PrototypeCreatureSnapshot StepPastVerdict(
        PrototypeCommandLog log,
        int atTick,
        int subject)
    {
        var world = new PrototypeWorld(log);
        while (!world.IsComplete && !world.IsAwaitingVerdict)
        {
            world.Step();
        }

        Assert.Equal(atTick, world.CurrentTick);
        world.Step();
        return world.GetSnapshot().Creatures.Single(item => item.Id == subject);
    }

    private static PrototypeCommandLog WithVerdict(
        PrototypeCommandLog log,
        int atTick,
        int creatureId,
        VerdictKind verdict) =>
        log with { Commands = [.. log.Commands, new VerdictCommand(atTick, creatureId, verdict)] };

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
