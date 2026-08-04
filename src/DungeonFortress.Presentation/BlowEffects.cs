namespace DungeonFortress.Presentation;

/// <summary>
/// What a blow looks like: how bright the flash is, where the number sits, what
/// colour each carries and how long the drawing holds still.
///
/// <para>
/// It is separate from <see cref="BlowReadout"/> on purpose. That one answers
/// <em>what happened</em> and is read off the canonical journal; this one answers
/// <em>how it reads</em> and is answerable with no journal at all. Keeping them
/// apart is what lets the mutant that switches the effects off leave the phase
/// intact, and it is the same split <c>SideOutline</c> and <c>MapAccents</c> already
/// use: the decision lives where the "Pure .NET" CI job can see it
/// (<see href="../../docs/decisions/0011-presentation-layer-without-engine.md">ADR
/// 0011</see>), and <c>Main.cs</c> multiplies and translates.
/// </para>
///
/// <para>
/// <b>Nothing here reaches the simulation.</b> Every number below changes pixels
/// and only pixels: no value enters the canonical snapshot, the checksum or the
/// command log, and <see cref="HitStopAlpha"/> in particular holds the
/// <em>drawing</em> of a tick, never the tick.
/// </para>
///
/// <para>
/// <b>Why every curve has a floor.</b> A blow lives for exactly the tick it was
/// recorded on, and <c>tickAlpha</c> runs from 0 to 1 across that tick. A curve
/// that reached zero at the end of it would be invisible in any frame drawn at
/// alpha 1 — which is every paused frame and every captured screenshot, because
/// <c>Main.MotionAlpha</c> answers 1 when the world is not being interpolated. So
/// the flash and the number fade towards a floor rather than to nothing: the
/// effect still moves while time runs, and it is still there when time stops.
/// </para>
/// </summary>
public static class BlowEffects
{
    /// <summary>How opaque the flash on a struck body is at the start of its tick.</summary>
    public const double FlashPeak = 0.85;

    /// <summary>And at the end of it. Above zero: see the class remark.</summary>
    public const double FlashFloor = 0.55;

    /// <summary>How opaque the damage number is at the start of its tick.</summary>
    public const double DamagePeak = 1.0;

    /// <summary>And at the end of it.</summary>
    public const double DamageFloor = 0.70;

    /// <summary>
    /// How far above the body's centre the damage number sits when the tick
    /// starts, in the reference pixels <c>Main.ScaleWorld</c> multiplies.
    /// </summary>
    public const double DamageBaseRef = 13.0;

    /// <summary>How much further it has drifted by the end of the tick.</summary>
    public const double DamageRiseRef = 7.0;

    /// <summary>
    /// The horizontal step between two numbers over one body. Two crew members
    /// striking the same raider on the same tick is ordinary — it happens twice in
    /// the first wave of the shipped <c>prepared</c> journal — and two numbers drawn
    /// at the same point would read as one wrong number.
    /// </summary>
    public const double DamageSlotRef = 11.0;

    /// <summary>
    /// The height of a damage number, in reference pixels. It is above the room
    /// caption's seven and the state abbreviation's, because it is read at a glance
    /// during a fight rather than studied.
    /// </summary>
    public const double DamageTextRef = 11.0;

    /// <summary>
    /// The dark rim drawn behind the glyph, and how wide it is. Without it a number
    /// lands on a goblin's own olive and gold and stops being readable at exactly
    /// the moment there is a fight to read — measured on the first captured frame
    /// of this change, where an amber "-5" over a goblin's chest could not be made
    /// out at all.
    /// </summary>
    public const string DamageOutlineColor = "#0b1220";

    /// <inheritdoc cref="DamageOutlineColor"/>
    public const double DamageOutlineRef = 1.4;

    /// <summary>The stroke width of the streak that says which way a blow travelled.</summary>
    public const double StreakWidthRef = 2.6;

    /// <summary>
    /// Where the streak starts and stops along the line between the two bodies.
    /// It is a segment in the middle rather than a line joining two centres: a
    /// stroke that reached either centre would be drawn across the faces it is
    /// meant to connect.
    /// </summary>
    public const double StreakStartShare = 0.38;

    /// <inheritdoc cref="StreakStartShare"/>
    public const double StreakEndShare = 0.86;

