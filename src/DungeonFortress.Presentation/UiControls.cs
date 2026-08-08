using System.Globalization;

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
/// <param name="MomentOfTruthOpen">
/// Whether the party is standing still waiting for a verdict. The toolbar has to
/// know: while it is true pressing RUN moves no tick at all, and until Issue #331
/// the button said nothing about why — the owner's playtest read that as a broken
/// pause ("Снять с паузы нельзя — видимо так ждётся что-то, но на UI не понимаю
/// что делать").
/// </param>
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
    bool SessionComplete,
    bool MomentOfTruthOpen = false);

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
        //
        // While a verdict is owed it grows a third thing to say. The button is
        // deliberately still enabled: waiting the window out is one of the two
        // ways the moment of truth closes, and disabling RUN would take that way
        // away. What was missing is the sentence, not the refusal.
        yield return new UiControl(
            view.Paused ? UiControlIds.Run : UiControlIds.Pause,
            string.Empty,
            "P",
            view.MomentOfTruthOpen
                ? "Run [P]\n" + MomentOfTruthPanel.TimeIsHeldTooltip
                : view.Paused
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
            view.MomentOfTruthOpen
                ? "Step [S]\n" + MomentOfTruthPanel.TimeIsHeldTooltip
                : "Step [S]\nAdvance exactly one simulation tick and stop.",
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

    // The toolbar and the summary print the same speed, so they print it through
    // the same function: the half-speed literal this replaced was a special case
    // that existed only because the general branch could not be trusted with a
    // fraction under a ru-RU culture (Issue #46).
    private static string SpeedLabel(double speed) => HudText.Speed(speed);
}

// =====================================================================
// The moment of truth as clickable controls (Issue #331)
//
// It lives beside the toolbar rather than in a file of its own because it is
// the same kind of thing: the text and the ids of a family of buttons, decided
// where a unit test can read them (ADR 0011), with the adapter left holding
// layout and dispatch. The difference from the two strips above is that this
// family is not fixed — the domain chooses how many cards there are and who
// they are about — so it is built from the snapshot instead of from a constant
// list, and it cannot be matched to buttons by position the way UiControls.Build
// is.
//
// Why it exists at all: the cards were already text (HudText.MomentOfTruth), but
// that text landed in the inspector column on the right, and the owner's first
// playtest of slice 3 (2026-08-08) never found it — «после боя игра запаузилась
// и непонятно куда нажимать и где ожидается ввод». The verdicts were reaching
// the simulation the whole time. What was missing was somewhere to look and
// something to press.
// =====================================================================

/// <summary>
/// What a press on the moment-of-truth band means. The band never decides a
/// verdict is legal — that is the simulation's answer on the tick of the command
/// (<a href="../../docs/decisions/0019-verdict-not-order.md">ADR 0019</a>) — it
/// only says which creature the player pointed at and with what sign.
/// </summary>
public enum MomentOfTruthPressKind
{
    /// <summary>Point the inspector at the creature this card is about.</summary>
    Select,

    /// <summary>Reward it, which is <c>VerdictKind.Reward</c> to the simulation.</summary>
    Reward,

    /// <summary>Punish it, which is <c>VerdictKind.Punish</c> to the simulation.</summary>
    Punish,
}

/// <summary>One resolved press: what was asked for, and about whom.</summary>
public sealed record MomentOfTruthPress(MomentOfTruthPressKind Kind, int CreatureId);

/// <summary>
/// The stable ids of the band's controls. Index-based rather than creature-based
/// because the adapter builds the rows once and refills them: the row is the
/// thing that persists, the creature on it is not.
/// </summary>
public static class MomentOfTruthControlIds
{
    public const string CardPrefix = "mot_card_";
    public const string RewardPrefix = "mot_reward_";
    public const string PunishPrefix = "mot_punish_";

    public static string Card(int index) => CardPrefix + Index(index);

    public static string Reward(int index) => RewardPrefix + Index(index);

