namespace DungeonFortress.Simulation;

// The live state types the world mutates, and their projection into the
// snapshot records the outside world reads.
public sealed partial class PrototypeWorld
{
    private PrototypeCreatureSnapshot ToSnapshot(CreatureState creature)
    {
        return new PrototypeCreatureSnapshot(
            creature.Id,
            creature.Name,
            creature.Might,
            creature.Grit,
            creature.Affinities,
            creature.Satiety,
            creature.Fatigue,
            creature.MartialForm,
            creature.Hp,
            creature.MaxHp,
            creature.Injury,
            creature.Position,
            creature.Mode,
            creature.CurrentJob?.Id,
            creature.Carrying,
            creature.CarryAmount,
            creature.MealReserved,
            creature.SpecialTarget,
            creature.SpecialTicks,
            creature.IsMustering,
            creature.MusterNeedsRation,
            creature.MusterTarget,
            creature.WorkTicks,
            creature.WatchTicks,
            creature.MoveCount,
            creature.LastMoveTick < 0 ? null : creature.LastMoveTick,
            creature.BlockedTicks,
            creature.YieldCount,
            creature.LastYieldTick < 0 ? null : creature.LastYieldTick,
            creature.LastDecision,
            ComputeReadiness(creature),
            creature.ReadinessAtRaid,
            creature.RecoveryTicks,
            [.. creature.RememberedPlaces.Values],
            ToSnapshot(creature.Loyalty, ReleasedGrudge(creature) > 0),
            [.. creature.InjuredParts().Select(
                part => new PrototypeInjurySnapshot(part.Part, part.Severity))],
            creature.StepsLostToLimp,
            creature.ActionsLostToStun,
            creature.WoundIntent);
    }

    private PrototypeDigDesignationSnapshot ToSnapshot(GridPoint tile)
    {
        var job = _jobs.FirstOrDefault(
            item => item.Kind == JobKind.Dig && item.Origin == tile);
        var reachable = IsDigReachable(tile);
        var reserved = job?.ReservedBy;
        var status = _priorities[JobKind.Dig] == 0
            ? "dig_blocked_priority"
            : !reachable
                ? "dig_unreachable"
                : reserved is null
                    ? "dig_waiting"
                    : job!.ProgressTicks > 0
                        ? "dig_in_progress"
                        : "dig_reserved";
        return new PrototypeDigDesignationSnapshot(
            tile,
            job?.Id,
            reserved,
            reserved is null ? null : job!.Target,
            job?.ProgressTicks ?? 0,
            job is { ReservedBy: not null }
                ? job.ProgressTicks + Math.Max(0, job.RemainingTicks)
                : PrototypeTuning.DigTicks,
            reachable,
            status);
    }

    /// <summary>
    /// Every stop of the chain gets its own reading, in the order the simulation
    /// itself decides them: is construction switched off, can the site be served,
    /// is it being built, is the material there, is it on its way, does the stone
    /// exist at all, or is every block of it already promised elsewhere.
    /// </summary>
    private PrototypeBuildSiteSnapshot ToSnapshot(BuildSiteState site)
    {
        var job = _jobs.FirstOrDefault(
            item => item.Kind == JobKind.Build && item.Origin == site.Tile);
        var reachable = IsBuildSiteWorkable(site.Tile);
        var reserved = job?.ReservedBy;
        var incoming = IncomingStoneAt(site.Tile);
        var complete = site.Delivered >= PrototypeTuning.BuildStoneCost;
        var status = _priorities[JobKind.Build] == 0
            ? "build_blocked_priority"
            : !reachable
                ? "build_unreachable"
                : job is { ProgressTicks: > 0 }
                    ? "build_in_progress"
                    : reserved is not null
                        ? "build_reserved"
                        : complete
                            ? "build_ready"
                            : incoming > 0
                                ? "build_carrier_on_the_way"
                                : _priorities[JobKind.Haul] == 0
                                    ? "build_haul_blocked"
                                    : AvailableStoneForSites() <= 0
                                        ? StoneAnywhere() > 0
                                            ? "build_stone_reserved"
                                            : "build_no_stone"
                                        : "build_waiting_carrier";
        return new PrototypeBuildSiteSnapshot(
            site.Tile,
            site.Delivered,
            PrototypeTuning.BuildStoneCost,
            incoming,
            job?.Id,
            reserved,
            job?.ProgressTicks ?? 0,
            job is { ReservedBy: not null }
                ? job.ProgressTicks + Math.Max(0, job.RemainingTicks)
                : PrototypeTuning.BuildTicks,
            reachable,
            status);
    }