    // -----------------------------------------------------------------------
    // The contact effect (Issue #244)
    //
    // ADR 0020 asks the probe for "один эффект контакта" and leaves the choice
    // to whoever builds it. Three were on the table and the body is 61.8 px tall
    // at the shipped 40 px tile, which is what decides between them:
    //
    // * a splash of particles needs each particle to be a separate readable
    //   mark, and a mark under about 2 world px is a pixel of noise on this
    //   canvas -- at 61.8 px a believable splash would be a dozen of them;
    // * a trail behind the weapon needs the weapon's own path to be legible
    //   frame to frame, and the drawing holds still for HitStopShare of the tick
    //   at exactly the moment the trail would be at its most useful;
    // * a spark is one shape whose meaning is its direction. Rays leaving one
    //   point read as an impact at any size at which the rays themselves are
    //   visible, and SparkOuterRef below is 8.5 reference px -- 15.5 world px at
    //   the shipped tile, a quarter of the body -- against a stroke of 1.6, i.e.
    //   2.9 world px. Both are far above the pixel where a stroke stops being a
    //   stroke.
    //
    // So: a spark. It is also the only one of the three that survives the
    // capture rule every effect in this class is written to -- a paused frame and
    // a screenshot are drawn at alpha 1, and a shape has a floor while a path
    // does not.
    // -----------------------------------------------------------------------

    /// <summary>
    /// How many rays the contact spark has. Eight, evenly spaced, so the shape
    /// is a star rather than a direction: which way the blow travelled is the
    /// streak's job, and a spark that also pointed would say it twice and
    /// disagree with itself whenever the two were drawn a frame apart.
    /// </summary>
    public const int SparkRays = 8;

    /// <summary>
    /// How far a long ray reaches from the point of contact, in the reference
    /// pixels <c>Main.ScaleWorld</c> multiplies.
    /// </summary>
    public const double SparkOuterRef = 8.5;

    /// <summary>And a short one: every second ray, so the star has a rhythm.</summary>
    public const double SparkInnerRef = 4.5;

    /// <summary>How far from the contact point the rays start.</summary>
    public const double SparkCoreRef = 1.6;

    /// <summary>The stroke of a ray.</summary>
    public const double SparkWidthRef = 1.6;

    /// <summary>
    /// Where along the line from striker to struck the spark sits. Further along
    /// than the streak ends, because contact happens at the body that was hit and
    /// not between the two: a spark in the middle of the gap reads as something
    /// that missed.
    /// </summary>
    public const double SparkContactShare = 0.78;

    /// <summary>How bright the spark is when the blow lands, and as it closes.</summary>
    public const double SparkPeak = 0.95;

    /// <inheritdoc cref="SparkPeak"/>
    public const double SparkFloor = 0.35;

    /// <summary>
    /// How much a ray has grown by the end of the contact window, as a share of
    /// its length. A spark that only faded would read as a light going out; one
    /// that also opens reads as something giving way.
    /// </summary>
    public const double SparkGrowth = 0.55;

    /// <summary>Where the point of contact is, for a blow between two bodies.</summary>
    public static ViewPoint SparkAt(ViewPoint attacker, ViewPoint target) =>
        Along(attacker, target, SparkContactShare);

    /// <summary>The opacity of the spark, across the contact window.</summary>
    public static double SparkAlpha(double contactAlpha) =>
        Fade(SparkPeak, SparkFloor, contactAlpha);

    /// <summary>
    /// How long the <paramref name="index"/>-th ray is at this point of the
    /// contact window, in reference pixels. Odd rays are the short ones.
    /// </summary>
    public static double SparkRayRef(int index, double contactAlpha) =>
        (index % 2 == 0 ? SparkOuterRef : SparkInnerRef) *
        (1.0 + (SparkGrowth * Clamp01(contactAlpha)));

    /// <summary>The direction of one ray, in radians.</summary>
    public static double SparkRayRadians(int index) =>
        Math.Tau * index / SparkRays;

    /// <summary>
    /// The colour of the spark: the same two the flash already uses, so a body
    /// going down and a body merely losing hit points read the same way in both
    /// marks.
    /// </summary>
    public static string SparkColor(BlowOutcome outcome) => FlashColor(outcome);

    /// <summary>
    /// The share of a tick the drawing holds still on when a blow lands. The
    /// bodies stop sliding, the tick does not stop: the simulation has already
    /// produced the state being drawn, and this only decides which frame of the
    /// journey between two canonical positions is shown.
    /// </summary>
    public const double HitStopShare = 0.35;

    /// <summary>The flash on a body struck this tick.</summary>
    public static double FlashAlpha(double tickAlpha) =>
        Fade(FlashPeak, FlashFloor, tickAlpha);

    /// <summary>The damage number's opacity.</summary>
    public static double DamageAlpha(double tickAlpha) =>
        Fade(DamagePeak, DamageFloor, tickAlpha);

