using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// The stable identity of every control. Ids are the join between three things
/// that must not drift apart: the button in the adapter, the icon in
/// <see cref="UiIconManifest"/> and the <c>ui.controls</c> entry an automated
/// check reads. They are never shown to a player.
/// </summary>
public static class UiControlIds
{
    public const string Run = "run";
    public const string Pause = "pause";
    public const string Step = "step";
    public const string Speed0_5 = "speed_0_5";
    public const string Speed1 = "speed_1";
    public const string Speed4 = "speed_4";
    public const string Speed16 = "speed_16";
    public const string FixtureBaseline = "fixture_baseline";
    public const string FixtureNeglected = "fixture_neglected";
    public const string Replay = "replay";

    public const string Inspect = "inspect";
    public const string Paint = "paint";
    public const string Erase = "erase";
    public const string Dig = "dig";
    public const string DigCancel = "dig_cancel";
    public const string Stockpile = "stockpile";
    public const string Build = "build";
    public const string BuildCancel = "build_cancel";
    public const string Zone = "zone";
    public const string Priority = "priority";
    public const string Rule = "rule";
}

/// <summary>Which of the two strips a control sits on.</summary>
public enum UiControlStrip
{
    /// <summary>Time and fixtures: run/pause, step, speed, the debug affordances.</summary>
    Time,

    /// <summary>Brushes and the three selectors — everything that marks the map.</summary>
    Brush,
}

/// <summary>
/// One button, as text. This is the whole contract an automated check has with
/// the toolbar: "which brushes exist and what do they do" is a unit test instead
/// of a screenshot somebody has to look at.
/// </summary>
/// <param name="Id">Stable identity; see <see cref="UiControlIds"/>.</param>
/// <param name="Label">
/// What is drawn as text. Empty for a control that an icon fully describes, and
/// non-empty exactly where the value cannot be an icon: the three selectors show
/// their current value, and speeds and fixtures stay numbers and words.
/// </param>
/// <param name="Hotkey">The badge drawn in the corner, and the key that works.</param>
/// <param name="Tooltip">Name and one short sentence, shown on hover.</param>
/// <param name="Active">Whether this is the state the game is in right now.</param>
/// <param name="Enabled">Whether pressing it would do anything.</param>
/// <param name="Icon">The manifest file name, or <c>null</c> for a text control.</param>
/// <param name="Strip">Which strip it belongs to.</param>
public sealed record UiControl(
    string Id,
    string Label,
    string Hotkey,
    string Tooltip,
    bool Active,
    bool Enabled,
    string? Icon,
    UiControlStrip Strip);

/// <summary>
/// Everything the toolbar is a function of. Deliberately small and deliberately
/// not the node: it is the same seam <see cref="HudViewState"/> draws, so a test
/// states a toolbar instead of driving an engine towards one.
/// </summary>
/// <param name="Mode">The brush being held.</param>
/// <param name="BrushZone">The zone the paint and erase brushes act on.</param>
/// <param name="SelectedJob">The job kind the priority selector points at.</param>
/// <param name="SelectedJobPriority">Its current priority.</param>
/// <param name="SelectedRuleId">The rule the rule selector points at.</param>
/// <param name="SelectedRuleValue">Its current value.</param>
/// <param name="Paused">Whether time is stopped.</param>
/// <param name="Speed">The time multiplier.</param>
/// <param name="Fixture">Which shipped command log the session started from.</param>
/// <param name="SessionComplete">Whether the session has run out of ticks.</param>
public sealed record UiControlsViewState(
    BrushMode Mode,
    ZoneKind BrushZone,
    JobKind SelectedJob,
    int SelectedJobPriority,
    string SelectedRuleId,
    int SelectedRuleValue,
    bool Paused,
    double Speed,
    string Fixture,
    bool SessionComplete);

/// <summary>
/// The two control strips as data.
///
/// The text lives here rather than in the adapter for the reason
/// <a href="../../docs/decisions/0011-presentation-layer-without-engine.md">ADR 0011</a>
/// gives: a tooltip is the text a player learns the game from, and text that can
/// only be read by starting Godot is text nothing in CI defends. Every string
/// below is covered by an ordinary unit test.
/// </summary>
public static class UiControls
{
    /// <summary>The rules the <c>[K]</c> selector cycles, in cycle order.</summary>
    public static IReadOnlyList<string> RuleIds { get; } =
        ["ration_reserve", "drill_min_satiety", "muster_lead_ticks"];

