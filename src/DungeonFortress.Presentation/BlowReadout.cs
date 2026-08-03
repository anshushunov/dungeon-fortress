using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>Which of the two body lists a blow names.</summary>
public enum BodyKind
{
    /// <summary>A member of the domain's crew, from <c>PrototypeSnapshot.Creatures</c>.</summary>
    Creature,

    /// <summary>A raider, from <c>PrototypeSnapshot.Raiders</c>.</summary>
    Raider,
}

/// <summary>One body, named the way the snapshot names it.</summary>
/// <param name="Kind">Which list it is in.</param>
/// <param name="Id">Its id inside that list.</param>
public readonly record struct BodyRef(BodyKind Kind, int Id);

/// <summary>What the blow did to the body it landed on.</summary>
public enum BlowOutcome
{
    /// <summary>The body lost hit points and is still standing.</summary>
    Hit,

    /// <summary>The body went down on this blow.</summary>
    Downed,
}

/// <summary>
/// How the view knows the blow happened. It is part of the reading and not an
/// implementation detail, because the two answers differ in what they can say:
/// a recorded blow names both ends, an inferred one names only the body that
/// lost hit points.
/// </summary>
public enum BlowEvidence
{
    /// <summary>
    /// The canonical journal names it: <c>combat_attack</c>,
    /// <c>combat_raider_downed</c> or <c>combat_downed</c>. Attacker, target and
    /// damage all come from the same entry.
    /// </summary>
    Recorded,

    /// <summary>
    /// The journal is silent and the hit points fell anyway. A raider's blow on a
    /// defender that survives records nothing at all
    /// (<c>PrototypeWorld.ActRaiders</c> writes only <c>combat_downed</c>, and only
    /// when the defender falls), so the drop between two canonical snapshots is
    /// the whole of what the view can honestly claim: <em>this body was struck,
    /// this hard</em>. Who struck it is not inferred — a guess drawn as an arrow
    /// would be indistinguishable on screen from a fact.
    /// </summary>
    Inferred,
}

/// <summary>
/// One blow, as the picture has to show it.
/// </summary>
/// <param name="Attacker">
/// The body that struck, or <c>null</c> when only the journal's silence and a
/// falling hit-point count are known — see <see cref="BlowEvidence.Inferred"/>.
/// </param>
/// <param name="Target">The body that was struck.</param>
/// <param name="Damage">Hit points the target lost on this blow.</param>
/// <param name="Outcome">Whether the target survived it.</param>
/// <param name="Evidence">Where the view got it from.</param>
public sealed record Blow(
    BodyRef? Attacker,
    BodyRef Target,
    int Damage,
    BlowOutcome Outcome,
    BlowEvidence Evidence);

/// <summary>
/// Every blow of one drawn moment, plus the pose each body takes because of it.
///
/// <para>
/// It is presentation state in the strict sense of
/// <see href="../../docs/decisions/0011-presentation-layer-without-engine.md">ADR
/// 0011</see>: derived from a canonical snapshot, never written back to one. No
/// value here reaches <c>PrototypeSnapshot</c>, the checksum or the command log,
/// and a build that always used <see cref="Empty"/> would be the game exactly as
/// it was.
/// </para>
/// </summary>
public sealed class BlowReading
{
    private readonly Dictionary<BodyRef, BodyActionPhase> _phases;
    private readonly Dictionary<BodyRef, List<Blow>> _struck;

    internal BlowReading(
        IReadOnlyList<Blow> blows,
        Dictionary<BodyRef, BodyActionPhase> phases,
        Dictionary<BodyRef, List<Blow>> struck)
    {
        Blows = blows;
        _phases = phases;
        _struck = struck;
    }

    /// <summary>A moment with no blow in it, which is what most ticks are.</summary>
    public static BlowReading Empty { get; } = new([], [], []);

    /// <summary>Every blow of this moment, in journal order.</summary>
    public IReadOnlyList<Blow> Blows { get; }

    /// <summary>
    /// Whether anything landed at all. This is the one question hit-stop asks:
    /// the drawing holds for a fraction of the tick, the tick itself does not.
    /// </summary>
    public bool Landed => Blows.Count > 0;

