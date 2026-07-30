using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// What an informational mark is about. The rule "a mark must not hide a body"
/// only has teeth once this is stated, because the three subjects answer the
/// question "whose sprite is underneath?" differently.
/// </summary>
public enum OverlayMarkSubject
{
    /// <summary>
    /// The mark explains a map cell. If a body can stand on that cell, the mark
    /// is drawn over somebody else's sprite and must not hide it.
    /// </summary>
    Cell,

    /// <summary>
    /// The mark is a body's own readout — HP, state, downed, selection ring. It
    /// is anchored to the body rather than to a cell, and it is deliberately
    /// drawn above the depth pass so wall volume cannot erase it. This is the
    /// defect the first review round of Issue #83 found: a raised wall top hid
    /// an HP bar completely while the body itself stayed visible.
    /// </summary>
    Body,

    /// <summary>
    /// The mark explains the gesture in progress and lives only while a mouse
    /// button is held. It is anchored to the selection under the pointer, not to
    /// a cell, and it is the player's own readout of what they are doing.
    /// </summary>
    Gesture,
}

/// <summary>
/// The three answers the rule allows for a mark that can share a cell with a
/// body, plus the one it forbids there.
/// </summary>
public enum OverlayMarkPolicy
{
    /// <summary>
    /// Drawn as it is, opaque fill included. Legal only where no body's sprite
    /// can be underneath — a cell no body can stand on, a body's own readout or
    /// the gesture readout.
    /// </summary>
    Opaque,

    /// <summary>
    /// The fill is translucent so the sprite reads through it. An outline, a
    /// stroke or a glyph may stay opaque, which is what keeps a countable mark
    /// countable.
    /// </summary>
    TranslucentFill,

    /// <summary>
    /// The mark has no fill at all: outline, stroke or glyph only. It cannot
    /// hide anything, so nothing is asked of its alpha.
    /// </summary>
    StrokeOnly,

    /// <summary>
    /// The mark is not drawn while a body occupies its cell. No mark chooses
    /// this today; it exists because it is one of the three answers the rule
    /// allows, so choosing it later is one line in this file rather than a new
    /// mechanism. <see cref="InformationalOverlays.IsDrawn"/> resolves it and
    /// <see cref="BodyOccupancy"/> supplies the input.
    /// </summary>
    SkipWhenOccupied,
}

/// <summary>
/// Every mark drawn above the depth pass. One value per reading, not per draw
/// call: a mark is the thing the player is meant to take away, and a routine
/// plus its helpers is how it gets there.
/// </summary>
public enum OverlayMark
{
    ZoneOutline,
    JobRoute,
    DigDesignation,
    BuildSiteProgress,
    StockpileOccupancy,
    BodyState,
    ZoneLabel,
    CellInteraction,
    BrushPreview,
    SelectionCount,
}

/// <param name="Mark">The reading this rule governs.</param>
/// <param name="Subject">Whose sprite can be underneath.</param>
/// <param name="CellCanHoldBody">
/// Whether the simulation can put a body on the cell this mark explains. It is
/// <c>false</c> only for a mark that lives on rock, and
/// <c>InformationalOverlayRuleTests</c> proves that against a real session
/// rather than trusting the declaration.
/// </param>
/// <param name="Policy">What the mark does about it.</param>
/// <param name="FillAlpha">
/// The alpha of an ordinary fill. 1.0 whenever the policy allows opacity, so the
/// adapter can read this value unconditionally.
/// </param>
/// <param name="AccentAlpha">
/// The alpha of a fill that carries the whole reading, such as a progress bar
/// with no outline to hold its shape. Equal to <paramref name="FillAlpha"/>
/// where a mark has no such fill.
/// </param>
/// <param name="Reason">Why this mark answers the rule the way it does.</param>
public sealed record OverlayMarkRule(
    OverlayMark Mark,
    OverlayMarkSubject Subject,
    bool CellCanHoldBody,
    OverlayMarkPolicy Policy,
    double FillAlpha,
    double AccentAlpha,
    string Reason);

