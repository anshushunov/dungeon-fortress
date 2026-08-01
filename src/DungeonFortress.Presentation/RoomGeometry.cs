using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>One straight piece of a drawn outline.</summary>
public readonly record struct ViewSegment(ViewPoint From, ViewPoint To);

/// <summary>
/// One piece of a room's outline, together with the cell and the side of that
/// cell it was drawn for.
/// </summary>
/// <param name="Cell">The room cell whose boundary this is.</param>
/// <param name="Side">Which side of that cell faces outward here.</param>
/// <param name="Segment">The line as drawn, inset and with its ends resolved.</param>
public readonly record struct RoomBorderEdge(
    GridPoint Cell,
    WallNeighbors Side,
    ViewSegment Segment);

/// <summary>
/// One run of a room's outline that is drawn in one pass, together with the cell
/// and the side of that cell it came from.
/// </summary>
/// <param name="Cell">The room cell whose boundary this is.</param>
/// <param name="Side">Which side of that cell faces outward here.</param>
/// <param name="Segment">The line as drawn: inset, ends resolved, then cut where
/// the pass it belongs to changes.</param>
/// <param name="Layer">The pass it is drawn in.</param>
public readonly record struct RoomBorderPiece(
    GridPoint Cell,
    WallNeighbors Side,
    ViewSegment Segment,
    RoomBorderLayer Layer);

/// <summary>
/// Which of the two passes a piece of a room's outline is drawn in (Issue #156).
///
/// A room's border used to be one informational mark drawn after the depth pass,
/// and the owner reported the consequence from playtest: «наверно существо должно
/// быть над границей комнаты, а не под ней» — a creature standing on the bottom
/// row of the kitchen was struck through by the line. A body has volume on this
/// map and a border is a line on the floor it stands on, so the depth pass is the
/// thing that should answer which of the two wins.
///
/// It cannot be the only answer, because the reason the border was put above the
/// depth pass in the first place is still true: a wall standing directly south of
/// a room cell is drawn <em>in front of</em> that cell and covers the bottom of it
/// outright, so a border drawn under the depth pass loses its south edge there and
/// no inset buys it back (Issues #139 and #147, and
/// <see cref="MaximumBorderInset"/>). Hence two layers rather than one, split by a
/// measurement rather than by taste: <see cref="LayerOf"/>.
///
/// The split is per <see cref="RoomBorderPiece"/> and not per boundary edge,
/// because a wall in front covers the lower part of the vertical edge meeting the
/// horizontal one and not the whole of it. Classifying whole edges opened the
/// corner between them; see <see cref="BorderPieces"/>.
/// </summary>
public enum RoomBorderLayer
{
    /// <summary>
    /// Drawn before the depth pass, so whoever stands on the line is drawn over
    /// it. This is where all but a handful of a map's border segments belong.
    /// </summary>
    UnderBodies,

    /// <summary>
    /// Drawn after the depth pass, and only for a segment a wall in front paints
    /// over completely. Nothing that is not behind that wall can be hidden by such
    /// a segment — the wall already hides it — so this layer is above the depth
    /// pass without being above a body anybody can see.
    /// </summary>
    OverWallInFront,
}

/// <summary>
/// The shape of a room on screen: its border, where its caption sits, and how far
/// in the border is drawn.
///
/// It lives here rather than in the simulation for the same reason
/// <see cref="WallTopology"/> does: an outline is topology over a set of tiles, it
/// follows from the tiles the snapshot publishes, and it needs no tick to run
/// (<see href="../../docs/decisions/0011-presentation-layer-without-engine.md">ADR
/// 0011</see>). The simulation publishes the patch; this file turns the patch into
/// a line.
///
/// <b>Why a border at all.</b>
/// <see href="../../docs/decisions/0013-what-is-a-room.md">ADR 0013</see> makes it
/// mandatory rather than optional, and names the game it is avoiding: in Dwarf
/// Fortress a room exists as an entity and its boundary is never shown, which is
/// «одна из самых устойчивых жалоб на игру». A per-cell box around every tile —
/// what the zone outline drew before — is the same failure wearing the opposite
/// costume: twenty-one boxes are not a room, they are texture. One line around the
/// patch is.
/// </summary>
public static class RoomGeometry
{
    /// <summary>
    /// How far inside its own cells a room's border is drawn, in reference pixels
    /// before the world scale is applied, for a room with no rock against it at
    /// all.
    ///
    /// It depends on the purpose because rooms overlap: a <c>Forbidden</c> paint
    /// over a gym is two rooms on the same tiles, and two borders on the same
    /// pixels are one border. Purposes three apart in the enum share a value, and
    /// that is deliberate rather than an oversight — the inset exists so that a
    /// second border is not swallowed whole, and colour does the rest of the
    /// telling apart.
    ///
    /// This ladder itself stays shallow — 2.0 to 5.0 — on purpose: widening it
    /// would push the innermost border a third of the way into a one-tile room
    /// at the smallest tile size ADR 0008 allows.
    /// <see cref="BorderInset(ZoneKind, WallNeighbors)"/> spends exactly that
    /// cost, but only on the side profile that actually earns it (Issues #139
    /// and #147): the alternative was a border sitting inside the wall's own
    /// drawn band, and that reads worse than a deep inset does. Naming the trade
    /// rather than paying it silently: it does not stop at the border. The
    /// wall-aware ladder's own reach pushed a second, smaller cost into view — it
    /// can overlap <see cref="LabelDefaultTop"/>'s old fixed caption position,
    /// which is why that position moves for a room that pays for it
    /// (<see cref="LabelTop"/>).
    /// </summary>
    public static double BorderInset(ZoneKind purpose) =>
        BorderInset(purpose, WallNeighbors.None);

