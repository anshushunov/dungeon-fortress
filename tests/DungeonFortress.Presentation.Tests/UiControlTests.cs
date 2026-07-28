using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The toolbar as an ordinary testable artifact.
///
/// Before the strips became Control nodes, every claim about a button — that it
/// exists, that it says what it does, that the right one is lit — could only be
/// checked by starting Godot and looking at a PNG. None of that ran in CI, and
/// the "Pure .NET" job still does not start an engine, so these are the checks a
/// pull request actually gets.
/// </summary>
public sealed class UiControlTests
{
    private static UiControlsViewState View(
        BrushMode mode = BrushMode.Inspect,
        ZoneKind zone = ZoneKind.Farm,
        JobKind job = JobKind.Harvest,
        int priority = 2,
        string rule = "ration_reserve",
        int ruleValue = 3,
        bool paused = true,
        double speed = 1.0,
        string fixture = "baseline",
        bool complete = false) =>
        new(mode, zone, job, priority, rule, ruleValue, paused, speed, fixture, complete);

    [Fact]
    public void Every_control_explains_itself()
    {
        foreach (var control in UiControls.Build(View()))
        {
            Assert.False(string.IsNullOrWhiteSpace(control.Id), $"'{control.Id}' has no id.");
            Assert.False(
                string.IsNullOrWhiteSpace(control.Hotkey),
                $"'{control.Id}' has no hotkey badge.");
            Assert.False(
                string.IsNullOrWhiteSpace(control.Tooltip),
                $"'{control.Id}' has no tooltip.");

            // The one rule that makes a row of symbols usable: a control is either
            // an icon or a value, and never neither.
            Assert.True(
                control.Icon is not null || control.Label.Length > 0,
                $"'{control.Id}' has neither an icon nor a label.");

            // The tooltip names the button and repeats its key, because that is
            // where a player looks when the icon did not land.
            Assert.Contains($"[{control.Hotkey}]", control.Tooltip, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Control_ids_are_unique()
    {
        var ids = UiControls.Build(View()).Select(control => control.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The brush strip is eight actions and three selectors, and the three
    /// selectors are the only elements of it that keep text on screen. That is the
    /// shape the spec asks for, and the width budget depends on it.
    /// </summary>
    [Fact]
    public void The_brush_strip_is_eight_icons_and_three_selectors()
    {
        var brush = UiControls.Build(View())
            .Where(control => control.Strip == UiControlStrip.Brush)
            .ToArray();

        Assert.Equal(11, brush.Length);
        Assert.Equal(8, brush.Count(control => control.Label.Length == 0));
        Assert.All(brush, control => Assert.NotNull(control.Icon));

        var selectors = brush.Where(control => control.Label.Length > 0).Select(c => c.Id).ToArray();
        Assert.Equal(
            new[] { UiControlIds.Zone, UiControlIds.Priority, UiControlIds.Rule },
            selectors);
    }

    /// <summary>
    /// The whole reason the three selectors are not icons: an icon can say "this
    /// is the zone selector", but it cannot say which zone is selected.
    /// </summary>
    [Fact]
    public void The_selectors_show_their_current_value()
    {
        var controls = UiControls.Build(View(
            zone: ZoneKind.MaterialStockpile,
            job: JobKind.Dig,
            priority: 4,
            rule: "drill_min_satiety",
            ruleValue: 40));

        Assert.Equal("Stock", Find(controls, UiControlIds.Zone).Label);
        Assert.Equal("Dig 4", Find(controls, UiControlIds.Priority).Label);
        Assert.Equal("drillSat 40", Find(controls, UiControlIds.Rule).Label);
    }

    [Fact]
    public void The_held_brush_is_the_active_one()
    {
        var controls = UiControls.Build(View(BrushMode.Dig));
        Assert.True(Find(controls, UiControlIds.Dig).Active);
        Assert.False(Find(controls, UiControlIds.Inspect).Active);
        Assert.Single(
            controls.Where(control => control.Strip == UiControlStrip.Brush && control.Active));
    }

    /// <summary>
    /// <c>[M]</c> is the paint brush with one zone preselected, so both buttons
    /// are lit: the shortcut does not hide which brush is actually held.
    /// </summary>
    [Fact]
    public void The_stockpile_shortcut_lights_the_paint_brush_too()
    {
        var controls = UiControls.Build(View(BrushMode.Paint, ZoneKind.MaterialStockpile));
        Assert.True(Find(controls, UiControlIds.Stockpile).Active);
        Assert.True(Find(controls, UiControlIds.Paint).Active);

        var otherZone = UiControls.Build(View(BrushMode.Paint, ZoneKind.Farm));
        Assert.False(Find(otherZone, UiControlIds.Stockpile).Active);
        Assert.True(Find(otherZone, UiControlIds.Paint).Active);
    }

    /// <summary>
    /// One button with two faces. It says what pressing it will do, so a paused
    /// game offers "run" and a running one offers "pause" — and the id changes
    /// with it, because the icon does.
    /// </summary>
    [Fact]
    public void Run_and_pause_are_one_button_that_states_what_it_will_do()
    {
        var paused = UiControls.Build(View(paused: true));
        Assert.Equal(UiControlIds.Run, paused[0].Id);
        Assert.Equal("icon_play.png", paused[0].Icon);
        Assert.False(paused[0].Active);

        var running = UiControls.Build(View(paused: false));
        Assert.Equal(UiControlIds.Pause, running[0].Id);
        Assert.Equal("icon_pause.png", running[0].Icon);
        Assert.True(running[0].Active);
    }

    [Fact]
    public void The_selected_speed_and_fixture_are_the_active_ones()
    {
        var controls = UiControls.Build(View(speed: 4.0, fixture: "neglected"));
        Assert.True(Find(controls, UiControlIds.Speed4).Active);
        Assert.False(Find(controls, UiControlIds.Speed1).Active);
        Assert.True(Find(controls, UiControlIds.FixtureNeglected).Active);
        Assert.False(Find(controls, UiControlIds.FixtureBaseline).Active);
    }

    /// <summary>
    /// Speeds and fixtures stay text and that is a decision, not an omission: a
    /// digit is already universal and a fixture is a debug affordance.
    /// </summary>
    [Fact]
    public void Speeds_and_fixtures_stay_text()
    {
        var controls = UiControls.Build(View());
        foreach (var id in new[]
                 {
                     UiControlIds.Speed0_5, UiControlIds.Speed1, UiControlIds.Speed4,
                     UiControlIds.Speed16, UiControlIds.FixtureBaseline,
                     UiControlIds.FixtureNeglected, UiControlIds.Replay,
                 })
        {
            var control = Find(controls, id);
            Assert.Null(control.Icon);
            Assert.NotEqual(string.Empty, control.Label);
        }

        Assert.Equal("0.5x", Find(controls, UiControlIds.Speed0_5).Label);
        Assert.Equal("16x", Find(controls, UiControlIds.Speed16).Label);
    }

    /// <summary>
    /// A finished session cannot be advanced, and a control that would do nothing
    /// says so rather than looking available.
    /// </summary>
    [Fact]
    public void Time_controls_are_disabled_once_the_session_is_over()
    {
        var running = UiControls.Build(View());
        Assert.All(running, control => Assert.True(control.Enabled));

        var complete = UiControls.Build(View(complete: true));
        Assert.False(Find(complete, UiControlIds.Step).Enabled);
        Assert.False(Find(complete, UiControlIds.Run).Enabled);
        Assert.False(Find(complete, UiControlIds.Speed1).Enabled);

        // Reloading a fixture and replaying the log still work: they are how a
        // finished session is left.
        Assert.True(Find(complete, UiControlIds.FixtureBaseline).Enabled);
        Assert.True(Find(complete, UiControlIds.Replay).Enabled);
        Assert.All(
            complete.Where(control => control.Strip == UiControlStrip.Brush),
            control => Assert.True(control.Enabled));
    }

    /// <summary>
    /// Every hotkey the toolbar advertises has to be a key the adapter actually
    /// binds. A badge promising a key nothing listens to is worse than no badge.
    /// </summary>
    [Fact]
    public void Every_hotkey_badge_is_distinct_within_its_strip()
    {
        foreach (var strip in new[] { UiControlStrip.Time, UiControlStrip.Brush })
        {
            var hotkeys = UiControls.Build(View())
                .Where(control => control.Strip == strip)
                .Select(control => control.Hotkey)
                .ToArray();
            Assert.Equal(hotkeys.Length, hotkeys.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void The_rule_selector_cycles_the_three_rules_the_simulation_knows()
    {
        Assert.Equal(
            new[] { "ration_reserve", "drill_min_satiety", "muster_lead_ticks" },
            UiControls.RuleIds);
    }

    private static UiControl Find(IReadOnlyList<UiControl> controls, string id) =>
        controls.Single(control => control.Id == id);
}
