using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>Which kind of body a world label belongs to.</summary>
public enum WorldLabelKind
{
    /// <summary>A creature of the domain.</summary>
    Creature,

    /// <summary>A raider.</summary>
    Raider,
}

/// <summary>
/// A body, named the same way by every part of this layout. It is a value rather
/// than a bare id because the two populations number themselves independently:
/// creature 3 and raider 3 are different bodies, and a layout that mixed them
/// would put one label over the other on purpose.
/// </summary>
public readonly record struct WorldLabelSubject(WorldLabelKind Kind, int Id);

/// <summary>
/// Why a label is on screen at all. The order of the members is the order the
/// layout serves them in, and therefore the order in which a label that cannot be
/// placed is given up: the body under the cursor is what the player is asking
/// about right now, and the raider nobody is pointing at is the one that can wait
/// for the crowd to thin.
/// </summary>
public enum WorldLabelRank
{
    /// <summary>The body under the pointer.</summary>
    Hovered,

    /// <summary>The body the inspector is pointed at.</summary>
    Selected,

    /// <summary>
    /// A returning raider carrying a past encounter — the one the caption of slice
    /// 5 exists for. It outranks a returner without one because its caption is the
    /// only one that says something the roster cannot.
    /// </summary>
    ReturningWithStory,

    /// <summary>A returning raider nobody reached last time.</summary>
    Returning,
}

/// <summary>One line of a world label, and how large it is drawn.</summary>
/// <param name="Text">What it says.</param>
/// <param name="TextSizeRef">Its size in reference pixels.</param>
public sealed record WorldLabelLine(string Text, double TextSizeRef);

/// <summary>
/// A label asking to be placed: whose it is, where its body's head is, what it
/// says and how badly it wants a spot.
/// </summary>
/// <param name="Subject">The body it belongs to.</param>
/// <param name="Head">
/// The point the label is attached to, in world pixels: horizontally the body's
/// render centre, vertically a little above the top of its drawn pixels.
/// </param>
/// <param name="Lines">Its lines, topmost first.</param>
/// <param name="Rank">Why it is on screen.</param>
/// <param name="Order">
/// Scene order inside one rank, so the layout is a function of the snapshot and
/// not of the order the bodies happened to be listed in.
/// </param>
public sealed record WorldLabelRequest(
    WorldLabelSubject Subject,
    ViewPoint Head,
    IReadOnlyList<WorldLabelLine> Lines,
    WorldLabelRank Rank,
    int Order);

/// <summary>
/// Where one label ended up.
/// </summary>
/// <param name="Request">What asked to be placed.</param>
/// <param name="Lines">
/// The lines actually drawn — the request's, or as many of them from the top as
/// there was room for.
///
/// <para><b>The name is what survives.</b> A caption's first line is «the half the
/// player is meant to recognise at a glance, and the half below it is the half he
/// reads when he has stopped to look» (<see cref="ReturningHeroLabel.Name"/>). The
/// second line is four tiles wide — it is a sentence — and on the frame six
/// returning raiders walk into two cells of one corridor there is no honest place
/// to put six of those. So a caption that will not fit whole is laid without its
/// last line rather than moved somewhere it no longer belongs to anybody, and the
/// sentence the player would have stopped to read is in the panel that opens when
/// he stops: <see cref="InspectorText.Raider"/> carries the same words.</para>
/// </param>
/// <param name="Box">The rectangle it occupies, in world pixels.</param>
/// <param name="Alignment">Which side of the head it took.</param>
/// <param name="AttachmentRef">
/// How far the box is from the head it belongs to, in reference pixels: the
/// distance from <see cref="WorldLabelRequest.Head"/> to the nearest point of
/// <see cref="Box"/>. Zero means the head is on the box's own edge.
/// </param>
public sealed record PlacedWorldLabel(
    WorldLabelRequest Request,
    IReadOnlyList<WorldLabelLine> Lines,
    ViewRect Box,
    WorldLabelSide Alignment,
    double AttachmentRef)
{
    /// <summary>
    /// Where the engine is handed each line: the left edge of the box at the
    /// baseline of that line. The adapter draws the text centred inside
    /// <see cref="ViewRect.Width"/>, so nothing it draws can leave the box the
    /// collisions were resolved against.
    /// </summary>
    public IReadOnlyList<ViewPoint> LineOrigins { get; } = BaselinesOf(Lines, Box);

    // The box top plus the ascent of the first line is where the first baseline
    // was measured to be; every line after it is one line height down. Both
    // numbers come from WorldLabelLayout, so a box and the baselines inside it
    // cannot drift apart.
    private static IReadOnlyList<ViewPoint> BaselinesOf(
        IReadOnlyList<WorldLabelLine> lines,
        ViewRect box)
    {
        var scale = WorldLabelLayout.ScaleOf(lines, box);
        var baseline = box.Y + (WorldLabelLayout.AscentRef(lines[0]) * scale);
        var origins = new List<ViewPoint>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            origins.Add(new ViewPoint(box.X, baseline));
            baseline += WorldLabelLayout.LineHeightRef * scale;
        }

        return origins;
    }
}

