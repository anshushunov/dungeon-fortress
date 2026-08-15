using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Issue #431, checkpoint 0 — <b>is the channel reachable at all</b>, measured
/// before a single line of behaviour is changed.
///
/// <para>The slice about to be written makes a wounded creature's verdict decide
/// whether it spares itself or takes the field. Every term of that decision is
/// downstream of a verdict, and <b>not one shipped journal contains a
/// verdict</b>: <c>baseline.commands.v2.json</c> carries <c>"commands": []</c>
/// and a search for <c>verdict</c> over <c>scenarios/prototype1/*.json</c> is
/// empty. Measured on the shipped matrix as it stands, this checkpoint would
/// therefore read zero always — which is why
/// <c>docs/design/VERDICT_AND_THE_WOUNDED.md</c> §6 makes a probe journal part
/// of the checkpoint rather than a convenience.</para>
///
/// <para><b>What the chain is.</b> §6 asks for one chain followed through by a
/// single <c>creatureId</c> rather than three independent counters, because
/// independent counters can belong to different creatures in different waves and
/// prove nothing:</para>
///
/// <list type="number">
/// <item><description>the creature carries a wound;</description></item>
/// <item><description>the domain shows a card about <b>that</b> creature;</description></item>
/// <item><description>the player answers that card with a concrete sign;</description></item>
/// <item><description>at a later roll call the creature still carries a
/// non-empty wound <b>and</b> a non-zero relevant term — <c>benefit_rewarded</c>
/// for <c>reward</c>, <c>fear_punished</c> for <c>punish</c>;</description></item>
/// <item><description>at that roll call it <b>reaches the insertion point of the
/// contest</b>, i.e. survives the four existing refusals — heavy torso, hunger,
/// released grudge and unreachability.</description></item>
/// </list>
///
/// <para><b>Why the chain ends at the insertion point and not at the contest.</b>
/// The second round of independent review of the specification found the
/// original wording unmeasurable in its own order: checkpoint 0 runs <em>before
/// any behavioural change</em>, and the contest only exists from checkpoint 2, so
/// a chain ending in "entered the contest" could not tell zero-because-unreachable
/// from zero-because-absent. Reaching the insertion point is observable today,
/// and it is exactly equivalent: after the four <c>continue</c>s the only path
/// left in <see cref="PrototypeWorld"/>'s roll call is the one that records
/// <c>combat_joined</c>, so "recorded <c>combat_joined</c> on this tick" is
/// "survived all four refusals on this tick".</para>
///
/// <para><b>Zero is a legitimate outcome that stops the slice</b> — with numbers,
/// returned to the coordinator, and never repaired by moving a weight. That is
/// why the assertion at the end of the measurement is separate from the report:
/// the report is written whatever the numbers are.</para>
/// </summary>
public sealed class VerdictWoundedReachabilityTests
{
    private static readonly string[] MatrixFixtures = ["baseline", "prepared", "neglected"];

    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    /// <summary>
    /// The probe journal of §6, shipped as an ordinary fixture. It answers the
    /// cards of the <b>first</b> moment of truth and no later one, and the choice
    /// is what makes it survive the slice it is measuring.
    ///
    /// <para>A verdict is only accepted on the tick its window is open, and that
    /// tick is emergent — it is the tick after the wave resolved. Later pauses
    /// move the moment the contest starts refusing wounded creatures the line, so
    /// a journal pinned to them would throw «A verdict is only accepted while the
    /// moment of truth is open» the first time the balance shifted. The first
    /// pause cannot move: a wound is only ever written by
    /// <c>ActRaiders</c>, and during wave 1 every wounded creature is either
    /// <c>Fighting</c> or <c>Downed</c> — both of which the roll call skips — so
    /// no creature the contest can act on exists until the domain picks its people
    /// up after wave 1.</para>
    /// </summary>
    private const string ProbeFixture = "probe-verdicts";

