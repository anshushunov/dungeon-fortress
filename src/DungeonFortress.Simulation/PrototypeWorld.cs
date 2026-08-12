using System.Text.Json;

namespace DungeonFortress.Simulation;

public sealed partial class PrototypeWorld
{
    private readonly PrototypeMap _map = new();
    private readonly PrototypeCommand[] _commands;
    private readonly Dictionary<ZoneKind, SortedSet<GridPoint>> _zones;
    private readonly Dictionary<JobKind, int> _priorities;
    private readonly Dictionary<string, int> _rules;
    private readonly List<CreatureState> _creatures;
    private readonly List<JobState> _jobs = [];
    private readonly Dictionary<GridPoint, BedState> _beds;
    private readonly Dictionary<(GridPoint Point, ResourceKind Resource), int> _loose = [];

    // Stored stone is canonical per-cell state, deliberately separate from the
    // loose pile on the same tile: "lying on the floor" and "put away in the
    // stockpile" are different game facts and must stay distinguishable.
    private readonly Dictionary<GridPoint, int> _storedStone = [];

    // Stone that already reached a construction site. It is deliberately a third
    // place material can sit, next to the loose pile and the stockpile cell: "on
    // its way into a post" is a different game fact from "put away".
    private readonly SortedDictionary<GridPoint, BuildSiteState> _buildSites = [];
    private readonly List<EventState> _events = [];
    private readonly List<RaiderState> _raiders = [];

    // The whole sequence exists from tick 0 with its timetable fixed; only the
    // composition of each wave is decided later, at its own announce tick, from
    // the renown standing at that moment. That is what makes pressure a
    // consequence of how the domain played rather than of a wave counter, and it
    // is the seam an event layer would replace without touching combat.
    private readonly List<WaveState> _waves;

    // Everybody who left the domain alive, oldest escape first. It is the whole
    // of the return rule's memory: a wave's composition is still decided from
    // renown alone, and this list only decides *who* fills the places that
    // composition allows (Issue #358).
    private readonly List<SurvivorState> _survivors = [];
    private readonly HashSet<string> _raiderNames = new(StringComparer.Ordinal);
    private DeterministicRandom _raiderNameRandom;

    // Deliberately not `readonly`, and the omission is the fix of Issue #361
    // rather than an oversight. DeterministicRandom is a mutable struct: it
    // carries its state in a field and advances it on every draw. C# answers a
    // mutating call on a `readonly` field of a struct type with a defensive
    // copy, so while this field was `readonly` the whole party's draws advanced
    // throwaway copies, `_state` never moved, and CombatJitter returned one and
    // the same number from the first blow to the last — T.raider_might_jitter
    // and T.damage_jitter were dead settings.
    //
    // The same reason applies to _raiderNameRandom above and to
    // SimulationWorld._random, which are declared the same way; the shape is
    // held to by No_readonly_field_of_the_simulation_holds_a_mutable_struct.
    private DeterministicRandom _combatRandom;

    /// <summary>
    /// Which part of a body a blow found. Its own stream, salted apart from
    /// combat for the reason <see cref="_raiderNameRandom"/> is: asking where a
    /// wound landed must not move the jitter of anybody's blow.
    ///
    /// <para>The separation is not tidiness. It is what let Issue #409 land the
    /// localisation of a wound as a <b>refactor with no behavioural delta at
    /// all</b>: the shipped journals produce the identical fight, the identical
    /// party score and the identical muster report before and after the four
    /// parts existed, because the fight never drew from this stream. Had the
    /// part been drawn from <see cref="_combatRandom"/>, every blow after the
    /// first wound in the party would have had a different jitter and nothing
    /// about the change could have been measured against what came before.</para>
    /// </summary>
    private DeterministicRandom _injuryRandom;
    private readonly Dictionary<GridPoint, int> _stationOccupiedTicks =
        PrototypeMap.KitchenTiles
            .Concat(PrototypeMap.AuthoredPostTiles)
            .ToDictionary(point => point, _ => 0);
    private readonly HashSet<GridPoint> _yieldReservations = [];
    private readonly SortedSet<GridPoint> _digDesignations = [];
    private long _nextJobId = 1;
    private int _nextCommandIndex;
    private int _stockRaw;
    private int _stockMeals = PrototypeTuning.StartMeals;
    private int _harvestsCompleted;
    private int _rawHaulsCompleted;
    private int _cookBatchesCompleted;
    private int _mealHaulsCompleted;
    private int _totalCreatureTicks;
    private int _foodWorkTicks;
    private int _restTicks;
    private int _eatTicks;
    private int _drillTicks;
    private int _watchTicks;
    private int _digTicks;
    private int _stoneHaulTicks;
    private int _digsCompleted;
    private int _stoneProduced;
    private int _stoneHaulsCompleted;
    private int _stoneStored;
    private int _stoneSpilled;
    private int _stoneDelivered;
    private int _stoneConsumed;
    private int _buildsCompleted;
    private int _buildTicks;
    private int _musterTicks;
    private int _idleTicks;
    private int _postCapacityTicks;
    private int _nextRaiderId;

