namespace DungeonFortress.Simulation;

// Which creature gets which job, and the record of why — the chosen
// pair, the counterfactual probe and the waiting reasons.
public sealed partial class PrototypeWorld
{
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
        foreach (var creature in candidates)
        {
            creature.AvoidedThisTick = null;
        }

        var pairs = CollectPairs(candidates, jobs, applyMemory: true);
        RecordMemoryProbe(candidates, jobs);

        // The refusal goes into the canonical log before anything is assigned, so
        // that a creature which then takes other work still tells the domain what
        // it would not do. `lastDecision` is overwritten a moment later by the job
        // it did take, which is right: the panel answers "what is it doing", and
        // the event feed answers "what happened".
        foreach (var creature in candidates
                     .Where(item => item.AvoidedThisTick is not null)
                     .OrderBy(item => item.Id))
        {
            var avoided = creature.AvoidedThisTick!.Value;
            RecordDecision(
                creature,
                AvoidanceReason(avoided.Place),
                new Dictionary<string, int>
                {
                    ["placeX"] = avoided.Place.Place.X,
                    ["placeY"] = avoided.Place.Place.Y,
                    ["sinceTick"] = avoided.Place.Tick,
                },
                avoided.Kind,
                avoided.Target);
        }

        ResolveMatching(pairs, apply: true);