    [Fact]
    public void Reachability_of_the_verdict_to_wounded_channel_is_measured_and_recorded()
    {
        var root = FindRepositoryRoot();
        var cells = new List<ReachabilityCell>();

        foreach (var fixture in MatrixFixtures)
        {
            foreach (var seed in MatrixSeeds)
            {
                cells.Add(Measure(
                    $"{fixture}/{seed}",
                    LoadFixture(root, fixture) with { Seed = seed }));
            }
        }

        var probePath = Path.Combine(root, "scenarios", "prototype1", $"{ProbeFixture}.commands.v2.json");
        ReachabilityCell? probe = File.Exists(probePath)
            ? Measure($"{ProbeFixture} (shipped)", PrototypeCommandDocument.Load(probePath))
            : null;

        var rewardChains = cells.Sum(cell => cell.RewardChains.Count) +
            (probe?.RewardChains.Count ?? 0);
        var punishChains = cells.Sum(cell => cell.PunishChains.Count) +
            (probe?.PunishChains.Count ?? 0);
        var strictReward = cells.Sum(cell => cell.RewardChains.Count(chain => chain.WoundedAtTheCard)) +
            (probe?.RewardChains.Count(chain => chain.WoundedAtTheCard) ?? 0);
        var strictPunish = cells.Sum(cell => cell.PunishChains.Count(chain => chain.WoundedAtTheCard)) +
            (probe?.PunishChains.Count(chain => chain.WoundedAtTheCard) ?? 0);

        var report = new ReachabilityReport(
            SchemaVersion: 1,
            Issue: "#431",
            Checkpoint: "0 — достижимость канала, до всякой правки поведения",
            Command:
                "dotnet test tests/DungeonFortress.Simulation.Tests " +
                "--filter FullyQualifiedName~VerdictWoundedReachabilityTests",
            What:
                "End-to-end chain, followed by a single creatureId: wound -> card about that " +
                "creature -> a verdict of a named sign -> a non-zero relevant term and a " +
                "non-empty wound at a later roll call -> the insertion point of the contest " +
                "reached (all four existing refusals survived). Counted separately for reward " +
                "and for punish. Zero on either is a legitimate outcome that stops the slice.",
            // <b>This file is re-measured by every run and therefore reports the
            // tree it ran on, not the gate.</b> The gate of checkpoint 0 — the
            // numbers that decided the slice could start, taken before the slice
            // touched behaviour — is frozen beside it in
            // `evidence/431-reachability-checkpoint0.json` at `1503af5`. Naming a
            // commit here would have been a label that stops being true the first
            // time the slice moves a trajectory, and it did: the refusal of the
            // line began reading the fear of the domain at checkpoint 3-bis
            // (owner's decision of 2026-08-15) and the chains moved 12/6 -> 11/6.
            MeasuredOnSimulationCommit:
                "the working tree this run was made on; the gate is the frozen copy " +
                "evidence/431-reachability-checkpoint0.json, measured at 1503af5",
            RelevantTerms: new RelevantTerms("benefit_rewarded", "fear_punished"),
            MatrixFixtures: MatrixFixtures,
            MatrixSeeds: MatrixSeeds,
            ProbeJournal: probe is null
                ? "absent — the shipped probe journal had not been authored when this ran"
                : $"scenarios/prototype1/{ProbeFixture}.commands.v2.json",
            Cells: [.. cells, .. probe is null ? Array.Empty<ReachabilityCell>() : [probe]],
            Aggregate: new AggregateResult(
                RewardChains: rewardChains,
                PunishChains: punishChains,
                RewardChainsWoundedAlreadyAtTheCard: strictReward,
                PunishChainsWoundedAlreadyAtTheCard: strictPunish),
            Verdict: rewardChains > 0 && punishChains > 0
                ? "REACHABLE: both signs complete the chain. The channel has something to act " +
                  "on, and the slice may proceed to checkpoint 1."
                : "UNREACHABLE: at least one sign never completes the chain. By §6 this stops " +
                  "the slice and returns the fork to the coordinator with these numbers; " +
                  "moving a weight instead is a blocking finding at review.");

        File.WriteAllText(
            Path.Combine(root, "evidence", "431-reachability.json"),
            JsonSerializer.Serialize(report, ReportOptions) + "\n",
            new UTF8Encoding(false));

        Assert.True(
            rewardChains > 0,
            "not one creature anywhere in the matrix carried a reward through to a roll call it " +
            "reached wounded, so the `reward` half of the channel has nothing to act on.\n" +
            Describe(cells, probe));
        Assert.True(
            punishChains > 0,
            "not one creature anywhere in the matrix carried a punishment through to a roll call " +
            "it reached wounded, so the `punish` half of the channel has nothing to act on.\n" +
            Describe(cells, probe));
    }

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Describe(IEnumerable<ReachabilityCell> cells, ReachabilityCell? probe)
    {
        var builder = new StringBuilder();
        foreach (var cell in cells.Concat(probe is null ? [] : [probe]))
        {
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{cell.Cell}: {cell.CardsAnswered} card(s) answered, " +
                $"{cell.RewardChains.Count} reward chain(s), {cell.PunishChains.Count} punish chain(s)"));
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------
    // The measurement.
    // ------------------------------------------------------------------