    /// <summary>
    /// Stone a construction site could still be given: loose piles and stockpiled
    /// blocks that no live job has already booked for somewhere else.
    /// </summary>
    private int AvailableStoneForSites()
    {
        var booked = _jobs
            .Where(job =>
                job.Kind == JobKind.Haul &&
                job.Resource == ResourceKind.Stone &&
                job.ReservedBy is not null)
            .Sum(job => job.StoreReserved);
        return LooseCount(ResourceKind.Stone) + StoredStoneTotal() - booked;
    }

    private int StoneAnywhere() =>
        LooseCount(ResourceKind.Stone) + StoredStoneTotal() + CarriedStoneTotal();

    /// <summary>
    /// Where a material stockpile may be painted right now. The map answers "is it
    /// pre-existing plain floor"; the world adds the one rule the map cannot know,
    /// namely that a construction site is not a warehouse.
    /// </summary>
    private IEnumerable<GridPoint> StockpileFloorTiles() =>
        _map.StockpileFloorTiles().Where(tile => !_buildSites.ContainsKey(tile));

    /// <summary>
    /// Where a blueprint may be placed right now: plain floor, including ground
    /// the player created by digging, minus the cells already promised to storage
    /// or to another blueprint.
    /// </summary>
    private IEnumerable<GridPoint> BuildFloorTiles() =>
        _map.BuildFloorTiles().Where(tile =>
            !_buildSites.ContainsKey(tile) &&
            !_zones[ZoneKind.MaterialStockpile].Contains(tile));

    /// <summary>
    /// Four readings the player must get from one cell: it is empty, it is partly
    /// full, it is full, or its remaining room is already promised to a carrier on
    /// the way. A cell inside Forbidden reports that it cannot be served at all.
    /// </summary>
    private PrototypeStockpileCellSnapshot ToStockpileSnapshot(GridPoint tile)
    {
        var stored = StoredStoneAt(tile);
        var incoming = IncomingStoneAt(tile);
        var reachable = _map.IsPassable(tile) && !_zones[ZoneKind.Forbidden].Contains(tile);
        var status = !reachable
            ? "stockpile_unreachable"
            : stored >= PrototypeTuning.StockpileCellCapacity
                ? "stockpile_full"
                : stored + incoming >= PrototypeTuning.StockpileCellCapacity
                    ? "stockpile_incoming"
                    : stored > 0
                        ? "stockpile_partial"
                        : "stockpile_empty";
        return new PrototypeStockpileCellSnapshot(
            tile,
            stored,
            PrototypeTuning.StockpileCellCapacity,
            incoming,
            reachable,
            status);
    }

    private static PrototypePendingCommandSnapshot ToSnapshot(PrototypeCommand command)
    {
        return command switch
        {
            ZonePaintCommand paint => new(
                paint.Tick,
                "zone_paint",
                paint.ZoneKind,
                paint.Tiles.ToArray(),
                null,
                null,
                null,
                null,
                null),
            ZoneEraseCommand erase => new(
                erase.Tick,
                "zone_erase",
                erase.ZoneKind,
                erase.Tiles.ToArray(),
                null,
                null,
                null,
                null,
                null),
            DigDesignateCommand designate => new(
                designate.Tick,
                "dig_designate",
                null,
                designate.Tiles.ToArray(),
                null,
                null,
                null,
                null,
                null),
            DigCancelCommand cancel => new(
                cancel.Tick,
                "dig_cancel",
                null,
                cancel.Tiles.ToArray(),
                null,
                null,
                null,
                null,
                null),
            BuildDesignateCommand build => new(
                build.Tick,
                "build_designate",
                null,
                build.Tiles.ToArray(),
                null,
                null,
                null,
                null,
                null),
            BuildCancelCommand unbuild => new(
                unbuild.Tick,
                "build_cancel",
                null,
                unbuild.Tiles.ToArray(),
                null,
                null,
                null,
                null,
                null),
            SetPriorityCommand priority => new(
                priority.Tick,
                "set_priority",
                null,
                [],
                priority.JobKind,
                null,
                priority.Value,
                null,
                null),
            SetRuleCommand rule => new(
                rule.Tick,
                "set_rule",
                null,
                [],
                null,
                rule.RuleId,
                rule.Value,
                null,
                null),
            VerdictCommand verdict => new(
                verdict.Tick,
                "verdict",
                null,
                [],
                null,
                null,
                null,
                verdict.CreatureId,
                ToVerdictJson(verdict.Verdict)),
            _ => throw new InvalidDataException(
                $"Unsupported prototype command: {command.GetType().Name}"),
        };
    }