    /// <summary>
    /// The inset a room is actually drawn at, given which of its sides have rock
    /// against them (<see cref="WallSides"/>).
    ///
    /// <see cref="WallRenderGeometry"/> draws a wall as a volume, and the volume
    /// reaches out of the wall's own footprint in two different ways that a room
    /// border has to clear — <see cref="WallRenderGeometry.DrawnBands"/> is where
    /// both are measurable:
    ///
    /// <list type="bullet">
    /// <item><b>North.</b> A front facade overhangs
    /// <see cref="WallRenderGeometry.FacadeReferenceOverhang"/> of ground past
    /// the footprint, into the cell right below it, and the seam that closes the
    /// facade off is a band half of which lands lower still. Issue #139 found the
    /// border drawn inside that overhang.</item>
    /// <item><b>East and west.</b> A wall's dark side seam is centred exactly on
    /// the boundary between its cell and its neighbour's, so half of
    /// <see cref="WallRenderGeometry.EdgeReferenceWidth"/> is drawn inside the
    /// neighbouring floor cell. That is a much thinner intrusion than the facade
    /// — and it was the one the owner actually complained about, because the
    /// plain ladder left the border 0.375 reference pixels clear of it, closer
    /// than the case #139 fixed ever was (Issue #147).</item>
    /// </list>
    ///
    /// The per-purpose step is added on top of whichever base the side profile
    /// earns, so two overlapping purposes pushed by the same walls are exactly as
    /// far apart from each other as they were before the push.
    ///
    /// <b>South is deliberately absent</b>, and not by oversight. A wall south of
    /// a room is drawn in front of it: its top mass is lifted
    /// <see cref="WallRenderGeometry.FacadeReferenceHeight"/> reference pixels
    /// above its own footprint and therefore covers the bottom of the room's cell
    /// outright, whatever the border does — and the bright seam along the top of
    /// that mass reaches half its own width higher still. Clearing it would cost
    /// more than <see cref="MaximumBorderInset"/> before a single purpose step is
    /// added — over half a cell — so no inset is an answer to it. The accepted
    /// answer is draw order, decided in Issue #83 and declared in
    /// <see cref="WorldDrawOrder"/>: the segment a wall in front would swallow is
    /// drawn after the depth pass, so the room keeps its south edge instead of
    /// losing it under the wall.
    ///
    /// Since Issue #156 that is <em>only</em> that segment and no longer the whole
    /// border: see <see cref="RoomBorderLayer"/>.
    /// </summary>
    public static double BorderInset(ZoneKind purpose, WallNeighbors wallSides) =>
        WallClearance(wallSides) + PurposeLadderStep(purpose);

    /// <summary>
    /// The one decision <c>Main.DrawRoomBorder</c> and <c>Main.DrawRoomLabel</c>
    /// both make, in one place: the inset for this room on this map.
    ///
    /// It is a single method rather than a condition each of them writes out,
    /// because review of Issue #139 spent two rounds proving by hand that the two
    /// copies still said the same thing. They cannot disagree if there is one of
    /// them.
    /// </summary>
    public static double BorderInsetFor(
        ZoneKind purpose,
        IReadOnlyCollection<GridPoint> tiles,
        IReadOnlySet<GridPoint> wallTiles) =>
        BorderInset(purpose, WallSides(tiles, wallTiles));

