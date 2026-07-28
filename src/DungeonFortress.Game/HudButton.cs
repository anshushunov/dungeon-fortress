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
/// </summary>
public partial class HudButton : Button
{
    /// <summary>
    /// Wide enough for a sentence at this font size, narrow enough that the
    /// tooltip cannot cover the map: the map is 616 px across, this is 240.
    /// </summary>
    private const int TooltipWidth = 240;

    public override Control _MakeCustomTooltip(string forText)
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

        var column = new VBoxContainer { CustomMinimumSize = new Vector2(TooltipWidth, 0) };
        column.AddThemeConstantOverride("separation", 2);
        column.AddChild(MakeTooltipLine(title, 11, "#dbeafe"));
        if (body.Length > 0)
        {
            column.AddChild(MakeTooltipLine(body, 9, "#94a3b8"));
        }

        panel.AddChild(column);
        return panel;
    }

    private static Label MakeTooltipLine(string text, int fontSize, string color)
    {
        var label = new Label
        {
            Text = text,
            // Without wrapping the description keeps its natural width and the
            // width limit above does nothing.
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(TooltipWidth, 0),
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", new Color(color));
        return label;
    }
}
