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

}

/// <summary>
/// The three answers the rule allows for a mark that can share a cell with a
/// body, plus the one it forbids there.
/// </summary>
public enum OverlayMarkPolicy
{
    /// <summary>
    /// Drawn as it is, opaque fill included. Legal only where no body's sprite
    /// can be underneath: a cell no body can stand on, or a body's own readout.
    /// </summary>
    Opaque,

    /// <summary>
    /// Opaque over a cell a body can stand on, as a stated exception rather than
    /// because the rule does not reach it. Every use has to carry a reason, and
    /// the rule test reports the exemptions by name so a second one cannot be
    /// added quietly.
    ///
    /// The one mark that uses it is the drag's cell count: it is an opaque plate
    /// because a number drawn over a sprite is unreadable, and making it
    /// translucent would be an appearance change Issue #90 forbids. Calling it
    /// "outside the rule" would have been the comfortable answer and the wrong
    /// one — the plate is anchored to the selection, which is an area of the map,
    /// and it lands on cells that hold bodies.
    /// </summary>
    OpaqueByExemption,

    /// <summary>
    /// The fill is translucent so the sprite reads through it. An outline, a
    /// stroke or a glyph may stay opaque, which is what keeps a countable mark
    /// countable.
    /// </summary>
    TranslucentFill,

    /// <summary>
    /// The mark has no fill at all: outline, stroke or glyph only, so nothing is
    /// asked of its alpha.
    ///
    /// <b>This does not mean it hides nothing.</b> That is what the docstring here
    /// used to say, and Issue #156 is the owner's playtest refuting it: a room's
    /// border is a two reference-pixel stroke drawn across a twenty-two pixel cell,
    /// and it struck through every goblin standing on it. A stroke covers what it
    /// is drawn over exactly as an opaque fill does — it just covers less of it.
    /// What this policy actually says is narrower and true: there is no fill, so
    /// there is no alpha to get wrong.
    ///
    /// A mark that chooses it therefore still owes an answer to "what is underneath
    /// me", and the answer has to be something other than "nothing can be":
    /// <see cref="OverlayMark.RoomBorder"/>'s is draw order — the part of it that
    /// can land on a body is drawn before the depth pass and the body walks over
    /// it. Whether the other marks on this policy owe the same answer is a separate
    /// audit, and it has its own Issue rather than a quiet paragraph here.
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

    // Issue #210. Not part of BodyState: the state of a body is what it is now,
    // and this is what has just happened to it — a different reading, drawn for
    // one tick and gone.
    BlowFeedback,
    CellInteraction,
    BrushPreview,
    SelectionCount,
    RememberedPlace,

    // Issue #52 / ADR 0013. RoomLabel replaces the ZoneLabel that used to live
    // here: the caption is no longer a word pinned to a hard-coded tile of a hard
    // coded zone, it is one room saying what it is and how it is doing.
    RoomBorder,
    RoomLabel,
    UnroomedObject,
}

