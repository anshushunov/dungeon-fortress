namespace DungeonFortress.Presentation;

/// <summary>
/// The four passes the map is drawn in. The order is the order of the enum, and
/// a routine's pass is what decides whether wall volume may occlude it.
/// </summary>
public enum WorldDrawPass
{
    /// <summary>
    /// Floor and base material that belongs below elevated world geometry,
    /// including blueprint and stockpile silhouettes but not their countable
    /// pips.
    /// </summary>
    BelowDepth,

    /// <summary>
    /// Walls, training posts, creatures and raiders in stable back-to-front
    /// Y-order. This is the only pass in which one thing may hide another.
    /// </summary>
    Depth,

    /// <summary>
    /// Informational marks above world depth: zone outlines, routes, dig intent,
    /// material pips, body information and zone labels.
    /// </summary>
    Informational,

    /// <summary>
    /// Input affordances above every informational mark: legal-target and
    /// selection outlines, then the active brush preview.
    /// </summary>
    Interaction,
}

/// <param name="Name">The adapter method, by name.</param>
/// <param name="Pass">Which pass it draws in.</param>
/// <param name="Mark">
/// The reading it produces, or <c>null</c> for a routine below the informational
/// passes, where the "must not hide a body" rule does not apply because the
/// depth order already answers the question.
/// </param>
public sealed record WorldDrawRoutine(
    string Name,
    WorldDrawPass Pass,
    OverlayMark? Mark);

/// <summary>
/// What <c>Main.DrawMap</c> draws, in order, and which pass each routine belongs
/// to.
///
/// This exists because "which pass is this mark in?" turned out to be the
/// question behind three of the four review rounds of Issue #83, and it is a
/// question no test could ask: the answer lived in the order of twelve statements
/// inside a file no test project references. Here it is data, and
/// <c>WorldDrawPassGuardTests</c> reads the adapter source and requires the two
/// to agree — the composition, the pass of every routine, and the alpha every
/// mark's fills are drawn with.
///
/// Nothing in this file is used at run time. It is a contract about the adapter,
/// kept on the side of the seam where a check can read it.
/// </summary>
public static class WorldDrawOrder
{
    /// <summary>The adapter method the whole map draw starts from.</summary>
    public const string Entry = "DrawMap";

    /// <summary>
    /// The prefix that makes a method a drawing routine. Anything with this
    /// prefix that <see cref="Entry"/> can reach must be declared below.
    ///
    /// This is a naming <em>convention</em>, and the boundary is worth stating
    /// plainly rather than leaving to be discovered: a drawing method named
    /// something else — <c>PaintThreatHalo</c> — is outside the manifest and
    /// therefore outside every check built on it. The convention is the load
    /// bearing part; the checks only hold code that follows it. The other way out
    /// was closed instead of documented: <see cref="Entry"/> itself draws no
    /// primitive, so there is no unnamed body left inside the passes.
    /// </summary>
    public const string RoutinePrefix = "Draw";

