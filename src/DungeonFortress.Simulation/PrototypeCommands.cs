namespace DungeonFortress.Simulation;

public abstract record PrototypeCommand(int Tick);

public sealed record ZonePaintCommand(
    int Tick,
    ZoneKind ZoneKind,
    IReadOnlyList<GridPoint> Tiles) : PrototypeCommand(Tick);

public sealed record ZoneEraseCommand(
    int Tick,
    ZoneKind ZoneKind,
    IReadOnlyList<GridPoint> Tiles) : PrototypeCommand(Tick);

/// <summary>
/// Marks internal rock for excavation. Strict: a single non-diggable tile rejects
/// the whole command before any mutation, mirroring <see cref="ZonePaintCommand"/>.
/// </summary>
public sealed record DigDesignateCommand(
    int Tick,
    IReadOnlyList<GridPoint> Tiles) : PrototypeCommand(Tick);

/// <summary>
/// Withdraws excavation intent. Tolerant of tiles that carry no designation, the
/// same way <see cref="ZoneEraseCommand"/> tolerates tiles outside the zone.
/// </summary>
public sealed record DigCancelCommand(
    int Tick,
    IReadOnlyList<GridPoint> Tiles) : PrototypeCommand(Tick);

/// <summary>
/// Marks plain floor as a training-post blueprint. Strict and atomic like
/// <see cref="DigDesignateCommand"/>: one illegal tile rejects the whole command
/// before any mutation. It says "a post belongs here", not who builds it.
/// </summary>
public sealed record BuildDesignateCommand(
    int Tick,
    IReadOnlyList<GridPoint> Tiles) : PrototypeCommand(Tick);

/// <summary>
/// Withdraws a blueprint. Tolerant of tiles that carry none, like
/// <see cref="DigCancelCommand"/>. Stone already delivered to the site returns to
/// the floor of that same tile, so cancelling never destroys material.
/// </summary>
public sealed record BuildCancelCommand(
    int Tick,
    IReadOnlyList<GridPoint> Tiles) : PrototypeCommand(Tick);

public sealed record SetPriorityCommand(
    int Tick,
    JobKind JobKind,
    int Value) : PrototypeCommand(Tick);

public sealed record SetRuleCommand(
    int Tick,
    string RuleId,
    int Value) : PrototypeCommand(Tick);

/// <summary>
/// The second addressed command of the dictionary, and the first that names a
/// creature as the <b>subject of a judgement</b> rather than as the holder of a
/// direction (ADR 0009). Its shape is fixed by ADR 0019 and is the whole of the
/// permission: <c>{tick, kind, creatureId, verdict}</c> and nothing else — any
/// field beyond this set takes the command out of the verdicts, whatever it is
/// called.
///
/// <para>There is no cancelling twin, on purpose: <c>zone_paint</c>,
/// <c>dig_designate</c> and <c>build_designate</c> have one because they state
/// an intention about a patch of ground, while a verdict has already been seen
/// by the domain, and un-saying it would make it a setting.</para>
/// </summary>
public sealed record VerdictCommand(
    int Tick,
    int CreatureId,
    VerdictKind Verdict) : PrototypeCommand(Tick);

public sealed record PrototypeCommandLog(
    string Scenario,
    ulong Seed,
    IReadOnlyList<PrototypeCommand> Commands);
