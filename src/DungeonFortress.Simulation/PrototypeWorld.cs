using System.Text.Json;

namespace DungeonFortress.Simulation;

public sealed class PrototypeWorld
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
    private readonly DeterministicRandom _combatRandom;
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
        // Order matters here and is part of the contract: the domain is declared
        // fallen while its people are still on the floor, and only afterwards do
        // the survivors of a finished wave get back up. Raising them first would
        // make a total wipe unobservable.
        ResolveSession();
        RaiseTheDowned();
        CurrentTick++;
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
                raider.Id, raider.Wave, raider.Hp, raider.Might, raider.Position, raider.CarryingMeals, raider.StealTicks, raider.ReturningToGate, raider.Mode)).ToArray(),
            BuildSessionResult());
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

    private void ApplyCommands()
    {
        while (_nextCommandIndex < _commands.Length &&
               _commands[_nextCommandIndex].Tick == CurrentTick)
        {
            ApplyCommand(_commands[_nextCommandIndex]);
            _nextCommandIndex++;
            CommandsApplied++;
        }
    }

    private void ApplyCommand(PrototypeCommand command)
    {
        switch (command)
        {
            case ZonePaintCommand paint:
                ValidateZoneTiles(paint.ZoneKind, paint.Tiles, painting: true);
                foreach (var tile in paint.Tiles)
                {
                    _zones[paint.ZoneKind].Add(tile);
                }

                break;
            case ZoneEraseCommand erase:
                ValidateZoneTiles(erase.ZoneKind, erase.Tiles, painting: false);
                if (erase.ZoneKind == ZoneKind.Larder)
                {
                    var remaining = new SortedSet<GridPoint>(_zones[ZoneKind.Larder]);
                    remaining.ExceptWith(erase.Tiles);
                    if (!remaining.Any(tile => _map[tile] == TileKind.Larder))
                    {
                        throw new InvalidDataException(
                            "zone_erase would remove the final larder feature from Larder.");
                    }
                }

                foreach (var tile in erase.Tiles)
                {
                    if (!_zones[erase.ZoneKind].Remove(tile))
                    {
                        continue;
                    }

                    if (erase.ZoneKind == ZoneKind.MaterialStockpile)
                    {
                        SpillStoredStone(tile);
                    }
                }

                break;
            case DigDesignateCommand designate:
                ApplyDigDesignate(designate);
                break;
            case DigCancelCommand cancel:
                ApplyDigCancel(cancel);
                break;
            case BuildDesignateCommand build:
                ApplyBuildDesignate(build);
                break;
            case BuildCancelCommand unbuild:
                ApplyBuildCancel(unbuild);
                break;
            case SetPriorityCommand priority:
                _priorities[priority.JobKind] = priority.Value;
                break;
            case SetRuleCommand rule:
                _rules[rule.RuleId] = rule.Value;
                break;
            default:
                throw new InvalidDataException($"Unsupported prototype command: {command.GetType().Name}");
        }
    }

    /// <summary>
    /// Strict and atomic: every tile is checked against the live map before the
    /// first designation is recorded, so a rejected command mutates nothing.
    /// Designating an already designated tile is a no-op, matching zone_paint.
    /// </summary>
    private void ApplyDigDesignate(DigDesignateCommand command)
    {
        foreach (var tile in command.Tiles)
        {
            if (!_map.IsDiggable(tile))
            {
                throw new InvalidDataException(
                    $"Dig tile ({tile.X},{tile.Y}) is not diggable rock. " +
                    "Floor, features, the gate and the map boundary cannot be designated.");
            }
        }

        var added = command.Tiles.Count(tile => !_digDesignations.Contains(tile));
        if (_digDesignations.Count + added > PrototypeTuning.MaximumDigDesignations)
        {
            throw new InvalidDataException(
                $"A session cannot hold more than {PrototypeTuning.MaximumDigDesignations} " +
                "dig designations.");
        }

        foreach (var tile in command.Tiles)
        {
            _digDesignations.Add(tile);
        }
    }

    /// <summary>
    /// Tolerant, like zone_erase: tiles without a designation are simply skipped.
    /// Releasing the owning job in the same step keeps the world from holding a
    /// reservation or half-finished progress for an intent the player withdrew.
    /// </summary>
    private void ApplyDigCancel(DigCancelCommand command)
    {
        foreach (var tile in command.Tiles)
        {
            if (!_digDesignations.Remove(tile))
            {
                continue;
            }

            var job = _jobs.FirstOrDefault(
                item => item.Kind == JobKind.Dig && item.Origin == tile);
            if (job is null)
            {
                continue;
            }

            var worker = _creatures.FirstOrDefault(creature => creature.CurrentJob == job);
            if (worker is not null)
            {
                CancelJob(worker, "dig_cancelled");
            }

            _jobs.Remove(job);
        }
    }

    /// <summary>
    /// Strict and atomic, exactly like <see cref="ApplyDigDesignate"/>: every tile
    /// is checked against the live map before the first blueprint is recorded.
    /// Designating an already designated tile is a no-op, matching zone_paint.
    /// </summary>
    private void ApplyBuildDesignate(BuildDesignateCommand command)
    {
        foreach (var tile in command.Tiles)
        {
            if (!_map.IsBuildableFloor(tile))
            {
                throw new InvalidDataException(
                    $"Build tile ({tile.X},{tile.Y}) is not plain floor. " +
                    "Rock, map features, the gate, the map boundary and an existing " +
                    "post cannot hold a blueprint.");
            }

            if (_zones[ZoneKind.MaterialStockpile].Contains(tile))
            {
                throw new InvalidDataException(
                    $"Build tile ({tile.X},{tile.Y}) is a material stockpile cell. " +
                    "Erase the cell first; a building site is not a warehouse.");
            }
        }

        var added = command.Tiles.Count(tile => !_buildSites.ContainsKey(tile));
        if (_buildSites.Count + added > PrototypeTuning.MaximumBuildDesignations)
        {
            throw new InvalidDataException(
                $"A session cannot hold more than {PrototypeTuning.MaximumBuildDesignations} " +
                "build designations.");
        }

        foreach (var tile in command.Tiles)
        {
            if (!_buildSites.ContainsKey(tile))
            {
                _buildSites[tile] = new BuildSiteState(tile);
            }
        }
    }

    /// <summary>
    /// Tolerant, like zone_erase and dig_cancel. Stone already delivered to the
    /// site becomes a loose pile on the very same tile, so withdrawing an intent
    /// never destroys material and never teleports it.
    /// </summary>
    private void ApplyBuildCancel(BuildCancelCommand command)
    {
        foreach (var tile in command.Tiles)
        {
            if (!_buildSites.TryGetValue(tile, out var site))
            {
                continue;
            }

            _buildSites.Remove(tile);
            if (site.Delivered > 0)
            {
                AddLoose(tile, ResourceKind.Stone, site.Delivered);
                _stoneSpilled += site.Delivered;
            }

            var job = _jobs.FirstOrDefault(
                item => item.Kind == JobKind.Build && item.Origin == tile);
            if (job is not null)
            {
                var worker = _creatures.FirstOrDefault(creature => creature.CurrentJob == job);
                if (worker is not null)
                {
                    CancelJob(worker, "build_cancelled");
                }

                _jobs.Remove(job);
            }
        }
    }

    /// <summary>
    /// A designation is workable when at least one orthogonal neighbour is a
    /// passable, non-forbidden tile that is not the gate. Reachability is checked
    /// here rather than at command time because digging changes it.
    /// </summary>
    private IEnumerable<GridPoint> DigApproachTiles(GridPoint rock)
    {
        return PrototypeMap.Neighbors(rock)
            .Where(neighbor =>
                _map.IsPassable(neighbor) &&
                neighbor != PrototypeMap.Gate &&
                !_zones[ZoneKind.Forbidden].Contains(neighbor))
            .Order();
    }

    private bool IsDigReachable(GridPoint rock)
    {
        return DigApproachTiles(rock).Any();
    }

    private bool TryFindDigApproach(
        CreatureState creature,
        GridPoint rock,
        out GridPoint target)
    {
        var candidate = DigApproachTiles(rock)
            .Select(tile => new
            {
                Tile = tile,
                Distance = _map.Distance(
                    creature.Position,
                    tile,
                    _zones[ZoneKind.Forbidden]),
            })
            .Where(item => item.Distance is not null)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Tile)
            .FirstOrDefault();
        if (candidate is null)
        {
            target = default;
            return false;
        }

        target = candidate.Tile;
        return true;
    }

    private void ValidateZoneTiles(
        ZoneKind zoneKind,
        IReadOnlyList<GridPoint> tiles,
        bool painting)
    {
        foreach (var tile in tiles)
        {
            if (!_map.IsPassable(tile))
            {
                throw new InvalidDataException($"Zone tile ({tile.X},{tile.Y}) is not passable.");
            }

            if (painting && _map[tile] == TileKind.Gate)
            {
                throw new InvalidDataException("The gate cannot belong to a zone.");
            }

            if (painting && zoneKind == ZoneKind.Forbidden && _map[tile] == TileKind.Larder)
            {
                throw new InvalidDataException("A larder feature cannot be Forbidden.");
            }

            // Material may only be stored on floor that was already floor when the
            // session began; zoning freshly excavated ground is step 3. Unlike a
            // dig designation, the initial layout answers this completely, so the
            // pre-flight is already sufficient and this stays a guard: it keeps the
            // rule attached to the mutation it protects.
            if (painting && zoneKind == ZoneKind.MaterialStockpile &&
                !_map.StockpileFloorTiles().Contains(tile))
            {
                throw new InvalidDataException(
                    $"MaterialStockpile tile ({tile.X},{tile.Y}) is not pre-existing plain floor. " +
                    "Map features and freshly excavated ground cannot store material yet.");
            }

            if (painting && zoneKind == ZoneKind.MaterialStockpile &&
                _buildSites.ContainsKey(tile))
            {
                throw new InvalidDataException(
                    $"MaterialStockpile tile ({tile.X},{tile.Y}) carries a construction " +
                    "blueprint. A building site is not a warehouse.");
            }
        }
    }

    /// <summary>
    /// Erasing a stockpile cell must never destroy what it held. The stone stops
    /// being stored and becomes a loose pile on the very same tile, so the total
    /// amount of stone in the world is unchanged by the command.
    /// </summary>
    private void SpillStoredStone(GridPoint tile)
    {
        var stored = _storedStone.GetValueOrDefault(tile);
        if (stored <= 0)
        {
            return;
        }

        _storedStone.Remove(tile);
        AddLoose(tile, ResourceKind.Stone, stored);
        _stoneSpilled += stored;
    }

    private void CancelInvalidJobs()
    {
        foreach (var creature in _creatures)
        {
            var job = creature.CurrentJob;
            if (job is null)
            {
                continue;
            }

            var invalid = _priorities[job.Kind] == 0 ||
                !JobStillSupported(job) ||
                (job.Kind == JobKind.Drill &&
                 creature.Satiety < _rules["drill_min_satiety"]);
            if (!invalid)
            {
                continue;
            }

            var reason = _priorities[job.Kind] == 0
                ? "refused_priority_zero"
                : job.Kind == JobKind.Drill
                    ? "refused_rule_min_satiety"
                    : job.Kind == JobKind.Dig
                        ? "dig_cancelled"
                        : job.Kind == JobKind.Build
                            ? "build_cancelled"
                            : "refused_zone_not_designated";
            CancelJob(creature, reason);
        }
    }

    private bool JobStillSupported(JobState job)
    {
        return job.Kind switch
        {
            JobKind.Harvest => _zones[ZoneKind.Farm].Contains(job.Origin),
            JobKind.Haul => job.PickedUp ||
                (job.SourceCell is { } source
                    ? StoredStoneAt(source) > 0 &&
                      _zones[ZoneKind.MaterialStockpile].Contains(source)
                    : HasLoose(job.Origin, job.Resource!.Value)),
            JobKind.Cook => _zones[ZoneKind.Kitchen].Contains(job.Origin),
            JobKind.Rest => _zones[ZoneKind.Quarters].Contains(job.Origin),
            JobKind.Drill => _zones[ZoneKind.TrainingGround].Contains(job.Origin),
            JobKind.Watch => _zones[ZoneKind.Watch].Contains(job.Origin),
            JobKind.Dig => _digDesignations.Contains(job.Origin) && _map.IsDiggable(job.Origin),
            JobKind.Build => _buildSites.TryGetValue(job.Origin, out var site) &&
                site.Delivered >= PrototypeTuning.BuildStoneCost &&
                _map.IsBuildableFloor(job.Origin),
            _ => false,
        };
    }

    private void GenerateJobs()
    {
        var desired = new HashSet<string>(StringComparer.Ordinal);
        if (_priorities[JobKind.Harvest] > 0 &&
            ZoneCoversFeature(ZoneKind.Farm, TileKind.Bed) &&
            _stockRaw < PrototypeTuning.RawTarget)
        {
            foreach (var bed in _beds.Values.OrderBy(bed => bed.Position))
            {
                if (bed.IsRipe)
                {
                    EnsureJob(
                        $"harvest:{bed.Position.X}:{bed.Position.Y}",
                        JobKind.Harvest,
                        bed.Position,
                        null,
                        0,
                        desired);
                }
            }
        }

        if (_priorities[JobKind.Haul] > 0)
        {
            // One Haul kind, two destinations. Food goes to the larder and needs a
            // larder feature in the zone; stone goes to a material stockpile cell
            // and needs free capacity there. Neither gate affects the other.
            var larderReady = ZoneCoversFeature(ZoneKind.Larder, TileKind.Larder);
            // Two destinations now compete for the same material: a stockpile cell
            // with free capacity and a construction site that still needs stone.
            // A pile is worth a job if either of them can take it.
            var buildDemand = AvailableBuildDemand();
            var stoneCapacity = AvailableStoneCapacity() + buildDemand;
            foreach (var entry in _loose
                         .Where(pair => pair.Value > 0)
                         .OrderBy(pair => pair.Key.Point)
                         .ThenBy(pair => pair.Key.Resource))
            {
                if (entry.Key.Resource == ResourceKind.Stone)
                {
                    if (stoneCapacity <= 0)
                    {
                        continue;
                    }

                    EnsureJob(
                        $"haul:{entry.Key.Point.X}:{entry.Key.Point.Y}:{entry.Key.Resource}",
                        JobKind.Haul,
                        entry.Key.Point,
                        entry.Key.Resource,
                        Math.Min(PrototypeTuning.StoneCarryCapacity, entry.Value),
                        desired);
                    continue;
                }

                if (!larderReady)
                {
                    continue;
                }

                EnsureJob(
                    $"haul:{entry.Key.Point.X}:{entry.Key.Point.Y}:{entry.Key.Resource}",
                    JobKind.Haul,
                    entry.Key.Point,
                    entry.Key.Resource,
                    Math.Min(PrototypeTuning.CarryCapacity, entry.Value),
                    desired);
            }

            // Stored stone becomes movable again only when a construction site is
            // asking for it. Without that gate a stockpile would shuffle its own
            // contents between cells forever.
            if (buildDemand > 0)
            {
                foreach (var cell in UsableStockpileCells().Where(tile => StoredStoneAt(tile) > 0))
                {
                    EnsureJob(
                        $"supply:{cell.X}:{cell.Y}:{ResourceKind.Stone}",
                        JobKind.Haul,
                        cell,
                        ResourceKind.Stone,
                        Math.Min(PrototypeTuning.StoneCarryCapacity, StoredStoneAt(cell)),
                        desired,
                        sourceCell: cell);
                }
            }
        }

        if (_priorities[JobKind.Cook] > 0 &&
            ZoneCoversFeature(ZoneKind.Kitchen, TileKind.Kitchen) &&
            _stockMeals < PrototypeTuning.MealTarget)
        {
            var reservedRaw = _jobs
                .Where(job => job.Kind == JobKind.Cook && job.ReservedBy is not null && !job.PickedUp)
                .Sum(job => job.Quantity);
            var availableBatches = Math.Max(0, _stockRaw - reservedRaw) / PrototypeTuning.CookInput;
            foreach (var station in PrototypeMap.KitchenTiles
                         .Where(tile => _zones[ZoneKind.Kitchen].Contains(tile))
                         .Order())
            {
                if (availableBatches-- <= 0)
                {
                    break;
                }

                EnsureJob(
                    $"cook:{station.X}:{station.Y}",
                    JobKind.Cook,
                    station,
                    ResourceKind.RawMushroom,
                    PrototypeTuning.CookInput,
                    desired);
            }
        }

        if (_priorities[JobKind.Rest] > 0 &&
            ZoneCoversFeature(ZoneKind.Quarters, TileKind.Bunk))
        {
            var claimed = _jobs
                .Where(job => job.Kind == JobKind.Rest && job.ReservedBy is not null)
                .Select(job => job.Origin)
                .ToHashSet();
            foreach (var creature in _creatures
                         .Where(creature =>
                             (creature.Fatigue >= PrototypeTuning.RestSeekThreshold ||
                              creature.Injury != InjuryKind.None) &&
                             !creature.IsMustering &&
                             creature.Mode is not (CreatureMode.Eating or CreatureMode.Downed
                                 or CreatureMode.Fled or CreatureMode.Fighting) &&
                             creature.Satiety >= PrototypeTuning.CollapseThreshold)
                         .OrderBy(creature => creature.Id))
            {
                if (!TryNearestRestTarget(creature, claimed, out var bunk))
                {
                    continue;
                }

                claimed.Add(bunk);
                EnsureJob(
                    $"rest:{creature.Id}",
                    JobKind.Rest,
                    bunk,
                    null,
                    0,
                    desired,
                    creature.Id);
            }
        }

        if (_priorities[JobKind.Drill] > 0 &&
            ZoneCoversFeature(ZoneKind.TrainingGround, TileKind.Post))
        {
            foreach (var post in _map.PostTiles()
                         .Where(tile => _zones[ZoneKind.TrainingGround].Contains(tile))
                         .Order())
            {
                EnsureJob(
                    $"drill:{post.X}:{post.Y}",
                    JobKind.Drill,
                    post,
                    null,
                    0,
                    desired);
            }
        }

        if (_priorities[JobKind.Watch] > 0 && _zones[ZoneKind.Watch].Count > 0)
        {
            foreach (var tile in _zones[ZoneKind.Watch].Take(PrototypeTuning.WatchSlots))
            {
                EnsureJob(
                    $"watch:{tile.X}:{tile.Y}",
                    JobKind.Watch,
                    tile,
                    null,
                    0,
                    desired);
            }
        }

        if (_priorities[JobKind.Dig] > 0)
        {
            foreach (var tile in _digDesignations)
            {
                if (!_map.IsDiggable(tile) || !IsDigReachable(tile))
                {
                    continue;
                }

                EnsureJob(
                    $"dig:{tile.X}:{tile.Y}",
                    JobKind.Dig,
                    tile,
                    null,
                    0,
                    desired);
            }
        }

        // Construction is the last kind on purpose: a blueprint whose material has
        // not arrived yet creates no work at all, so "waiting for stone" and
        // "waiting for a builder" stay different, separately explained states.
        if (_priorities[JobKind.Build] > 0)
        {
            foreach (var site in _buildSites.Values
                         .Where(item =>
                             item.Delivered >= PrototypeTuning.BuildStoneCost &&
                             IsBuildSiteWorkable(item.Tile)))
            {
                EnsureJob(
                    $"build:{site.Tile.X}:{site.Tile.Y}",
                    JobKind.Build,
                    site.Tile,
                    null,
                    0,
                    desired);
            }
        }

        _jobs.RemoveAll(job => job.ReservedBy is null && !desired.Contains(job.Key));
    }

    private void EnsureJob(
        string key,
        JobKind kind,
        GridPoint target,
        ResourceKind? resource,
        int quantity,
        HashSet<string> desired,
        int? personalCreatureId = null,
        GridPoint? sourceCell = null)
    {
        desired.Add(key);
        if (_jobs.Any(job => job.Key == key))
        {
            return;
        }

        _jobs.Add(new JobState(
            _nextJobId++,
            key,
            kind,
            target,
            resource,
            quantity,
            personalCreatureId)
        {
            SourceCell = sourceCell,
        });
    }

    private void DecideNeedsAndMuster()
    {
        var musterActive = IsMusterActive();
        foreach (var creature in _creatures.OrderBy(creature => creature.Id))
        {
            if (musterActive)
            {
                if (!creature.IsMustering)
                {
                    if (creature.CurrentJob is not null)
                    {
                        CancelJob(creature, "chosen_muster");
                    }

                    creature.IsMustering = true;
                    creature.MusterTarget = MusterTargetFor(creature.Id);
                    creature.MusterNeedsRation =
                        creature.Satiety < PrototypeTuning.RationSatietyGate &&
                        AvailableMealsForReservation() > 0;
                    if (creature.MusterNeedsRation)
                    {
                        creature.MealReserved = true;
                        RecordDecision(
                            creature,
                            "chosen_ration",
                            new Dictionary<string, int>
                            {
                                ["satiety"] = creature.Satiety,
                                ["gate"] = PrototypeTuning.RationSatietyGate,
                            });
                    }
                    else
                    {
                        RecordDecision(
                            creature,
                            "chosen_muster",
                            new Dictionary<string, int>
                            {
                                ["raidTick"] = CurrentWave()?.ArriveTick ?? 0,
                                ["leadTicks"] = _rules["muster_lead_ticks"],
                            });
                    }
                }

                if (!creature.MusterNeedsRation &&
                    !creature.MealReserved &&
                    creature.Satiety < PrototypeTuning.RationSatietyGate &&
                    AvailableMealsForReservation() > 0)
                {
                    creature.MusterNeedsRation = true;
                    creature.MealReserved = true;
                    RecordDecision(
                        creature,
                        "chosen_ration",
                        new Dictionary<string, int>
                        {
                            ["satiety"] = creature.Satiety,
                            ["gate"] = PrototypeTuning.RationSatietyGate,
                        });
                }

                continue;
            }

            if (creature.IsMustering)
            {
                continue;
            }

            if (creature.Satiety < PrototypeTuning.CollapseThreshold)
            {
                if (TryStartEating(creature, ignoreReserve: false, "chosen_need_hunger"))
                {
                    continue;
                }

                if (creature.CurrentJob is not null)
                {
                    CancelJob(creature, "refused_too_exhausted");
                }

                RecordDecision(
                    creature,
                    "refused_too_exhausted",
                    new Dictionary<string, int>
                    {
                        ["satiety"] = creature.Satiety,
                        ["threshold"] = PrototypeTuning.CollapseThreshold,
                    });
                continue;
            }

            if (creature.Satiety < PrototypeTuning.EatThreshold &&
                creature.Mode != CreatureMode.Eating)
            {
                if (TryStartEating(creature, ignoreReserve: false, "chosen_need_hunger"))
                {
                    continue;
                }

                var reason = _stockMeals - ReservedMeals() <= 0
                    ? "waiting_input_missing"
                    : "refused_rule_reserve";
                RecordDecision(
                    creature,
                    reason,
                    new Dictionary<string, int>
                    {
                        ["meals"] = _stockMeals,
                        ["reserve"] = _rules["ration_reserve"],
                    });
            }

            if ((creature.Fatigue > PrototypeTuning.RestThreshold ||
                 creature.Injury != InjuryKind.None) &&
                !creature.IsMustering)
            {
                creature.NeedsRest = TryNearestRestTarget(
                    creature,
                    new HashSet<GridPoint>(),
                    out _);
                if (creature.NeedsRest &&
                    creature.CurrentJob is { Kind: not JobKind.Rest })
                {
                    CancelJob(creature, "chosen_need_fatigue");
                }
                else if (!creature.NeedsRest && _priorities[JobKind.Rest] == 0)
                {
                    RecordDecision(
                        creature,
                        "refused_priority_zero",
                        new Dictionary<string, int>
                        {
                            ["fatigue"] = creature.Fatigue,
                            ["threshold"] = PrototypeTuning.RestThreshold,
                        },
                        JobKind.Rest);
                }
            }
            else
            {
                creature.NeedsRest = false;
            }
        }

        if (musterActive)
        {
            foreach (var creature in _creatures
                         .Where(creature => creature.MusterNeedsRation)
                         .OrderBy(creature => creature.Id))
            {
                creature.SpecialTarget = CanAdvanceMealQueue(creature, out var target)
                    ? target
                    : null;
            }
        }
    }

    private bool TryStartEating(CreatureState creature, bool ignoreReserve, string reason)
    {
        if (creature.Mode == CreatureMode.Eating)
        {
            return true;
        }

        if (!ignoreReserve &&
            ReservedMeals() >= ActiveLarderTiles().Count())
        {
            return false;
        }

        var available = AvailableMealsForReservation();
        if (available <= 0)
        {
            return false;
        }

        if (!ignoreReserve &&
            _stockMeals - ReservedMeals() - 1 < _rules["ration_reserve"])
        {
            return false;
        }

        if (!TryFindLarderTarget(
                creature,
                out var larder,
                out _))
        {
            RecordDecision(
                creature,
                "refused_zone_unreachable",
                new Dictionary<string, int>
                {
                    ["zoneKind"] = (int)ZoneKind.Larder,
                });
            return false;
        }

        if (creature.CurrentJob is not null)
        {
            CancelJob(creature, reason);
        }

        creature.MealReserved = true;
        creature.Mode = CreatureMode.Eating;
        creature.SpecialTarget = larder;
        creature.SpecialTicks = PrototypeTuning.EatTicks;
        RecordDecision(
            creature,
            reason,
            new Dictionary<string, int>
            {
                ["satiety"] = creature.Satiety,
                ["threshold"] = PrototypeTuning.EatThreshold,
            },
            target: creature.SpecialTarget);
        return true;
    }

    private void PlanTrafficActions()
    {
        _yieldReservations.Clear();
        foreach (var creature in _creatures)
        {
            creature.TrafficTarget = null;
            creature.WaitThisTick = false;
        }

        var intents = _creatures
            .Select(CreateMovementIntent)
            .Where(intent => intent is not null)
            .Cast<MovementIntent>()
            .ToArray();
        foreach (var contenders in intents.GroupBy(intent => intent.Next))
        {
            var ordered = contenders
                .OrderByDescending(intent => IsUrgentMover(intent.Creature))
                .ThenByDescending(intent => intent.Creature.BlockedTicks)
                .ThenBy(intent => FairnessKey(intent.Creature))
                .ThenBy(intent => intent.Creature.Id)
                .ToArray();
            foreach (var loser in ordered.Skip(1))
            {
                loser.Creature.WaitThisTick = true;
            }
        }

        var active = intents
            .Where(intent => !intent.Creature.WaitThisTick)
            .ToDictionary(intent => intent.Creature);
        var occupants = _creatures.ToDictionary(creature => creature.Position);
        var requested = active.Values.Select(intent => intent.Next).ToHashSet();
        var handledCycles = new HashSet<int>();

        foreach (var root in active.Keys.OrderBy(creature => creature.Id))
        {
            var chain = new List<CreatureState>();
            var indices = new Dictionary<CreatureState, int>();
            var current = root;
            while (active.TryGetValue(current, out var intent) &&
                   occupants.TryGetValue(intent.Next, out var occupant) &&
                   occupant != current)
            {
                indices[current] = chain.Count;
                chain.Add(current);
                if (!active.ContainsKey(occupant))
                {
                    _ = TryPlanYield(
                        occupant,
                        root,
                        intent.Destination,
                        requested,
                        dependencyCycle: false);
                    break;
                }

                if (indices.TryGetValue(occupant, out var cycleStart))
                {
                    var cycle = chain.Skip(cycleStart).ToArray();
                    var cycleKey = cycle.Min(creature => creature.Id);
                    if (handledCycles.Add(cycleKey))
                    {
                        var yielder = cycle
                            .OrderBy(creature => IsUrgentMover(creature))
                            .ThenBy(creature => creature.YieldCount)
                            .ThenBy(creature => FairnessKey(creature))
                            .ThenBy(creature => creature.Id)
                            .First();
                        _ = TryPlanYield(
                            yielder,
                            root,
                            intent.Destination,
                            requested,
                            dependencyCycle: true);
                    }

                    break;
                }

                current = occupant;
            }
        }
    }

    private MovementIntent? CreateMovementIntent(CreatureState creature)
    {
        var destination = PrimaryDestination(creature);
        if (destination is not { } target || creature.Position == target)
        {
            return null;
        }

        var next = _map.NextStep(
            creature.Position,
            target,
            _zones[ZoneKind.Forbidden]);
        return next is null || next == creature.Position
            ? null
            : new MovementIntent(creature, target, next.Value);
    }

    /// <summary>
    /// Where this creature is trying to get to, as traffic arbitration sees it.
    ///
    /// A creature that broke has one too, and saying so is the whole of the fix
    /// Issue #101 needed on this side. Flight stopped being a teleport and became
    /// a walk, but a walk whose destination nobody published: <c>PrimaryDestination</c>
    /// answered <c>null</c> for a runner, so it produced no
    /// <see cref="MovementIntent"/>, took no part in the arbitration, and was
    /// governed by nothing except "do not step onto an occupied tile". Nobody
    /// stepped aside for it and no dependency cycle containing it was resolved.
    /// Measured on the matrix: eleven of fifty-five flights on <c>prepared</c>
    /// never moved the creature a single tile, one of them for sixty-seven ticks
    /// — half a minute of a defender who announced panic and then stood in the
    /// middle of a fight, which reads as broken rather than as frightened.
    ///
    /// The refuge is the destination, published exactly the way a worker's target
    /// is, with no priority of its own: see <see cref="IsUrgentMover"/>, which a
    /// runner deliberately does not join. Panic does not entitle anybody to the
    /// corridor.
    /// </summary>
    private GridPoint? PrimaryDestination(CreatureState creature)
    {
        if (creature.Mode == CreatureMode.Fled)
        {
            return FleeTile(creature);
        }

        if (creature.IsMustering)
        {
            return creature.MusterNeedsRation
                ? creature.SpecialTarget
                : creature.MusterTarget;
        }

        if (creature.MealReserved)
        {
            return creature.SpecialTarget;
        }

        return creature.CurrentJob?.Target;
    }

    private bool TryPlanYield(
        CreatureState blocker,
        CreatureState beneficiary,
        GridPoint beneficiaryTarget,
        IReadOnlySet<GridPoint> requested,
        bool dependencyCycle)
    {
        if (!CanYield(blocker, allowUrgent: dependencyCycle))
        {
            return false;
        }

        var occupants = _creatures.ToDictionary(creature => creature.Position);
        var beneficiaryUrgent = IsUrgentMover(beneficiary);
        var queue = new Queue<GridPoint>();
        var previous = new Dictionary<GridPoint, GridPoint?>();
        queue.Enqueue(blocker.Position);
        previous[blocker.Position] = null;
        GridPoint? openTarget = null;

        while (queue.TryDequeue(out var current) && openTarget is null)
        {
            foreach (var neighbor in PrototypeMap.Neighbors(current))
            {
                if (previous.ContainsKey(neighbor) ||
                    !_map.IsPassable(neighbor) ||
                    _map[neighbor] == TileKind.Larder ||
                    neighbor == PrototypeMap.Gate ||
                    _zones[ZoneKind.Forbidden].Contains(neighbor) ||
                    (!beneficiaryUrgent && requested.Contains(neighbor)) ||
                    _yieldReservations.Contains(neighbor))
                {
                    continue;
                }

                previous[neighbor] = current;
                if (!occupants.TryGetValue(neighbor, out var occupant))
                {
                    openTarget = neighbor;
                    break;
                }

                if (CanYield(occupant, allowUrgent: false))
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        if (openTarget is null)
        {
            return false;
        }

        var path = new List<GridPoint>();
        for (GridPoint? point = openTarget; point is { } value; point = previous[value])
        {
            path.Add(value);
        }

        path.Reverse();
        for (var index = path.Count - 2; index >= 0; index--)
        {
            var actor = occupants[path[index]];
            var target = path[index + 1];
            actor.TrafficTarget = target;
            actor.WaitThisTick = false;
            _yieldReservations.Add(target);
            RecordDecision(
                actor,
                "chosen_traffic_yield",
                new Dictionary<string, int>
                {
                    ["beneficiaryId"] = beneficiary.Id,
                    ["dependencyCycle"] = dependencyCycle ? 1 : 0,
                    ["targetX"] = target.X,
                    ["targetY"] = target.Y,
                },
                actor.CurrentJob?.Kind,
                target);
        }

        return true;
    }

    /// <summary>
    /// Whether this creature may be told to step aside.
    ///
    /// The order has to go to somebody who will actually take the step, and two
    /// modes will not (Issue #119). <see cref="ActCreatures"/> hands a
    /// <see cref="CreatureMode.Fighting"/> creature to
    /// <see cref="ActCombatant"/> before it ever reads
    /// <c>TrafficTarget</c>, and it skips a <see cref="CreatureMode.Downed"/>
    /// one outright. Choosing either as the yielder cost the tick twice: the
    /// booked tile stayed shut for everybody, including the creature the yield
    /// was made for, and <c>chosen_traffic_yield</c> went into the canonical log
    /// for a move that never happened.
    ///
    /// Measured on the hall layout of `main`, per party over the seed matrix:
    /// 21 to 109 such orders to a defender in a fight and 0 to 173 to a creature
    /// on the floor. The dungeon of Issue #117 is what made the cost visible —
    /// in a hall the traffic walks around a tile locked for nothing, in a
    /// doorway there is nothing to walk around — and the traffic measurement
    /// behind the decision to fix it here is
    /// <c>evidence/117-traffic.json</c>.
    ///
    /// <see cref="CreatureMode.Fled"/> is deliberately not in the list: a runner
    /// does read the booking and does take the step, which is the half of this
    /// Issue #101 already closed.
    /// </summary>
    private bool CanYield(CreatureState creature, bool allowUrgent)
    {
        return creature.TrafficTarget is null &&
            creature.Mode is not (CreatureMode.Fighting or CreatureMode.Downed) &&
            (allowUrgent ||
             (!creature.MealReserved && !creature.IsMustering)) &&
            PrimaryDestination(creature) != creature.Position;
    }

    private static bool IsUrgentMover(CreatureState creature)
    {
        return creature.MealReserved || creature.MusterNeedsRation;
    }

    private int FairnessKey(CreatureState creature)
    {
        var count = _creatures.Count;
        return (creature.Id - CurrentTick % count + count) % count;
    }

    private void MatchJobs()
    {
        var candidates = _creatures
            .Where(creature =>
                creature.CurrentJob is null &&
                !creature.IsMustering &&
                creature.TrafficTarget is null &&
                creature.Mode is not (CreatureMode.Eating or CreatureMode.Fighting or CreatureMode.Fled or CreatureMode.Downed) &&
                creature.Satiety >= PrototypeTuning.CollapseThreshold)
            .OrderBy(creature => creature.Id)
            .ToList();
        var jobs = _jobs
            .Where(job => job.ReservedBy is null)
            .OrderBy(job => job.Id)
            .ToList();
        var pairs = new List<MatchPair>();

        foreach (var creature in candidates)
        {
            foreach (var job in jobs)
            {
                if (job.PersonalCreatureId is { } personal && personal != creature.Id)
                {
                    continue;
                }

                if (job.Kind == JobKind.Drill &&
                    creature.Satiety < _rules["drill_min_satiety"])
                {
                    continue;
                }

                // A wounded creature wants the bunk whatever its fatigue says:
                // lying down is how the wound closes, and that is the labour the
                // window between two waves is actually spent on.
                if (job.Kind == JobKind.Rest &&
                    creature.Fatigue < PrototypeTuning.RestSeekThreshold &&
                    creature.Injury == InjuryKind.None)
                {
                    continue;
                }

                if (creature.NeedsRest && job.Kind != JobKind.Rest)
                {
                    continue;
                }

                // Nobody picks stone up without a place to put it down. The
                // destination is planned from the pile, so the carry leg is the
                // short one and the choice does not depend on who volunteers.
                if (job.Kind == JobKind.Haul &&
                    job.Resource == ResourceKind.Stone &&
                    !TryPlanStoneDestination(job, job.Origin, job.Quantity, out _, out _))
                {
                    continue;
                }

                if (!TryInitialTarget(creature, job, out var target))
                {
                    continue;
                }

                var targetOccupant = _creatures.FirstOrDefault(
                    other => other != creature && other.Position == target);
                if (targetOccupant is not null && targetOccupant.CurrentJob is null)
                {
                    continue;
                }

                var distance = _map.Distance(
                    creature.Position,
                    target,
                    _zones[ZoneKind.Forbidden]);
                if (distance is null)
                {
                    continue;
                }

                var urgency = Urgency(job.Kind, job.Resource);
                var affinity = creature.Affinity(job.Kind);
                var score = _priorities[job.Kind] * PrototypeTuning.ScorePriorityWeight +
                    affinity * PrototypeTuning.ScoreAffinityWeight +
                    urgency -
                    distance.Value;
                if (score >= PrototypeTuning.ScoreFloor)
                {
                    pairs.Add(new MatchPair(creature, job, target, score, urgency, affinity, distance.Value));
                }
            }
        }

        while (pairs.Count > 0)
        {
            var selected = pairs
                .OrderByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Job.Id)
                .ThenBy(pair => pair.Creature.Id)
                .First();
            var competitors = pairs
                .Where(pair => pair.Creature == selected.Creature && pair.Job != selected.Job)
                .OrderByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Job.Id)
                .ToArray();

            if (!Assign(selected, competitors.FirstOrDefault()))
            {
                // Capacity is a property of the job, not of the volunteer: if the
                // booking failed once it fails for everyone this tick.
                pairs.RemoveAll(pair => pair.Job == selected.Job);
                continue;
            }

            pairs.RemoveAll(pair =>
                pair.Creature == selected.Creature ||
                pair.Job == selected.Job ||
                (ActiveLarderTiles().Contains(selected.InitialTarget) &&
                 pair.InitialTarget == selected.InitialTarget));
        }

        foreach (var creature in candidates.Where(creature => creature.CurrentJob is null))
        {
            RecordWaitingReason(creature);
        }
    }

    private bool Assign(MatchPair selected, MatchPair? competitor)
    {
        var creature = selected.Creature;
        var job = selected.Job;
        if (job.Kind == JobKind.Haul && job.Resource == ResourceKind.Stone)
        {
            // Book the destination before anything else mutates, so a failed
            // booking leaves both the creature and the job untouched.
            if (!TryPlanStoneDestination(job, job.Origin, job.Quantity, out var cell, out var amount) ||
                amount <= 0)
            {
                return false;
            }

            job.StoreCell = cell;
            job.StoreReserved = amount;
            job.Quantity = amount;
        }

        job.ReservedBy = creature.Id;
        creature.CurrentJob = job;
        creature.Mode = CreatureMode.Moving;
        job.Target = selected.InitialTarget;
        job.RemainingTicks = WorkDuration(creature, job.Kind);
        var reason = competitor is null
            ? "chosen_only_option"
            : ExplainChoice(selected, competitor);
        RecordDecision(
            creature,
            reason,
            new Dictionary<string, int>
            {
                ["jobId"] = checked((int)job.Id),
                ["score"] = selected.Score,
                ["distance"] = selected.Distance,
            },
            job.Kind,
            selected.InitialTarget);
        return true;
    }

    private string ExplainChoice(MatchPair selected, MatchPair competitor)
    {
        var selectedPriority = _priorities[selected.Job.Kind];
        var competitorPriority = _priorities[competitor.Job.Kind];
        if (selectedPriority > competitorPriority)
        {
            return "chosen_highest_priority";
        }

        if (selected.Urgency > competitor.Urgency)
        {
            return "chosen_bottleneck";
        }

        if (selected.Affinity > competitor.Affinity)
        {
            return "chosen_affinity_match";
        }

        if (selected.Distance < competitor.Distance)
        {
            return "chosen_nearest";
        }

        return "chosen_tie_break";
    }

    private void RecordWaitingReason(CreatureState creature)
    {
        var diagnosticKind = Enum.GetValues<JobKind>()
            .Where(kind =>
                _priorities[kind] > 0 &&
                (kind != JobKind.Rest || creature.Fatigue >= PrototypeTuning.RestSeekThreshold))
            .Select(kind => new
            {
                Kind = kind,
                Score = _priorities[kind] * PrototypeTuning.ScorePriorityWeight +
                    creature.Affinity(kind) * PrototypeTuning.ScoreAffinityWeight +
                    DiagnosticUrgency(kind),
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Kind)
            .FirstOrDefault();

        var reason = diagnosticKind is null
            ? "waiting_no_job_available"
            : DiagnosticReason(creature, diagnosticKind.Kind);
        RecordDecision(
            creature,
            reason,
            DiagnosticDetails(creature, diagnosticKind?.Kind),
            diagnosticKind?.Kind);
        creature.Mode = CreatureMode.Waiting;
    }

    private string DiagnosticReason(CreatureState creature, JobKind kind)
    {
        if (kind == JobKind.Drill && creature.Satiety < _rules["drill_min_satiety"])
        {
            return "refused_rule_min_satiety";
        }

        if (kind == JobKind.Dig)
        {
            // Digging needs no zone, so it gets its own explanation ladder.
            if (_digDesignations.Count == 0)
            {
                return "waiting_no_designation";
            }

            return AnyReachableTarget(creature, JobKind.Dig)
                ? "waiting_no_job_available"
                : "dig_unreachable";
        }

        if (kind == JobKind.Build)
        {
            // Construction needs no zone either, so it gets the same shape of
            // ladder: no intent, no way in, no material, or simply nobody free.
            if (_buildSites.Count == 0)
            {
                return "waiting_no_blueprint";
            }

            if (!_buildSites.Keys.Any(IsBuildSiteWorkable))
            {
                return "build_unreachable";
            }

            if (!AnyReachableTarget(creature, JobKind.Build))
            {
                return _buildSites.Values.Any(
                    site => site.Delivered >= PrototypeTuning.BuildStoneCost)
                    ? "build_unreachable"
                    : StoneAnywhere() > 0
                        ? "build_waiting_material"
                        : "build_no_stone";
            }

            return "waiting_no_job_available";
        }

        // A blocked stone chain has a better answer than the generic haul ladder,
        // and it is only consulted when stone is actually lying on the floor.
        if (kind == JobKind.Haul && TryExplainStoneHaulWait(creature, out var stoneReason))
        {
            return stoneReason;
        }

        var requiredZone = kind switch
        {
            JobKind.Harvest => (ZoneKind.Farm, TileKind.Bed),
            JobKind.Cook => (ZoneKind.Kitchen, TileKind.Kitchen),
            JobKind.Rest => (ZoneKind.Quarters, TileKind.Bunk),
            JobKind.Drill => (ZoneKind.TrainingGround, TileKind.Post),
            JobKind.Watch => (ZoneKind.Watch, TileKind.Floor),
            _ => ((ZoneKind, TileKind)?)null,
        };
        if (requiredZone is { } zone &&
            (kind == JobKind.Watch
                ? _zones[zone.Item1].Count == 0
                : !ZoneCoversFeature(zone.Item1, zone.Item2)))
        {
            return "refused_zone_not_designated";
        }

        if (!AnyReachableTarget(creature, kind))
        {
            return "refused_zone_unreachable";
        }

        return kind switch
        {
            JobKind.Harvest when _stockRaw >= PrototypeTuning.RawTarget =>
                "waiting_stock_sufficient",
            JobKind.Harvest => "waiting_crop_not_ripe",
            JobKind.Haul when _stockRaw + _stockMeals >= PrototypeTuning.LarderCapacity =>
                "waiting_storage_full",
            JobKind.Haul => "waiting_input_missing",
            JobKind.Cook when _stockMeals >= PrototypeTuning.MealTarget =>
                "waiting_stock_sufficient",
            JobKind.Cook => "waiting_input_missing",
            _ => "waiting_no_job_available",
        };
    }

    /// <summary>
    /// Answers "why is that stone still lying there?" without the player opening
    /// the log. Returns false when stone hauling is in fact possible, so the
    /// established food explanations stay untouched in a session without stone.
    /// </summary>
    private bool TryExplainStoneHaulWait(CreatureState creature, out string reason)
    {
        reason = string.Empty;
        if (LooseCount(ResourceKind.Stone) <= 0)
        {
            return false;
        }

        if (_zones[ZoneKind.MaterialStockpile].Count == 0)
        {
            reason = "waiting_no_stockpile";
            return true;
        }

        var usable = UsableStockpileCells().ToArray();
        if (usable.Length == 0 ||
            !usable.Any(cell =>
                _map.Distance(creature.Position, cell, _zones[ZoneKind.Forbidden]) is not null))
        {
            reason = "stone_unreachable";
            return true;
        }

        if (AvailableStoneCapacity() == 0)
        {
            reason = "waiting_stockpile_full";
            return true;
        }

        return false;
    }

    private Dictionary<string, int> DiagnosticDetails(CreatureState creature, JobKind? kind)
    {
        return kind switch
        {
            JobKind.Drill => new()
            {
                ["satiety"] = creature.Satiety,
                ["minimum"] = _rules["drill_min_satiety"],
            },
            JobKind.Harvest => new()
            {
                ["ripeBeds"] = _beds.Values.Count(bed => bed.IsRipe),
                ["rawStock"] = _stockRaw,
            },
            JobKind.Cook => new()
            {
                ["rawStock"] = _stockRaw,
                ["required"] = PrototypeTuning.CookInput,
            },
            JobKind.Haul => new()
            {
                ["looseItems"] = _loose.Values.Sum(),
                ["freeCapacity"] = PrototypeTuning.LarderCapacity - _stockRaw - _stockMeals,
                ["looseStone"] = LooseCount(ResourceKind.Stone),
                ["storedStone"] = StoredStoneTotal(),
                ["stockpileCells"] = _zones[ZoneKind.MaterialStockpile].Count,
                ["stockpileFree"] = AvailableStoneCapacity(),
            },
            JobKind.Dig => new()
            {
                ["designations"] = _digDesignations.Count,
                ["reachable"] = _digDesignations.Count(IsDigReachable),
            },
            JobKind.Build => new()
            {
                ["blueprints"] = _buildSites.Count,
                ["reachable"] = _buildSites.Keys.Count(IsBuildSiteWorkable),
                ["required"] = PrototypeTuning.BuildStoneCost,
                ["delivered"] = SiteStoneTotal(),
                ["stoneAvailable"] = AvailableStoneForSites(),
            },
            _ => new(),
        };
    }

    private void ActCreatures()
    {
        // Nerve is asked before anybody acts, so a defender that broke this tick
        // leaves instead of striking, and everyone reads the same world when
        // they answer. It sits here rather than in the raiders' subphase because
        // fear is now a standing condition rather than a reflex to one event:
        // the tick after an ally falls is the earliest anyone can react to it.
        ApplyMorale();

        foreach (var creature in _creatures.OrderBy(creature => creature.Id))
        {
            if (creature.Mode == CreatureMode.Fighting)
            {
                ActCombatant(creature);
                continue;
            }

            if (creature.Mode == CreatureMode.Fled)
            {
                // A runner honours a yield the same way a worker does, and for
                // the same reason: `TryPlanYield` writes `chosen_traffic_yield`
                // into the canonical log and books the tile for this tick. A mode
                // that took the booking and then walked its own way would make
                // both of those a lie — and it did, for tens of ticks a party,
                // because a broken defender now spends real time in a corridor
                // instead of vanishing to the far wall.
                if (creature.TrafficTarget is { } refugeYield)
                {
                    if (Move(creature, refugeYield))
                    {
                        creature.YieldCount++;
                        creature.LastYieldTick = CurrentTick;
                    }

                    creature.TrafficTarget = null;
                    continue;
                }

                RunFromTheFight(creature);
                continue;
            }

            if (creature.Mode == CreatureMode.Downed)
            {
                continue;
            }

            if (creature.TrafficTarget is { } trafficTarget)
            {
                if (Move(creature, trafficTarget))
                {
                    creature.YieldCount++;
                    creature.LastYieldTick = CurrentTick;
                }

                creature.TrafficTarget = null;
                continue;
            }

            if (creature.IsMustering)
            {
                ActMuster(creature);
                continue;
            }

            if (creature.Mode == CreatureMode.Eating)
            {
                ActEating(creature);
                continue;
            }

            if (creature.CurrentJob is { } job)
            {
                ActJob(creature, job);
            }
        }
    }

    /// <summary>
    /// A wave's composition is fixed at its own announce tick from the renown
    /// standing right then. Deciding it once and storing it is what makes the
    /// announcement honest: the countdown the player reads names the raiders
    /// that will actually walk through the gate.
    /// </summary>
    private void AnnounceWaves()
    {
        foreach (var wave in _waves.Where(item => !item.Announced && item.AnnounceTick <= CurrentTick))
        {
            var renown = Renown();
            wave.Announced = true;
            wave.RenownAtAnnounce = renown;
            wave.RaiderCount = Math.Min(
                PrototypeTuning.WaveMaxRaiders,
                PrototypeTuning.WaveBaseRaiders + renown / PrototypeTuning.RenownPerExtraRaider);
            wave.RaiderMight = PrototypeTuning.RaiderMightBase +
                renown / PrototypeTuning.RenownPerRaiderMight;
        }
    }

    /// <summary>
    /// The wave in hand: the one that arrived and has not been resolved,
    /// otherwise the next one that has not arrived yet. Null only once the last
    /// wave is over.
    /// </summary>
    private WaveState? CurrentWave() =>
        _waves.FirstOrDefault(wave => wave.Outcome is null);

    private WaveState? ActiveWave() =>
        _waves.FirstOrDefault(wave => wave.Outcome is null && wave.ArriveTick <= CurrentTick);

    private void EnterRaiders()
    {
        if (ActiveWave() is not { } wave)
        {
            return;
        }

        while (wave.Entered < wave.RaiderCount &&
               CurrentTick >= wave.ArriveTick + wave.Entered * PrototypeTuning.RaiderEntryInterval)
        {
            _raiders.Add(new RaiderState(
                _nextRaiderId++,
                wave.Number,
                PrototypeTuning.RaiderHp,
                wave.RaiderMight + CombatJitter(PrototypeTuning.RaiderMightJitter),
                PrototypeMap.Gate));
            wave.Entered++;
        }

        if (!wave.Arrived)
        {
            // Renown is credited the moment a wave reaches the domain, not when
            // it is beaten: the domain is now a place raiders travel to, and
            // that fact cannot be taken back by losing the fight.
            wave.Arrived = true;
            // The trend the HUD draws is measured from here, so both numbers are
            // read once, at the wave that just landed, and never recomputed.
            _renownAtPreviousWave = Renown();
            _strengthAtPreviousWave = DomainStrength();
        }
    }

    private void UpdateCombatParticipation()
    {
        if (ActiveWave() is not { } wave)
        {
            return;
        }

        // The first check happens on the wave's own arrival tick; after that the
        // rest of the domain is asked again on the same period as before. Both
        // are relative to this wave, not to a single session-wide raid tick.
        var sinceArrival = CurrentTick - wave.ArriveTick;
        if (sinceArrival != 0 && sinceArrival % PrototypeTuning.CombatJoinRecheck != 0)
        {
            return;
        }

        foreach (var creature in _creatures.Where(c => c.Mode is not (CreatureMode.Fighting or CreatureMode.Fled or CreatureMode.Downed)).OrderBy(c => c.Id))
        {
            var failed = new Dictionary<string, int> { ["wave"] = wave.Number };
            if (creature.Injury == InjuryKind.Heavy)
            {
                failed["injured"] = 1;
                RecordDecision(creature, "combat_refused_injured", failed);
                continue;
            }
            if (creature.Satiety < PrototypeTuning.CombatMinSatiety)
            {
                failed["satiety"] = creature.Satiety;
                failed["threshold"] = PrototypeTuning.CombatMinSatiety;
                RecordDecision(creature, "combat_refused_starving", failed);
                continue;
            }

            var distance = _map.Distance(creature.Position, PrototypeMap.LarderTiles[0], _zones[ZoneKind.Forbidden]);
            if (!creature.IsMustering && distance is > PrototypeTuning.EngageRadius)
            {
                RecordDecision(creature, "combat_absent_unreachable", new Dictionary<string, int> { ["distance"] = distance ?? -1, ["wave"] = wave.Number });
                continue;
            }

            if (creature.CurrentJob is not null)
            {
                CancelJob(creature, "combat_joined");
            }
            creature.IsMustering = false;
            creature.MusterNeedsRation = false;
            creature.MealReserved = false;
            creature.Mode = CreatureMode.Fighting;
            RecordDecision(creature, "combat_joined", new Dictionary<string, int> { ["readiness"] = ComputeReadiness(creature), ["wave"] = wave.Number });
        }
    }

    private void ActCombatant(CreatureState creature)
    {
        var target = _raiders.Where(raider => raider.Mode == RaiderMode.Raiding)
            .OrderBy(raider => Manhattan(creature.Position, raider.Position))
            .ThenBy(raider => raider.Id)
            .FirstOrDefault();
        if (target is null)
        {
            return;
        }

        // Reach is read from the attack, not written into the rule. Raising
        // MeleeAttackRange is the whole edit a ranged weapon would need here.
        if (Manhattan(creature.Position, target.Position) > PrototypeTuning.MeleeAttackRange)
        {
            var next = _map.NextStep(creature.Position, target.Position, _zones[ZoneKind.Forbidden]);
            if (next is { } step)
            {
                _ = Move(creature, step);
            }
            return;
        }

        var damage = Math.Max(PrototypeTuning.DamageFloor,
            creature.Might + ComputeReadiness(creature) / PrototypeTuning.DamageReadinessDivisor + CombatJitter(PrototypeTuning.DamageJitter));
        target.Hp -= damage;
        RecordDecision(creature, "combat_attack", new Dictionary<string, int> { ["raiderId"] = target.Id, ["damage"] = damage });
        if (target.Hp <= 0)
        {
            target.Hp = 0;
            DropRaiderMeals(target);
            target.Mode = RaiderMode.Downed;
            _raidersDownedTotal++;
            RecordDecision(creature, "combat_raider_downed", new Dictionary<string, int> { ["raiderId"] = target.Id });
        }
    }

    private void ActRaiders()
    {
        foreach (var raider in _raiders.Where(raider => raider.Mode == RaiderMode.Raiding).OrderBy(raider => raider.Id))
        {
            var defender = _creatures.Where(creature => creature.Mode == CreatureMode.Fighting)
                .OrderBy(creature => Manhattan(creature.Position, raider.Position))
                .ThenBy(creature => creature.Id)
                .FirstOrDefault();
            if (defender is not null &&
                Manhattan(defender.Position, raider.Position) <= PrototypeTuning.RaiderAttackRange)
            {
                var damage = Math.Max(PrototypeTuning.DamageFloor,
                    raider.Might - ComputeReadiness(defender) / PrototypeTuning.ArmourReadinessDivisor + CombatJitter(PrototypeTuning.DamageJitter));
                defender.Hp -= damage;
                if (defender.Hp * 100 <= defender.MaxHp * PrototypeTuning.LightInjuryShare && defender.Injury == InjuryKind.None)
                {
                    defender.Injury = InjuryKind.Light;
                    defender.RecoveryTicks = 0;
                }
                if (defender.Hp <= 0)
                {
                    defender.Hp = 0;
                    defender.Injury = InjuryKind.Heavy;
                    defender.Mode = CreatureMode.Downed;
                    CurrentWave()?.CountDefenderDowned();
                    RecordDecision(defender, "combat_downed", new Dictionary<string, int> { ["raiderId"] = raider.Id, ["damage"] = damage });
                }
                continue;
            }

            var target = raider.ReturningToGate
                ? PrototypeMap.Gate
                : PrototypeMap.LarderTiles[0];
            if (raider.Position == PrototypeMap.LarderTiles[0] &&
                raider.CarryingMeals < PrototypeTuning.CarryCapacity)
            {
                if (_stockMeals == 0)
                {
                    raider.ReturningToGate = true;
                    target = PrototypeMap.Gate;
                }
                else
                {
                    raider.StealTicks++;
                    if (raider.StealTicks < PrototypeTuning.StealPeriod)
                    {
                        continue;
                    }

                    _stockMeals--;
                    raider.CarryingMeals++;
                    raider.StealTicks = 0;
                    if (raider.CarryingMeals >= PrototypeTuning.CarryCapacity)
                    {
                        raider.ReturningToGate = true;
                    }
                    continue;
                }
            }
            var next = _map.NextStep(raider.Position, target, _zones[ZoneKind.Forbidden]);
            if (next is { } step)
            {
                raider.Position = step;
            }
            if (target == PrototypeMap.Gate && raider.Position == PrototypeMap.Gate)
            {
                raider.Mode = RaiderMode.Escaped;
            }
        }

        ResolveWave();
    }

    /// <summary>
    /// "End of combat" is now measured against the arrival of the wave in hand:
    /// the first tick at or after it in which none of that wave's raiders is
    /// still on the map. The session fuse is not part of the rule.
    ///
    /// Resolving a wave also ends its consequences: whoever ran is back at work,
    /// because a party of four waves cannot spend a creature on one panic.
    /// Whoever was put down is carried off the floor by
    /// <see cref="RaiseTheDowned"/> a step later, with a heavy wound that keeps
    /// them out of the next fight until it has been mended.
    /// </summary>
    private void ResolveWave()
    {
        if (ActiveWave() is not { } wave ||
            wave.Entered < wave.RaiderCount ||
            WaveRaiders(wave).Any(raider => raider.Mode == RaiderMode.Raiding))
        {
            return;
        }

        var raiders = WaveRaiders(wave).ToArray();
        var downed = raiders.Count(raider => raider.Mode == RaiderMode.Downed);
        var casualties = wave.DefendersDowned + wave.DefendersFled;
        wave.Outcome = downed == wave.RaiderCount
            ? casualties == 0 ? "repelled_clean" : "repelled_costly"
            : downed == 0 ? "overrun" : "larder_raided";
        wave.EndTick = CurrentTick;
        wave.RaidersDowned = downed;
        wave.MealsStolen = raiders
            .Where(raider => raider.Mode == RaiderMode.Escaped)
            .Sum(raider => raider.CarryingMeals);

        foreach (var creature in _creatures.Where(creature => creature.Mode is CreatureMode.Fled or CreatureMode.Fighting))
        {
            var returning = creature.Mode == CreatureMode.Fled;
            creature.Mode = CreatureMode.Waiting;
            if (returning)
            {
                RecordDecision(
                    creature,
                    "combat_returned",
                    new Dictionary<string, int> { ["wave"] = wave.Number });
            }
        }
    }

    private IEnumerable<RaiderState> WaveRaiders(WaveState wave) =>
        _raiders.Where(raider => raider.Wave == wave.Number);

    /// <summary>
    /// Once no wave is on the map the domain picks its people up off the floor.
    /// They stand with a heavy wound and one point of health: barred from the
    /// next fight, and worth a bunk and a portion for as long as it takes to
    /// mend. That is the price of a lost wave — a domain that meets the next one
    /// short-handed — rather than a counter running backwards.
    /// </summary>
    private void RaiseTheDowned()
    {
        if (_sessionOutcome is not null || ActiveWave() is not null)
        {
            return;
        }

        foreach (var creature in _creatures
                     .Where(creature => creature.Mode == CreatureMode.Downed)
                     .OrderBy(creature => creature.Id))
        {
            creature.Mode = CreatureMode.Waiting;
            creature.Hp = Math.Max(1, creature.Hp);
            creature.RecoveryTicks = 0;
            RecordDecision(
                creature,
                "injury_tended",
                new Dictionary<string, int>
                {
                    ["hp"] = creature.Hp,
                    ["maxHp"] = creature.MaxHp,
                });
        }
    }

    /// <summary>
    /// The end of the party, checked once a tick after everything else has
    /// happened. Three forms, because two were telling a lie: a domain that lost
    /// a wave outright, watched its larder carried off twice and ended with an
    /// empty pantry was still reported as having held, and slice 1 exists to
    /// make that feedback honest.
    ///
    /// - `fallen`  — nobody left who can work and defend;
    /// - `held`    — survived, and every wave was actually repelled;
    /// - `raided`  — survived, but at least one wave got through.
    ///
    /// The line between the last two is drawn on the wave outcomes rather than
    /// on the number of portions carried away, for two reasons. It is what ADR
    /// 0015 says literally — "отражены все волны" — and `repelled_clean` and
    /// `repelled_costly` are precisely the outcomes whose names say the wave was
    /// repelled. And counting portions instead would let a domain whose larder
    /// was already empty be called victorious for losing nothing: the raiders
    /// walked in and out unopposed, which is not holding.
    /// </summary>
    private void ResolveSession()
    {
        if (_sessionOutcome is not null)
        {
            return;
        }

        if (HasFallen())
        {
            _sessionOutcome = "fallen";
            _sessionEndTick = CurrentTick;
            return;
        }

        if (_waves.All(wave => wave.Outcome is not null))
        {
            _sessionOutcome = _waves.All(WasRepelled) ? "held" : "raided";
            _sessionEndTick = CurrentTick;
        }
    }

    private static bool WasRepelled(WaveState wave) =>
        wave.Outcome is "repelled_clean" or "repelled_costly";

    /// <summary>
    /// "Nobody left who can work and defend", stated so that it is a fact about
    /// the world rather than a mood. Every creature is either on the floor or
    /// below the exhaustion threshold, and there is not one portion left in the
    /// larder, on the ground or on anybody's back.
    ///
    /// That state cannot be walked out of: an exhausted creature refuses work,
    /// so nothing will be harvested, nothing will be cooked, and no portion will
    /// ever appear again. Requiring the empty larder is what keeps a domain that
    /// is merely hungry from being declared dead while it still has supper.
    /// </summary>
    private bool HasFallen()
    {
        if (_creatures.All(creature => creature.Mode == CreatureMode.Downed))
        {
            return true;
        }

        if (_creatures.Any(CanWorkAndDefend))
        {
            return false;
        }

        return _stockMeals == 0 &&
            LooseCount(ResourceKind.Meal) == 0 &&
            _creatures.All(creature => creature.Carrying != ResourceKind.Meal);
    }

    /// <summary>
    /// One of the people the domain still has: on their feet and fed enough to
    /// do something about it. The party score counts exactly these as survivors,
    /// which is why a fallen domain scores none of them without anyone writing
    /// a special case — "nobody left who can work and defend" is the same
    /// sentence read over the whole population.
    /// </summary>
    private static bool CanWorkAndDefend(CreatureState creature) =>
        creature.Mode != CreatureMode.Downed &&
        creature.Satiety >= PrototypeTuning.CollapseThreshold;

    /// <summary>
    /// Whether each defender still holds, asked of every one of them separately
    /// once a tick rather than of all of them at once when an ally goes down.
    ///
    /// The old shape was a single domain-wide count of the fallen against a
    /// single threshold, evaluated only at the instant somebody dropped. Two
    /// properties of that shape made panic a herd rather than a decision, and
    /// neither was a mistake in the arithmetic. The pressure term was the same
    /// number for everybody, so one casualty raised the bar for the whole
    /// company at the same moment; and the resisting side — grit plus readiness
    /// — barely moves during a fight, so whoever sat in the band the bar had
    /// just crossed all broke on that one tick. Measured on the seed matrix
    /// before this change: five and six of a nine-strong domain leaving on a
    /// single tick, every wave.
    ///
    /// What replaces it is the same question asked from where the creature is
    /// standing. Dread is what this defender can see: allies down within
    /// <see cref="PrototypeTuning.MoraleWitnessRadius"/> and raiders pressing
    /// within <see cref="PrototypeTuning.MoralePressRadius"/>. Nerve now carries
    /// the defender's own wounds beside its character. Both sides change tick by
    /// tick, and they change differently for each creature, because raiders pick
    /// their target by distance and the wounded are not the same people as the
    /// crowded ones. So the moment of breaking spreads by itself, out of facts
    /// the snapshot already publishes, without a hidden counter and without a
    /// combat trait — the latter is deliberately somebody else's work
    /// (Issue #101 non-goals).
    ///
    /// Asking every tick rather than once per casualty also raises how often the
    /// question can be answered "no", and that is a real cost rather than a
    /// rounding error: at the weights the shape was first written with, the whole
    /// line left every wave and `defendersDowned` fell to 0..1 a party. The
    /// weights in <see cref="PrototypeTuning"/> were re-measured against that,
    /// and what they are worth now is argued there.
    ///
    /// Distance is Manhattan rather than a path: the question is "what can I see
    /// from here", and a breadth-first search per defender per fallen ally per
    /// tick would buy a corner case at a price the whole party pays.
    /// </summary>
    private void ApplyMorale()
    {
        foreach (var creature in _creatures
                     .Where(creature => creature.Mode == CreatureMode.Fighting)
                     .OrderBy(creature => creature.Id))
        {
            var downedNear = _creatures.Count(other =>
                other != creature &&
                other.Mode == CreatureMode.Downed &&
                Manhattan(creature.Position, other.Position) <= PrototypeTuning.MoraleWitnessRadius);
            var raidersNear = _raiders.Count(raider =>
                raider.Mode == RaiderMode.Raiding &&
                Manhattan(creature.Position, raider.Position) <= PrototypeTuning.MoralePressRadius);
            var nerve = creature.Grit * PrototypeTuning.MoraleGritWeight +
                ComputeReadiness(creature) / PrototypeTuning.MoraleReadinessDivisor +
                creature.Hp * PrototypeTuning.MoraleHealthWeight / creature.MaxHp;
            var dread = PrototypeTuning.MoraleBase +
                PrototypeTuning.MoralePerDowned * downedNear +
                PrototypeTuning.MoralePerRaiderNear * raidersNear;
            if (nerve >= dread)
            {
                continue;
            }

            creature.Mode = CreatureMode.Fled;
            CurrentWave()?.CountDefenderFled();
            RecordDecision(
                creature,
                "combat_fled_morale",
                new Dictionary<string, int>
                {
                    ["downedAlliesNear"] = downedNear,
                    ["raidersNear"] = raidersNear,
                    ["hpPercent"] = creature.Hp * 100 / creature.MaxHp,
                });
        }
    }

    /// <summary>
    /// A defender who broke leaves the fight on foot. The position used to be
    /// assigned outright, which put a creature half a map away inside one tick
    /// and gave the presentation layer a jump to interpolate — the one thing
    /// Presentation pass A promised would never happen, because movement that
    /// does not read as movement cannot be read at all.
    ///
    /// Running is therefore ordinary movement through <see cref="Move"/>: one
    /// tile a tick, no tile shared with anybody, no swapping past a neighbour.
    /// It also means the domain watches somebody run, which is the whole
    /// observable point — a wave usually ends before the runner reaches the far
    /// wall, and that is fine. Whoever is still on the way is put back to work
    /// by <see cref="ResolveWave"/> from wherever the end of the fight found
    /// them.
    /// </summary>
    private void RunFromTheFight(CreatureState creature)
    {
        // The same destination traffic arbitration planned around this tick, read
        // from the same place, so that what was arbitrated and what is walked
        // cannot drift apart.
        if (PrimaryDestination(creature) is not { } refuge ||
            creature.Position == refuge)
        {
            return;
        }

        _ = Move(creature, refuge);
    }

    /// <summary>
    /// Where a broken defender is heading. It used to be one tile per creature id
    /// with nothing checking it was free; a creature that flees and then comes
    /// back to work after the wave makes that shortcut visible, because two
    /// creatures on one tile break movement for both.
    ///
    /// It is recomputed every tick of the flight rather than remembered, and it
    /// is a pure function of the published world, so the run stays deterministic
    /// and needs no field of its own in the canonical snapshot.
    /// </summary>
    private GridPoint FleeTile(CreatureState creature)
    {
        bool Free(GridPoint tile) =>
            _map.IsPassable(tile) &&
            tile != PrototypeMap.Gate &&
            !_creatures.Any(other => other != creature && other.Position == tile);

        var preferred = new GridPoint(1, Math.Min(PrototypeTuning.MapHeight - 2, 1 + creature.Id));
        return Free(preferred)
            ? preferred
            : Enumerable.Range(1, PrototypeTuning.MapHeight - 2)
                .Select(y => new GridPoint(1, y))
                .FirstOrDefault(Free, creature.Position);
    }

    /// <summary>
    /// How visible the domain is from outside. Every term is a counter that only
    /// grows, so renown can never fall: a raided larder, a downed creature or a
    /// razed post cost the domain its answer to the next wave, never its score.
    /// Making impoverishment pay was the whole point — the previous evaluation
    /// marked H2 contradicted precisely because a loss metric made `overrun` the
    /// best result a player could aim for.
    ///
    /// Weights and the shape of the sum are tuning by ADR 0010; that it may not
    /// decrease is the invariant.
    /// </summary>
    private int Renown() =>
        _waves.Count(wave => wave.Arrived) * PrototypeTuning.RenownPerWaveArrived +
        _raidersDownedTotal * PrototypeTuning.RenownPerRaiderDowned +
        _digsCompleted * PrototypeTuning.RenownPerExcavation +
        _buildsCompleted * PrototypeTuning.RenownPerConstruction +
        _peakMeals / PrototypeTuning.RenownMealsPerPoint;

    /// <summary>
    /// How ready the domain is to meet the next wave. It influences nothing — it
    /// is the mirror the player holds next to renown, and the gap between the
    /// two is the answer to "am I doing well?".
    ///
    /// It counts readiness and not potential, which is the difference between a
    /// mirror and a flattering one. A domain starving to death used to show the
    /// best number of its whole party, because inborn might and drilled form
    /// survive hunger on paper; the summary then read "renown 4 against strength
    /// 86" at the exact moment the domain died. Two things stop that:
    ///
    /// - only creatures who could actually answer the call are counted, by the
    ///   same admission rule combat itself uses (10.2) minus the distance test,
    ///   which is about where somebody happens to stand rather than about the
    ///   condition of the domain;
    /// - what each of them brings is scaled by their readiness, so hunger,
    ///   exhaustion and wounds show up in the number rather than beside it.
    /// </summary>
    private int DomainStrength() =>
        _creatures
            .Where(CanAnswerTheCall)
            .Sum(creature =>
                (creature.Might * PrototypeTuning.StrengthPerMight +
                 creature.MartialForm / PrototypeTuning.StrengthMartialDivisor) *
                ComputeReadiness(creature) / PrototypeTuning.StrengthReadinessScale);

    /// <summary>
    /// Could this creature take the field if a wave arrived right now? The rule
    /// is combat's own, so the mirror cannot report strength that the fight
    /// would refuse to use.
    /// </summary>
    private static bool CanAnswerTheCall(CreatureState creature) =>
        creature.Mode != CreatureMode.Downed &&
        creature.Injury != InjuryKind.Heavy &&
        creature.Satiety >= PrototypeTuning.CombatMinSatiety;

    private int CombatJitter(int amplitude) => _combatRandom.NextInt32(amplitude * 2 + 1) - amplitude;

    private void DropRaiderMeals(RaiderState raider)
    {
        if (raider.CarryingMeals <= 0)
        {
            return;
        }

        AddLoose(raider.Position, ResourceKind.Meal, raider.CarryingMeals);
        raider.CarryingMeals = 0;
    }

    private static int Manhattan(GridPoint left, GridPoint right) => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private void CountLaborForTick()
    {
        _totalCreatureTicks += _creatures.Count;
        foreach (var creature in _creatures)
        {
            if (creature.Mode == CreatureMode.Eating ||
                (creature.IsMustering && creature.MusterNeedsRation))
            {
                _eatTicks++;
            }
            else if (creature.IsMustering)
            {
                _musterTicks++;
            }
            else
            {
                // Stone logistics is construction labour, not food labour: folding
                // it into foodWorkTicks would silently inflate the food share the
                // contract corridors are measured against.
                if (creature.CurrentJob is { Kind: JobKind.Haul, Resource: ResourceKind.Stone })
                {
                    _stoneHaulTicks++;
                    continue;
                }

                switch (creature.CurrentJob?.Kind)
                {
                    case JobKind.Harvest:
                    case JobKind.Haul:
                    case JobKind.Cook:
                        _foodWorkTicks++;
                        break;
                    case JobKind.Rest:
                        _restTicks++;
                        break;
                    case JobKind.Drill:
                        _drillTicks++;
                        break;
                    case JobKind.Watch:
                        _watchTicks++;
                        break;
                    case JobKind.Dig:
                        _digTicks++;
                        break;
                    case JobKind.Build:
                        _buildTicks++;
                        break;
                    default:
                        _idleTicks++;
                        break;
                }
            }
        }
    }

    private void CountPostOccupancyForTick()
    {
        foreach (var station in _stationOccupiedTicks.Keys.ToArray())
        {
            if (_creatures.Any(creature => IsUsingStation(creature, station)))
            {
                _stationOccupiedTicks[station]++;
            }
        }

        // Training is no longer something that only happens before the one raid,
        // so post capacity is counted for as long as the party runs.
        var activePosts = _map.PostTiles()
            .Where(point => _zones[ZoneKind.TrainingGround].Contains(point))
            .ToArray();
        _postCapacityTicks += activePosts.Length;
    }

    private bool IsUsingStation(CreatureState creature, GridPoint point)
    {
        return creature.Position == point &&
            (creature.CurrentJob?.Kind, _map[point]) switch
            {
                (JobKind.Cook, TileKind.Kitchen) => true,
                (JobKind.Drill, TileKind.Post) => true,
                _ => false,
            };
    }

    private void ActMuster(CreatureState creature)
    {
        if (creature.MusterNeedsRation)
        {
            if (!CanAdvanceMealQueue(creature, out var larder))
            {
                return;
            }

            if (creature.Position != larder)
            {
                _ = Move(creature, larder);
                return;
            }

            creature.Mode = CreatureMode.Eating;
            if (creature.SpecialTicks == 0)
            {
                creature.SpecialTicks = PrototypeTuning.EatTicks;
            }

            creature.SpecialTicks--;
            if (creature.SpecialTicks == 0)
            {
                ConsumeReservedMeal(creature);
                creature.MusterNeedsRation = false;
                creature.Mode = CreatureMode.Mustering;
            }

            return;
        }

        creature.Mode = CreatureMode.Mustering;
        if (creature.MusterTarget is { } target && creature.Position != target)
        {
            _ = Move(creature, target);
        }
    }

    private void ActEating(CreatureState creature)
    {
        if (!CanAdvanceMealQueue(creature, out var queueTarget))
        {
            return;
        }

        creature.SpecialTarget = queueTarget;
        if (creature.SpecialTarget is not { } target)
        {
            CancelMealReservation(creature);
            creature.Mode = CreatureMode.Waiting;
            return;
        }

        if (creature.Position != target)
        {
            _ = Move(creature, target);
            return;
        }

        creature.SpecialTicks--;
        if (creature.SpecialTicks <= 0)
        {
            ConsumeReservedMeal(creature);
            creature.Mode = CreatureMode.Waiting;
            creature.SpecialTarget = null;
        }
    }

    private void ActJob(CreatureState creature, JobState job)
    {
        // The larder retry below uses "target == origin" to mean "no lane found".
        // A stone haul never borrows that sentinel: its destination lives in
        // StoreCell and may legitimately be the pile's own tile.
        if (job.Kind == JobKind.Haul &&
            job.Resource != ResourceKind.Stone &&
            job.PickedUp &&
            job.Target == job.Origin)
        {
            if (!TryFindLarderTarget(
                    creature,
                    out var retryTarget,
                    out var retryAvailability))
            {
                RecordDecision(
                    creature,
                    "refused_zone_unreachable",
                    new Dictionary<string, int>
                    {
                        ["jobId"] = checked((int)job.Id),
                    },
                    job.Kind,
                    job.Origin);
                return;
            }

            if (retryAvailability == LarderAvailability.Occupied)
            {
                RecordMovementBlocked(creature, retryTarget);
                return;
            }

            job.Target = retryTarget;
        }

        if (creature.Position != job.Target)
        {
            creature.Mode = CreatureMode.Moving;
            _ = Move(creature, job.Target);
            return;
        }

        creature.Mode = job.Kind switch
        {
            JobKind.Rest => CreatureMode.Resting,
            _ => CreatureMode.Working,
        };

        if (job.Kind == JobKind.Haul && !job.PickedUp &&
            job.Resource == ResourceKind.Stone)
        {
            PickUpStone(creature, job);
            return;
        }

        if (job.Kind == JobKind.Haul && !job.PickedUp)
        {
            var available = LooseAt(job.Origin, job.Resource!.Value);
            var quantity = Math.Min(job.Quantity, available);
            RemoveLoose(job.Origin, job.Resource.Value, quantity);
            if (quantity == 0)
            {
                FinishJob(creature, job);
                return;
            }

            creature.Carrying = job.Resource;
            creature.CarryAmount = quantity;
            job.Quantity = quantity;
            job.PickedUp = true;
            if (!TryFindLarderTarget(
                    creature,
                    out var larder,
                    out var availability))
            {
                job.Target = job.Origin;
                RecordDecision(
                    creature,
                    "refused_zone_unreachable",
                    new Dictionary<string, int>
                    {
                        ["jobId"] = checked((int)job.Id),
                    },
                    job.Kind,
                    job.Origin);
                return;
            }

            if (availability == LarderAvailability.Occupied)
            {
                job.Target = job.Origin;
                RecordMovementBlocked(creature, larder);
                return;
            }

            job.Target = larder;
            return;
        }

        if (job.Kind == JobKind.Cook && !job.PickedUp)
        {
            if (_stockRaw < PrototypeTuning.CookInput)
            {
                CancelJob(creature, "waiting_input_missing");
                return;
            }

            _stockRaw -= PrototypeTuning.CookInput;
            creature.Carrying = ResourceKind.RawMushroom;
            creature.CarryAmount = PrototypeTuning.CookInput;
            job.PickedUp = true;
            job.Target = job.Origin;
            return;
        }

        if (job.Kind == JobKind.Rest)
        {
            job.ProgressTicks++;
            if (job.ProgressTicks % PrototypeTuning.RestRecoveryPeriod == 0)
            {
                creature.Fatigue = Math.Max(0, creature.Fatigue - 1);
            }

            // A wound keeps its owner in the bunk past the fatigue target: the
            // mending in ApplyPassiveProcesses only counts ticks spent resting.
            if (creature.Fatigue <= PrototypeTuning.RestTarget &&
                creature.Injury == InjuryKind.None)
            {
                FinishJob(creature, job);
            }

            return;
        }

        if (job.Kind == JobKind.Watch)
        {
            creature.WatchTicks++;
            if (creature.WatchTicks % PrototypeTuning.WatchFatiguePeriod == 0)
            {
                creature.Fatigue = Math.Min(100, creature.Fatigue + 1);
            }

            return;
        }

        if (job.Kind is JobKind.Dig or JobKind.Build)
        {
            if (job.ProgressTicks == 0)
            {
                RecordDecision(
                    creature,
                    job.Kind == JobKind.Dig ? "dig_started" : "build_started",
                    new Dictionary<string, int>
                    {
                        ["tileX"] = job.Origin.X,
                        ["tileY"] = job.Origin.Y,
                        ["requiredTicks"] = job.RemainingTicks,
                    },
                    job.Kind,
                    job.Origin);
            }

            job.ProgressTicks++;
        }

        creature.WorkTicks++;
        if (creature.WorkTicks % PrototypeTuning.FatigueGainPeriod == 0)
        {
            creature.Fatigue = Math.Min(100, creature.Fatigue + 1);
        }

        job.RemainingTicks--;
        if (job.RemainingTicks > 0)
        {
            return;
        }

        CompleteJob(creature, job);
    }

    /// <summary>
    /// The moment loose stone becomes carried stone. The booking shrinks to the
    /// amount actually lifted, so a pile that shrank in the meantime does not keep
    /// holding stockpile space it will never use.
    /// </summary>
    private void PickUpStone(CreatureState creature, JobState job)
    {
        if (job.StoreCell is not { } cell)
        {
            FinishJob(creature, job);
            return;
        }

        // Two sources, one lifting rule: a loose pile on the tile, or the stone
        // already put away in the stockpile cell the job names as its source.
        var available = job.SourceCell is { } source
            ? StoredStoneAt(source)
            : LooseAt(job.Origin, ResourceKind.Stone);
        // Never lift more than the destination is holding room for. A replan can
        // shrink the booking below the quantity the job was created with, and
        // lifting the old quantity would let this job over-book the new cell.
        var quantity = Math.Min(Math.Min(job.Quantity, available), job.StoreReserved);
        if (quantity <= 0)
        {
            ReleaseStoreReservation(job);
            FinishJob(creature, job);
            return;
        }

        if (job.SourceCell is { } stockpile)
        {
            var remaining = StoredStoneAt(stockpile) - quantity;
            if (remaining <= 0)
            {
                _storedStone.Remove(stockpile);
            }
            else
            {
                _storedStone[stockpile] = remaining;
            }
        }
        else
        {
            RemoveLoose(job.Origin, ResourceKind.Stone, quantity);
        }

        creature.Carrying = ResourceKind.Stone;
        creature.CarryAmount = quantity;
        job.Quantity = quantity;
        job.StoreReserved = quantity;
        job.PickedUp = true;
        job.Target = cell;
        RecordDecision(
            creature,
            "stone_picked_up",
            new Dictionary<string, int>
            {
                ["quantity"] = quantity,
                ["cellX"] = cell.X,
                ["cellY"] = cell.Y,
                ["fromStockpile"] = job.SourceCell is null ? 0 : 1,
            },
            JobKind.Haul,
            cell);
    }

    /// <summary>
    /// The moment carried stone becomes stored stone. Anything the cell cannot
    /// take is put down as a loose pile instead of vanishing, which is what keeps
    /// produced stone equal to loose plus carried plus stored at every tick.
    /// </summary>
    private void StoreCarriedStone(CreatureState creature, JobState job)
    {
        if (job.StoreCell is { } site && _buildSites.ContainsKey(site))
        {
            DeliverCarriedStone(creature, job, site);
            return;
        }

        var carried = creature.CarryAmount;
        var free = job.StoreCell is { } target &&
            creature.Position == target &&
            _zones[ZoneKind.MaterialStockpile].Contains(target)
                ? Math.Max(0, PrototypeTuning.StockpileCellCapacity - StoredStoneAt(target))
                : 0;
        // Bounded by the booking as well as by the room: a carrier whose booking
        // was shrunk by a replan must not spend the slot another job is holding.
        // Whatever it cannot put away is set down here, so nothing is lost.
        var delivered = Math.Min(Math.Min(free, carried), job.StoreReserved);
        if (delivered > 0)
        {
            var cell = job.StoreCell!.Value;
            _storedStone[cell] = StoredStoneAt(cell) + delivered;
            _stoneStored += delivered;
            _stoneHaulsCompleted++;
            RecordDecision(
                creature,
                "stone_stored",
                new Dictionary<string, int>
                {
                    ["quantity"] = delivered,
                    ["cellX"] = cell.X,
                    ["cellY"] = cell.Y,
                    ["stored"] = StoredStoneAt(cell),
                    ["capacity"] = PrototypeTuning.StockpileCellCapacity,
                },
                JobKind.Haul,
                cell);
        }

        var spilled = carried - delivered;
        if (spilled > 0)
        {
            AddLoose(creature.Position, ResourceKind.Stone, spilled);
            _stoneSpilled += spilled;
            RecordDecision(
                creature,
                "stone_spilled",
                new Dictionary<string, int>
                {
                    ["quantity"] = spilled,
                    ["tileX"] = creature.Position.X,
                    ["tileY"] = creature.Position.Y,
                },
                JobKind.Haul,
                creature.Position);
        }

        ReleaseStoreReservation(job);
        creature.Carrying = null;
        creature.CarryAmount = 0;
    }

    /// <summary>
    /// The moment carried stone becomes site stone. The rules are the stockpile's,
    /// with the site's own demand as the capacity: anything the site cannot take is
    /// put down as a loose pile rather than vanishing, so produced stone still
    /// equals loose plus carried plus stored plus delivered plus consumed.
    /// </summary>
    private void DeliverCarriedStone(CreatureState creature, JobState job, GridPoint tile)
    {
        var carried = creature.CarryAmount;
        var site = _buildSites[tile];
        var free = creature.Position == tile
            ? Math.Max(0, PrototypeTuning.BuildStoneCost - site.Delivered)
            : 0;
        var delivered = Math.Min(Math.Min(free, carried), job.StoreReserved);
        if (delivered > 0)
        {
            site.Delivered += delivered;
            _stoneDelivered += delivered;
            RecordDecision(
                creature,
                "stone_delivered",
                new Dictionary<string, int>
                {
                    ["quantity"] = delivered,
                    ["tileX"] = tile.X,
                    ["tileY"] = tile.Y,
                    ["delivered"] = site.Delivered,
                    ["required"] = PrototypeTuning.BuildStoneCost,
                },
                JobKind.Haul,
                tile);
        }

        var spilled = carried - delivered;
        if (spilled > 0)
        {
            AddLoose(creature.Position, ResourceKind.Stone, spilled);
            _stoneSpilled += spilled;
            RecordDecision(
                creature,
                "stone_spilled",
                new Dictionary<string, int>
                {
                    ["quantity"] = spilled,
                    ["tileX"] = creature.Position.X,
                    ["tileY"] = creature.Position.Y,
                },
                JobKind.Haul,
                creature.Position);
        }

        ReleaseStoreReservation(job);
        creature.Carrying = null;
        creature.CarryAmount = 0;
    }

    private void CompleteJob(CreatureState creature, JobState job)
    {
        switch (job.Kind)
        {
            case JobKind.Harvest:
                _beds[job.Origin].Growth = 0;
                AddLoose(job.Origin, ResourceKind.RawMushroom, PrototypeTuning.HarvestOutput);
                _harvestsCompleted++;
                break;
            case JobKind.Haul when job.Resource == ResourceKind.Stone:
                StoreCarriedStone(creature, job);
                break;
            case JobKind.Haul:
                var free = PrototypeTuning.LarderCapacity - _stockRaw - _stockMeals;
                var delivered = Math.Min(free, creature.CarryAmount);
                if (creature.Carrying == ResourceKind.RawMushroom)
                {
                    _stockRaw += delivered;
                    if (delivered > 0)
                    {
                        _rawHaulsCompleted++;
                    }
                }
                else
                {
                    _stockMeals += delivered;
                    if (delivered > 0)
                    {
                        _mealHaulsCompleted++;
                    }
                }

                if (delivered < creature.CarryAmount)
                {
                    AddLoose(
                        creature.Position,
                        creature.Carrying!.Value,
                        creature.CarryAmount - delivered);
                }

                creature.Carrying = null;
                creature.CarryAmount = 0;
                break;
            case JobKind.Cook:
                creature.Carrying = null;
                creature.CarryAmount = 0;
                AddLoose(job.Origin, ResourceKind.Meal, PrototypeTuning.CookOutput);
                MealsProduced += PrototypeTuning.CookOutput;
                _cookBatchesCompleted++;
                break;
            case JobKind.Dig:
                _map.Excavate(job.Origin);
                _digDesignations.Remove(job.Origin);
                AddLoose(job.Origin, ResourceKind.Stone, PrototypeTuning.DigStoneYield);
                _digsCompleted++;
                _stoneProduced += PrototypeTuning.DigStoneYield;
                RecordDecision(
                    creature,
                    "dig_completed",
                    new Dictionary<string, int>
                    {
                        ["tileX"] = job.Origin.X,
                        ["tileY"] = job.Origin.Y,
                        ["stone"] = PrototypeTuning.DigStoneYield,
                    },
                    job.Kind,
                    job.Origin);
                break;
            case JobKind.Build:
                CompleteBuild(creature, job);
                break;
            case JobKind.Drill:
                creature.MartialForm = Math.Min(
                    100,
                    creature.MartialForm + PrototypeTuning.DrillGain);
                creature.Fatigue = Math.Min(
                    100,
                    creature.Fatigue + PrototypeTuning.DrillFatigue);
                creature.Satiety = Math.Max(
                    0,
                    creature.Satiety - PrototypeTuning.DrillSatietyCost);
                break;
        }

        FinishJob(creature, job);
    }

    /// <summary>
    /// The only place stone leaves the world, and the second place the map
    /// changes. The cost is spent, the blueprint is gone, and the tile becomes a
    /// training post that the existing Drill rules cannot tell from an authored
    /// one — which is the whole point of the step.
    /// </summary>
    private void CompleteBuild(CreatureState creature, JobState job)
    {
        var site = _buildSites[job.Origin];
        _buildSites.Remove(job.Origin);
        _stoneConsumed += PrototypeTuning.BuildStoneCost;
        // Defensive: the site can only ever hold what the demand allowed, so a
        // surplus is impossible. If one ever appeared it must not be deleted.
        AddLoose(job.Origin, ResourceKind.Stone, site.Delivered - PrototypeTuning.BuildStoneCost);
        _map.BuildPost(job.Origin);
        _stationOccupiedTicks.TryAdd(job.Origin, 0);
        _buildsCompleted++;
        RecordDecision(
            creature,
            "build_completed",
            new Dictionary<string, int>
            {
                ["tileX"] = job.Origin.X,
                ["tileY"] = job.Origin.Y,
                ["stone"] = PrototypeTuning.BuildStoneCost,
            },
            job.Kind,
            job.Origin);
    }

    private void FinishJob(CreatureState creature, JobState job)
    {
        _jobs.Remove(job);
        creature.CurrentJob = null;
        creature.Mode = CreatureMode.Waiting;
    }

    private void CancelJob(CreatureState creature, string reason)
    {
        if (creature.CurrentJob is not { } job)
        {
            return;
        }

        if (creature.Carrying is { } resource && creature.CarryAmount > 0)
        {
            AddLoose(creature.Position, resource, creature.CarryAmount);
            if (resource == ResourceKind.Stone)
            {
                // Counted here too, so stoneSpilled means one thing everywhere:
                // stone that went back onto the floor after being picked up or
                // stored, whatever interrupted it.
                _stoneSpilled += creature.CarryAmount;
            }

            creature.Carrying = null;
            creature.CarryAmount = 0;
        }

        job.ReservedBy = null;
        job.PickedUp = false;
        job.Target = job.Origin;
        job.RemainingTicks = 0;
        // A cancelled carrier must not keep holding stockpile space; the stone it
        // was carrying has just been dropped as a loose pile above.
        ReleaseStoreReservation(job);
        if (job.Kind is JobKind.Dig or JobKind.Build)
        {
            // Neither excavation nor construction has a partial result: an
            // interrupted tile is untouched rock or an untouched blueprint again,
            // so its progress must not survive the cancellation. The stone already
            // delivered to a blueprint stays on the site; only the labour is lost.
            job.ProgressTicks = 0;
        }

        creature.CurrentJob = null;
        creature.Mode = CreatureMode.Waiting;
        RecordDecision(
            creature,
            reason,
            new Dictionary<string, int>
            {
                ["jobId"] = checked((int)job.Id),
            },
            job.Kind,
            job.Origin);
    }

    private bool Move(CreatureState creature, GridPoint target)
    {
        if (creature.LastMoveTick == CurrentTick)
        {
            return false;
        }

        if (creature.WaitThisTick)
        {
            creature.BlockedTicks++;
            RecordMovementBlocked(creature, target);
            return false;
        }

        var next = _map.NextStep(
            creature.Position,
            target,
            _zones[ZoneKind.Forbidden]);
        if (next is null)
        {
            RecordDecision(
                creature,
                "refused_zone_unreachable",
                new Dictionary<string, int>
                {
                    ["targetX"] = target.X,
                    ["targetY"] = target.Y,
                },
                creature.CurrentJob?.Kind,
                target);
            return false;
        }

        if (next == creature.Position)
        {
            creature.BlockedTicks = 0;
            return true;
        }

        if (_yieldReservations.Contains(next.Value) &&
            creature.TrafficTarget != next)
        {
            creature.BlockedTicks++;
            RecordMovementBlocked(creature, target);
            return false;
        }

        if (_creatures.Any(other => other != creature && other.Position == next))
        {
            creature.BlockedTicks++;
            RecordMovementBlocked(creature, target);
            return false;
        }

        creature.Position = next.Value;
        creature.MoveCount++;
        creature.LastMoveTick = CurrentTick;
        creature.BlockedTicks = 0;
        return true;
    }

    private void RecordMovementBlocked(CreatureState creature, GridPoint target)
    {
        RecordDecision(
            creature,
            "waiting_blocked_by_other",
            new Dictionary<string, int>
            {
                ["targetX"] = target.X,
                ["targetY"] = target.Y,
            },
            creature.CurrentJob?.Kind,
            target: target);
    }

    private bool CanAdvanceMealQueue(CreatureState creature, out GridPoint target)
    {
        var larderTiles = ActiveLarderTiles().ToArray();
        if (larderTiles.Length == 0)
        {
            target = default;
            RecordDecision(
                creature,
                "refused_zone_unreachable",
                new Dictionary<string, int>
                {
                    ["zoneKind"] = (int)ZoneKind.Larder,
                });
            return false;
        }

        var reserved = _creatures
            .Where(candidate => candidate.MealReserved)
            .ToArray();
        var musterQueue = reserved.Any(candidate => candidate.IsMustering);
        var queue = musterQueue
            ? reserved.OrderBy(candidate => candidate.Id).ToArray()
            : reserved
                .OrderBy(candidate =>
                    larderTiles.Contains(candidate.Position) ? 0 : 1)
                .ThenBy(candidate => candidate.Position)
                .ThenBy(candidate => candidate.Id)
                .ToArray();
        var index = Array.IndexOf(queue, creature);
        var activeCount = Math.Min(queue.Length, larderTiles.Length);
        if (index < 0 || index >= activeCount)
        {
            target = default;
            return false;
        }

        var active = queue.Take(activeCount).ToArray();
        var occupiedAssignment = active
            .Where(candidate => larderTiles.Contains(candidate.Position))
            .ToDictionary(candidate => candidate.Id, candidate => candidate.Position);
        if (occupiedAssignment.TryGetValue(creature.Id, out target))
        {
            return true;
        }

        var claimed = occupiedAssignment.Values.ToHashSet();
        foreach (var candidate in active
                     .Where(candidate => !occupiedAssignment.ContainsKey(candidate.Id)))
        {
            var assignment = larderTiles
                .Where(tile => !claimed.Contains(tile))
                .Select(tile => new
                {
                    Tile = tile,
                    Distance = _map.Distance(
                        candidate.Position,
                        tile,
                        _zones[ZoneKind.Forbidden]),
                })
                .Where(item => item.Distance is not null)
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Tile)
                .FirstOrDefault();
            if (assignment is null)
            {
                if (candidate == creature)
                {
                    target = default;
                    RecordDecision(
                        creature,
                        "refused_zone_unreachable",
                        new Dictionary<string, int>
                        {
                            ["zoneKind"] = (int)ZoneKind.Larder,
                        });
                    return false;
                }

                continue;
            }

            occupiedAssignment[candidate.Id] = assignment.Tile;
            claimed.Add(assignment.Tile);
        }

        if (!occupiedAssignment.TryGetValue(creature.Id, out target))
        {
            return false;
        }

        var assignedTarget = target;
        var laneOccupied = _creatures.Any(other =>
            other != creature &&
            other.CurrentJob is not null &&
            other.Position == assignedTarget);
        if (laneOccupied)
        {
            RecordMovementBlocked(creature, target);
            return false;
        }

        return true;
    }

    private void ApplyPassiveProcesses()
    {
        foreach (var bed in _beds.Values)
        {
            if (!bed.IsRipe)
            {
                bed.Growth++;
            }
        }

        if ((CurrentTick + 1) % PrototypeTuning.SatietyDecayPeriod == 0)
        {
            foreach (var creature in _creatures)
            {
                creature.Satiety = Math.Max(0, creature.Satiety - 1);
            }
        }

        _peakMeals = Math.Max(_peakMeals, _stockMeals);
        MendTheWounded();
    }

    /// <summary>
    /// A wound closes over time in a creature that rests and eats. It is the one
    /// thing the window between two waves needed to become a decision: the
    /// domain spends a bunk and a portion on getting somebody back on their
    /// feet, or it meets the next wave short-handed.
    ///
    /// Healing is read off the same health share that set the wound in the first
    /// place, so there is one rule and not two: above the light-injury share the
    /// wound stops being heavy, and at full health it is gone. Nothing here
    /// mends a creature that is not lying down and fed.
    /// </summary>
    private void MendTheWounded()
    {
        foreach (var creature in _creatures.OrderBy(creature => creature.Id))
        {
            if (creature.Injury == InjuryKind.None ||
                creature.Mode != CreatureMode.Resting ||
                creature.Satiety < PrototypeTuning.RecoveryMinSatiety)
            {
                continue;
            }

            creature.RecoveryTicks++;
            if (creature.RecoveryTicks % PrototypeTuning.HpRecoveryPeriod != 0)
            {
                continue;
            }

            creature.Hp = Math.Min(creature.MaxHp, creature.Hp + 1);
            var mended = creature.Injury switch
            {
                InjuryKind.Heavy when creature.Hp * 100 >
                    creature.MaxHp * PrototypeTuning.LightInjuryShare => InjuryKind.Light,
                InjuryKind.Light when creature.Hp >= creature.MaxHp => InjuryKind.None,
                _ => creature.Injury,
            };
            if (mended == creature.Injury)
            {
                continue;
            }

            creature.Injury = mended;
            creature.RecoveryTicks = 0;
            RecordDecision(
                creature,
                mended == InjuryKind.None ? "injury_healed" : "injury_mending",
                new Dictionary<string, int>
                {
                    ["hp"] = creature.Hp,
                    ["maxHp"] = creature.MaxHp,
                },
                JobKind.Rest);
        }
    }

    private void ConsumeReservedMeal(CreatureState creature)
    {
        if (!creature.MealReserved)
        {
            return;
        }

        if (_stockMeals <= 0)
        {
            creature.MealReserved = false;
            return;
        }

        _stockMeals--;
        MealsEaten++;
        creature.Satiety = Math.Min(100, creature.Satiety + PrototypeTuning.MealSatiety);
        creature.MealReserved = false;
    }

    private void CancelMealReservation(CreatureState creature)
    {
        creature.MealReserved = false;
        creature.SpecialTarget = null;
        creature.SpecialTicks = 0;
    }

    private int AvailableMealsForReservation()
    {
        return Math.Max(0, _stockMeals - ReservedMeals());
    }

    private int ReservedMeals()
    {
        return _creatures.Count(creature => creature.MealReserved);
    }

    /// <summary>
    /// The muster rule is now a standing order rather than a one-off: it fires
    /// ahead of every wave that has not arrived yet. That is what makes it a
    /// real trade — thirty per cent of each gap spent standing in the gathering
    /// zone instead of working.
    /// </summary>
    private bool IsMusterActive()
    {
        var lead = _rules["muster_lead_ticks"];
        if (lead <= 0 || CurrentWave() is not { } wave || wave.ArriveTick <= CurrentTick)
        {
            return false;
        }

        return CurrentTick >= wave.ArriveTick - lead;
    }

    private GridPoint MusterTargetFor(int creatureId)
    {
        var zone = _zones[ZoneKind.Watch].Count > 0
            ? _zones[ZoneKind.Watch]
            : _zones[ZoneKind.Larder];
        if (creatureId < zone.Count)
        {
            return zone.ElementAt(creatureId);
        }

        return _map.PassableTiles()
            .Where(tile => !zone.Contains(tile) && tile != PrototypeMap.Gate)
            .Select(tile => new
            {
                Tile = tile,
                Distance = zone
                    .Select(target => _map.Distance(tile, target, _zones[ZoneKind.Forbidden]))
                    .Where(distance => distance is not null)
                    .Min() ?? int.MaxValue,
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Tile)
            .ElementAt(creatureId - zone.Count)
            .Tile;
    }

    private bool TryInitialTarget(
        CreatureState creature,
        JobState job,
        out GridPoint target)
    {
        if (job.Kind == JobKind.Dig)
        {
            // The worker never enters the rock: it stands on a neighbouring tile.
            return TryFindDigApproach(creature, job.Origin, out target);
        }

        if (job.Kind != JobKind.Cook)
        {
            target = job.Origin;
            return true;
        }

        return TryFindLarderTarget(creature, out target, out var availability) &&
            availability == LarderAvailability.Available;
    }

    private bool TryFindLarderTarget(
        CreatureState creature,
        out GridPoint target,
        out LarderAvailability availability)
    {
        var reachable = ActiveLarderTiles()
            .Select(candidate => new
            {
                Target = candidate,
                Distance = _map.Distance(
                    creature.Position,
                    candidate,
                    _zones[ZoneKind.Forbidden]),
                Claims = _creatures.Count(other =>
                    other != creature &&
                    (other.CurrentJob?.Target == candidate ||
                     (other.MealReserved &&
                      other.SpecialTarget == candidate))),
            })
            .Where(item => item.Distance is not null)
            .OrderBy(item => item.Claims)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Target)
            .ToArray();
        if (reachable.Length == 0)
        {
            target = default;
            availability = LarderAvailability.Unreachable;
            return false;
        }

        var available = reachable.FirstOrDefault(item =>
            !_creatures.Any(other =>
                other != creature &&
                other.Position == item.Target));
        if (available is not null)
        {
            target = available.Target;
            availability = LarderAvailability.Available;
            return true;
        }

        target = reachable[0].Target;
        availability = LarderAvailability.Occupied;
        return true;
    }

    private IEnumerable<GridPoint> ActiveLarderTiles()
    {
        return PrototypeMap.LarderTiles
            .Where(tile => _zones[ZoneKind.Larder].Contains(tile))
            .Order();
    }

    /// <summary>
    /// Stockpile cells a creature could actually use. A cell inside
    /// <see cref="ZoneKind.Forbidden"/> keeps whatever it already stores but stops
    /// being a destination, because nobody is allowed to walk onto it.
    /// </summary>
    private IEnumerable<GridPoint> UsableStockpileCells()
    {
        return _zones[ZoneKind.MaterialStockpile]
            .Where(tile =>
                _map.IsPassable(tile) &&
                !_zones[ZoneKind.Forbidden].Contains(tile))
            .Order();
    }

    private int StoredStoneAt(GridPoint tile) => _storedStone.GetValueOrDefault(tile);

    /// <summary>
    /// Stone already booked by a live Haul job for this cell. Counting it is what
    /// prevents two creatures from filling the same last free slot.
    /// </summary>
    private int IncomingStoneAt(GridPoint tile)
    {
        return _jobs
            .Where(job => job.StoreCell == tile)
            .Sum(job => job.StoreReserved);
    }

    private int FreeStoneCapacityAt(GridPoint tile)
    {
        return Math.Max(
            0,
            PrototypeTuning.StockpileCellCapacity - StoredStoneAt(tile) - IncomingStoneAt(tile));
    }

    private int AvailableStoneCapacity()
    {
        return UsableStockpileCells().Sum(FreeStoneCapacityAt);
    }

    private int StoredStoneTotal() => _storedStone.Values.Sum();

    private int CarriedStoneTotal()
    {
        return _creatures
            .Where(creature => creature.Carrying == ResourceKind.Stone)
            .Sum(creature => creature.CarryAmount);
    }

    private int ReservedStoneTotal()
    {
        return _jobs.Sum(job => job.StoreReserved);
    }

    /// <summary>
    /// A construction site the crew may actually serve. Like a stockpile cell, a
    /// site inside <see cref="ZoneKind.Forbidden"/> keeps whatever was delivered
    /// but stops being a destination, because nobody may walk onto it.
    /// </summary>
    private bool IsBuildSiteWorkable(GridPoint tile)
    {
        return _map.IsBuildableFloor(tile) && !_zones[ZoneKind.Forbidden].Contains(tile);
    }

    private int FreeBuildDemandAt(GridPoint tile)
    {
        return _buildSites.TryGetValue(tile, out var site)
            ? Math.Max(0, PrototypeTuning.BuildStoneCost - site.Delivered - IncomingStoneAt(tile))
            : 0;
    }

    private int AvailableBuildDemand()
    {
        return _buildSites.Keys.Where(IsBuildSiteWorkable).Sum(FreeBuildDemandAt);
    }

    private int SiteStoneTotal() => _buildSites.Values.Sum(site => site.Delivered);

    /// <summary>
    /// Free room at a stone destination, whichever kind it is. Stockpile cells and
    /// construction sites can never share a tile, so one tile has exactly one
    /// meaning here and one <see cref="IncomingStoneAt"/> booking.
    /// </summary>
    private int FreeStoneRoomAt(GridPoint tile)
    {
        return _buildSites.ContainsKey(tile)
            ? FreeBuildDemandAt(tile)
            : FreeStoneCapacityAt(tile);
    }

    /// <summary>
    /// Picks where a load of stone should go: nearest to the job's origin, ties
    /// broken by tile order. Construction sites outrank stockpile cells, because a
    /// blueprint is an explicit player intention and putting the stone away first
    /// would make the crew carry it twice. A load withdrawn from the stockpile may
    /// only go to a site, which is what stops material from circling.
    /// It is a pure function of canonical state, so the same log always produces
    /// the same destination.
    /// </summary>
    private bool TryPlanStoneDestination(
        JobState job,
        GridPoint from,
        int quantity,
        out GridPoint cell,
        out int amount)
    {
        var candidates = _buildSites.Keys
            .Where(tile => IsBuildSiteWorkable(tile) && FreeBuildDemandAt(tile) > 0)
            .Select(tile => new { Tier = 0, Tile = tile })
            .ToList();
        if (job.SourceCell is null)
        {
            candidates.AddRange(UsableStockpileCells()
                .Where(tile => FreeStoneCapacityAt(tile) > 0)
                .Select(tile => new { Tier = 1, Tile = tile }));
        }

        var candidate = candidates
            .Select(item => new
            {
                item.Tier,
                item.Tile,
                Distance = _map.Distance(from, item.Tile, _zones[ZoneKind.Forbidden]),
            })
            .Where(item => item.Distance is not null)
            .OrderBy(item => item.Tier)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Tile)
            .FirstOrDefault();
        if (candidate is null)
        {
            cell = default;
            amount = 0;
            return false;
        }

        cell = candidate.Tile;
        amount = Math.Min(quantity, FreeStoneRoomAt(cell));
        return true;
    }

    private void ReleaseStoreReservation(JobState job)
    {
        job.StoreCell = null;
        job.StoreReserved = 0;
    }

    /// <summary>
    /// A destination stays valid only while it is still a usable stockpile cell
    /// with room for the booked load. Erase, Forbidden and a full cell all
    /// invalidate it, and the carrier is re-planned rather than left walking to a
    /// place that can no longer accept the stone.
    /// </summary>
    private bool IsStoreCellStillValid(JobState job, GridPoint from)
    {
        if (job.StoreCell is not { } cell)
        {
            return false;
        }

        if (_map.Distance(from, cell, _zones[ZoneKind.Forbidden]) is null)
        {
            return false;
        }

        // A construction site is a destination on exactly the same terms: it must
        // still exist, still be reachable, and still have room for the booking.
        if (_buildSites.TryGetValue(cell, out var site))
        {
            return IsBuildSiteWorkable(cell) &&
                site.Delivered + job.StoreReserved <= PrototypeTuning.BuildStoneCost;
        }

        return _zones[ZoneKind.MaterialStockpile].Contains(cell) &&
            _map.IsPassable(cell) &&
            !_zones[ZoneKind.Forbidden].Contains(cell) &&
            StoredStoneAt(cell) + job.StoreReserved <= PrototypeTuning.StockpileCellCapacity;
    }

    /// <summary>
    /// Runs before job cancellation each tick. Stone that is already on a
    /// creature's back is never deleted here: it is either re-routed to another
    /// cell or put down as a loose pile on the tile the carrier stands on.
    /// </summary>
    private void RevalidateStoneHauls()
    {
        foreach (var job in _jobs
                     .Where(item =>
                         item.Kind == JobKind.Haul &&
                         item.Resource == ResourceKind.Stone &&
                         item.StoreCell is not null)
                     .OrderBy(item => item.Id)
                     .ToArray())
        {
            var carrier = _creatures.FirstOrDefault(creature => creature.CurrentJob == job);
            var from = carrier?.Position ?? job.Origin;
            if (IsStoreCellStillValid(job, from))
            {
                continue;
            }

            var previous = job.StoreCell!.Value;
            var wanted = job.PickedUp && carrier is not null
                ? carrier.CarryAmount
                : job.Quantity;
            ReleaseStoreReservation(job);
            if (TryPlanStoneDestination(job, from, wanted, out var replacement, out var amount) &&
                amount > 0)
            {
                job.StoreCell = replacement;
                job.StoreReserved = amount;
                if (job.PickedUp)
                {
                    job.Target = replacement;
                }
                else
                {
                    // Keep the job's intent equal to what it may actually deliver,
                    // so the pile is not half-lifted and then put straight back.
                    job.Quantity = amount;
                }

                if (carrier is not null)
                {
                    RecordDecision(
                        carrier,
                        "stone_target_replanned",
                        new Dictionary<string, int>
                        {
                            ["fromX"] = previous.X,
                            ["fromY"] = previous.Y,
                            ["toX"] = replacement.X,
                            ["toY"] = replacement.Y,
                            ["quantity"] = amount,
                        },
                        JobKind.Haul,
                        replacement);
                }

                continue;
            }

            // Nowhere left to put it. Dropping the load where the carrier stands is
            // the only conserving answer; teleporting it back would be a lie.
            if (carrier is null)
            {
                _jobs.Remove(job);
                continue;
            }

            var dropped = carrier.CarryAmount;
            if (job.PickedUp && dropped > 0)
            {
                AddLoose(carrier.Position, ResourceKind.Stone, dropped);
                carrier.Carrying = null;
                carrier.CarryAmount = 0;
                _stoneSpilled += dropped;
            }

            RecordDecision(
                carrier,
                "stone_haul_cancelled",
                new Dictionary<string, int>
                {
                    ["jobId"] = checked((int)job.Id),
                    ["dropped"] = job.PickedUp ? dropped : 0,
                    ["cellX"] = previous.X,
                    ["cellY"] = previous.Y,
                },
                JobKind.Haul,
                carrier.Position);
            carrier.CurrentJob = null;
            carrier.Mode = CreatureMode.Waiting;
            _jobs.Remove(job);
        }
    }

    private bool TryNearestRestTarget(
        CreatureState creature,
        IReadOnlySet<GridPoint> claimed,
        out GridPoint target)
    {
        if (_priorities[JobKind.Rest] == 0)
        {
            target = default;
            return false;
        }

        var candidate = PrototypeMap.BunkTiles
            .Where(tile =>
                _zones[ZoneKind.Quarters].Contains(tile) &&
                !_zones[ZoneKind.Forbidden].Contains(tile) &&
                !claimed.Contains(tile) &&
                !_creatures.Any(other =>
                    other != creature && other.Position == tile) &&
                !_jobs.Any(job =>
                    job.Kind == JobKind.Rest &&
                    job.Origin == tile &&
                    job.PersonalCreatureId != creature.Id))
            .Select(tile => new
            {
                Target = tile,
                Distance = _map.Distance(
                    creature.Position,
                    tile,
                    _zones[ZoneKind.Forbidden]),
            })
            .Where(item => item.Distance is not null)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Target)
            .FirstOrDefault();
        if (candidate is null)
        {
            target = default;
            return false;
        }

        target = candidate.Target;
        return true;
    }

    private int WorkDuration(CreatureState creature, JobKind kind)
    {
        var baseDuration = kind switch
        {
            JobKind.Harvest => PrototypeTuning.HarvestTicks,
            JobKind.Haul => PrototypeTuning.HaulTransferTicks,
            JobKind.Cook => PrototypeTuning.CookTicks,
            JobKind.Drill => PrototypeTuning.DrillTicks,
            JobKind.Dig => PrototypeTuning.DigTicks,
            JobKind.Build => PrototypeTuning.BuildTicks,
            JobKind.Rest or JobKind.Watch => int.MaxValue,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (baseDuration == int.MaxValue)
        {
            return baseDuration;
        }

        var exhausted = creature.Fatigue > PrototypeTuning.RestThreshold &&
            !TryNearestRestTarget(
                creature,
                new HashSet<GridPoint>(),
                out _);
        var multiplier = exhausted ? PrototypeTuning.ExhaustedSpeedMultiplier : 1;
        return Math.Max(
            1,
            baseDuration * multiplier * PrototypeTuning.AffinitySpeedDenominator /
            (PrototypeTuning.AffinitySpeedDenominator + creature.Affinity(kind)));
    }

    private int Urgency(JobKind kind, ResourceKind? resource)
    {
        return kind switch
        {
            JobKind.Cook when _stockMeals <= PrototypeTuning.LowMealsThreshold =>
                PrototypeTuning.UrgencyLowMeals,
            JobKind.Haul when resource == ResourceKind.Meal =>
                PrototypeTuning.UrgencyHaulMeal,
            JobKind.Haul when resource == ResourceKind.RawMushroom =>
                PrototypeTuning.UrgencyHaulRaw,
            JobKind.Haul when resource == ResourceKind.Stone =>
                PrototypeTuning.UrgencyHaulStone,
            JobKind.Harvest when _beds.Values.Count(bed => bed.IsRipe) >=
                PrototypeTuning.RipeBacklogThreshold =>
                PrototypeTuning.UrgencyRipeBacklog,
            _ => 0,
        };
    }

    private int DiagnosticUrgency(JobKind kind)
    {
        return _jobs
            .Where(job => job.Kind == kind && job.ReservedBy is null)
            .Select(job => Urgency(job.Kind, job.Resource))
            .DefaultIfEmpty(0)
            .Max();
    }

    private bool AnyReachableTarget(CreatureState creature, JobKind kind)
    {
        bool Reachable(GridPoint target)
        {
            return _map.Distance(
                creature.Position,
                target,
                _zones[ZoneKind.Forbidden]) is not null;
        }

        return kind switch
        {
            JobKind.Harvest => PrototypeMap.BedTiles.Any(tile =>
                _zones[ZoneKind.Farm].Contains(tile) && Reachable(tile)),
            // A haul is reachable when some unreserved job has both a reachable
            // pile and a reachable destination — the larder for food, a usable
            // stockpile cell for stone.
            JobKind.Haul => _jobs.Any(job =>
                job.Kind == JobKind.Haul &&
                job.ReservedBy is null &&
                Reachable(job.Origin) &&
                (job.Resource == ResourceKind.Stone
                    ? UsableStockpileCells().Any(Reachable)
                    : ActiveLarderTiles().Any(Reachable))),
            JobKind.Cook => PrototypeMap.KitchenTiles.Any(tile =>
                    _zones[ZoneKind.Kitchen].Contains(tile) && Reachable(tile)) &&
                ActiveLarderTiles().Any(Reachable),
            JobKind.Rest => PrototypeMap.BunkTiles.Any(tile =>
                _zones[ZoneKind.Quarters].Contains(tile) && Reachable(tile)),
            JobKind.Drill => _map.PostTiles().Any(tile =>
                _zones[ZoneKind.TrainingGround].Contains(tile) && Reachable(tile)),
            JobKind.Watch => _zones[ZoneKind.Watch].Any(Reachable),
            JobKind.Dig => _digDesignations.Any(tile =>
                _map.IsDiggable(tile) && DigApproachTiles(tile).Any(Reachable)),
            // A blueprint is only workable once its stone has arrived: "no
            // material yet" and "cannot get there" must stay different answers.
            JobKind.Build => _buildSites.Values.Any(site =>
                site.Delivered >= PrototypeTuning.BuildStoneCost &&
                IsBuildSiteWorkable(site.Tile) &&
                Reachable(site.Tile)),
            _ => false,
        };
    }

    private static int Percentage(int numerator, int denominator)
    {
        return denominator == 0
            ? 0
            : numerator * 100 / denominator;
    }

    private bool ZoneCoversFeature(ZoneKind zone, TileKind feature)
    {
        return _zones[zone].Any(tile => _map[tile] == feature);
    }

    private bool HasLoose(GridPoint point, ResourceKind resource)
    {
        return LooseAt(point, resource) > 0;
    }

    private int LooseAt(GridPoint point, ResourceKind resource)
    {
        return _loose.GetValueOrDefault((point, resource));
    }

    private int LooseCount(ResourceKind resource)
    {
        return _loose
            .Where(pair => pair.Key.Resource == resource)
            .Sum(pair => pair.Value);
    }

    private void AddLoose(GridPoint point, ResourceKind resource, int quantity)
    {
        if (quantity <= 0)
        {
            return;
        }

        _loose[(point, resource)] = LooseAt(point, resource) + quantity;
    }

    private void RemoveLoose(GridPoint point, ResourceKind resource, int quantity)
    {
        var remaining = LooseAt(point, resource) - quantity;
        if (remaining <= 0)
        {
            _loose.Remove((point, resource));
        }
        else
        {
            _loose[(point, resource)] = remaining;
        }
    }

    private void RecordDecision(
        CreatureState creature,
        string reason,
        Dictionary<string, int> details,
        JobKind? kind = null,
        GridPoint? target = null)
    {
        creature.LastDecision = new PrototypeDecision(CurrentTick, reason, details, kind, target);
        var previous = _events.LastOrDefault(@event => @event.CreatureId == creature.Id);
        if (previous is not null &&
            previous.ReasonCode == reason &&
            previous.JobKind == kind &&
            previous.Target == target &&
            DetailsEqual(previous.Details, details))
        {
            previous.LastTick = CurrentTick;
            previous.Repeats++;
            return;
        }

        _events.Add(new EventState(
            CurrentTick,
            creature.Id,
            reason,
            details,
            kind,
            target));
    }

    private static bool DetailsEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        return left.Count == right.Count &&
            left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

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
            creature.RecoveryTicks);
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
                null),
            ZoneEraseCommand erase => new(
                erase.Tick,
                "zone_erase",
                erase.ZoneKind,
                erase.Tiles.ToArray(),
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
                null),
            DigCancelCommand cancel => new(
                cancel.Tick,
                "dig_cancel",
                null,
                cancel.Tiles.ToArray(),
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
                null),
            BuildCancelCommand unbuild => new(
                unbuild.Tick,
                "build_cancel",
                null,
                unbuild.Tiles.ToArray(),
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
                priority.Value),
            SetRuleCommand rule => new(
                rule.Tick,
                "set_rule",
                null,
                [],
                null,
                rule.RuleId,
                rule.Value),
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

    private static int ComputeReadiness(CreatureState creature)
    {
        var injuryPenalty = creature.Injury switch
        {
            InjuryKind.None => 0,
            InjuryKind.Light => PrototypeTuning.InjuryLightPenalty,
            InjuryKind.Heavy => PrototypeTuning.InjuryHeavyPenalty,
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
        public InjuryKind Injury { get; set; }
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

    private sealed class RaiderState(int id, int wave, int hp, int might, GridPoint position)
    {
        public int Id { get; } = id;
        public int Wave { get; } = wave;
        public int Hp { get; set; } = hp;
        public int Might { get; } = might;
        public GridPoint Position { get; set; } = position;
        public int CarryingMeals { get; set; }
        public int StealTicks { get; set; }
        public bool ReturningToGate { get; set; }
        public RaiderMode Mode { get; set; } = RaiderMode.Raiding;
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