/// <summary>
/// What the player is pointing at and what the inspector is pointed at, named the
/// same way for both populations. Before Issue #364 only creatures could be
/// either: the pointer read <c>_state.Creatures</c> and nothing else, so a raider
/// could be neither hovered nor selected nor inspected — the owner's second
/// finding on the playtest of 2026-08-10, «Враги, кстати вообще не выбираются и
/// при наведении ничего нет».
/// </summary>
public sealed record WorldLabelFocus(
    WorldLabelSubject? Hovered,
    WorldLabelSubject? Selected)
{
    /// <summary>Nothing pointed at and nothing selected.</summary>
    public static WorldLabelFocus None { get; } = new(null, null);
}

/// <summary>
/// The ring round the body the inspector is pointed at (Issue #364, point 3 of the
/// addendum of 2026-08-10).
///
/// <para><b>Why the rule is here and not in the adapter.</b> It was in the adapter,
/// and only for one of the two populations: <c>Main.DrawCreatureInformation</c>
/// drew a ring when <c>_selectedCreatureId</c> matched, and
/// <c>DrawRaiderInformation</c> had no such branch — until this Issue a raider
/// could not be selected at all, so there was nothing for it to draw. Copying the
/// four lines into the second routine would have left two statements of one
/// decision in a file no CI job builds. The rule and its numbers live here
/// instead and both routines read them, which is the seam
/// <see cref="ReturningHeroLabel"/> is on and for the same reason (ADR 0011).</para>
///
/// <para>The numbers are the creature ring's own, unchanged. So a selected
/// creature is drawn exactly as it was before this Issue, and a selected raider is
/// drawn exactly like a selected creature — «так же однозначно» is the criterion,
/// and identical geometry is the strongest reading of it available.</para>
/// </summary>
public static class WorldSelectionMark
{
    /// <summary>
    /// Whether this body carries the ring.
    ///
    /// <para>Selection only, and deliberately not hover: the pointer already
    /// answers with a label over the head, and a ring that followed the cursor
    /// would blink round every body it crossed on the way. The two questions are
    /// «which one am I asking about» and «which one have I chosen», and only the
    /// second one is worth a mark that stays.</para>
    /// </summary>
    public static bool IsRinged(WorldLabelSubject body, WorldLabelFocus focus)
    {
        ArgumentNullException.ThrowIfNull(focus);
        return focus.Selected == body;
    }

    /// <summary>How far from the body's render centre the ring sits, in reference pixels.</summary>
    public const double RadiusRef = 10.0;

    /// <summary>How thick the ring is drawn, in reference pixels.</summary>
    public const double StrokeRef = 2.0;

    /// <summary>How many segments the arc is drawn with.</summary>
    public const int Segments = 16;

    /// <summary>The colour of the ring.</summary>
    public const string Color = "#ffffff";
}

/// <summary>Which side of the head a label took.</summary>
public enum WorldLabelSide
{
    /// <summary>Straddling the head, which is where a label goes when it can.</summary>
    Centre,

    /// <summary>Ending just left of the head's column.</summary>
    Left,

    /// <summary>Starting just right of the head's column.</summary>
    Right,
}

