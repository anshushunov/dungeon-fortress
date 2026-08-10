using System;

namespace DungeonFortress.Presentation;

/// <summary>
/// The font sizes and width the toolbar and its tooltip are authored at, and
/// the one arithmetic rule that turns an authored size into a physical one at
/// a given UI scale.
///
/// <para>
/// Issue #127: the tooltip body was authored two points smaller than the
/// button label it explains (9 against 10, two unrelated numbers picked
/// independently), and neither the tooltip's font sizes nor its width grew
/// when the rest of the interface did. The engine-side reason is on
/// <c>HudButton.UiScale</c>: the tooltip popup Godot builds is not a
/// descendant of the HUD's own scaled <c>Control</c> subtree, so it cannot
/// inherit a scale the way every other label does and has to be told the
/// scale directly.
/// </para>
///
/// <para>
/// Putting the numbers here rather than as literals in <c>HudButton.cs</c>
/// does two things a Godot-side literal cannot: the ordering between the
/// button label and its tooltip becomes one comparison instead of two numbers
/// a reader has to compare by hand, and the scaling arithmetic itself becomes
/// provable without starting the engine — <c>DungeonFortress.Game</c> needs
/// the Godot runtime and no test project may reference it (ADR 0011), so this
/// is the only layer <c>HudFontSizesTests</c> can reach.
/// </para>
/// </summary>
public static class HudFontSizes
{
    /// <summary>
    /// The toolbar button's own label, set in <c>CreateControlButton</c> in
    /// <c>Main.cs</c>.
    ///
    /// <para>
    /// Raised from 10 by Issue #352: at the design frame (UI scale 1) an
    /// authored size is also the physical one, and 10 no longer clears
    /// <see cref="HudReadability.MinimumPhysicalTextPixels"/>. <see
    /// cref="TooltipTitleFontSize"/> and <see cref="TooltipBodyFontSize"/> are
    /// derived from this constant, so raising it once is what carries the
    /// tooltip past the floor as well — the wording the owner reported unable
    /// to read on the playtest of slice 3.
    /// </para>
    /// </summary>
    public const int ButtonLabelFontSize = 12;

    /// <summary>
    /// The hotkey badge in a toolbar button's corner — the smallest font in the
    /// toolbar on purpose (see the comment next to <c>_hotkeyBadges.Add</c> in
    /// <c>Main.Hud.cs</c>): the readability guard is meant to be proven against
    /// whatever is actually smallest, not against a size that happens to be
    /// comfortable to reach.
    ///
    /// <para>
    /// Tied to <see cref="HudReadability.MinimumPhysicalTextPixels"/> rather
    /// than carrying its own literal, because the badge being <i>exactly</i> at
    /// the floor is the property that makes the guard's negative half meaningful
    /// for it — the same "own value or equal to the floor" question Issue #352
    /// asked, decided in favour of the floor so the two numbers cannot drift
    /// apart into the shape Issue #350 named (a fact stated twice with only one
    /// copy kept honest). Before #352 the badge was a bare literal, 8, equal to
    /// the floor of that time for the same reason.
    /// </para>
    /// </summary>
    public const int HotkeyBadgeFontSize = (int)HudReadability.MinimumPhysicalTextPixels;

    /// <summary>
    /// The tooltip's first line: the name of the action. Larger than the body
    /// by the same two points the pre-Issue-#127 tooltip used (11 against 9),
    /// kept as a ratio to the body rather than as an unrelated literal.
    /// </summary>
    public const int TooltipTitleFontSize = ButtonLabelFontSize + 2;

    /// <summary>
    /// The tooltip's explanation. Equal to <see cref="ButtonLabelFontSize"/>
    /// rather than smaller than it — Issue #127 Scope item 1 in full: text
    /// that explains a button must not be drawn smaller than the button it
    /// explains.
    /// </summary>
    public const int TooltipBodyFontSize = ButtonLabelFontSize;

    /// <summary>
    /// Wide enough for a sentence at <see cref="TooltipBodyFontSize"/>, narrow
    /// enough that the tooltip cannot cover the map: the map is 616 px across
    /// at the design frame, this is 240 (<c>HudButton</c>).
    /// </summary>
    public const int TooltipWidth = 240;

    /// <summary>
    /// An authored size or width, grown to the interface's current UI scale —
    /// the same rule <c>Main.LayoutHud</c> applies to the rest of the HUD by
    /// scaling <c>_hudRoot</c> itself. The tooltip cannot use that mechanism
    /// (see <c>HudButton.UiScale</c>), so <c>HudButton</c> calls this directly
    /// instead of leaving the multiplication inline, so the arithmetic is the
    /// same function this test file proves rather than a copy of it.
    /// </summary>
    public static int ScaledSize(int authoredSize, double uiScale)
    {
        if (uiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(uiScale),
                uiScale,
                "UI scale must be positive.");
        }

        return (int)Math.Round(authoredSize * uiScale, MidpointRounding.AwayFromZero);
    }
}