    /// <summary>
    /// The pose this body owes to the blow, or <see cref="BodyActionPhase.None"/>
    /// when no blow touches it.
    ///
    /// <para>
    /// Being struck wins over striking. A body that both landed a blow and took
    /// one on the same tick is drawn recoiling: the pack has one pose for each,
    /// only one can be shown, and the harm done to a body is the fact the player
    /// is being asked to read.
    /// </para>
    /// </summary>
    public BodyActionPhase PhaseOf(BodyRef body) =>
        _phases.TryGetValue(body, out var phase) ? phase : BodyActionPhase.None;

    /// <summary>Every blow that landed on this body, in journal order.</summary>
    public IReadOnlyList<Blow> Struck(BodyRef body) =>
        _struck.TryGetValue(body, out var blows) ? blows : [];

    /// <summary>
    /// The outcome the body's flash carries when several blows land on it at
    /// once, or <c>null</c> when none does. Going down wins: it is the one of the
    /// two that the next frame cannot show any more.
    /// </summary>
    public BlowOutcome? OutcomeOf(BodyRef body)
    {
        BlowOutcome? outcome = null;
        foreach (var blow in Struck(body))
        {
            if (blow.Outcome == BlowOutcome.Downed)
            {
                return BlowOutcome.Downed;
            }

            outcome = BlowOutcome.Hit;
        }

        return outcome;
    }
}

/// <summary>
/// Which blows a drawn moment contains, read off the canonical journal.
///
/// <para>
/// <b>Which tick a snapshot shows.</b> <c>PrototypeWorld</c> increments
/// <c>CurrentTick</c> at the <em>end</em> of a step, so an event recorded during
/// that step carries the number the tick had while it ran. A snapshot whose
/// <c>Tick</c> is <c>T</c> is therefore the world after step <c>T - 1</c>, and the
/// blows it should be showing are the ones stamped <c>T - 1</c>. Measured, not
/// assumed: at <c>Tick</c> 1320 of the shipped <c>prepared</c> journal the freshest
/// combat event is stamped 1319 (<c>evidence/210-before.json</c>).
/// </para>
///
/// <para>
/// <b>Why collapsed repeats do not hide a blow.</b> <c>RecordDecision</c> folds an
/// identical repeat into the creature's previous entry: <c>LastTick</c> moves and
/// <c>Repeats</c> grows instead of a second entry appearing. Over one shipped party
/// that is 136 blows in 52 entries, up to nine folded into one
/// (<c>evidence/210-before.json</c>). It costs the view nothing, because the view
/// draws one moment and asks one question — <em>did this creature strike on the
/// tick that has just run?</em> — and <c>LastTick</c> is by construction the tick of
/// the most recent repeat. What a fold loses is how many times the same thing
/// happened earlier, which no frame shows.
/// <c>BlowJournalSourceTests.Every_blow_of_the_party_is_recoverable_tick_by_tick</c>
/// walks a whole party and recovers all 136.
/// </para>
/// </summary>
public static class BlowReadout
{
    /// <summary>A crew member struck a raider. Details: <c>raiderId</c>, <c>damage</c>.</summary>
    public const string AttackReason = "combat_attack";

    /// <summary>A crew member's blow put a raider down. Details: <c>raiderId</c>.</summary>
    public const string RaiderDownedReason = "combat_raider_downed";

    /// <summary>A raider's blow put a defender down. Details: <c>raiderId</c>, <c>damage</c>.</summary>
    public const string DefenderDownedReason = "combat_downed";

    private const string RaiderDetail = "raiderId";
    private const string DamageDetail = "damage";