/// <summary>
/// The one place that decides where every world label of a frame goes — the name
/// of a creature, the name of a returning raider and the line about his last
/// visit — and the only place that can, because it is the only one that sees them
/// all at once (Issue #364).
///
/// <para><b>Why one place and not two.</b> Until this existed the two populations
/// were laid out by code that could not see each other: a creature's name was a
/// <c>Label</c> node positioned by a formula from its body's centre, and a
/// raider's caption was a <c>DrawString</c> placed by a band counter. Neither knew
/// the other existed, so a collision <em>between</em> them was not resolvable in
/// either — which is the shape of the owner's complaint on the playtest of
/// 2026-08-10: «имена своих же тоже наползают».</para>
///
/// <para><b>The technique, and the ones it was chosen over.</b> A label tries the
/// places around its own head in order of how near they are, and takes the first
/// free one; if every place inside the limit is taken, it is not drawn at all.
/// That is the point-feature rule of cartography — candidate positions around the
/// anchor, tested outward, an explicit ceiling on how far the label may go — with
/// the leader line deliberately left out, and the "priority plus suppression"
/// half of automatic label placement bolted on for the case where the ceiling
/// makes every candidate impossible. The comparison it came out of, with the five
/// rejected alternatives, is «Подписи над телами в мире» in
/// <c>docs/product/REFERENCES.md</c>. The technique it replaces — «каждому
/// следующему строка вверх, без потолка» — is named there as the source of the
/// defect rather than as an option.</para>
/// </summary>
public static class WorldLabelLayout
{
    /// <summary>The tile the reference geometry of this assembly is authored against.</summary>
    public const double ReferenceTileSize = 22.0;

    /// <summary>
    /// How far above the top of a body's drawn pixels the label is attached, in
    /// reference pixels.
    ///
    /// <para>Above and not below, because below the render centre is where the HP
    /// bar (+8 for a creature, +9 for a raider) and the damage numbers already
    /// are. The clearance is small on purpose: every reference pixel of air here
    /// is a reference pixel the label is further from the body it names.</para>
    /// </summary>
    public const double HeadClearanceRef = 4.0;

    /// <summary>
    /// The furthest a label may end up from its own head, in reference pixels,
    /// measured from the head to the nearest point of the label's box.
    ///
    /// <para><b>One tile, and that is a measurement rather than a taste.</b> Bodies
    /// stand on cells, so two neighbours are exactly one tile apart. A label that
    /// has moved further than a tile from its own head is nearer to somebody
    /// else's head than to its own, and from that moment the frame no longer says
    /// whose it is — which is the whole of what a name over a body is for. The
    /// technique this replaces had no ceiling at all: <c>TopRefOf(slot)</c> lifted
    /// the n-th caption twenty reference pixels times n, so six returning raiders
    /// put a name six lines up in an empty corridor
    /// (<c>evidence/364-before.png</c>).</para>
    /// </summary>
    public const double MaximumAttachmentRef = ReferenceTileSize;

    /// <summary>
    /// How much a candidate rises above the one before it, in reference pixels: a
    /// quarter of a tile. Finer than a line, because two labels that miss each
    /// other by a hair should not be pushed a whole line apart, and coarse enough
    /// that the ladder inside <see cref="MaximumAttachmentRef"/> stays short.
    /// </summary>
    public const double RiseStepRef = ReferenceTileSize / 4.0;

    /// <summary>
    /// The air between the head's column and a label pushed to one side, in
    /// reference pixels. Small on purpose: a side-placed label is meant to read as
    /// leaning against its body, and the gap is what keeps it from touching the
    /// head it belongs to.
    /// </summary>
    public const double SideGapRef = 2.0;

    /// <summary>The distance between two baselines of one label, in reference pixels.</summary>
    public const double LineHeightRef = 8.0;

    /// <summary>
    /// How wide one glyph is, per reference pixel of text size — an <em>upper
    /// bound</em> of the fallback font's advance and not a fit.
    ///
    /// <para>The direction of the error is the point. Over-estimating a box can
    /// only make the layout more careful than it needed to be: it may push a label
    /// aside, or give one up, that would in fact have fitted. Under-estimating
    /// would let two labels the guard calls disjoint be drawn over each other,
    /// which is the defect this file exists to stop. So the constant is chosen
    /// above every measurement rather than through them.</para>
    ///
    /// <para>Measured rather than assumed: the adapter reports the engine's own
    /// <c>GetStringSize</c> beside this estimate for every label of the frame, in
    /// the <c>worldLabels</c> section of the run's view state, and
    /// <c>evidence/364-widths.json</c> is that report on the owner's scene.</para>
    /// </summary>
    public const double GlyphAdvanceRef = 0.72;