    private static PrototypeEvent ToSnapshot(EventState @event)
    {
        return new PrototypeEvent(
            @event.FirstTick,
            @event.LastTick,
            @event.CreatureId,
            @event.ReasonCode,
            @event.Details,
            @event.Repeats,
            @event.JobKind,
            @event.Target);
    }

    /// <summary>
    /// How fit this creature is to do anything at all.
    ///
    /// <para><b>The wound term is the torso's and no longer every wound's</b>
    /// (Issue #409, coordinator's decision of 2026-08-12, record 1 of
    /// <see href="https://github.com/anshushunov/dungeon-fortress/issues/415">#415</see>).
    /// Until this slice the penalty was read off the summary <c>Injury</c>, which
    /// is the worst of the four parts, so a creature with a ruined arm and a whole
    /// body was as unfit as one that had been opened up. That is «вычитание
    /// числа» — the exact thing section 6.13 of the pitch replaces with
    /// consequences — applied to all four parts at once, and it double-charged
    /// three of them: the arm already loses weight off the blow, the leg already
    /// loses steps, the head already loses actions, and each of them was paying a
    /// second time through a term that belongs to the body.</para>
    ///
    /// <para>The torso is where it belongs because the torso is the part with no
    /// consequence of its own to name. The pitch gives the other three a sentence
    /// each — «роняет оружие», «хромает и не убегает», «оглушён» — and calls the
    /// torso the fourth part of the readability budget without saying what it
    /// does. This is what it does: a hurt body is a body that brings less of
    /// itself to everything, which is what readiness already meant.</para>
    /// </summary>
    private static int ComputeReadiness(CreatureState creature)
    {
        var injuryPenalty = creature.PartInjury(BodyPart.Torso) switch
        {
            InjuryKind.None => 0,
            InjuryKind.Light => PrototypeTuning.TorsoLightPenalty,
            InjuryKind.Heavy => PrototypeTuning.TorsoHeavyPenalty,
            _ => 0,
        };
        var readiness = PrototypeTuning.ReadinessBase +
            creature.Satiety * PrototypeTuning.ReadinessSatietyNumerator /
            PrototypeTuning.ReadinessSatietyDenominator +
            creature.MartialForm * PrototypeTuning.ReadinessMartialNumerator /
            PrototypeTuning.ReadinessMartialDenominator +
            (100 - creature.Fatigue) / PrototypeTuning.ReadinessRestDenominator -
            injuryPenalty;
        return Math.Clamp(readiness, 0, 100);
    }

    private sealed record CreatureDefinition(
        int Id,
        string Name,
        int Might,
        int Grit,
        IReadOnlyDictionary<JobKind, int> Affinities,
        GridPoint Position);

    private sealed class CreatureState(CreatureDefinition definition)
    {
        public int Id => definition.Id;
        public string Name => definition.Name;
        public int Might => definition.Might;
        public int Grit => definition.Grit;
        public IReadOnlyDictionary<JobKind, int> Affinities => definition.Affinities;
        public GridPoint Position { get; set; } = definition.Position;
        public int Satiety { get; set; }
        public int Fatigue { get; set; }
        public int MartialForm { get; set; }
        public int MaxHp { get; } = PrototypeTuning.DefenderHpBase +
            definition.Might * PrototypeTuning.DefenderHpPerMight;
        public int Hp { get; set; } = PrototypeTuning.DefenderHpBase +
            definition.Might * PrototypeTuning.DefenderHpPerMight;
        /// <summary>
        /// What each of the four parts carries, indexed by <see cref="BodyPart"/>.
        /// This is the wound: <see cref="Injury"/> below is read off it and is
        /// stored nowhere.
        ///
        /// <para><b>Why the scalar became a function.</b> Before Issue #409 a
        /// creature carried one <see cref="InjuryKind"/> and nothing else, so
        /// «его достали» was a number subtracted from readiness and there was
        /// no answer to «where». Fifteen call sites read that scalar — combat
        /// admission, readiness, mending, matching, planning, the loyalty
        /// ledger — and every one of them asks the same question, «how badly is
        /// this one hurt overall». Keeping that question answerable in one
        /// place, from the parts, is what let the localisation be added without
        /// re-deciding any of the fifteen.</para>
        /// </summary>
        private readonly InjuryKind[] _parts = new InjuryKind[BodyParts.Count];