    /// <summary>
    /// How far above the body's centre the number is drawn, in reference pixels.
    /// Negative, because the view's Y grows downwards.
    /// </summary>
    public static double DamageOffsetRef(double tickAlpha) =>
        -(DamageBaseRef + (DamageRiseRef * Clamp01(tickAlpha)));

    /// <summary>
    /// The horizontal offset of one number among <paramref name="count"/> drawn
    /// over the same body, so the row of them stays centred on it.
    /// </summary>
    public static double DamageSlotOffsetRef(int index, int count) =>
        count <= 1 ? 0.0 : (index - ((count - 1) / 2.0)) * DamageSlotRef;

    /// <summary>
    /// What a damage number says. The minus sign is the reading: the number is
    /// hit points leaving a body, not a score.
    /// </summary>
    public static string DamageLabel(Blow blow)
    {
        ArgumentNullException.ThrowIfNull(blow);
        return $"-{blow.Damage}";
    }

    /// <summary>
    /// The colour of a damage number, and the whole of the "hit, no blow and
    /// downed read differently" claim on the number's channel.
    ///
    /// <list type="bullet">
    /// <item>going down is the white the downed cross is already drawn in, so the
    /// two marks on that body agree;</item>
    /// <item>a raider losing hit points is amber — the colour the HUD already
    /// uses for a fighting creature's own state dot;</item>
    /// <item>a crew member losing them is the hostile red of
    /// <see cref="SideOutline"/>, because the harm came from that side.</item>
    /// </list>
    ///
    /// There is no fourth case for a miss, and that is a fact about the
    /// simulation rather than a gap here: an attack in reach always lands, damage
    /// is floored at <c>PrototypeTuning.DamageFloor</c>, and no reason code says
    /// "missed". The third reading is the absence of the mark — a fighting body
    /// with no blow this tick gets no number, no flash and no wind-up.
    /// </summary>
    public static string DamageColor(Blow blow)
    {
        ArgumentNullException.ThrowIfNull(blow);
        return blow.Outcome == BlowOutcome.Downed
            ? "#f8fafc"
            : blow.Target.Kind == BodyKind.Raider
                ? "#fbbf24"
                : "#fb7185";
    }

    /// <summary>
    /// The colour a struck body flashes in. Warm white for a body still standing,
    /// the hostile red-white for one that has just gone down, so the flash carries
    /// the outcome even where the number is hidden behind another body.
    /// </summary>
    public static string FlashColor(BlowOutcome outcome) =>
        outcome == BlowOutcome.Downed ? "#fecdd3" : "#fff7d6";

    /// <summary>The colour of the streak between attacker and target.</summary>
    public static string StreakColor(Blow blow)
    {
        ArgumentNullException.ThrowIfNull(blow);
        return blow.Target.Kind == BodyKind.Raider ? "#fde68a" : "#fda4af";
    }

    /// <summary>
    /// The stroke that says which way the blow travelled. Both ends are on the
    /// line between the two bodies, neither reaches either of them, and a blow
    /// whose ends coincide — two bodies drawn on one point — gives a segment of
    /// zero length the adapter skips.
    /// </summary>
    public static ViewSegment Streak(ViewPoint attacker, ViewPoint target) =>
        new(
            Along(attacker, target, StreakStartShare),
            Along(attacker, target, StreakEndShare));

    /// <summary>
    /// How far the drawing has travelled between the previous canonical position
    /// and the current one, once hit-stop has had its say.
    ///
    /// <para>
    /// When a blow lands the picture spends the first <see cref="HitStopShare"/>
    /// of the tick at the position the tick started from and then catches up, so
    /// the moment of contact reads as an impact instead of a slide. The remapping
    /// can only ever lower the alpha, which is what keeps it safe: the frame-pacing
    /// probe counts a violation when a body is drawn in a cell the simulation has
    /// not reached, and a lower alpha draws it closer to the cell it came from.
    /// </para>
    /// </summary>
    public static double HitStopAlpha(double tickAlpha, bool landed)
    {
        var alpha = Clamp01(tickAlpha);
        return landed
            ? Clamp01((alpha - HitStopShare) / (1.0 - HitStopShare))
            : alpha;
    }

    private static double Fade(double peak, double floor, double tickAlpha) =>
        peak + ((floor - peak) * Clamp01(tickAlpha));

    private static ViewPoint Along(ViewPoint from, ViewPoint to, double share) =>
        new(
            from.X + ((to.X - from.X) * share),
            from.Y + ((to.Y - from.Y) * share));

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}
