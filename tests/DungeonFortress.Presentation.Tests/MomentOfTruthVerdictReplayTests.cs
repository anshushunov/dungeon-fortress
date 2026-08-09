using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The full circle of Issue #351: the player presses an answer, the adapter
/// rebuilds the world from the log, and the band under the map reads differently
/// afterwards.
///
/// <para><b>Why this file exists at all.</b> Nothing in the repository walked
/// that circle before. The slice-3 tests (Issue #312) drive
/// <see cref="PrototypeWorld"/> directly, so the verdict is already in the log
/// before the world is built and no rebuild happens; the press guards of
/// Issue #331 stop at the command the press <em>would</em> build. Between the two
/// sat the rebuild itself — the step every player command actually takes — with
/// no test at all, and a verdict that never reached the screen went through two
/// rounds of independent review and two playtests because of it.</para>
///
/// <para><b>What is emulated and what is real.</b> <see cref="Session"/> below is
/// the adapter's own state — the shipped journal, the commands this session has
/// added, the live world and the last snapshot — and its three routines are the
/// bodies of <c>Main.TryApplyPlayerCommand</c>, <c>Main.Advance</c> and
/// <c>Main.ReplayCurrentLog</c>. Everything they call is production code:
/// <see cref="WorldReplay"/>, <see cref="PrototypeCommandValidator"/>,
/// <see cref="PrototypeWorld"/> and <see cref="MomentOfTruthPanel"/>. The engine
/// is the only thing missing, and ADR 0011 is why it has to be
/// (<c>DungeonFortress.Game</c> is referenced by no test project).</para>
/// </summary>
public sealed class MomentOfTruthVerdictReplayTests
{
    /// <summary>
    /// The journal the playtest was played on. Its first wave ends well inside a
    /// session, which is what gives this file a window to answer.
    /// </summary>
    private const string Fixture = "baseline";

    /// <summary>
    /// The two answers as the canonical snapshot spells them. The mapping from
    /// <c>VerdictKind</c> to the word is <c>internal</c> to the simulation, and
    /// <c>MomentOfTruthPanelTests</c> already holds the same two literals against
    /// a real card.
    /// </summary>
    private const string Reward = "reward";

    private const string Punish = "punish";

    // ------------------------------------------------------------------
    // Criterion 3 of Issue #351 — a verdict changes the counter, and the
    // second answer does not undo the first.
    // ------------------------------------------------------------------

    /// <summary>
    /// One press, read on the band. This is the owner's report as a check:
    /// «кнопки нажимаются, но эффекта никакого нет … всё равно 3 unanswered».
    /// </summary>
    [Fact]
    public void A_verdict_the_player_issues_is_visible_on_the_band_it_answers()
    {
        var session = Session.PlayedToTheMomentOfTruth(Fixture);
        var before = session.Band();
        Assert.True(before.Open, "the party did not stop between two waves");
        Assert.Equal(PrototypeTuning.MomentOfTruthCards, before.Unanswered);

        var answered = session.Answer(before.Cards[0].CreatureId, VerdictKind.Reward);
        var after = session.Band();

        Assert.True(
            answered,
            $"the simulation refused the verdict: {session.Feedback}");
        Assert.True(
            after.Open,
            "the window closed on the first answer of three, so the press did not " +
            "answer a card — it restarted the party's pause.");
        Assert.Equal(before.Unanswered - 1, after.Unanswered);
        Assert.Equal(
            Reward,
            after.Cards.Single(card => card.CreatureId == before.Cards[0].CreatureId).Verdict);
    }