        /// <summary>
        /// How badly this creature is hurt, whole: the worst of its parts. It
        /// is exactly the value the field it replaced held — a creature with a
        /// light arm and a heavy leg is a heavily wounded creature — so nothing
        /// that read the scalar had to change its meaning.
        /// </summary>
        public InjuryKind Injury
        {
            get
            {
                var worst = InjuryKind.None;
                foreach (var severity in _parts)
                {
                    if (severity > worst)
                    {
                        worst = severity;
                    }
                }

                return worst;
            }
        }

        public InjuryKind PartInjury(BodyPart part) => _parts[(int)part];

        public void SetPartInjury(BodyPart part, InjuryKind severity) =>
            _parts[(int)part] = severity;

        /// <summary>
        /// The injured parts in <see cref="BodyPart"/> order. Empty when this
        /// creature is whole.
        /// </summary>
        public IEnumerable<(BodyPart Part, InjuryKind Severity)> InjuredParts()
        {
            for (var index = 0; index < BodyParts.Count; index++)
            {
                if (_parts[index] != InjuryKind.None)
                {
                    yield return ((BodyPart)index, _parts[index]);
                }
            }
        }

        /// <summary>
        /// Steps a hurt leg has taken away from this creature over the party. A
        /// monotone counter of the same family as <see cref="MoveCount"/> and
        /// <see cref="BlockedTicks"/>: nothing reads it, and it exists so that the
        /// leg's consequence can be measured without inferring it from how much
        /// walking a wounded creature happened to have to do.
        /// </summary>
        public int StepsLostToLimp { get; set; }

        /// <summary>
        /// Combat actions a hurt head has taken away from this creature over the
        /// party. The head's own counter, of the same family and for the same
        /// reason as <see cref="StepsLostToLimp"/>: nothing in the simulation
        /// reads it, and it exists so the stun can be measured as a rate rather
        /// than inferred from how many ticks a wounded creature happened to
        /// spend in a fight.
        ///
        /// <para>It is a counter and not a journal count on purpose. The journal
        /// folds an entry into the creature's own last one, and a stunned
        /// creature that is still walking towards a raider writes nothing in
        /// between, so two stuns a tick apart become one entry with a count —
        /// which is right for a player reading the document and wrong for
        /// anything that has to add them up.</para>
        /// </summary>
        public int ActionsLostToStun { get; set; }

        /// <summary>
        /// The last tick a limp was charged, so that two calls to
        /// <see cref="PrototypeWorld.Move"/> inside one tick cannot charge it
        /// twice. Transient, like <see cref="WaitThisTick"/>: it is a function of
        /// the tick in hand and never survives into the canonical document.
        /// </summary>
        public int LastLimpTick { get; set; } = -1;

        /// <summary>
        /// What this creature decided at the roll call about its own wound, and
        /// null while it is whole or no wave has asked it yet (Issue #431).
        ///
        /// <para>Canonical state and not a view model: it is written by the
        /// contest in <see cref="PrototypeWorld.UpdateCombatParticipation"/> and
        /// has to survive into the document, because the panel that reads it is
        /// not allowed to read <see cref="LastDecision"/> — the roll call runs
        /// before job generation and matching, so the decision would be
        /// overwritten inside the tick it was taken on.</para>
        /// </summary>
        public PrototypeWoundIntentSnapshot? WoundIntent { get; set; }