    /// <summary>Every control of both strips, in the order they are drawn.</summary>
    public static IReadOnlyList<UiControl> Build(UiControlsViewState view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return [.. TimeStrip(view), .. BrushStrip(view)];
    }

    /// <summary>
    /// The time strip. Run/pause and step are icons; the speeds and the two
    /// fixtures stay text on purpose — a digit is already universal, and the
    /// fixtures are a debug affordance rather than a game action, so an icon for
    /// them would be generation spent on something the player never uses.
    ///
    /// <c>REPLAY</c> sits here rather than with the brushes for the same reason:
    /// it rebuilds the world from the command log, which is what <c>BASE</c> and
    /// <c>NEGLECT</c> do, and it is not a brush. It also keeps the brush strip
    /// exactly what the spec describes — eight actions and three selectors.
    /// </summary>
    private static IEnumerable<UiControl> TimeStrip(UiControlsViewState view)
    {
        // One button with two faces: it says what pressing it will do, so a paused
        // game shows the play icon and a running one shows pause.
        yield return new UiControl(
            view.Paused ? UiControlIds.Run : UiControlIds.Pause,
            string.Empty,
            "P",
            view.Paused
                ? "Run [P]\nStart time. The crew keeps choosing its own work."
                : "Pause [P]\nStop time. Marking the map works while paused.",
            !view.Paused,
            !view.SessionComplete,
            UiIconManifest.FileFor(view.Paused ? UiControlIds.Run : UiControlIds.Pause),
            UiControlStrip.Time);

        yield return new UiControl(
            UiControlIds.Step,
            string.Empty,
            "S",
            "Step [S]\nAdvance exactly one simulation tick and stop.",
            false,
            !view.SessionComplete,
            UiIconManifest.FileFor(UiControlIds.Step),
            UiControlStrip.Time);

        foreach (var (id, speed, hotkey) in new (string Id, double Speed, string Hotkey)[]
                 {
                     (UiControlIds.Speed0_5, 0.5, "1"),
                     (UiControlIds.Speed1, 1.0, "2"),
                     (UiControlIds.Speed4, 4.0, "3"),
                     (UiControlIds.Speed16, 16.0, "4"),
                 })
        {
            yield return new UiControl(
                id,
                SpeedLabel(speed),
                hotkey,
                $"{SpeedLabel(speed)} [{hotkey}]\nRun time at {SpeedLabel(speed)}. Speed is presentation only " +
                "and never enters canonical state.",
                view.Speed == speed,
                !view.SessionComplete,
                null,
                UiControlStrip.Time);
        }

        yield return new UiControl(
            UiControlIds.FixtureBaseline,
            "BASE",
            "R",
            "Baseline fixture [R]\nReload the shipped baseline session from tick 1. " +
            "Everything marked in this session is discarded.",
            view.Fixture == "baseline",
            true,
            null,
            UiControlStrip.Time);

        yield return new UiControl(
            UiControlIds.FixtureNeglected,
            "NEGLECT",
            "N",
            "Neglected fixture [N]\nReload the starvation-prone session from tick 1. " +
            "Everything marked in this session is discarded.",
            view.Fixture == "neglected",
            true,
            null,
            UiControlStrip.Time);

        yield return new UiControl(
            UiControlIds.Replay,
            "REPLAY",
            "Y",
            "Replay [Y]\nRebuild the world from the command log and compare checksums. " +
            "A mismatch means the projection drifted from canonical state.",
            false,
            true,
            null,
            UiControlStrip.Time);
    }