    // The high-water mark of the larder, not its current contents. Renown must
    // never fall, and a raided larder is a loss the domain pays for with a
    // weaker answer to the next wave, not with a smaller score.
    private int _peakMeals = PrototypeTuning.StartMeals;
    private int _raidersDownedTotal;
    private int? _renownAtPreviousWave;
    private int? _strengthAtPreviousWave;
    private string? _sessionOutcome;
    private int? _sessionEndTick;

    public PrototypeWorld(PrototypeCommandLog commandLog)
    {
        ArgumentNullException.ThrowIfNull(commandLog);
        PrototypeCommandValidator.Validate(commandLog);
        Seed = commandLog.Seed;
        _commands = commandLog.Commands
            .Select(CloneCommand)
            .ToArray();
        _zones = PrototypeMap.CreateDefaultZones(_map);
        _priorities = new()
        {
            [JobKind.Harvest] = PrototypeTuning.DefaultHarvestPriority,
            [JobKind.Haul] = PrototypeTuning.DefaultHaulPriority,
            [JobKind.Cook] = PrototypeTuning.DefaultCookPriority,
            [JobKind.Rest] = PrototypeTuning.DefaultRestPriority,
            [JobKind.Drill] = PrototypeTuning.DefaultDrillPriority,
            [JobKind.Watch] = PrototypeTuning.DefaultWatchPriority,
            [JobKind.Dig] = PrototypeTuning.DefaultDigPriority,
            [JobKind.Build] = PrototypeTuning.DefaultBuildPriority,
        };
        _rules = new(StringComparer.Ordinal)
        {
            ["ration_reserve"] = PrototypeTuning.RationReserveDefault,
            ["drill_min_satiety"] = PrototypeTuning.DrillMinimumSatietyDefault,
            ["muster_lead_ticks"] = PrototypeTuning.MusterLeadDefault,
        };
        _beds = PrototypeMap.BedTiles
            .Select((point, index) => new BedState(point, index * PrototypeTuning.BedRipenessOffset))
            .ToDictionary(bed => bed.Position);
        _creatures = CreateCreatures(commandLog.Seed);
        _waves = CreateWaves();
        _combatRandom = new DeterministicRandom(commandLog.Seed ^ 0x636F6D626174UL);
        // Its own stream, salted apart from combat: drawing a name must not move
        // the jitter of anybody's blow (Issue #358).
        _raiderNameRandom = new DeterministicRandom(
            commandLog.Seed ^ PrototypeRaiderNames.StreamSalt);
        _injuryRandom = new DeterministicRandom(commandLog.Seed ^ 0x696E6A757279UL);
    }

    /// <summary>
    /// The timetable of the session. The first wave keeps the long runway the
    /// single raid used to have, so the player still gets a quiet stretch to
    /// learn the levers; every later wave is announced on the short lead, which
    /// is what turns one event into a rhythm.
    /// </summary>
    private static List<WaveState> CreateWaves()
    {
        var waves = new List<WaveState>();
        for (var number = 1; number <= PrototypeTuning.WaveCount; number++)
        {
            var arriveTick = PrototypeTuning.FirstRaidTick +
                (number - 1) * PrototypeTuning.WaveIntervalTicks;
            var announceTick = number == 1
                ? PrototypeTuning.ThreatAnnounceTick
                : arriveTick - PrototypeTuning.WaveAnnounceLead;
            waves.Add(new WaveState(number, announceTick, arriveTick));
        }

        return waves;
    }

