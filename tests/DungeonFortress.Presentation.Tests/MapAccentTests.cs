using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The half of Issue #58 that the set-of-cells tests cannot see.
///
/// "Unpausing must not redraw the marking" is not only about which cells carry a
/// mark — it is about how each one reads. The first attempt drew a waiting mark
/// with the colour of a designation waiting for a worker and a waiting blueprint
/// with the colour of a site whose material is on the way. Both are wrong in the
/// most ordinary session: mark rock with <c>Dig</c> priority 0 and every mark
/// turns grey the moment time moves; mark a training post before any stone has
/// been dug and it turns from teal to amber. The cells were right in both cases.
///
/// So the comparison is made where it belongs: the reading a mark has while it
/// waits, against the reading the world gives it, <em>across the very tick that
/// applies the command</em>. The world is the real one — the same log run to
/// <c>T</c> and to <c>T + 1</c> — so this also pins the copy: if the simulation's
/// status ladder changes and <see cref="MapAccents"/> does not, this fails.
/// </summary>
public sealed class MapAccentTests
{
    private static readonly GridPoint Rock = new(25, 1);
    private static readonly GridPoint Floor = new(12, 10);

    /// <summary>
    /// The same log stopped on the tick its last command carries, and one tick
    /// later. The first is the frame the player sees while paused; the second is
    /// what unpausing produces.
    /// </summary>
    private static (MapProjection Waiting, MapProjection Applied) Across(
        int tick,
        params PrototypeCommand[] commands)
    {
        var log = PresentationFixtures.Log(commands);
        return (
            MapProjection.Of(PrototypeScenario.Run(log, tick).State),
            MapProjection.Of(PrototypeScenario.Run(log, tick + 1).State));
    }

    // Read on the frame *after* the command applied, where nothing is waiting any
    // more, so these are the world's own word and the comparisons below are not
    // circular.
    private static DigMarkAccent AppliedDig(MapProjection applied, GridPoint tile) =>
        MapAccents.Dig(applied, applied.DigDesignations.Single(item => item.Tile == tile));

    private static BlueprintAccent AppliedBlueprint(MapProjection applied, GridPoint tile) =>
        MapAccents.Blueprint(applied, applied.BuildSites.Single(site => site.Tile == tile));

    private static StockpileCellAccent AppliedStockpile(MapProjection applied, GridPoint tile) =>
        MapAccents.Stockpile(applied, applied.StockpileCells.Single(cell => cell.Position == tile));

    [Fact]
    public void A_waiting_dig_mark_reads_the_same_as_the_designation_it_becomes()
    {
        var (waiting, applied) = Across(40, new DigDesignateCommand(40, [Rock]));

        Assert.Equal(DigMarkAccent.Waiting, MapAccents.PendingDig(waiting));
        Assert.Equal(MapAccents.PendingDig(waiting), AppliedDig(applied, Rock));
    }

    /// <summary>
    /// The defect this test exists for: with digging switched off the world calls
    /// every designation <c>dig_blocked_priority</c> on the first branch of its
    /// ladder, so a waiting mark drawn as an ordinary one made <em>every</em> mark
    /// change colour on unpause. The priority is a published snapshot field, not
    /// map topology.
    /// </summary>
    [Fact]
    public void A_waiting_dig_mark_is_already_grey_when_digging_is_switched_off()
    {
        var (waiting, applied) = Across(
            40,
            new SetPriorityCommand(0, JobKind.Dig, 0),
            new DigDesignateCommand(40, [Rock]));

        Assert.Equal(DigMarkAccent.BlockedByPriority, MapAccents.PendingDig(waiting));
        Assert.Equal(MapAccents.PendingDig(waiting), AppliedDig(applied, Rock));
    }