        foreach (var creature in candidates.Where(creature => creature.CurrentJob is null))
        {
            RecordWaitingReason(creature);
        }
    }

    /// <summary>
    /// Every (creature, job) pair this tick that the creature could actually
    /// take, scored. One copy of the conditions, called twice: once for the
    /// matching the party runs on, and once — only under
    /// <see cref="TrackMemoryFreeMatching"/> — with
    /// <paramref name="applyMemory"/> off, so that the counterfactual Issue #125
    /// states its criterion in is measured against the same rules rather than
    /// against a second implementation of them.
    /// </summary>
    private List<MatchPair> CollectPairs(
        IReadOnlyList<CreatureState> candidates,
        IReadOnlyList<JobState> jobs,
        bool applyMemory)
    {
        var pairs = new List<MatchPair>();
        foreach (var creature in candidates)
        {
            var bestTaken = int.MinValue;
            (JobKind Kind, GridPoint Target, PrototypeRememberedPlace Place, long JobId, int Score)? refused = null;
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
                if (score < PrototypeTuning.ScoreFloor)
                {
                    continue;
                }

                // Where a creature broke or was put down, it will not start work
                // again (Issue #117). Lying down is deliberately exempt: a wound
                // closes in a bunk and nowhere else, so a creature that refused
                // the bunk it was carried to would be refusing to heal, which is
                // not a decision about work at all.
                //
                // The arm is **last**, after every other condition on the pair and
                // after the score (Issue #125). Standing first it refused work the
                // creature was never going to take — unreachable, occupied, below
                // the floor — and the refusal named that work to the player, so
                // "memory changed what this one did" was said about a job memory
                // never touched. Three refusals in five over the matrix were of
                // that kind; the count is in evidence/125-false-refusals.json.
                //
                // Among the pairs memory does take away, the one named is the one
                // with the highest score, ties going to the lower job id — the
                // same order ResolveMatching picks a winner in, so the refusal
                // names the work this creature would have put first rather than
                // whichever job happened to be oldest in the list.
                if (applyMemory &&
                    job.Kind != JobKind.Rest &&
                    AvoidedPlace(creature, target) is { } avoided)
                {
                    if (refused is not { } held || score > held.Score)
                    {
                        refused = (job.Kind, target, avoided, job.Id, score);
                    }

                    continue;
                }

                bestTaken = Math.Max(bestTaken, score);
                pairs.Add(new MatchPair(creature, job, target, score, urgency, affinity, distance.Value));
            }

            // And the refusal is only recorded when memory actually changed what
            // this creature put first (Issue #125). A creature whose best work is
            // untouched by memory takes that work either way: saying it "will not
            // take cooking at (15,7)" while it walks off to the harvest it was
            // always going to do names a change that did not happen. Twenty of the
            // twenty refusals on prepared/20260726 were of exactly that kind.
            //
            // Strictly greater, not greater-or-equal: on a tie the matching would
            // have preferred the lower job id, and a creature that ends up doing
            // work of equal worth has not been changed by the memory either.
            if (refused is { } best && best.Score > bestTaken)
            {
                creature.AvoidedThisTick = best;
            }
        }

        return pairs;
    }

    /// <summary>
    /// The global matching of 8.2, run over a set of scored pairs. With
    /// <paramref name="apply"/> it is the party's own matching and books
    /// everything it decides; without it, nothing is written and the plan is
    /// only returned, which is what makes the counterfactual measurable without
    /// a second copy of the algorithm.
    /// </summary>
    private Dictionary<int, MatchPair> ResolveMatching(List<MatchPair> pairs, bool apply)
    {
        var chosen = new Dictionary<int, MatchPair>();
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

            if (!(apply ? Assign(selected, competitors.FirstOrDefault()) : CanAssign(selected)))
            {
                // Capacity is a property of the job, not of the volunteer: if the
                // booking failed once it fails for everyone this tick.
                pairs.RemoveAll(pair => pair.Job == selected.Job);
                continue;
            }

            chosen[selected.Creature.Id] = selected;
            pairs.RemoveAll(pair =>
                pair.Creature == selected.Creature ||
                pair.Job == selected.Job ||
                (ActiveLarderTiles().Contains(selected.InitialTarget) &&
                 pair.InitialTarget == selected.InitialTarget));
        }

        return chosen;
    }

    /// <summary>
    /// The one way <see cref="Assign"/> can refuse a pair it was handed, asked
    /// without booking anything. Stone is the only kind of work whose
    /// destination can vanish between being scored and being taken.
    /// </summary>
    private bool CanAssign(MatchPair selected) =>
        selected.Job.Kind != JobKind.Haul ||
        selected.Job.Resource != ResourceKind.Stone ||
        (TryPlanStoneDestination(
            selected.Job,
            selected.Job.Origin,
            selected.Job.Quantity,
            out _,
            out var amount) && amount > 0);

    /// <summary>
    /// The counterfactual of Issue #125, resolved inside the tick it belongs to:
    /// what each creature would have been assigned had memory of place not
    /// existed. Off unless <see cref="TrackMemoryFreeMatching"/> asks for it.
    /// </summary>
    private void RecordMemoryProbe(IReadOnlyList<CreatureState> candidates, IReadOnlyList<JobState> jobs)
    {
        if (!TrackMemoryFreeMatching)
        {
            _memoryProbe = [];
            return;
        }

        var memoryFreePairs = CollectPairs(candidates, jobs, applyMemory: false);
        var best = memoryFreePairs
            .GroupBy(pair => pair.Creature.Id)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(pair => pair.Score)
                    .ThenBy(pair => pair.Job.Id)
                    .First());
        var free = ResolveMatching(memoryFreePairs, apply: false);
        _memoryProbe = candidates
            .Select(creature =>
            {
                var refused = creature.AvoidedThisTick;
                var choice = free.GetValueOrDefault(creature.Id);
                var jobWinner = refused is { } taken
                    ? free.Values.FirstOrDefault(pair => pair.Job.Id == taken.JobId)?.Creature.Id
                    : null;
                var tileWinner = refused is { } tile
                    ? free.Values.FirstOrDefault(pair => pair.InitialTarget == tile.Target)?.Creature.Id
                    : null;
                return new MemoryProbe(
                    creature.Id,
                    refused?.JobId,
                    refused?.Kind,
                    refused?.Target,
                    best.GetValueOrDefault(creature.Id)?.Job.Id,
                    choice?.Job.Id,
                    choice?.Job.Kind,
                    choice?.InitialTarget,
                    jobWinner,
                    tileWinner);
            })
            .Where(probe => probe.RefusedJobId is not null || probe.MemoryFreeJobId is not null)
            .ToArray();
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
        // A creature standing idle because it will not go back to where it broke
        // says so, instead of reporting the next-best diagnostic about a job it
        // was never going to take. This is the branch the player is looking at
        // when they ask why somebody is doing nothing.
        //
        // The decision is **not written again here**. It was written for this
        // creature, with these exact arguments, by the loop above the matching in
        // <see cref="MatchJobs"/>, and it is already this creature's
        // <c>lastDecision</c>. Writing it a second time did not create a second
        // event — <see cref="RecordDecision"/> folds an identical repeat — it
        // incremented <c>repeats</c> on the first one, so a refusal that happened
        // once was published as having happened twice, on its very first tick.
        //
        // That is a canonical counter, not a display detail: the feed printed
        // "(x2)", `ReasonCodeOccurrences` sums `Repeats`, and every count of this
        // code quoted anywhere was inflated by it. The rule the fix restores is
        // the one the counter is named for: <c>repeats</c> counts **ticks on
        // which the decision was taken**, not calls that recorded it.
        if (creature.AvoidedThisTick is not null)
        {
            creature.Mode = CreatureMode.Waiting;
            return;
        }

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
}