    /// <summary>
    /// Which sides of a room have rock drawing into them.
    ///
    /// North and south read the straight neighbour, because that is the only
    /// direction from which a wall's horizontal geometry — facade overhang below,
    /// lifted top mass above — can reach into a room's cell.
    ///
    /// East and west read the whole column beside the cell, diagonals included,
    /// and that is the correction Issue #147 needed over the north-only predicate
    /// Issue #139 left. A wall's dark side seam runs the full height of its
    /// visual mass, and that mass is lifted a facade's height above the wall's
    /// own row and hangs an overhang's worth below it — so a wall one row up and
    /// one column across still paints inside the cell, at exactly the same depth
    /// as a wall straight beside it. <c>quarters@19,2</c> on the shipped map has
    /// both, and its north-west neighbour alone would have been enough.
    /// </summary>
    public static WallNeighbors WallSides(
        IReadOnlyCollection<GridPoint> tiles,
        IReadOnlySet<GridPoint> wallTiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(wallTiles);
        var sides = WallNeighbors.None;
        foreach (var cell in tiles)
        {
            if (wallTiles.Contains(new GridPoint(cell.X, cell.Y - 1)))
            {
                sides |= WallNeighbors.North;
            }

            if (wallTiles.Contains(new GridPoint(cell.X, cell.Y + 1)))
            {
                sides |= WallNeighbors.South;
            }

            for (var row = cell.Y - 1; row <= cell.Y + 1; row++)
            {
                if (wallTiles.Contains(new GridPoint(cell.X - 1, row)))
                {
                    sides |= WallNeighbors.West;
                }

                if (wallTiles.Contains(new GridPoint(cell.X + 1, row)))
                {
                    sides |= WallNeighbors.East;
                }
            }
        }

        return sides;
    }

    /// <summary>
    /// The reference-pixel cell every quantity in this file is measured in,
    /// before <see cref="CameraView.WorldVisualScale"/> turns it into screen
    /// pixels. <c>CameraView</c> keeps its own copy private;
    /// <c>ReferenceCell_is_the_one_CameraView_scales_by</c> pins the two together
    /// through <see cref="CameraView.WorldVisualScale"/> rather than trusting the
    /// restatement, because a reference-pixel number silently compared against a
    /// screen-pixel one is a mistake this file's tests have already made twice.
    /// </summary>
    public const double ReferenceCell = 22.0;

    /// <summary>
    /// The reference-pixel width <c>Main.DrawRoomBorder</c> strokes a border line
    /// with. The adapter reads it from here rather than repeating the number, so
    /// the arithmetic below cannot go stale behind a change to the drawn line —
    /// which is exactly what the debt ledger recorded against the private
    /// half-width this used to be a silent copy of. That ledger entry was
    /// deleted once this link became executable; the record of why it existed
    /// is this docstring and PR #151.
    /// </summary>
    public const double BorderStrokeWidth = 2.0;

    /// <summary>
    /// Half of <see cref="BorderStrokeWidth"/>: a line "at" some coordinate
    /// actually covers a band a half-stroke to either side of it, and it is the
    /// near edge of that band — not the coordinate itself — that must clear the
    /// wall.
    /// </summary>
    public const double BorderStrokeHalfWidth = BorderStrokeWidth / 2.0;

    /// <summary>
    /// A further reference pixel kept clear once the wall's own drawn band and
    /// the border stroke's half-width are both accounted for, so the border reads
    /// as apart from the wall rather than merely not touching it.
    /// </summary>
    public const double WallVisibleGap = 1.0;

    /// <summary>
    /// The base a room with nothing against it starts from — Issue #52's original
    /// ladder, kept as the shallow end.
    /// </summary>
    public const double PlainBorderBase = 2.0;

    /// <summary>
    /// The reference-pixel depth a border needs, before its per-purpose step, to
    /// clear a wall standing to the east or west: half the wall's own side seam,
    /// which is centred on the shared cell boundary, plus the half-width of the
    /// border's stroke, plus a visible gap.
    /// </summary>
    public const double SideWallClearance =
        (WallRenderGeometry.EdgeReferenceWidth / 2.0) + BorderStrokeHalfWidth + WallVisibleGap;

    /// <summary>
    /// The same for a wall standing to the north, where the facade's downward
    /// overhang past the wall's own footprint is added to it — plus the half of
    /// the seam that closes the facade off, which is drawn lower than the facade
    /// rectangle itself and which Issue #139 did not account for, leaving 0.375
    /// reference pixels instead of the whole gap it meant to buy.
    ///
    /// Derived from <see cref="WallRenderGeometry"/> throughout rather than
    /// restated, so a change to the wall's geometry cannot silently leave this
    /// stale.
    /// </summary>
    public const double NorthWallClearance =
        WallRenderGeometry.FacadeReferenceOverhang +
        (WallRenderGeometry.EdgeReferenceWidth / 2.0) +
        BorderStrokeHalfWidth +
        WallVisibleGap;