/// <param name="Mark">The reading this rule governs.</param>
/// <param name="Subject">Whose sprite can be underneath.</param>
/// <param name="CellCanHoldBody">
/// Whether the simulation can put a body on the cell this mark explains, or
/// <c>null</c> for a mark that is not about a cell at all. It is <c>false</c>
/// only for a mark that lives on rock, and <c>InformationalOverlayRuleTests</c>
/// proves that against a real session rather than trusting the declaration. The
/// value is required exactly when <paramref name="Subject"/> is
/// <see cref="OverlayMarkSubject.Cell"/> and forbidden otherwise, so it can
/// never sit unread next to a mark it does not describe.
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
    bool? CellCanHoldBody,
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
            "The outline never fills, so nothing is hidden by it. Since Issue #52 " +
            "it is drawn only for a paint accepted on this tick and not applied " +
            "yet: a settled zone is a room and gets a border round the whole " +
            "patch instead of a box round each of its cells."),
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
            CellCanHoldBody: null,
            OverlayMarkPolicy.Opaque,
            1.0,
            1.0,
            "HP, state dot, downed cross and selection ring are the body's own " +
            "readout. They are drawn above the depth pass precisely so a raised " +
            "wall top cannot erase them, and they must stay legible."),
        new(
            OverlayMark.BlowFeedback,
            OverlayMarkSubject.Body,
            CellCanHoldBody: null,
            OverlayMarkPolicy.Opaque,
            1.0,
            1.0,
            "The flash, the damage number and the streak that says which way a " +
            "blow travelled are anchored to bodies and not to cells: the flash and " +
            "the number sit on the body that lost the hit points, and the streak " +
            "is a piece of the line between the two bodies the journal names. They " +
            "are above the depth pass for the reason the HP bar is — a raised wall " +
            "top erased a body's readout completely in the first review round of " +
            "Issue #83, and bodies stack besides, three raiders to one larder tile " +
            "in the first wave of the shipped journal. Nothing here fills: a tinted " +
            "silhouette of the body's own pose, a glyph and a stroke, all three " +
            "gone with the tick the blow was recorded on."),
        new(
            OverlayMark.RoomBorder,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.StrokeOnly,
            1.0,
            1.0,
            "A room is where creatures work, so a body inside it is the point. " +
            "Since Issue #156 the mark is not the whole border: the border is a " +
            "line on the floor a body stands on, so it is drawn under the depth " +
            "pass and the body walks over it. What is left above the depth pass " +
            "is the segment a wall standing directly in front of the room paints " +
            "over completely, which no inset can clear (Issues #139, #147) and " +
            "which therefore cannot hide anybody the wall is not hiding already. " +
            "The mark never fills either way. The owner's playtest is what " +
            "retired the old reason — «наверно существо должно быть над границей " +
            "комнаты, а не под ней»: a stroke does not have to fill a cell to " +
            "strike a creature through."),
        new(
            OverlayMark.RoomLabel,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.StrokeOnly,
            1.0,
            1.0,
            "A room caption is a stroke glyph and a word on the room's anchor " +
            "cell, and creatures stand on that cell like any other. Neither the " +
            "icon nor the text fills, so the caption reads over a body rather " +
            "than instead of it."),
        new(
            OverlayMark.UnroomedObject,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.StrokeOnly,
            1.0,
            1.0,
            "The mark on a post nobody has zoned lands on the very cell a " +
            "creature would be drilling at if the zone existed, which is the " +
            "whole point of it. A ring and a bar, no fill."),
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
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.OpaqueByExemption,
            1.0,
            1.0,
            "The count is a plate on the row above the drag, and a selection is " +
            "an area of the map: it does land on cells that hold bodies, and on " +
            "a rock selection in row 0 the caption is pushed inside the " +
            "selection itself. The rule reaches it and the exception is taken " +
            "anyway, for one reason — a number drawn over a sprite is " +
            "unreadable, and translucency would be an appearance change Issue " +
            "#90 forbids. It is bounded: it exists only while a button is held."),
        new(
            OverlayMark.RememberedPlace,
            OverlayMarkSubject.Cell,
            CellCanHoldBody: true,
            OverlayMarkPolicy.StrokeOnly,
            1.0,
            1.0,
            "A place a creature remembers is a place creatures walk through, and " +
            "the refusal is only readable if the player can see somebody standing " +
            "next to it still working. The mark is a ring and a cross-hatch with " +
            "no fill, so it cannot hide whoever is on the tile."),
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
    /// The marks the rule reaches: those that explain a cell the simulation can
    /// put a body on. Derived rather than listed, so a new mark joins it by being
    /// declared. An exemption stays in this set on purpose — it is a mark the
    /// rule reaches and that answers with a stated exception, not a mark the rule
    /// misses.
    /// </summary>
    public static IEnumerable<OverlayMarkRule> GovernedByTheRule() =>
        Rules.Where(rule =>
            rule.Subject == OverlayMarkSubject.Cell && rule.CellCanHoldBody == true);

    /// <summary>The accepted exceptions, each with the reason it was accepted.</summary>
    public static IEnumerable<OverlayMarkRule> Exemptions() =>
        Rules.Where(rule => rule.Policy == OverlayMarkPolicy.OpaqueByExemption);
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