    /// <summary>
    /// How far above its baseline a line of text may reach, as a fraction of its
    /// size, and how far below. A whole size above and a quarter below are both
    /// deliberately generous, for the reason <see cref="GlyphAdvanceRef"/> is.
    /// </summary>
    public const double AscentFraction = 1.0;

    /// <inheritdoc cref="AscentFraction"/>
    public const double DescentFraction = 0.25;

    /// <summary>The size of a creature's own name line, in reference pixels.</summary>
    public const double CreatureNameTextRef = 10.0;

    /// <summary>Where a body's label is attached, in world pixels.</summary>
    public static ViewPoint HeadOf(ViewPoint renderCentre, int tileSize)
    {
        var body = CameraView.GoblinOpaqueRect(renderCentre, tileSize);
        return new ViewPoint(
            renderCentre.X,
            body.Y - (HeadClearanceRef * CameraView.WorldVisualScale(tileSize)));
    }

    /// <summary>The width of the widest line of a label, in reference pixels.</summary>
    public static double WidthRef(IReadOnlyList<WorldLabelLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Count == 0 ? 0.0 : lines.Max(WidthRef);
    }

    /// <inheritdoc cref="WidthRef(IReadOnlyList{WorldLabelLine})"/>
    public static double WidthRef(WorldLabelLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Text.Length * GlyphAdvanceRef * line.TextSizeRef;
    }

    /// <inheritdoc cref="AscentFraction"/>
    public static double AscentRef(WorldLabelLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.TextSizeRef * AscentFraction;
    }

    /// <summary>The height of a whole label, in reference pixels.</summary>
    public static double HeightRef(IReadOnlyList<WorldLabelLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Count == 0
            ? 0.0
            : AscentRef(lines[0]) +
                ((lines.Count - 1) * LineHeightRef) +
                (lines[^1].TextSizeRef * DescentFraction);
    }

    /// <summary>
    /// The world pixels one reference pixel of <paramref name="request"/> was
    /// scaled by, recovered from the box the layout produced. It exists so
    /// <see cref="PlacedWorldLabel"/> can find its baselines without carrying the
    /// tile size around beside the box.
    /// </summary>
    internal static double ScaleOf(IReadOnlyList<WorldLabelLine> lines, ViewRect box)
    {
        var height = HeightRef(lines);
        return height <= 0 ? 1.0 : box.Height / height;
    }

    /// <summary>
    /// Every label of this frame, placed. Labels are served in rank order and,
    /// inside a rank, in scene order; each takes the nearest free place to its own
    /// head, and one that finds none inside <see cref="MaximumAttachmentRef"/> is
    /// left out of the result entirely.
    ///
    /// <para><b>Names first, sentences after — and that is two passes, not a
    /// tidier way of writing one.</b> The second line of a returning raider's
    /// caption is a sentence: «волна 2 · достали (23,7)» is a hundred reference
    /// pixels, nearly five tiles, against thirty for the name above it. Laid
    /// greedily — each label taking its whole text before the next one is
    /// considered — one such caption fills the places of five neighbours, and on
    /// the owner's own wave-4 frame that is measured, not supposed: three names of
    /// six survived and three were given up. So the first pass places every label
    /// as its name alone, and only then does the second pass grow each one back to
    /// its full text where there is still room beside it. A name is never
    /// outbid by somebody else's sentence, and a sentence never appears anywhere
    /// but directly under the name it belongs to.</para>
    ///
    /// <para>That ordering is what makes the suppression of «приём 5» land last:
    /// a label disappears only after the shortened form has been tried too, which
    /// is the difference between a name that could have fitted and a name nobody
    /// sees.</para>
    /// </summary>
    public static IReadOnlyList<PlacedWorldLabel> Place(
        IEnumerable<WorldLabelRequest> requests,
        int tileSize)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var scale = CameraView.WorldVisualScale(tileSize);
        var placed = new List<PlacedWorldLabel>();
        foreach (var request in requests
                     .OrderBy(request => request.Rank)
                     .ThenBy(request => request.Order)
                     .ThenBy(request => request.Subject.Kind)
                     .ThenBy(request => request.Subject.Id))
        {
            if (request.Lines.Count > 0 &&
                Fit(request, request.Lines.Take(1).ToArray(), placed, null, scale) is { } named)
            {
                placed.Add(named);
            }
        }