    /// <summary>
    /// The deepest a border may ever be inset before it stops being a border: at
    /// half a cell the two opposite sides of a one-cell room meet, and their
    /// stroke bands meet a half-width earlier still.
    ///
    /// This is the ceiling the ladder is bounded against, and it replaces the
    /// "a quarter of a tile" rule the tests used to carry. That rule compared a
    /// reference-pixel inset against 32 — the smallest <em>screen</em> tile ADR
    /// 0008 allows — which is the units mistake the debt ledger recorded until PR #151
    /// closed it — the entry is gone, and this comment is what is left of it;
    /// and once Issue #139 pushed the wall-adjacent ladder to 8.0 it was no
    /// longer true at any reading of the units. The ceiling that survives the
    /// question is the geometric one: the inset must leave a room to be inside.
    /// </summary>
    public const double MaximumBorderInset = (ReferenceCell / 2.0) - BorderStrokeHalfWidth;

    /// <summary>
    /// The base a border starts from for one side profile: the deepest clearance
    /// any of its walled sides demands, and the plain base when none does.
    /// </summary>
    public static double WallClearance(WallNeighbors wallSides)
    {
        var clearance = PlainBorderBase;
        if (wallSides.HasFlag(WallNeighbors.East) || wallSides.HasFlag(WallNeighbors.West))
        {
            clearance = Math.Max(clearance, SideWallClearance);
        }

        if (wallSides.HasFlag(WallNeighbors.North))
        {
            clearance = Math.Max(clearance, NorthWallClearance);
        }

        return clearance;
    }

    /// <summary>
    /// The step every base climbs by purpose, so that whichever base a room
    /// starts from, two overlapping purposes stay exactly as far apart.
    /// </summary>
    private static double PurposeLadderStep(ZoneKind purpose) => ((int)purpose % 3) * 1.5;

    /// <summary>
    /// The reference-pixel size <c>Main.DrawRoomIcon</c> draws a room's purpose
    /// glyph at. <see cref="LabelTop"/> also uses it as the vertical span
    /// between the icon's own top and the caption baseline directly under it,
    /// because that is what <c>Main.DrawRoomLabel</c> draws: the two shared one
    /// unnamed literal before Issue #139 F1, and a border cutting through both
    /// of them at once is what independent review found by naming the two
    /// separately at the same coordinate.
    /// </summary>
    public const double LabelIconSize = 8.0;

    /// <summary>
    /// Where a room's caption and icon start, in reference pixels below the
    /// anchor cell's top edge, absent any reason to move them — <c>Main.cs</c>'s
    /// original, unconditional position from before Issue #139 gave the border
    /// underneath a reason to move.
    /// </summary>
    public const double LabelDefaultTop = 2.0;

    /// <summary>
    /// A reference pixel of daylight kept between a room's border stroke and
    /// its caption/icon block, past the stroke's own half-width — the same
    /// shape of margin <see cref="WallClearance"/> keeps against the wall's
    /// own drawn band — so a border pushed deep by
    /// <see cref="BorderInset(ZoneKind, WallNeighbors)"/> cannot read as cutting
    /// through the glyphs sitting next to it.
    /// </summary>
    private const double LabelClearanceGap = 1.0;

    /// <summary>
    /// How far below the anchor cell's top edge a room's caption and icon may
    /// start, in reference pixels, given the inset the room's own border is
    /// actually drawn at — what <see cref="BorderInsetFor"/> gave
    /// <c>Main.DrawRoomBorder</c> for the same room, passed in as
    /// <paramref name="borderInset"/> rather than recomputed here, so the two can
    /// never read a different inset for one room than the other.
    ///
    /// Below <see cref="LabelDefaultTop"/> only when the border's own stroke
    /// band — <paramref name="borderInset"/> ± the stroke's half-width — would
    /// otherwise reach into it. Issue #139 F1: independent review found the
    /// deepened wall-adjacent ladder cutting straight through the caption and
    /// icon of every room checkpoint 2's own evidence screenshotted, because
    /// the label was still drawn at the fixed pre-#139 position and knew
    /// nothing of how far the border underneath it had moved.
    /// </summary>
    public static double LabelTop(double borderInset) =>
        Math.Max(LabelDefaultTop, borderInset + BorderStrokeHalfWidth + LabelClearanceGap);