    /// <summary>
    /// The brush strip: eight actions as icons, then the three selectors.
    ///
    /// The selectors are not replaced by an icon and that is the point of them:
    /// an icon can say "this is the zone selector", but it cannot say
    /// <em>which</em> zone. They are the only three elements of the strip whose
    /// text stays on screen, and it stays deliberately.
    /// </summary>
    private static IEnumerable<UiControl> BrushStrip(UiControlsViewState view)
    {
        // The row order is the one the text strip had. A player who learned where
        // DIG sits should not have to hunt for it in the step that changes its
        // shape. STOCK keeps its place next to the brush it is a shortcut for.
        foreach (var (id, hotkey, active, tooltip) in new (string Id, string Hotkey, bool Active, string Tooltip)[]
                 {
                     (UiControlIds.Inspect, "I", view.Mode == BrushMode.Inspect,
                         "Inspect [I]\nClick a creature or a cell to read why it is doing what it is doing."),
                     (UiControlIds.Paint, "B", view.Mode == BrushMode.Paint,
                         "Paint zone [B]\nDrag a rectangle to add the selected zone to every cell in it."),
                     (UiControlIds.Erase, "E", view.Mode == BrushMode.Erase,
                         "Erase zone [E]\nDrag a rectangle to remove the selected zone. " +
                         "Erasing a stockpile cell drops its stone back on the tile."),
                     (UiControlIds.Dig, "D", view.Mode == BrushMode.Dig,
                         "Dig [D]\nDrag a rectangle over rock to mark it for excavation. " +
                         "Nobody is ordered: a free creature picks the job on its own."),
                     (UiControlIds.DigCancel, "X", view.Mode == BrushMode.CancelDig,
                         "Cancel dig [X]\nDrag a rectangle to withdraw dig marks. " +
                         "Work already done on a tile is lost."),
                     (UiControlIds.Stockpile, "M",
                         view.Mode == BrushMode.Paint && view.BrushZone == ZoneKind.MaterialStockpile,
                         "Material stockpile [M]\nSelects the paint brush and the MaterialStockpile zone " +
                         $"together. Each cell holds {PrototypeTuning.StockpileCellCapacity} stone."),
                     (UiControlIds.Build, "C", view.Mode == BrushMode.Build,
                         "Build post [C]\nDrag a rectangle over plain floor — including ground you dug — " +
                         "to mark training posts. Each costs stone the crew fetches itself."),
                     (UiControlIds.BuildCancel, "V", view.Mode == BrushMode.CancelBuild,
                         "Cancel blueprint [V]\nDrag a rectangle to withdraw blueprints. " +
                         "Stone already delivered drops back onto the tile."),
                 })
        {
            yield return new UiControl(
                id,
                string.Empty,
                hotkey,
                tooltip,
                active,
                true,
                UiIconManifest.FileFor(id),
                UiControlStrip.Brush);
        }

        yield return new UiControl(
            UiControlIds.Zone,
            ShortZone(view.BrushZone),
            "Z",
            $"Zone: {view.BrushZone} [Z]\nWhich zone the paint and erase brushes act on. " +
            "Click to cycle.",
            false,
            true,
            UiIconManifest.FileFor(UiControlIds.Zone),
            UiControlStrip.Brush);

        yield return new UiControl(
            UiControlIds.Priority,
            $"{view.SelectedJob} {view.SelectedJobPriority}",
            "J",
            $"Priority: {view.SelectedJob} = {view.SelectedJobPriority} [J]\n" +
            "Click to cycle the job kind; [+] and [-] change its priority. " +
            "0 stops that kind of work entirely.",
            false,
            true,
            UiIconManifest.FileFor(UiControlIds.Priority),
            UiControlStrip.Brush);

        yield return new UiControl(
            UiControlIds.Rule,
            $"{ShortRuleId(view.SelectedRuleId)} {view.SelectedRuleValue}",
            "K",
            $"Rule: {view.SelectedRuleId} = {view.SelectedRuleValue} [K]\n" +
            "Click to cycle the standing rule; [+] and [-] change its value.",
            false,
            true,
            UiIconManifest.FileFor(UiControlIds.Rule),
            UiControlStrip.Brush);
    }

    /// <summary>
    /// The same wording the text strip used, kept so the buttons the player
    /// learned do not change their name in the step that changes their shape.
    /// </summary>
    public static string ShortZone(ZoneKind zone) => zone switch
    {
        ZoneKind.Kitchen => "Kitch",
        ZoneKind.Quarters => "Quart",
        ZoneKind.TrainingGround => "Train",
        ZoneKind.Forbidden => "Forbid",
        ZoneKind.MaterialStockpile => "Stock",
        _ => zone.ToString(),
    };

    /// <inheritdoc cref="ShortZone"/>
    public static string ShortRuleId(string ruleId) => ruleId switch
    {
        "ration_reserve" => "ration",
        "drill_min_satiety" => "drillSat",
        _ => "muster",
    };

    private static string SpeedLabel(double speed) =>
        speed == 0.5 ? "0.5x" : $"{speed:0}x";
}