/// <summary>
/// One rule governs every informational mark: <b>a mark that can share a cell
/// with a body must not hide it.</b>
///
/// The rule was written down once in <c>PROTOTYPE_GRAYBOX.md</c> and then broken
/// three times in a row, in three consecutive review rounds of Issue #83 — the
/// post fill, the work-goal dot, the delivery pips — each time landing opaque on
/// exactly the creature it explains. It was caught by eye on a captured frame
/// every time, because no test project references <c>DungeonFortress.Game</c>
/// and the project deliberately has no pixel golden.
///
/// So the rule lives here, on the side of the seam
/// <see href="../../docs/decisions/0011-presentation-layer-without-engine.md">
/// ADR 0011</see> makes testable: the declaration is data, the alpha the adapter
/// draws with is read from this file, and the adapter is a translator rather
/// than a second copy of the decision.
///
/// The simulation is what makes a shared cell the normal case rather than an
/// edge one: <c>Drill</c> requires the post cell, <c>Build</c> requires the site
/// cell for every one of its ticks, and storing stone requires the stockpile
/// cell.
/// </summary>
public static class InformationalOverlays
{
    /// <summary>
    /// The fill alpha of a mark whose outline carries its shape. Low enough that
    /// a goblin sprite reads through a delivery pip sitting on it.
    /// </summary>
    public const double TranslucentFillAlpha = 0.35;

    /// <summary>
    /// The fill alpha of a mark that <em>is</em> its fill — a progress bar has no
    /// outline to carry the shape, so it needs more than
    /// <see cref="TranslucentFillAlpha"/> and still has to stay under 1.
    /// </summary>
    public const double TranslucentAccentAlpha = 0.6;

    /// <summary>
    /// The brush preview tints whole cells at once, so it sits lower than every
    /// other translucent mark. Its own value rather than a shared one: this is a
    /// reading, and sharing a constant would silently move it whenever the pip
    /// alpha moved.
    /// </summary>
    public const double BrushPreviewAlpha = 0.32;

    private static readonly OverlayMarkRule[] Rules =
    [
        new(
            OverlayMark.ZoneOutline,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.StrokeOnly,
            1.0,
            1.0,
            "A zone is where creatures work, so a body on the cell is the point. " +
            "The outline never fills, so nothing is hidden by it."),
        new(
            OverlayMark.JobRoute,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.TranslucentFill,
            TranslucentFillAlpha,
            TranslucentFillAlpha,
            "The goal of a Drill job is always the post cell the creature stands " +
            "on, and the goal of a Haul job is the tile of the item at pickup."),
        new(
            OverlayMark.DigDesignation,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: false,
            OverlayMarkPolicy.Opaque,
            1.0,
            1.0,
            "A dig mark is on rock. Rock is impassable, so no body is ever " +
            "underneath and the mark may stay solid enough to read at tile size."),
        new(
            OverlayMark.BuildSiteProgress,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.TranslucentFill,
            TranslucentFillAlpha,
            TranslucentAccentAlpha,
            "A Build job is created only once the stone has arrived and its " +
            "target is the site itself, so the builder stands on the cell for " +
            "every tick the bar is on screen."),
        new(
            OverlayMark.StockpileOccupancy,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.TranslucentFill,
            TranslucentFillAlpha,
            TranslucentFillAlpha,
            "Storing stone requires the carrier to be on the stockpile cell, so " +
            "the pip appears on the tick a body is standing there."),
        new(
            OverlayMark.BodyState,
            OverlayMarkSubject.Body,
            CellCanHoldBody: true,
            OverlayMarkPolicy.Opaque,
            1.0,
            1.0,
            "HP, state dot, downed cross and selection ring are the body's own " +
            "readout. They are drawn above the depth pass precisely so a raised " +
            "wall top cannot erase them, and they must stay legible."),
        new(
            OverlayMark.ZoneLabel,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.StrokeOnly,
            1.0,
            1.0,
            "A zone caption is glyphs on the zone's anchor cell. It never fills " +
            "the cell, so it reads over a body rather than instead of it."),
        new(
            OverlayMark.CellInteraction,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.StrokeOnly,
            1.0,
            1.0,
            "The legal-target outline and the selected-cell outline are input " +
            "affordances and must stay visible; neither of them fills."),
        new(
            OverlayMark.BrushPreview,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.TranslucentFill,
            BrushPreviewAlpha,
            BrushPreviewAlpha,
            "A drag tints whole cells, including cells creatures are standing " +
            "on, and the player has to see who is in the area being marked."),
        new(
            OverlayMark.SelectionCount,
            OverlayMarkSubject.Gesture,
            CellCanHoldBody: true,
            OverlayMarkPolicy.Opaque,
            1.0,
            1.0,
            "The cell count is the gesture's own readout on an opaque plate, " +
            "kept for one reason: a number over a sprite is unreadable. It is " +
            "anchored to the selection rather than to a cell and it exists only " +
            "while the button is held."),
    ];

