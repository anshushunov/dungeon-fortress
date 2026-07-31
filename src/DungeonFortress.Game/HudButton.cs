using DungeonFortress.Presentation;

using Godot;

namespace DungeonFortress.Game;

/// <summary>
/// A toolbar button whose tooltip is built by the HUD instead of by the default
/// theme.
///
/// Godot draws a plain <c>TooltipText</c> with the theme's default font size,
/// which is 16. Every other piece of this HUD sets its size explicitly and none
/// of them goes above 15, so the default made the tooltip larger than the title
/// of the game. It also has no width limit, so a one-sentence description ran as
/// a single line across the map it was supposed to explain. Both were reported on
/// the owner playtest of PR #57.
///
/// Overriding <see cref="_MakeCustomTooltip"/> is the only way to control either:
/// the tooltip is a separate control created by the engine, so a theme override
/// on the button itself never reaches it.
///
/// <para>
/// Issue #127 found the fix for PR #57 had overshot in a different way: the
/// tooltip body ended up smaller than the button label it explains (9px against
/// 10px), and neither its font sizes nor <see cref="DungeonFortress.Presentation.HudFontSizes.TooltipWidth"/>
/// grew when the rest of the interface did. The sizes now live in
/// <see cref="DungeonFortress.Presentation.HudFontSizes"/>, next to the button
/// label they must not be smaller than; see <see cref="UiScale"/> for why the
/// scaling has to happen here rather than by inheriting it from the HUD.
/// </para>
/// </summary>
public partial class HudButton : Button
{
    /// <summary>
    /// The interface scale this button's tooltip is drawn at, kept in step with
    /// <c>Main.LayoutHud</c>'s <c>_uiScale</c> field.
    ///
    /// <para>
    /// Every other HUD label grows automatically: <c>Main.LayoutHud</c> sets
    /// <c>_hudRoot.Scale</c> once and every descendant <c>Control</c> renders
    /// through that transform. The tooltip cannot use that mechanism, because
    /// it is not a descendant of <c>_hudRoot</c> at all — confirmed by reading
    /// Godot 4.7's own source rather than assuming it: <c>Viewport::_gui_show_tooltip_at</c>
    /// (<c>scene/main/viewport.cpp</c>) takes the Control
    /// <see cref="_MakeCustomTooltip"/> returns, wraps it in a <c>PopupPanel</c>
    /// — itself a <c>Window</c> — and adds that panel as a child of the hovered
    /// button (<c>tooltip_owner-&gt;add_child(gui.tooltip_popup)</c>), never of
    /// <c>_hudRoot</c>. A <c>Window</c> scales its own content by
    /// <c>content_scale_factor</c>, set from <c>get_popup_base_transform()</c>
    /// — and with this project's <c>window/stretch/aspect="expand"</c>
    /// (<c>project.godot</c>), that transform's scale stays 1 regardless of
    /// window size, because "expand" exposes more of the design frame instead
    /// of stretching it. So the tooltip has no ambient scale to inherit from
    /// either mechanism, and has to carry <see cref="UiScale"/> itself or stay
    /// the same physical size while the rest of the interface grows around it —
    /// exactly what Issue #127 observed on the owner's maximized window.
    /// </para>
    /// </summary>
    public double UiScale { get; set; } = 1.0;

    public override Control _MakeCustomTooltip(string forText) => BuildTooltip(forText, UiScale);

    /// <summary>
    /// The tooltip at UI scale 1, for the HUD readability guard rather than for
    /// display. <c>Main.CreateControlStrips</c> keeps the result of one call to
    /// this, invisible, as a permanent child of <c>_hudRoot</c>, so the same
    /// subtree walk that reaches every other label (<c>Main.CollectHudTextSizes</c>)
    /// reaches this one too — the walk needs nothing added for it, because the
    /// sample lives inside the tree it already walks.
    ///
    /// <para>
    /// Built at a fixed scale rather than at the button's own
    /// <see cref="UiScale"/>, because <c>HudReadability.Violations</c> applies
    /// each supported frame's own scale to the logical size it is given; handing
    /// it an already-scaled number would scale the tooltip twice.
    /// </para>
    /// </summary>
    public Control MakeAuthoredTooltip(string forText) => BuildTooltip(forText, 1.0);

    private Control BuildTooltip(string forText, double scale)
    {
        // The first line names the action and carries its hotkey; the rest
        // explains it. UiControls builds the string in that shape, so the split
        // is on the same seam rather than on a guess about the wording.
        var parts = (forText ?? string.Empty).Split('\n', 2);
        var title = parts[0];
        var body = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color("#0f1d2d"),
            BorderColor = new Color("#3b82f6"),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
        });

        var width = HudFontSizes.ScaledSize(HudFontSizes.TooltipWidth, scale);
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(width, 0) };
        column.AddThemeConstantOverride("separation", 2);
        column.AddChild(MakeTooltipLine(
            "TooltipTitle",
            title,
            HudFontSizes.ScaledSize(HudFontSizes.TooltipTitleFontSize, scale),
            "#dbeafe",
            width));
        if (body.Length > 0)
        {
            column.AddChild(MakeTooltipLine(
                "TooltipBody",
                body,
                HudFontSizes.ScaledSize(HudFontSizes.TooltipBodyFontSize, scale),
                "#94a3b8",
                width));
        }

        panel.AddChild(column);
        return panel;
    }

    /// <summary>
    /// One line of the tooltip. Named explicitly (<paramref name="name"/>)
    /// rather than left for Godot to auto-name, so the readability guard's
    /// fallback naming (<c>Main.DescribeHudTextNode</c>) reports
    /// <c>Label[TooltipTitle]</c> / <c>Label[TooltipBody]</c> instead of an
    /// opaque generated id — the same reason every other unnamed HUD text node
    /// gets a name there.
    /// </summary>
    private static Label MakeTooltipLine(string name, string text, int fontSize, string color, int width)
    {
        var label = new Label
        {
            Name = name,
            Text = text,
            // Without wrapping the description keeps its natural width and the
            // width limit above does nothing.
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(width, 0),
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", new Color(color));
        return label;
    }
}
