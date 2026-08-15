using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Issue #431, checkpoint 2 — the contest of the wounded at the roll call.
///
/// <para>The rule under test is in
/// <c>docs/design/PROTOTYPE_01_PREPARE_FOR_RAID.md</c> §10.2: a creature that
/// carries a wound and has survived all four existing refusals decides for
/// itself whether to spare itself or take the field, and the player's verdict
/// shifts that decision without ever settling it.</para>
///
/// <para>Every check here is asked of the shipped matrix rather than of one
/// hand-picked party, for the reason
/// <c>PrototypeMomentOfTruthTests.A_verdict_makes_the_named_creature_behave_differently_in_the_next_wave</c>
/// spells out at length: which party happens to contain a scene is a fact about
/// how its fights went, and a check pinned to one seed has to be re-pointed by
/// hand every time the balance moves.</para>
/// </summary>
public sealed class PrototypeWoundedContestTests
{
    private static readonly string[] Fixtures = ["baseline", "prepared"];

    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    /// <summary>
    /// One decision of one contest, read off the published <c>woundIntent</c> on
    /// the tick the roll call wrote it.
    /// </summary>
    private sealed record Decision(
        string Cell,
        string Regime,
        int Tick,
        int CreatureId,
        string Name,
        string Sign,
        string Code,
        int Spare,
        int Press,
        string Part,
        string Severity,
        bool VerdictDecided,
        bool WasMustering,
        int RestingAtTick);

    // ------------------------------------------------------------------
    // Criterion 4 — the channel is not a gate, and it is measured.
    // ------------------------------------------------------------------

    /// <summary>
    /// Criterion 4, asked <b>of each of the two values</b> rather than of one on
    /// choice (the fourth amendment of the second review round). Over the matrix,
    /// creatures carrying one and the same verdict are observed both sparing
    /// themselves and taking the field. Always one and the same outcome would be
    /// the signature of a gate, whatever the arithmetic says.
    /// </summary>
    [Fact]
    public void One_and_the_same_verdict_gives_different_outcomes_over_the_matrix()
    {
        var decisions = MeasureEverything();

        foreach (var sign in new[] { "reward", "punish" })
        {
            var carried = decisions.Where(item => item.Sign == sign).ToArray();
            var spared = carried.Where(item => item.Code == "spared").ToArray();
            var pressed = carried.Where(item => item.Code == "pressed").ToArray();

            Assert.True(
                spared.Length > 0 && pressed.Length > 0,
                $"`{sign}` produced {spared.Length} decision(s) to spare and {pressed.Length} to " +
                "take the field over the whole matrix. One and the same outcome at one and the " +
                "same verdict is a gate rather than a term of a contest (§3.5), and the slice " +
                "promises a term.\n" + Describe(carried));
        }

        // And the strongest reading of the same claim: one creature, one verdict,
        // two different answers at two roll calls — which is only possible because
        // the contest is replayed with the magnitudes as they stand and the fear
        // of the domain fades.
        var flipped = decisions
            .Where(item => item.Sign != "-")
            .GroupBy(item => (item.Cell, item.Regime, item.CreatureId, item.Sign))
            .Where(group => group.Select(item => item.Code).Distinct().Count() > 1)
            .ToArray();
        Assert.True(
            flipped.Length > 0,
            "no creature anywhere in the matrix ever answered the same verdict two different " +
            "ways at two roll calls, so nothing here shows the decision being weighed again " +
            "rather than remembered.\n" + Describe(decisions));
    }

    // ------------------------------------------------------------------
    // Criterion 5 — the causality rule is applied and not described.
    // ------------------------------------------------------------------