    public static IReadOnlyList<OverlayMarkRule> All => Rules;

    /// <summary>
    /// The rule for one mark. It throws rather than returning a default, because
    /// a missing declaration is exactly the failure this manifest exists to make
    /// impossible.
    /// </summary>
    public static OverlayMarkRule For(OverlayMark mark)
    {
        foreach (var rule in Rules)
        {
            if (rule.Mark == mark)
            {
                return rule;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(mark),
            mark,
            "This mark is drawn above the depth pass with no declared policy. " +
            "Declare how it answers 'a mark must not hide a body' first.");
    }

    /// <summary>The alpha the adapter must draw this mark's ordinary fill with.</summary>
    public static double FillAlpha(OverlayMark mark) => For(mark).FillAlpha;

    /// <summary>
    /// The alpha for a fill that carries the whole reading, such as a progress
    /// bar with no outline behind it.
    /// </summary>
    public static double AccentAlpha(OverlayMark mark) => For(mark).AccentAlpha;

    /// <summary>
    /// Whether a mark with this policy is drawn at all. Only
    /// <see cref="OverlayMarkPolicy.SkipWhenOccupied"/> can answer <c>false</c>,
    /// and the input comes from <see cref="BodyOccupancy"/>.
    /// </summary>
    public static bool IsDrawn(OverlayMarkPolicy policy, bool cellHoldsBody) =>
        policy != OverlayMarkPolicy.SkipWhenOccupied || !cellHoldsBody;

    /// <inheritdoc cref="IsDrawn(OverlayMarkPolicy, bool)"/>
    public static bool IsDrawn(OverlayMark mark, bool cellHoldsBody) =>
        IsDrawn(For(mark).Policy, cellHoldsBody);

    /// <summary>
    /// The marks that would hide a body if their policy were relaxed. This is the
    /// set the rule test walks, and it is derived rather than listed so a new
    /// mark joins it by being declared.
    /// </summary>
    public static IEnumerable<OverlayMarkRule> GovernedByTheRule() =>
        Rules.Where(rule =>
            rule.Subject == OverlayMarkSubject.Cell && rule.CellCanHoldBody);
}

/// <summary>
/// Which cells a body is standing on. The concept lives here and not in the
/// adapter for the same reason the policy does: "can this mark land on a
/// creature?" is a claim about the simulation, and a claim made in
/// <c>Main.cs</c> is a claim no CI job can read.
///
/// It reads positions only. A downed creature still has a sprite on the map, so
/// it still counts; an escaped raider has left and does not.
/// </summary>
public static class BodyOccupancy
{
    public static IReadOnlySet<GridPoint> Of(PrototypeSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var cells = new HashSet<GridPoint>();
        foreach (var creature in state.Creatures)
        {
            cells.Add(creature.Position);
        }

        foreach (var raider in state.Raiders)
        {
            if (raider.Mode != RaiderMode.Escaped)
            {
                cells.Add(raider.Position);
            }
        }

        return cells;
    }

    public static bool IsOccupied(PrototypeSnapshot state, GridPoint cell) =>
        Of(state).Contains(cell);
}
