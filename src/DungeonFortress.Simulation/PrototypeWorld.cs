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
    private readonly List<EventState> _events = [];
    private readonly List<RaiderState> _raiders = [];
    private readonly DeterministicRandom _combatRandom;
    private readonly Dictionary<GridPoint, int> _stationOccupiedTicks =
        PrototypeMap.KitchenTiles
            .Concat(PrototypeMap.PostTiles)
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
    private int _musterTicks;
    private int _idleTicks;
    private int _postCapacityTicks;
    private string? _outcome;
    private int? _combatEndTick;

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
        _combatRandom = new DeterministicRandom(commandLog.Seed ^ 0x636F6D626174UL);
    }

    public ulong Seed { get; }

    public int CurrentTick { get; private set; }

    public int CommandsApplied { get; private set; }

    public int MealsProduced { get; private set; }

    public int MealsEaten { get; private set; }

    public bool IsComplete => CurrentTick >= PrototypeTuning.SessionTicks;

    public void RunTicks(int tickCount)
    {
        if (tickCount < 0 || tickCount > PrototypeTuning.SessionTicks - CurrentTick)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tickCount),
                $"Tick count must leave the world between 0 and {PrototypeTuning.SessionTicks}.");
        }

        for (var index = 0; index < tickCount; index++)
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
        EnterRaiders();
        if (CurrentTick == PrototypeTuning.RaidTick)
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
                job.StoreReserved))
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
            .Concat(PrototypeMap.PostTiles)
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
            [.. _map.StockpileFloorTiles()]);
        var designations = _digDesignations
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
                _stoneSpilled),
            new PrototypeLaborSnapshot(
                _totalCreatureTicks,
                _foodWorkTicks,
                _restTicks,
                _eatTicks,
                _drillTicks,
                _watchTicks,
                _digTicks,
                _stoneHaulTicks,
                _musterTicks,
                _idleTicks,
                Percentage(_foodWorkTicks, _totalCreatureTicks),
                PrototypeMap.PostTiles.Sum(point => _stationOccupiedTicks[point]),
                _postCapacityTicks,
                Percentage(
                    PrototypeMap.PostTiles.Sum(point => _stationOccupiedTicks[point]),
                    _postCapacityTicks)),
            stations,
            events,
            new PrototypeThreatSnapshot(
                CurrentTick > PrototypeTuning.ThreatAnnounceTick,
                PrototypeTuning.ThreatAnnounceTick,
                PrototypeTuning.RaidTick,
                PrototypeTuning.RaiderCount,
                Math.Max(0, PrototypeTuning.RaidTick - CurrentTick)),
            _raiders.OrderBy(raider => raider.Id).Select(raider => new PrototypeRaiderSnapshot(
                raider.Id, raider.Hp, raider.Might, raider.Position, raider.CarryingMeals, raider.StealTicks, raider.ReturningToGate, raider.Mode)).ToArray(),
            new PrototypeSessionResultSnapshot(
                _outcome,
                _combatEndTick,
                _outcome is null && CurrentTick >= PrototypeTuning.RaidTick,
                _creatures.Count(creature => creature.Mode == CreatureMode.Downed),
                _creatures.Count(creature => creature.Mode == CreatureMode.Fled),
                _raiders.Count(raider => raider.Mode == RaiderMode.Downed),
                _raiders.Where(raider => raider.Mode == RaiderMode.Escaped)
                    .Sum(raider => raider.CarryingMeals),
                _stockMeals));
    }

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
            SetPriorityCommand priority => priority,
            SetRuleCommand rule => rule,
            _ => throw new InvalidDataException(
                $"Unsupported prototype command: {command.GetType().Name}"),
        };
    }

    private static List<CreatureState> CreateCreatures(ulong seed)
    {
        var random = new DeterministicRandom(seed ^ 0x776F726C645F696EUL);
        var definitions = new[]
        {
            new CreatureDefinition(0, "Брусок", 2, 3, Affinities((JobKind.Cook, 2)), new(11, 8)),
            new CreatureDefinition(1, "Кремень", 4, 4, Affinities((JobKind.Watch, 2)), new(24, 12)),
            new CreatureDefinition(2, "Мотылёк", 1, 2, Affinities((JobKind.Haul, 2)), new(13, 9)),
            new CreatureDefinition(3, "Смола", 2, 3, Affinities((JobKind.Harvest, 2)), new(4, 3)),
            new CreatureDefinition(4, "Дёготь", 3, 2, Affinities((JobKind.Harvest, 1), (JobKind.Haul, 1)), new(6, 5)),
            new CreatureDefinition(5, "Уголёк", 3, 3, Affinities((JobKind.Drill, 2)), new(21, 9)),
            new CreatureDefinition(6, "Прель", 1, 4, Affinities((JobKind.Cook, 1), (JobKind.Harvest, 1)), new(10, 9)),
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
                        : "refused_zone_not_designated";
            CancelJob(creature, reason);
        }
    }

    private bool JobStillSupported(JobState job)
    {
        return job.Kind switch
        {
            JobKind.Harvest => _zones[ZoneKind.Farm].Contains(job.Origin),
            JobKind.Haul => HasLoose(job.Origin, job.Resource!.Value) || job.PickedUp,
            JobKind.Cook => _zones[ZoneKind.Kitchen].Contains(job.Origin),
            JobKind.Rest => _zones[ZoneKind.Quarters].Contains(job.Origin),
            JobKind.Drill => _zones[ZoneKind.TrainingGround].Contains(job.Origin),
            JobKind.Watch => _zones[ZoneKind.Watch].Contains(job.Origin),
            JobKind.Dig => _digDesignations.Contains(job.Origin) && _map.IsDiggable(job.Origin),
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
            var stoneCapacity = AvailableStoneCapacity();
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
                             creature.Fatigue >= PrototypeTuning.RestSeekThreshold &&
                             !creature.IsMustering &&
                             creature.Mode != CreatureMode.Eating &&
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
            foreach (var post in PrototypeMap.PostTiles
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

        _jobs.RemoveAll(job => job.ReservedBy is null && !desired.Contains(job.Key));
    }

    private void EnsureJob(
        string key,
        JobKind kind,
        GridPoint target,
        ResourceKind? resource,
        int quantity,
        HashSet<string> desired,
        int? personalCreatureId = null)
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
            personalCreatureId));
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
                                ["raidTick"] = PrototypeTuning.RaidTick,
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

            if (creature.Fatigue > PrototypeTuning.RestThreshold &&
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

    private static GridPoint? PrimaryDestination(CreatureState creature)
    {
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

    private static bool CanYield(CreatureState creature, bool allowUrgent)
    {
        return creature.TrafficTarget is null &&
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

                if (job.Kind == JobKind.Rest &&
                    creature.Fatigue < PrototypeTuning.RestSeekThreshold)
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
                    !TryPlanStoreCell(job.Origin, job.Quantity, out _, out _))
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
            if (!TryPlanStoreCell(job.Origin, job.Quantity, out var cell, out var amount) ||
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
            _ => new(),
        };
    }

    private void ActCreatures()
    {
        foreach (var creature in _creatures.OrderBy(creature => creature.Id))
        {
            if (creature.Mode == CreatureMode.Fighting)
            {
                ActCombatant(creature);
                continue;
            }

            if (creature.Mode is CreatureMode.Fled or CreatureMode.Downed)
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

    private void EnterRaiders()
    {
        if (CurrentTick < PrototypeTuning.RaidTick)
        {
            return;
        }

        while (_raiders.Count < PrototypeTuning.RaiderCount &&
               CurrentTick >= PrototypeTuning.RaidTick + _raiders.Count * PrototypeTuning.RaiderEntryInterval)
        {
            var id = _raiders.Count;
            _raiders.Add(new RaiderState(
                id,
                PrototypeTuning.RaiderHp,
                PrototypeTuning.RaiderMightBase + CombatJitter(PrototypeTuning.RaiderMightJitter),
                PrototypeMap.Gate));
        }
    }

    private void UpdateCombatParticipation()
    {
        if (CurrentTick < PrototypeTuning.RaidTick ||
            (CurrentTick != PrototypeTuning.RaidTick && CurrentTick % PrototypeTuning.CombatJoinRecheck != 0))
        {
            return;
        }

        foreach (var creature in _creatures.Where(c => c.Mode is not (CreatureMode.Fighting or CreatureMode.Fled or CreatureMode.Downed)).OrderBy(c => c.Id))
        {
            var failed = new Dictionary<string, int>();
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
                RecordDecision(creature, "combat_absent_unreachable", new Dictionary<string, int> { ["distance"] = distance ?? -1 });
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
            RecordDecision(creature, "combat_joined", new Dictionary<string, int> { ["readiness"] = ComputeReadiness(creature) });
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

        if (Manhattan(creature.Position, target.Position) > 1)
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
            if (defender is not null && Manhattan(defender.Position, raider.Position) <= 1)
            {
                var damage = Math.Max(PrototypeTuning.DamageFloor,
                    raider.Might - ComputeReadiness(defender) / PrototypeTuning.ArmourReadinessDivisor + CombatJitter(PrototypeTuning.DamageJitter));
                defender.Hp -= damage;
                if (defender.Hp * 100 <= defender.MaxHp * PrototypeTuning.LightInjuryShare && defender.Injury == InjuryKind.None)
                {
                    defender.Injury = InjuryKind.Light;
                }
                if (defender.Hp <= 0)
                {
                    defender.Hp = 0;
                    defender.Injury = InjuryKind.Heavy;
                    defender.Mode = CreatureMode.Downed;
                    RecordDecision(defender, "combat_downed", new Dictionary<string, int> { ["raiderId"] = raider.Id, ["damage"] = damage });
                    ApplyMorale();
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

        if (_raiders.Count == PrototypeTuning.RaiderCount && _raiders.All(raider => raider.Mode is RaiderMode.Downed or RaiderMode.Escaped) && _outcome is null)
        {
            var downed = _raiders.Count(raider => raider.Mode == RaiderMode.Downed);
            var casualties = _creatures.Count(creature => creature.Mode is CreatureMode.Downed or CreatureMode.Fled);
            _outcome = downed == PrototypeTuning.RaiderCount
                ? casualties == 0 ? "repelled_clean" : "repelled_costly"
                : downed == 0 ? "overrun" : "larder_raided";
            _combatEndTick = CurrentTick;
        }
    }

    private void ApplyMorale()
    {
        var downed = _creatures.Count(creature => creature.Mode == CreatureMode.Downed);
        foreach (var creature in _creatures.Where(creature => creature.Mode == CreatureMode.Fighting).OrderBy(creature => creature.Id))
        {
            if (creature.Grit * PrototypeTuning.MoraleGritWeight + ComputeReadiness(creature) / PrototypeTuning.MoraleReadinessDivisor >=
                PrototypeTuning.MoraleBase + PrototypeTuning.MoralePerDowned * downed)
            {
                continue;
            }
            creature.Mode = CreatureMode.Fled;
            creature.Position = new GridPoint(1, Math.Min(14, 1 + creature.Id));
            RecordDecision(creature, "combat_fled_morale", new Dictionary<string, int> { ["downedAllies"] = downed });
        }
    }

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

        if (CurrentTick >= PrototypeTuning.RaidTick)
        {
            return;
        }

        var activePosts = PrototypeMap.PostTiles
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

            if (creature.Fatigue <= PrototypeTuning.RestTarget)
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

        if (job.Kind == JobKind.Dig)
        {
            if (job.ProgressTicks == 0)
            {
                RecordDecision(
                    creature,
                    "dig_started",
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

        var available = LooseAt(job.Origin, ResourceKind.Stone);
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

        RemoveLoose(job.Origin, ResourceKind.Stone, quantity);
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
        if (job.Kind == JobKind.Dig)
        {
            // Excavation has no partial result: an interrupted tile is untouched
            // rock again, so its progress must not survive the cancellation.
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

    private bool IsMusterActive()
    {
        var lead = _rules["muster_lead_ticks"];
        return lead > 0 &&
            CurrentTick >= PrototypeTuning.RaidTick - lead &&
            CurrentTick < PrototypeTuning.RaidTick;
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
    /// Picks the stockpile cell a load of stone should go to: nearest to
    /// <paramref name="from"/>, ties broken by tile order. It is a pure function of
    /// canonical state, so the same log always produces the same destination.
    /// </summary>
    private bool TryPlanStoreCell(
        GridPoint from,
        int quantity,
        out GridPoint cell,
        out int amount)
    {
        var candidate = UsableStockpileCells()
            .Where(tile => FreeStoneCapacityAt(tile) > 0)
            .Select(tile => new
            {
                Tile = tile,
                Distance = _map.Distance(from, tile, _zones[ZoneKind.Forbidden]),
            })
            .Where(item => item.Distance is not null)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Tile)
            .FirstOrDefault();
        if (candidate is null)
        {
            cell = default;
            amount = 0;
            return false;
        }

        cell = candidate.Tile;
        amount = Math.Min(quantity, FreeStoneCapacityAt(cell));
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

        return _zones[ZoneKind.MaterialStockpile].Contains(cell) &&
            _map.IsPassable(cell) &&
            !_zones[ZoneKind.Forbidden].Contains(cell) &&
            StoredStoneAt(cell) + job.StoreReserved <= PrototypeTuning.StockpileCellCapacity &&
            _map.Distance(from, cell, _zones[ZoneKind.Forbidden]) is not null;
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
            if (TryPlanStoreCell(from, wanted, out var replacement, out var amount) &&
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
            JobKind.Drill => PrototypeMap.PostTiles.Any(tile =>
                _zones[ZoneKind.TrainingGround].Contains(tile) && Reachable(tile)),
            JobKind.Watch => _zones[ZoneKind.Watch].Any(Reachable),
            JobKind.Dig => _digDesignations.Any(tile =>
                _map.IsDiggable(tile) && DigApproachTiles(tile).Any(Reachable)),
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
            creature.ReadinessAtRaid);
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
        // stockpile cell holding capacity for a delivery that will never arrive.
        public GridPoint? StoreCell { get; set; }
        public int StoreReserved { get; set; }
    }

    private sealed class BedState(GridPoint position, int growth)
    {
        public GridPoint Position { get; } = position;
        public int Growth { get; set; } = growth;
        public bool IsRipe => Growth >= PrototypeTuning.BedGrowthTicks;
    }

    private sealed class RaiderState(int id, int hp, int might, GridPoint position)
    {
        public int Id { get; } = id;
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