    /// <summary>
    /// The closed outline of a room, as the boundary edges of its patch — one
    /// segment per cell side whose neighbour is outside the room, moved inward by
    /// <paramref name="inset"/> and trimmed or extended at its ends so the corners
    /// meet exactly.
    ///
    /// Holes are handled by construction: an enclosed cell that is not part of the
    /// room produces four inner edges, and they are drawn like any other boundary.
    /// A room made of two touching cells therefore has six segments and not eight.
    ///
    /// Flat cell geometry is correct here and nowhere else in this file needs
    /// saying twice: a zone may only be painted on passable tiles (contract 4.4),
    /// so no cell of a room is ever rock, and rock is the only thing on this map
    /// with volume to reach out of its footprint.
    /// </summary>
    public static IReadOnlyList<ViewSegment> Border(
        IReadOnlyCollection<GridPoint> tiles,
        int tileSize,
        double inset) =>
        BorderEdges(tiles, tileSize, inset).Select(edge => edge.Segment).ToArray();

    /// <summary>
    /// The same outline, with each piece still knowing which cell and which side
    /// of that cell it came from.
    ///
    /// <see cref="Border"/> is what drawing needs — a bag of lines. This is what
    /// a check needs: "is this line clear of the wall next to it" is a question
    /// about one side of one cell, and a flat list of segments has thrown away
    /// which side of which cell each one is. Reconstructing that in the test
    /// would be a second copy of <see cref="Edge"/>'s trimming rules, which is
    /// the one thing a check of this geometry must not be.
    /// </summary>
    public static IReadOnlyList<RoomBorderEdge> BorderEdges(
        IReadOnlyCollection<GridPoint> tiles,
        int tileSize,
        double inset)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var room = tiles as IReadOnlySet<GridPoint> ?? tiles.ToHashSet();
        var edges = new List<RoomBorderEdge>();
        foreach (var cell in tiles)
        {
            foreach (var side in Sides)
            {
                if (room.Contains(Step(cell, side.Normal)))
                {
                    continue;
                }

                edges.Add(new RoomBorderEdge(
                    cell,
                    side.Facing,
                    Edge(room, cell, side, tileSize, inset)));
            }
        }

