using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The moment of truth as something the player can find and press (Issue #331).
///
/// <para>
/// Slice 3 shipped working verdicts and an unplayable presentation of them: the
/// cards were drawn as text in the inspector column on the right, and the owner's
/// first playtest never found them — «после боя игра запаузилась и непонятно куда
/// нажимать и где ожидается ввод». These are the checks that say the cards exist
/// somewhere other than that column, that pressing one points at the creature it
/// is about, and that a verdict can be reached with the mouse alone.
/// </para>
///
/// <para>
/// Every claim below is asserted against the result of a fresh call on a real
/// snapshot, never against a literal beside another literal: a check that
/// compares two constants cannot fail under any mutation, and the project has
/// returned work for exactly that shape.
/// </para>
/// </summary>
public sealed class MomentOfTruthPanelTests
{
    /// <summary>
    /// A party played until it stops by itself and waits for the player. The stop
    /// is what is looked for rather than a tick number: the tick a wave ends on
    /// is emergent, and a constant here would be a balance value pretending to be
    /// a fixture.
    /// </summary>
    private static PrototypeSnapshot AtMomentOfTruth(string fixtureName = "baseline")
    {
        var world = new PrototypeWorld(PresentationFixtures.LogOf(fixtureName));
        while (!world.IsComplete && !world.IsAwaitingVerdict)
        {
            world.Step();
        }

        Assert.True(
            world.IsAwaitingVerdict,
            $"'{fixtureName}' played a whole party without ever stopping between two waves, " +
            "so there is no moment of truth to show.");
        return world.GetSnapshot();
    }

    private static HudViewState View(PrototypeSnapshot state, int? selected = null) => new(
        state,
        "baseline",
        new string('0', 64),
        Paused: true,
        Speed: 1.0,
        SelectedCreatureId: selected,
        SelectedCell: null,
        ControlFeedback: string.Empty,
        PlayerCommands: [],
        DiagnosticCount: 0);

    /// <summary>
    /// The claim of criterion 1: the cards are offered somewhere other than the
    /// inspector column's text panel. The band carries one control per card, and
    /// the sentence on it is the same sentence the panel prints — not a second
    /// wording of it, which would be a second thing to keep in step.
    /// </summary>
    [Fact]
    public void Every_card_is_offered_outside_the_inspector_panel()
    {
        var state = AtMomentOfTruth();

        var prompt = MomentOfTruthPanel.Of(state, null);

        Assert.True(
            prompt.Open,
            "The window is open in canonical state but the band says it is closed, so the " +
            "cards exist only in the inspector column — the defect Issue #331 is about.");
        Assert.NotEmpty(prompt.Cards);
        Assert.Equal(state.MomentOfTruth.Cards.Count, prompt.Cards.Count);
        foreach (var card in state.MomentOfTruth.Cards)
        {
            var offered = Assert.Single(
                prompt.Cards.Where(entry => entry.CreatureId == card.CreatureId));
            Assert.Equal(HudText.MomentOfTruthCardLine(card), offered.Text);
            Assert.Contains(card.Name, offered.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The claim of criterion 2: a press on a card points at the creature that
    /// card is about, and at no other. It walks every row, so a mapping that
    /// always answers with the first creature fails rather than passing on the
    /// one row where it happens to be right.
    /// </summary>
    [Fact]
    public void Pressing_a_card_points_at_the_creature_it_is_about()
    {
        var state = AtMomentOfTruth();
        var prompt = MomentOfTruthPanel.Of(state, null);
        Assert.True(
            prompt.Cards.Count > 1,
            "One card cannot tell a real mapping from a constant one.");

        for (var index = 0; index < prompt.Cards.Count; index++)
        {
            var press = MomentOfTruthPanel.Press(prompt, MomentOfTruthControlIds.Card(index));

            Assert.NotNull(press);
            Assert.True(press!.IsSelectionOnly);
            Assert.Null(press.Verdict);
            Assert.Equal(state.MomentOfTruth.Cards[index].CreatureId, press.CreatureId);
        }
    }

    /// <summary>
    /// The claim of criterion 3: both answers are reachable by pressing, each
    /// carries the creature of its own row, and each carries <b>its own sign</b>.
    /// Nothing here decides the verdict is legal — that is the simulation's
    /// answer on the tick of the command (ADR 0019) — only which creature and
    /// with what sign.
    ///
    /// <para>The sign is asserted as a <c>VerdictKind</c>, which is the value the
    /// command carries, and not as a third enumeration the adapter would have to
    /// translate. Independent review of PR #345 swapped the two arms of that
    /// translation and every check in the repository stayed green; this is the
    /// check that would now fail.</para>
    /// </summary>
    [Fact]
    public void Both_verdicts_are_reachable_by_pressing_and_name_their_own_creature()
    {
        var state = AtMomentOfTruth();
        var prompt = MomentOfTruthPanel.Of(state, null);

        for (var index = 0; index < prompt.Cards.Count; index++)
        {
            var expected = state.MomentOfTruth.Cards[index].CreatureId;
            var reward = MomentOfTruthPanel.Press(prompt, MomentOfTruthControlIds.Reward(index));
            var punish = MomentOfTruthPanel.Press(prompt, MomentOfTruthControlIds.Punish(index));

            Assert.NotNull(reward);
            Assert.NotNull(punish);
            Assert.Equal(VerdictKind.Reward, reward!.Verdict);
            Assert.Equal(VerdictKind.Punish, punish!.Verdict);
            Assert.False(reward.IsSelectionOnly);
            Assert.False(punish.IsSelectionOnly);
            Assert.Equal(expected, reward.CreatureId);
            Assert.Equal(expected, punish.CreatureId);
        }
    }

    /// <summary>
    /// The button the player reads names itself correctly while the window is
    /// open, whatever the clock is doing.
    ///
    /// <para>Independent review of PR #345 found the first version answering
    /// "Run [P]" over the pause icon whenever a moment of truth opened while time
    /// was running — a tooltip calling a button by another button's name, in
    /// exactly the state the player reaches for it. The title is now read off the
    /// same face the icon is, so the two cannot disagree.</para>
    /// </summary>
    [Theory]
    [InlineData(true, UiControlIds.Run, "Run [P]")]
    [InlineData(false, UiControlIds.Pause, "Pause [P]")]
    public void The_time_button_keeps_its_own_name_while_the_window_is_open(
        bool paused,
        string expectedId,
        string expectedTitle)
    {
        var control = Assert.Single(
            Toolbar(momentOfTruthOpen: true, paused: paused)
                .Where(entry => entry.Strip == UiControlStrip.Time)
                .Take(1));

        Assert.Equal(expectedId, control.Id);
        Assert.StartsWith(expectedTitle + "\n", control.Tooltip, StringComparison.Ordinal);
        Assert.Contains(
            MomentOfTruthPanel.TimeIsHeldTooltip,
            control.Tooltip,
            StringComparison.Ordinal);
        Assert.Equal(UiIconManifest.FileFor(expectedId), control.Icon);
    }

    /// <summary>
    /// A press the band does not own is refused rather than guessed at, and a
    /// press while the window is closed answers nothing at all. Without this a
    /// mapping that returned the first card for every id would still satisfy the
    /// checks above on row zero.
    /// </summary>
    [Theory]
    [InlineData("run")]
    [InlineData("mot_card_")]
    [InlineData("mot_card_9")]
    [InlineData("mot_reward_9")]
    [InlineData("")]
    public void A_press_the_band_does_not_own_is_refused(string controlId)
    {
        var prompt = MomentOfTruthPanel.Of(AtMomentOfTruth(), null);

        Assert.Null(MomentOfTruthPanel.Press(prompt, controlId));
    }

    [Fact]
    public void A_closed_window_offers_nothing_to_press()
    {
        var closed = MomentOfTruthPanel.Of(PresentationFixtures.Baseline(1), null);

        Assert.False(closed.Open);
        Assert.Empty(closed.Cards);
        Assert.Null(MomentOfTruthPanel.Press(closed, MomentOfTruthControlIds.Card(0)));
    }

    /// <summary>
    /// The claim of criterion 4: the two numbers the player is missing are on the
    /// heading, and both are read off the snapshot rather than counted twice.
    /// </summary>
    [Fact]
    public void The_heading_carries_the_unanswered_count_and_the_countdown()
    {
        var state = AtMomentOfTruth();
        var pause = state.MomentOfTruth;

        var prompt = MomentOfTruthPanel.Of(state, null);

        Assert.Equal(pause.Cards.Count(card => card.Verdict is null), prompt.Unanswered);
        Assert.Equal(pause.WindowSteps - pause.WaitedSteps, prompt.StepsLeft);
        Assert.Equal(pause.WaveNumber, prompt.WaveNumber);
        Assert.Contains(
            prompt.Unanswered.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " of " +
                prompt.Cards.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            prompt.Title,
            StringComparison.Ordinal);
        Assert.Contains(
            prompt.StepsLeft.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " steps left",
            prompt.Title,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The countdown is a projection and not a counter of its own: stepping the
    /// waiting party spends the window, and the heading has to move with it.
    /// </summary>
    [Fact]
    public void The_countdown_falls_as_the_window_is_spent()
    {
        var world = new PrototypeWorld(PresentationFixtures.LogOf("baseline"));
        while (!world.IsComplete && !world.IsAwaitingVerdict)
        {
            world.Step();
        }

        var before = MomentOfTruthPanel.Of(world.GetSnapshot(), null);
        world.Step();
        world.Step();
        var after = MomentOfTruthPanel.Of(world.GetSnapshot(), null);

        Assert.True(after.Open);
        Assert.Equal(before.StepsLeft - 2, after.StepsLeft);
    }

    /// <summary>
    /// The claim of criterion 5: asking for time while the window is open
    /// produces a sentence with this window's own numbers in it, rather than
    /// silence. It is the line the adapter writes to the feedback row when RUN or
    /// STEP is pressed.
    /// </summary>
    [Fact]
    public void Asking_for_time_while_the_window_is_open_is_explained()
    {
        var prompt = MomentOfTruthPanel.Of(AtMomentOfTruth(), null);

        var explained = MomentOfTruthPanel.TimeIsHeld(prompt);

        Assert.Contains(
            prompt.StepsLeft.ToString(System.Globalization.CultureInfo.InvariantCulture),
            explained,
            StringComparison.Ordinal);
        Assert.Contains(
            prompt.Unanswered.ToString(System.Globalization.CultureInfo.InvariantCulture),
            explained,
            StringComparison.Ordinal);
        Assert.Contains("Time is held", explained, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same sentence reaches the toolbar, so a player who hovers RUN instead
    /// of pressing it is told the same thing. The tooltip is checked through
    /// <see cref="UiControls.Build"/> because that is what the adapter draws.
    /// </summary>
    [Fact]
    public void The_run_button_says_why_time_will_not_move()
    {
        var open = Toolbar(momentOfTruthOpen: true);
        var closed = Toolbar(momentOfTruthOpen: false);

        Assert.Contains(
            MomentOfTruthPanel.TimeIsHeldTooltip,
            open.Single(control => control.Id == UiControlIds.Run).Tooltip,
            StringComparison.Ordinal);
        Assert.Contains(
            MomentOfTruthPanel.TimeIsHeldTooltip,
            open.Single(control => control.Id == UiControlIds.Step).Tooltip,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            MomentOfTruthPanel.TimeIsHeldTooltip,
            closed.Single(control => control.Id == UiControlIds.Run).Tooltip,
            StringComparison.Ordinal);

        // Still pressable: waiting the window out is one of the two ways it
        // closes, and a disabled RUN would take that way away.
        Assert.True(open.Single(control => control.Id == UiControlIds.Run).Enabled);
    }

    private static IReadOnlyList<UiControl> Toolbar(
        bool momentOfTruthOpen,
        bool paused = true) => UiControls.Build(
        new UiControlsViewState(
            BrushMode.Inspect,
            ZoneKind.Farm,
            JobKind.Harvest,
            2,
            "ration_reserve",
            3,
            Paused: paused,
            Speed: 1.0,
            Fixture: "baseline",
            SessionComplete: false,
            MomentOfTruthOpen: momentOfTruthOpen));

    /// <summary>
    /// An answered card says so and takes no second answer. The verdict is read
    /// off canonical state, so the band cannot show an answer the simulation did
    /// not accept.
    /// </summary>
    [Fact]
    public void An_answered_card_carries_its_verdict()
    {
        var log = PresentationFixtures.LogOf("baseline");
        var world = new PrototypeWorld(log);
        while (!world.IsComplete && !world.IsAwaitingVerdict)
        {
            world.Step();
        }

        var judged = world.GetSnapshot().MomentOfTruth.Cards[0].CreatureId;
        var answered = new PrototypeWorld(log with
        {
            Commands =
            [
                .. log.Commands,
                new VerdictCommand(world.CurrentTick, judged, VerdictKind.Reward),
            ],
        });
        while (!answered.IsComplete && !answered.IsAwaitingVerdict)
        {
            answered.Step();
        }

        answered.Step();
        var state = answered.GetSnapshot();
        var prompt = MomentOfTruthPanel.Of(state, null);

        var card = Assert.Single(prompt.Cards.Where(entry => entry.CreatureId == judged));
        Assert.True(card.Answered);
        // Compared against what the simulation recorded rather than against a
        // word written here: the band reports the answer, it does not name it.
        Assert.Equal(
            state.MomentOfTruth.Cards.Single(entry => entry.CreatureId == judged).Verdict,
            card.Verdict);
        Assert.NotNull(card.Verdict);
        Assert.Equal(prompt.Cards.Count - 1, prompt.Unanswered);
    }

    /// <summary>
    /// Pointing the inspector at a creature lights its row and no other, which is
    /// what makes the keyboard and the mouse agree about who is being judged.
    /// </summary>
    [Fact]
    public void The_selected_creature_lights_its_own_row()
    {
        var state = AtMomentOfTruth();
        var chosen = state.MomentOfTruth.Cards[^1].CreatureId;

        var prompt = MomentOfTruthPanel.Of(state, chosen);

        var lit = Assert.Single(prompt.Cards.Where(card => card.Selected));
        Assert.Equal(chosen, lit.CreatureId);
    }

    /// <summary>
    /// The layout guard's worst case is a real band: three rows of the widest
    /// sentence this party's creatures can produce, every number at its longest.
    /// It has to be at least as wide as what the player actually reads, or the
    /// guard is measuring something easier than the game.
    /// </summary>
    [Fact]
    public void The_worst_case_band_is_never_narrower_than_the_live_one()
    {
        var state = AtMomentOfTruth();
        var live = MomentOfTruthPanel.Of(state, null);

        var worst = MomentOfTruthPanel.WorstCase(state);

        Assert.True(worst.Open);
        Assert.Equal(PrototypeTuning.MomentOfTruthCards, worst.Cards.Count);
        Assert.True(worst.Cards.Count >= live.Cards.Count);
        var widestLive = live.Cards.Max(card => card.Text.Length);
        Assert.True(
            worst.Cards.Min(card => card.Text.Length) >= widestLive,
            $"The worst case measures {worst.Cards.Min(card => card.Text.Length)} characters " +
            $"but the live band already needs {widestLive}.");
        Assert.True(worst.Title.Length >= live.Title.Length);
        Assert.True(worst.Explanation.Length >= live.Explanation.Length);
    }

    /// <summary>
    /// Ids are unique across the whole band, because the adapter dispatches on
    /// them: two rows sharing one id would wire a press to the wrong creature.
    /// </summary>
    [Fact]
    public void Control_ids_are_unique_across_the_band()
    {
        var prompt = MomentOfTruthPanel.Of(AtMomentOfTruth(), null);

        var ids = prompt.Cards
            .SelectMany(card => new[] { card.CardId, card.RewardId, card.PunishId })
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The explanation names both ways out and the price of neither, and it stops
    /// promising a countdown once there is nothing left to answer.
    /// </summary>
    [Fact]
    public void The_explanation_names_both_ways_the_window_closes()
    {
        var prompt = MomentOfTruthPanel.Of(AtMomentOfTruth(), null);

        Assert.Contains("REWARD", prompt.Explanation, StringComparison.Ordinal);
        Assert.Contains("PUNISH", prompt.Explanation, StringComparison.Ordinal);
        Assert.Contains("by itself", prompt.Explanation, StringComparison.Ordinal);
        Assert.Contains("remembered against you", prompt.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "by itself",
            MomentOfTruthPanel.Explanation(0, prompt.StepsLeft),
            StringComparison.Ordinal);
    }
}
