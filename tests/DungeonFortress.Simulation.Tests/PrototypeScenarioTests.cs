using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

public sealed class PrototypeScenarioTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("baseline")]
    [InlineData("prepared")]
    [InlineData("neglected")]
    public void Same_seed_commands_and_tick_produce_identical_state_log_and_checksum(
        string fixtureName)
    {
        var commands = LoadFixture(fixtureName);
        var first = PrototypeScenario.Run(commands, PrototypeTuning.FirstRaidTick + 1);
        var second = PrototypeScenario.Run(commands, PrototypeTuning.FirstRaidTick + 1);

        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.CanonicalEventLog, second.CanonicalEventLog);
        Assert.Equal(first.Checksum, second.Checksum);
    }

    [Fact]
    public void Scenario_label_does_not_change_canonical_state()
    {
        const string template =
            """
            {"schemaVersion":2,"scenario":"SCENARIO","seed":42,"commands":[]}
            """;
        var baseline = PrototypeCommandDocument.Parse(
            Encoding.UTF8.GetBytes(template.Replace("SCENARIO", "baseline")));
        var custom = PrototypeCommandDocument.Parse(
            Encoding.UTF8.GetBytes(template.Replace("SCENARIO", "custom")));

        Assert.Equal(
            PrototypeScenario.Run(baseline, 64).Checksum,
            PrototypeScenario.Run(custom, 64).Checksum);
    }

    [Fact]
    public void Changing_seed_changes_canonical_state()
    {
        var fixture = LoadFixture("baseline");
        var changed = fixture with { Seed = fixture.Seed + 1 };

        var original = PrototypeScenario.Run(fixture, 128);
        var alternate = PrototypeScenario.Run(changed, 128);

        Assert.NotEqual(
            original.State.Creatures
                .Select(creature => (creature.Satiety, creature.Fatigue))
                .ToArray(),
            alternate.State.Creatures
                .Select(creature => (creature.Satiety, creature.Fatigue))
                .ToArray());
        Assert.NotEqual(original.State.Events, alternate.State.Events);
    }

    [Fact]
    public void Changing_a_relevant_indirect_command_changes_canonical_state()
    {
        const string baselineJson =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[]}
            """;
        const string changedJson =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[
              {"tick":0,"kind":"set_priority","jobKind":"Harvest","value":0}
            ]}
            """;

        var baseline = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(baselineJson));
        var changed = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(changedJson));
        Assert.NotEqual(
            PrototypeScenario.Run(baseline, 128).Checksum,
            PrototypeScenario.Run(changed, 128).Checksum);
    }

    [Fact]
    public void Tile_order_step_size_and_current_culture_do_not_change_state()
    {
        const string firstJson =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[
              {"tick":0,"kind":"zone_paint","zoneKind":"Watch","tiles":[[3,2],[2,2]]}
            ]}
            """;
        const string secondJson =
            """
            {"schemaVersion":2,"scenario":"custom","seed":42,"commands":[
              {"tick":0,"kind":"zone_paint","zoneKind":"Watch","tiles":[[2,2],[3,2]]}
            ]}
            """;
        var firstLog = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(firstJson));
        var secondLog = PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(secondJson));
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            var first = new PrototypeWorld(firstLog);
            first.RunTicks(128);

            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            var second = new PrototypeWorld(secondLog);
            for (var tick = 0; tick < 128; tick++)
            {
                second.Step();
            }

            Assert.Equal(
                PrototypeScenario.Capture(first).CanonicalJson,
                PrototypeScenario.Capture(second).CanonicalJson);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Gameplay_v2_rejects_addressing_unknown_fields_and_invalid_bounds()
    {
        var addressed =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"set_priority","jobKind":"Harvest","value":3,"creatureId":4}
            ]}
            """;
        var outOfBounds =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"zone_paint","zoneKind":"Farm","tiles":[[28,1]]}
            ]}
            """;
        var legacy =
            """
            {"schemaVersion":1,"scenario":"custom","seed":1,"commands":[]}
            """;

        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(addressed)));
        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(outOfBounds)));
        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(legacy)));
    }

    [Fact]
    public void Semantic_error_rejects_the_whole_document_before_world_creation()
    {
        const string json =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"zone_erase","zoneKind":"Larder",
               "tiles":[[14,7],[15,7]]}
            ]}
            """;
        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json)));

        var direct = new PrototypeCommandLog(
            "custom",
            1,
            [
                new SetPriorityCommand(0, JobKind.Harvest, 4),
                new ZoneEraseCommand(
                    1,
                    ZoneKind.Larder,
                    [new(14, 7), new(15, 7)]),
            ]);
        Assert.Throws<InvalidDataException>(() => new PrototypeWorld(direct));
    }

    [Fact]
    public void Invalid_later_command_rejects_the_whole_document_before_a_world_exists()
    {
        const string json =
            """
            {"schemaVersion":2,"scenario":"custom","seed":1,"commands":[
              {"tick":0,"kind":"set_priority","jobKind":"Harvest","value":4},
              {"tick":1,"kind":"direct_order","creatureId":2}
            ]}
            """;

        Assert.Throws<InvalidDataException>(
            () => PrototypeCommandDocument.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Baseline_completes_the_economic_chain_and_exposes_structured_state()
    {
        var result = PrototypeScenario.Run(
            LoadFixture("baseline"),
            PrototypeTuning.FirstRaidTick + 1);

        Assert.True(result.State.Stocks.MealsProduced > 0);
        Assert.True(result.State.Stocks.MealsEaten > 0);
        Assert.True(result.State.Economy.HarvestsCompleted > 0);
        Assert.True(result.State.Economy.RawHaulsCompleted > 0);
        Assert.True(result.State.Economy.CookBatchesCompleted > 0);
        Assert.True(result.State.Economy.MealHaulsCompleted > 0);
        Assert.True(result.State.Economy.MealsEaten > PrototypeTuning.StartMeals);
        Assert.Equal(9, result.State.Creatures.Count);
        Assert.All(result.State.Creatures, creature =>
        {
            Assert.False(string.IsNullOrWhiteSpace(creature.Name));
            Assert.False(string.IsNullOrWhiteSpace(creature.LastDecision.ReasonCode));
            Assert.NotNull(creature.ReadinessAtRaid);
        });
        Assert.Contains(result.State.Events, @event => @event.ReasonCode == "chosen_need_hunger");
        Assert.Contains(
            result.State.Events,
            @event => @event.JobKind is not null);
    }

    /// <summary>
    /// The contract corridors of 13.4, re-measured for a party of waves. The
    /// comparison moment is the arrival of the first wave, because that is the
    /// last tick at which all three scenarios are still comparable: after it the
    /// three domains stop living the same life.
    ///
    /// <c>neglected</c> no longer reaches a wave at all. Forbidding the harvest
    /// empties the larder by tick ~500 and leaves nobody above the exhaustion
    /// threshold to refill it, which is now stated as an end of the party
    /// instead of being played out as eight hundred ticks of standing still.
    /// </summary>
    [Fact]
    public void Contract_scenarios_satisfy_the_precombat_invariants_of_a_wave_party()
    {
        var baseline = PrototypeScenario.Run(
            LoadFixture("baseline"),
            PrototypeTuning.FirstRaidTick + 1);
        var prepared = PrototypeScenario.Run(
            LoadFixture("prepared"),
            PrototypeTuning.FirstRaidTick + 1);
        var neglected = PrototypeScenario.Run(
            LoadFixture("neglected"),
            PrototypeTuning.SessionTicks);
        var baselineEnd = PrototypeScenario.Run(
            LoadFixture("baseline"),
            PrototypeTuning.SessionTicks);
        var preparedEnd = PrototypeScenario.Run(
            LoadFixture("prepared"),
            PrototypeTuning.SessionTicks);

        var readiness = (
            Baseline: AverageReadiness(baseline),
            Prepared: AverageReadiness(prepared));
        Assert.True(
            readiness.Prepared > readiness.Baseline,
            Describe(baseline, prepared, neglected, readiness));
        Assert.All(baseline.State.Creatures, creature => Assert.Equal(0, creature.MartialForm));
        Assert.Contains(
            neglected.State.Events,
            @event => @event.ReasonCode == "refused_rule_min_satiety");
        Assert.Contains(
            neglected.State.Events,
            @event => @event.ReasonCode == "refused_too_exhausted");
        Assert.Contains(
            prepared.State.Events,
            @event => @event.ReasonCode == "chosen_muster");
        Assert.Contains(
            prepared.State.Events,
            @event => @event.ReasonCode == "chosen_ration");
        // `combat_refused_starving` used to be asserted here, on `preparedEnd`
        // and over the matrix only. It has moved into AssertEndOfPartyInvariants
        // and onto the party that witnesses it — read the note there before
        // moving it back.
        Assert.InRange(baseline.State.Creatures.Average(c => c.Satiety), 45, 75);
        Assert.InRange(prepared.State.Creatures.Average(c => c.Satiety), 45, 75);
        Assert.InRange(neglected.State.Creatures.Average(c => c.Satiety), 0, 15);
        // Re-measured on the dungeon of Issue #117 across the whole matrix, at
        // the arrival of the first wave: readiness 46/47/48 for baseline against
        // 53/57/59 for prepared, and martial form 0 against 20/48/49. Both
        // corridors moved down and towards each other, because walls cost the
        // domain logistics and the preparation is what pays for them first. The
        // ordering — which is the invariant, and what contract 13.4 requires — is
        // unchanged and holds on every seed.
        Assert.InRange(readiness.Baseline, 38, 55);
        Assert.InRange(readiness.Prepared, 50, 70);
        Assert.True(prepared.State.Creatures.Average(c => c.MartialForm) >= 20);
        Assert.True(
            baselineEnd.State.Stocks.MealsProduced is >= 95 and <= 130 &&
            preparedEnd.State.Stocks.MealsProduced is >= 70 and <= 110 &&
            neglected.State.Stocks.MealsProduced is >= 0 and <= 6,
            $"end production baseline={baselineEnd.State.Stocks.MealsProduced}, " +
            $"prepared={preparedEnd.State.Stocks.MealsProduced}, " +
            $"neglected={neglected.State.Stocks.MealsProduced}");

        // Neither fixture holds the domain: both let a wave through, so both end
        // `raided`. That is the honest reading of what they actually did, and it
        // is why the third end form exists.
        Assert.Equal("raided", baselineEnd.State.SessionResult.Outcome);
        Assert.Equal("raided", preparedEnd.State.SessionResult.Outcome);
        Assert.Equal("fallen", neglected.State.SessionResult.Outcome);
        AssertEndOfPartyInvariants(baselineEnd.State, preparedEnd.State, neglected.State);
        AssertFirstWaveInvariants(baseline.State, prepared.State);
    }

    /// <summary>
    /// Contract 13.4 says its invariants hold "for any seed of the matrix". That
    /// sentence used to be prose while the test ran one seed, which is how a
    /// broken invariant survived: `renown(prepared) > renown(baseline)` is false
    /// on seed 20260727 and nothing said so. The claim is now executed.
    /// </summary>
    [Theory]
    [InlineData(20_260_726UL)]
    [InlineData(20_260_727UL)]
    [InlineData(20_260_728UL)]
    public void The_contract_invariants_hold_on_every_seed_of_the_matrix(ulong seed)
    {
        AssertEndOfPartyInvariants(
            RunAtSeed("baseline", seed, PrototypeTuning.SessionTicks),
            RunAtSeed("prepared", seed, PrototypeTuning.SessionTicks),
            RunAtSeed("neglected", seed, PrototypeTuning.SessionTicks));
        AssertFirstWaveInvariants(
            RunAtSeed("baseline", seed, PrototypeTuning.FirstRaidTick + 1),
            RunAtSeed("prepared", seed, PrototypeTuning.FirstRaidTick + 1));
    }

    /// <summary>
    /// The half of invariant 11 that the seed used to carry: <b>preparation
    /// outscores its absence.</b>
    ///
    /// <para>
    /// Until Issue #129 this was asserted seed by seed, and 13.4 already recorded
    /// why that was uncomfortable — «зазор меньше разброса», the gap between the
    /// two plans is smaller than the spread of either across seeds. The approach
    /// rule narrowed the gap further, because it helps the plan with the worse
    /// geometry more: on seed 20260727 <c>baseline</c> gains a repelled wave and
    /// finishes at 831 against prepared's 803. The owner accepted that price on
    /// 2026-08-01 and asked for a ground that says what is promised after it.
    /// </para>
    ///
    /// <para>
    /// What is promised is the matrix, not the seed: over the three seeds
    /// together preparation scores more than its absence. That is not a weaker
    /// version of the old claim fitted to the new run — it holds on <c>main</c>
    /// as well (2525 against 2193) and after #129 (2526 against 1366), and it is
    /// the level at which 13.4 has always said its corridors mean anything. The
    /// per-seed half of the promise survives as the band comparison in
    /// <c>AssertEndOfPartyInvariants</c>: preparation may cost score on a seed,
    /// but it may not end the domain worse off than doing nothing.
    /// </para>
    /// <para>
    /// What the margin is made of, named because the number can mislead. It used
    /// to mislead: before Issue #171 the per-seed gaps were +38, -28 and +1150,
    /// so 1150 of the 1160 came from baseline/20260728 — the party that won its
    /// fights and starved — and on the two seeds where both parties survived
    /// preparation won by 10 points out of roughly 1600. That was measured by the
    /// independent review of #129, which asked for the clause to be rechecked
    /// when #171 closed.
    /// </para>
    ///
    /// <para>
    /// Rechecked, and it is no longer true: with the price of memory of place
    /// bounded the gaps are +92, -28 and +152, prepared 2546 against baseline
    /// 2330, and no single seed carries the claim. The margin is smaller and it
    /// is spread over the matrix, which is the level 13.4 has always said its
    /// corridors mean anything at. Figures in <c>evidence/171-after.json</c>.
    /// </para>
    ///
    /// <para>
    /// Command:
    /// <c>dotnet test tests/DungeonFortress.Simulation.Tests -c Release --filter
    /// "FullyQualifiedName~Preparation_outscores_its_absence_over_the_matrix"
    /// --logger "console;verbosity=detailed"</c>. Both trees are in
    /// <c>evidence/129-invariants.json</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void Preparation_outscores_its_absence_over_the_matrix()
    {
        var seeds = new[] { 20_260_726UL, 20_260_727UL, 20_260_728UL };
        var prepared = 0;
        var baseline = 0;
        var report = new StringBuilder();
        foreach (var seed in seeds)
        {
            var preparedEnd = RunAtSeed("prepared", seed, PrototypeTuning.SessionTicks);
            var baselineEnd = RunAtSeed("baseline", seed, PrototypeTuning.SessionTicks);
            prepared += Score(preparedEnd);
            baseline += Score(baselineEnd);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{seed}: prepared {Score(preparedEnd)} ({preparedEnd.SessionResult.Outcome}), " +
                $"baseline {Score(baselineEnd)} ({baselineEnd.SessionResult.Outcome})");
        }

        output.WriteLine(report.ToString());
        Assert.True(
            prepared > baseline,
            $"over the matrix preparation scored {prepared} and its absence {baseline}. The one " +
            "thing the party score is for is ranking how well the domain was played, and a plan " +
            $"that stops paying over three seeds has stopped being a plan.{Environment.NewLine}" +
            $"{report}");
    }

    /// <summary>
    /// The invariants read where the fixtures are still comparable: the arrival
    /// of the first wave. Both are condition numbers, and condition is only
    /// comparable at the same moment of the same story — read at the end of the
    /// party, every fixture is starving and every number collapses towards zero.
    /// </summary>
    private static void AssertFirstWaveInvariants(
        PrototypeSnapshot baseline,
        PrototypeSnapshot prepared)
    {
        Assert.True(
            AverageReadiness(prepared) > AverageReadiness(baseline),
            $"readiness at wave 1 prepared={AverageReadiness(prepared)}, " +
            $"baseline={AverageReadiness(baseline)}");
        Assert.True(
            prepared.Domain.Strength > baseline.Domain.Strength,
            $"strength at wave 1 prepared={prepared.Domain.Strength}, " +
            $"baseline={baseline.Domain.Strength}");
    }

    /// <summary>
    /// The invariants of 13.4 read at the end of the party. What is deliberately
    /// absent is as load-bearing as what is here: renown ranks a domain that
    /// lives against one that died, and does not reliably rank two living plans
    /// on a single party, because it leans on raiders put down and that number
    /// swings with combat jitter. Ranking the plans is the party score's job
    /// now, and it is asserted here rather than described in prose — that is
    /// the whole point of ADR 0016.
    /// </summary>
    private static void AssertEndOfPartyInvariants(
        PrototypeSnapshot baseline,
        PrototypeSnapshot prepared,
        PrototypeSnapshot neglected)
    {
        // Invariant 5, the half about hunger, moved here from
        // Contract_scenarios_satisfy_the_precombat_invariants_of_a_wave_party and
        // re-homed onto the party that witnesses it.
        //
        // The promise is that hunger can refuse a creature the line and that it
        // is observable in a shipped party. It was asked of `prepared` and «over
        // the matrix rather than on every seed», and both of those are the scar
        // of one owner's decision of 2026-07-31: the reference plan was rewritten
        // for the dungeon, and under the new plan `prepared` gave 13, 21 and 0 of
        // these events. The fixture was never part of the claim; the claim is
        // about the reason code.
        //
        // Measured on this branch at a join threshold of 30, over a whole party
        // at SessionTicks: `prepared` gives 0, 0, 0 over the matrix and `baseline`
        // gives 26, 5 and 3 — on every seed. That is what a prepared domain means:
        // it rations before the wave, so nobody is caught under the threshold. So
        // the fixture that carried the promise has stopped being able to, and the
        // one that always could is now asked instead — on every seed, which is the
        // strength the 2026-07-31 weakening gave up.
        //
        // What this is NOT: a promise retired as unreachable. The reason code is
        // reached three to twenty-six times a party. Figures and commands are in
        // evidence/333-starving-reachability.json.
        Assert.Contains(
            baseline.Events,
            @event => @event.ReasonCode == "combat_refused_starving");

        Assert.True(
            baseline.Domain.Renown > neglected.Domain.Renown &&
            prepared.Domain.Renown > neglected.Domain.Renown,
            $"renown baseline={baseline.Domain.Renown}, prepared={prepared.Domain.Renown}, " +
            $"neglected={neglected.Domain.Renown}");

        // Invariant 11, first half, in the form the owner accepted on 2026-08-01:
        // a party that survived its four waves outranks a party that fell, on the
        // same seed. It used to name the fixtures — baseline and prepared above
        // neglected — and that reading stopped being about survival the moment a
        // fixture other than `neglected` could fall, which Issue #129 made
        // possible. Naming the outcome instead says the same thing about more
        // pairs and takes the fixture names out of a claim that was never about
        // them. It holds on both trees; see evidence/129-invariants.json.
        var parties = new (string Name, PrototypeSnapshot State)[]
        {
            ("baseline", baseline), ("prepared", prepared), ("neglected", neglected),
        };
        foreach (var lived in parties.Where(party => Survived(party.State)))
        {
            foreach (var fell in parties.Where(party => !Survived(party.State)))
            {
                Assert.True(
                    Score(lived.State) > Score(fell.State),
                    $"{lived.Name} survived with score {Score(lived.State)} and {fell.Name} fell " +
                    $"with {Score(fell.State)}: a party that lived has to outrank one that did " +
                    "not, and the score is the only thing that says so (10.8, ADR 0016).");
            }
        }

        // Invariant 11, second half, in the form the owner accepted on 2026-08-01
        // together with the approach rule of Issue #129. It used to be
        // `score(prepared) > score(baseline)` on every seed. That is a claim
        // about the *gap* between the two plans, and the approach rule narrows
        // the gap because it helps the weaker geometry more: on seed 20260727
        // `baseline` gains a repelled wave and 831 beats 803. The owner accepted
        // that price, so what is promised now is what preparation is actually
        // for — it must never end the party in a worse band than its absence,
        // and over the matrix it must still score more.
        Assert.True(
            Band(prepared) >= Band(baseline),
            $"prepared ended the party as {prepared.SessionResult.Outcome} and baseline as " +
            $"{baseline.SessionResult.Outcome}: preparation may cost score on a seed, but it may " +
            "not end the domain in a worse state than doing nothing.");

        // Invariant 4 in the form the owner accepted on 2026-08-01: preparation
        // makes the raid cheaper, measured as one price rather than two counts.
        //
        // It used to be two separate comparisons — meals stolen and defenders
        // broken by morale — and two counts that can trade against each other are
        // two claims, not one. After #129 they do trade: on seed 20260727
        // `prepared` is robbed of 12 fewer meals and breaks 5 more defenders, so
        // the second count fails while the raid is plainly cheaper. The price is
        // the score's own cost side (10.8) with the owner's own weights, so this
        // is not a new metric invented here; and it counts every defender the
        // domain lost rather than only the ones who ran, which stops preparation
        // from buying the count by trading a flight for a downing.
        //
        // It is read per wave the party actually resolved, and that is what makes
        // seed 20260728 a fair comparison rather than an exception: `baseline`
        // falls there before its fourth wave, so it pays for three waves against
        // prepared's four, and comparing the totals would have flattered the
        // party that died. The old form's failure on that seed was exactly this
        // artefact.
        var costPrepared = RaidCost(prepared);
        var costBaseline = RaidCost(baseline);
        var wavesPrepared = Math.Max(1, prepared.SessionResult.WavesResolved);
        var wavesBaseline = Math.Max(1, baseline.SessionResult.WavesResolved);
        Assert.True(
            costPrepared * wavesBaseline < costBaseline * wavesPrepared,
            $"the raid cost prepared {costPrepared} over {wavesPrepared} resolved wave(s) and " +
            $"baseline {costBaseline} over {wavesBaseline}: preparation has stopped making the " +
            $"raid cheaper. Stolen {prepared.SessionResult.MealsStolen}/" +
            $"{baseline.SessionResult.MealsStolen}, defenders lost " +
            $"{prepared.SessionResult.DefendersDowned + prepared.SessionResult.DefendersFled}/" +
            $"{baseline.SessionResult.DefendersDowned + baseline.SessionResult.DefendersFled}.");
    }

    /// <summary>
    /// What the raid took out of the domain, in the currency the party score
    /// already uses: meals carried out of the gate and defenders the domain no
    /// longer has, at the weights of 10.8. Reading the price rather than its two
    /// halves is what stops one half being bought with the other.
    /// </summary>
    private static int RaidCost(PrototypeSnapshot state) =>
        state.SessionResult.MealsStolen * PrototypeTuning.ScorePerMealStolen +
        (state.SessionResult.DefendersDowned + state.SessionResult.DefendersFled) *
            PrototypeTuning.ScorePerDefenderLost;

    private static bool Survived(PrototypeSnapshot state) =>
        state.SessionResult.Outcome is "held" or "raided";

    private static int Band(PrototypeSnapshot state) => state.SessionResult.Outcome switch
    {
        "held" => 2,
        "raided" => 1,
        _ => 0,
    };

    /// <summary>
    /// The score of a party that ended. A party without one has not ended, and
    /// reading it as a zero would rank an interrupted run against finished ones.
    /// </summary>
    private static int Score(PrototypeSnapshot state) =>
        state.SessionResult.Score ??
        throw new InvalidOperationException(
            $"The party did not end (outcome {state.SessionResult.Outcome ?? "null"}, " +
            $"unresolved {state.SessionResult.Unresolved}), so it has no score to compare.");

    private static int CountEvents(PrototypeSnapshot state, string reasonCode) =>
        state.Events.Where(@event => @event.ReasonCode == reasonCode).Sum(@event => @event.Repeats);

    private static PrototypeSnapshot RunAtSeed(string fixtureName, ulong seed, int ticks) =>
        PrototypeScenario.Run(LoadFixture(fixtureName) with { Seed = seed }, ticks).State;

    private static int AverageReadiness(PrototypeSnapshot state) =>
        (int)state.Creatures.Average(creature => creature.ReadinessAtRaid!.Value);

    [Fact]
    public void Replay_from_loaded_command_log_is_byte_identical()
    {
        var path = FixturePath("prepared");
        var first = PrototypeScenario.Run(
            PrototypeCommandDocument.Load(path),
            PrototypeTuning.FirstRaidTick + 1);
        var replay = PrototypeScenario.Run(
            PrototypeCommandDocument.Load(path),
            PrototypeTuning.FirstRaidTick + 1);

        Assert.Equal(first.CanonicalJson, replay.CanonicalJson);
    }

    [Fact]
    public void Performance_sanity_completes_three_full_parties()
    {
        var results = new List<PrototypeRunResult>();
        foreach (var scenario in new[] { "baseline", "prepared", "neglected" })
        {
            results.Add(PrototypeScenario.Run(
                LoadFixture(scenario),
                PrototypeTuning.SessionTicks));
        }

        Assert.Equal(3, results.Count);
        // A party ends on its own tick, so what is asserted is that it ended at
        // all and inside the fuse — not that all three ended together.
        Assert.All(results, result => Assert.NotNull(result.State.SessionResult.Outcome));
        Assert.All(results, result => Assert.InRange(result.Tick, 1, PrototypeTuning.SessionTicks));
    }

    [Fact]
    public void Preparation_changes_the_deterministic_party_without_direct_orders()
    {
        var prepared = PrototypeScenario.Run(LoadFixture("prepared"), PrototypeTuning.SessionTicks);
        var baseline = PrototypeScenario.Run(LoadFixture("baseline"), PrototypeTuning.SessionTicks);
        var preparedReplay = PrototypeScenario.Run(LoadFixture("prepared"), PrototypeTuning.SessionTicks);

        Assert.Equal(
            prepared.State.Waves.Where(wave => wave.Arrived).Sum(wave => wave.RaiderCount),
            prepared.State.Raiders.Count);
        Assert.Equal(
            baseline.State.Waves.Where(wave => wave.Arrived).Sum(wave => wave.RaiderCount),
            baseline.State.Raiders.Count);
        Assert.All(prepared.State.Waves, wave => Assert.NotNull(wave.Outcome));
        Assert.All(baseline.State.Waves, wave => Assert.NotNull(wave.Outcome));
        Assert.True(
            prepared.State.SessionResult.RaidersDowned >
            baseline.State.SessionResult.RaidersDowned,
            $"prepared put down {prepared.State.SessionResult.RaidersDowned} raiders, " +
            $"baseline {baseline.State.SessionResult.RaidersDowned}");
        Assert.True(
            prepared.State.Creatures.Average(creature => creature.ReadinessAtRaid!.Value) >
            baseline.State.Creatures.Average(creature => creature.ReadinessAtRaid!.Value));
        Assert.Equal(prepared.Checksum, preparedReplay.Checksum);
    }

    [Fact]
    public void Raid_steals_one_meal_per_period_and_preserves_meal_accounting()
    {
        var world = new PrototypeWorld(LoadFixture("baseline"));
        var theftTicks = new Dictionary<int, List<int>>();
        var previousCarrying = new Dictionary<int, int>();

        while (!world.IsComplete)
        {
            world.Step();
            var state = world.GetSnapshot();
            foreach (var raider in state.Raiders)
            {
                var before = previousCarrying.GetValueOrDefault(raider.Id);
                if (raider.CarryingMeals > before)
                {
                    Assert.Equal(before + 1, raider.CarryingMeals);
                    Assert.Equal(0, raider.StealTicks);
                    if (!theftTicks.TryGetValue(raider.Id, out var ticks))
                    {
                        ticks = [];
                        theftTicks.Add(raider.Id, ticks);
                    }
                    ticks.Add(state.Tick);
                }

                previousCarrying[raider.Id] = raider.CarryingMeals;
            }
        }

        Assert.NotEmpty(theftTicks.SelectMany(pair => pair.Value));
        Assert.All(theftTicks.Values, ticks =>
            Assert.All(ticks.Zip(ticks.Skip(1)), pair =>
                Assert.True(pair.Second - pair.First >= PrototypeTuning.StealPeriod)));

        var result = world.GetSnapshot();
        var looseMeals = result.LooseItems
            .Where(item => item.Resource == ResourceKind.Meal)
            .Sum(item => item.Quantity);
        Assert.Equal(
            PrototypeTuning.StartMeals + result.Stocks.MealsProduced,
            result.Stocks.Meals + looseMeals + result.Stocks.MealsEaten + result.SessionResult.MealsStolen);
        Assert.NotNull(result.SessionResult.Outcome);
    }

    [Fact]
    public void Defender_max_hp_comes_from_might_tuning()
    {
        var state = PrototypeScenario.Run(LoadFixture("prepared"), PrototypeTuning.FirstRaidTick).State;
        Assert.All(state.Creatures, creature =>
            Assert.Equal(
                PrototypeTuning.DefenderHpBase + creature.Might * PrototypeTuning.DefenderHpPerMight,
                creature.MaxHp));
    }

    /// <summary>
    /// A raider that reaches an empty larder turns round instead of standing
    /// there.
    ///
    /// The witness for this branch has now been wrong twice, both times without
    /// the branch changing. It was <c>neglected</c>, whose larder was empty
    /// before the raid started, until that domain began falling from hunger
    /// before a wave arrives. It became <c>baseline</c> at its default seed,
    /// until Issue #101 changed when defenders leave a fight and with it who is
    /// standing where while the larder empties. The third version — hunt the
    /// matrix for the same instant — was the same mistake with more surface: it
    /// looked for a raider standing on one tile on the one tick after the last
    /// portion left, and after the traffic change that instant survives in one
    /// cell of six.
    ///
    /// What this version asserts is split in two, and only the first half is the
    /// rule:
    ///
    /// - **the rule, everywhere.** In every party of the matrix, at every tick,
    ///   a raider that is standing on the larder tile with nothing to take has
    ///   turned back by the next tick. Quantified over all of it rather than
    ///   sampled, so it cannot pass by missing the case.
    /// - **coverage, once.** The branch is reached at all, witnessed by its own
    ///   lasting consequence rather than by its instant: a raider still inside
    ///   the domain, already heading for the gate, carrying nothing. Nothing else
    ///   in the simulation produces that state — the other way to start returning
    ///   is a full load — and unlike the instant it persists for the whole walk
    ///   back, measured at 18 to 162 raider-ticks in five cells of six.
    ///
    /// A built fixture was considered and rejected: an empty larder before the
    /// first wave is a starving domain, and a starving domain is dead before tick
    /// 1300, which is what retired the <c>neglected</c> witness in the first
    /// place. The state this branch needs is one raiders create, late, and the
    /// honest way to witness it is to watch for what it leaves behind.
    /// </summary>
    [Fact]
    public void Empty_larder_raider_turns_back_to_the_gate_instead_of_waiting()
    {
        var witnessed = new List<string>();
        var searched = new List<string>();

        foreach (var fixtureName in new[] { "baseline", "prepared" })
        {
            foreach (var seed in new[] { 20_260_726UL, 20_260_727UL, 20_260_728UL })
            {
                searched.Add($"{fixtureName}/{seed}");
                var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
                var pending = new List<int>();
                var turnedBackEmpty = 0;

                while (!world.IsComplete)
                {
                    var state = world.GetSnapshot();
                    foreach (var id in pending)
                    {
                        var moved = state.Raiders.Single(item => item.Id == id);
                        Assert.True(
                            moved.Mode != RaiderMode.Raiding || moved.ReturningToGate,
                            $"{fixtureName}/{seed}: raider {id} stood on the larder tile with an " +
                            $"empty larder and was still raiding forwards on tick {state.Tick}.");
                        Assert.Equal(0, moved.CarryingMeals);
                    }

                    pending.Clear();
                    pending.AddRange(state.Raiders
                        .Where(item =>
                            item.Mode == RaiderMode.Raiding &&
                            !item.ReturningToGate &&
                            item.CarryingMeals == 0 &&
                            item.Position == new GridPoint(14, 7) &&
                            state.Stocks.Meals == 0)
                        .Select(item => item.Id));

                    turnedBackEmpty += state.Raiders.Count(item =>
                        item.Mode == RaiderMode.Raiding &&
                        item.ReturningToGate &&
                        item.CarryingMeals == 0);
                    world.Step();
                }

                if (turnedBackEmpty > 0)
                {
                    witnessed.Add($"{fixtureName}/{seed} ({turnedBackEmpty} raider-ticks)");
                }
            }
        }

        Assert.True(
            witnessed.Count > 0,
            "No party of the matrix produced a raider heading for the gate empty-handed, " +
            "which is the only thing the empty-larder branch can leave behind. Searched " +
            string.Join(", ", searched) + ".");
    }

    private static int AverageReadiness(PrototypeRunResult result)
    {
        return (int)result.State.Creatures.Average(creature => creature.ReadinessAtRaid!.Value);
    }

    private static string Describe(
        PrototypeRunResult baseline,
        PrototypeRunResult prepared,
        PrototypeRunResult neglected,
        (int Baseline, int Prepared) readiness)
    {
        static string One(PrototypeRunResult result, int ready) =>
            $"ready={ready},sat={result.State.Creatures.Average(c => c.Satiety):F1}," +
            $"fat={result.State.Creatures.Average(c => c.Fatigue):F1}," +
            $"form={result.State.Creatures.Average(c => c.MartialForm):F1}," +
            $"made={result.State.Stocks.MealsProduced},ate={result.State.Stocks.MealsEaten}," +
            $"meals={result.State.Stocks.Meals},raw={result.State.Stocks.RawMushroom}," +
            $"looseRaw={result.State.Stocks.LooseRawMushroom}";
        return $"baseline[{One(baseline, readiness.Baseline)}] " +
            $"prepared[{One(prepared, readiness.Prepared)}] " +
            $"neglected[{One(neglected, 0)}]";
    }

    private static PrototypeCommandLog LoadFixture(string name)
    {
        return PrototypeCommandDocument.Load(FixturePath(name));
    }

    private static string FixturePath(string name)
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "scenarios",
            "prototype1",
            $"{name}.commands.v2.json");
    }

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