        public int RecoveryTicks { get; set; }
        public CreatureMode Mode { get; set; }
        public JobState? CurrentJob { get; set; }
        public PrototypeDecision LastDecision { get; set; } = null!;
        public int? ReadinessAtRaid { get; set; }
        public ResourceKind? Carrying { get; set; }
        public int CarryAmount { get; set; }
        public bool MealReserved { get; set; }
        public GridPoint? SpecialTarget { get; set; }
        public int SpecialTicks { get; set; }
        public bool IsMustering { get; set; }
        public bool MusterNeedsRation { get; set; }
        public GridPoint? MusterTarget { get; set; }
        public bool NeedsRest { get; set; }
        public GridPoint? TrafficTarget { get; set; }
        public int WorkTicks { get; set; }
        public int WatchTicks { get; set; }
        public int MoveCount { get; set; }
        public int LastMoveTick { get; set; } = -1;
        public int BlockedTicks { get; set; }
        public int YieldCount { get; set; }
        public int LastYieldTick { get; set; } = -1;
        public bool WaitThisTick { get; set; }

        /// <summary>
        /// Consecutive ticks this creature has spent waiting with no job
        /// (Issue #201). It is what tells "between two pieces of work" from "off
        /// duty": the second is only claimed after
        /// <see cref="PrototypeTuning.OffDutyDelayTicks"/> of the first. Anything
        /// that gives the creature something to do — a job, a muster, a fight, a
        /// meal — resets it to zero.
        /// </summary>
        public int IdleTicks { get; set; }

        /// <summary>
        /// The tile in the quarters this creature is walking to because it has
        /// nothing to do. Held rather than recomputed every tick so that the
        /// journal entry is written once per departure, and so that a creature
        /// does not change its mind halfway when somebody else moves.
        /// </summary>
        public GridPoint? OffDutyTarget { get; set; }

        /// <summary>
        /// True from the tick a wave ended with this creature still in it until
        /// the creature is given something to do or reaches the quarters. It is
        /// what narrows Issue #201 to its own sentence — "уходит **с места
        /// боя**" — instead of "leaves whenever it is idle".
        ///
        /// <para>The distinction was measured, not assumed. The first version of
        /// the rule fired on any idleness, and the ordinary
        /// <c>waiting_stock_sufficient</c> pause is frequent enough that the
        /// party started walking to the far corner of the map and back all
        /// session: <c>prepared/20260726</c> ended `fallen` at t2032 with an
        /// average satiety of 0 — the food chain lost to the commute. Tying the
        /// rule to the end of a fight leaves peacetime behaviour untouched.</para>
        /// </summary>
        public bool LeftTheFight { get; set; }

        /// <summary>
        /// Where this creature broke or was put down. Keyed by tile so that the
        /// same place remembered twice stays one entry, and sorted so that the
        /// canonical document does not depend on the order the events arrived
        /// in. Capped at <see cref="PrototypeTuning.MemoryPlacesMax"/>.
        /// </summary>
        public SortedDictionary<GridPoint, PrototypeRememberedPlace> RememberedPlaces { get; } = [];

        /// <summary>
        /// What this creature is worth to the domain and the domain to it —
        /// fear, benefit and grudge, with the terms each of them was built from.
        /// Everything that writes to it goes through
        /// <see cref="PrototypeWorld.Accrue"/>; see
        /// <c>PrototypeWorld.Loyalty.cs</c>.
        /// </summary>
        public LoyaltyState Loyalty { get; } = new();

        /// <summary>
        /// The work this creature turned down this tick because of where it
        /// would have had to start. Transient — it is recomputed every tick from
        /// the memory and the job list, exactly like <see cref="WaitThisTick"/>,
        /// so it is not canonical state.
        ///
        /// <para>
        /// <c>Score</c> and <c>JobId</c> are carried so that the pair kept is the
        /// best of the ones memory took away rather than the first one met
        /// (Issue #125). Nothing outside the matching reads them; the journal
        /// entry is still built from the kind, the tile and the place.
        /// </para>
        /// </summary>
        public (JobKind Kind, GridPoint Target, PrototypeRememberedPlace Place, long JobId, int Score)?
            AvoidedThisTick { get; set; }

        public int Affinity(JobKind kind)
        {
            return Affinities.GetValueOrDefault(kind);
        }
    }