        for (var index = 0; index < placed.Count; index++)
        {
            var request = placed[index].Request;
            for (var count = request.Lines.Count; count > placed[index].Lines.Count; count--)
            {
                if (Fit(request, request.Lines.Take(count).ToArray(), placed, index, scale)
                    is { } grown)
                {
                    placed[index] = grown;
                    break;
                }
            }
        }

        return placed;
    }

    /// <summary>
    /// The nearest free place for these lines, or <c>null</c> when every candidate
    /// inside the limit is taken.
    /// </summary>
    /// <param name="ignore">
    /// Which already-placed label is this one, when the caller is growing a label
    /// it has already put down. A label must not collide with the box it is about
    /// to give up.
    /// </param>
    private static PlacedWorldLabel? Fit(
        WorldLabelRequest request,
        IReadOnlyList<WorldLabelLine> lines,
        IReadOnlyList<PlacedWorldLabel> placed,
        int? ignore,
        double scale)
    {
        foreach (var candidate in Candidates)
        {
            var box = BoxOf(request.Head, lines, candidate, scale);
            var attachment = DistanceRef(request.Head, box, scale);
            if (attachment > MaximumAttachmentRef)
            {
                continue;
            }

            var taken = false;
            for (var other = 0; other < placed.Count && !taken; other++)
            {
                taken = other != ignore && Overlap(placed[other].Box, box);
            }

            if (!taken)
            {
                return new PlacedWorldLabel(request, lines, box, candidate.Side, attachment);
            }
        }

        return null;
    }

    /// <summary>
    /// The rectangle a label would take at one candidate place, in world pixels.
    /// </summary>
    private static ViewRect BoxOf(
        ViewPoint head,
        IReadOnlyList<WorldLabelLine> lines,
        WorldLabelCandidate candidate,
        double scale)
    {
        var width = WidthRef(lines) * scale;
        var height = HeightRef(lines) * scale;
        var descent = lines[^1].TextSizeRef * DescentFraction * scale;
        var bottom = head.Y + descent - (candidate.RiseRef * scale);
        var left = candidate.Side switch
        {
            WorldLabelSide.Left => head.X - (SideGapRef * scale) - width,
            WorldLabelSide.Right => head.X + (SideGapRef * scale),
            _ => head.X - (width / 2.0),
        };
        return new ViewRect(left, bottom - height, width, height);
    }

    /// <summary>
    /// How far the head is from the nearest point of the box, in reference pixels.
    /// Zero when the head lies on or inside the box, which is what the centred
    /// candidate with no rise gives.
    /// </summary>
    private static double DistanceRef(ViewPoint head, ViewRect box, double scale)
    {
        var dx = Math.Max(Math.Max(box.X - head.X, head.X - (box.X + box.Width)), 0.0);
        var dy = Math.Max(Math.Max(box.Y - head.Y, head.Y - (box.Y + box.Height)), 0.0);
        return Math.Sqrt((dx * dx) + (dy * dy)) / scale;
    }

    /// <summary>
    /// Whether two placed boxes share any pixel. Touching edges are not an
    /// overlap: two labels that end and begin on the same column are readable, and
    /// treating that as a collision would cost a place for nothing.
    /// </summary>
    public static bool Overlap(ViewRect one, ViewRect other) =>
        one.X < other.X + other.Width &&
        other.X < one.X + one.Width &&
        one.Y < other.Y + other.Height &&
        other.Y < one.Y + one.Height;

    private readonly record struct WorldLabelCandidate(WorldLabelSide Side, double RiseRef);

    /// <summary>
    /// The places a label may take, nearest first: straddling the head, then
    /// leaning left, then right, each of those at every rise up to
    /// <see cref="MaximumAttachmentRef"/>.
    ///
    /// <para>The order is what makes the layout deterministic and what makes it
    /// prefer attachment over freedom: a label only leaves the head's own column
    /// when the column is taken, and only rises when both the column and the sides
    /// are.</para>
    /// </summary>
    private static IReadOnlyList<WorldLabelCandidate> Candidates { get; } = BuildCandidates();

    private static IReadOnlyList<WorldLabelCandidate> BuildCandidates()
    {
        var rises = new List<double>();
        for (var rise = 0.0; rise <= MaximumAttachmentRef + 1e-9; rise += RiseStepRef)
        {
            rises.Add(rise);
        }

        return
        [
            .. rises.SelectMany(rise => new WorldLabelCandidate[]
            {
                new(WorldLabelSide.Centre, rise),
                new(WorldLabelSide.Left, rise),
                new(WorldLabelSide.Right, rise),
            }),
        ];
    }
}