    /// <summary>
    /// One cell: a journal answered by the probe rule, played out, and read for
    /// completed chains.
    ///
    /// <para>When the journal already carries verdicts of its own — the shipped
    /// probe fixture does — it is played as it stands and nothing is added, so the
    /// measurement of the shipped file is a measurement of the file rather than of
    /// a rule applied on top of it.</para>
    /// </summary>
    private static ReachabilityCell Measure(string cell, PrototypeCommandLog log)
    {
        var answers = log.Commands.OfType<VerdictCommand>().Any()
            ? [.. AnswersOf(log)]
            : AnswerEveryCard(log, out log);

        var chains = FollowChains(log, answers);
        return new ReachabilityCell(
            cell,
            log.Seed,
            answers.Count,
            [.. answers.Select(answer => answer.ToString())],
            [.. chains.Where(chain => chain.Sign == "reward")],
            [.. chains.Where(chain => chain.Sign == "punish")]);
    }

    /// <summary>
    /// The verdicts a journal already carries, described the same way the probe
    /// rule describes the ones it issues. The wound at the card is read by
    /// replaying to each verdict's own tick, because "was it already hurt when the
    /// domain reported on it" is a fact about the world and not about the command.
    /// </summary>
    private static IEnumerable<CardAnswer> AnswersOf(PrototypeCommandLog log)
    {
        var verdicts = log.Commands.OfType<VerdictCommand>()
            .OrderBy(command => command.Tick)
            .ThenBy(command => command.CreatureId)
            .ToArray();
        var world = new PrototypeWorld(log);
        var wounded = new Dictionary<(int Tick, int CreatureId), bool>();
        foreach (var tick in verdicts.Select(command => command.Tick).Distinct())
        {
            while (!world.IsComplete && world.CurrentTick < tick)
            {
                world.Step();
            }

            var state = world.GetSnapshot();
            foreach (var creature in state.Creatures)
            {
                wounded[(tick, creature.Id)] = creature.Injuries.Count > 0;
            }
        }

        return verdicts.Select(command => new CardAnswer(
            command.Tick,
            command.CreatureId,
            SignOf(command.Verdict),
            wounded.GetValueOrDefault((command.Tick, command.CreatureId))));
    }

