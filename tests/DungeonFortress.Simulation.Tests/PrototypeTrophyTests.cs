using DungeonFortress.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// docs/design/TROPHY_WEAPON.md. Every check here is a party played out on the
/// shipped fixtures, because the simulation has no seam for placing a weapon
/// by hand and is not going to get one: the rules are proved on what the
/// domain actually does.
/// </summary>
public sealed class PrototypeTrophyTests(ITestOutputHelper output)
{
    private const string Baseline = "baseline";
    private const string Prepared = "prepared";
    private static readonly ulong[] MatrixSeeds = [20_260_726, 20_260_727, 20_260_728];

    [Fact]
    public void A_fresh_world_carries_no_weapon_anywhere()
    {
        var state = new PrototypeWorld(LoadFixture(Baseline, PrototypeTuning.DefaultSeed)).GetSnapshot();
        output.WriteLine($"tick {state.Tick}: {state.Creatures.Count} creatures, {state.LooseWeapons.Count} loose weapons");

        Assert.Empty(state.LooseWeapons);
        Assert.All(state.Creatures, creature => Assert.Null(creature.Weapon));
    }

    [Fact]
    public void A_downed_raider_leaves_a_weapon_named_after_it_where_it_fell()
    {
        var drops = 0;
        Walk(Baseline, PrototypeTuning.DefaultSeed, (before, after) =>
        {
            foreach (var raider in after.Raiders.Where(raider => raider.Mode == RaiderMode.Downed))
            {
                var was = before.Raiders.SingleOrDefault(other => other.Id == raider.Id);
                if (was is null || was.Mode == RaiderMode.Downed)
                {
                    continue;
                }

                drops++;
                output.WriteLine($"t{after.Tick}: {raider.Name} (#{raider.Id}, might {raider.Might}) fell at ({raider.Position.X},{raider.Position.Y})");
                var weapon = Assert.Single(after.LooseWeapons, entry => entry.Weapon.RaiderId == raider.Id);
                Assert.Equal(raider.Position, weapon.Position);
                Assert.Equal(raider.Name, weapon.Weapon.Name);
                Assert.Equal(raider.Wave, weapon.Weapon.Wave);
                Assert.Equal(
                    Math.Max(PrototypeTuning.TrophyBonusBase, PrototypeTuning.TrophyBonusBase + raider.Might - PrototypeTuning.RaiderMightBase),
                    weapon.Weapon.Bonus);
                Assert.Contains(after.Creatures, creature => creature.Id == weapon.Weapon.DownedBy);
            }
        });

        Assert.True(drops > 0, "nobody was put down in the whole party, so the rule was never exercised");
    }

    [Fact]
    public void No_claim_is_offered_while_a_wave_is_inside_and_one_is_offered_after()
    {
        var offered = 0;
        foreach (var (fixture, seed) in Matrix())
        {
            Walk(fixture, seed, (_, after) =>
            {
                var claims = after.Jobs.Where(job => job.Kind == JobKind.Claim).ToArray();
                if (after.Threat.Active)
                {
                    Assert.Empty(claims);
                }

                offered += claims.Length;
            });
        }

        Assert.True(offered > 0, "the whole matrix never offered a claim, so nobody could ever pick a weapon up");
    }

    [Fact]
    public void The_weapon_is_taken_and_the_ledger_says_by_whom_and_at_whose_expense()
    {
        var taken = 0;
        var lost = 0;
        foreach (var (fixture, seed) in Matrix())
        {
            Walk(fixture, seed, (before, after) =>
            {
                foreach (var creature in after.Creatures)
                {
                    var was = before.Creatures.Single(other => other.Id == creature.Id);
                    if (creature.Weapon is null || was.Weapon is not null)
                    {
                        continue;
                    }

                    taken++;
                    var weapon = creature.Weapon;
                    output.WriteLine($"{fixture}/{seed} t{after.Tick}: {creature.Name} took the blade of {weapon.Name} (downed by #{weapon.DownedBy})");

                    // The floor no longer holds it, and the journal names the deed.
                    Assert.DoesNotContain(after.LooseWeapons, entry => entry.Weapon.RaiderId == weapon.RaiderId);
                    Assert.Contains(after.Events, e =>
                        e.CreatureId == creature.Id && e.ReasonCode == "trophy_taken" && e.LastTick >= after.Tick - 1
                        && e.Details["raiderId"] == weapon.RaiderId);
                    Assert.Contains(creature.Loyalty.BenefitTerms, term => term.Code == "benefit_trophy" && term.Amount > 0);

                    if (weapon.DownedBy == creature.Id)
                    {
                        continue;
                    }

                    var downer = after.Creatures.Single(other => other.Id == weapon.DownedBy);
                    if (downer.Mode is CreatureMode.Downed or CreatureMode.Fled)
                    {
                        Assert.DoesNotContain(downer.Loyalty.GrudgeTerms, term => term.Code == "grudge_trophy_taken");
                        continue;
                    }

                    lost++;
                    Assert.Contains(after.Events, e =>
                        e.CreatureId == downer.Id && e.ReasonCode == "trophy_lost"
                        && e.Details["raiderId"] == weapon.RaiderId && e.Details["takenBy"] == creature.Id);
                    Assert.Contains(downer.Loyalty.GrudgeTerms, term => term.Code == "grudge_trophy_taken" && term.Amount > 0);
                }
            });
        }

        output.WriteLine($"taken {taken}, lost {lost}");
        Assert.True(taken > 0, "nobody in the whole matrix ever picked a weapon up");
        Assert.True(lost > 0, "in the whole matrix the one who downed the raider always took the blade: the conflict the slice exists for never happened");
    }

