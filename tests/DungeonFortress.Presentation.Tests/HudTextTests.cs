using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The three panels the inspector does not own. Their wording used to be
/// observable only by starting Godot and reading a captured frame.
/// </summary>
public sealed class HudTextTests
{
    [Fact]
    public void The_summary_puts_identity_and_bookkeeping_on_the_first_line()
    {
        var state = PresentationFixtures.Baseline(190);

        var summary = HudText.Summary(View(state, fixture: "baseline", paused: true));

        var lines = summary.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(
            $"BASELINE  •  t190  •  PAUSED  •  jobs {state.Jobs.Count}  •  0123abcd",
            lines[0]);
    }

    /// <summary>
    /// The fractional speeds are deliberately absent: <c>{speed:0.#}</c> renders
    /// the decimal separator of the current culture, so "0.5x" here would be a
    /// machine-dependent expectation rather than a property of the HUD. The
    /// behaviour predates this seam and is left exactly as it was.
    /// </summary>
    [Fact]
    public void A_running_session_shows_its_speed_where_a_paused_one_shows_PAUSED()
    {
        var state = PresentationFixtures.Baseline(10);

        Assert.StartsWith(
            "BASELINE  •  t10  •  4x  •",
            HudText.Summary(View(state, paused: false, speed: 4.0)),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "BASELINE  •  t10  •  16x  •",
            HudText.Summary(View(state, paused: false, speed: 16.0)),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "BASELINE  •  t10  •  PAUSED  •",
            HudText.Summary(View(state, paused: true, speed: 16.0)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Three separate stone facts on purpose — on the floor, on a back, put away.
    /// A single combined number would hide the part of the chain that is moving.
    /// </summary>
    [Fact]
    public void The_resource_line_keeps_loose_carried_and_stored_stone_apart()
    {
        var state = PresentationFixtures.FullChain(336);

        var second = HudText.Summary(View(state)).Split('\n')[1];

        Assert.Contains(
            $"stone {state.Stocks.LooseStone}L {state.Stocks.CarriedStone}C " +
            $"{state.Stocks.StoredStone}/{state.Stocks.StockpileCapacity}S",
            second,
            StringComparison.Ordinal);
        Assert.Contains(
            $"dug {state.Economy.DigsCompleted}  •  marks {state.DigDesignations.Count}",
            second,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_fixture_name_is_upper_cased_and_the_checksum_is_cut_to_eight()
    {
        var summary = HudText.Summary(View(
            PresentationFixtures.Baseline(1),
            fixture: "neglected",
            checksum: "abcdef0123456789"));

        Assert.StartsWith("NEGLECTED  •", summary, StringComparison.Ordinal);
        Assert.Contains("  •  abcdef01\n", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef012", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "RAID QUIET · warn t300")]
    [InlineData(400, "RAID IN 1100t")]
    public void The_raid_phase_is_quiet_then_counts_down(int tick, string expected)
    {
        Assert.Equal(expected, HudText.RaidPhase(PresentationFixtures.Baseline(tick)));
    }

    [Fact]
    public void An_active_raid_outranks_the_countdown_and_an_outcome_outranks_the_raid()
    {
        var state = PresentationFixtures.Baseline(1);
        var raiding = state with
        {
            Raiders = [new PrototypeRaiderSnapshot(0, 10, 3, new GridPoint(27, 13), 0, 0, false, RaiderMode.Raiding)],
        };

        Assert.Equal("RAID ACTIVE", HudText.RaidPhase(raiding));
        Assert.Equal(
            "RAID HELD",
            HudText.RaidPhase(raiding with
            {
                SessionResult = state.SessionResult with { Outcome = "HELD" },
            }));
    }

    /// <summary>
    /// The empty panel repeats its own header. That is what the shipped HUD does,
    /// so it is what this asserts; changing it is a product decision, not a tidy-up.
    /// </summary>
    [Fact]
    public void An_empty_event_buffer_still_carries_the_header_and_the_diagnostics_count()
    {
        var state = PresentationFixtures.Baseline(1) with { Events = [] };

        Assert.Equal(
            "EVENT FEEDBACK\nEVENT FEEDBACK\n" +
            "No events yet. Step or unpause to watch autonomous choices." +
            "\n\nDiagnostics: 0 (structured JSON is emitted by smoke/capture).",
            HudText.Feedback(View(state)));
    }

    [Fact]
    public void The_event_panel_shows_the_last_three_choices_newest_first()
    {
        var state = PresentationFixtures.Baseline(400);
        Assert.True(state.Events.Count >= 4);
        var newest = state.Events[^1];

        var feedback = HudText.Feedback(View(state, diagnosticCount: 2));

        var lines = feedback.Split('\n');
        Assert.Equal("EVENT FEEDBACK", lines[0]);
        Assert.Equal(
            $"t{newest.LastTick} · {HudText.CreatureName(state, newest.CreatureId)}",
            lines[1]);
        Assert.Equal(newest.ReasonCode, lines[2]);
        // Header, three events at two lines each, a blank line, the diagnostics line.
        Assert.Equal(9, lines.Length);
        Assert.EndsWith(
            "Diagnostics: 2 (structured JSON is emitted by smoke/capture).",
            feedback,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An event about a creature the snapshot no longer carries must still read.
    /// </summary>
    [Fact]
    public void An_unknown_creature_id_degrades_to_a_hash_instead_of_disappearing()
    {
        var state = PresentationFixtures.Baseline(1);

        Assert.Equal(state.Creatures[0].Name, HudText.CreatureName(state, state.Creatures[0].Id));
        Assert.Equal("#404", HudText.CreatureName(state, 404));
    }

    [Fact]
    public void An_untouched_session_reports_an_empty_command_log()
    {
        var state = PresentationFixtures.Baseline(1);

        var roster = HudText.Roster(View(state, controlFeedback: "Inspect mode (Esc); brush cancelled."));

        var lines = roster.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("CREW  ", lines[0]);
        Assert.Equal("Inspect mode (Esc); brush cancelled.", lines[1]);
        Assert.Equal("LOG empty", lines[2]);
    }

    [Fact]
    public void The_command_log_line_keeps_only_the_two_most_recent_commands()
    {
        var state = PresentationFixtures.Baseline(1);
        var commands = new PrototypeCommand[]
        {
            new DigDesignateCommand(1, [new GridPoint(25, 1)]),
            new SetRuleCommand(2, "ration_reserve", 4),
            new SetPriorityCommand(3, JobKind.Dig, 5),
            new ZonePaintCommand(200, ZoneKind.MaterialStockpile, [new GridPoint(22, 1), new GridPoint(23, 1)]),
        };

        Assert.EndsWith(
            "\nLOG t3 priority Dig=5 | t200 paint MaterialStockpile (2)",
            HudText.Roster(View(state, playerCommands: commands)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_command_kind_has_its_own_short_form()
    {
        Assert.Equal(
            "t1 paint Farm (1)",
            HudText.DescribeCommand(new ZonePaintCommand(1, ZoneKind.Farm, [new GridPoint(1, 1)])));
        Assert.Equal(
            "t2 erase Forbidden (2)",
            HudText.DescribeCommand(new ZoneEraseCommand(
                2, ZoneKind.Forbidden, [new GridPoint(1, 1), new GridPoint(1, 2)])));
        Assert.Equal(
            "t3 dig_designate (1)",
            HudText.DescribeCommand(new DigDesignateCommand(3, [new GridPoint(25, 1)])));
        Assert.Equal(
            "t4 dig_cancel (1)",
            HudText.DescribeCommand(new DigCancelCommand(4, [new GridPoint(25, 1)])));
        Assert.Equal(
            "t5 priority Haul=3",
            HudText.DescribeCommand(new SetPriorityCommand(5, JobKind.Haul, 3)));
        Assert.Equal(
            "t6 rule muster_lead_ticks=12",
            HudText.DescribeCommand(new SetRuleCommand(6, "muster_lead_ticks", 12)));
    }

    [Fact]
    public void The_crew_line_abbreviates_every_creature_mode()
    {
        var creature = PresentationFixtures.Baseline(1).Creatures[0];

        Assert.Equal("DOWN", HudText.CreatureStateShort(creature with { Mode = CreatureMode.Downed }));
        Assert.Equal("FLED", HudText.CreatureStateShort(creature with { Mode = CreatureMode.Fled }));
        Assert.Equal("FIGHT", HudText.CreatureStateShort(creature with { Mode = CreatureMode.Fighting }));
        Assert.Equal("WORK", HudText.CreatureStateShort(creature with { Mode = CreatureMode.Working }));
        Assert.Equal("MOVE", HudText.CreatureStateShort(creature with { Mode = CreatureMode.Moving }));
        Assert.Equal("READY", HudText.CreatureStateShort(creature with { Mode = CreatureMode.Waiting }));
        Assert.Equal("READY", HudText.CreatureStateShort(creature with { Mode = CreatureMode.Eating }));
    }

    [Fact]
    public void The_inspector_header_spells_the_life_state_out_in_full()
    {
        var creature = PresentationFixtures.Baseline(1).Creatures[0];

        Assert.Equal("DOWNED", HudText.CreatureLifeState(creature with { Mode = CreatureMode.Downed }));
        Assert.Equal("FLED", HudText.CreatureLifeState(creature with { Mode = CreatureMode.Fled }));
        Assert.Equal(
            "ALIVE / FIGHTING",
            HudText.CreatureLifeState(creature with { Mode = CreatureMode.Fighting }));
        Assert.Equal("ALIVE", HudText.CreatureLifeState(creature with { Mode = CreatureMode.Working }));
    }

    /// <summary>
    /// One call builds all four panels, so a caller cannot update three of them
    /// and leave the fourth showing the previous frame.
    /// </summary>
    [Fact]
    public void Build_returns_the_same_four_panels_the_individual_calls_produce()
    {
        var view = View(PresentationFixtures.FullChain(336));

        var panels = HudText.Build(view);

        Assert.Equal(HudText.Summary(view), panels.Summary);
        Assert.Equal(HudText.Inspector(view), panels.Inspector);
        Assert.Equal(HudText.Feedback(view), panels.Feedback);
        Assert.Equal(HudText.Roster(view), panels.Roster);
    }

    private static HudViewState View(
        PrototypeSnapshot state,
        string fixture = "baseline",
        string checksum = "0123abcdef",
        bool paused = true,
        double speed = 1.0,
        int? selectedCreatureId = null,
        GridPoint? selectedCell = null,
        string controlFeedback = "",
        IReadOnlyList<PrototypeCommand>? playerCommands = null,
        int diagnosticCount = 0) =>
        new(
            state,
            fixture,
            checksum,
            paused,
            speed,
            selectedCreatureId,
            selectedCell,
            controlFeedback,
            playerCommands ?? [],
            diagnosticCount);
}
