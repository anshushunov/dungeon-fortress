using System;

namespace DungeonFortress.Presentation;

/// <summary>
/// Every font size the HUD is authored at, and the one arithmetic rule that
/// turns an authored size into a physical one at a given UI scale.
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
/// Issue #352 widened this file from the toolbar and its tooltip to every
/// text surface <c>Main.Hud.cs</c> draws. Before it, seven of the eleven were
/// bare literals passed straight to <c>MakeHudLabel</c> or a legend tuple —
/// the same "a fact stated twice, only one copy checked" shape Issue #350
/// named, except the unchecked copy here was a number nobody had reason to
/// look at until the owner reported the tooltip unreadable on the playtest of
/// slice 3 (2026-08-09). <c>HudReadabilityTests.AuthoredHud</c> is the fixture
/// that is supposed to keep every one of them honest; it could not, for the
/// four now named here (<see cref="RosterFontSize"/>,
/// <see cref="InspectorFontSize"/> and the eight <see cref="LegendFontSize"/>
/// rows), because they were never routed through a shared, named place a test
/// could point at.
/// </para>
///
/// <para>
/// Putting the numbers here rather than as literals in <c>Main.Hud.cs</c> or
/// <c>HudButton.cs</c> does two things a Godot-side literal cannot: an
/// ordering between two sizes becomes one comparison instead of two numbers a
/// reader has to compare by hand, and the arithmetic itself becomes provable
/// without starting the engine — <c>DungeonFortress.Game</c> needs the Godot
/// runtime and no test project may reference it (ADR 0011), so this is the
/// only layer <c>HudFontSizesTests</c> and <c>HudReadabilityTests</c> can
/// reach.
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
    /// The HUD's own title, set in <c>CreateSideColumn</c>. Already above
    /// <see cref="HudReadability.MinimumPhysicalTextPixels"/> before Issue
    /// #352 raised the floor, and unchanged by it — named here only so it
    /// stops being a bare literal, the same reason <see cref="SummaryFontSize"/>,
    /// <see cref="InspectorHeadingFontSize"/> and <see cref="FeedbackFontSize"/>
    /// are named rather than left as the three other literals that already
    /// cleared the floor.
    /// </summary>
    public const int TitleFontSize = 15;

    /// <summary>
    /// The summary line: session identity, tick, renown and the wave/resource
    /// line beneath it. Already at the floor before Issue #352; named for the
    /// same reason as <see cref="TitleFontSize"/>.
    /// </summary>
    public const int SummaryFontSize = 12;

    /// <summary>
    /// The crew roster line, control feedback and the tail of the command log
    /// (<c>HudText.Roster</c>).
    ///
    /// <para>
    /// Raised from 10 by Issue #352, for the same reason
    /// <see cref="ButtonLabelFontSize"/> was: at the design frame an authored
    /// size is the physical one, and 10 no longer clears
    /// <see cref="HudReadability.MinimumPhysicalTextPixels"/>.
    /// </para>
    /// </summary>
    public const int RosterFontSize = 12;

    /// <summary>
    /// The side-column heading above the inspector text (<c>CreateSideColumn</c>).
    /// Already at the floor before Issue #352; named for the same reason as
    /// <see cref="TitleFontSize"/>.
    /// </summary>
    public const int InspectorHeadingFontSize = 13;

    /// <summary>
    /// The inspector body: the explanation for whatever cell or creature is
    /// selected.
    ///
    /// <para>
    /// Raised from 11 by Issue #352, for the same reason as
    /// <see cref="RosterFontSize"/>.
    /// </para>
    /// </summary>
    public const int InspectorFontSize = 12;

    /// <summary>
    /// The event feed / creature story panel (<c>HudText.Feedback</c>).
    /// Already at the floor before Issue #352; named for the same reason as
    /// <see cref="TitleFontSize"/>.
    /// </summary>
    public const int FeedbackFontSize = 12;

    /// <summary>
    /// The eight legend rows explaining the map's marks and colours
    /// (<c>CreateLegend</c>). One constant for all eight rather than eight
    /// repeated numbers, because Issues #52, #222 and #86 already show the
    /// row count and wording change on their own schedule — the font size is
    /// not a per-row decision and giving it eight copies would only be eight
    /// chances for one of them to drift.
    ///
    /// <para>
    /// Issue #352 raised every other HUD font size to
    /// <see cref="HudReadability.MinimumPhysicalTextPixels"/> and measured
    /// this one instead of raising it on the same schedule:
    /// <c>evidence/352-fit.json</c> found that growing all eleven surfaces to
    /// 12 px overflowed the <c>feedback</c> panel on two of the eight checked
    /// resolutions, because the legend's own rows would have grown roughly
    /// one and a half times taller in the same fixed-height column. Put to the
    /// owner with the measurement, the choice was to keep the legend at its
    /// pre-#352 size rather than shrink the legend's copy or the feedback
    /// panel to make room. <b>This constant is therefore deliberately not
    /// tied to the floor</b> — unlike <see cref="HotkeyBadgeFontSize"/>, which
    /// still is — and <see cref="HudReadability.LegendReadabilityExemption"/>
    /// is what tells the readability guard not to expect it to clear the
    /// floor. See that member for the full decision record, dated
    /// 2026-08-10.
    /// </para>
    /// </summary>
    public const int LegendFontSize = 8;

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
