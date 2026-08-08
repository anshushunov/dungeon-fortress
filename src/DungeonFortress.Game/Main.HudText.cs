using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

// The words the HUD shows, the layout pass that places them, and the
// guards that say they fit the frame and stay readable.
public partial class Main
{
    /// <summary>
    /// The summary as it will read when the party is over, one string per end.
    ///
    /// No entry point ever draws this frame: a smoke run stops at tick 1 and a
    /// screenshot run stops wherever it was told to, so the one frame the owner
    /// actually reads their result in was the one frame nothing measured. It is
    /// also the widest the line ever gets — `DOMAIN RAIDED · N/4 repelled ·
    /// score N` runs well past the longest countdown — so it is precisely the
    /// case that would wrap onto a third line over the time toolbar.
    ///
    /// The strings come from the real <see cref="HudText"/> on the real snapshot
    /// with only the session result substituted, so this measures the shipping
    /// wording rather than a hand-written imitation of it. That substitution has
    /// to cover **every** field the terminal wording reads. The party score was
    /// the second one, and inheriting it from a live snapshot — where it is
    /// null, because a party in progress has no score — silently returned this
    /// guard to measuring the wording it replaced, some fourteen characters
    /// short of what ships. A guard that stays green by no longer measuring the
    /// thing it was written for is the defect Issue #49 is about.
    /// </summary>
    private (string Outcome, string Text)[] TerminalSummaries()
    {
        if (_state is null)
        {
            return [];
        }

        var view = CurrentHudView();

        // Chosen for width rather than for realism: the guard has to measure the
        // widest score the wording can ever carry, not a comfortable one. That is
        // the whole `held` band plus everything a party can still be holding at
        // the end, with a minus sign in front because a ruined party's score goes
        // negative. Deriving it from the weights keeps the worst case honest when
        // the weights move, which they may — they are tuning by ADR 0010.
        var widestScore = -(PrototypeTuning.ScoreOutcomeHeld +
            _state.Waves.Count * PrototypeTuning.ScorePerWaveRepelled +
            _state.Creatures.Count * PrototypeTuning.ScorePerSurvivor +
            PrototypeTuning.MealTarget * PrototypeTuning.ScorePerMealKept);

        return new[] { ("held", _state.Waves.Count), ("raided", 1), ("fallen", 0) }
            .Select(end => (end.Item1, HudText.Summary(view with
            {
                Snapshot = _state with
                {
                    SessionResult = _state.SessionResult with
                    {
                        Outcome = end.Item1,
                        WavesRepelled = end.Item2,
                        Score = widestScore,
                    },
                },
            })))
            .ToArray();
    }

    /// <summary>
    /// Puts a candidate summary into the real label and measures it at a given
    /// frame size. The text is left in place for the caller to keep measuring
    /// and is put back by <see cref="RestoreSummary"/>.
    /// </summary>
    private (int Needed, int Shown) MeasureSummary(string text, HudFitFrame frame)
    {
        _summary!.Text = text;
        LayoutHud(frame.Viewport, frame.UiScale);
        return (_summary.GetLineCount(), _summary.GetVisibleLineCount());
    }

    private void RestoreSummary()
    {
        if (_state is not null)
        {
            _summary!.Text = HudText.Summary(CurrentHudView());
        }
    }

    /// <summary>
    /// The event panel as it reads when a creature is selected: one entry per
    /// creature in the session, plus a padded worst case (Issue #128).
    ///
    /// <para>
    /// The panel is the domain feed until somebody is selected, and no entry
    /// point selects anybody unless it is told to, so without this the story a
    /// player actually reads would be the one shape of the panel nothing
    /// measured — the same hole <see cref="TerminalSummaries"/> was written to
    /// close for the end-of-party summary. The story is also the taller of the
    /// two shapes: the domain feed is three lines and the story is
    /// <see cref="HudText.CreatureStoryLines"/>.
    /// </para>
    ///
    /// <para>
    /// The padded case exists because a smoke run stands at tick 1, where a
    /// creature has one journal entry and the panel is one line high. It repeats
    /// the widest line this session's journal can produce until the panel is at
    /// its full height, so every entry point measures a full-height panel rather
    /// than only the runs that happen to stop late. It is a floor under the
    /// check and not the whole of it: the widest line at tick 1 is a narrower
    /// sentence than a refusal by memory of place carrying a tile and a tick, so
    /// a run captured after a wave measures more than this can. One such run is
    /// recorded in <c>evidence/128-history.json</c>.
    /// </para>
    /// </summary>
    private (string Who, string Text)[] CreatureStoryPanels()
    {
        if (_state is null)
        {
            return [];
        }

        var view = CurrentHudView();
        var panels = _state.Creatures
            .Select(creature => (
                Who: creature.Name,
                Text: HudText.Feedback(view with { SelectedCreatureId = creature.Id })))
            .ToArray();
        if (panels.Length == 0)
        {
            return panels;
        }

        var widest = panels
            .SelectMany(panel => panel.Text.Split('\n'))
            .OrderByDescending(line => line.Length)
            .First();
        var padded = string.Join(
            "\n",
            Enumerable.Repeat(widest, HudText.CreatureStoryLines + 1));
        return [.. panels, ("widest padded story", padded)];
    }