    /// <summary>
    /// Criterion 5. A verdict is named as the cause only where removing its own
    /// term flips the contest, and the case the criterion asks for by name — the
    /// benefit that was earned by being fed and tended rather than given by a
    /// reward — is the case where the answer has to be «no».
    ///
    /// <para>Both halves are checked. Every decision taken with no verdict at all
    /// must report <c>verdictDecided = false</c>, which is the class-wide form of
    /// the criterion; and at least one decision that <b>did</b> flip must be
    /// present, otherwise the flag is false everywhere and says nothing.</para>
    /// </summary>
    [Fact]
    public void The_verdict_is_named_the_cause_only_when_removing_its_term_flips_the_contest()
    {
        var decisions = MeasureEverything();

        var unanswered = decisions.Where(item => item.Sign == "-").ToArray();
        Assert.True(unanswered.Length > 0, "the matrix produced no contest about an unanswered creature at all.");
        foreach (var item in unanswered)
        {
            Assert.False(
                item.VerdictDecided,
                $"{item.Cell}/{item.Regime} t{item.Tick}: {item.Name} was never answered by the " +
                "player and the contest still names a verdict as its cause. Benefit is earned by " +
                "being fed and tended as well as given (§3.2), and the feed may not credit the " +
                "player with what a well-run domain would have done anyway.");
        }

        // The case the criterion names literally: a creature that spared itself on
        // benefit it earned rather than benefit it was given.
        var earned = unanswered.Where(item => item.Code == "spared").ToArray();
        Assert.True(
            earned.Length > 0,
            "nowhere in the matrix did a creature spare itself without a verdict, so the case " +
            "the criterion is about — benefit from `benefit_fed` and `benefit_tended` — was " +
            "never reached and the check compared nothing.\n" + Describe(decisions));

        var flipped = decisions.Where(item => item.VerdictDecided).ToArray();
        Assert.True(
            flipped.Length > 0,
            "no verdict anywhere in the matrix decided a contest, so `verdictDecided` is false " +
            "by construction and holds nothing.");
        Assert.All(flipped, item => Assert.NotEqual("-", item.Sign));
    }

    // ------------------------------------------------------------------
    // Criterion 13 — sparing oneself is a transition, not a `continue`.
    // ------------------------------------------------------------------

    /// <summary>
    /// Criterion 13, added by the first amendment of the second review round.
    /// «Кто не встал, тот ложится» does not follow from the code on its own: the
    /// unconditional right of a wounded creature to a bunk holds only outside a
    /// muster, and a mustering creature is walked to the assembly point. So a
    /// creature that spares itself <b>out of an active muster</b> is followed
    /// until it is actually lying down.
    /// </summary>
    [Fact]
    public void A_wounded_creature_that_spares_itself_out_of_a_muster_reaches_a_bunk()
    {
        var decisions = MeasureEverything();
        var outOfMuster = decisions
            .Where(item => item.Code == "spared" && item.WasMustering)
            .ToArray();

        Assert.True(
            outOfMuster.Length > 0,
            "nowhere in the matrix did a wounded creature spare itself while it was still " +
            "mustering, so the one case criterion 13 is about was never reached. Without it the " +
            "transition out of the muster is untested and «кто не встал, тот ложится» rests on " +
            "an argument.\n" + Describe(decisions.Where(item => item.Code == "spared")));

        foreach (var item in outOfMuster)
        {
            Assert.True(
                item.RestingAtTick >= 0,
                $"{item.Cell}/{item.Regime} t{item.Tick}: {item.Name} spared itself out of an " +
                "active muster and never reached `Mode == Resting` before the wave was over. " +
                "The three flags a fighter sheds have to be shed here too, or the creature is " +
                "left standing at the assembly point and «чинить» never happens.");
            Assert.True(
                item.RestingAtTick > item.Tick,
                $"{item.Cell}/{item.Regime}: {item.Name} was already resting when the contest " +
                "asked it, so this case proves nothing about the transition.");
        }
    }

    // ------------------------------------------------------------------
    // The order of the refusals (second amendment of the second review round).
    // ------------------------------------------------------------------

