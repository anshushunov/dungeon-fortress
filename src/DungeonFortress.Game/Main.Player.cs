using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

// What the player's input does to the world — brushes, selection, pause,
// speed, one applied command — and the scripted demo runs that stand in for it.
public partial class Main
{
    private void CycleZone()
    {
        _brushZone = (ZoneKind)(((int)_brushZone + 1) % Enum.GetValues<ZoneKind>().Length);
        RefreshState();
    }

    private void CycleJob()
    {
        _selectedJob = (JobKind)(((int)_selectedJob + 1) % Enum.GetValues<JobKind>().Length);
        _editingPriorities = true;
        RefreshState();
    }

    private void CycleRule()
    {
        _selectedRule = (_selectedRule + 1) % UiControls.RuleIds.Count;
        _editingPriorities = false;
        RefreshState();
    }

    private void UpdatePointer(Vector2 position)
    {
        _lastPanPointer = position;
        _hoverCell = ScreenToCell(position);
        _hoverCreatureId = _hoverCell is { } hovered
            ? _state!.Creatures.FirstOrDefault(creature => creature.Position == hovered)?.Id
            : null;
        UpdateCreatureLabels();
        QueueRedraw();
    }

    private void SelectAt(Vector2 position)
    {
        var cell = ScreenToCell(position);
        if (cell is not { } selected)
        {
            return;
        }

        _selectedCell = selected;
        _selectedCreatureId = _state!.Creatures
            .Where(creature => creature.Position == selected)
            .Select(creature => (int?)creature.Id)
            .FirstOrDefault();
        UpdateHud();
        UpdateCreatureLabels();
        QueueRedraw();
    }

    /// <summary>
    /// Points the inspector at one creature by name rather than by where it
    /// stands. Clicking the map has a cell and finds the creature on it; a card
    /// of the moment of truth has the creature and has to find the cell, and it
    /// has to work while the party is standing still — which is every moment the
    /// band is on screen (Issue #331).
    /// </summary>
    private void SelectCreature(int creatureId)
    {
        if (_state?.Creatures.FirstOrDefault(creature => creature.Id == creatureId)
            is not { } chosen)
        {
            return;
        }

        _selectedCreatureId = chosen.Id;
        _selectedCell = chosen.Position;
        UpdateHud();
        UpdateCreatureLabels();
        QueueRedraw();
    }

    /// <summary>
    /// What the rectangle the player is dragging would do right now. It is the
    /// same value the release applies, so the highlighted area, the cell count
    /// above the cursor and the command that lands cannot disagree.
    /// </summary>
    private BrushStroke? PendingStroke() =>
        _projection is null || _dragAnchor is not { } anchor
            ? null
            : BrushSelection.Resolve(
                _projection,
                _editMode,
                _brushZone,
                anchor,
                _dragCurrent ?? anchor);

    /// <summary>
    /// A released rectangle. Every cell the simulation would accept goes into
    /// <em>one</em> command, and a cell it would refuse never becomes a command at
    /// all — it becomes an explanation in the feedback line.
    ///
    /// One command rather than one per cell is what makes partially applied
    /// marking impossible: the world validates the whole tile list before it
    /// records the first designation, so a rejected rectangle changes nothing.
    ///
    /// A single click is a 1x1 rectangle and goes through exactly this path, so
    /// the click and the drag cannot drift apart either.
    /// </summary>
    private void ApplyBrushStroke(GridPoint from, GridPoint to)
    {
        if (_projection is null)
        {
            return;
        }

        // Resolved against the projection, so a cell that already carries a mark
        // the world has not applied yet is not marked a second time. Paused, that
        // is the difference between one command and one per click.
        var stroke = BrushSelection.Resolve(_projection, _editMode, _brushZone, from, to);
        if (BrushSelection.ToCommand(stroke, _projection.State.Tick) is { } command)
        {
            TryApplyPlayerCommand(command);
            return;
        }

        _controlFeedback = stroke.Refusal ?? "Nothing to mark there.";
        UpdateHud();
        QueueRedraw();
    }