    /// <summary>
    /// Two presses on two cards. The second one is the press the owner's session
    /// died on: with the rebuild addressed by the tick alone it silently threw
    /// the first answer away, so the counter never left <c>3 of 3</c>.
    /// </summary>
    [Fact]
    public void A_second_verdict_does_not_undo_the_first()
    {
        var session = Session.PlayedToTheMomentOfTruth(Fixture);
        var opening = session.Band();
        var first = opening.Cards[0].CreatureId;
        var second = opening.Cards[1].CreatureId;

        Assert.True(session.Answer(first, VerdictKind.Reward), session.Feedback);
        Assert.True(session.Answer(second, VerdictKind.Punish), session.Feedback);

        var band = session.Band();
        Assert.True(band.Open, "answering two of three cards closed the window");
        Assert.Equal(opening.Unanswered - 2, band.Unanswered);
        Assert.Equal(
            Reward,
            band.Cards.Single(card => card.CreatureId == first).Verdict);
        Assert.Equal(
            Punish,
            band.Cards.Single(card => card.CreatureId == second).Verdict);
    }

    /// <summary>
    /// The same two presses with the clock let go between them, which is how the
    /// owner played: a window that has already spent steps must not be rewound to
    /// step zero by the next press. The countdown the band prints is the visible
    /// half of the same property.
    /// </summary>
    [Fact]
    public void A_window_that_has_spent_steps_is_not_rewound_by_the_next_press()
    {
        var session = Session.PlayedToTheMomentOfTruth(Fixture);
        var opening = session.Band();
        Assert.True(session.Answer(opening.Cards[0].CreatureId, VerdictKind.Reward), session.Feedback);

        // The player lets the clock run for a moment, exactly as pressing RUN
        // during an open window does: a step is spent waiting and no tick happens.
        var tickBefore = session.Tick;
        session.Step(3);
        Assert.Equal(tickBefore, session.Tick);
        var spent = session.Band().StepsLeft;

        Assert.True(session.Answer(opening.Cards[1].CreatureId, VerdictKind.Punish), session.Feedback);

        var band = session.Band();
        Assert.True(band.Open, "the window closed while two of three cards were unanswered");
        Assert.True(
            band.StepsLeft <= spent,
            $"the press gave the window {band.StepsLeft - spent} step(s) back: it had " +
            $"{spent} left and the answer left it with {band.StepsLeft}. A press that " +
            "rewinds the window rewinds every verdict already answered with it.");
        Assert.Equal(opening.Unanswered - 2, band.Unanswered);
    }

    // ------------------------------------------------------------------
    // Criterion 4 — the other commands go through the same rebuild.
    // ------------------------------------------------------------------

    /// <summary>
    /// A priority nudge is not a verdict, but it takes the same rebuild, so it
    /// used to throw away every answer already given while the window was open.
    /// Nothing about the card is asked here: the claim is that an unrelated
    /// command leaves the answers alone.
    /// </summary>
    [Fact]
    public void An_unrelated_command_during_the_window_keeps_the_answers_already_given()
    {
        var session = Session.PlayedToTheMomentOfTruth(Fixture);
        var opening = session.Band();
        var judged = opening.Cards[0].CreatureId;
        Assert.True(session.Answer(judged, VerdictKind.Reward), session.Feedback);

        var priority = session.Snapshot.Priorities[JobKind.Drill];
        Assert.True(
            session.Apply(new SetPriorityCommand(session.Tick, JobKind.Drill, priority == 4 ? 3 : 4)),
            session.Feedback);

        var band = session.Band();
        Assert.True(band.Open, "a priority nudge closed the moment of truth");
        Assert.Equal(opening.Unanswered - 1, band.Unanswered);
        Assert.Equal(
            Reward,
            band.Cards.Single(card => card.CreatureId == judged).Verdict);
    }

    // ------------------------------------------------------------------
    // Criterion 5 — REPLAY [Y].
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>REPLAY</c> replays the session's own log and compares the checksum with
    /// the live one. It is written with the same address as the rebuild, so
    /// during an open window it replayed a different state and reported a
    /// mismatch about a session that was in fact reproducible.
    /// </summary>
    [Fact]
    public void Replay_reproduces_a_session_whose_verdicts_were_cast_in_the_window()
    {
        var session = Session.PlayedToTheMomentOfTruth(Fixture);
        var opening = session.Band();
        Assert.True(session.Answer(opening.Cards[0].CreatureId, VerdictKind.Reward), session.Feedback);
        session.Step(3);

        Assert.Equal(session.Checksum, session.ReplayChecksum());
    }

