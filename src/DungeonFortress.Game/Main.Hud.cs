using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

// The HUD's widgets: map column, control strips, buttons and their
// icons, the side column and the legend.
public partial class Main
{
    /// <summary>
    /// The left column: header, controls, an explicit world viewport and roster.
    /// Its width is negotiated with the side panel and never derived from the
    /// map's pixel width. A larger frame therefore grows this viewport and shows
    /// more world at the same camera zoom.
    /// </summary>
    private Control CreateMapColumn()
    {
        var column = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 2.2f,
            CustomMinimumSize = new Vector2(HudMapColumnMinimumWidth, 0),
        };
        column.AddThemeConstantOverride("separation", 0);

        var header = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.Fill,
            // Ends exactly where the time toolbar starts. The old summary label
            // ran 45px from y=42 and drew its second line over the buttons.
            CustomMinimumSize = new Vector2(0, ToolbarStripTop - HudTopMargin),
        };
        header.AddThemeConstantOverride("separation", 2);
        column.AddChild(header);

        _title = MakeHudLabel(15, new Color("#dbeafe"));
        _title.AutowrapMode = TextServer.AutowrapMode.Off;
        _title.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _title.Text = "DUNGEON FORTRESS  //  PROTOTYPE 1 GRAYBOX";
        header.AddChild(_title);

        _summary = MakeHudLabel(12, new Color("#bfdbfe"));
        header.AddChild(_summary);
        // The summary is always exactly two lines, and it is the one panel that
        // must never be squeezed: the line under it is the time toolbar.
        _summary.CustomMinimumSize = new Vector2(0, HudTextHeight(_summary, 2));

        column.AddChild(CreateControlStrips());
        _worldViewport = new Control
        {
            Name = "WorldViewport",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 3f,
        };
        column.AddChild(_worldViewport);

        _roster = MakeHudLabel(10, new Color("#cbd5e1"));
        _roster.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _roster.SizeFlagsStretchRatio = 1f;
        column.AddChild(_roster);
        return column;
    }

    // ---------------------------------------------------------------------
    // The two control strips
    //
    // They used to be DrawRect plus DrawString in _Draw(), with a parallel table
    // of rectangles that TryHandleToolbarClick hit-tested by hand. Two
    // descriptions of the same button, and the only thing keeping them in step
    // was that one function produced both. That is a defect by construction, and
    // deleting it is worth more than decorating it with icons.
    //
    // Every button is now a Button with an icon, a hotkey badge and a tooltip. It
    // owns its own click, so the map cannot receive one that landed on a button,
    // and it owns its own rectangle, so nothing has to agree with it.
    //
    // What each button says is decided in DungeonFortress.Presentation, which
    // does not reference Godot and is covered by unit tests running in CI. This
    // side does layout and dispatch and nothing else.
    // ---------------------------------------------------------------------
    private Control CreateControlStrips()
    {
        var band = new VBoxContainer
        {
            Name = "ControlStrips",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0, ControlStripsBandHeight),
        };
        band.AddThemeConstantOverride("separation", ControlStripSeparation);

        var time = CreateStrip(band, "TimeStrip", "#0f1d2d");
        var brush = CreateStrip(band, "BrushStrip", "#102338");
        _timeStrip = (Control)time.GetParent();
        _brushStrip = (Control)brush.GetParent();

        var controls = UiControls.Build(CurrentControlsView());
        for (var index = 0; index < controls.Count; index++)
        {
            var control = controls[index];
            var button = CreateControlButton(control, index);
            (control.Strip == UiControlStrip.Time ? time : brush).AddChild(button);
            _controlButtons.Add(button);
        }

        // A permanent, invisible sample of the tooltip HudButton draws for real,
        // so the HUD readability guard's subtree walk (Main.HudTextSizes,
        // Main.CollectHudTextSizes) reaches a text surface Godot otherwise
        // creates only on hover and never as a child of _hudRoot. See
        // HudButton.MakeAuthoredTooltip for why this is built at UI scale 1
        // rather than the live one. Issue #127: the tooltip was the one HUD
        // text surface no guard measured.
        //
        // Failing loudly rather than silently skipping this block when
        // UiControls.Build ever returns zero controls is deliberate: review on
        // Issue #127 found that the earlier `if (_controlButtons.Count > 0)`
        // guard had exactly the failure shape of the inert --strict-hud-fit
        // flag (Issue #49) — the tooltip would drop out of the guard with every
        // check still green, and InjectHudTooltipReadabilityRegression would
        // crash on a bare null-reference instead of naming what broke.
        if (_controlButtons.Count == 0)
        {
            throw new InvalidOperationException(
                "The HUD readability guard needs at least one control button to build a " +
                "tooltip readability sample from, and UiControls.Build returned none.");
        }

        _tooltipReadabilitySample = _controlButtons[0].MakeAuthoredTooltip(
            TooltipReadabilitySampleText);
        _tooltipReadabilitySample.Visible = false;
        _hudRoot!.AddChild(_tooltipReadabilitySample);

        RefreshControls();
        return band;
    }

    /// <summary>
    /// Fixed content for <see cref="_tooltipReadabilitySample"/> rather than a
    /// real button's live tooltip text: the guard measures font size, not
    /// wording, and a synthetic two-line string guarantees the sample always
    /// has both a title and a body to measure regardless of what any button's
    /// tooltip happens to say.
    /// </summary>
    private const string TooltipReadabilitySampleText = "Sample\nSample tooltip body";

    /// <summary>
    /// One strip: a panel that hugs its buttons. It shrinks to its content on
    /// purpose — a strip stretched to the column would make "does the brush strip
    /// fit inside the map?" unmeasurable, and that question is the whole point of
    /// moving eight labelled rectangles to eight icons.
    /// </summary>
    private static HBoxContainer CreateStrip(Control parent, string name, string background)
    {
        var panel = new PanelContainer
        {
            Name = name,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.Fill,
        };
        var style = new StyleBoxFlat { BgColor = new Color(background) };
        style.SetContentMarginAll(ControlStripPadding);
        panel.AddThemeStyleboxOverride("panel", style);
        parent.AddChild(panel);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", ControlButtonSeparation);
        panel.AddChild(row);
        return row;
    }

    /// <summary>
    /// A toolbar button: the icon it is, the hotkey badge in the corner and the
    /// tooltip that names it. All three together, because a row of unlabelled
    /// symbols would be <em>less</em> friendly than the text it replaces — which
    /// is why RimWorld and Prison Architect ship all three too.
    /// </summary>
    private HudButton CreateControlButton(UiControl control, int index)
    {
        var accent = control.Strip == UiControlStrip.Time ? "#1d4ed8" : "#b45309";
        // HudButton rather than Button: the default theme draws a tooltip at font
        // size 16 with no width limit, which made it larger than the title of the
        // game and wide enough to cover the map. See HudButton.
        var button = new HudButton
        {
            Name = control.Id,
            ToggleMode = true,
            FocusMode = Control.FocusModeEnum.None,
            ClipText = false,
            TooltipText = control.Tooltip,
            CustomMinimumSize = new Vector2(ControlButtonSize, ControlButtonSize),
        };
        button.AddThemeFontSizeOverride("font_size", HudFontSizes.ButtonLabelFontSize);
        button.AddThemeColorOverride("font_color", new Color("#dbeafe"));
        button.AddThemeColorOverride("font_pressed_color", new Color("#fef3c7"));
        button.AddThemeColorOverride("font_hover_color", new Color("#f8fafc"));
        button.AddThemeStyleboxOverride("normal", ControlButtonStyle("#1b2f45", "#24364b"));
        button.AddThemeStyleboxOverride("hover", ControlButtonStyle("#25415f", "#3b82f6"));
        button.AddThemeStyleboxOverride("pressed", ControlButtonStyle(accent, "#f8fafc"));
        button.AddThemeStyleboxOverride("disabled", ControlButtonStyle("#151f2b", "#1f2c3a"));
        button.Pressed += () => HandleControlPressed(index);

        // Full-rect anchors rather than a corner offset: the badge then sits in the
        // corner of whatever size the layout gives the button, instead of the size
        // it was authored against.
        var badge = new Label
        {
            Text = control.Hotkey,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        badge.AddThemeFontSizeOverride("font_size", 8);
        badge.AddThemeColorOverride("font_color", new Color("#e0f2fe"));
        // The badge sits on top of the icon, so it needs to be legible against
        // whatever the icon happens to put in that corner rather than against the
        // button background.
        badge.AddThemeColorOverride("font_outline_color", new Color("#0b1622"));
        badge.AddThemeConstantOverride("outline_size", 3);
        button.AddChild(badge);
        badge.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        badge.OffsetRight = -2;
        // Kept, because the badge carries the smallest font in the toolbar and
        // the readability policy measures what is smallest rather than what is
        // easy to reach.
        _hotkeyBadges.Add(badge);
        return button;
    }

    private static StyleBoxFlat ControlButtonStyle(string fill, string border)
    {
        var style = new StyleBoxFlat { BgColor = new Color(fill), BorderColor = new Color(border) };
        style.SetBorderWidthAll(1);
        style.SetContentMarginAll(2);
        style.SetCornerRadiusAll(2);
        return style;
    }

    /// <summary>
    /// Puts the current state on the buttons. Nothing about which buttons exist or
    /// what they say is decided here: this walks the list
    /// <see cref="UiControls.Build"/> returns, in the order it returns it, so the
    /// toolbar a player sees and the <c>ui.controls</c> an automated check reads
    /// are the same list.
    /// </summary>
    private void RefreshControls()
    {
        if (_controlButtons.Count == 0)
        {
            return;
        }

        var controls = UiControls.Build(CurrentControlsView());
        // Buttons are matched to controls by position, and a press dispatches on
        // the id at that position, so a list whose length depends on the state
        // would silently wire buttons to the wrong actions. It does not; this is
        // what says so out loud if it ever starts to.
        if (controls.Count != _controlButtons.Count)
        {
            throw new InvalidOperationException(
                $"The toolbar has {_controlButtons.Count} buttons but UiControls.Build " +
                $"returned {controls.Count} controls.");
        }

        for (var index = 0; index < _controlButtons.Count; index++)
        {
            var control = controls[index];
            var button = _controlButtons[index];
            button.Text = control.Label;
            button.TooltipText = control.Tooltip;
            button.Icon = control.Icon is null ? null : IconTexture(control.Icon);
            button.Disabled = !control.Enabled;
            // No signal: the state on the button is a projection of the game, not
            // an input to it, exactly like every other thing this adapter draws.
            button.SetPressedNoSignal(control.Active);
        }
    }

    /// <summary>
    /// The adapter state the toolbar is allowed to depend on. It is readable
    /// before a fixture exists, because <c>_Ready</c> builds the HUD first and
    /// loads the world after.
    /// </summary>
    private UiControlsViewState CurrentControlsView() => new(
        _editMode,
        _brushZone,
        _selectedJob,
        _state?.Priorities[_selectedJob] ?? 0,
        UiControls.RuleIds[_selectedRule],
        _state?.Rules[UiControls.RuleIds[_selectedRule]] ?? 0,
        _paused,
        _speed,
        _fixture,
        _state is not null && _state.Tick >= PrototypeTuning.SessionTicks);

    /// <summary>
    /// What a press does. Dispatch is by control id read from the current list,
    /// not by a number baked into the handler, so the one button whose identity
    /// changes with the state — run versus pause — needs no special case.
    /// </summary>
    private void HandleControlPressed(int index)
    {
        var controls = UiControls.Build(CurrentControlsView());
        if (index < 0 || index >= controls.Count)
        {
            return;
        }

        switch (controls[index].Id)
        {
            case UiControlIds.Run:
            case UiControlIds.Pause:
                TogglePause();
                break;
            case UiControlIds.Step:
                Advance(1);
                break;
            case UiControlIds.Speed0_5:
                SetSpeed(0.5);
                break;
            case UiControlIds.Speed1:
                SetSpeed(1.0);
                break;
            case UiControlIds.Speed4:
                SetSpeed(4.0);
                break;
            case UiControlIds.Speed16:
                SetSpeed(16.0);
                break;
            case UiControlIds.FixtureBaseline:
                LoadFixture("baseline", 1);
                break;
            case UiControlIds.FixtureNeglected:
                LoadFixture("neglected", 1);
                break;
            case UiControlIds.Replay:
                ReplayCurrentLog();
                break;
            case UiControlIds.Inspect:
                SelectEditMode(BrushMode.Inspect);
                break;
            case UiControlIds.Paint:
                SelectEditMode(BrushMode.Paint);
                break;
            case UiControlIds.Erase:
                SelectEditMode(BrushMode.Erase);
                break;
            case UiControlIds.Dig:
                SelectEditMode(BrushMode.Dig);
                break;
            case UiControlIds.DigCancel:
                SelectEditMode(BrushMode.CancelDig);
                break;
            case UiControlIds.Stockpile:
                SelectStockpileBrush();
                break;
            case UiControlIds.Build:
                SelectEditMode(BrushMode.Build);
                break;
            case UiControlIds.BuildCancel:
                SelectEditMode(BrushMode.CancelBuild);
                break;
            case UiControlIds.Zone:
                CycleZone();
                break;
            case UiControlIds.Priority:
                CycleJob();
                break;
            case UiControlIds.Rule:
                CycleRule();
                break;
        }
    }

    /// <summary>
    /// The icon pack of Issue #54, loaded by name from
    /// <see cref="UiIconManifest"/>. A file that has not been generated yet gets a
    /// placeholder, so the toolbar is complete and clickable before the art
    /// exists and dropping the real PNG in requires no code change.
    ///
    /// Every icon is resampled to its drawn size here rather than being scaled by
    /// the button. That is the lesson of the goblin pack: 96x96 art squeezed into
    /// a 20x20 rectangle by the renderer turned into mush, and nothing measured
    /// it. At the manifest's 48x48 this is an exact 2x downscale.
    /// </summary>
    private void LoadIcons()
    {
        foreach (var icon in UiIconManifest.Toolbar)
        {
            var path = UiIconManifest.ResourcePath(icon.FileName);
            if (!ResourceLoader.Exists(path) || GD.Load<Texture2D>(path) is not { } texture)
            {
                _missingIcons.Add(icon.FileName);
                continue;
            }

            _icons[icon.FileName] = Resample(texture);
        }
    }

    private static Texture2D Resample(Texture2D texture)
    {
        var image = texture.GetImage();
        if (image is null)
        {
            return texture;
        }

        if (image.GetWidth() != IconDrawSize || image.GetHeight() != IconDrawSize)
        {
            image.Resize(IconDrawSize, IconDrawSize, Image.Interpolation.Bilinear);
        }

        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// The texture a button draws: the generated icon, or the shared placeholder
    /// while the pack is still being produced. A placeholder button keeps its
    /// hotkey badge and its tooltip, so it still says what it is.
    /// </summary>
    private Texture2D IconTexture(string fileName) =>
        _icons.TryGetValue(fileName, out var texture) ? texture : PlaceholderIcon();

    private Texture2D PlaceholderIcon()
    {
        if (_iconPlaceholder is not null)
        {
            return _iconPlaceholder;
        }

        var image = Image.CreateEmpty(IconDrawSize, IconDrawSize, false, Image.Format.Rgba8);
        var fill = new Color("#334155");
        var edge = new Color("#7dd3fc");
        for (var y = 0; y < IconDrawSize; y++)
        {
            for (var x = 0; x < IconDrawSize; x++)
            {
                var border = x is 2 or IconDrawSize - 3 || y is 2 or IconDrawSize - 3;
                var inside = x is >= 2 and <= IconDrawSize - 3 && y is >= 2 and <= IconDrawSize - 3;
                image.SetPixel(x, y, inside ? (border ? edge : fill) : new Color(0, 0, 0, 0));
            }
        }

        _iconPlaceholder = ImageTexture.CreateFromImage(image);
        return _iconPlaceholder;
    }

    /// <summary>
    /// The measurable claim of this step: the brush strip is narrower than the
    /// explicit world viewport it controls. The viewport is layout, not map pixel
    /// width, so changing tile size cannot silently push the buttons off-screen.
    ///
    /// Measured at every frame size the HUD guard uses, and for the same reason: a
    /// check that only ever saw one size cannot tell a layout that fits from one
    /// that happens to.
    /// </summary>
    private void AssertControlStripsFit()
    {
        var live = GetViewportRect().Size;
        var failures = new List<string>();
        foreach (var frame in HudFitFrames())
        {
            LayoutHud(frame.Viewport, frame.UiScale);
            var worldWidth = _worldViewport!.Size.X;
            var allowedWidth = Math.Min(worldWidth, MapPixelSize.X);
            foreach (var (name, strip) in ControlStrips())
            {
                if (strip is null || strip.Size.X <= allowedWidth)
                {
                    continue;
                }

                failures.Add(
                    $"'{name}' is {FormatNumber(strip.Size.X)}px wide at viewport " +
                    $"{FormatVector(frame.Viewport)} and UI scale {FormatNumber(frame.UiScale)}, " +
                    $"wider than the {FormatNumber(allowedWidth)}px usable world width");
            }
        }

        LayoutHud(live, _uiScale);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"A control strip is wider than the world viewport in {failures.Count} place(s): " +
                string.Join("; ", failures) +
                ". A strip that overhangs the map puts controls where the player is " +
                "not looking and breaks the column the HUD is laid out in.");
        }
    }

    private (string Name, Control? Strip)[] ControlStrips() =>
    [
        ("time", _timeStrip),
        ("brush", _brushStrip),
    ];

    /// <summary>
    /// The strip measurements published next to <see cref="LabelFit"/>, so a run
    /// states the widths instead of the guard being trusted, and the icons that
    /// are still placeholders are named rather than merely absent from a picture.
    /// </summary>
    private object? ControlStripFit()
    {
        if (_worldViewport is null)
        {
            return null;
        }

        var usableWidth = Math.Min(_worldViewport.Size.X, MapPixelSize.X);
        return new
        {
            mapWidth = MapPixelSize.X,
            worldViewportWidth = _worldViewport.Size.X,
            usableWorldWidth = usableWidth,
            widths = ControlStrips()
                .Where(entry => entry.Strip is not null)
                .Select(entry => (object)new { name = entry.Name, width = entry.Strip!.Size.X })
                .ToArray(),
            iconDrawSize = IconDrawSize,
            loadedIcons = _icons.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            placeholderIcons = _missingIcons,
        };
    }

    /// <summary>
    /// The side panel: heading, inspector, legend, event feedback. The legend is
    /// static text and takes exactly the height it needs; the two panels that
    /// grow with the session share what is left in a 3:2 ratio, so the column
    /// cannot become over-subscribed the way the authored 456px one was.
    /// </summary>
    private Control CreateSideColumn()
    {
        var panel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1f,
            CustomMinimumSize = new Vector2(HudSidePanelMinimumWidth, 0),
        };
        var background = new StyleBoxFlat { BgColor = new Color("#0f1d2d"), BorderColor = new Color("#334155") };
        background.SetBorderWidthAll(1);
        background.SetContentMarginAll(10);
        panel.AddThemeStyleboxOverride("panel", background);

        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", HudPanelSeparation);
        panel.AddChild(column);

        var heading = MakeHudLabel(13, new Color("#93c5fd"));
        // Named because nothing holds a reference to it: the readability walk
        // finds it in the tree and reports it by path, and a path with a default
        // engine name in it is not a sentence anyone can act on.
        heading.Name = "InspectorHeading";
        heading.AutowrapMode = TextServer.AutowrapMode.Off;
        heading.Text = "STATE / WHY";
        column.AddChild(heading);
        heading.CustomMinimumSize = new Vector2(0, HudTextHeight(heading, 1));

        _inspector = MakeHudLabel(11, new Color("#e2e8f0"));
        _inspector.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _inspector.SizeFlagsStretchRatio = 3;
        column.AddChild(_inspector);

        column.AddChild(CreateLegend());
        // The rule the absolute layout drew at y=400: it is what separates the
        // static map key from the live event feed above and below it.
        column.AddChild(new HSeparator { MouseFilter = Control.MouseFilterEnum.Ignore });

        _feedback = MakeHudLabel(12, new Color("#94a3b8"));
        _feedback.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _feedback.SizeFlagsStretchRatio = 2;
        column.AddChild(_feedback);
        return panel;
    }

    /// <summary>
    /// The map legend. It used to be eight <c>DrawString</c> calls at fixed
    /// offsets with no width limit, so the long stockpile rows ran past the right
    /// edge of the panel and nothing measured it. One coloured Label per row
    /// keeps the colour cue, reserves the height in the column, and puts every
    /// row under the same overflow guard as the four panels.
    /// </summary>
    private Control CreateLegend()
    {
        var legend = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.Fill,
        };
        legend.AddThemeConstantOverride("separation", 0);

        foreach (var (text, size, color) in new (string Text, int Size, string Color)[]
                 {
                     // Issue #222 round 2. Growing the legend by the state-dot row
                     // below to nine rows overflows AssertLabelsFit's worst-case
                     // feed measurement: at viewport (2048, 1440), UI scale 2, a
                     // "prepared" fixture run at tick 1318
                     // (evidence/222-crowd-frame.json) fails with "the story of
                     // 'widest padded story' needs 10 lines but only 9 fit". This
                     // is not a rebase side effect — it reproduces on commit
                     // 8e41cf3, this Issue's own round-1 tip, unrebased. The column
                     // has room for eight legend rows, not nine, so the standalone
                     // "LEGEND" heading (previously its own 9pt row) is folded into
                     // the shortest content row below instead of dropped, keeping
                     // every word this legend already had at eight rows total.
                     ("LEGEND — amber X = dig mark / yellow bar = dig progress", 8, "#cbd5e1"),
                     ("teal outline = crew / red outline = raider / bar = HP / white X = downed", 8, "#cbd5e1"),
                     // Issue #222. The state dot is the small circle at the upper-
                     // right corner of each own body. It disappears behind the
                     // legend row "bar = HP" on the line above, so this row is
                     // the one that keeps the copy honest when the test walks the
                     // live subtree (see HudReadabilityTests.AuthoredHud).
                     ("dot: blue=idle amber=fighting pink=fled green=working gray=downed", 8, "#bfdbfe"),
                     // Issue #52. It replaces the quarters' rest rule rather than
                     // joining it: the panel column is under the same overflow
                     // guard as everything else, and the rest rule now sits on the
                     // room line of the inspector, where clicking the quarters
                     // puts it. What could not move anywhere is the amber ring —
                     // it is the one mark on the map with no words next to it.
                     ("room = own floor + outline + caption; amber ring = object with no room", 8, "#fcd34d"),
                     ("light warm block = diggable rock / dark = map edge", 8, "#d6d3d1"),
                     ("red X = unreachable / pale tile = new floor / gray dot = loose stone", 8, "#fca5a5"),
                     ("[M] stockpile: cornered square = material cell / grey box on a crew = carried stone", 8, "#e2e8f0"),
                     ("filled pip = stored / hollow blue pip = booked by a carrier on the way", 8, "#7dd3fc"),
                 })
        {
            var line = MakeHudLabel(size, new Color(color));
            line.Text = text;
            legend.AddChild(line);
            line.CustomMinimumSize = new Vector2(0, HudTextHeight(line, 1));
            _legendLines.Add(line);
        }

        return legend;
    }

    /// <summary>
    /// A HUD panel. Clipping is on for all of them: a Label that does not fit its
    /// rectangle either drops lines or draws over the panel below it, and only
    /// the first of the two is detectable, so the guard is given something it can
    /// actually see.
    /// </summary>
    private static Label MakeHudLabel(int fontSize, Color color)
    {
        var label = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ClipText = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>
    /// The height a label needs for a known number of lines, read from the font
    /// it will actually be drawn with rather than from a guessed constant.
    /// </summary>
    private static float HudTextHeight(Label label, int lines)
    {
        var fontSize = label.GetThemeFontSize("font_size");
        var lineHeight = label.GetThemeFont("font").GetHeight(fontSize) +
            label.GetThemeConstant("line_spacing");
        return Mathf.Ceil(lineHeight * lines);
    }
}