    private void CancelDrag(string source)
    {
        _dragAnchor = null;
        _dragCurrent = null;
        // Nothing was emitted while the rectangle was being dragged, so there is
        // nothing to undo: a cancelled selection leaves no entry in the log.
        _controlFeedback = $"Selection cancelled ({source}); nothing was marked.";
        UpdateHud();
        QueueRedraw();
    }

    /// <summary>
    /// One key for the whole intent "I want a material stockpile here": it picks
    /// the zone and the Paint mode together, because cycling zones with [Z] to
    /// find MaterialStockpile is the step players lose the thread on. It stays an
    /// ordinary <c>zone_paint</c> — no new command and no new selection framework.
    /// </summary>
    private void SelectStockpileBrush()
    {
        _brushZone = ZoneKind.MaterialStockpile;
        SelectEditMode(BrushMode.Paint);
        _controlFeedback =
            "STOCKPILE [M]: painting MaterialStockpile. Drag a rectangle over pre-existing " +
            $"floor; each cell holds {PrototypeTuning.StockpileCellCapacity} stone. " +
            "[E] erases and drops stored stone back on the tile. Esc puts the brush away.";
        UpdateHud();
        QueueRedraw();
    }

    private void SelectEditMode(BrushMode mode)
    {
        _editMode = mode;
        // A brush change abandons whatever rectangle was in progress, and abandons
        // it the same way Esc does: nothing was emitted, so nothing is undone.
        _dragAnchor = null;
        _dragCurrent = null;
        _controlFeedback = mode switch
        {
            BrushMode.Dig =>
                "DIG: drag a rectangle over rock to mark it for excavation in one command. " +
                "A free creature chooses the job on its own. Esc cancels a drag, then the brush.",
            BrushMode.CancelDig =>
                "CANCEL DIG: drag a rectangle over designations to withdraw them. " +
                "Esc cancels a drag, then the brush.",
            BrushMode.Build =>
                "BUILD [C]: drag a rectangle over plain floor — including ground you dug — to " +
                $"mark training posts. Each costs {PrototypeTuning.BuildStoneCost} stone, " +
                "which the crew brings on its own. Esc cancels a drag, then the brush.",
            BrushMode.CancelBuild =>
                "UNBUILD [V]: drag a rectangle over blueprints to withdraw them. Stone already " +
                "delivered drops back onto that tile. Esc cancels a drag, then the brush.",
            BrushMode.Inspect => "Inspect mode; brush put away.",
            _ => _controlFeedback,
        };
        RefreshState();
    }

    private void CancelBrush(string source)
    {
        _editMode = BrushMode.Inspect;
        _dragAnchor = null;
        _dragCurrent = null;
        _controlFeedback = $"Inspect mode ({source}); brush put away.";
        RefreshState();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        // A run that is moving again draws the moment its own clock is at, not
        // the one somebody stepped to while it was stopped.
        _strikeScrub = null;
        ExplainHeldTime();
        UpdateHud();
        QueueRedraw();
    }

    /// <summary>
    /// Why the clock did not move (Issue #331). Asking for time while a verdict
    /// is owed spends a step of the window and advances no tick, and until this
    /// existed the run said nothing at all — the owner read a working pause as a
    /// broken one: «Снять с паузы нельзя — видимо так ждётся что-то, но на UI не
    /// понимаю что делать».
    ///
    /// <para>The press is deliberately not refused. Waiting the window out is one
    /// of the two ways the moment of truth closes (<c>CloseMomentOfTruth</c>), so
    /// a RUN that did nothing would take a legitimate answer — silence — away
    /// from the player. What was missing is the sentence.</para>
    /// </summary>
    private void ExplainHeldTime()
    {
        if (_state is not { MomentOfTruth.Open: true })
        {
            return;
        }

        _controlFeedback = MomentOfTruthPanel.TimeIsHeld(CurrentMomentOfTruth());
    }

    private void SetSpeed(double speed)
    {
        _speed = speed;
        _paused = false;
        UpdateHud();
        QueueRedraw();
    }