        return edges;
    }

    /// <summary>
    /// The part of a room's outline that belongs to one of the two passes it is
    /// drawn in (Issue #156). The two layers partition
    /// <see cref="BorderPieces"/> exactly: every piece is in one of them and no
    /// piece is in both, so splitting the border cannot quietly lose a line.
    /// </summary>
    public static IReadOnlyList<ViewSegment> Border(
        IReadOnlyCollection<GridPoint> tiles,
        int tileSize,
        double inset,
        IReadOnlySet<GridPoint> wallTiles,
        RoomBorderLayer layer) =>
        BorderPieces(tiles, tileSize, inset, wallTiles)
            .Where(piece => piece.Layer == layer)
            .Select(piece => piece.Segment)
            .ToArray();

    /// <summary>
    /// The whole outline, cut where the pass it is drawn in changes, with each
    /// piece still knowing which cell and which side of that cell it came from.
    ///
    /// <para>
    /// <b>Why pieces and not edges.</b> The first version of Issue #156 classified
    /// a whole boundary edge at a time, and independent review found what that
    /// costs at a corner: on <c>quarters@19,2</c> the south edge of the cell with a
    /// wall in front stayed above the depth pass while the west edge meeting it —
    /// covered by that same wall only along its lower few pixels — went below and
    /// was cut off by it. The two ends no longer met, and the outline of the room
    /// opened at one corner while the opposite corner stayed shut. ADR 0013 and
    /// Issue #52 bought exactly the property that broke: a room is <em>one line
    /// around the whole patch</em>, not a frame per cell. The gap was
    /// <c>8.625 − (inset + 1.0)</c> reference pixels — 5.0 on quarters, 0.5 on the
    /// kitchen — and it was a limit of the granularity, not of the decision.
    /// </para>
    ///
    /// <para>
    /// So the cut is made where the answer changes rather than per edge. The
    /// covered tail of a vertical edge now goes above the depth pass with the
    /// horizontal edge it meets, and the corner closes.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RoomBorderPiece> BorderPieces(
        IReadOnlyCollection<GridPoint> tiles,
        int tileSize,
        double inset,
        IReadOnlySet<GridPoint> wallTiles)
    {
        ArgumentNullException.ThrowIfNull(wallTiles);
        var half = BorderStrokeHalfWidth * CameraView.WorldVisualScale(tileSize);
        var pieces = new List<RoomBorderPiece>();
        foreach (var edge in BorderEdges(tiles, tileSize, inset))
        {
            var stroke = StrokeBand(edge.Segment, half);
            var bands = WallBandsInFrontOf(edge.Cell, stroke, wallTiles, tileSize);
            foreach (var (segment, layer) in Split(edge.Segment, stroke, bands))
            {
                pieces.Add(new RoomBorderPiece(edge.Cell, edge.Side, segment, layer));
            }
        }

        return pieces;
    }

    /// <summary>
    /// Which pass one piece of stroke is drawn in — the whole of Issue #156's
    /// decision, as one expression everything else reads.
    ///
    /// <para>
    /// A piece a wall in front paints over completely may be drawn after the depth
    /// pass, because nothing it lands on is visible anyway; a piece that is not
    /// must be drawn before it, or it lands on whoever is standing there.
    /// </para>
    ///
    /// <para>
    /// Coverage is asked of the <em>union</em> of the wall's bands, not of any one
    /// of them — the first version asked for a single band and answered
    /// <c>UnderBodies</c> for the kitchen, whose south stroke straddles the
    /// boundary between the wall's lifted top mass and the bright seam along it,
    /// with both of them painting over it. Answering <c>UnderBodies</c> wrongly
    /// costs a piece a wall clips; answering <c>OverWallInFront</c> wrongly costs
    /// the whole issue.
    /// </para>
    /// </summary>
    public static RoomBorderLayer LayerOf(
        ViewRect strokePiece,
        IReadOnlyList<ViewRect> wallBandsInFront) =>
        wallBandsInFront.Count > 0 && IsCoveredBy(strokePiece, wallBandsInFront)
            ? RoomBorderLayer.OverWallInFront
            : RoomBorderLayer.UnderBodies;

    /// <summary>
    /// Every rectangle painted by the walls that are drawn <em>after</em> a body
    /// standing on <paramref name="cell"/> and can reach
    /// <paramref name="stroke"/>: the row directly south of it, and nothing else.
    ///
    /// <para>
    /// South is the one direction that qualifies.
    /// <see cref="WorldRenderGeometry"/> anchors a wall at the bottom of its own
    /// footprint and a body at its interpolated centre, and a body can never
    /// interpolate into rock, so a wall at <c>(x, y + 1)</c> is always behind a
    /// body at <c>(x, y)</c> in the Y-order and always covers it. A wall to the
    /// north hangs its facade into the cell too — and is drawn <em>before</em> the
    /// body, which walks over it — so its band is no shelter at all. A wall further
    /// south is drawn later still, but its mass reaches only
    /// <see cref="WallRenderGeometry.FacadeReferenceHeight"/> plus half a seam
    /// above its own footprint and cannot reach this row.
    /// </para>
    ///
    /// <para>
    /// The bands are measured with <see cref="WallRenderGeometry.DrawnBands"/>
    /// rather than derived from a mechanism, for the reason Issue #147 had to learn
    /// twice: a wall is its rectangles <em>plus</em> the bands its seams are
    /// painted as.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ViewRect> WallBandsInFrontOf(
        GridPoint cell,
        ViewRect stroke,
        IReadOnlySet<GridPoint> wallTiles,
        int tileSize)
    {
        ArgumentNullException.ThrowIfNull(wallTiles);
        var row = cell.Y + 1;
        var first = (int)Math.Floor(stroke.X / tileSize) - 1;
        var last = (int)Math.Floor((stroke.X + stroke.Width) / tileSize) + 1;
        var bands = new List<ViewRect>();
        for (var column = first; column <= last; column++)
        {
            var wall = new GridPoint(column, row);
            if (!wallTiles.Contains(wall))
            {
                continue;
            }

            bands.AddRange(WallRenderGeometry.DrawnBands(
                wall,
                WallTopology.SelectVariant(wall, wallTiles),
                tileSize));
        }

        return bands;
    }

    /// <summary>
    /// One boundary segment cut into the runs that answer <see cref="LayerOf"/> the
    /// same way, in order along the segment.
    ///
    /// The cut points are the wall bands' own boundaries along the segment's axis,
    /// so within each elementary interval no band starts or ends and the answer is
    /// constant — the same argument <see cref="IsCoveredBy"/> makes across two axes,
    /// used here along one. Neighbouring intervals with the same answer are merged,
    /// so a segment nothing changes across comes back as itself.
    /// </summary>
    private static IEnumerable<(ViewSegment Segment, RoomBorderLayer Layer)> Split(
        ViewSegment segment,
        ViewRect stroke,
        IReadOnlyList<ViewRect> bands)
    {
        var horizontal = stroke.Width >= stroke.Height;
        var low = horizontal ? stroke.X : stroke.Y;
        var high = low + (horizontal ? stroke.Width : stroke.Height);
        if (high - low <= Tolerance)
        {
            yield break;
        }

        var cuts = Cuts(
            low,
            high,
            bands.SelectMany(band => horizontal
                ? new[] { band.X, band.X + band.Width }
                : new[] { band.Y, band.Y + band.Height }));

        var runStart = low;
        var runLayer = (RoomBorderLayer?)null;
        for (var index = 0; index + 1 < cuts.Count; index++)
        {
            var from = cuts[index];
            var to = cuts[index + 1];
            if (to - from <= Tolerance)
            {
                continue;
            }

            var layer = LayerOf(
                horizontal
                    ? new ViewRect(from, stroke.Y, to - from, stroke.Height)
                    : new ViewRect(stroke.X, from, stroke.Width, to - from),
                bands);
            if (runLayer is { } current && current != layer)
            {
                yield return (Piece(segment, horizontal, runStart, from), current);
                runStart = from;
            }

            runLayer = layer;
        }

        yield return (
            Piece(segment, horizontal, runStart, high),
            runLayer ?? RoomBorderLayer.UnderBodies);
    }

    private static ViewSegment Piece(
        ViewSegment segment,
        bool horizontal,
        double from,
        double to) =>
        horizontal
            ? new ViewSegment(
                new ViewPoint(from, segment.From.Y),
                new ViewPoint(to, segment.To.Y))
            : new ViewSegment(
                new ViewPoint(segment.From.X, from),
                new ViewPoint(segment.To.X, to));

    /// <summary>
    /// Whether every pixel of <paramref name="target"/> is inside at least one of
    /// <paramref name="bands"/>.
    ///
    /// The rectangles overlap, so this cannot be asked of one of them at a time.
    /// It is answered exactly rather than by sampling: cutting the target at every
    /// band boundary that falls inside it leaves a grid of pieces, each of which is
    /// wholly inside a band or wholly outside every one of them, so testing the
    /// centre of each piece decides the whole piece.
    ///
    /// It is public because a check has to be able to ask the same question of a
    /// wall set it chose itself. The <em>decision</em> — which walls count as being
    /// drawn in front of a body — stays in <see cref="WallBandsInFrontOf"/> and
    /// <see cref="LayerOf"/>; this is only the geometry underneath them, and
    /// sharing the geometry is what stops a check re-deriving a sweep and getting
    /// it subtly different.
    /// </summary>
    public static bool IsCoveredBy(ViewRect target, IReadOnlyList<ViewRect> bands)
    {
        var xs = Cuts(
            target.X,
            target.X + target.Width,
            bands.SelectMany(band => new[] { band.X, band.X + band.Width }));
        var ys = Cuts(
            target.Y,
            target.Y + target.Height,
            bands.SelectMany(band => new[] { band.Y, band.Y + band.Height }));

        for (var column = 0; column + 1 < xs.Count; column++)
        {
            for (var row = 0; row + 1 < ys.Count; row++)
            {
                if (xs[column + 1] - xs[column] <= Tolerance ||
                    ys[row + 1] - ys[row] <= Tolerance)
                {
                    continue;
                }

                var x = (xs[column] + xs[column + 1]) / 2.0;
                var y = (ys[row] + ys[row + 1]) / 2.0;
                if (!bands.Any(band =>
                        x >= band.X && x <= band.X + band.Width &&
                        y >= band.Y && y <= band.Y + band.Height))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static IReadOnlyList<double> Cuts(
        double low,
        double high,
        IEnumerable<double> boundaries) =>
        boundaries
            .Where(value => value > low && value < high)
            .Append(low)
            .Append(high)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

    /// <summary>
    /// The rectangle a border segment is actually painted as: the line widened by
    /// <see cref="BorderStrokeHalfWidth"/> across itself, the same way
    /// <see cref="WallRenderGeometry.DrawnBands"/> widens a wall seam. A line is
    /// not a line on screen, and every question about what a border covers or is
    /// covered by is a question about this band.
    /// </summary>
    public static ViewRect StrokeBand(ViewSegment segment, double halfStroke)
    {
        var left = Math.Min(segment.From.X, segment.To.X);
        var right = Math.Max(segment.From.X, segment.To.X);
        var top = Math.Min(segment.From.Y, segment.To.Y);
        var bottom = Math.Max(segment.From.Y, segment.To.Y);
        return right - left >= bottom - top
            ? new ViewRect(left, top - halfStroke, right - left, (bottom - top) + (2.0 * halfStroke))
            : new ViewRect(left - halfStroke, top, (right - left) + (2.0 * halfStroke), bottom - top);
    }

    /// <summary>
    /// The slack allowed when a piece of the target is too thin to be a piece at
    /// all. Both sides are built from the same tile size and the same reference
    /// pixel, so the only difference this absorbs is the last bit of a double.
    /// </summary>
    private const double Tolerance = 1e-9;

    /// <summary>
    /// Where the caption and the icon of a room go: the top-left corner of the
    /// tile the room is named after — the first of its cells in reading order,
    /// which is the same tile <c>PrototypeRooms.Identify</c> builds the id from.
    ///
    /// The caption and the identity therefore point at one cell rather than two,
    /// which is what lets a player who reads <c>trainingGround@10,2</c> in a
    /// structured run find the caption on the map without a second lookup.
    /// </summary>
    public static ViewPoint LabelAnchor(IReadOnlyList<GridPoint> perimeter, int tileSize)
    {
        ArgumentNullException.ThrowIfNull(perimeter);
        if (perimeter.Count == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(perimeter),
                "A room with no cells cannot exist: it is derived from the cells.");
        }

        return CameraView.CellTopLeft(perimeter.Min(), tileSize);
    }

    /// <summary>
    /// The smallest rectangle covering the room, used to place things relative to
    /// the whole of it rather than to one cell.
    /// </summary>
    public static ViewRect Bounds(IReadOnlyCollection<GridPoint> tiles, int tileSize)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Count == 0)
        {
            return new ViewRect(0, 0, 0, 0);
        }

        var left = tiles.Min(cell => cell.X) * (double)tileSize;
        var top = tiles.Min(cell => cell.Y) * (double)tileSize;
        var right = (tiles.Max(cell => cell.X) + 1) * (double)tileSize;
        var bottom = (tiles.Max(cell => cell.Y) + 1) * (double)tileSize;
        return new ViewRect(left, top, right - left, bottom - top);
    }

    private readonly record struct Side(GridPoint Normal, GridPoint Tangent, WallNeighbors Facing);

    /// <summary>
    /// The four sides of a cell as an outward normal and the direction the edge
    /// runs in. The tangent is the "second" end; the "first" end is its opposite.
    /// </summary>
    private static readonly Side[] Sides =
    [
        new(new GridPoint(0, -1), new GridPoint(1, 0), WallNeighbors.North),
        new(new GridPoint(1, 0), new GridPoint(0, 1), WallNeighbors.East),
        new(new GridPoint(0, 1), new GridPoint(1, 0), WallNeighbors.South),
        new(new GridPoint(-1, 0), new GridPoint(0, 1), WallNeighbors.West),
    ];

    /// <summary>
    /// One boundary edge, inset and with both ends resolved.
    ///
    /// An end meets one of three situations, and each has exactly one right
    /// answer if the outline is to close:
    ///
    /// <list type="bullet">
    /// <item>the cell along the edge is outside the room — the border turns here,
    /// so the end is pulled back by the inset to meet the edge it turns
    /// into;</item>
    /// <item>that cell is inside and its own neighbour across this edge is inside
    /// too — an inner corner, so the end is pushed out by the inset to meet the
    /// edge coming the other way;</item>
    /// <item>otherwise the edge simply continues into its neighbour's, and the end
    /// stays on the grid line.</item>
    /// </list>
    /// </summary>
    private static ViewSegment Edge(
        IReadOnlySet<GridPoint> room,
        GridPoint cell,
        Side side,
        int tileSize,
        double inset)
    {
        var origin = CameraView.CellTopLeft(cell, tileSize);
        var far = new ViewPoint(origin.X + tileSize, origin.Y + tileSize);

        // The line the edge sits on once it has been moved inward.
        var x = side.Normal.X switch
        {
            > 0 => far.X - inset,
            < 0 => origin.X + inset,
            _ => double.NaN,
        };
        var y = side.Normal.Y switch
        {
            > 0 => far.Y - inset,
            < 0 => origin.Y + inset,
            _ => double.NaN,
        };

        var first = Trim(room, cell, side, Negate(side.Tangent), inset);
        var second = Trim(room, cell, side, side.Tangent, inset);

        if (double.IsNaN(x))
        {
            // A horizontal edge: it spans the cell in x and sits at the computed y.
            return new ViewSegment(
                new ViewPoint(origin.X - first, y),
                new ViewPoint(far.X + second, y));
        }

        return new ViewSegment(
            new ViewPoint(x, origin.Y - first),
            new ViewPoint(x, far.Y + second));
    }

    private static double Trim(
        IReadOnlySet<GridPoint> room,
        GridPoint cell,
        Side side,
        GridPoint towards,
        double inset)
    {
        var along = Step(cell, towards);
        if (!room.Contains(along))
        {
            return -inset;
        }

        return room.Contains(Step(along, side.Normal)) ? inset : 0;
    }

    private static GridPoint Step(GridPoint cell, GridPoint offset) =>
        new(cell.X + offset.X, cell.Y + offset.Y);

    private static GridPoint Negate(GridPoint offset) => new(-offset.X, -offset.Y);
}