    private sealed class JobState(
        long id,
        string key,
        JobKind kind,
        GridPoint origin,
        ResourceKind? resource,
        int quantity,
        int? personalCreatureId)
    {
        public long Id { get; } = id;
        public string Key { get; } = key;
        public JobKind Kind { get; } = kind;
        public GridPoint Origin { get; } = origin;
        public GridPoint Target { get; set; } = origin;
        public ResourceKind? Resource { get; } = resource;
        public int Quantity { get; set; } = quantity;
        public int? PersonalCreatureId { get; } = personalCreatureId;
        public int? ReservedBy { get; set; }
        public int RemainingTicks { get; set; }
        public int ProgressTicks { get; set; }
        public bool PickedUp { get; set; }

        // Stone hauling only. The booking lives on the job rather than on the cell
        // so that releasing the job — cancel, replan, completion — cannot leave a
        // stockpile cell or a construction site holding room for a delivery that
        // will never arrive.
        public GridPoint? StoreCell { get; set; }
        public int StoreReserved { get; set; }

        // Set only when the load is withdrawn from a stockpile cell. A cell can
        // hold stored stone and a loose pile at once, so the source is stated.
        public GridPoint? SourceCell { get; set; }
    }

    /// <summary>
    /// A blueprint plus the stone that physically arrived at it. Delivered stone
    /// lives here rather than on the job, so cancelling a carrier can never take
    /// material that is already on the ground with it.
    /// </summary>
    private sealed class BuildSiteState(GridPoint tile)
    {
        public GridPoint Tile { get; } = tile;
        public int Delivered { get; set; }
    }

    private sealed class BedState(GridPoint position, int growth)
    {
        public GridPoint Position { get; } = position;
        public int Growth { get; set; } = growth;
        public bool IsRipe => Growth >= PrototypeTuning.BedGrowthTicks;
    }

    /// <summary>
    /// One wave of the session. The timetable is fixed at construction; the
    /// composition is written once at <see cref="AnnounceTick"/> from the renown
    /// standing then, and the tallies are written as the fight happens. Nothing
    /// here reads the wave number to decide how hard the wave is, which is what
    /// lets a future event layer replace the source without touching combat.
    /// </summary>
    private sealed class WaveState(int number, int announceTick, int arriveTick)
    {
        public int Number { get; } = number;
        public int AnnounceTick { get; } = announceTick;
        public int ArriveTick { get; } = arriveTick;
        public bool Announced { get; set; }
        public bool Arrived { get; set; }
        public int RenownAtAnnounce { get; set; }
        public int RaiderCount { get; set; }
        public int RaiderMight { get; set; }
        public int Entered { get; set; }
        public string? Outcome { get; set; }
        public int? EndTick { get; set; }
        public int RaidersDowned { get; set; }
        public int DefendersDowned { get; private set; }
        public int DefendersFled { get; private set; }
        public int MealsStolen { get; set; }

        public void CountDefenderDowned() => DefendersDowned++;

        public void CountDefenderFled() => DefendersFled++;
    }