    private void AdjustSelectedControl(int delta)
    {
        if (_editingPriorities)
        {
            var priorityValue = Math.Clamp(_state!.Priorities[_selectedJob] + delta, PrototypeTuning.PriorityMinimum, PrototypeTuning.PriorityMaximum);
            TryApplyPlayerCommand(new SetPriorityCommand(_state.Tick, _selectedJob, priorityValue));
            return;
        }

        var ruleId = UiControls.RuleIds[_selectedRule];
        var maximum = ruleId switch
        {
            "ration_reserve" => PrototypeTuning.RationReserveMaximum,
            "drill_min_satiety" => PrototypeTuning.DrillMinimumSatietyMaximum,
            _ => PrototypeTuning.MusterLeadMaximum,
        };
        var value = Math.Clamp(_state!.Rules[ruleId] + delta, 0, maximum);
        TryApplyPlayerCommand(new SetRuleCommand(_state.Tick, ruleId, value));
    }

    /// <summary>
    /// Answers the card of the currently selected creature (Issue #312). The
    /// adapter contributes nothing but the sign: which creature is judged is the
    /// one the player clicked, and whether the judgement is legal at all is the
    /// simulation's answer on the tick of the command (ADR 0019).
    /// </summary>
    private void IssueVerdict(VerdictKind verdict)
    {
        if (_state is null)
        {
            return;
        }

        if (_selectedCreatureId is not { } creatureId)
        {
            _controlFeedback =
                "no creature selected: click the one the card is about, then G to reward or " +
                "H to punish.";
            UpdateHud();
            QueueRedraw();
            return;
        }

        TryApplyPlayerCommand(new VerdictCommand(_state.Tick, creatureId, verdict));
    }

    private void TryApplyPlayerCommand(PrototypeCommand command)
    {
        try
        {
            var candidateCommands = _playerCommands.Append(command).ToArray();
            var candidateLog = BuildFullLog(candidateCommands);
            PrototypeCommandValidator.Validate(candidateLog);
            var candidateWorld = new PrototypeWorld(candidateLog);
            // Replayed to the same **tick** and not for the same number of steps:
            // a step stopped being a tick when the party learned to stand still
            // between two waves (Issue #312), and RunTicks counts steps.
            while (!candidateWorld.IsComplete && candidateWorld.CurrentTick < _state!.Tick)
            {
                candidateWorld.Step();
            }
            _playerCommands.Add(command);
            _world = candidateWorld;
            _controlFeedback = $"accepted {HudText.DescribeCommand(command)}; activates on next tick";
            RefreshState();
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            RecordDiagnostic("indirect_command", exception);
            _controlFeedback = $"rejected {command.GetType().Name}: {exception.Message}";
            UpdateHud();
            QueueRedraw();
        }
    }

    private PrototypeCommandLog BuildFullLog(IEnumerable<PrototypeCommand> playerCommands)
    {
        var ordered = _fixtureLog!.Commands
            .Concat(playerCommands)
            .OrderBy(command => command.Tick)
            .ToArray();
        return new PrototypeCommandLog(_fixtureLog.Scenario, _fixtureLog.Seed, ordered);
    }

    private void ReplayCurrentLog()
    {
        var replay = new PrototypeWorld(BuildFullLog(_playerCommands));
        var replayTarget = _state!.Tick;
        while (!replay.IsComplete && replay.CurrentTick < replayTarget)
        {
            replay.Step();
        }

        var checksum = PrototypeScenario.Capture(replay).Checksum;
        _controlFeedback = checksum == _checksum ? "replay checksum matches" : "replay checksum MISMATCH";
        if (checksum == _checksum)
        {
            _world = replay;
            RefreshState();
        }
        else
        {
            RecordDiagnostic("replay", new InvalidOperationException(_controlFeedback));
            UpdateHud();
        }
    }

    private void ApplyDemoControls()
    {
        TryApplyPlayerCommand(new ZonePaintCommand(_state!.Tick, ZoneKind.TrainingGround, [new GridPoint(7, 11)]));
        TryApplyPlayerCommand(new SetPriorityCommand(_state!.Tick, JobKind.Drill, 4));
        TryApplyPlayerCommand(new SetRuleCommand(_state!.Tick, "ration_reserve", 4));
    }

