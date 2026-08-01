using System.Globalization;

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
    public void The_summary_puts_identity_bookkeeping_and_the_two_domain_numbers_on_the_first_line()
    {
        var state = PresentationFixtures.Baseline(190);

        var summary = HudText.Summary(View(state, fixture: "baseline", paused: true));

        var lines = summary.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(
            $"BASELINE  •  t190  •  PAUSED  •  jobs {state.Jobs.Count}  •  0123abcd" +
            $"  •  renown {state.Domain.Renown}" +
            $"  •  strength {state.Domain.Strength}" +
            $"  •  crew {state.Domain.LivingCreatures}",
            lines[0]);
    }

    /// <summary>
    /// Numbers and a trend arrow, never a bar. Neither the head count nor the
    /// strength of a domain has a maximum, so a bar would state a share of
    /// something that does not exist.
    /// </summary>
    [Fact]
    public void The_domain_numbers_carry_a_trend_arrow_only_once_a_wave_has_landed()
    {
        var state = PresentationFixtures.Baseline(190);
        Assert.Null(state.Domain.RenownAtPreviousWave);
        Assert.DoesNotContain('↑', HudText.Summary(View(state)));
        Assert.DoesNotContain('↓', HudText.Summary(View(state)));
        Assert.DoesNotContain('→', HudText.Summary(View(state)));

        Assert.Equal(string.Empty, HudText.Trend(10, null));
        Assert.Equal("↑", HudText.Trend(11, 10));
        Assert.Equal("↓", HudText.Trend(9, 10));
        Assert.Equal("→", HudText.Trend(10, 10));

        var afterWave = state with
        {
            Domain = state.Domain with
            {
                Renown = 40,
                RenownAtPreviousWave = 20,
                Strength = 50,
                StrengthAtPreviousWave = 58,
            },
        };
        var first = HudText.Summary(View(afterWave)).Split('\n')[0];
        Assert.Contains("renown 40↑", first, StringComparison.Ordinal);
        Assert.Contains("strength 50↓", first, StringComparison.Ordinal);
    }

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
    /// Issue #46. The speed is the only fractional number the HUD prints, and it
    /// used to take the decimal separator of the machine: "0,5x" on a ru-RU
    /// desktop against "0.5x" in CI. Nothing caught it because all three golden
    /// frames are paused, so the branch never ran — the check passed for the
    /// wrong reason, and the first unpaused reference frame would have split the
    /// two environments with a diff nobody could explain.
    ///
    /// This text is a checked artefact, not a localised interface, so it is
    /// invariant everywhere.
    /// </summary>
    [Theory]
    [InlineData("ru-RU")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("")]
    public void The_speed_prints_the_same_under_any_culture_of_the_thread(string culture)
    {
        var state = PresentationFixtures.Baseline(10);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture.Length == 0
                ? CultureInfo.InvariantCulture
                : CultureInfo.GetCultureInfo(culture);

            Assert.Equal("0.5x", HudText.Speed(0.5));
            Assert.Equal("2x", HudText.Speed(2.0));
            Assert.StartsWith(
                "BASELINE  •  t10  •  0.5x  •",
                HudText.Summary(View(state, paused: false, speed: 0.5)),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                ',',
                HudText.Summary(View(state, paused: false, speed: 0.5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
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
        Assert.Contains("  •  abcdef01  •  renown", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef012", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "WAVE 1/4 · warn t300")]
    [InlineData(400, "WAVE 1/4 IN 900t ×4")]
    public void The_wave_phase_is_quiet_then_counts_the_named_wave_down(int tick, string expected)
    {
        Assert.Equal(expected, HudText.WavePhase(PresentationFixtures.Baseline(tick)));
    }

    [Fact]
    public void An_arriving_wave_outranks_its_countdown_and_the_end_of_the_party_outranks_both()
    {
        var state = PresentationFixtures.Baseline(1);
        var arriving = state with
        {
            Threat = state.Threat with { Announced = true, Active = true, RaiderCount = 6 },
        };

        Assert.Equal("WAVE 1/4 ACTIVE ×6", HudText.WavePhase(arriving));
        Assert.Equal(
            "DOMAIN HELD 4/4",
            HudText.WavePhase(arriving with
            {
                SessionResult = state.SessionResult with { Outcome = "held" },
            }));
        Assert.Equal(
            "DOMAIN FELL · wave 1/4",
            HudText.WavePhase(arriving with
            {
                SessionResult = state.SessionResult with { Outcome = "fallen" },
            }));
    }

    /// <summary>
    /// Three ends, three different words in the same place, so which one
    /// happened is read at a glance. "Raided" also carries how many waves were
    /// actually turned back, which is the number a player asks for next — and it
    /// comes from canonical state rather than being counted again here.
    /// </summary>
    [Fact]
    public void A_domain_that_survived_a_wave_getting_through_reads_as_raided()
    {
        var state = PresentationFixtures.Baseline(1);
        var raided = state with
        {
            SessionResult = state.SessionResult with
            {
                Outcome = "raided",
                WavesRepelled = 2,
            },
        };

        Assert.Equal("DOMAIN RAIDED · 2/4 repelled", HudText.WavePhase(raided));

        // An end of a party the HUD has not been taught is refused, not drawn.
        // The catch-all this replaced rendered anything unknown as "the domain
        // fell", which is the worst wording to arrive at by accident.
        var unknown = raided with
        {
            SessionResult = raided.SessionResult with { Outcome = "besieged" },
        };
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => HudText.WavePhase(unknown));
        Assert.Contains("besieged", refused.Message, StringComparison.Ordinal);
        Assert.NotEqual(HudText.WavePhase(raided), HudText.WavePhase(raided with
        {
            SessionResult = raided.SessionResult with { Outcome = "held" },
        }));
        Assert.NotEqual(HudText.WavePhase(raided), HudText.WavePhase(raided with
        {
            SessionResult = raided.SessionResult with { Outcome = "fallen" },
        }));
    }

    /// <summary>
    /// ADR 0016 split two questions by the moment they are asked. "How am I
    /// doing" is answered all party long by the gap between renown and domain
    /// strength; "how did I play" is answered once, at the end, by the party
    /// score. So the summary of a party in progress must not carry a score —
    /// not even a provisional one — and the end of the party must.
    /// </summary>
    [Fact]
    public void The_party_score_appears_with_the_end_of_the_party_and_never_before_it()
    {
        var running = PresentationFixtures.Baseline(400);
        Assert.Null(running.SessionResult.Outcome);
        Assert.Null(running.SessionResult.Score);

        var duringTheParty = HudText.Summary(View(running));
        Assert.DoesNotContain("score", duringTheParty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("renown", duringTheParty, StringComparison.Ordinal);
        Assert.Contains("strength", duringTheParty, StringComparison.Ordinal);

        var ended = running with
        {
            SessionResult = running.SessionResult with
            {
                Outcome = "raided",
                WavesRepelled = 2,
                Score = 678,
            },
        };

        Assert.Equal("DOMAIN RAIDED · 2/4 repelled · score 678", HudText.WavePhase(ended));
        Assert.Equal(
            "DOMAIN HELD 4/4 · score 678",
            HudText.WavePhase(ended with
            {
                SessionResult = ended.SessionResult with { Outcome = "held" },
            }));
        Assert.Equal(
            "DOMAIN FELL · wave 1/4 · score -12",
            HudText.WavePhase(ended with
            {
                SessionResult = ended.SessionResult with { Outcome = "fallen", Score = -12 },
            }));

        // The first line is the one the player reads all party long; the score
        // never joins it, so the summary does not grow a third number.
        Assert.Equal(
            HudText.Summary(View(running)).Split('\n')[0],
            HudText.Summary(View(ended)).Split('\n')[0]);
        Assert.Contains("score 678", HudText.Summary(View(ended)).Split('\n')[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The empty panel carries the header once, the invitation and the diagnostics
    /// count.
    ///
    /// <para>
    /// It used to print the header twice — the panel concatenated "EVENT FEEDBACK"
    /// in front of a body that already opened with it — and this test asserted the
    /// doubling as "what the shipped HUD does". Issue #145 gave the header facts to
    /// carry ("· 3 of 9 crew · 155 of 4425 mattered"), and a line that carries
    /// counts cannot be printed twice without lying about them. That is the product
    /// decision the old wording of this test asked for, made and written down.
    /// </para>
    /// </summary>
    [Fact]
    public void An_empty_event_buffer_still_carries_the_header_and_the_diagnostics_count()
    {
        var state = PresentationFixtures.Baseline(1) with { Events = [] };

        Assert.Equal(
            "EVENT FEEDBACK\n" +
            "No events yet. Step or unpause to watch autonomous choices." +
            "\n\nDiagnostics: 0 (structured JSON is emitted by smoke/capture).",
            HudText.Feedback(View(state)));
    }

    /// <summary>
    /// The shape of the domain feed: a header that says what is off the panel, at
    /// most <see cref="HudText.DomainFeedLines"/> event lines, a blank line and the
    /// diagnostics counter.
    ///
    /// <para>
    /// It used to assert that the first line was the newest entry of the journal.
    /// That is the defect Issue #145 was opened about — 96.5 % of the journal is
    /// waiting and stepping aside, so the newest three entries almost never mean
    /// anything — and what the panel shows now is one line per creature, the most
    /// significant thing each has decided. The rule itself is checked in
    /// <c>DomainFeedTests</c>; what is checked here is that the shape of the panel
    /// around it did not move.
    /// </para>
    /// </summary>
    [Fact]
    public void The_event_panel_is_a_header_three_lines_and_the_diagnostics_count()
    {
        var state = PresentationFixtures.Baseline(400);
        Assert.True(state.Events.Count >= 4);

        var feedback = HudText.Feedback(View(state, diagnosticCount: 2));

        var lines = feedback.Split('\n');
        Assert.StartsWith("EVENT FEEDBACK · ", lines[0], StringComparison.Ordinal);
        // One line per event since Issue #117, and it is a sentence rather than a
        // code: the name in front, then what the creature decided. The code is
        // still in the canonical state — the feed reads it through
        // <see cref="EventNarration"/> — and it is deliberately not on screen.
        var shown = HudText.DomainSelection(state.Events);
        Assert.Equal(HudText.DomainFeedLines, shown.Count);
        Assert.Equal($"t{shown[0].LastTick} · {EventNarration.Describe(state, shown[0])}", lines[1]);
        Assert.Contains(
            HudText.CreatureName(state, shown[0].CreatureId),
            lines[1],
            StringComparison.Ordinal);
        foreach (var code in state.Events.Select(@event => @event.ReasonCode).Distinct())
        {
            Assert.DoesNotContain(code, feedback, StringComparison.Ordinal);
        }

        // Header, three event lines, a blank line, the diagnostics line.
        Assert.Equal(3 + HudText.DomainFeedLines, lines.Length);
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