    /// <summary>
    /// Puts a candidate event panel into the real label and measures it, the way
    /// <see cref="MeasureSummary"/> does for the summary. Put back by
    /// <see cref="RestoreFeedback"/>.
    /// </summary>
    private (int Needed, int Shown) MeasureFeedback(string text, HudFitFrame frame)
    {
        _feedback!.Text = text;
        LayoutHud(frame.Viewport, frame.UiScale);
        return (_feedback.GetLineCount(), _feedback.GetVisibleLineCount());
    }

    private void RestoreFeedback()
    {
        if (_state is not null)
        {
            _feedback!.Text = HudText.Feedback(CurrentHudView());
        }
    }

    /// <summary>
    /// The labels of the moment-of-truth band, named the way the overflow guard
    /// names everything else.
    ///
    /// <para>
    /// They are deliberately <b>not</b> in <see cref="HudLabels"/>. The band is
    /// hidden until a wave ends and a row is hidden when the domain raised fewer
    /// cards than there are rows, and a hidden Control is given no size by its
    /// container — so measuring them live would report every empty row as text
    /// that does not fit. They are measured instead by
    /// <see cref="MeasureMomentOfTruthBand"/> against the worst case
    /// <see cref="MomentOfTruthPanel.WorstCase"/> builds, at every frame, which
    /// is a wider band than any live one (proved in
    /// <c>MomentOfTruthPanelTests</c>).
    /// </para>
    /// </summary>
    private (string Name, Label? Label)[] MomentOfTruthLabels() =>
    [
        ("momentTitle", _momentTitle),
        ("momentExplanation", _momentExplanation),
        .. _momentRows.Select((row, index) => ($"momentCard[{index}]", (Label?)row.Text)),
    ];

    /// <summary>
    /// Shows the widest band this party can produce and measures every label of
    /// it, then puts the live one back.
    ///
    /// <para>
    /// This is the counterpart of <see cref="CreatureStoryPanels"/> for the band:
    /// a capture stands wherever it was told to stand, the window is open for a
    /// handful of steps of a whole party, and without this the one shape a player
    /// reads during the moment of truth would be the one shape nothing measures.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> MeasureMomentOfTruthBand(HudFitFrame frame)
    {
        if (_state is null || _momentBand is null)
        {
            return [];
        }

        ShowMomentOfTruth(MomentOfTruthPanel.WorstCase(_state));
        LayoutHud(frame.Viewport, frame.UiScale);
        var failures = new List<string>();
        foreach (var (name, label) in MomentOfTruthLabels())
        {
            if (label is null)
            {
                continue;
            }

            var needed = label.GetLineCount();
            var shown = label.GetVisibleLineCount();
            if (needed > shown)
            {
                failures.Add(
                    $"the moment of truth's '{name}' needs {needed} lines but only {shown} " +
                    $"fit in {FormatVector(label.Size)} at viewport " +
                    $"{FormatVector(frame.Viewport)}, UI scale {FormatNumber(frame.UiScale)}");
            }
        }

        RefreshMomentOfTruthBand();
        LayoutHud(frame.Viewport, frame.UiScale);
        return failures;
    }

    /// <summary>
    /// Every piece of HUD text the overflow guard measures. The four panels the
    /// golden UI state records come first; the header and the legend rows are
    /// here too, because a Control layout can squeeze them just as easily and
    /// nothing else would notice.
    /// </summary>
    private (string Name, Label? Label)[] HudLabels() =>
    [
        ("summary", _summary),
        ("inspector", _inspector),
        ("feedback", _feedback),
        ("roster", _roster),
        ("title", _title),
        .. _legendLines.Select((label, index) => ($"legend[{index}]", (Label?)label)),
    ];

