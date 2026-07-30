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
    /// </summary>
    public const string RoutinePrefix = "Draw";

    private static readonly WorldDrawRoutine[] Routines =
    [
        // Pass 1 — below depth.
        new("DrawBuildSites", WorldDrawPass.BelowDepth, null),
        new("DrawBlueprint", WorldDrawPass.BelowDepth, null),
        new("DrawStockpileCells", WorldDrawPass.BelowDepth, null),
        new("DrawStockpileCell", WorldDrawPass.BelowDepth, null),

        // Pass 2 — the depth pass itself.
        new("DrawElevatedWorld", WorldDrawPass.Depth, null),
        new("DrawWall", WorldDrawPass.Depth, null),
        new("DrawBuiltPost", WorldDrawPass.Depth, null),
        new("DrawCreature", WorldDrawPass.Depth, null),
        new("DrawRaider", WorldDrawPass.Depth, null),
        new("DrawGoblin", WorldDrawPass.Depth, null),

        // Pass 3 — informational marks.
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
        new("DrawDownedMark", WorldDrawPass.Informational, OverlayMark.BodyState),
        new("DrawHpBar", WorldDrawPass.Informational, OverlayMark.BodyState),
        new("DrawZoneLabels", WorldDrawPass.Informational, OverlayMark.ZoneLabel),
        new("DrawZoneLabel", WorldDrawPass.Informational, OverlayMark.ZoneLabel),

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
        "DrawBuildSites",
        "DrawStockpileCells",
        "DrawElevatedWorld",
        "DrawZoneOutlines",
        "DrawJobRoutes",
        "DrawDigDesignations",
        "DrawBuildSiteInformationOverlays",
        "DrawStockpileInformationOverlays",
        "DrawBodyInformationOverlays",
        "DrawZoneLabels",
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