    public static string Punish(int index) => PunishPrefix + Index(index);

    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One card as three controls: the card itself, which points at the creature,
/// and the two answers.
/// </summary>
/// <param name="Index">Which row of the band this is.</param>
/// <param name="CreatureId">Who the domain is reporting on.</param>
/// <param name="Text">
/// The sentence, which is <see cref="HudText.MomentOfTruthCardLine"/> and not a
/// second wording of it.
/// </param>
/// <param name="Verdict">The answer already given, or <c>null</c>.</param>
/// <param name="Selected">Whether the inspector is already pointed here.</param>
public sealed record MomentOfTruthCardControl(
    int Index,
    int CreatureId,
    string Text,
    string? Verdict,
    bool Selected)
{
    public string CardId => MomentOfTruthControlIds.Card(Index);

    public string RewardId => MomentOfTruthControlIds.Reward(Index);

    public string PunishId => MomentOfTruthControlIds.Punish(Index);

    /// <summary>An answered card takes no second answer.</summary>
    public bool Answered => Verdict is not null;

    public string CardTooltip =>
        $"{Text}\nClick to point the inspector at this creature. " +
        "REWARD and PUNISH answer the card without touching the map.";
}

/// <summary>
/// The whole band, as data. <see cref="Open"/> is the only thing the adapter has
/// to ask before deciding whether to show it.
/// </summary>
/// <param name="Unanswered">How many cards still have no verdict.</param>
/// <param name="StepsLeft">How many steps until the window closes on its own.</param>
/// <param name="Title">The one-line heading: wave, count, countdown.</param>
/// <param name="Explanation">What closes the window and what silence costs.</param>
public sealed record MomentOfTruthPrompt(
    bool Open,
    int WaveNumber,
    int Unanswered,
    int StepsLeft,
    string Title,
    string Explanation,
    IReadOnlyList<MomentOfTruthCardControl> Cards);

/// <summary>
/// The moment of truth as something to look at and press.
///
/// <para>Everything here is a projection of the canonical snapshot; nothing is
/// computed that the snapshot does not already carry, and nothing decides
/// whether an answer is allowed. Which creature is judged is the one whose card
/// was pressed, and whether that judgement is legal at all is answered by the
/// simulation on the tick of the command — ADR 0019, and the same seam the
/// <c>G</c>/<c>H</c> keys already went through.</para>
/// </summary>
public static class MomentOfTruthPanel
{
    public const string RewardLabel = "REWARD";
    public const string PunishLabel = "PUNISH";
    public const string RewardHotkey = "G";
    public const string PunishHotkey = "H";

    /// <summary>
    /// What RUN and STEP say while a verdict is owed. One sentence in one place,
    /// because the toolbar tooltip, the band and the line a refused press writes
    /// all have to say the same thing.
    /// </summary>
    public const string TimeIsHeldTooltip =
        "Time is not moving: the domain is waiting for a verdict. Answer the cards " +
        "under the map, or let the window run out — pressing this only spends the " +
        "window, it does not advance a tick.";

    private static readonly MomentOfTruthPrompt ClosedPrompt = new(
        false, 0, 0, 0, string.Empty, string.Empty, []);

    /// <summary>
    /// The band with nothing to ask. It is a value rather than <c>null</c> so
    /// that a caller with no world yet — the adapter builds the HUD before it
    /// loads a fixture — draws the same shape as a caller with a closed window.
    /// </summary>
    public static MomentOfTruthPrompt Closed => ClosedPrompt;

    /// <summary>
    /// The longest answer the canonical snapshot can carry on a card, for the
    /// layout guard's worst case. It is a literal because the mapping from
    /// <c>VerdictKind</c> to the word is <c>internal</c> to the simulation; both
    /// words are six characters, so the choice between them decides nothing, and
    /// <c>MomentOfTruthPanelTests</c> holds the worst case against a real card.
    /// </summary>
    private const string WidestVerdict = "punish";

    /// <summary>
    /// The band for one frame. Closed windows produce a closed prompt with no
    /// cards rather than <c>null</c>, so the adapter has one shape to draw.
    /// </summary>
    /// <param name="state">Canonical state.</param>
    /// <param name="selectedCreatureId">Who the inspector is pointed at.</param>
    public static MomentOfTruthPrompt Of(PrototypeSnapshot state, int? selectedCreatureId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var pause = state.MomentOfTruth;
        if (!pause.Open)
        {
            return ClosedPrompt;
        }

        var cards = pause.Cards
            .Select((card, index) => new MomentOfTruthCardControl(
                index,
                card.CreatureId,
                HudText.MomentOfTruthCardLine(card),
                card.Verdict,
                selectedCreatureId == card.CreatureId))
            .ToArray();

        var unanswered = cards.Count(card => !card.Answered);
        var stepsLeft = Math.Max(0, pause.WindowSteps - pause.WaitedSteps);
        return new MomentOfTruthPrompt(
            true,
            pause.WaveNumber,
            unanswered,
            stepsLeft,
            Title(pause.WaveNumber, unanswered, cards.Length, stepsLeft),
            Explanation(unanswered, stepsLeft),
            cards);
    }

    /// <summary>
    /// The heading: which wave is being answered for, how much is left to answer
    /// and how long the domain will keep asking. All three are the numbers the
    /// player is missing, and all three are read off the snapshot.
    /// </summary>
    public static string Title(int waveNumber, int unanswered, int cardCount, int stepsLeft) =>
        $"MOMENT OF TRUTH · wave {Number(waveNumber)} · {Number(unanswered)} of " +
        $"{Number(cardCount)} unanswered · {Number(stepsLeft)} steps left · time is held";