    /// <summary>
    /// The blows of the moment <paramref name="state"/> draws.
    /// </summary>
    /// <param name="state">The canonical snapshot the frame is being drawn from.</param>
    /// <param name="previousCreatureHitPoints">
    /// What each creature's hit points were before the ticks this snapshot
    /// advanced through, or <c>null</c> when there is no previous reading — a
    /// freshly loaded fixture, for instance. It is the only source for a raider's
    /// blow that a defender survived, because the journal does not record one; see
    /// <see cref="BlowEvidence.Inferred"/>.
    /// </param>
    public static BlowReading Of(
        PrototypeSnapshot state,
        IReadOnlyDictionary<int, int>? previousCreatureHitPoints = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var tick = state.Tick - 1;
        var blows = new List<Blow>();
        var explained = new HashSet<int>();

        var fresh = state.Events.Where(@event => @event.LastTick == tick).ToArray();
        var kills = fresh
            .Where(@event => @event.ReasonCode == RaiderDownedReason)
            .ToArray();
        var claimedKills = new HashSet<(int Creature, int Raider)>();

        foreach (var attack in fresh.Where(@event => @event.ReasonCode == AttackReason))
        {
            var raiderId = Detail(attack, RaiderDetail);
            var fatal = kills.Any(kill =>
                kill.CreatureId == attack.CreatureId &&
                Detail(kill, RaiderDetail) == raiderId);
            if (fatal)
            {
                claimedKills.Add((attack.CreatureId, raiderId));
            }

            blows.Add(new Blow(
                new BodyRef(BodyKind.Creature, attack.CreatureId),
                new BodyRef(BodyKind.Raider, raiderId),
                Detail(attack, DamageDetail),
                fatal ? BlowOutcome.Downed : BlowOutcome.Hit,
                BlowEvidence.Recorded));
        }

        // A kill is always recorded right after the blow that caused it, so this
        // loop normally adds nothing. It exists because the alternative to a
        // defensive branch here is a raider that dies with no blow drawn at all,
        // and the journal's ordering is the simulation's business, not the view's.
        foreach (var kill in kills)
        {
            var raiderId = Detail(kill, RaiderDetail);
            if (claimedKills.Contains((kill.CreatureId, raiderId)))
            {
                continue;
            }

            blows.Add(new Blow(
                new BodyRef(BodyKind.Creature, kill.CreatureId),
                new BodyRef(BodyKind.Raider, raiderId),
                Detail(kill, DamageDetail),
                BlowOutcome.Downed,
                BlowEvidence.Recorded));
        }

        foreach (var downed in fresh.Where(@event =>
                     @event.ReasonCode == DefenderDownedReason))
        {
            blows.Add(new Blow(
                new BodyRef(BodyKind.Raider, Detail(downed, RaiderDetail)),
                new BodyRef(BodyKind.Creature, downed.CreatureId),
                Detail(downed, DamageDetail),
                BlowOutcome.Downed,
                BlowEvidence.Recorded));
            explained.Add(downed.CreatureId);
        }

        if (previousCreatureHitPoints is not null)
        {
            foreach (var creature in state.Creatures)
            {
                if (explained.Contains(creature.Id) ||
                    !previousCreatureHitPoints.TryGetValue(creature.Id, out var before))
                {
                    continue;
                }

                var lost = before - creature.Hp;
                if (lost <= 0)
                {
                    continue;
                }

                blows.Add(new Blow(
                    null,
                    new BodyRef(BodyKind.Creature, creature.Id),
                    lost,
                    BlowOutcome.Hit,
                    BlowEvidence.Inferred));
            }
        }

        if (blows.Count == 0)
        {
            return BlowReading.Empty;
        }

        var phases = new Dictionary<BodyRef, BodyActionPhase>();
        var struck = new Dictionary<BodyRef, List<Blow>>();
        foreach (var blow in blows)
        {
            if (blow.Attacker is { } attacker)
            {
                // Windup never overwrites a flinch already written for the same
                // body: PhaseOf documents why being struck wins.
                if (!phases.ContainsKey(attacker))
                {
                    phases[attacker] = BodyActionPhase.Windup;
                }
            }

            phases[blow.Target] = BodyActionPhase.Flinch;
            if (!struck.TryGetValue(blow.Target, out var landed))
            {
                landed = [];
                struck[blow.Target] = landed;
            }

            landed.Add(blow);
        }

        return new BlowReading(blows, phases, struck);
    }

    private static int Detail(PrototypeEvent @event, string key) =>
        @event.Details.TryGetValue(key, out var value) ? value : 0;
}