    public ulong Seed { get; }

    public int CurrentTick { get; private set; }

    public int CommandsApplied { get; private set; }

    public int MealsProduced { get; private set; }

    public int MealsEaten { get; private set; }

    /// <summary>
    /// A party ends by itself: every wave seen through, or nobody left who can
    /// work and defend. <see cref="PrototypeTuning.SessionTicks"/> is no longer
    /// the end of the story, only the fuse that keeps a pathological run finite.
    /// </summary>
    public bool IsComplete =>
        CurrentTick >= PrototypeTuning.SessionTicks || _sessionOutcome is not null;

    /// <summary>
    /// Diagnostic only, and off by default: with it on, every tick additionally
    /// resolves the same job matching <b>as if memory of place did not exist</b>,
    /// and publishes what each creature would have been given through
    /// <see cref="MemoryProbes"/>.
    ///
    /// <para>
    /// It exists because Issue #125 states its criterion as a counterfactual —
    /// "the same tick, run with memory switched off, gives this creature the job
    /// the refusal names" — and a counterfactual cannot be measured by playing
    /// the party twice. The second party diverges on the first tick where memory
    /// changed anything, and from there on the two runs are different worlds, so
    /// "the same tick" no longer exists to compare. Resolved inside the tick it
    /// is exact, and it costs one extra matching pass, which is why play, the
    /// load stage and the canonical runs leave it off.
    /// </para>
    ///
    /// <para>
    /// It writes nothing. The canonical snapshot, the event log and the checksum
    /// are the same whether it is on or off, and
    /// <c>PrototypeMemoryTests.The_counterfactual_probe_changes_nothing_the_party_does</c>
    /// is the check that says so rather than the comment.
    /// </para>
    /// </summary>
    public bool TrackMemoryFreeMatching { get; set; }

    /// <summary>
    /// For the tick just stepped: every creature that either refused work by
    /// memory of place or would have been given work with memory switched off.
    /// Always empty unless <see cref="TrackMemoryFreeMatching"/> is on.
    /// </summary>
    public IReadOnlyList<MemoryProbe> MemoryProbes => _memoryProbe;

    private IReadOnlyList<MemoryProbe> _memoryProbe = [];

    /// <summary>
    /// One creature's counterfactual for one tick: the job its refusal by memory
    /// named, and the job the same tick would have given it had memory of place
    /// not existed. Either half can be absent.
    ///
    /// <para>
    /// It is diagnostic and deliberately not part of
    /// <see cref="PrototypeSnapshot"/>: the canonical document says what the
    /// world did, and what the world would have done under a rule it does not
    /// have is a question, not a fact about the party.
    /// </para>
    /// </summary>
    public sealed record MemoryProbe(
        int CreatureId,
        long? RefusedJobId,
        JobKind? RefusedKind,
        GridPoint? RefusedTarget,
        // The best pair this creature had in the memory-free collection, before
        // anyone competed for anything: the work it would have put first had
        // memory of place not existed. This is the half of the counterfactual
        // that is a fact about *this* creature, and it is what the refusal is
        // required to name.
        long? MemoryFreeBestJobId,
        // What the memory-free matching actually leaves this creature with once
        // everybody has competed. It can differ from the line above for one
        // reason only, and the two fields below say which.
        long? MemoryFreeJobId,
        JobKind? MemoryFreeKind,
        GridPoint? MemoryFreeTarget,
        // Who ends up with the refused job in the memory-free plan, and who ends
        // up starting work on the tile the refusal names. Between them they say
        // whether a creature that does not get the work it would have put first
        // lost it to somebody, which is the only thing allowed to stand there.
        int? MemoryFreeWinnerOfRefusedJob,
        int? MemoryFreeWinnerOfRefusedTile);