    /// <summary>
    /// The two ways out and the price of neither. It says "closes on its own"
    /// rather than "you may ignore it", because the window closing unanswered is
    /// not free: a card the domain raised for a deed and nobody answered is
    /// remembered as a slight (<c>grudge_ignored</c>).
    /// </summary>
    public static string Explanation(int unanswered, int stepsLeft) => unanswered == 0
        ? "Every card is answered. Time starts again on the next step."
        : $"Click a card to point at the creature, then REWARD [{RewardHotkey}] or " +
            $"PUNISH [{PunishHotkey}]. The window closes when all {Number(unanswered)} are " +
            $"answered, or by itself in {Number(stepsLeft)} steps — and a deed nobody " +
            "answered for is remembered against you.";

    /// <summary>
    /// The line a run writes when the player asks for time while the window is
    /// open. It carries the same numbers the band does, because the feedback line
    /// is where a player who pressed RUN is already looking.
    /// </summary>
    public static string TimeIsHeld(MomentOfTruthPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return $"Time is held by the moment of truth of wave {Number(prompt.WaveNumber)}: " +
            $"{Number(prompt.Unanswered)} card(s) unanswered, {Number(prompt.StepsLeft)} steps " +
            "until the window closes by itself. Answer the cards under the map — REWARD or " +
            "PUNISH — or keep pressing to spend the window.";
    }

    /// <summary>
    /// Every number this band prints, in the one culture the HUD is allowed to
    /// speak. Same rule and same reason as <see cref="HudText.Speed"/>: this text
    /// is a checked artefact, and a separator that follows the machine would pass
    /// locally and fail in CI (Issue #46).
    /// </summary>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// What a pressed control id means, or <c>null</c> when nothing in this band
    /// owns that id.
    ///
    /// <para>This is the whole of "clicking a card picks the creature it is
    /// about", and it is a function of the prompt rather than of a table the
    /// adapter keeps beside it: the row a player pressed and the creature the
    /// verdict names cannot drift apart, because there is only one description of
    /// the pairing.</para>
    /// </summary>
    public static MomentOfTruthPress? Press(MomentOfTruthPrompt prompt, string controlId)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (!prompt.Open || string.IsNullOrEmpty(controlId))
        {
            return null;
        }

        foreach (var card in prompt.Cards)
        {
            var kind = controlId switch
            {
                _ when string.Equals(controlId, card.CardId, StringComparison.Ordinal) =>
                    (MomentOfTruthPressKind?)MomentOfTruthPressKind.Select,
                _ when string.Equals(controlId, card.RewardId, StringComparison.Ordinal) =>
                    MomentOfTruthPressKind.Reward,
                _ when string.Equals(controlId, card.PunishId, StringComparison.Ordinal) =>
                    MomentOfTruthPressKind.Punish,
                _ => null,
            };

            if (kind is { } pressed)
            {
                return new MomentOfTruthPress(pressed, card.CreatureId);
            }
        }

        return null;
    }

    /// <summary>
    /// The widest band this party can ever produce, for the layout guard.
    ///
    /// <para>A capture stands wherever it was told to stand, and the window is
    /// open for a handful of steps of a whole party, so the shape a player reads
    /// is the one shape nothing would measure — the same hole
    /// <c>Main.CreatureStoryPanels</c> was written to close for the creature
    /// story. This builds a full band out of the widest card <em>this</em>
    /// snapshot's creatures can produce, so the guard measures a real sentence
    /// rather than a hand-written imitation of one.</para>
    /// </summary>
    /// <param name="state">Canonical state, open window or not.</param>
    public static MomentOfTruthPrompt WorstCase(PrototypeSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var widest = state.Creatures
            .Select(creature => HudText.MomentOfTruthCardLine(new PrototypeMomentOfTruthCard(
                creature.Id,
                creature.Name,
                creature.Loyalty,
                0,
                0,
                0,
                // The longest headline of the four Headline() can produce is the
                // plural deed, and it is longest with a two-digit count.
                RaidersDowned: 99,
                DominantAxis: "deed",
                Notability: 0,
                // An answered card is the wider one: it carries the arrow and the
                // verdict on top of everything an unanswered one has.
                Verdict: WidestVerdict)))
            .Concat([string.Empty])
            .OrderByDescending(line => line.Length)
            // A tie-break, so two creatures with equally long lines cannot make
            // the guard measure a different string on two runs of one seed.
            .ThenBy(line => line, StringComparer.Ordinal)
            .First();

        var cards = Enumerable
            .Range(0, PrototypeTuning.MomentOfTruthCards)
            .Select(index => new MomentOfTruthCardControl(
                index,
                index,
                widest,
                WidestVerdict,
                false))
            .ToArray();

        // The widest heading and the widest explanation are the ones with every
        // number at its longest, not the ones this frame happens to show.
        var stepsLeft = PrototypeTuning.MomentOfTruthWindowSteps;
        return new MomentOfTruthPrompt(
            true,
            state.Waves.Count,
            cards.Length,
            stepsLeft,
            Title(state.Waves.Count, cards.Length, cards.Length, stepsLeft),
            Explanation(cards.Length, stepsLeft),
            cards);
    }
}