    /// <summary>
    /// The reproducible excavation capture: mark four rock tiles with the DIG
    /// brush, withdraw one with CANCEL DIG, then let --screenshot-ticks pick the
    /// before/during/after moment. It uses the same brush path as a human.
    /// </summary>
    private void ApplyDemoDig()
    {
        _editMode = BrushMode.Dig;
        foreach (var tile in new GridPoint[]
                 {
                     new(25, 1), new(25, 2), new(25, 3), new(26, 1), new(26, 3),
                 })
        {
            ApplyBrushStroke(tile, tile);
        }

        // The withdrawal deliberately lands on the next tick, which is what keeps
        // this session's log the same shape as
        // scenarios/prototype1/dig-demo.commands.v2.json. The brush no longer
        // needs the step to see the marks: since Issue #58 a mark is part of the
        // projection the moment it is accepted.
        Advance(1);
        _editMode = BrushMode.CancelDig;
        ApplyBrushStroke(new GridPoint(26, 3), new GridPoint(26, 3));
        // Left holding the dig brush on purpose: the capture then also shows the
        // outline every still-diggable tile gets while the brush is active.
        _editMode = BrushMode.Dig;
        _selectedCell = new GridPoint(25, 3);
        _selectedCreatureId = null;
        _controlFeedback =
            "Demo: DIG marked (25,1) (25,2) (25,3) (26,1); CANCEL DIG withdrew (26,3). " +
            "(26,1) is walled in until a neighbour is dug.";
        RefreshState();
    }

    /// <summary>
    /// The reproducible stone-logistics capture. It uses the same brush path a
    /// human uses — [D] to mark rock, [M] to paint a stockpile — and schedules the
    /// stockpile for a later tick so that <c>--screenshot-ticks</c> alone selects
    /// the "loose stone, no stockpile", "stone in transit" or "stockpile full"
    /// moment. Nothing here addresses a creature.
    /// </summary>
    private void ApplyDemoStone()
    {
        _editMode = BrushMode.Dig;
        foreach (var tile in new GridPoint[] { new(25, 1), new(25, 2), new(25, 3), new(26, 1) })
        {
            ApplyBrushStroke(tile, tile);
        }

        // The stockpile is painted at a fixed future tick, after the pocket is
        // excavated, so the earlier frames legitimately show stone with nowhere
        // to go instead of a stockpile that has not been drawn yet.
        SelectStockpileBrush();
        TryApplyPlayerCommand(
            new ZonePaintCommand(
                DemoStoneZoneTick,
                ZoneKind.MaterialStockpile,
                [new GridPoint(22, 1), new GridPoint(23, 1)]));

        _selectedCell = new GridPoint(23, 1);
        _selectedCreatureId = null;
        _controlFeedback =
            "Demo: DIG marked (25,1) (25,2) (25,3) (26,1); [M] paints the material " +
            $"stockpile (22,1) (23,1) at tick {DemoStoneZoneTick}. Nobody was ordered to carry anything.";
        RefreshState();
    }

    private const int DemoStoneZoneTick = 200;
    private const int DemoBuildBlueprintTick = 1_000;
    private static GridPoint DemoBuildSite => new(25, 2);