    /// <summary>
    /// The contest stands <b>after</b> the reachability test, so a creature that
    /// both could not have got there and would have spared itself is reported as
    /// unreachable. Checked by construction rather than by hoping the matrix
    /// contains the coincidence: no creature ever carries a decision about its
    /// wound on a tick on which it was refused as unreachable.
    /// </summary>
    [Fact]
    public void A_creature_that_cannot_reach_the_fight_is_reported_unreachable_and_not_sparing()
    {
        var collisions = new List<string>();
        var unreachables = 0;
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
                var seen = new Dictionary<int, int>();
                while (!world.IsComplete)
                {
                    world.Step();
                    var state = world.GetSnapshot();
                    foreach (var creature in state.Creatures)
                    {
                        var absences = state.Events
                            .Where(item => item.CreatureId == creature.Id &&
                                item.ReasonCode == "combat_absent_unreachable")
                            .Sum(item => item.Repeats);
                        var known = seen.GetValueOrDefault(creature.Id);
                        seen[creature.Id] = absences;
                        if (absences <= known)
                        {
                            continue;
                        }

                        unreachables++;
                        if (creature.WoundIntent is { } intent && intent.Tick == world.CurrentTick - 1)
                        {
                            collisions.Add(
                                $"{fixtureName}/{seed} t{intent.Tick}: {creature.Name} is both " +
                                $"`combat_absent_unreachable` and `{intent.Code}` on one roll call.");
                        }
                    }
                }
            }
        }

        Assert.True(unreachables > 0, "the matrix never turned anybody away as unreachable, so the order was not exercised.");
        Assert.True(collisions.Count == 0, string.Join(Environment.NewLine, collisions));
    }

    // ------------------------------------------------------------------
    // The evidence file: criterion 3's base reading and criterion 4's matrix.
    // ------------------------------------------------------------------

    [Fact]
    public void The_contest_over_the_matrix_is_recorded()
    {
        var decisions = MeasureEverything();
        var byRegime = decisions
            .GroupBy(item => item.Regime)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                regime = group.Key,
                spared = group.Count(item => item.Code == "spared"),
                pressed = group.Count(item => item.Code == "pressed"),
                verdictDecided = group.Count(item => item.VerdictDecided),
            })
            .ToArray();

        File.WriteAllText(
            Path.Combine(FindRepositoryRoot(), "evidence", "431-contest.json"),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    issue = "#431",
                    checkpoint = "2 — состязание на перекличке",
                    command =
                        "dotnet test tests/DungeonFortress.Simulation.Tests " +
                        "--filter FullyQualifiedName~PrototypeWoundedContestTests",
                    what =
                        "Every contest of the wounded over baseline and prepared on the three " +
                        "matrix seeds, under five regimes of answering: silence, every card " +
                        "rewarded, every card punished, the cards of the first pause only " +
                        "rewarded, and the cards of the first pause only punished. The last two " +
                        "are what let a verdict fade before the next contest, which is where a " +
                        "single verdict value is observed producing both outcomes (criterion 4).",
                    tuning = new
                    {
                        combatSpareWoundWeight = PrototypeTuning.CombatSpareWoundWeight,
                        combatSpareBenefitDivisor = PrototypeTuning.CombatSpareBenefitDivisor,
                        combatPressDomainFearDivisor = PrototypeTuning.CombatPressDomainFearDivisor,
                        combatPressGritWeight = PrototypeTuning.CombatPressGritWeight,
                    },
                    totals = new
                    {
                        contests = decisions.Count,
                        spared = decisions.Count(item => item.Code == "spared"),
                        pressed = decisions.Count(item => item.Code == "pressed"),
                        verdictDecided = decisions.Count(item => item.VerdictDecided),
                        sparedOutOfAnActiveMuster = decisions.Count(item =>
                            item.Code == "spared" && item.WasMustering),
                    },
                    byRegime,
                    bySign = new[] { "reward", "punish", "-" }
                        .Select(sign => new
                        {
                            sign,
                            spared = decisions.Count(item => item.Sign == sign && item.Code == "spared"),
                            pressed = decisions.Count(item => item.Sign == sign && item.Code == "pressed"),
                            verdictDecided = decisions.Count(item => item.Sign == sign && item.VerdictDecided),
                        })
                        .ToArray(),
                    decisions,
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }) + "\n",
            new UTF8Encoding(false));

        Assert.NotEmpty(decisions);
    }

    // ------------------------------------------------------------------
    // The measurement.
    // ------------------------------------------------------------------

    /// <summary>
    /// Every contest of the matrix under every regime, taken once and reused by
    /// all the checks above.
    /// </summary>
    private static IReadOnlyList<Decision> MeasureEverything()
    {
        var decisions = new List<Decision>();
        foreach (var fixtureName in Fixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                var log = LoadFixture(fixtureName) with { Seed = seed };
                var cell = $"{fixtureName}/{seed}";
                decisions.AddRange(Measure(cell, "silent", log));
                decisions.AddRange(Measure(cell, "every-reward", AnswerEveryPause(log, VerdictKind.Reward)));
                decisions.AddRange(Measure(cell, "every-punish", AnswerEveryPause(log, VerdictKind.Punish)));
                decisions.AddRange(Measure(cell, "first-reward", AnswerFirstPause(log, VerdictKind.Reward)));
                decisions.AddRange(Measure(cell, "first-punish", AnswerFirstPause(log, VerdictKind.Punish)));
            }
        }

        return decisions;
    }

    /// <summary>
    /// Plays one journal out and records every decision the contest took, with
    /// the state of the muster immediately before it and the tick the creature
    /// first lay down afterwards.
    /// </summary>
    private static IEnumerable<Decision> Measure(string cell, string regime, PrototypeCommandLog log)
    {
        var signs = log.Commands.OfType<VerdictCommand>()
            .GroupBy(command => command.CreatureId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(command => command.Verdict == VerdictKind.Reward ? "reward" : "punish")
                    .Distinct()
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .Aggregate((left, right) => $"{left}+{right}"));

        var decisions = new List<Decision>();
        var pendingRest = new List<(Decision Decision, int CreatureId)>();
        var world = new PrototypeWorld(log);
        var before = world.GetSnapshot();
        while (!world.IsComplete)
        {
            world.Step();
            var after = world.GetSnapshot();
            foreach (var creature in after.Creatures)
            {
                if (creature.Mode == CreatureMode.Resting)
                {
                    foreach (var waiting in pendingRest.Where(item => item.CreatureId == creature.Id).ToArray())
                    {
                        decisions[decisions.IndexOf(waiting.Decision)] =
                            waiting.Decision with { RestingAtTick = after.Tick };
                        pendingRest.Remove(waiting);
                    }
                }

                if (creature.WoundIntent is not { } intent || intent.Tick != after.Tick - 1)
                {
                    continue;
                }

                var decision = new Decision(
                    cell,
                    regime,
                    intent.Tick,
                    creature.Id,
                    creature.Name,
                    signs.GetValueOrDefault(creature.Id, "-"),
                    intent.Code,
                    intent.Spare,
                    intent.Press,
                    intent.Part.ToString(),
                    intent.Severity.ToString(),
                    intent.VerdictDecided,
                    // The muster as it stood at the start of the tick the roll
                    // call ran in: the contest itself is what clears the flag, so
                    // reading it afterwards would always say `false`.
                    before.Creatures.Single(item => item.Id == creature.Id).IsMustering,
                    -1);
                decisions.Add(decision);
                if (intent.Code == "spared")
                {
                    pendingRest.Add((decision, creature.Id));
                }
            }

            before = after;
        }

        return decisions;
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

    /// <summary>
    /// The cards of the <b>first</b> pause only, which is the regime the shipped
    /// probe journal is built in and the one that lets a verdict fade before the
    /// next contest asks the same creature again.
    /// </summary>
    private static PrototypeCommandLog AnswerFirstPause(PrototypeCommandLog baseLog, VerdictKind sign)
    {
        var world = new PrototypeWorld(baseLog);
        while (!world.IsComplete && !world.IsAwaitingVerdict)
        {
            world.Step();
        }

        var issued = world.GetSnapshot().MomentOfTruth.Cards
            .Select(card => (PrototypeCommand)new VerdictCommand(world.CurrentTick, card.CreatureId, sign))
            .ToArray();
        return baseLog with { Commands = [.. baseLog.Commands, .. issued] };
    }

    private static string Describe(IEnumerable<Decision> decisions) =>
        string.Join(
            Environment.NewLine,
            decisions.Select(item =>
                $"{item.Cell}/{item.Regime} t{item.Tick} #{item.CreatureId} {item.Name} " +
                $"[{item.Sign}] {item.Code} {item.Spare} v {item.Press} " +
                $"{item.Part}:{item.Severity}{(item.VerdictDecided ? " (verdict decided)" : string.Empty)}"));

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
