using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// The party score of ADR 0016. The properties asserted here are the ones the
/// ADR calls invariants; the weights behind them are tuning and may move without
/// touching this file.
/// </summary>
public sealed class PrototypePartyScoreTests
{
    private static int Score(
        string outcome = "raided",
        int wavesRepelled = 2,
        int survivors = 5,
        int mealsKept = 4,
        int mealsStolen = 12,
        int defendersLost = 7) =>
        PrototypePartyScore.Compute(
            outcome, wavesRepelled, survivors, mealsKept, mealsStolen, defendersLost);

    /// <summary>
    /// The first requirement of ADR 0016, stated the way the ADR states it —
    /// "all else equal". This is what renown could not do at any weights: it
    /// had no term at all that told a domain that stood from one that fell, so
    /// dying late scored better than surviving.
    /// </summary>
    [Fact]
    public void The_outcome_ranks_strictly_when_every_other_fact_is_equal()
    {
        Assert.True(Score(outcome: "held") > Score(outcome: "raided"));
        Assert.True(Score(outcome: "raided") > Score(outcome: "fallen"));
    }

    /// <summary>
    /// The score falls when the domain loses something. This is the property
    /// renown is forbidden to have — a falling renown would make impoverishment
    /// a strategy — and precisely why the two numbers had to be separated.
    /// </summary>
    [Fact]
    public void Losing_a_creature_or_supplies_lowers_the_score()
    {
        Assert.True(Score(survivors: 4) < Score(survivors: 5));
        Assert.True(Score(mealsKept: 3) < Score(mealsKept: 4));
        Assert.True(Score(mealsStolen: 13) < Score(mealsStolen: 12));
        Assert.True(Score(defendersLost: 8) < Score(defendersLost: 7));
    }

    [Fact]
    public void Turning_a_wave_back_raises_the_score()
    {
        Assert.True(Score(wavesRepelled: 3) > Score(wavesRepelled: 2));
    }

    /// <summary>
    /// What the score refuses to take is as load-bearing as what it takes, so
    /// the argument list itself is asserted. Two terms are absent on purpose:
    ///
    /// - waves that merely arrived, and the tick the party ended on — those
    ///   measure how long the domain lived, not how well it played, and paying
    ///   for them is exactly how renown came to rank a corpse above a survivor;
    /// - raiders put down — the noisiest number the party produces (30/60/45
    ///   against 70/45/55 on three seeds), which would rank combat luck.
    ///
    /// Adding either is a product decision that belongs in an ADR, and this
    /// test is what makes it impossible to make by accident.
    /// </summary>
    [Fact]
    public void The_score_has_no_term_for_time_lived_or_for_raiders_put_down()
    {
        var parameters = typeof(PrototypePartyScore)
            .GetMethod(nameof(PrototypePartyScore.Compute))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.Equal(
            ["outcome", "wavesRepelled", "survivors", "mealsKept", "mealsStolen", "defendersLost"],
            parameters);
    }

    [Fact]
    public void An_end_of_a_party_the_score_does_not_know_is_refused_rather_than_scored()
    {
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => Score(outcome: "besieged"));
        Assert.Contains("besieged", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second half of ADR 0016: the score is read once, at the end. A party
    /// in progress does not carry a provisional score, and the canonical state
    /// of one does not carry the field at all — so the checksum of a mid-party
    /// tick is what it was before the score existed, and a golden UI frame that
    /// moves is a leak rather than a chore.
    /// </summary>
    [Fact]
    public void A_party_in_progress_has_no_score_in_its_state_or_in_its_canonical_json()
    {
        var midParty = PrototypeScenario.Run(
            LoadFixture("baseline"),
            PrototypeTuning.FirstRaidTick + 1);

        Assert.Null(midParty.State.SessionResult.Outcome);
        Assert.Null(midParty.State.SessionResult.Score);

        using var document = JsonDocument.Parse(midParty.CanonicalJson);
        var sessionResult = document.RootElement.GetProperty("sessionResult");
        Assert.False(sessionResult.TryGetProperty("score", out _));
        // The rest of the summary is there all along; only the score waits for
        // the party to end.
        Assert.True(sessionResult.TryGetProperty("mealsStolen", out _));
        Assert.True(sessionResult.TryGetProperty("wavesRepelled", out _));
    }

    [Fact]
    public void A_finished_party_carries_the_score_its_own_facts_add_up_to()
    {
        var ended = PrototypeScenario.Run(LoadFixture("baseline"), PrototypeTuning.SessionTicks);
        var state = ended.State;
        var summary = state.SessionResult;

        Assert.NotNull(summary.Outcome);
        var score = Assert.IsType<int>(summary.Score);
        Assert.Equal(
            PrototypePartyScore.Compute(
                summary.Outcome!,
                summary.WavesRepelled,
                state.Creatures.Count(creature =>
                    creature.Mode != CreatureMode.Downed &&
                    creature.Satiety >= PrototypeTuning.CollapseThreshold),
                summary.MealsLeft,
                summary.MealsStolen,
                summary.DefendersDowned + summary.DefendersFled),
            score);

        using var document = JsonDocument.Parse(ended.CanonicalJson);
        Assert.Equal(
            score,
            document.RootElement.GetProperty("sessionResult").GetProperty("score").GetInt32());
    }

    /// <summary>
    /// A domain that fell scores no survivors without anyone writing that rule
    /// twice: "nobody left who can work and defend" is the same sentence the
    /// party score reads per creature.
    /// </summary>
    [Fact]
    public void A_fallen_domain_has_no_survivors_left_to_score()
    {
        var fallen = PrototypeScenario.Run(LoadFixture("neglected"), PrototypeTuning.SessionTicks).State;

        Assert.Equal("fallen", fallen.SessionResult.Outcome);
        Assert.Equal(
            0,
            fallen.Creatures.Count(creature =>
                creature.Mode != CreatureMode.Downed &&
                creature.Satiety >= PrototypeTuning.CollapseThreshold));
        Assert.Equal(PrototypeTuning.ScoreOutcomeFallen, fallen.SessionResult.Score);
    }

    private static PrototypeCommandLog LoadFixture(string name) =>
        PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(), "scenarios", "prototype1", $"{name}.commands.v2.json"));

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