    /// <summary>
    /// The reproducible functional-room capture, and the whole Issue #48 chain in
    /// one brush session: [D] marks the pocket, [M] paints the stockpile, and at a
    /// fixed later tick [C] marks a blueprint on ground that did not exist at tick
    /// 0, [B] zones it as a TrainingGround and [J] switches Drill on. Nothing here
    /// addresses a creature; every stone that reaches the post is fetched back out
    /// of the stockpile by whoever is free.
    /// </summary>
    private void ApplyDemoBuild()
    {
        _editMode = BrushMode.Dig;
        foreach (var tile in new GridPoint[] { new(25, 1), new(25, 2), new(25, 3), new(26, 1) })
        {
            ApplyBrushStroke(tile, tile);
        }

        SelectStockpileBrush();
        TryApplyPlayerCommand(
            new ZonePaintCommand(
                DemoStoneZoneTick,
                ZoneKind.MaterialStockpile,
                [new GridPoint(22, 1), new GridPoint(23, 1)]));

        // Scheduled for a tick at which the pocket is dug and its stone is already
        // put away, so the blueprint has to pull the material back out again.
        TryApplyPlayerCommand(new BuildDesignateCommand(DemoBuildBlueprintTick, [DemoBuildSite]));
        TryApplyPlayerCommand(
            new ZonePaintCommand(
                DemoBuildBlueprintTick,
                ZoneKind.TrainingGround,
                [DemoBuildSite]));
        TryApplyPlayerCommand(
            new SetPriorityCommand(DemoBuildBlueprintTick, JobKind.Drill, 3));

        _editMode = BrushMode.Build;
        _brushZone = ZoneKind.TrainingGround;
        _selectedCell = DemoBuildSite;
        _selectedCreatureId = null;
        _controlFeedback =
            "Demo: DIG marked (25,1) (25,2) (25,3) (26,1); [M] paints the material " +
            $"stockpile (22,1) (23,1) at tick {DemoStoneZoneTick}; [C] marks a training " +
            $"post on (25,2) at tick {DemoBuildBlueprintTick}, [B] zones it TrainingGround " +
            "and Drill is switched on. Nobody was ordered to carry or build anything.";
        RefreshState();
    }

    /// <summary>
    /// The reproducible moment-of-truth capture (Issue #331): play the shipped
    /// journal until the party stops by itself and wait there.
    ///
    /// <para>
    /// It stops on a <em>state</em> and not on a tick, for the reason the
    /// simulation's own tests give: the tick a wave ends on is emergent, and a
    /// number here would be a balance value pretending to be a fixture. It is
    /// also why <c>--screenshot-ticks</c> cannot reach this frame — running "to
    /// tick N" past the end of a wave spends the whole window on the way and
    /// arrives after the question has closed.
    /// </para>
    ///
    /// <para>
    /// Nothing here is simulation: it runs ordinary steps of the shipped log and
    /// stops on one of them. A run that stopped here and one that played straight
    /// through the same steps print the same checksum.
    /// </para>
    /// </summary>
    private void ApplyDemoMomentOfTruth()
    {
        // Steps rather than ticks: while the window is open a step is spent
        // waiting, so the bound has to cover every window of the party as well as
        // every tick of it.
        var remaining = PrototypeTuning.SessionTicks +
            (PrototypeTuning.WaveCount * PrototypeTuning.MomentOfTruthWindowSteps);
        while (_world is { IsComplete: false, IsAwaitingVerdict: false } && remaining-- > 0)
        {
            RememberMotionOrigin();
            _world.Step();
        }

        RefreshState();
        if (_state is not { MomentOfTruth.Open: true })
        {
            throw new InvalidOperationException(
                $"Fixture '{_fixture}' played a whole party without ever stopping between two " +
                "waves, so --demo-moment-of-truth has no frame to capture.");
        }

        _paused = true;
        // Nothing selected on purpose: this is the frame the player is actually
        // given when a wave ends, and the question the capture has to answer is
        // whether that frame explains itself.
        _selectedCreatureId = null;
        _selectedCell = null;
        _controlFeedback =
            "Demo: the party stopped between two waves. The cards are under the map; " +
            "click one and answer it, or watch the window count down.";
        UpdateHud();
        QueueRedraw();
    }

    // ---------------------------------------------------------------------
    // The duel scene (Issue #244, ADR 0020)
    //
    // ADR 0020 asks the probe for «сцена один на один, крупно» and says why:
    // «качество самой анимации важнее поведения в толпе на этом шаге». So this
    // is not a new kind of run — it is the shipped raid fixture, stopped on the
    // first tick the canonical journal records a blow on, with the camera on the
    // two bodies that tick names.
    //
    // Nothing here reaches canonical state. The search runs ordinary ticks and
    // stops on one of them; the camera and the scrub decide pixels only. A duel
    // run and a plain run of the same fixture to the same tick print the same
    // checksum, which is the hard constraint of the Issue and is measured in
    // evidence/244-invariants.json.
    // ---------------------------------------------------------------------