    private static readonly WorldDrawRoutine[] Routines =
    [
        // Pass 1 — below depth.
        new("DrawMapBackground", WorldDrawPass.BelowDepth, null),
        new("DrawFloorTiles", WorldDrawPass.BelowDepth, null),
        // A room's floor is floor and not a mark: it is material laid under the
        // depth pass, so a body walks over it and no "must not hide a body" rule
        // has to reach it. That is the Dungeon Keeper answer ADR 0013 takes —
        // «читаемость решена не контуром, а полом» — and it is why the covering
        // replaced the translucent film rather than joining it.
        new("DrawRoomFloors", WorldDrawPass.BelowDepth, null),
        new("DrawRoomFloor", WorldDrawPass.BelowDepth, null),
        new("DrawBeds", WorldDrawPass.BelowDepth, null),
        new("DrawLooseItems", WorldDrawPass.BelowDepth, null),
        new("DrawBuildSites", WorldDrawPass.BelowDepth, null),
        new("DrawBlueprint", WorldDrawPass.BelowDepth, null),
        new("DrawStockpileCells", WorldDrawPass.BelowDepth, null),
        new("DrawStockpileCell", WorldDrawPass.BelowDepth, null),
        // Issue #156. A room's border is a line on the floor a body stands on, so
        // the depth pass is what should decide which of the two is on top — the
        // owner reported the alternative from playtest, with the line struck
        // through the creatures on the kitchen's bottom row. It is drawn last of
        // this pass, after the things standing on the floor, because it is the
        // edge of the room's own covering and a stockpile silhouette breaking the
        // outline would be the same defect wearing a different hat.
        new("DrawRoomBorders", WorldDrawPass.BelowDepth, null),
        new("DrawRoomBorder", WorldDrawPass.BelowDepth, null),

        // Pass 2 — the depth pass itself.
        new("DrawElevatedWorld", WorldDrawPass.Depth, null),
        new("DrawWall", WorldDrawPass.Depth, null),
        new("DrawBuiltPost", WorldDrawPass.Depth, null),
        new("DrawCreature", WorldDrawPass.Depth, null),
        new("DrawRaider", WorldDrawPass.Depth, null),
        new("DrawSidedBody", WorldDrawPass.Depth, null),
        new("DrawGoblinOutline", WorldDrawPass.Depth, null),
        new("DrawGoblin", WorldDrawPass.Depth, null),
        // Issue #244 / ADR 0020. The body drawn from the cutout rig instead of a
        // flat pose. It is in the depth pass because it *is* the body: the same
        // rectangle, the same foot line, the same Y-order slot, only assembled
        // from parts.
        new("DrawRigBody", WorldDrawPass.Depth, null),

        // Pass 3 — informational marks.
        // The half of a room's border that a wall standing in front of it would
        // otherwise swallow whole, and nothing else: RoomGeometry.RoomBorderLayer
        // is where the split is decided, and no inset can buy that segment back
        // (Issues #139, #147).
        new("DrawRoomBordersOverWalls", WorldDrawPass.Informational, OverlayMark.RoomBorder),
        new("DrawRoomBorderOverWall", WorldDrawPass.Informational, OverlayMark.RoomBorder),
        new("DrawZoneOutlines", WorldDrawPass.Informational, OverlayMark.ZoneOutline),
        new("DrawJobRoutes", WorldDrawPass.Informational, OverlayMark.JobRoute),
        new("DrawDigDesignations", WorldDrawPass.Informational, OverlayMark.DigDesignation),
        new("DrawDigMark", WorldDrawPass.Informational, OverlayMark.DigDesignation),
        new(
            "DrawBuildSiteInformationOverlays",
            WorldDrawPass.Informational,
            OverlayMark.BuildSiteProgress),
        new("DrawBlueprintPips", WorldDrawPass.Informational, OverlayMark.BuildSiteProgress),
        new(
            "DrawStockpileInformationOverlays",
            WorldDrawPass.Informational,
            OverlayMark.StockpileOccupancy),
        new("DrawStockpilePips", WorldDrawPass.Informational, OverlayMark.StockpileOccupancy),
        new("DrawBodyInformationOverlays", WorldDrawPass.Informational, OverlayMark.BodyState),
        new("DrawCreatureInformation", WorldDrawPass.Informational, OverlayMark.BodyState),
        new("DrawRaiderInformation", WorldDrawPass.Informational, OverlayMark.BodyState),
        // Issue #210. All three marks of a blow sit above the depth pass, for the
        // reason the HP bar does: a raised wall top and a body sharing the same
        // cell both erase a mark left inside that pass, and the first review round
        // of Issue #83 is where that was measured.
        new("DrawBlowFlash", WorldDrawPass.Informational, OverlayMark.BlowFeedback),
        // Issue #244. The rig's half of the flash, and the contact spark. Both
        // are of the same mark and the same pass as the three marks above: a
        // spark is what has just happened to a body, drawn for one tick and gone.
        // DrawRigFlash repeats the loop of DrawRigBody rather than calling it,
        // because the two are in different passes and a call across passes is
        // exactly the defect this manifest exists to catch.
        new("DrawRigFlash", WorldDrawPass.Informational, OverlayMark.BlowFeedback),
        new("DrawContactSparks", WorldDrawPass.Informational, OverlayMark.BlowFeedback),
        new("DrawBlowStreaks", WorldDrawPass.Informational, OverlayMark.BlowFeedback),
        new("DrawBlowDamage", WorldDrawPass.Informational, OverlayMark.BlowFeedback),
        new("DrawDownedMark", WorldDrawPass.Informational, OverlayMark.BodyState),
        new("DrawHpBar", WorldDrawPass.Informational, OverlayMark.BodyState),
        new("DrawRoomLabels", WorldDrawPass.Informational, OverlayMark.RoomLabel),
        new("DrawRoomLabel", WorldDrawPass.Informational, OverlayMark.RoomLabel),
        new("DrawRoomIcon", WorldDrawPass.Informational, OverlayMark.RoomLabel),
        new(
            "DrawUnroomedObjects",
            WorldDrawPass.Informational,
            OverlayMark.UnroomedObject),
        new(
            "DrawRememberedPlaces",
            WorldDrawPass.Informational,
            OverlayMark.RememberedPlace),

        // Pass 4 — input affordances.
        new(
            "DrawCellInteractionOverlays",
            WorldDrawPass.Interaction,
            OverlayMark.CellInteraction),
        new("DrawBrushPreview", WorldDrawPass.Interaction, OverlayMark.BrushPreview),
        new("DrawSelectionCount", WorldDrawPass.Interaction, OverlayMark.SelectionCount),
    ];

    /// <summary>Every declared routine, whether or not <c>DrawMap</c> calls it directly.</summary>
    public static IReadOnlyList<WorldDrawRoutine> All => Routines;

    /// <summary>
    /// The routines <c>DrawMap</c> calls itself, in the order it calls them. Each
    /// one opens or continues a pass; the rest are their helpers.
    /// </summary>
    public static IReadOnlyList<string> Steps { get; } =
    [
        "DrawMapBackground",
        "DrawFloorTiles",
        "DrawRoomFloors",
        "DrawBuildSites",
        "DrawStockpileCells",
        "DrawBeds",
        "DrawLooseItems",
        "DrawRoomBorders",
        "DrawElevatedWorld",
        "DrawRoomBordersOverWalls",
        "DrawZoneOutlines",
        "DrawJobRoutes",
        "DrawDigDesignations",
        "DrawBuildSiteInformationOverlays",
        "DrawStockpileInformationOverlays",
        "DrawBodyInformationOverlays",
        "DrawRoomLabels",
        "DrawUnroomedObjects",
        "DrawRememberedPlaces",
        "DrawCellInteractionOverlays",
        "DrawBrushPreview",
    ];

    public static WorldDrawRoutine? Find(string routine) =>
        Routines.FirstOrDefault(item =>
            string.Equals(item.Name, routine, StringComparison.Ordinal));

    /// <summary>Every routine that produces one reading, entry plus helpers.</summary>
    public static IEnumerable<WorldDrawRoutine> RoutinesOf(OverlayMark mark) =>
        Routines.Where(routine => routine.Mark == mark);
}