    /// <summary>
    /// What the HUD currently says, as text rather than as pixels. Every branch of
    /// the inspector then becomes an ordinary testable artifact: pick the frame
    /// with <c>--screenshot-ticks</c>, point at a tile with <c>--select-cell</c>
    /// and assert a substring of <c>ui.inspector</c>.
    ///
    /// Nothing here depends on the camera. Pixel positions, the visible tile range
    /// and the viewport size are deliberately absent, because ADR 0008 drops the
    /// fixed 960x540 frame and those values stop being stable.
    /// </summary>
    private object UiText() => new
    {
        summary = _summary?.Text,
        inspector = _inspector?.Text,
        feedback = _feedback?.Text,
        roster = _roster?.Text,
        controlFeedback = _controlFeedback,
        editMode = _editMode.ToString(),
        brushZone = _brushZone.ToString(),
        selectedCell = _selectedCell is { } selected ? new[] { selected.X, selected.Y } : null,
        selectedCreatureId = _selectedCreatureId,
        // The toolbar as text. "Which brushes are available, what do they do and
        // which one is held" stops being a question only a screenshot answers,
        // which is what makes the icon pass checkable at all.
        controls = UiControls.Build(CurrentControlsView())
            .Select(control => (object)new
            {
                id = control.Id,
                label = control.Label,
                hotkey = control.Hotkey,
                tooltip = control.Tooltip,
                active = control.Active,
                enabled = control.Enabled,
                icon = control.Icon,
            })
            .ToArray(),
        // The band the moment of truth is answered on, as text and ids rather
        // than as pixels (Issue #331). It is what makes "the cards are visible
        // outside the inspector, they say how many are unanswered and how long
        // the window has left, and each one can be pressed" checkable by an
        // automated run instead of by looking at a screenshot.
        momentOfTruth = MomentOfTruthState(),
        // The rectangle in progress, so a drag is observable without a picture.
        // It is null unless the button is actually down, which is also the claim
        // "a cancelled drag left nothing behind".
        selection = PendingStroke() is { } stroke
            ? (object)new
            {
                mode = stroke.Mode.ToString(),
                tiles = stroke.Tiles.Count,
                rectangle = stroke.RectangleTiles,
                refusal = stroke.Refusal,
            }
            : null,
        // Intent accepted for this tick that the tick has not applied yet: the
        // marking the map draws over canonical state, and the priority changes
        // that decide how those marks read. "It showed up straight away" is then a
        // field in a structured run rather than something judged from a
        // screenshot. Null whenever nothing waits, which is every frame of
        // free-running time.
        pending = _projection is { HasPendingIntent: true } waiting
            ? (object)new
            {
                tick = _state!.Tick,
                commands = waiting.PendingCommandCount,
                digMarks = Tiles(waiting.PendingDigMarks),
                digWithdrawals = Tiles(waiting.PendingDigWithdrawals),
                buildMarks = Tiles(waiting.PendingBuildMarks),
                buildWithdrawals = Tiles(waiting.PendingBuildWithdrawals),
                stockpileCells = Tiles(waiting.PendingStockpileCells),
                priorities = waiting.PendingPriorities
                    .OrderBy(pair => pair.Key)
                    .Select(pair => (object)new { job = pair.Key.ToString(), value = pair.Value })
                    .ToArray(),
            }
            : null,
    };

    private static int[][] Tiles(IReadOnlyList<GridPoint> tiles) =>
        [.. tiles.Select(tile => new[] { tile.X, tile.Y })];

