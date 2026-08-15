using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// The canonical snapshot is an invariant by
/// <see href="../../docs/decisions/0010-contract-invariants-and-tuning.md">ADR 0010</see>,
/// but until this file existed nothing noticed when its composition moved: the
/// only assertion about the version compared <c>PrototypeCanonical.SchemaVersion</c>
/// with the number that same constant had just written, so it held for every
/// possible value of it.
///
/// What is pinned here is the **set of fields the snapshot carries**, section by
/// section, next to the schema version the set was read from. Neither can move
/// without this file moving with it, which is what turns the versioning rule of
/// <see href="../../docs/engineering/PROTOTYPE_HEADLESS.md">PROTOTYPE_HEADLESS.md</see>
/// from a paragraph into a check. The rule in one line: an addition keeps the
/// version, a rename, a removal or a change of meaning raises it and needs an
/// ADR.
///
/// Types and values are deliberately not pinned. The checksum and the scenario
/// tests already hold those, and a shape that carried types would move with the
/// state of the run instead of with a decision.
/// </summary>
public sealed class PrototypeSnapshotShapeTests
{
    /// <summary>
    /// The version the inventory below was read from, written out as a literal
    /// on purpose: comparing the constant with itself is exactly what made the
    /// previous check unable to fail.
    /// </summary>
    private const int ShapeRecordedForSchemaVersion = 3;

    /// <summary>
    /// The one field of the snapshot whose presence is conditional: a party that
    /// has not ended carries no score rather than a null one (ADR 0016, contract
    /// 12.1). A second conditional field is a decision that has to be argued in
    /// the same places, and this test refuses to let one appear quietly.
    /// </summary>
    private const string ConditionalPath = "$.sessionResult";

    private const string ConditionalProperty = "score";

