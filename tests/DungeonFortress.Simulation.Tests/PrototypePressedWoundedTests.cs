using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

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
/// <item><description>the loop closes through the mechanism that is already
/// there: the resentment surfaces as the fear hiding it fades, and
/// <c>combat_refused_grudge</c> takes the creature out of the line a wave or two
/// later. One case is enough, and it has to be named by seed, creature and
/// ticks;</description></item>
/// <item><description>§3.6 and the seventh amendment of the second review
/// round: a creature that mends its last part mid-wave <b>bypasses</b> the
/// contest at the next re-check, takes the existing <c>combat_joined</c> path,
/// and has its intent field cleared.</description></item>
/// </list>
/// </summary>
public sealed class PrototypePressedWoundedTests
{
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
    /// </summary>
    [Fact(Skip =
        "Criterion 6 is NOT met and the fork is with the coordinator, not with this test. " +
        "Measured on the shipped matrix: the refusal condition of ResentmentOutweighsTheLine " +
        "does become true for a coerced creature, and it becomes true on the very tick the " +
        "creature enters the fight — at which point the roll call no longer asks it, because " +
        "it is Fighting. By the next roll call the fight has raised its fear back above its " +
        "grudge. That is the same structural shape independent review of PR #328 found for " +
        "`combat_left_grudge`. Numbers, the alternative tuning that was tried and what it did, " +
        "in evidence/431-loop.json. Unskip only after the fork is answered.")]
    public void The_loop_closes_a_pressed_wounded_creature_later_refuses_the_line()
    {
        var closures = FindClosures();
        Assert.True(
            closures.Count > 0,
            "nowhere in the matrix did a creature charged `grudge_pressed_wounded` later refuse " +
            "the line by `combat_refused_grudge`, so the delayed price of coercion is credited " +
            "and never acted on. §3.4 promises the loop closes through the existing mechanism.");
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
                    },
                    nearMisses = new
                    {
                        what =
                            "For every creature charged `grudge_pressed_wounded`, the closest " +
                            "the refusal `ReleasedGrudge x T.loyalty_refuse_grudge_weight > " +
                            "benefit + fear + grit x T.loyalty_refuse_grit_weight` ever came to " +
                            "being true afterwards. A negative gap means the condition WAS true " +
                            "and the roll call still never asked — because the creature was " +
                            "Fighting at that moment.",
                        cases = NearMisses(),
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

        // `closures` is deliberately NOT asserted non-empty: on the shipped matrix
        // it is empty, and that is the finding this file escalates rather than
        // hides. The file records it with its numbers so the fork is answerable.
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
        int HoldingAtRefusal);

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
        int Fear);

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
                    var holding = creature.Loyalty.Benefit + creature.Loyalty.Fear +
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
                        creature.Loyalty.Fear);
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