    /// <summary>
    /// Runs up to <paramref name="tickCount"/> ticks and stops early when the
    /// party ends. Stopping rather than throwing is what lets a caller say "play
    /// the whole session" without first knowing which tick it will end on.
    /// </summary>
    public void RunTicks(int tickCount)
    {
        if (tickCount < 0 || tickCount > PrototypeTuning.SessionTicks - CurrentTick)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tickCount),
                $"Tick count must leave the world between 0 and {PrototypeTuning.SessionTicks}.");
        }

        for (var index = 0; index < tickCount && !IsComplete; index++)
        {
            Step();
        }
    }

    public void Step()
    {
        if (IsComplete)
        {
            throw new InvalidOperationException("The prototype session has ended.");
        }

        // A party that owes the player an answer stops being a party until it
        // gets one: no phase below runs, and CurrentTick does not move. That is
        // the whole of "пауза наблюдаема прогоном" — the tick simply does not
        // happen.
        if (StepWhileAwaitingVerdict())
        {
            return;
        }

        ApplyCommands();
        AnnounceWaves();
        EnterRaiders();
        if (CurrentWave() is { } arriving && arriving.ArriveTick == CurrentTick)
        {
            foreach (var creature in _creatures)
            {
                creature.ReadinessAtRaid = ComputeReadiness(creature);
            }
        }

        UpdateCombatParticipation();

        RevalidateStoneHauls();
        CancelInvalidJobs();
        DecideNeedsAndMuster();
        GenerateJobs();
        MatchJobs();
        PlanTrafficActions();
        CountLaborForTick();
        ActCreatures();
        ActRaiders();
        CountPostOccupancyForTick();
        ApplyPassiveProcesses();
        // Loyalty is credited from what this tick published and nothing else, so
        // it sits after every phase that could publish anything and before the
        // party is judged to be over.
        AccrueLoyalty();
        // Order matters here and is part of the contract: the domain is declared
        // fallen while its people are still on the floor, and only afterwards do
        // the survivors of a finished wave get back up. Raising them first would
        // make a total wipe unobservable.
        ResolveSession();
        RaiseTheDowned();
        CurrentTick++;
        // Last of all, so that the tick the pause holds back is the one after the
        // wave was resolved and the cards are built from a finished tick.
        OpenPendingMomentOfTruth();
    }

    public PrototypeSnapshot GetSnapshot()
    {
        var zones = _zones
            .OrderBy(pair => pair.Key)
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<GridPoint>)[.. pair.Value]);
        var priorities = _priorities
            .OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var rules = _rules
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var creatures = _creatures
            .OrderBy(creature => creature.Id)
            .Select(ToSnapshot)
            .ToArray();
        var pendingCommands = _commands
            .Skip(_nextCommandIndex)
            .Select(ToSnapshot)
            .ToArray();
        var jobs = _jobs
            .OrderBy(job => job.Id)
            .Select(job => new PrototypeJobSnapshot(
                job.Id,
                job.Key,
                job.Kind,
                job.Origin,
                job.Target,
                job.Resource,
                job.Quantity,
                job.PersonalCreatureId,
                job.ReservedBy,
                job.RemainingTicks,
                job.ProgressTicks,
                job.PickedUp,
                job.StoreCell,
                job.StoreReserved,
                job.SourceCell))
            .ToArray();
        var beds = _beds.Values
            .OrderBy(bed => bed.Position)
            .Select(bed => new PrototypeBedSnapshot(
                bed.Position,
                bed.Growth,
                bed.IsRipe))
            .ToArray();
        var looseItems = _loose
            .Where(pair => pair.Value > 0)
            .OrderBy(pair => pair.Key.Point)
            .ThenBy(pair => pair.Key.Resource)
            .Select(pair => new PrototypeLooseItemSnapshot(
                pair.Key.Point,
                pair.Key.Resource,
                pair.Value))
            .ToArray();
        var stockpileCells = _zones[ZoneKind.MaterialStockpile]
            .Order()
            .Select(ToStockpileSnapshot)
            .ToArray();
        var stations = PrototypeMap.KitchenTiles
            .Concat(_map.PostTiles())
            .OrderBy(point => _map[point])
            .ThenBy(point => point)
            .Select(point => new PrototypeStationSnapshot(
                point,
                _map[point],
                _creatures
                    .Where(creature => IsUsingStation(creature, point))
                    .Select(creature => (int?)creature.Id)
                    .SingleOrDefault(),
                _stationOccupiedTicks[point]))
            .ToArray();
        var events = _events
            .Select(ToSnapshot)
            .ToArray();
        // DiggableTiles keeps the "what may be designated" rule in the simulation:
        // the Godot brush filters against this list instead of re-deriving it.
        var map = new PrototypeMapSnapshot(
            [.. _map.RockTiles()],
            [.. _map.RockTiles().Where(_map.IsDiggable)],
            [.. _map.ExcavatedTiles],
            [.. StockpileFloorTiles()],
            [.. BuildFloorTiles()],
            [.. _map.BuiltPostTiles]);
        var designations = _digDesignations
            .Select(ToSnapshot)
            .ToArray();
        var buildSites = _buildSites.Values
            .Select(ToSnapshot)
            .ToArray();

        return new PrototypeSnapshot(
            PrototypeCanonical.SchemaVersion,
            _nextJobId,
            Seed,
            CurrentTick,
            CommandsApplied,
            pendingCommands,
            creatures,
            zones,
            priorities,
            rules,
            map,
            designations,
            buildSites,
            beds,
            looseItems,
            stockpileCells,
            new PrototypeStockSnapshot(
                _stockRaw,
                _stockMeals,
                LooseCount(ResourceKind.RawMushroom),
                LooseCount(ResourceKind.Meal),
                LooseCount(ResourceKind.Stone),
                CarriedStoneTotal(),
                StoredStoneTotal(),
                SiteStoneTotal(),
                ReservedStoneTotal(),
                _zones[ZoneKind.MaterialStockpile].Count * PrototypeTuning.StockpileCellCapacity,
                PrototypeTuning.LarderCapacity,
                MealsProduced,
                MealsEaten),
            jobs,
            new PrototypeEconomyCountersSnapshot(
                _harvestsCompleted,
                _rawHaulsCompleted,
                _cookBatchesCompleted,
                _mealHaulsCompleted,
                MealsProduced,
                MealsEaten,
                _digsCompleted,
                _stoneProduced,
                _stoneHaulsCompleted,
                _stoneStored,
                _stoneSpilled,
                _stoneDelivered,
                _stoneConsumed,
                _buildsCompleted),
            new PrototypeLaborSnapshot(
                _totalCreatureTicks,
                _foodWorkTicks,
                _restTicks,
                _eatTicks,
                _drillTicks,
                _watchTicks,
                _digTicks,
                _stoneHaulTicks,
                _buildTicks,
                _musterTicks,
                _idleTicks,
                Percentage(_foodWorkTicks, _totalCreatureTicks),
                _map.PostTiles().Sum(point => _stationOccupiedTicks[point]),
                _postCapacityTicks,
                Percentage(
                    _map.PostTiles().Sum(point => _stationOccupiedTicks[point]),
                    _postCapacityTicks)),
            stations,
            events,
            ToSnapshot(CurrentWave() ?? _waves[^1]),
            _waves.Select(ToWaveSnapshot).ToArray(),
            new PrototypeDomainSnapshot(
                Renown(),
                DomainStrength(),
                _renownAtPreviousWave,
                _strengthAtPreviousWave,
                _creatures.Count(creature => creature.Mode != CreatureMode.Downed),
                _creatures.Count(creature => creature.Mode == CreatureMode.Downed),
                _creatures.Count(creature => creature.Injury != InjuryKind.None),
                _peakMeals,
                _waves.Count(wave => wave.Arrived),
                _waves.Count(wave => wave.Outcome is not null),
                _waves.Count),
            _raiders.OrderBy(raider => raider.Id).Select(raider => new PrototypeRaiderSnapshot(
                raider.Id, raider.Wave, raider.Hp, raider.Might, raider.Position, raider.CarryingMeals, raider.StealTicks, raider.ReturningToGate, raider.Mode,
                raider.Name, raider.ReturnedFromWave, raider.ScarFromLastTime, raider.RememberedPlace)).ToArray(),
            BuildSessionResult(),
            // Derived here and stored nowhere (ADR 0013, variant C): a room is
            // whatever the zones and the map add up to at this tick, so it cannot
            // fall out of step with them and no command creates one directly.
            PrototypeRooms.Derive(_map, _zones, _priorities),
            ToMomentOfTruthSnapshot(),
            [.. SurvivorSnapshots()]);
    }

    /// <summary>
    /// The end of the party as a set of facts, and — once there is an end —
    /// the single number those facts add up to.
    /// </summary>
    private PrototypeSessionResultSnapshot BuildSessionResult()
    {
        var wavesRepelled = _waves.Count(WasRepelled);
        var defendersDowned = _waves.Sum(wave => wave.DefendersDowned);
        var defendersFled = _waves.Sum(wave => wave.DefendersFled);
        var mealsStolen = _raiders
            .Where(raider => raider.Mode == RaiderMode.Escaped)
            .Sum(raider => raider.CarryingMeals);

        return new PrototypeSessionResultSnapshot(
            _sessionOutcome,
            _sessionEndTick,
            _sessionOutcome is null && CurrentTick >= PrototypeTuning.SessionTicks,
            _waves.LastOrDefault(wave => wave.Outcome is not null)?.Outcome,
            _waves.Count(wave => wave.Outcome is not null),
            wavesRepelled,
            _waves.Count,
            Renown(),
            DomainStrength(),
            defendersDowned,
            defendersFled,
            _raiders.Count(raider => raider.Mode == RaiderMode.Downed),
            mealsStolen,
            _stockMeals,
            // A party that has not ended has no score, and one cut short by the
            // fuse never will: it did not end, it was stopped. The number is
            // read once, at the end, and never during the party (ADR 0016).
            _sessionOutcome is null
                ? null
                : PrototypePartyScore.Compute(
                    _sessionOutcome,
                    wavesRepelled,
                    _creatures.Count(CanWorkAndDefend),
                    _stockMeals,
                    mealsStolen,
                    defendersDowned + defendersFled));
    }

    /// <summary>
    /// The wave the panel is about. Before the last wave is resolved this is the
    /// one in hand; afterwards it stays on the last one, so the countdown never
    /// points at a wave that will not come.
    /// </summary>
    private PrototypeThreatSnapshot ToSnapshot(WaveState wave) =>
        new(
            wave.Announced,
            wave.Number,
            _waves.Count,
            wave.AnnounceTick,
            wave.ArriveTick,
            wave.RaiderCount,
            wave.RaiderMight,
            Math.Max(0, wave.ArriveTick - CurrentTick),
            WaveRaiders(wave).Any(raider => raider.Mode == RaiderMode.Raiding));

    private PrototypeWaveSnapshot ToWaveSnapshot(WaveState wave) =>
        new(
            wave.Number,
            wave.AnnounceTick,
            wave.ArriveTick,
            wave.Announced,
            wave.Arrived,
            wave.RaiderCount,
            wave.RaiderMight,
            wave.Outcome,
            wave.EndTick,
            wave.Outcome is null
                ? WaveRaiders(wave).Count(raider => raider.Mode == RaiderMode.Downed)
                : wave.RaidersDowned,
            wave.DefendersDowned,
            wave.DefendersFled,
            wave.Outcome is null
                ? WaveRaiders(wave)
                    .Where(raider => raider.Mode == RaiderMode.Escaped)
                    .Sum(raider => raider.CarryingMeals)
                : wave.MealsStolen,
            wave.RenownAtAnnounce);

    private static IReadOnlyDictionary<JobKind, int> Affinities(params (JobKind Kind, int Value)[] values)
    {
        return values.OrderBy(value => value.Kind).ToDictionary(value => value.Kind, value => value.Value);
    }

    private static PrototypeCommand CloneCommand(PrototypeCommand command)
    {
        return command switch
        {
            ZonePaintCommand paint => paint with { Tiles = paint.Tiles.ToArray() },
            ZoneEraseCommand erase => erase with { Tiles = erase.Tiles.ToArray() },
            DigDesignateCommand designate => designate with { Tiles = designate.Tiles.ToArray() },
            DigCancelCommand cancel => cancel with { Tiles = cancel.Tiles.ToArray() },
            BuildDesignateCommand build => build with { Tiles = build.Tiles.ToArray() },
            BuildCancelCommand unbuild => unbuild with { Tiles = unbuild.Tiles.ToArray() },
            SetPriorityCommand priority => priority,
            SetRuleCommand rule => rule,
            VerdictCommand verdict => verdict,
            _ => throw new InvalidDataException(
                $"Unsupported prototype command: {command.GetType().Name}"),
        };
    }

    /// <summary>
    /// The nine, where they stand at tick 0.
    ///
    /// Three of the starting tiles moved with the dungeon of Issue #117 and the
    /// reason is named for each, because "the fixture changed" is not a reason:
    ///
    /// - Мотылёк was on <c>(13,9)</c>, which the wall between the kitchen and
    ///   the larder now runs through. It moved into the larder, which is where a
    ///   carrier belongs;
    /// - Прель was on <c>(10,9)</c> and Уголёк on <c>(21,9)</c>. Both tiles
    ///   survived the change, and both became **doors** — the kitchen's only way
    ///   to the spine and the quarters' only way down. A creature standing in a
    ///   doorway on tick 0 blocks the room behind it for as long as it takes to
    ///   pick a job, which is a start position picking a fight with the traffic
    ///   arbitration. Both stepped one tile back into their own room.
    ///
    /// Everything else — id, name, might, grit, affinities — is untouched.
    /// </summary>
    private static List<CreatureState> CreateCreatures(ulong seed)
    {
        var random = new DeterministicRandom(seed ^ 0x776F726C645F696EUL);
        var definitions = new[]
        {
            new CreatureDefinition(0, "Брусок", 2, 3, Affinities((JobKind.Cook, 2)), new(11, 8)),
            new CreatureDefinition(1, "Кремень", 4, 4, Affinities((JobKind.Watch, 2)), new(24, 12)),
            new CreatureDefinition(2, "Мотылёк", 1, 2, Affinities((JobKind.Haul, 2)), new(16, 8)),
            new CreatureDefinition(3, "Смола", 2, 3, Affinities((JobKind.Harvest, 2)), new(4, 3)),
            new CreatureDefinition(4, "Дёготь", 3, 2, Affinities((JobKind.Harvest, 1), (JobKind.Haul, 1)), new(6, 5)),
            new CreatureDefinition(5, "Уголёк", 3, 3, Affinities((JobKind.Drill, 2)), new(20, 5)),
            new CreatureDefinition(6, "Прель", 1, 4, Affinities((JobKind.Cook, 1), (JobKind.Harvest, 1)), new(9, 8)),
            new CreatureDefinition(7, "Обух", 5, 2, Affinities((JobKind.Watch, 1)), new(25, 13)),
            new CreatureDefinition(8, "Тишина", 2, 5, Affinities((JobKind.Haul, 1), (JobKind.Drill, 1)), new(17, 10)),
        };

        return definitions.Select(definition =>
        {
            var satietyJitter = random.NextInt32(PrototypeTuning.StartJitter * 2 + 1) -
                PrototypeTuning.StartJitter;
            var fatigueJitter = random.NextInt32(PrototypeTuning.StartJitter * 2 + 1) -
                PrototypeTuning.StartJitter;
            return new CreatureState(definition)
            {
                Satiety = PrototypeTuning.StartSatiety + satietyJitter,
                Fatigue = PrototypeTuning.StartFatigue + fatigueJitter,
                LastDecision = new PrototypeDecision(
                    0,
                    "waiting_no_job_available",
                    new Dictionary<string, int>()),
            };
        }).ToList();
    }
}