    /// <summary>
    /// The wire name of a sign. Restated here rather than read off
    /// <c>PrototypeWorld.ToVerdictJson</c>, which is internal to the simulation:
    /// the two are held together by
    /// <c>PrototypeMomentOfTruthTests.Every_verdict_value_is_walked_through_the_five_conditions_in_the_contract</c>,
    /// which fails the moment a value of the enumeration has no name in the
    /// contract.
    /// </summary>
    private static string SignOf(VerdictKind verdict) => verdict switch
    {
        VerdictKind.Reward => "reward",
        VerdictKind.Punish => "punish",
        _ => throw new InvalidDataException($"Unknown verdict: {verdict}"),
    };

    /// <summary>
    /// Answers every card of every moment of truth, alternating the sign by the
    /// position of the card, and returns the journal that does it.
    ///
    /// <para>Built one pause at a time and replayed from scratch each round, for
    /// the reason <c>PrototypeMomentOfTruthTests.PlayPunishingEveryCard</c> is:
    /// the tick a wave ends on and the creature a card is about are both emergent,
    /// and answering pause <i>k</i> moves pause <i>k+1</i>.</para>
    /// </summary>
    private static List<CardAnswer> AnswerEveryCard(
        PrototypeCommandLog baseLog,
        out PrototypeCommandLog answered)
    {
        var issued = new List<PrototypeCommand>();
        var answers = new List<CardAnswer>();
        for (var round = 0; round < PrototypeTuning.WaveCount; round++)
        {
            var world = new PrototypeWorld(baseLog with { Commands = [.. baseLog.Commands, .. issued] });
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

                if (++seen <= round)
                {
                    continue;
                }

                var state = world.GetSnapshot();
                for (var index = 0; index < state.MomentOfTruth.Cards.Count; index++)
                {
                    var card = state.MomentOfTruth.Cards[index];
                    var sign = index % 2 == 0 ? VerdictKind.Reward : VerdictKind.Punish;
                    issued.Add(new VerdictCommand(world.CurrentTick, card.CreatureId, sign));
                    answers.Add(new CardAnswer(
                        world.CurrentTick,
                        card.CreatureId,
                        SignOf(sign),
                        state.Creatures.Single(item => item.Id == card.CreatureId).Injuries.Count > 0));
                }

                added = true;
            }

            if (!added)
            {
                break;
            }
        }