    /// <summary>
    /// The moment-of-truth band as data: whether it is on screen, what it says
    /// and which control answers which creature. The <c>visible</c> field is read
    /// off the live node rather than off the prompt, so the claim it supports is
    /// "the band is drawn" and not "the band was asked to be drawn".
    /// </summary>
    private object MomentOfTruthState()
    {
        var prompt = CurrentMomentOfTruth();
        return new
        {
            open = prompt.Open,
            visible = _momentBand?.Visible ?? false,
            // Whether the clock is stopped, next to whether the question is being
            // asked. The two together are the claim of round 2 of Issue #331: a
            // band the player has time to read. An open window reported with
            // `paused: false` is a band being spent at the speed of the toolbar —
            // 6.7 seconds at 1x, 0.42 at 16x.
            paused = _paused,
            // Named so a reader can tell at a glance that the band is not the
            // inspector column: the two are different nodes with different
            // parents, and Issue #331 is exactly about which one the cards are in.
            node = _momentBand is null ? null : _hudRoot?.GetPathTo(_momentBand).ToString(),
            waveNumber = prompt.WaveNumber,
            unanswered = prompt.Unanswered,
            stepsLeft = prompt.StepsLeft,
            title = prompt.Title,
            explanation = prompt.Explanation,
            cards = prompt.Cards
                .Select(card => (object)new
                {
                    index = card.Index,
                    creatureId = card.CreatureId,
                    text = card.Text,
                    verdict = card.Verdict,
                    selected = card.Selected,
                    cardId = card.CardId,
                    rewardId = card.RewardId,
                    punishId = card.PunishId,
                    // What the row actually shows, so the text on the button and
                    // the text in this record cannot drift apart.
                    drawn = card.Index < _momentRows.Count
                        ? _momentRows[card.Index].Text.Text
                        : null,
                })
                .ToArray(),
        };
    }

    /// <summary>
    /// Frame and UI-scale pairs the HUD is required to hold all of its text at.
    /// The live pair is always included. Larger UI scales are paired with larger
    /// frames because a scale that cannot fit is rejected rather than clipped.
    ///
    /// A size that is missing here is not "unsupported": it is unmeasured. The
    /// current text does not fit the old 960x540 frame at readable sizes — the
    /// side column needs about 33 lines and that frame offers about 29 — which is
    /// exactly the deficit Issue #28 measured and this Issue had to clear.
    /// </summary>
    private readonly record struct HudFitFrame(Vector2 Viewport, double UiScale)
    {
        public Vector2 LogicalViewport => Viewport / (float)UiScale;
    }

    private HudFitFrame[] HudFitFrames() =>
        new[]
        {
            new HudFitFrame(GetViewportRect().Size, _uiScale),
            new HudFitFrame(new Vector2(1280, 720), 1.0),
            new HudFitFrame(new Vector2(1366, 768), 1.0),
            new HudFitFrame(new Vector2(1600, 900), 1.0),
            new HudFitFrame(new Vector2(1024, 768), 1.0),
            new HudFitFrame(new Vector2(1920, 1080), 1.25),
            new HudFitFrame(new Vector2(2048, 1440), 2.0),
            // The owner's maximized client area at the scale the automatic
            // policy gives it: logical 1522x861, and the frame Issue #86 was
            // opened about. The overflow guard had never measured it.
            new HudFitFrame(new Vector2(3044, 1722), 2.0),
        }
        .DistinctBy(frame => (frame.LogicalViewport.X, frame.LogicalViewport.Y))
        .ToArray();

    /// <summary>
    /// Lays the HUD out at a given frame size and waits for nothing. Godot sorts
    /// containers on a deferred pass, so a guard that ran in <c>_Ready</c> without
    /// this would measure rectangles nobody had laid out yet. Notifying the
    /// subtree runs every container's sort synchronously, parent first, which is
    /// the same placement a frame would produce.
    ///
    /// Two passes, because a wrapped legend row's height depends on the width the
    /// first pass hands it. Asking for that height back is what makes a narrow
    /// frame take space away from the panels that can spare it instead of
    /// quietly clipping the legend.
    /// </summary>
    private void LayoutHud(Vector2 size, double uiScale)
    {
        _hudRoot!.Scale = Vector2.One * (float)uiScale;
        _hudRoot.Size = size / (float)uiScale;
        // The tooltip popup cannot inherit _hudRoot.Scale (see HudButton.UiScale
        // for why), so every button is told the same uiScale this call gives
        // the rest of the HUD, and applies it the next time Godot asks it for a
        // tooltip.
        foreach (var button in _controlButtons)
        {
            button.UiScale = uiScale;
        }

        _hudRoot.PropagateNotification((int)Container.NotificationSortChildren);
        foreach (var line in _legendLines)
        {
            line.CustomMinimumSize = new Vector2(0, HudTextHeight(line, line.GetLineCount()));
        }

        // The band's own two lines, for exactly the reason the legend rows are
        // here: an autowrapping Label reports a minimum height of nothing,
        // because it can always wrap narrower, so a container that has no spare
        // room gives it one pixel. Measured on this Issue's first engine run —
        // "'momentTitle' needs 1 lines but only 0 fit in (843, 1)" at every one
        // of the eight frames the guard checks.
        foreach (var label in new[] { _momentTitle, _momentExplanation })
        {
            if (label is not null)
            {
                label.CustomMinimumSize = new Vector2(
                    0,
                    HudTextHeight(label, label.GetLineCount()));
            }
        }

        // The same second pass for the cards of the moment of truth, and for the
        // same reason: the card's Label is anchored to its Button rather than
        // laid out by a container, so the Button cannot know how tall a wrapped
        // sentence made it. The first pass gives the row its width; this asks how
        // many lines that width costs and reserves them (Issue #331).
        foreach (var row in _momentRows)
        {
            row.Card.CustomMinimumSize = new Vector2(
                0,
                Math.Max(
                    ControlButtonSize,
                    HudTextHeight(row.Text, row.Text.GetLineCount()) + MomentCardPadding));
        }

        _hudRoot.PropagateNotification((int)Container.NotificationSortChildren);
        LayoutWorldViewportMasks(size);
    }

