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
    private static readonly (string Name, string Fixture, int Ticks)[] Samples =
    [
        // Nothing applied yet, so the whole log is still pending.
        ("build-demo @ 0", "build-demo", 0),
        // Five dig designations, three of them reserved with a work tile.
        ("dig-demo @ 5", "dig-demo", 5),
        // Two stockpile cells and loose stone still waiting for a carrier.
        ("stone-haul-demo @ 210", "stone-haul-demo", 210),
        // A blueprint that nothing has been delivered to yet.
        ("build-demo @ 1001", "build-demo", 1001),
        // The first wave has landed, so there are raiders inside the domain.
        ("prepared @ first raid + 5", "prepared", PrototypeTuning.FirstRaidTick + 5),
        // A party that ended: the only state in which the score exists.
        ("neglected @ session end", "neglected", PrototypeTuning.SessionTicks),
        // Far enough past the first wave that somebody has broken or been put
        // down, which is the only way a creature carries a remembered place
        // (Issue #117). Without it the array is present but never populated, and
        // the composition of its elements would go unrecorded.
        ("baseline @ after the first wave", "baseline", PrototypeTuning.FirstRaidTick + 200),
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
        "$ -> beds, buildSites, commandsApplied, creatures, digDesignations, domain, economy, events, jobs, labor, looseItems, map, materialStockpile, nextJobId, pendingCommands, priorities, raiders, rooms, rules, schemaVersion, seed, sessionResult, stations, stocks, threat, tick, waves, zones",
        "$.beds[] -> growthProgress, position, ripe",
        "$.buildSites[] -> delivered, incomingReserved, jobId, progressTicks, reachable, required, requiredTicks, reservedBy, statusCode, tile",
        "$.creatures[] -> affinities, blockedTicks, carryAmount, carrying, currentJobId, fatigue, grit, hp, id, injury, isMustering, lastDecision, lastMoveTick, lastYieldTick, martialForm, maxHp, mealReserved, mealTarget, mealTicksRemaining, might, mode, moveCount, musterNeedsRation, musterTarget, name, position, readiness, readinessAtRaid, recoveryTicks, rememberedPlaces, satiety, watchTicks, workTicks, yieldCount",
        "$.creatures[].lastDecision -> details, jobKind, reasonCode, target, tick",
        "$.creatures[].rememberedPlaces[] -> cause, place, tick",
        "$.digDesignations[] -> jobId, progressTicks, reachable, requiredTicks, reservedBy, statusCode, tile, workTile",
        "$.domain -> downedCreatures, injuredCreatures, livingCreatures, peakMeals, renown, renownAtPreviousWave, strength, strengthAtPreviousWave, waveCount, wavesArrived, wavesResolved",
        "$.economy -> buildsCompleted, cookBatchesCompleted, digsCompleted, harvestsCompleted, mealHaulsCompleted, mealsEaten, mealsProduced, rawHaulsCompleted, stoneConsumed, stoneDelivered, stoneHaulsCompleted, stoneProduced, stoneSpilled, stoneStored",
        "$.events[] -> creatureId, details, firstTick, jobKind, lastTick, reasonCode, repeats, target",
        "$.jobs[] -> jobId, key, kind, origin, personalCreatureId, pickedUp, progressTicks, quantity, remainingTicks, reservedBy, resource, sourceCell, storeCell, storeReserved, target",
        "$.labor -> buildTicks, digTicks, drillTicks, eatTicks, foodWorkPercent, foodWorkTicks, idleTicks, musterTicks, postCapacityTicks, postOccupancyPercent, postOccupiedTicks, restTicks, stoneHaulTicks, totalCreatureTicks, watchTicks",
        "$.looseItems[] -> position, quantity, resource",
        "$.map -> buildFloorTiles, builtPostTiles, diggableTiles, excavatedTiles, rockTiles, stockpileFloorTiles",
        "$.materialStockpile[] -> capacity, incomingReserved, position, reachable, statusCode, stored",
        "$.pendingCommands[] -> jobKind, kind, ruleId, tick, tiles, value, zoneKind",
        "$.raiders[] -> carryingMeals, hp, id, might, mode, position, returningToGate, stealTicks, wave",
        "$.rooms[] -> complete, contents, id, perimeter, purpose, statusCode",
        "$.rooms[].contents[] -> kind, position",
        "$.sessionResult -> defendersDowned, defendersFled, endTick, lastWaveOutcome, mealsLeft, mealsStolen, outcome, raidersDowned, renown, score, strength, unresolved, waveCount, wavesRepelled, wavesResolved",
        "$.stations[] -> kind, occupiedBy, occupiedTicks, position",
        "$.stocks -> capacity, carriedStone, looseMeals, looseRawMushroom, looseStone, meals, mealsEaten, mealsProduced, rawMushroom, reservedStone, siteStone, stockpileCapacity, storedStone",
        "$.threat -> active, announceTick, announced, arriveTick, raiderCount, raiderMight, ticksRemaining, waveCount, waveNumber",
        "$.waves[] -> announceTick, announced, arriveTick, arrived, defendersDowned, defendersFled, endTick, mealsStolen, number, outcome, raiderCount, raiderMight, raidersDowned, renownAtAnnounce",
    ];

    [Fact]
    public void The_canonical_snapshot_carries_exactly_the_recorded_composition()
    {
        Assert.Equal(ShapeRecordedForSchemaVersion, PrototypeCanonical.SchemaVersion);

        var problems = new List<string>();
        var observed = new List<(string Name, bool PartyEnded, SnapshotShape Shape)>();

        foreach (var (name, fixture, ticks) in Samples)
        {
            var run = PrototypeScenario.Run(LoadFixture(fixture), ticks);
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