/// <summary>
/// Which bodies of a frame are labelled, what their labels say, and which body a
/// map cell belongs to.
///
/// <para>It is separate from <see cref="WorldLabelLayout"/> because the two
/// answer different questions and are mutated by different defects: this one
/// decides <em>who</em> and <em>what</em>, the layout decides <em>where</em>. The
/// guard of Issue #364 reddens on each half through its own substitution.</para>
/// </summary>
public static class WorldLabels
{
    /// <summary>
    /// <b>Every</b> body standing on a cell, in the order clicking cycles through
    /// them: the crew first, by id, then the raiders, by id.
    ///
    /// <para><b>Every, and that word is the whole finding.</b> This used to answer
    /// with one body — the first creature on the cell, or failing that the first
    /// raider — and the reasoning attached to it, that a raider sharing a tile is
    /// «already answerable through the cell beside it», was false on the very frame
    /// this Issue is about. On tick 2380 of <c>baseline</c>/<c>20260729</c> four of
    /// the six captioned returners — «Секира», «Сиплый», «Ловчий» and «Косой» —
    /// stand on (15,7) together with the crew member «Тишина», so clicking that
    /// cell answered «Тишина» four times over and none of the four could be
    /// selected at all. The neighbouring cell does not help: (16,7) holds two
    /// <em>other</em> raiders.</para>
    ///
    /// <para>A raider that has left through the gate is not on the map and is
    /// skipped, exactly as the drawing does (<c>Main.Rendering.SceneRaiders</c>); a
    /// downed one is kept, because his body is still lying there and is still worth
    /// asking about.</para>
    /// </summary>
    public static IReadOnlyList<WorldLabelSubject> BodiesAt(
        PrototypeSnapshot state,
        GridPoint cell)
    {
        ArgumentNullException.ThrowIfNull(state);
        return
        [
            .. state.Creatures
                .Where(creature => creature.Position == cell)
                .OrderBy(creature => creature.Id)
                .Select(creature => new WorldLabelSubject(
                    WorldLabelKind.Creature,
                    creature.Id)),
            .. state.Raiders
                .Where(raider => raider.Position == cell && raider.Mode != RaiderMode.Escaped)
                .OrderBy(raider => raider.Id)
                .Select(raider => new WorldLabelSubject(WorldLabelKind.Raider, raider.Id)),
        ];
    }

    /// <summary>
    /// The body a fresh click on this cell answers with: the first of
    /// <see cref="BodiesAt"/>, or <c>null</c> for an empty cell.
    /// </summary>
    public static WorldLabelSubject? At(PrototypeSnapshot state, GridPoint cell) =>
        BodiesAt(state, cell) is [var first, ..] ? first : null;