    private sealed class RaiderState(
        int id,
        int wave,
        int hp,
        int might,
        GridPoint position,
        string name)
    {
        public int Id { get; } = id;
        public int Wave { get; } = wave;
        public int Hp { get; set; } = hp;
        public int Might { get; } = might;
        public GridPoint Position { get; set; } = position;
        public int CarryingMeals { get; set; }
        public int StealTicks { get; set; }

        /// <summary>
        /// How many consecutive ticks this raider has been unable to step because
        /// another raider stood where it was going. Criterion 2 of Issue #76 asks
        /// that a body with its way blocked not stall silently, so the wait has a
        /// limit and the limit is a tuning number rather than a literal.
        /// </summary>
        public int BlockedTicks { get; set; }
        public bool ReturningToGate { get; set; }
        public RaiderMode Mode { get; set; } = RaiderMode.Raiding;

        /// <summary>
        /// What this one is called. It is drawn once, from the party's own
        /// deterministic stream, and survives the raider: a survivor keeps it and
        /// the body that walks back in two waves later carries the same string.
        /// </summary>
        public string Name { get; } = name;

        /// <summary>
        /// The wave this raider walked out of alive, if it is here because it did.
        /// Null for a raider entering the domain for the first time.
        /// </summary>
        public int? ReturnedFromWave { get; init; }

        /// <summary>
        /// What the previous raid left on it, and the place it was hit hardest
        /// then. Both are read off the damage that actually landed
        /// (<see cref="Scar"/> below is a function of <see cref="LowestHp"/>), so
        /// neither can be handed out by the wave that carries it.
        /// </summary>
        public InjuryKind ScarFromLastTime { get; init; }

        /// <inheritdoc cref="ScarFromLastTime"/>
        public PrototypeRememberedPlace? RememberedPlace { get; init; }

        /// <summary>
        /// The health this raider walked in with.
        ///
        /// <para>Today it is <see cref="PrototypeTuning.RaiderHp"/> for everybody,
        /// returning raider included: the health bonus a returner was first given
        /// did not survive its own measurement — over eight parties it kept one
        /// raider in fifty-four off the floor and moved no party's score
        /// (<c>evidence/358-strengthening.json</c>), so the strengthening is one
        /// knob and it is might.</para>
        ///
        /// <para>The field stays anyway, and not out of habit: <see cref="Scar"/>
        /// reads the share of health a raider lost, and reading it against the
        /// health <em>this</em> raider started with rather than against a constant
        /// is what keeps that rule true if a body ever walks in with a different
        /// amount. It is one line and it removes a whole class of silent
        /// wrongness.</para>
        /// </summary>
        public int StartingHp { get; } = hp;

        /// <summary>The low-water mark of its health over this raid.</summary>
        public int LowestHp { get; private set; } = hp;

        /// <summary>
        /// The single hardest blow it took this raid, and where it was standing
        /// when it landed. The hardest and not the first: the first blow of a raid
        /// is usually a graze traded on the way past, and «где его достали» is the
        /// place the domain nearly finished it, not the place it walked through.
        /// Ties go to the earlier tick, so the answer never depends on the order
        /// two equal blows happened to be resolved in.
        /// </summary>
        public (int Damage, GridPoint Place, int Tick)? WorstBlow { get; private set; }

        public void RecordBlow(int damage, int tick)
        {
            LowestHp = Math.Min(LowestHp, Hp);
            if (WorstBlow is { } worst && worst.Damage >= damage)
            {
                return;
            }

            WorstBlow = (damage, Position, tick);
        }

        /// <summary>
        /// What this raid left on it, read off the health it actually lost. A
        /// raider nobody reached carries nothing; the share that separates a light
        /// mark from a heavy one is <see cref="PrototypeTuning.LightInjuryShare"/>,
        /// the same one the domain's own people are wounded by, so the two sides
        /// of the fight are read with one ruler.
        /// </summary>
        public InjuryKind Scar =>
            LowestHp >= StartingHp
                ? InjuryKind.None
                : LowestHp * 100 > StartingHp * PrototypeTuning.LightInjuryShare
                    ? InjuryKind.Light
                    : InjuryKind.Heavy;
    }

    /// <summary>
    /// A raider who walked out of the domain alive and the return the domain owes
    /// because of it. It is live state and not a snapshot projection: whether a
    /// place in a wave was found for this one is decided while the wave is
    /// entering, and the answer has to survive into the canonical document.
    /// </summary>
    private sealed class SurvivorState(
        string name,
        int escapedWave,
        int escapedTick,
        int returnWave,
        InjuryKind scar,
        PrototypeRememberedPlace? rememberedPlace)
    {
        public string Name { get; } = name;
        public int EscapedWave { get; } = escapedWave;
        public int EscapedTick { get; } = escapedTick;
        public int ReturnWave { get; } = returnWave;
        public InjuryKind Scar { get; } = scar;
        public PrototypeRememberedPlace? RememberedPlace { get; } = rememberedPlace;
        public string Status { get; set; } = "awaiting";
        public int? ReturnedAsRaiderId { get; set; }
    }

    private sealed class EventState(
        int tick,
        int creatureId,
        string reasonCode,
        Dictionary<string, int> details,
        JobKind? jobKind,
        GridPoint? target)
    {
        public int FirstTick { get; } = tick;
        public int LastTick { get; set; } = tick;
        public int CreatureId { get; } = creatureId;
        public string ReasonCode { get; } = reasonCode;
        public IReadOnlyDictionary<string, int> Details { get; } =
            details.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        public int Repeats { get; set; } = 1;
        public JobKind? JobKind { get; } = jobKind;
        public GridPoint? Target { get; } = target;
    }

    private sealed record MatchPair(
        CreatureState Creature,
        JobState Job,
        GridPoint InitialTarget,
        int Score,
        int Urgency,
        int Affinity,
        int Distance);

    private sealed record MovementIntent(
        CreatureState Creature,
        GridPoint Destination,
        GridPoint Next);

    private enum LarderAvailability
    {
        Available,
        Occupied,
        Unreachable,
    }
}