    /// <summary>
    /// The defect on the construction side: a fresh site has nothing delivered, so
    /// in the first session anybody plays — mark a post, then go dig for it — the
    /// applying tick deterministically answers <c>build_no_stone</c>. Drawing the
    /// waiting blueprint with the "a carrier is coming" colour meant the most
    /// ordinary blueprint in the game flipped colour on unpause.
    /// </summary>
    [Fact]
    public void A_waiting_blueprint_with_no_stone_in_the_world_reads_as_waiting_for_material()
    {
        var (waiting, applied) = Across(40, new BuildDesignateCommand(40, [Floor]));

        Assert.Equal(0, waiting.State.Stocks.LooseStone + waiting.State.Stocks.StoredStone);
        Assert.Equal(BlueprintAccent.WaitingForMaterial, MapAccents.PendingBlueprint(waiting, Floor));
        Assert.Equal(MapAccents.PendingBlueprint(waiting, Floor), AppliedBlueprint(applied, Floor));
    }

    /// <summary>
    /// A priority the player changed in the same paused moment counts.
    ///
    /// Switching digging off and then marking rock is one gesture, and the tick
    /// applies both: the world sets the priority first and then reads it on the
    /// first branch of its ladder. Reading the canonical value here made the mark
    /// blink in both directions — amber then grey when digging was being switched
    /// off, grey then amber when it was being switched back on — which is the
    /// original defect one level down. Every earlier priority case in this file
    /// puts the change on tick 0, where it is already applied; these two put it on
    /// the tick of the mark, which is what a player does.
    /// </summary>
    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    public void A_dig_mark_reads_the_priority_the_same_moment_accepted(int from, int to)
    {
        var (waiting, applied) = Across(
            40,
            new SetPriorityCommand(0, JobKind.Dig, from),
            new SetPriorityCommand(40, JobKind.Dig, to),
            new DigDesignateCommand(40, [Rock]));

        Assert.Equal(from, waiting.State.Priorities[JobKind.Dig]);
        Assert.Equal(to, waiting.Priority(JobKind.Dig));
        Assert.Equal(MapAccents.PendingDig(waiting), AppliedDig(applied, Rock));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    public void A_blueprint_reads_the_priority_the_same_moment_accepted(int from, int to)
    {
        var (waiting, applied) = Across(
            40,
            new SetPriorityCommand(0, JobKind.Build, from),
            new SetPriorityCommand(40, JobKind.Build, to),
            new BuildDesignateCommand(40, [Floor]));

        Assert.Equal(from, waiting.State.Priorities[JobKind.Build]);
        Assert.Equal(to, waiting.Priority(JobKind.Build));
        Assert.Equal(MapAccents.PendingBlueprint(waiting, Floor), AppliedBlueprint(applied, Floor));
    }

    /// <summary>
    /// And the other side of the same gate: with stone lying about, the site really
    /// is only waiting for somebody to pick it up, and the waiting blueprint has to
    /// say so from the start.
    /// </summary>
    [Fact]
    public void A_waiting_blueprint_with_free_stone_reads_as_waiting_for_a_carrier()
    {
        var (waiting, applied) = Across(
            700,
            new DigDesignateCommand(0, PresentationFixtures.Pocket),
            new BuildDesignateCommand(700, [Floor]));

        Assert.True(waiting.State.Stocks.LooseStone > 0);
        Assert.Equal(BlueprintAccent.WaitingForCarrier, MapAccents.PendingBlueprint(waiting, Floor));
        Assert.Equal(MapAccents.PendingBlueprint(waiting, Floor), AppliedBlueprint(applied, Floor));
    }

    [Theory]
    [InlineData(nameof(JobKind.Build))]
    [InlineData(nameof(JobKind.Haul))]
    public void A_waiting_blueprint_is_already_grey_when_the_work_it_needs_is_switched_off(string job)
    {
        var (waiting, applied) = Across(
            40,
            new SetPriorityCommand(0, Enum.Parse<JobKind>(job), 0),
            new BuildDesignateCommand(40, [Floor]));

        Assert.Equal(BlueprintAccent.BlockedByPriority, MapAccents.PendingBlueprint(waiting, Floor));
        Assert.Equal(MapAccents.PendingBlueprint(waiting, Floor), AppliedBlueprint(applied, Floor));
    }

    /// <summary>
    /// Forbidden is a zone, so the projection knows about it — including a
    /// Forbidden paint accepted in the same paused moment. A site nobody may step
    /// on therefore reads as unreachable straight away.
    /// </summary>
    [Fact]
    public void A_waiting_blueprint_on_forbidden_ground_reads_as_unreachable()
    {
        var (waiting, applied) = Across(
            40,
            new ZonePaintCommand(0, ZoneKind.Forbidden, [Floor]),
            new BuildDesignateCommand(40, [Floor]));

        Assert.Equal(BlueprintAccent.Unreachable, MapAccents.PendingBlueprint(waiting, Floor));
        Assert.Equal(MapAccents.PendingBlueprint(waiting, Floor), AppliedBlueprint(applied, Floor));
    }

    [Fact]
    public void A_waiting_stockpile_cell_reads_as_the_empty_cell_it_becomes()
    {
        var cell = PresentationFixtures.StockLeft;
        var (waiting, applied) = Across(
            40,
            new ZonePaintCommand(40, ZoneKind.MaterialStockpile, [cell]));

        Assert.Equal(StockpileCellAccent.Room, MapAccents.PendingStockpile(waiting, cell));
        Assert.Equal(MapAccents.PendingStockpile(waiting, cell), AppliedStockpile(applied, cell));
    }

    [Fact]
    public void A_waiting_stockpile_cell_on_forbidden_ground_reads_as_unreachable()
    {
        var cell = PresentationFixtures.StockLeft;
        var (waiting, applied) = Across(
            40,
            new ZonePaintCommand(0, ZoneKind.Forbidden, [cell]),
            new ZonePaintCommand(40, ZoneKind.MaterialStockpile, [cell]));

        Assert.Equal(StockpileCellAccent.Unreachable, MapAccents.PendingStockpile(waiting, cell));
        Assert.Equal(MapAccents.PendingStockpile(waiting, cell), AppliedStockpile(applied, cell));
    }

    /// <summary>
    /// One frame, two dig marks, one waiting priority: they must read the same.
    ///
    /// This is the sharpest form of the defect, and it is the form that a rule
    /// about "waiting marks" alone cannot fix. An older mark carries a
    /// <c>statusCode</c> the world computed under the old priority; a mark made a
    /// second ago knows the new one. Correcting only the second put two
    /// designations of different colours side by side on the same map making
    /// opposite claims about the same fact — reachable through the toolbar in two
    /// clicks, with any existing mark on screen.
    /// </summary>
    [Theory]
    [InlineData(3, 0)]
    [InlineData(0, 3)]
    public void An_old_mark_and_a_new_one_read_the_same_when_a_priority_is_waiting(int from, int to)
    {
        var diggable = PresentationFixtures.Baseline(1).Map.DiggableTiles;
        var older = diggable[0];
        var newer = diggable[^1];
        var (waiting, applied) = Across(
            40,
            new SetPriorityCommand(0, JobKind.Dig, from),
            new DigDesignateCommand(1, [older]),
            new SetPriorityCommand(40, JobKind.Dig, to),
            new DigDesignateCommand(40, [newer]));

        // Both are on the map in the same frame: one the world holds, one waiting.
        Assert.Contains(waiting.DigDesignations, item => item.Tile == older);
        Assert.Equal([newer], waiting.PendingDigMarks);

        var old = MapAccents.Dig(waiting, waiting.DigDesignations.Single(item => item.Tile == older));
        Assert.Equal(MapAccents.PendingDig(waiting), old);

        // ...and the frame after the tick still says the same about the old one.
        Assert.Equal(old, AppliedDig(applied, older));
        Assert.Equal(old, AppliedDig(applied, newer));
    }

    /// <summary>
    /// The same claim for construction, where the ladder has two priority gates
    /// and the second one sits far below the first.
    /// </summary>
    [Theory]
    [InlineData(nameof(JobKind.Build))]
    [InlineData(nameof(JobKind.Haul))]
    public void An_old_blueprint_and_a_new_one_read_the_same_when_a_priority_is_waiting(string job)
    {
        var older = new GridPoint(12, 10);
        var newer = new GridPoint(15, 10);
        var (waiting, applied) = Across(
            40,
            new BuildDesignateCommand(1, [older]),
            new SetPriorityCommand(40, Enum.Parse<JobKind>(job), 0),
            new BuildDesignateCommand(40, [newer]));

        Assert.Contains(waiting.BuildSites, site => site.Tile == older);
        Assert.Equal([newer], waiting.PendingBuildMarks);

        var old = MapAccents.Blueprint(waiting, waiting.BuildSites.Single(site => site.Tile == older));
        Assert.Equal(BlueprintAccent.BlockedByPriority, old);
        Assert.Equal(MapAccents.PendingBlueprint(waiting, newer), old);
        Assert.Equal(old, AppliedBlueprint(applied, older));
        Assert.Equal(old, AppliedBlueprint(applied, newer));
    }

    /// <summary>
    /// The same claim for a forbidden square, which is the other fact the player
    /// can change with a command and the world asks about in a ladder.
    ///
    /// Painting or erasing <c>Forbidden</c> over the tile of a blueprint the world
    /// already holds decides whether anybody may work there. Reading that from the
    /// site's own <c>Reachable</c> field takes it from the zones the world holds,
    /// which is a frame behind — and produced the same contradiction the priority
    /// did: two sites under one waiting paint, drawn in different colours.
    ///
    /// The whole-session sweep cannot catch this by construction. It compares the
    /// prediction with the world's word, and both took the same stale
    /// <c>Reachable</c>, so on exactly the ticks that matter they agreed with each
    /// other and were both wrong.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_old_blueprint_and_a_new_one_read_the_same_when_forbidden_is_waiting(bool painting)
    {
        var older = new GridPoint(12, 10);
        var newer = new GridPoint(15, 10);
        // Erasing needs the paint to be there in the first place, so the session
        // starts on the opposite side of the fact under test.
        PrototypeCommand[] before = painting
            ? []
            : [new ZonePaintCommand(0, ZoneKind.Forbidden, [older, newer])];
        PrototypeCommand waiting = painting
            ? new ZonePaintCommand(40, ZoneKind.Forbidden, [older, newer])
            : new ZoneEraseCommand(40, ZoneKind.Forbidden, [older, newer]);

        var (frame, applied) = Across(
            40,
            [.. before, new BuildDesignateCommand(1, [older]), waiting, new BuildDesignateCommand(40, [newer])]);

        var site = frame.BuildSites.Single(item => item.Tile == older);
        // The world is still holding the opposite of what the player just asked
        // for — reachable while a paint waits, unreachable while an erase does —
        // which is what makes this a real test of the fold rather than of the
        // snapshot.
        Assert.Equal(painting, site.Reachable);

        var old = MapAccents.Blueprint(frame, site);
        Assert.Equal(
            painting ? BlueprintAccent.Unreachable : BlueprintAccent.WaitingForMaterial,
            old);
        Assert.Equal(MapAccents.PendingBlueprint(frame, newer), old);
        Assert.Equal(old, AppliedBlueprint(applied, older));
        Assert.Equal(old, AppliedBlueprint(applied, newer));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_old_stockpile_cell_and_a_new_one_read_the_same_when_forbidden_is_waiting(bool painting)
    {
        var older = PresentationFixtures.StockLeft;
        var newer = PresentationFixtures.StockRight;
        PrototypeCommand[] before = painting
            ? []
            : [new ZonePaintCommand(0, ZoneKind.Forbidden, [older, newer])];
        PrototypeCommand waiting = painting
            ? new ZonePaintCommand(40, ZoneKind.Forbidden, [older, newer])
            : new ZoneEraseCommand(40, ZoneKind.Forbidden, [older, newer]);

        var (frame, applied) = Across(
            40,
            [
                .. before,
                new ZonePaintCommand(1, ZoneKind.MaterialStockpile, [older]),
                waiting,
                new ZonePaintCommand(40, ZoneKind.MaterialStockpile, [newer]),
            ]);

        var cell = frame.StockpileCells.Single(item => item.Position == older);
        Assert.Equal(painting, cell.Reachable);

        var old = MapAccents.Stockpile(frame, cell);
        Assert.Equal(
            painting ? StockpileCellAccent.Unreachable : StockpileCellAccent.Room,
            old);
        Assert.Equal(MapAccents.PendingStockpile(frame, newer), old);
        Assert.Equal(old, AppliedStockpile(applied, older));
        Assert.Equal(old, AppliedStockpile(applied, newer));
    }

    /// <summary>
    /// The gates are a ladder, so the order they are asked in is part of the
    /// answer, and only a case where two of them fire at once can pin it. Every
    /// defect found in this layer so far has been an unpinned rung.
    ///
    /// A tile nobody can reach is the one reading this side of the seam cannot
    /// have — but only while digging is switched on. With <c>Dig</c> priority 0
    /// the world never gets as far as reachability, so the waiting mark and the
    /// applied one agree even here.
    /// </summary>
    [Fact]
    public void Priority_is_asked_before_reachability_for_a_dig_mark()
    {
        var walledIn = new GridPoint(26, 2);
        var (waiting, applied) = Across(
            40,
            new SetPriorityCommand(0, JobKind.Dig, 0),
            new DigDesignateCommand(40, [walledIn]));

        Assert.False(applied.DigDesignations.Single(item => item.Tile == walledIn).Reachable);
        Assert.Equal(DigMarkAccent.BlockedByPriority, MapAccents.PendingDig(waiting));
        Assert.Equal(MapAccents.PendingDig(waiting), AppliedDig(applied, walledIn));
    }

    /// <summary>
    /// The same rung on the construction ladder: a site nobody may step on is
    /// unreachable whether or not carrying is switched off, because the world asks
    /// about the ground before it asks about the work.
    /// </summary>
    [Fact]
    public void Reachability_is_asked_before_hauling_for_a_blueprint()
    {
        var (waiting, applied) = Across(
            40,
            new SetPriorityCommand(0, JobKind.Haul, 0),
            new ZonePaintCommand(0, ZoneKind.Forbidden, [Floor]),
            new BuildDesignateCommand(40, [Floor]));

        Assert.Equal(BlueprintAccent.Unreachable, MapAccents.PendingBlueprint(waiting, Floor));
        Assert.Equal(MapAccents.PendingBlueprint(waiting, Floor), AppliedBlueprint(applied, Floor));
    }

    /// <summary>
    /// The one branch where the <c>− booked</c> term of "free stone for sites"
    /// actually decides the answer.
    ///
    /// It bites only when stone is lying there — loose or stored — and a live haul
    /// has already promised all of it to somewhere else. Then the world says
    /// <c>build_stone_reserved</c>: the stone exists, and this site still cannot
    /// have it. Everywhere else in this file the term is zero, or the sum it is
    /// subtracted from is, so the subtraction would go unmissed.
    ///
    /// An earlier version of this test picked the moment when the only block was
    /// in somebody's hands. That looked like the same case and was not: carried
    /// stone is not in the sum at all, so with <c>loose + stored == 0</c> the
    /// subtraction could not decide anything and replacing it with zero left the
    /// test green.
    ///
    /// The moment is found by running the session: one rock dug, one blueprint
    /// already claiming the block, and then the tick where the claim covers
    /// everything still on the ground.
    /// </summary>
    [Fact]
    public void A_waiting_blueprint_whose_stone_is_promised_elsewhere_reads_as_waiting_for_material()
    {
        var claimed = new GridPoint(15, 10);
        PrototypeCommand[] prelude =
        [
            new DigDesignateCommand(0, [Rock]),
            new BuildDesignateCommand(200, [claimed]),
        ];
        var tick = FirstTickWithEveryLoosePileClaimed(prelude);
        var (waiting, applied) = Across(tick, [.. prelude, new BuildDesignateCommand(tick, [Floor])]);

        var stocks = waiting.State.Stocks;
        Assert.True(stocks.LooseStone + stocks.StoredStone > 0);
        Assert.Equal(
            "build_stone_reserved",
            applied.BuildSites.Single(site => site.Tile == Floor).StatusCode);
        Assert.Equal(BlueprintAccent.WaitingForMaterial, MapAccents.PendingBlueprint(waiting, Floor));
        Assert.Equal(MapAccents.PendingBlueprint(waiting, Floor), AppliedBlueprint(applied, Floor));
    }

    /// <summary>
    /// The first tick at which every block of stone still on the ground is
    /// promised to a live haul, so a new site could be given none of it.
    /// </summary>
    private static int FirstTickWithEveryLoosePileClaimed(PrototypeCommand[] commands)
    {
        var world = new PrototypeWorld(PresentationFixtures.Log(commands));
        for (var step = 0; step < 900; step++)
        {
            var state = world.GetSnapshot();
            var onTheGround = state.Stocks.LooseStone + state.Stocks.StoredStone;
            var booked = state.Jobs
                .Where(job =>
                    job.Kind == JobKind.Haul &&
                    job.Resource == ResourceKind.Stone &&
                    job.ReservedBy is not null)
                .Sum(job => job.StoreReserved);
            if (onTheGround > 0 && booked >= onTheGround)
            {
                return state.Tick;
            }

            world.Step();
        }

        throw new InvalidOperationException(
            "The session never reached a tick where every block on the ground was booked.");
    }

    /// <summary>
    /// The ladders themselves, swept against the world.
    ///
    /// Nothing draws from a <c>statusCode</c> any more: both blueprints and
    /// stockpile cells are read by walking the world's ladder over published
    /// facts, unconditionally. That is only safe if the walk agrees with the
    /// simulation, so this compares the two on every tick of a full session where
    /// nothing is waiting — where the world's word is, by definition, the right
    /// answer. A rung that stops matching — a reordered gate, a changed threshold,
    /// a dropped term — fails here rather than in a playtest.
    ///
    /// What it cannot catch is a fact both sides read from the same stale place;
    /// that is what the point tests above are for.
    /// </summary>
    [Fact]
    public void The_predicted_readings_match_the_world_at_every_tick_of_a_session()
    {
        var world = new PrototypeWorld(PresentationFixtures.Log(SweptSession()));
        var sites = 0;
        var cells = 0;
        var seenSites = new HashSet<BlueprintAccent>();
        var seenCells = new HashSet<StockpileCellAccent>();
        for (var step = 0; step < PresentationFixtures.BlueprintTick + 400; step++)
        {
            var view = MapProjection.Of(world.GetSnapshot());
            world.Step();
            if (view.HasPendingIntent)
            {
                // On the tick a command lands the world has not applied it yet, so
                // its word is the old one and disagreeing with it is the point.
                continue;
            }

            foreach (var site in view.BuildSites)
            {
                var predicted = MapAccents.Blueprint(view, site);
                Assert.Equal(MapAccents.BlueprintReadingOfStatus(site.StatusCode), predicted);
                seenSites.Add(predicted);
                sites++;
            }

            foreach (var cell in view.StockpileCells)
            {
                var predicted = MapAccents.Stockpile(view, cell);
                Assert.Equal(MapAccents.StockpileReadingOfStatus(cell.StatusCode), predicted);
                seenCells.Add(predicted);
                cells++;
            }
        }

        Assert.True(sites > 50, $"only {sites} blueprint readings were compared");
        Assert.True(cells > 50, $"only {cells} stockpile readings were compared");
        Assert.Equal(
            [
                BlueprintAccent.WaitingForCarrier,
                BlueprintAccent.WaitingForMaterial,
                BlueprintAccent.InProgress,
                BlueprintAccent.BlockedByPriority,
                BlueprintAccent.Unreachable,
            ],
            seenSites.Order().ToArray());
        Assert.Equal(
            [
                StockpileCellAccent.Room,
                StockpileCellAccent.Full,
                StockpileCellAccent.Incoming,
                StockpileCellAccent.Unreachable,
            ],
            seenCells.Order().ToArray());
    }

    /// <summary>
    /// The Issue #48 chain with every rung of both ladders forced to fire at some
    /// point: construction switched off and back on, carrying switched off and
    /// back on, the site forbidden and released, a stockpile cell forbidden and
    /// released, and more posts than the dug stone can pay for. A sweep that only
    /// ever saw one answer would prove nothing.
    /// </summary>
    private static PrototypeCommand[] SweptSession()
    {
        var haul = PresentationFixtures.Baseline(1).Priorities[JobKind.Haul];
        var site = PresentationFixtures.Site;
        return
        [
            .. PresentationFixtures.BuildChain().Commands,
            new SetPriorityCommand(PresentationFixtures.BlueprintTick + 5, JobKind.Build, 0),
            new SetPriorityCommand(PresentationFixtures.BlueprintTick + 15, JobKind.Build, 3),
            new SetPriorityCommand(PresentationFixtures.BlueprintTick + 20, JobKind.Haul, 0),
            new SetPriorityCommand(PresentationFixtures.BlueprintTick + 30, JobKind.Haul, haul),
            new ZonePaintCommand(PresentationFixtures.BlueprintTick + 35, ZoneKind.Forbidden, [site]),
            new ZoneEraseCommand(PresentationFixtures.BlueprintTick + 45, ZoneKind.Forbidden, [site]),
            new ZonePaintCommand(
                PresentationFixtures.BlueprintTick + 55,
                ZoneKind.Forbidden,
                [PresentationFixtures.StockLeft]),
            new ZoneEraseCommand(
                PresentationFixtures.BlueprintTick + 65,
                ZoneKind.Forbidden,
                [PresentationFixtures.StockLeft]),
            // More posts than the dug stone can pay for, so some site is left
            // watching material it cannot have.
            new BuildDesignateCommand(
                PresentationFixtures.BlueprintTick + 200,
                [new GridPoint(12, 10), new GridPoint(15, 10), new GridPoint(12, 12)]),
        ];
    }

    /// <summary>
    /// The residual boundary, named rather than implied.
    ///
    /// <c>dig_unreachable</c> asks whether any orthogonal neighbour of the rock is
    /// passable, not the gate and not <c>Forbidden</c>. That is map topology, and
    /// copying it into the presentation layer would put the same rule on both
    /// sides of the seam ADR 0011 draws. So this is the one reading that can still
    /// change when the tick runs, and it is not a rounding error: on the shipped
    /// baseline map two of the twelve diggable tiles are walled in until a
    /// neighbour is dug.
    ///
    /// The test designates every diggable tile at once and sorts the readings the
    /// applying tick produces into three piles. Two are allowed:
    ///
    /// <list type="bullet">
    /// <item><see cref="DigMarkAccent.Unreachable"/> — the boundary above, and it
    /// must be exactly those two tiles;</item>
    /// <item><see cref="DigMarkAccent.InProgress"/> — somebody was already
    /// standing next to the rock and started digging on that tick. That is the
    /// world answering the mark, which is what the player asked for; it is not the
    /// mark being drawn a second time.</item>
    /// </list>
    ///
    /// Everything else must read exactly as it did while it waited. Widening the
    /// exception silently is not possible while this test exists.
    /// </summary>
    [Fact]
    public void Only_reachability_and_work_starting_change_a_reading_when_the_tick_runs()
    {
        var diggable = PresentationFixtures.Baseline(1).Map.DiggableTiles;
        var (waiting, applied) = Across(1, new DigDesignateCommand(1, [.. diggable]));
        var wasWaiting = MapAccents.PendingDig(waiting);

        var readings = diggable.ToDictionary(tile => tile, tile => AppliedDig(applied, tile));
        var unreachable = Tiles(readings, DigMarkAccent.Unreachable);
        var started = Tiles(readings, DigMarkAccent.InProgress);
        var unchanged = readings.Count(pair => pair.Value == wasWaiting);

        Assert.Equal(12, diggable.Count);
        Assert.Equal([new GridPoint(26, 1), new GridPoint(26, 2)], unreachable);
        Assert.Equal(diggable.Count, unreachable.Length + started.Length + unchanged);
    }

    private static GridPoint[] Tiles(
        IReadOnlyDictionary<GridPoint, DigMarkAccent> readings,
        DigMarkAccent accent) =>
        [.. readings.Where(pair => pair.Value == accent).Select(pair => pair.Key).Order()];
}
