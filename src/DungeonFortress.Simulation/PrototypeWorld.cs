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
    private readonly List<EventState> _events = [];
    private long _nextJobId = 1;
    private int _nextCommandIndex;
    private int _stockRaw;
    private int _stockMeals = PrototypeTuning.StartMeals;

    public PrototypeWorld(PrototypeCommandLog commandLog)
    {
        ArgumentNullException.ThrowIfNull(commandLog);
        Seed = commandLog.Seed;
        _commands = [.. commandLog.Commands];
        ValidateCommandOrder(_commands);
        _zones = CreateDefaultZones();
        _priorities = new()
        {
            [JobKind.Harvest] = PrototypeTuning.DefaultHarvestPriority,
            [JobKind.Haul] = PrototypeTuning.DefaultHaulPriority,
            [JobKind.Cook] = PrototypeTuning.DefaultCookPriority,
            [JobKind.Rest] = PrototypeTuning.DefaultRestPriority,
            [JobKind.Drill] = PrototypeTuning.DefaultDrillPriority,
            [JobKind.Watch] = PrototypeTuning.DefaultWatchPriority,
        };
        _rules = new(StringComparer.Ordinal)
        {
            ["ration_reserve"] = 0,
            ["drill_min_satiety"] = 40,
            ["muster_lead_ticks"] = 0,
        };
        _beds = PrototypeMap.BedTiles
            .Select((point, index) => new BedState(point, index * PrototypeTuning.BedRipenessOffset))
            .ToDictionary(bed => bed.Position);
        _creatures = CreateCreatures(commandLog.Seed);
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
        if (CurrentTick == PrototypeTuning.RaidTick)
        {
            foreach (var creature in _creatures)
            {
                creature.ReadinessAtRaid = ComputeReadiness(creature);
            }
        }

        CancelInvalidJobs();
        GenerateJobs();
        DecideNeedsAndMuster();
        ClearIdleLarderAccess();
        MatchJobs();
        ActCreatures();
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
        var jobs = _jobs
            .OrderBy(job => job.Id)
            .Select(job => new PrototypeJobSnapshot(
                job.Id,
                job.Kind,
                job.Target,
                job.Resource,
                job.ReservedBy,
                job.RemainingTicks))
            .ToArray();
        var events = _events
            .Select(ToSnapshot)
            .ToArray();

        return new PrototypeSnapshot(
            1,
            Seed,
            CurrentTick,
            CommandsApplied,
            creatures,
            zones,
            priorities,
            rules,
            new PrototypeStockSnapshot(
                _stockRaw,
                _stockMeals,
                LooseCount(ResourceKind.RawMushroom),
                LooseCount(ResourceKind.Meal),
                PrototypeTuning.LarderCapacity,
                MealsProduced,
                MealsEaten),
            jobs,
            events,
            new PrototypeThreatSnapshot(
                CurrentTick > PrototypeTuning.ThreatAnnounceTick,
                PrototypeTuning.ThreatAnnounceTick,
                PrototypeTuning.RaidTick,
                4,
                Math.Max(0, PrototypeTuning.RaidTick - CurrentTick)));
    }

    private static IReadOnlyDictionary<JobKind, int> Affinities(params (JobKind Kind, int Value)[] values)
    {
        return values.OrderBy(value => value.Kind).ToDictionary(value => value.Kind, value => value.Value);
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

    private Dictionary<ZoneKind, SortedSet<GridPoint>> CreateDefaultZones()
    {
        var zones = Enum.GetValues<ZoneKind>()
            .ToDictionary(kind => kind, _ => new SortedSet<GridPoint>());
        PaintRectangle(zones[ZoneKind.Farm], new(1, 1), new(6, 7));
        PaintRectangle(zones[ZoneKind.Kitchen], new(9, 6), new(12, 8));
        PaintRectangle(zones[ZoneKind.Larder], new(13, 6), new(16, 8));
        PaintRectangle(zones[ZoneKind.Quarters], new(19, 2), new(23, 5));
        return zones;
    }

    private void PaintRectangle(SortedSet<GridPoint> zone, GridPoint start, GridPoint end)
    {
        for (var y = start.Y; y <= end.Y; y++)
        {
            for (var x = start.X; x <= end.X; x++)
            {
                var point = new GridPoint(x, y);
                if (_map.IsPassable(point) && _map[point] != TileKind.Gate)
                {
                    zone.Add(point);
                }
            }
        }
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
                    _zones[erase.ZoneKind].Remove(tile);
                }

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
        }
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
            _ => false,
        };
    }

    private void GenerateJobs()
    {
        var desired = new HashSet<string>(StringComparer.Ordinal);
        var ripeCount = _beds.Values.Count(bed => bed.IsRipe);
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

        if (_priorities[JobKind.Haul] > 0 && ZoneCoversFeature(ZoneKind.Larder, TileKind.Larder))
        {
            foreach (var entry in _loose
                         .Where(pair => pair.Value > 0)
                         .OrderBy(pair => pair.Key.Point)
                         .ThenBy(pair => pair.Key.Resource))
            {
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
            ZoneCoversFeature(ZoneKind.Quarters, TileKind.Bunk) &&
            _creatures.Any(creature => creature.Fatigue >= PrototypeTuning.RestSeekThreshold))
        {
            foreach (var bunk in PrototypeMap.BunkTiles
                         .Where(tile => _zones[ZoneKind.Quarters].Contains(tile))
                         .Order())
            {
                EnsureJob(
                    $"rest:{bunk.X}:{bunk.Y}",
                    JobKind.Rest,
                    bunk,
                    null,
                    0,
                    desired);
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

        _jobs.RemoveAll(job => job.ReservedBy is null && !desired.Contains(job.Key));
        _ = ripeCount;
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
                _priorities[JobKind.Rest] == 0)
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
    }

    private bool TryStartEating(CreatureState creature, bool ignoreReserve, string reason)
    {
        if (creature.Mode == CreatureMode.Eating)
        {
            return true;
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

        if (creature.CurrentJob is not null)
        {
            CancelJob(creature, reason);
        }

        creature.MealReserved = true;
        creature.Mode = CreatureMode.Eating;
        creature.SpecialTarget = NearestAvailableLarder(creature);
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

    private void ClearIdleLarderAccess()
    {
        foreach (var creature in _creatures
                     .Where(creature =>
                         DistanceToLarder(creature.Position) <= 1 &&
                         creature.CurrentJob is null &&
                         !creature.MealReserved &&
                         !creature.IsMustering)
                     .OrderBy(creature => creature.Id))
        {
            var exit = PrototypeMap.Neighbors(creature.Position)
                .Where(point =>
                    _map.IsPassable(point) &&
                    _map[point] != TileKind.Larder &&
                    !_zones[ZoneKind.Forbidden].Contains(point) &&
                    !_creatures.Any(other => other != creature && other.Position == point) &&
                    DistanceToLarder(point) > DistanceToLarder(creature.Position))
                .Order()
                .FirstOrDefault();
            if (exit != default)
            {
                Move(creature, exit);
            }
        }
    }

    private static int DistanceToLarder(GridPoint point)
    {
        return PrototypeMap.LarderTiles.Min(
            larder => Math.Abs(point.X - larder.X) + Math.Abs(point.Y - larder.Y));
    }

    private void MatchJobs()
    {
        var candidates = _creatures
            .Where(creature =>
                creature.CurrentJob is null &&
                !creature.IsMustering &&
                creature.Mode != CreatureMode.Eating &&
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
                if (ReservedMeals() > 0 &&
                    job.Kind is JobKind.Cook or JobKind.Haul)
                {
                    continue;
                }

                if (job.PersonalCreatureId is { } personal && personal != creature.Id)
                {
                    continue;
                }

                if (job.Kind == JobKind.Drill &&
                    creature.Satiety < _rules["drill_min_satiety"])
                {
                    continue;
                }

                var target = InitialTarget(creature, job);
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

            Assign(selected, competitors.FirstOrDefault());
            pairs.RemoveAll(pair =>
                pair.Creature == selected.Creature || pair.Job == selected.Job);
        }

        foreach (var creature in candidates.Where(creature => creature.CurrentJob is null))
        {
            RecordWaitingReason(creature);
        }
    }

    private void Assign(MatchPair selected, MatchPair? competitor)
    {
        var creature = selected.Creature;
        var job = selected.Job;
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
            },
            _ => new(),
        };
    }

    private void ActCreatures()
    {
        foreach (var creature in _creatures.OrderBy(creature => creature.Id))
        {
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
                if (!Move(creature, larder))
                {
                    RecordMovementBlocked(creature, larder);
                }

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
            if (!Move(creature, target))
            {
                RecordMovementBlocked(creature, target);
            }
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
            if (!Move(creature, target))
            {
                RecordMovementBlocked(creature, target);
            }

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
        if (creature.Position != job.Target)
        {
            creature.Mode = CreatureMode.Moving;
            if (!Move(creature, job.Target))
            {
                RecordDecision(
                    creature,
                    "waiting_blocked_by_other",
                    new Dictionary<string, int>
                    {
                        ["targetX"] = job.Target.X,
                        ["targetY"] = job.Target.Y,
                    },
                    job.Kind,
                    job.Target);
            }

            return;
        }

        creature.Mode = job.Kind switch
        {
            JobKind.Rest => CreatureMode.Resting,
            _ => CreatureMode.Working,
        };

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
            job.Target = NearestAvailableLarder(creature);
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
            creature.ActiveTicks++;
            if (creature.ActiveTicks % PrototypeTuning.WatchFatiguePeriod == 0)
            {
                creature.Fatigue = Math.Min(100, creature.Fatigue + 1);
            }

            return;
        }

        creature.ActiveTicks++;
        if (creature.ActiveTicks % PrototypeTuning.FatigueGainPeriod == 0)
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

    private void CompleteJob(CreatureState creature, JobState job)
    {
        switch (job.Kind)
        {
            case JobKind.Harvest:
                _beds[job.Origin].Growth = 0;
                AddLoose(job.Origin, ResourceKind.RawMushroom, PrototypeTuning.HarvestOutput);
                break;
            case JobKind.Haul:
                var free = PrototypeTuning.LarderCapacity - _stockRaw - _stockMeals;
                var delivered = Math.Min(free, creature.CarryAmount);
                if (creature.Carrying == ResourceKind.RawMushroom)
                {
                    _stockRaw += delivered;
                }
                else
                {
                    _stockMeals += delivered;
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
        creature.ActiveTicks = 0;
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
            creature.Carrying = null;
            creature.CarryAmount = 0;
        }

        job.ReservedBy = null;
        job.PickedUp = false;
        job.Target = job.Origin;
        job.RemainingTicks = 0;
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
        var blocked = PathBlocks(creature, includeCreatures: true);
        blocked.Remove(target);
        var next = _map.NextStep(creature.Position, target, blocked);
        if (next is null || next == creature.Position)
        {
            return next == creature.Position;
        }

        if (_creatures.Any(other => other != creature && other.Position == next))
        {
            return false;
        }

        creature.Position = next.Value;
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
            target: target);
    }

    private bool CanAdvanceMealQueue(CreatureState creature, out GridPoint target)
    {
        var reserved = _creatures
            .Where(candidate => candidate.MealReserved)
            .ToArray();
        var musterQueue = reserved.Any(candidate => candidate.IsMustering);
        var queue = musterQueue
            ? reserved.OrderBy(candidate => candidate.Id).ToArray()
            : reserved
                .OrderBy(candidate =>
                    PrototypeMap.LarderTiles.Contains(candidate.Position) ? 0 : 1)
                .ThenBy(candidate => candidate.Position)
                .ThenBy(candidate => candidate.Id)
                .ToArray();
        var index = Array.IndexOf(queue, creature);
        if (index is < 0 or >= 2)
        {
            target = default;
            return false;
        }

        var active = queue.Take(2).ToArray();
        var occupiedAssignment = active
            .Where(candidate => PrototypeMap.LarderTiles.Contains(candidate.Position))
            .ToDictionary(candidate => candidate.Id, candidate => candidate.Position);
        if (occupiedAssignment.TryGetValue(creature.Id, out target))
        {
            return true;
        }

        var claimed = occupiedAssignment.Values.ToHashSet();
        var remainingTargets = PrototypeMap.LarderTiles
            .Where(tile => !claimed.Contains(tile))
            .ToArray();
        var remainingCreatures = active
            .Where(candidate => !occupiedAssignment.ContainsKey(candidate.Id))
            .ToArray();
        if (remainingCreatures.Length == 1)
        {
            target = remainingTargets[0];
        }
        else
        {
            var directCost = QueueDistance(remainingCreatures[0], remainingTargets[0]) +
                QueueDistance(remainingCreatures[1], remainingTargets[1]);
            var swappedCost = QueueDistance(remainingCreatures[0], remainingTargets[1]) +
                QueueDistance(remainingCreatures[1], remainingTargets[0]);
            var swap = swappedCost < directCost;
            var remainingIndex = Array.IndexOf(remainingCreatures, creature);
            target = swap
                ? remainingTargets[1 - remainingIndex]
                : remainingTargets[remainingIndex];
        }

        return !_creatures.Any(other =>
            other != creature &&
            PrototypeMap.LarderTiles.Contains(other.Position) &&
            other.CurrentJob is not null);
    }

    private int QueueDistance(CreatureState creature, GridPoint target)
    {
        return _map.Distance(creature.Position, target, _zones[ZoneKind.Forbidden]) ??
            int.MaxValue / 4;
    }

    private HashSet<GridPoint> PathBlocks(CreatureState creature, bool includeCreatures)
    {
        var blocked = new HashSet<GridPoint>(_zones[ZoneKind.Forbidden]);
        if (includeCreatures)
        {
            foreach (var other in _creatures)
            {
                if (other != creature)
                {
                    blocked.Add(other.Position);
                }
            }
        }

        return blocked;
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

    private GridPoint InitialTarget(CreatureState creature, JobState job)
    {
        return job.Kind == JobKind.Cook
            ? NearestAvailableLarder(creature)
            : job.Origin;
    }

    private GridPoint NearestAvailableLarder(CreatureState creature)
    {
        var available = PrototypeMap.LarderTiles
            .Where(target =>
                !_creatures.Any(other => other != creature && other.Position == target))
            .ToArray();
        return NearestReachable(
            creature.Position,
            available.Length > 0 ? available : PrototypeMap.LarderTiles);
    }

    private GridPoint NearestReachable(GridPoint start, IEnumerable<GridPoint> targets)
    {
        return targets
            .Where(target => _zones[ZoneKind.Larder].Contains(target) || _map[target] != TileKind.Larder)
            .Select(target => new
            {
                Target = target,
                Distance = _map.Distance(start, target, _zones[ZoneKind.Forbidden]),
            })
            .Where(item => item.Distance is not null)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Target)
            .FirstOrDefault()?.Target ??
            throw new InvalidOperationException("No reachable target exists.");
    }

    private int WorkDuration(CreatureState creature, JobKind kind)
    {
        var baseDuration = kind switch
        {
            JobKind.Harvest => PrototypeTuning.HarvestTicks,
            JobKind.Haul => PrototypeTuning.HaulTransferTicks,
            JobKind.Cook => PrototypeTuning.CookTicks,
            JobKind.Drill => PrototypeTuning.DrillTicks,
            JobKind.Rest or JobKind.Watch => int.MaxValue,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (baseDuration == int.MaxValue)
        {
            return baseDuration;
        }

        var exhausted = creature.Fatigue > PrototypeTuning.RestThreshold &&
            (_priorities[JobKind.Rest] == 0 ||
             !_jobs.Any(job => job.Kind == JobKind.Rest && job.ReservedBy is null));
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
            JobKind.Harvest when _beds.Values.Count(bed => bed.IsRipe) >=
                PrototypeTuning.RipeBacklogThreshold =>
                PrototypeTuning.UrgencyRipeBacklog,
            _ => 0,
        };
    }

    private int DiagnosticUrgency(JobKind kind)
    {
        return Urgency(kind, kind == JobKind.Haul ? ResourceKind.Meal : null);
    }

    private bool AnyReachableTarget(CreatureState creature, JobKind kind)
    {
        var targets = kind switch
        {
            JobKind.Harvest => PrototypeMap.BedTiles,
            JobKind.Haul or JobKind.Cook => PrototypeMap.LarderTiles,
            JobKind.Rest => PrototypeMap.BunkTiles,
            JobKind.Drill => PrototypeMap.PostTiles,
            JobKind.Watch => [.. _zones[ZoneKind.Watch]],
            _ => [],
        };
        return targets.Any(target =>
            _map.Distance(creature.Position, target, _zones[ZoneKind.Forbidden]) is not null);
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
            DetailsEqual(previous.Details, details))
        {
            previous.LastTick = CurrentTick;
            previous.Repeats++;
            return;
        }

        _events.Add(new EventState(CurrentTick, creature.Id, reason, details));
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
            creature.Injury,
            creature.Position,
            creature.Mode,
            creature.CurrentJob?.Id,
            creature.LastDecision,
            ComputeReadiness(creature),
            creature.ReadinessAtRaid);
    }

    private static PrototypeEvent ToSnapshot(EventState @event)
    {
        return new PrototypeEvent(
            @event.FirstTick,
            @event.LastTick,
            @event.CreatureId,
            @event.ReasonCode,
            @event.Details,
            @event.Repeats);
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

    private static void ValidateCommandOrder(IReadOnlyList<PrototypeCommand> commands)
    {
        var previous = -1;
        foreach (var command in commands)
        {
            if (command.Tick < previous)
            {
                throw new ArgumentException(
                    "Commands must be ordered by non-decreasing tick.",
                    nameof(commands));
            }

            previous = command.Tick;
        }
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
        public InjuryKind Injury { get; set; }
        public CreatureMode Mode { get; set; }
        public JobState? CurrentJob { get; set; }
        public PrototypeDecision LastDecision { get; set; } = null!;
        public int? ReadinessAtRaid { get; set; }
        public ResourceKind? Carrying { get; set; }
        public int CarryAmount { get; set; }
        public int ActiveTicks { get; set; }
        public bool MealReserved { get; set; }
        public GridPoint? SpecialTarget { get; set; }
        public int SpecialTicks { get; set; }
        public bool IsMustering { get; set; }
        public bool MusterNeedsRation { get; set; }
        public GridPoint? MusterTarget { get; set; }

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
    }

    private sealed class BedState(GridPoint position, int growth)
    {
        public GridPoint Position { get; } = position;
        public int Growth { get; set; } = growth;
        public bool IsRipe => Growth >= PrototypeTuning.BedGrowthTicks;
    }

    private sealed class EventState(
        int tick,
        int creatureId,
        string reasonCode,
        Dictionary<string, int> details)
    {
        public int FirstTick { get; } = tick;
        public int LastTick { get; set; } = tick;
        public int CreatureId { get; } = creatureId;
        public string ReasonCode { get; } = reasonCode;
        public IReadOnlyDictionary<string, int> Details { get; } =
            details.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        public int Repeats { get; set; } = 1;
    }

    private sealed record MatchPair(
        CreatureState Creature,
        JobState Job,
        GridPoint InitialTarget,
        int Score,
        int Urgency,
        int Affinity,
        int Distance);
}