    /// <summary>
    /// The body the next click on this cell selects, given what is selected now.
    ///
    /// <para><b>Clicking the same cell again takes the next body on it</b>, and
    /// round to the first again after the last. That is the mechanism chosen for
    /// «налётчик участвует в выборе наравне с существом владения» when several
    /// bodies share a tile, and it is the cheap canonical one: no new input, no
    /// modifier key, no list to open, and one click still selects on a cell that
    /// holds a single body. Clicking a <em>different</em> cell always starts from
    /// its first body, because the body selected a moment ago is not on it.</para>
    ///
    /// <para><b>The alternative it was chosen over</b> is a chooser: a click on a
    /// crowded cell opens a small list of the bodies standing there and the player
    /// picks one. It is better at four bodies — it says how many there are and
    /// takes one click instead of four — and it was rejected because it is a new
    /// piece of interface with its own placement, its own dismissal and its own
    /// collision problems on exactly the crowded frames it would appear on, which
    /// is the defect this Issue exists to fix arriving through a third door.
    /// Cycling costs the player up to one click per body on the tile and tells him
    /// nothing about how many there are until he has been round; that cost is paid
    /// down by the feedback line <see cref="SelectionHint"/> writes, which names
    /// the count.</para>
    /// </summary>
    public static WorldLabelSubject? NextAt(
        PrototypeSnapshot state,
        GridPoint cell,
        WorldLabelSubject? selected)
    {
        var bodies = BodiesAt(state, cell);
        if (bodies.Count == 0)
        {
            return null;
        }

        var current = selected is { } body ? IndexOf(bodies, body) : -1;
        return bodies[(current + 1) % bodies.Count];
    }

    /// <summary>
    /// Which body the pointer reports on this cell. The selected one when it is
    /// standing here, so that having cycled to «Ловчий» and then pointed at his
    /// tile does not answer «Тишина» again; otherwise the cell's first body.
    /// </summary>
    public static WorldLabelSubject? PointedAt(
        PrototypeSnapshot state,
        GridPoint cell,
        WorldLabelSubject? selected) =>
        selected is { } body && IndexOf(BodiesAt(state, cell), body) >= 0
            ? body
            : At(state, cell);

    /// <summary>
    /// What the feedback line says after a click, or <c>null</c> when the cell
    /// holds nothing worth a sentence — nobody, or one body, which is the case the
    /// player already understands without being told.
    ///
    /// <para>It exists because a mechanism nobody can discover is not a mechanism:
    /// four raiders on one tile look exactly like one raider on one tile, so
    /// without this line the second click reads as the map changing its mind.</para>
    /// </summary>
    public static string? SelectionHint(
        PrototypeSnapshot state,
        GridPoint cell,
        WorldLabelSubject? selected)
    {
        var bodies = BodiesAt(state, cell);
        if (bodies.Count < 2 || selected is not { } body)
        {
            return null;
        }

        var index = IndexOf(bodies, body);
        return index < 0
            ? null
            : $"({cell.X},{cell.Y}): {index + 1} of {bodies.Count} standing here; " +
                "click the cell again for the next one.";
    }