    /// <summary>
    /// Objects whose property names are data rather than fields: zone kinds, job
    /// kinds, rule ids and the numeric arguments of a reason code. All four
    /// vocabularies are tuning by ADR 0010 and move without changing the shape of
    /// the snapshot, so the walk stops at them.
    /// </summary>
    private static readonly IReadOnlySet<string> OpenMaps =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "$.zones",
            "$.priorities",
            "$.rules",
            "$.creatures[].affinities",
            "$.creatures[].lastDecision.details",
            "$.events[].details",
        };

    /// <summary>
    /// Runs chosen so that every array of the snapshot is non-empty in at least
    /// one of them and both states of a party are present. Coverage is asserted
    /// rather than assumed: a section no sample reaches any more fails the test
    /// instead of quietly dropping out of the inventory.
    /// </summary>
    /// <summary>
    /// How a sample decides where to stop. Two of the three states the snapshot
    /// can only be photographed in are emergent — the tick a wave ends on and the
    /// tick a wounded creature is first asked whether it will stand — so they are
    /// named by the state and never by a tick number: a balance change would move
    /// the number and the section would go silently unrecorded.
    /// </summary>
    private enum StopAt
    {
        Tick,
        MomentOfTruth,
        WoundIntent,
    }

    private static readonly (string Name, string Fixture, int Ticks, StopAt Stop)[] Samples =
    [
        // Nothing applied yet, so the whole log is still pending.
        ("build-demo @ 0", "build-demo", 0, StopAt.Tick),
        // Five dig designations, three of them reserved with a work tile.
        ("dig-demo @ 5", "dig-demo", 5, StopAt.Tick),
        // Two stockpile cells and loose stone still waiting for a carrier.
        ("stone-haul-demo @ 210", "stone-haul-demo", 210, StopAt.Tick),
        // A blueprint that nothing has been delivered to yet.
        ("build-demo @ 1001", "build-demo", 1001, StopAt.Tick),
        // The first wave has landed, so there are raiders inside the domain.
        ("prepared @ first raid + 5", "prepared", PrototypeTuning.FirstRaidTick + 5, StopAt.Tick),
        // A party that ended: the only state in which the score exists.
        ("neglected @ session end", "neglected", PrototypeTuning.SessionTicks, StopAt.Tick),
        // Far enough past the first wave that somebody has broken or been put
        // down, which is the only way a creature carries a remembered place
        // (Issue #117). Without it the array is present but never populated, and
        // the composition of its elements would go unrecorded.
        ("baseline @ after the first wave", "baseline", PrototypeTuning.FirstRaidTick + 200, StopAt.Tick),
        // The party standing still between two waves, which is the only state in
        // which the cards of the moment of truth exist (Issue #312). It is
        // defined by the state it stops in and not by a tick number, because the
        // tick a wave ends on is emergent and a balance change would move it.
        ("baseline @ the moment of truth", "baseline", 0, StopAt.MomentOfTruth),
        // A whole party of baseline, which is the only sample that reaches the
        // returning raider (Issue #358): `survivors` is empty until somebody walks
        // out of the gate alive, and `raiders[].rememberedPlace` stays null until
        // one of them walks back in carrying a scar. `neglected @ session end`
        // does not reach it — that domain falls before the first wave is resolved.
        ("baseline @ session end", "baseline", PrototypeTuning.SessionTicks, StopAt.Tick),
        // The first roll call at which somebody carrying a wound is asked whether
        // it will stand (Issue #431), which is the only state in which
        // `creatures[].woundIntent` is anything but null. Named by the state for
        // the same reason as the moment of truth above: the tick is emergent —
        // nobody carries a wound until a wave has been fought and the domain has
        // picked its people up off the floor. `prepared` and not `baseline`,
        // because on `baseline`'s own seed nobody carrying a wound survives to a
        // roll call at all — measured, not assumed: the contest is first reached
        // at t1651 on prepared/20260726 and never on baseline/20260726.
        ("prepared @ the contest of the wounded", "prepared", 0, StopAt.WoundIntent),
    ];

    /// <summary>
    /// The recorded composition of schema version 3. Every line is
    /// <c>path -&gt; fields</c>; <c>[]</c> stands for the elements of an array,
    /// and all elements of one array are required to carry the same fields.
    ///
    /// Updating this list is not a chore that trails a change — it is where the
    /// change declares which kind it is. A new line, or a new field on a line,
    /// is additive and keeps the version. A removed or renamed one is breaking:
    /// it raises <c>PrototypeCanonical.SchemaVersion</c> and needs an ADR and a
    /// contract update in the same change set.
    /// </summary>
    private static readonly string[] RecordedShape =
    [
        "$ -> beds, buildSites, commandsApplied, creatures, digDesignations, domain, economy, events, jobs, labor, looseItems, map, materialStockpile, momentOfTruth, nextJobId, pendingCommands, priorities, raiders, rooms, rules, schemaVersion, seed, sessionResult, stations, stocks, survivors, threat, tick, waves, zones",
        "$.beds[] -> growthProgress, position, ripe",
        "$.buildSites[] -> delivered, incomingReserved, jobId, progressTicks, reachable, required, requiredTicks, reservedBy, statusCode, tile",
        // Issue #409, additive twice over: `stepsLostToLimp` is what a hurt leg
        // has taken away and `actionsLostToStun` what a hurt head has, both in the
        // family of `moveCount` and `blockedTicks` — counters nothing in the
        // simulation reads, published so that a consequence can be measured as a
        // rate instead of inferred.
        // Issue #431. Additive: `woundIntent` is what a wounded creature decided
        // at the roll call, and null — like `raiders[].rememberedPlace` — until a
        // wave asks somebody who is carrying a wound.
        "$.creatures[] -> actionsLostToStun, affinities, blockedTicks, carryAmount, carrying, currentJobId, fatigue, grit, hp, id, injuries, injury, isMustering, lastDecision, lastMoveTick, lastYieldTick, loyalty, martialForm, maxHp, mealReserved, mealTarget, mealTicksRemaining, might, mode, moveCount, musterNeedsRation, musterTarget, name, position, readiness, readinessAtRaid, recoveryTicks, rememberedPlaces, satiety, stepsLostToLimp, watchTicks, workTicks, woundIntent, yieldCount",
        // Issue #409. Additive: a new array beside `injury`, which stays and is
        // its worst entry.
        "$.creatures[].injuries[] -> part, severity",
        "$.creatures[].lastDecision -> details, jobKind, reasonCode, target, tick",
        // Issue #431. Additive: `fearOfTheDomain` is the part of `fear` that is
        // about the player rather than about the fight, carried beside the three
        // totals because it is a magnitude with a fade of its own and not a term
        // of any of the three ledgers.
        "$.creatures[].loyalty -> benefit, benefitTerms, fear, fearOfTheDomain, fearTerms, grudge, grudgeReleased, grudgeTerms",
        "$.creatures[].loyalty.benefitTerms[] -> amount, code",
        "$.creatures[].loyalty.fearTerms[] -> amount, code",
        "$.creatures[].loyalty.grudgeTerms[] -> amount, code",
        "$.creatures[].rememberedPlaces[] -> cause, place, tick",
        "$.creatures[].woundIntent -> code, part, press, severity, spare, tick, verdictDecided, wave",
        "$.digDesignations[] -> jobId, progressTicks, reachable, requiredTicks, reservedBy, statusCode, tile, workTile",
        "$.domain -> downedCreatures, injuredCreatures, livingCreatures, peakMeals, renown, renownAtPreviousWave, strength, strengthAtPreviousWave, waveCount, wavesArrived, wavesResolved",
        "$.economy -> buildsCompleted, cookBatchesCompleted, digsCompleted, harvestsCompleted, mealHaulsCompleted, mealsEaten, mealsProduced, rawHaulsCompleted, stoneConsumed, stoneDelivered, stoneHaulsCompleted, stoneProduced, stoneSpilled, stoneStored",
        "$.events[] -> creatureId, details, firstTick, jobKind, lastTick, reasonCode, repeats, target",
        "$.jobs[] -> jobId, key, kind, origin, personalCreatureId, pickedUp, progressTicks, quantity, remainingTicks, reservedBy, resource, sourceCell, storeCell, storeReserved, target",
        "$.labor -> buildTicks, digTicks, drillTicks, eatTicks, foodWorkPercent, foodWorkTicks, idleTicks, musterTicks, postCapacityTicks, postOccupancyPercent, postOccupiedTicks, restTicks, stoneHaulTicks, totalCreatureTicks, watchTicks",
        "$.looseItems[] -> position, quantity, resource",
        "$.map -> buildFloorTiles, builtPostTiles, diggableTiles, excavatedTiles, rockTiles, stockpileFloorTiles",
        "$.materialStockpile[] -> capacity, incomingReserved, position, reachable, statusCode, stored",
        "$.momentOfTruth -> cards, open, openedTick, waitedSteps, waveNumber, windowSteps",
        "$.momentOfTruth.cards[] -> benefitThisWave, creatureId, dominantAxis, fearThisWave, grudgeThisWave, name, notability, raidersDowned, verdict",
        "$.pendingCommands[] -> creatureId, jobKind, kind, ruleId, tick, tiles, value, verdict, zoneKind",
        "$.raiders[] -> carryingMeals, hp, id, might, mode, name, position, rememberedPlace, returnedFromWave, returningToGate, scar, stealTicks, wave",
        "$.raiders[].rememberedPlace -> cause, place, tick",
        "$.rooms[] -> complete, contents, id, perimeter, purpose, statusCode",
        "$.rooms[].contents[] -> kind, position",
        "$.sessionResult -> defendersDowned, defendersFled, endTick, lastWaveOutcome, mealsLeft, mealsStolen, outcome, raidersDowned, renown, score, strength, unresolved, waveCount, wavesRepelled, wavesResolved",
        "$.stations[] -> kind, occupiedBy, occupiedTicks, position",
        "$.stocks -> capacity, carriedStone, looseMeals, looseRawMushroom, looseStone, meals, mealsEaten, mealsProduced, rawMushroom, reservedStone, siteStone, stockpileCapacity, storedStone",
        "$.survivors[] -> escapedTick, escapedWave, name, rememberedPlace, returnWave, returnedAsRaiderId, scar, status",
        "$.survivors[].rememberedPlace -> cause, place, tick",
        "$.threat -> active, announceTick, announced, arriveTick, raiderCount, raiderMight, ticksRemaining, waveCount, waveNumber",
        "$.waves[] -> announceTick, announced, arriveTick, arrived, defendersDowned, defendersFled, endTick, mealsStolen, number, outcome, raiderCount, raiderMight, raidersDowned, renownAtAnnounce",
    ];

    [Fact]
    public void The_canonical_snapshot_carries_exactly_the_recorded_composition()
    {
        Assert.Equal(ShapeRecordedForSchemaVersion, PrototypeCanonical.SchemaVersion);

        var problems = new List<string>();
        var observed = new List<(string Name, bool PartyEnded, SnapshotShape Shape)>();

        foreach (var (name, fixture, ticks, stop) in Samples)
        {
            var run = stop switch
            {
                StopAt.MomentOfTruth => RunToMomentOfTruth(fixture),
                StopAt.WoundIntent => RunToTheContestOfTheWounded(fixture),
                _ => PrototypeScenario.Run(LoadFixture(fixture), ticks),
            };
            using var document = JsonDocument.Parse(run.CanonicalJson);
            var root = document.RootElement;

            Assert.Equal(
                PrototypeCanonical.SchemaVersion,
                root.GetProperty("schemaVersion").GetInt32());

            var partyEnded = root
                .GetProperty("sessionResult")
                .GetProperty("outcome")
                .ValueKind != JsonValueKind.Null;

            var shape = new SnapshotShape();
            Collect(root, "$", shape, problems, name);
            observed.Add((name, partyEnded, shape));
        }

        var union = new SnapshotShape();
        foreach (var (_, _, shape) in observed)
        {
            foreach (var (path, fields) in shape)
            {
                if (union.TryGetValue(path, out var known))
                {
                    known.UnionWith(fields);
                }
                else
                {
                    union[path] = new SortedSet<string>(fields, StringComparer.Ordinal);
                }
            }
        }

        // The union is the shape of a party that ended. Every sample has to
        // match it exactly, minus the one field the decision allows to be
        // conditional — otherwise a second conditional field would hide inside
        // the union and never be argued anywhere.
        foreach (var (name, partyEnded, shape) in observed)
        {
            foreach (var (path, fields) in shape)
            {
                var expected = union[path];
                if (path == ConditionalPath && !partyEnded)
                {
                    expected = new SortedSet<string>(expected, StringComparer.Ordinal);
                    expected.Remove(ConditionalProperty);
                }

                if (!fields.SetEquals(expected))
                {
                    problems.Add(
                        $"{name}: `{path}` carries [{string.Join(", ", fields)}] here and " +
                        $"[{string.Join(", ", expected)}] in another run. Only " +
                        $"`{ConditionalPath}.{ConditionalProperty}` may come and go, and only " +
                        "with the end of the party (ADR 0016).");
                }
            }

            if (shape.TryGetValue(ConditionalPath, out var sessionResult))
            {
                var carriesScore = sessionResult.Contains(ConditionalProperty);
                if (carriesScore != partyEnded)
                {
                    problems.Add(
                        $"{name}: `{ConditionalPath}.{ConditionalProperty}` is " +
                        $"{(carriesScore ? "present" : "absent")} for a party that " +
                        $"{(partyEnded ? "ended" : "is still being played")}. A party has a " +
                        "score once it has ended and never before, and an unscored party " +
                        "carries no field rather than a null one (ADR 0016).");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        var actual = Render(union);
        Assert.True(RecordedShape.SequenceEqual(actual), Explain(actual));
    }

    /// <summary>
    /// The party played until it stops by itself and waits for a verdict. The
    /// stop is the sample: the tick a wave ends on is emergent, so a number
    /// would be a balance value pretending to be a fixture.
    /// </summary>
    private static PrototypeRunResult RunToMomentOfTruth(string fixture)
    {
        var world = new PrototypeWorld(LoadFixture(fixture));
        while (!world.IsComplete && !world.IsAwaitingVerdict)
        {
            world.Step();
        }

        Assert.True(
            world.IsAwaitingVerdict,
            $"{fixture} played a whole party without ever stopping between two waves, so the " +
            "cards of the moment of truth were never recorded by any sample.");
        return PrototypeScenario.Capture(world);
    }

    /// <summary>
    /// The first tick on which some creature carries a decision about its own
    /// wound (Issue #431). Photographed on the tick the contest wrote it, because
    /// the intent is cleared again when the wave it was about resolves.
    /// </summary>
    private static PrototypeRunResult RunToTheContestOfTheWounded(string fixture)
    {
        var world = new PrototypeWorld(LoadFixture(fixture));
        while (!world.IsComplete &&
               !world.GetSnapshot().Creatures.Any(creature => creature.WoundIntent is not null))
        {
            world.Step();
        }

        var capture = PrototypeScenario.Capture(world);
        Assert.True(
            capture.State.Creatures.Any(creature => creature.WoundIntent is not null),
            $"{fixture} played a whole party without one wounded creature ever being asked " +
            "whether it would stand, so the composition of `woundIntent` was never recorded " +
            "by any sample.");
        return capture;
    }

    /// <summary>
    /// The failure has to say what to do next, because the answer is not
    /// "re-record the shape" — it is "declare which kind of change this is".
    /// The pasteable block is there so that the declaration costs a paste rather
    /// than an hour, and so that the diff of this file shows a reviewer exactly
    /// which fields moved.
    /// </summary>
    private static string Explain(string[] actual)
    {
        var added = actual.Except(RecordedShape, StringComparer.Ordinal).ToArray();
        var gone = RecordedShape.Except(actual, StringComparer.Ordinal).ToArray();
        var lines = new List<string>
        {
            "The canonical snapshot no longer carries the composition recorded for schema " +
            $"version {ShapeRecordedForSchemaVersion}.",
            string.Empty,
        };

        if (gone.Length > 0)
        {
            lines.Add("Recorded, no longer produced:");
            lines.AddRange(gone.Select(line => "  - " + line));
            lines.Add(
                "  (a whole section listed here may also mean no sample reaches it any " +
                "more — check Samples before concluding the field is gone.)");
        }

        if (added.Length > 0)
        {
            lines.Add("Produced, not recorded:");
            lines.AddRange(added.Select(line => "  + " + line));
        }

        lines.AddRange(
        [
            string.Empty,
            "Decide which kind of change this is, per the versioning rule in",
            "docs/engineering/PROTOTYPE_HEADLESS.md:",
            "  - additive (a new section, or a new field on an existing one, with nothing",
            "    renamed, removed or re-meant): keep PrototypeCanonical.SchemaVersion and",
            "    record the new shape below;",
            "  - breaking (a field removed, renamed, retyped or given a new meaning under",
            "    the same name): raise SchemaVersion, write an ADR, update the contract and",
            "    ShapeRecordedForSchemaVersion here, and re-measure the goldens and evidence",
            "    that the moved checksum invalidates.",
            string.Empty,
            "Observed composition, ready to paste into RecordedShape:",
            string.Empty,
        ]);
        lines.AddRange(actual.Select(line => $"        \"{line}\","));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The arguments of a reason code are the one part of the snapshot the
    /// inventory above deliberately does not walk into: <c>$.events[].details</c>
    /// is an open map, because the vocabulary of reason codes and what each of
    /// them carries is tuning by ADR 0010 rather than a field of the schema.
    ///
    /// That has a consequence worth writing down rather than rediscovering.
    /// Issue #101 rewrote the arguments of <c>combat_fled_morale</c>, the shape
    /// above did not move, and so the versioning rule of
    /// <c>docs/engineering/PROTOTYPE_HEADLESS.md</c> had to be applied by hand
    /// rather than by a red test. It is applied here, and the first draft of it
    /// got the answer right for the wrong reason, which is worth keeping:
    ///
    /// - <c>raidersNear</c> and <c>hpPercent</c> are new arguments. Additive.
    /// - <c>downedAllies</c> was **not** additive and was not left alone. The old
    ///   name counted every ally the domain had lost, anywhere, 0..8; the new
    ///   quantity counts the ones this creature can see, 0..2. That is the
    ///   textbook breaking change of the rule — "a field kept its name and began
    ///   answering a different question" — so the key was renamed to
    ///   <c>downedAlliesNear</c>. The old name now means nothing rather than
    ///   something else, which is the whole point of the rule.
    ///
    /// <c>PrototypeCanonical.SchemaVersion</c> stays at 3 regardless, and not by
    /// exception: <c>$.events[].details</c> is not a field of the schema at all,
    /// and the composition of a reason code is tuning by ADR 0010. The rename is
    /// what makes that argument honest instead of merely convenient.
    ///
    /// This test is what stops the change from being invisible: it pins the
    /// arguments of the one reason code the change touched, so removing or
    /// renaming one of them is a red test and a decision rather than a silent
    /// edit. It does not pin the vocabulary at large — that is still tuning.
    /// </summary>
    [Fact]
    public void The_reason_code_for_a_broken_defender_carries_the_arguments_recorded_for_it()
    {
        var run = PrototypeScenario.Run(LoadFixture("baseline"), PrototypeTuning.SessionTicks);
        using var document = JsonDocument.Parse(run.CanonicalEventLog);

        var flights = document.RootElement
            .GetProperty("events")
            .EnumerateArray()
            .Where(@event => @event.GetProperty("reasonCode").GetString() == "combat_fled_morale")
            .ToArray();

        Assert.NotEmpty(flights);
        Assert.All(flights, flight => Assert.Equal(
            ["downedAlliesNear", "hpPercent", "raidersNear"],
            flight.GetProperty("details")
                .EnumerateObject()
                .Select(argument => argument.Name)
                .Order(StringComparer.Ordinal)
                .ToArray()));
        Assert.Equal(ShapeRecordedForSchemaVersion, PrototypeCanonical.SchemaVersion);
    }

    /// <summary>
    /// The event log is the second canonical document and carries the same
    /// version. It is checked here so that a bump cannot leave it behind: state
    /// and event log are read together or not at all.
    /// </summary>
    [Fact]
    public void The_canonical_event_log_is_the_version_and_nothing_but_the_events()
    {
        var run = PrototypeScenario.Run(
            LoadFixture("prepared"),
            PrototypeTuning.FirstRaidTick + 5);
        using var document = JsonDocument.Parse(run.CanonicalEventLog);
        var root = document.RootElement;

        Assert.Equal(
            ["schemaVersion", "events"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(
            PrototypeCanonical.SchemaVersion,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.NotEmpty(root.GetProperty("events").EnumerateArray());
    }

    private sealed class SnapshotShape : SortedDictionary<string, SortedSet<string>>
    {
        public SnapshotShape()
            : base(StringComparer.Ordinal)
        {
        }
    }

    private static string[] Render(SnapshotShape shape) =>
        shape
            .Select(entry => $"{entry.Key} -> {string.Join(", ", entry.Value)}")
            .ToArray();

    private static void Collect(
        JsonElement element,
        string path,
        SnapshotShape shape,
        List<string> problems,
        string sample)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (OpenMaps.Contains(path))
                {
                    return;
                }

                var fields = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    fields.Add(property.Name);
                    Collect(property.Value, $"{path}.{property.Name}", shape, problems, sample);
                }

                if (shape.TryGetValue(path, out var recorded))
                {
                    if (!recorded.SetEquals(fields))
                    {
                        problems.Add(
                            $"{sample}: two elements of `{path}` carry different fields — " +
                            $"[{string.Join(", ", recorded)}] against [{string.Join(", ", fields)}].");
                        recorded.UnionWith(fields);
                    }
                }
                else
                {
                    shape[path] = fields;
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, path + "[]", shape, problems, sample);
                }

                break;
        }
    }

    private static PrototypeCommandLog LoadFixture(string name) =>
        PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(), "scenarios", "prototype1", $"{name}.commands.v2.json"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DungeonFortress.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