    /// <summary>
    /// A <see cref="Label"/> that does not fit its rectangle silently loses text:
    /// unclipped it draws over the panel below it, clipped it drops the
    /// overflowing lines. Both happened in Issue #26 and both were found by eye on
    /// a PNG.
    ///
    /// The check is made against the rectangle the layout produced and never
    /// against a window constant, because ADR 0008 drops the fixed 960x540 frame.
    /// Since the HUD became a Control tree the measurement is only meaningful
    /// *after* a layout pass, which is the opposite of what the absolute layout
    /// needed: a container gives a label its size, so the size is the designed one
    /// and an unclipped label can no longer re-expand to its own content.
    /// <see cref="LayoutHud"/> forces that pass, so <c>_Ready</c> is still a valid
    /// place to run this on every entry point.
    ///
    /// <para>
    /// It took a <c>strict</c> parameter and a <c>--strict-hud-fit</c> flag until
    /// Issue #49: they used to switch off a recorded line deficit, PR #45 removed
    /// the deficit, and both spent three Issues doing nothing at all. What the
    /// flag was for — proving this check is able to fail — is now the negative
    /// run in the <c>godot</c> stage of <c>verify.ps1</c>, which requires exit
    /// code 1 at logical width 1024.
    /// </para>
    /// </summary>
    private void AssertLabelsFit()
    {
        var live = GetViewportRect().Size;
        var failures = new List<string>();
        var terminal = TerminalSummaries();
        var stories = CreatureStoryPanels();
        foreach (var frame in HudFitFrames())
        {
            LayoutHud(frame.Viewport, frame.UiScale);
            foreach (var (name, label) in HudLabels())
            {
                if (label is null)
                {
                    continue;
                }

                var needed = label.GetLineCount();
                var shown = label.GetVisibleLineCount();
                if (needed > shown)
                {
                    failures.Add(
                        $"'{name}' needs {needed} lines but only {shown} fit in " +
                        $"{FormatVector(label.Size)} at viewport {FormatVector(frame.Viewport)}, " +
                        $"UI scale {FormatNumber(frame.UiScale)}");
                }
            }

            foreach (var (outcome, text) in terminal)
            {
                var (needed, shown) = MeasureSummary(text, frame);
                if (needed > shown)
                {
                    failures.Add(
                        $"summary at the end of a party ('{outcome}') needs {needed} " +
                        $"lines but only {shown} fit at viewport {FormatVector(frame.Viewport)}, " +
                        $"UI scale {FormatNumber(frame.UiScale)}");
                }
            }

            // The event panel as a selected creature's story (Issue #128). It is
            // taller than the domain feed the label normally carries, so the
            // shape a player reads is measured rather than assumed.
            foreach (var (who, text) in stories)
            {
                var (needed, shown) = MeasureFeedback(text, frame);
                if (needed > shown)
                {
                    failures.Add(
                        $"the story of '{who}' needs {needed} lines but only {shown} fit at " +
                        $"viewport {FormatVector(frame.Viewport)}, UI scale " +
                        $"{FormatNumber(frame.UiScale)}");
                }
            }

            // The band a player answers the moment of truth on (Issue #331), at
            // its widest rather than at whatever this run happens to show.
            failures.AddRange(MeasureMomentOfTruthBand(frame));

            // Put the panel back before the next frame measures the live labels,
            // or the loop above reports the candidate left in the label rather
            // than what the HUD actually says. Measured: without this the
            // 'feedback' label failed alongside every story it had been lent.
            RestoreFeedback();
        }

        RestoreSummary();
        RestoreFeedback();
        LayoutHud(live, _uiScale);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"The HUD loses text in {failures.Count} place(s): " +
                string.Join("; ", failures) +
                ". Text that does not fit its rectangle is dropped or drawn over " +
                "the panel below it.");
        }
    }

    /// <summary>
    /// Every font the HUD draws text with, read off the live subtree.
    ///
    /// <para>
    /// The whole HUD tree is walked rather than a list of the nodes this file
    /// happens to keep a reference to. The first version of this routine did the
    /// latter, and independent review walked straight through it: the inspector
    /// column's "STATE / WHY" heading is a local variable in
    /// <see cref="CreateSideColumn"/>, held by nothing, so re-authoring it at
    /// four pixels left every guard green — a check that looked passed, which is
    /// the exact defect class Issue #86 is about. A hand-maintained list can only
    /// ever be as complete as the last person to remember it; the subtree is
    /// complete by construction, so "the policy reacts to a change in the HUD"
    /// became a true sentence rather than an intention.
    /// </para>
    ///
    /// <para>
    /// Names are borrowed from the fields the overflow guard already names, so a
    /// failure still says <c>legend[3]</c>; anything the walk finds that no field
    /// holds is named by its path under the HUD root, which is exactly the case
    /// the walk exists for.
    /// </para>
    /// </summary>
    private IReadOnlyList<HudTextSize> HudTextSizes()
    {
        var named = new Dictionary<Control, string>();
        foreach (var (name, label) in HudLabels())
        {
            if (label is not null)
            {
                named[label] = name;
            }
        }

        foreach (var button in _controlButtons)
        {
            named[button] = $"control[{button.Name}]";
        }

        for (var index = 0; index < _hotkeyBadges.Count; index++)
        {
            named[_hotkeyBadges[index]] = $"hotkey[{index}]";
        }

        var sizes = new List<HudTextSize>();
        CollectHudTextSizes(_hudRoot!, named, sizes);
        return sizes;
    }

    /// <summary>
    /// Depth-first over the HUD subtree, collecting the Controls that draw text.
    /// <c>Label</c> and <c>Button</c> and nothing else on purpose: a
    /// <c>PanelContainer</c> or an <c>HSeparator</c> has no <c>font_size</c> to
    /// ask for, and asking anyway would report a theme default as if the HUD had
    /// authored it.
    /// </summary>
    private void CollectHudTextSizes(
        Node node,
        IReadOnlyDictionary<Control, string> named,
        List<HudTextSize> sizes)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Label or Button && child is Control text)
            {
                sizes.Add(new HudTextSize(
                    named.TryGetValue(text, out var name) ? name : DescribeHudTextNode(text),
                    text.GetThemeFontSize("font_size")));
            }

            CollectHudTextSizes(child, named, sizes);
        }
    }

    /// <summary>
    /// A name for a text node no field holds. Its own name when the scene gave
    /// it one, and the whole path under the HUD root when it did not, because
    /// <c>@Label@25</c> on its own would name nothing a reader could find.
    /// </summary>
    private string DescribeHudTextNode(Control text)
    {
        var name = text.Name.ToString();
        return name.StartsWith('@')
            ? $"{text.GetType().Name}[{_hudRoot!.GetPathTo(text)}]"
            : $"{text.GetType().Name}[{name}]";
    }

    /// <summary>
    /// The readability guard: measure here, decide in
    /// <see cref="HudReadability"/>. It is held against the supported frame
    /// matrix and the scale the automatic policy would choose for each frame,
    /// not against this run's own pair, because an explicit <c>--ui-scale</c> is
    /// an override a capture declares on purpose — including the deliberately
    /// small ones <c>verify.ps1</c> uses to prove the frame does not reach
    /// canonical state.
    /// </summary>
    private void AssertHudTextReadable() => HudReadability.AssertReadable(HudTextSizes());

    /// <summary>
    /// What the readability guard measured, published so a run states the
    /// physical size of its smallest text instead of a reader inferring it from
    /// a screenshot. This is the number Issue #86 was opened about: 8 px on a
    /// 3044x1722 client area.
    /// </summary>
    private object? HudReadabilityFit()
    {
        if (_hudRoot is null)
        {
            return null;
        }

        var viewport = GetViewportRect().Size;
        var frame = new ViewSize(viewport.X, viewport.Y);
        var texts = HudTextSizes();
        var violations = HudReadability.Violations(frame, _uiScale, texts);
        return new
        {
            minimumPhysicalTextPixels = HudReadability.MinimumPhysicalTextPixels,
            maximumLogicalDensity = HudReadability.MaximumLogicalDensity,
            uiScale = _uiScale,
            logicalDensity = HudReadability.LogicalDensity(frame, _uiScale),
            smallestPhysicalTextPixels =
                HudReadability.SmallestPhysicalTextPixels(texts, _uiScale),
            // The verdict on this run's own pair, which the guard deliberately
            // does not act on: an explicit --ui-scale is an override, and a
            // window bigger than the largest supported scale must not be refused
            // a launch. Deliberately not acting on it is not a reason to make a
            // reader work the density out for themselves — a run that opens the
            // pair Issue #86 was reported on now says so in its own output
            // instead of exiting 0 and looking fine.
            readable = violations.Count == 0,
            violations,
            texts = texts
                .Select(entry => (object)new
                {
                    name = entry.Name,
                    logicalPixels = entry.LogicalPixels,
                    physicalPixels =
                        HudReadability.PhysicalTextPixels(entry.LogicalPixels, _uiScale),
                })
                .ToArray(),
            // The same measurement on every supported frame, at the scale the
            // automatic policy chooses there. A run on a laptop therefore still
            // reports what the owner's maximized window would get.
            checkedFrames = HudReadability.SupportedFrames
                .Select(supported =>
                {
                    var automatic = CameraView.AutomaticUiScale(supported);
                    return (object)new
                    {
                        frame = new[] { supported.Width, supported.Height },
                        uiScale = automatic,
                        logicalDensity = HudReadability.LogicalDensity(supported, automatic),
                        smallestPhysicalTextPixels =
                            HudReadability.SmallestPhysicalTextPixels(texts, automatic),
                    };
                })
                .ToArray(),
        };
    }

    /// <summary>
    /// The measurements <see cref="AssertLabelsFit"/> acts on, published so a run
    /// states what the guard had to work with instead of the guard being trusted.
    /// The viewport is reported next to them, because every height below is a
    /// share of it rather than a constant.
    /// </summary>
    private object LabelFit()
    {
        var viewport = GetViewportRect().Size;
        return new
        {
            viewport = new[] { viewport.X, viewport.Y },
            uiScale = _uiScale,
            checkedViewports = HudFitFrames()
                .Select(frame => new[] { frame.Viewport.X, frame.Viewport.Y })
                .Distinct()
                .ToArray(),
            checkedFrames = HudFitFrames()
                .Select(frame => (object)new
                {
                    viewport = new[] { frame.Viewport.X, frame.Viewport.Y },
                    logicalViewport = new[]
                    {
                        frame.LogicalViewport.X,
                        frame.LogicalViewport.Y,
                    },
                    uiScale = frame.UiScale,
                })
                .ToArray(),
            labels = HudLabels()
                .Where(entry => entry.Label is not null)
                .Select(entry => (object)new
                {
                    name = entry.Name,
                    neededLines = entry.Label!.GetLineCount(),
                    visibleLines = entry.Label.GetVisibleLineCount(),
                    hardLines = (entry.Label.Text ?? string.Empty).Split('\n').Length,
                    width = entry.Label.Size.X,
                    height = entry.Label.Size.Y,
                })
                .ToArray(),
            // The frame the owner reads their result in, stated rather than
            // trusted: what each end of a party needs and what fits, at the
            // narrowest frame the guard checks.
            terminalSummaries = TerminalSummaryFit(),
        };
    }

    private object[] TerminalSummaryFit()
    {
        var live = GetViewportRect().Size;
        var narrowest = HudFitFrames()
            .OrderBy(frame => frame.Viewport.X / frame.UiScale)
            .First();
        var measured = TerminalSummaries()
            .Select(end =>
            {
                var (needed, shown) = MeasureSummary(end.Text, narrowest);
                return (object)new
                {
                    outcome = end.Outcome,
                    viewport = new[] { narrowest.Viewport.X, narrowest.Viewport.Y },
                    uiScale = narrowest.UiScale,
                    neededLines = needed,
                    visibleLines = shown,
                    text = end.Text,
                };
            })
            .ToArray();
        RestoreSummary();
        LayoutHud(live, _uiScale);
        return measured;
    }
}