        answered = baseLog with { Commands = [.. baseLog.Commands, .. issued] };
        return answers;
    }

    /// <summary>
    /// Plays the answered journal out and reports, for every creature that was
    /// answered, the first roll call after the verdict at which it both carried a
    /// wound and reached the insertion point of the contest.
    ///
    /// <para>Reaching the insertion point is read as "recorded
    /// <c>combat_joined</c> during this step", which is the same fact: the four
    /// refusals above it all <c>continue</c>, so a creature that reaches the join
    /// is a creature none of them turned away. The count is taken off the folded
    /// journal by summing <c>Repeats</c>, because two joins a wave apart can fold
    /// into one entry when their details happen to match.</para>
    /// </summary>
    private static List<Chain> FollowChains(PrototypeCommandLog log, List<CardAnswer> answers)
    {
        var chains = new List<Chain>();
        if (answers.Count == 0)
        {
            return chains;
        }

        var pending = answers
            .GroupBy(answer => (answer.CreatureId, answer.Sign))
            .ToDictionary(group => group.Key, group => group.OrderBy(answer => answer.Tick).First());

        // Nothing before the first wave can complete a chain: a wound is only ever
        // written inside a fight, so the stretch that has to be watched tick by
        // tick starts at the first raid tick and the quiet half of the party is
        // not paid for.
        var world = new PrototypeWorld(log);
        while (!world.IsComplete && world.CurrentTick < PrototypeTuning.FirstRaidTick)
        {
            world.Step();
        }

        var before = world.GetSnapshot();
        while (!world.IsComplete)
        {
            world.Step();
            var after = world.GetSnapshot();
            foreach (var (key, answer) in pending.ToArray())
            {
                var (creatureId, sign) = key;
                if (before.Tick <= answer.Tick ||
                    JoinCount(after, creatureId) <= JoinCount(before, creatureId))
                {
                    continue;
                }

                // Read off `before`: the roll call is the first phase of the tick
                // that has just run, so the world it decided on is the one
                // photographed before the step.
                var creature = before.Creatures.Single(item => item.Id == creatureId);
                if (creature.Injuries.Count == 0)
                {
                    continue;
                }

                var term = sign == "reward"
                    ? creature.Loyalty.BenefitTerms.FirstOrDefault(item => item.Code == "benefit_rewarded")
                    : creature.Loyalty.FearTerms.FirstOrDefault(item => item.Code == "fear_punished");
                if (term is null || term.Amount == 0)
                {
                    continue;
                }

                chains.Add(new Chain(
                    creatureId,
                    creature.Name,
                    sign,
                    answer.Tick,
                    answer.WoundedAtTheCard,
                    before.Tick,
                    string.Join(
                        "+",
                        creature.Injuries.Select(injury => $"{injury.Part}:{injury.Severity}")),
                    term.Code,
                    term.Amount));
                pending.Remove(key);
            }

            before = after;
        }

        return chains;
    }

    /// <summary>
    /// The reason codes that say a creature got past all four refusals of the
    /// roll call and reached the point the contest is inserted at.
    ///
    /// <para>Before checkpoint 2 that is <c>combat_joined</c> alone, because it is
    /// the only path below the four <c>continue</c>s. From checkpoint 2 the same
    /// point has a second outcome — the wounded creature that decided to spare
    /// itself — and it has to count as "reached", otherwise this measurement would
    /// read the slice's own success as the channel going dead.</para>
    /// </summary>
    private static readonly string[] InsertionPointReached =
        ["combat_joined", "combat_spared_wound"];

    private static int JoinCount(PrototypeSnapshot state, int creatureId) =>
        state.Events
            .Where(item => item.CreatureId == creatureId &&
                InsertionPointReached.Contains(item.ReasonCode, StringComparer.Ordinal))
            .Sum(item => item.Repeats);

    // ------------------------------------------------------------------
    // Helpers and the shape of the report.
    // ------------------------------------------------------------------

    private static PrototypeCommandLog LoadFixture(string root, string fixture) =>
        PrototypeCommandDocument.Load(
            Path.Combine(root, "scenarios", "prototype1", $"{fixture}.commands.v2.json"));

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

    private sealed record CardAnswer(int Tick, int CreatureId, string Sign, bool WoundedAtTheCard)
    {
        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"t{Tick} #{CreatureId} {Sign}{(WoundedAtTheCard ? " (wounded)" : string.Empty)}");
    }

    private sealed record Chain(
        int CreatureId,
        string Name,
        string Sign,
        int VerdictTick,
        bool WoundedAtTheCard,
        int RollCallTick,
        string WoundAtTheRollCall,
        string TermCode,
        int TermAmount);

    private sealed record ReachabilityCell(
        string Cell,
        ulong Seed,
        int CardsAnswered,
        IReadOnlyList<string> Answers,
        IReadOnlyList<Chain> RewardChains,
        IReadOnlyList<Chain> PunishChains);

    private sealed record RelevantTerms(string Reward, string Punish);

    private sealed record AggregateResult(
        int RewardChains,
        int PunishChains,
        int RewardChainsWoundedAlreadyAtTheCard,
        int PunishChainsWoundedAlreadyAtTheCard);

    private sealed record ReachabilityReport(
        int SchemaVersion,
        string Issue,
        string Checkpoint,
        string Command,
        string What,
        string MeasuredOnSimulationCommit,
        RelevantTerms RelevantTerms,
        IReadOnlyList<string> MatrixFixtures,
        IReadOnlyList<ulong> MatrixSeeds,
        string ProbeJournal,
        IReadOnlyList<ReachabilityCell> Cells,
        AggregateResult Aggregate,
        string Verdict);
}