    /// <summary>The fixture with a raid in it: the only one that produces a duel.</summary>
    private const string DuelFixture = "prepared";

    /// <summary>
    /// How far the search may run before giving up. The first wave of the shipped
    /// <c>prepared</c> journal reaches the defenders well inside this, and a bound
    /// is what keeps a fixture with no fighting in it from spinning the whole
    /// session.
    /// </summary>
    private const int DuelSearchTicks = PrototypeTuning.SessionTicks;

    /// <summary>
    /// How many steps one blow is scrubbed through. Twelve, because the chain has
    /// five phases and the shortest of them — the strike — is 17 % of a tick: a
    /// step coarser than that could skip the moment of contact entirely, which is
    /// the one frame the scene exists to show.
    /// </summary>
    private const int StrikeScrubSteps = 12;

    /// <summary>The zoom a duel is watched at: the largest the camera declares.</summary>
    private static double DuelZoom => CameraView.ZoomLevels[^1];

    /// <summary>
    /// How far from either body of the duel the scene wants nobody else, in
    /// cells. Two, which at the shipped tile and the duel's zoom is the whole
    /// visible height of the world viewport either side of the pair: a third body
    /// closer than that is in the frame, and a frame with a third body in it is
    /// the crowd scene the review of vertical 3 rejected and was right to.
    /// </summary>
    private const int DuelClearance = 2;

    private void ApplyDemoDuel(int? frame)
    {
        LoadFixture(DuelFixture, 1);
        var chosen = FindDuelTick();
        LoadFixture(DuelFixture, 1);
        while (_state!.Tick < chosen && _world is { IsComplete: false })
        {
            // The same pair of calls the running clock makes, so the frame knows
            // which cell every body stepped out of and the scrub below has a
            // journey to interpolate along.
            RememberMotionOrigin();
            Advance(1);
        }

        _duelPair = DuelPair();
        _paused = true;
        if (_duelPair is { } pair &&
            BodyPosition(pair.Attacker) is { } attacker &&
            BodyPosition(pair.Target) is { } target)
        {
            var one = CameraView.CellCenter(attacker, _tileSize);
            var other = CameraView.CellCenter(target, _tileSize);
            _cameraCenter = CameraView.ClampCenterToMap(
                new ViewPoint((one.X + other.X) / 2.0, (one.Y + other.Y) / 2.0),
                _tileSize);
            _cameraZoom = CameraView.ValidateZoom(DuelZoom);
            // Both are the scene's now, so neither the layout pass nor a resize
            // may take them back — the same latch --camera-zoom already sets.
            _cameraZoomIsAutomatic = false;
            // Nothing is selected on purpose. The selection ring is drawn over the
            // body it names, and at this zoom it covers the chest of one of the
            // two bodies the scene exists to look at.
            _selectedCreatureId = null;
            _selectedCell = null;
        }

        if (frame is { } step)
        {
            _strikeScrub = Math.Clamp(step, 0, StrikeScrubSteps) / (double)StrikeScrubSteps;
        }

        _controlFeedback =
            "Duel: the first recorded blow of the " + DuelFixture + " journal. " +
            "[SPACE] runs the exchange, [F] steps one twelfth of the blow at a " +
            "time, [S] runs one whole tick.";
        UpdateHud();
        QueueRedraw();
    }

    /// <summary>
    /// The two bodies of the first blow this tick's reading names, or <c>null</c>
    /// when the journal named none. Both ends have to be on the map: a blow whose
    /// striker the journal does not name is not a duel, it is one body being hurt
    /// by something the view may not draw.
    /// </summary>
    private (BodyRef Attacker, BodyRef Target)? DuelPair()
    {
        foreach (var blow in _blows.Blows)
        {
            if (blow.Attacker is { } attacker &&
                BodyPosition(attacker) is not null &&
                BodyPosition(blow.Target) is not null)
            {
                return (attacker, blow.Target);
            }
        }

        return null;
    }