    // ------------------------------------------------------------------
    // The adapter, minus the engine.
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>Main</c>'s command path with no Godot in it: the shipped journal, the
    /// commands this session added, the live world, the last snapshot and the
    /// feedback line.
    /// </summary>
    private sealed class Session
    {
        private readonly PrototypeCommandLog _fixtureLog;
        private readonly List<PrototypeCommand> _playerCommands = [];
        private PrototypeWorld _world;

        private Session(PrototypeCommandLog fixtureLog, PrototypeWorld world)
        {
            _fixtureLog = fixtureLog;
            _world = world;
            Snapshot = world.GetSnapshot();
        }

        /// <summary>The last snapshot, which is what every panel is drawn from.</summary>
        public PrototypeSnapshot Snapshot { get; private set; }

        public string Feedback { get; private set; } = string.Empty;

        public int Tick => Snapshot.Tick;

        public string Checksum => PrototypeScenario.Capture(_world).Checksum;

        /// <summary>
        /// A party played until it stops by itself and asks the player something.
        /// The stop is a state and not a tick, for the reason the simulation's own
        /// tests give: the tick a wave ends on is emergent.
        /// </summary>
        public static Session PlayedToTheMomentOfTruth(string fixtureName)
        {
            var log = PresentationFixtures.LogOf(fixtureName);
            var world = new PrototypeWorld(log);
            while (!world.IsComplete && !world.IsAwaitingVerdict)
            {
                world.Step();
            }

            Assert.True(
                world.IsAwaitingVerdict,
                $"{fixtureName} played a whole party without ever stopping between two waves.");
            return new Session(log, world);
        }

        /// <summary>The band as the player reads it, with nobody selected.</summary>
        public MomentOfTruthPrompt Band() => MomentOfTruthPanel.Of(Snapshot, null);

        /// <summary>
        /// A press on one card, all the way through: the sign the panel resolved,
        /// the command <c>Main.MomentOfTruthVerdictCommand</c> builds from it and
        /// the rebuild <c>Main.TryApplyPlayerCommand</c> runs.
        /// </summary>
        public bool Answer(int creatureId, VerdictKind verdict) =>
            Apply(new VerdictCommand(Tick, creatureId, verdict));

        /// <summary>The body of <c>Main.TryApplyPlayerCommand</c>.</summary>
        public bool Apply(PrototypeCommand command)
        {
            try
            {
                var candidateCommands = _playerCommands.Append(command).ToArray();
                var candidateLog = BuildFullLog(candidateCommands);
                PrototypeCommandValidator.Validate(candidateLog);
                var candidateWorld = WorldReplay.To(candidateLog, WorldReplay.PositionOf(Snapshot));
                _playerCommands.Add(command);
                _world = candidateWorld;
                Feedback = $"accepted {command.GetType().Name}";
                Refresh();
                return true;
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                Feedback = $"rejected {command.GetType().Name}: {exception.Message}";
                return false;
            }
        }

        /// <summary>
        /// The clock, as <c>Main.Advance</c> drives it: steps rather than ticks,
        /// because a step of an open window is spent waiting and no tick happens.
        /// </summary>
        public void Step(int steps)
        {
            for (var index = 0; index < steps && !_world.IsComplete; index++)
            {
                _world.Step();
            }

            Refresh();
        }

        /// <summary>The checksum <c>Main.ReplayCurrentLog</c> compares against.</summary>
        public string ReplayChecksum() =>
            PrototypeScenario.Capture(
                WorldReplay.To(BuildFullLog(_playerCommands), WorldReplay.PositionOf(Snapshot)))
                .Checksum;

        private PrototypeCommandLog BuildFullLog(IEnumerable<PrototypeCommand> playerCommands) =>
            new(
                _fixtureLog.Scenario,
                _fixtureLog.Seed,
                [.. _fixtureLog.Commands.Concat(playerCommands).OrderBy(command => command.Tick)]);

        private void Refresh() => Snapshot = _world.GetSnapshot();
    }
}