    [Fact]
    public void A_claim_reads_the_pull_of_a_fighter_and_a_holder_never_claims_again()
    {
        var claims = 0;
        foreach (var (fixture, seed) in Matrix())
        {
            Walk(fixture, seed, (before, after) =>
            {
                foreach (var job in after.Jobs.Where(job => job.Kind == JobKind.Claim && job.ReservedBy is { }))
                {
                    var wasReserved = before.Jobs.Any(other => other.JobId == job.JobId && other.ReservedBy is { });
                    if (wasReserved)
                    {
                        continue;
                    }

                    claims++;
                    var taker = after.Creatures.Single(creature => creature.Id == job.ReservedBy);
                    var wasTaker = before.Creatures.Single(creature => creature.Id == job.ReservedBy);
                    Assert.Null(wasTaker.Weapon);
                    // The decision that *took* this job, and not simply the last
                    // one of the tick: a creature whose way out is blocked, or
                    // whose work is cancelled again, writes over `lastDecision`
                    // on the same tick it was given the job. The assignment is
                    // the decision that names this job and the score it won by.
                    var chosen = after.Events.Last(e =>
                        e.CreatureId == taker.Id &&
                        e.Details.GetValueOrDefault("jobId", -1) == job.JobId &&
                        e.Details.ContainsKey("score"));
                    output.WriteLine($"{fixture}/{seed} t{after.Tick}: {taker.Name} claims by {chosen.ReasonCode}, affinity {chosen.Details["affinity"]}");
                    Assert.Equal(taker.Affinities.GetValueOrDefault(JobKind.Drill), chosen.Details["affinity"]);
                }
            });
        }

        Assert.True(claims > 0, "no claim was ever assigned in the matrix");
    }

    [Fact]
    public void A_holder_strikes_harder_by_exactly_the_bonus()
    {
        var blows = 0;
        foreach (var (fixture, seed) in Matrix())
        {
            Walk(fixture, seed, (_, after) =>
            {
                foreach (var creature in after.Creatures)
                {
                    if (creature.Weapon is null ||
                        creature.LastDecision.ReasonCode != "combat_attack" ||
                        creature.LastDecision.Tick < after.Tick - 1 ||
                        creature.Injuries.Any(injury => injury.Part == BodyPart.Arm))
                    {
                        continue;
                    }

                    blows++;
                    var details = creature.LastDecision.Details;
                    Assert.Equal(creature.Weapon.Bonus, details["bonus"]);
                    // Damage is weight + readiness share + jitter in [-DamageJitter, DamageJitter],
                    // so with the bonus in the weight the blow can never fall further
                    // below the armed weight than the jitter allows.
                    var armedWeight = (creature.Might + creature.Weapon.Bonus) * PrototypeTuning.DamageMightWeight;
                    Assert.True(
                        details["damage"] + PrototypeTuning.DamageJitter >= armedWeight,
                        $"{creature.Name} with +{creature.Weapon.Bonus} struck for {details["damage"]}, below an armed weight of {armedWeight}");
                }
            });
        }

        Assert.True(blows > 0, "no armed creature with a whole arm ever struck in the matrix");
    }

    // ---- helpers shared by every test of this file ----

    internal static PrototypeCommandLog LoadFixture(string name, ulong seed)
    {
        var document = PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "scenarios",
            "prototype1",
            $"{name}.commands.v2.json"));
        return document.Seed == seed ? document : document with { Seed = seed };
    }

    /// <summary>
    /// Plays one party tick by tick and hands every consecutive pair of
    /// snapshots to <paramref name="visit"/>. The pair is what a rule about a
    /// transition ("when X becomes Y, Z is true") needs; a single end state is
    /// not enough because weapons are picked up and dropped again.
    /// </summary>
    internal static void Walk(
        string fixture,
        ulong seed,
        Action<PrototypeSnapshot, PrototypeSnapshot> visit)
    {
        var world = new PrototypeWorld(LoadFixture(fixture, seed));
        var before = world.GetSnapshot();
        while (!world.IsComplete)
        {
            world.Step();
            var after = world.GetSnapshot();
            visit(before, after);
            before = after;
        }
    }

    internal static IEnumerable<(string Fixture, ulong Seed)> Matrix()
    {
        foreach (var fixture in new[] { Baseline, Prepared })
        {
            foreach (var seed in MatrixSeeds)
            {
                yield return (fixture, seed);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DungeonFortress.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("DungeonFortress.sln not found above " + AppContext.BaseDirectory);
    }
}