    /// <summary>
    /// The tick the duel stops on: the first one whose blow has nobody else
    /// within <see cref="DuelClearance"/> of either end, and failing that the
    /// emptiest one the search found.
    ///
    /// <para>
    /// It runs the fixture forward and is then thrown away — the caller reloads
    /// and runs to the tick this returned. That costs the search twice and buys
    /// the one thing the scene is for: a frame with two bodies in it. Both passes
    /// run the same ticks of the same journal, so the tick they agree on is a
    /// property of the fixture rather than of when the search happened to stop.
    /// </para>
    /// </summary>
    private int FindDuelTick()
    {
        var chosen = 0;
        var emptiest = int.MaxValue;
        for (var searched = 0; searched < DuelSearchTicks; searched++)
        {
            if (DuelPair() is { } pair)
            {
                var score = DuelScore(pair);
                if (score < emptiest)
                {
                    emptiest = score;
                    chosen = _state!.Tick;
                }

                if (score == 0)
                {
                    break;
                }
            }

            if (_world is null || _world.IsComplete)
            {
                break;
            }

            Advance(1);
        }

        return chosen;
    }

    /// <summary>
    /// How bad a blow is as a duel: lower is better, and zero is a side-on blow
    /// with nobody else near it.
    ///
    /// <para>
    /// Side-on beats everything, which is what the hundred is for. A blow struck
    /// straight up or down has no sideways part at all, and the sideways part is
    /// what carries the reading: the facing, the lean and the direction two bodies
    /// are thrown in are all signed by it, and two bodies on one column are drawn
    /// one on top of the other besides.
    /// </para>
    /// </summary>
    private int DuelScore((BodyRef Attacker, BodyRef Target) pair)
    {
        if (BodyPosition(pair.Attacker) is not { } attacker ||
            BodyPosition(pair.Target) is not { } target)
        {
            return int.MaxValue;
        }

        return (attacker.X == target.X ? 100 : 0) + DuelCrowd(pair);
    }

    /// <summary>
    /// How many standing bodies other than the two of the blow are close enough
    /// to be in the picture with them.
    /// </summary>
    private int DuelCrowd((BodyRef Attacker, BodyRef Target) pair)
    {
        if (BodyPosition(pair.Attacker) is not { } attacker ||
            BodyPosition(pair.Target) is not { } target)
        {
            return int.MaxValue;
        }

        var crowd = 0;
        foreach (var creature in _state!.Creatures)
        {
            var body = new BodyRef(BodyKind.Creature, creature.Id);
            if (body != pair.Attacker && body != pair.Target &&
                IsNearDuel(creature.Position, attacker, target))
            {
                crowd++;
            }
        }

        foreach (var raider in _state.Raiders)
        {
            var body = new BodyRef(BodyKind.Raider, raider.Id);
            if (raider.Mode != RaiderMode.Escaped &&
                body != pair.Attacker && body != pair.Target &&
                IsNearDuel(raider.Position, attacker, target))
            {
                crowd++;
            }
        }

        return crowd;
    }

    private static bool IsNearDuel(GridPoint cell, GridPoint attacker, GridPoint target) =>
        Chebyshev(cell, attacker) <= DuelClearance ||
        Chebyshev(cell, target) <= DuelClearance;

    private static int Chebyshev(GridPoint one, GridPoint other) =>
        Math.Max(Math.Abs(one.X - other.X), Math.Abs(one.Y - other.Y));

    /// <summary>
    /// One step through the blow being drawn, without running a tick.
    ///
    /// <para>
    /// This is the "остановить и промотать покадрово" half of ADR 0020's scene,
    /// and it is presentation twice over: it picks which moment between two
    /// canonical snapshots is drawn and runs nothing. A run that has been stepped
    /// through a whole blow and one that has not print the same checksum.
    /// </para>
    /// </summary>
    private void StepStrikeFrame()
    {
        _paused = true;
        var current = (int)Math.Round((_strikeScrub ?? 1.0) * StrikeScrubSteps);
        _strikeScrub = ((current + 1) % (StrikeScrubSteps + 1)) / (double)StrikeScrubSteps;
        UpdateHud();
        QueueRedraw();
    }
}