    private static int IndexOf(
        IReadOnlyList<WorldLabelSubject> bodies,
        WorldLabelSubject body)
    {
        for (var index = 0; index < bodies.Count; index++)
        {
            if (bodies[index] == body)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Every label this frame is asking for. A creature is named when the player
    /// is pointing at it or has selected it — the rule that has been in force
    /// since a name per body made the economy unreadable — and a returning raider
    /// is named always, because the caption is a claim about him the player is
    /// meant to recognise without hunting for it. The rule and the alternative it
    /// was chosen over are in «Подписи над телами в мире» of
    /// <c>docs/product/REFERENCES.md</c>.
    /// </summary>
    /// <param name="centreOf">
    /// Where a body is drawn this frame, in world pixels. The adapter passes the
    /// interpolated centre, so a label follows the body between two ticks instead
    /// of snapping a whole tile ahead of it; a check that states a scene rather
    /// than driving one leaves it out and gets the canonical cell centres.
    /// </param>
    public static IReadOnlyList<WorldLabelRequest> Requests(
        PrototypeSnapshot state,
        WorldLabelFocus focus,
        int tileSize,
        Func<WorldLabelSubject, ViewPoint>? centreOf = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(focus);
        var requests = new List<WorldLabelRequest>();
        var order = 0;
        foreach (var creature in state.Creatures
                     .OrderBy(creature => creature.Position.Y)
                     .ThenBy(creature => creature.Position.X)
                     .ThenBy(creature => creature.Id))
        {
            var subject = new WorldLabelSubject(WorldLabelKind.Creature, creature.Id);
            if (RankOf(subject, focus) is not { } rank)
            {
                continue;
            }

            requests.Add(new WorldLabelRequest(
                subject,
                HeadIn(subject, creature.Position, tileSize, centreOf),
                [CreatureLine(creature)],
                rank,
                order++));
        }

        foreach (var raider in state.Raiders
                     .OrderBy(raider => raider.Position.Y)
                     .ThenBy(raider => raider.Position.X)
                     .ThenBy(raider => raider.Id))
        {
            var subject = new WorldLabelSubject(WorldLabelKind.Raider, raider.Id);
            var lines = CaptionOf(raider);
            if (lines.Count == 0)
            {
                continue;
            }

            requests.Add(new WorldLabelRequest(
                subject,
                HeadIn(subject, raider.Position, tileSize, centreOf),
                lines,
                RankOf(subject, focus) ?? (ReturningHeroLabel.Story(raider) is null
                    ? WorldLabelRank.Returning
                    : WorldLabelRank.ReturningWithStory),
                order++));
        }

        return requests;
    }

    /// <summary>
    /// The lines of a raider's caption: the name, and the line about the last
    /// encounter when there was one. Both come from
    /// <see cref="ReturningHeroLabel"/> unchanged — Issue #364 moves the caption,
    /// it does not rewrite it.
    ///
    /// <para><b>A raider the domain has never met gets no world label at all, not
    /// even while the pointer is on him.</b> That is the decision of §8.2 of
    /// <c>docs/design/SLICE_05_RETURNING_HERO.md</c> left standing on purpose:
    /// every raider carries a name in the snapshot and only the returning one is
    /// named on screen, because the caption is a claim — «вот этого ты отпустил».
    /// The alternative, giving a hovered stranger the same label a hovered
    /// creature gets, was rejected here rather than left unnoticed: since Issue
    /// #364 the inspector answers "who is this" for any raider the player clicks,
    /// so the world does not have to, and changing §8.2 to buy something the panel
    /// already gives would be a product decision taken sideways.</para>
    /// </summary>
    public static IReadOnlyList<WorldLabelLine> CaptionOf(PrototypeRaiderSnapshot raider) =>
    [
        .. ReturningHeroLabel.Lines(raider)
            .Select((text, index) => new WorldLabelLine(
                text,
                index == 0
                    ? ReturningHeroLabel.NameTextRef
                    : ReturningHeroLabel.StoryTextRef)),
    ];

    /// <summary>
    /// Why this body is labelled, or <c>null</c> when it is not. Pointing at a
    /// body outranks having selected it, because the pointer is the question the
    /// player is asking right now.
    /// </summary>
    private static WorldLabelRank? RankOf(WorldLabelSubject subject, WorldLabelFocus focus) =>
        focus.Hovered == subject
            ? WorldLabelRank.Hovered
            : focus.Selected == subject
                ? WorldLabelRank.Selected
                : null;

    private static ViewPoint HeadIn(
        WorldLabelSubject subject,
        GridPoint cell,
        int tileSize,
        Func<WorldLabelSubject, ViewPoint>? centreOf) =>
        WorldLabelLayout.HeadOf(
            centreOf is null ? CameraView.CellCenter(cell, tileSize) : centreOf(subject),
            tileSize);

    /// <summary>
    /// Every line any body of this snapshot could carry, whether or not anything
    /// is pointed at. It exists to be measured: the adapter reports the engine's
    /// own width for each of these beside
    /// <see cref="WorldLabelLayout.GlyphAdvanceRef"/>'s estimate, and that report
    /// is what says whether the estimate is still the upper bound it claims to be.
    /// </summary>
    public static IReadOnlyList<WorldLabelLine> AllLines(PrototypeSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return
        [
            .. state.Creatures.Select(CreatureLine),
            .. state.Raiders.SelectMany(CaptionOf),
        ];
    }

    /// <summary>What a creature's own label says: who it is and what it is doing.</summary>
    public static WorldLabelLine CreatureLine(PrototypeCreatureSnapshot creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        return new WorldLabelLine(
            $"{creature.Name} {HudText.CreatureStateShort(creature)}",
            WorldLabelLayout.CreatureNameTextRef);
    }

    /// <inheritdoc cref="Requests"/>
    public static IReadOnlyList<PlacedWorldLabel> Of(
        PrototypeSnapshot state,
        WorldLabelFocus focus,
        int tileSize,
        Func<WorldLabelSubject, ViewPoint>? centreOf = null) =>
        WorldLabelLayout.Place(Requests(state, focus, tileSize, centreOf), tileSize);
}
