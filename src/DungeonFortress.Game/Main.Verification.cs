using System.Globalization;
using System.Text.Json;

using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

// The headless runs an agent asks for: the brush and marking smokes, the
// screenshot, the deterministic fixture and the result the CLI prints.
public partial class Main
{
    /// <summary>
    /// The reference path of the world-geometry journal, relative to the
    /// repository root.
    /// </summary>
    private const string WorldGeometryReference = "tests/golden/world/draw-calls.json";

    /// <summary>
    /// The flag that rewrites <see cref="WorldGeometryReference"/> instead of
    /// comparing with it. Regeneration is a deliberate act with a diff to read,
    /// which is why it is a flag and not "write the file if it is missing".
    /// </summary>
    private const string WorldGeometryWriteFlag = "--write-world-geometry";

    /// <summary>
    /// What the map actually draws, compared with the committed record of it
    /// (Issue #295).
    ///
    /// <para>
    /// <b>The hole this closes.</b> Nothing in the pipeline could see a change
    /// in map geometry. Measured, not assumed: with every room's border inset
    /// three times as far, the whole of <c>verify.ps1</c> stayed green — all
    /// nine stages, both canonical checksums byte for byte identical, the three
    /// golden UI frames matching, the repeated screenshot still equal to itself
    /// (<c>evidence/295-mutant-before.json</c>). The canonical checksum is
    /// view-invariant by construction, golden UI pins HUD text, the screenshot
    /// stage compares a picture with a second copy of itself, and the source
    /// guards pin which primitive a routine calls rather than what number it is
    /// given.
    /// </para>
    ///
    /// <para>
    /// <b>How it is captured.</b> The primitives in <c>Main.Rendering.cs</c>
    /// hide the engine's own, so switching the journal on turns one ordinary
    /// <c>DrawMap</c> into a record of every mark with its numbers, without a
    /// single call site knowing. Nothing is painted while recording, which is
    /// what lets this run from <c>_Ready</c> — the engine refuses a draw command
    /// outside <c>_Draw</c>, and the smoke never reaches a frame.
    /// </para>
    ///
    /// <para>
    /// <b>Where it runs.</b> Inside the controls smoke, which the <c>godot</c>
    /// stage starts exactly once. A stage of its own would be the honest place
    /// and would cost one more engine start; <c>scripts/verify.ps1</c> is held
    /// by three other Issues and this one may not edit it, so the check rides
    /// the one headless entry point that already runs once per verification.
    /// </para>
    ///
    /// <para>
    /// <b>What it does not claim.</b> It records what the adapter asks the
    /// engine to draw, not what the engine paints; a defect in the driver, in
    /// the font or in the sprite import is outside it, and that is the price of
    /// not being a golden PNG (<c>evidence/295-method-choice.json</c>).
    /// </para>
    /// </summary>
    private void VerifyWorldGeometry()
    {
        var journal = new WorldDrawJournal();
        var fallbackDrawsBefore = _fallbackSpriteDraws;
        var bodyFrameBefore = _bodyFrame;
        var selectedCellBefore = _selectedCell;
        var selectedCreatureBefore = _selectedCreatureId;
        var hoverCellBefore = _hoverCell;
        var editModeBefore = _editMode;
        var brushZoneBefore = _brushZone;
        var dragAnchorBefore = _dragAnchor;
        var dragCurrentBefore = _dragCurrent;
        try
        {
            // A frame nobody is pointing at draws no interaction pass at all,
            // and a pass with nothing in it is a pass this check cannot speak
            // for. So the recording frame is posed: one selected creature, one
            // hovered cell, and a dig rectangle held open over the rock the
            // fixture starts with. Every value here is a constant, which is
            // what keeps the record the same on every machine.
            _selectedCreatureId = _state!.Creatures.Select(creature => creature.Id).Min();
            _selectedCell = new GridPoint(14, 7);
            _hoverCell = new GridPoint(25, 1);
            _editMode = BrushMode.Dig;
            _brushZone = ZoneKind.TrainingGround;
            _dragAnchor = new GridPoint(25, 1);
            _dragCurrent = new GridPoint(26, 3);
            _worldDrawJournal = journal;
            DrawMap();
        }
        finally
        {
            // The recording pass is not a frame and must leave nothing of
            // itself behind: these are all values a real frame reads, and the
            // controls smoke this rides starts from the ones it had.
            _worldDrawJournal = null;
            _fallbackSpriteDraws = fallbackDrawsBefore;
            _bodyFrame = bodyFrameBefore;
            _selectedCell = selectedCellBefore;
            _selectedCreatureId = selectedCreatureBefore;
            _hoverCell = hoverCellBefore;
            _editMode = editModeBefore;
            _brushZone = brushZoneBefore;
            _dragAnchor = dragAnchorBefore;
            _dragCurrent = dragCurrentBefore;
        }

        var document = WorldGeometryDocument(journal);
        var rewrites = OS.GetCmdlineUserArgs()
            .Contains(WorldGeometryWriteFlag, StringComparer.Ordinal);
        var referencePath = WorldGeometryReferencePath();
        if (rewrites)
        {
            File.WriteAllText(
                referencePath,
                document,
                new System.Text.UTF8Encoding(false));
            GD.Print(JsonSerializer.Serialize(new
            {
                @event = "world_geometry_reference",
                status = "written",
                path = referencePath,
            }));
            return;
        }

        if (!File.Exists(referencePath))
        {
            throw new FileNotFoundException(
                $"The world-geometry reference is missing at '{referencePath}'. " +
                $"Regenerate it with {WorldGeometryWriteFlag} and review the diff.",
                referencePath);
        }

        var expected = File.ReadAllText(referencePath).Replace("\r\n", "\n");
        if (string.Equals(expected, document, StringComparison.Ordinal))
        {
            GD.Print(JsonSerializer.Serialize(new
            {
                @event = "world_geometry_reference",
                status = "ok",
                passes = journal.Passes.Count,
                calls = journal.Passes.Sum(pass => pass.Calls),
            }));
            return;
        }

        throw new InvalidOperationException(
            "The map is drawn with different geometry than the committed record of " +
            $"it ({WorldGeometryReference}):\n  " +
            string.Join("\n  ", WorldGeometryDifferences(expected, document)) +
            $"\nIf the change is intended, regenerate with {WorldGeometryWriteFlag} " +
            "and review the diff.");
    }

    /// <summary>
    /// The first few lines that differ, named by line number, so the failure
    /// says which pass moved instead of only that something did.
    /// </summary>
    private static IReadOnlyList<string> WorldGeometryDifferences(
        string expected,
        string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var differences = new List<string>();
        for (var index = 0;
             index < Math.Max(expectedLines.Length, actualLines.Length) && differences.Count < 8;
             index++)
        {
            var expectedLine = index < expectedLines.Length ? expectedLines[index] : "<missing>";
            var actualLine = index < actualLines.Length ? actualLines[index] : "<missing>";
            if (!string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                differences.Add(
                    $"line {index + 1}\n    committed: {expectedLine.Trim()}\n" +
                    $"    run:       {actualLine.Trim()}");
            }
        }

        return differences;
    }

    private string WorldGeometryDocument(WorldDrawJournal journal)
    {
        var passes = journal.Passes.Select(pass => new
        {
            pass = pass.Pass.ToString(),
            calls = pass.Calls,
            primitives = pass.Primitives.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal),
            extent = pass.Extent,
            sizes = pass.Sizes.ToArray(),
            digest = pass.Digest(),
        });

        var text = JsonSerializer.Serialize(
            new
            {
                frame = new
                {
                    entry = WorldDrawOrder.Entry,
                    fixture = _fixture,
                    tick = _state!.Tick,
                    canonicalChecksum = _checksum,
                    tileSize = _tileSize,
                },
                passes,
            },
            new JsonSerializerOptions { WriteIndented = true });
        return text.Replace("\r\n", "\n") + "\n";
    }

    /// <summary>
    /// Where the committed record lives, found the way a fixture is found: by
    /// walking up from the assembly and from the working directory until a
    /// directory looks like the repository.
    /// </summary>
    private static string WorldGeometryReferencePath()
    {
        var relative = WorldGeometryReference.Replace('/', Path.DirectorySeparatorChar);
        foreach (var startingDirectory in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory(),
                 })
        {
            for (var directory = new DirectoryInfo(startingDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "scenarios", "prototype1")))
                {
                    var candidate = Path.Combine(directory.FullName, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root to resolve '{WorldGeometryReference}'.");
    }

    private void VerifyControlsSmoke()
    {
        // Before the strokes below change anything: the geometry check is about
        // the map this run loaded, and every stroke this smoke applies would
        // move it. See VerifyWorldGeometry for why it rides this entry point.
        VerifyWorldGeometry();


        // This is an input seam rather than a simulation test: it asserts that a
        // brush stroke accepts multiple cells and that cancelling never leaves
        // the UI in a mouse-capturing edit mode.
        var strokeStart = _playerCommands.Count;
        _editMode = BrushMode.Paint;
        TryApplyPlayerCommand(new ZonePaintCommand(_state!.Tick, ZoneKind.TrainingGround, [new GridPoint(17, 10)]));
        TryApplyPlayerCommand(new ZonePaintCommand(_state!.Tick, ZoneKind.TrainingGround, [new GridPoint(18, 10)]));
        Advance(1); // Commands at the current tick become visible on the next simulation tick.
        if (_playerCommands.Count != strokeStart + 2 ||
            !_state!.Zones[ZoneKind.TrainingGround].Contains(new GridPoint(17, 10)) ||
            !_state.Zones[ZoneKind.TrainingGround].Contains(new GridPoint(18, 10)))
        {
            throw new InvalidOperationException("Brush smoke did not apply two independent cells.");
        }
        CancelBrush("smoke");
        if (_editMode != BrushMode.Inspect)
        {
            throw new InvalidOperationException("Brush smoke did not return to inspect mode.");
        }

        VerifyRectangleSelectionSmoke();

        var beforeChecksum = _checksum;
        var beforeCount = _playerCommands.Count;
        TryApplyPlayerCommand(new ZonePaintCommand(_state!.Tick, ZoneKind.Forbidden, [new GridPoint(14, 7)]));
        if (_playerCommands.Count != beforeCount || _checksum != beforeChecksum)
        {
            throw new InvalidOperationException("Invalid indirect command changed the world or log.");
        }

        VerifyPausedMarkingSmoke();
        VerifyDigBrushSmoke();
        VerifyStockpileBrushSmoke();
        VerifyBuildBrushSmoke();

        Advance(40);
        var first = PrototypeScenario.Capture(_world!).Checksum;
        var replay = new PrototypeWorld(BuildFullLog(_playerCommands));
        for (var index = 0; index < _state!.Tick; index += 3)
        {
            replay.RunTicks(Math.Min(3, _state.Tick - replay.CurrentTick));
        }
        if (PrototypeScenario.Capture(replay).Checksum != first)
        {
            throw new InvalidOperationException("Command replay differs across update pacing.");
        }
    }

    /// <summary>
    /// The input seam of the rectangle brush, checked through the same path the
    /// mouse drives.
    ///
    /// Two claims, and they are the two the whole step rests on: a released
    /// rectangle emits <em>exactly one</em> command carrying every cell of the
    /// selection, and a cancelled rectangle emits none and changes nothing. The
    /// second is why nothing is emitted until the button comes back up.
    /// </summary>
    private void VerifyRectangleSelectionSmoke()
    {
        // Plain floor clear of every authored feature, of the masonry and of the
        // two cells the zone stroke above already painted. It moved into the south
        // chamber with the dungeon of Issue #117: on the old hall the middle of
        // the map was open, and the same rectangle now takes in four walls.
        var from = new GridPoint(12, 12);
        var to = new GridPoint(15, 14);
        _brushZone = ZoneKind.TrainingGround;
        SelectEditMode(BrushMode.Paint);

        var cancelledCount = _playerCommands.Count;
        var cancelledChecksum = _checksum;
        _dragAnchor = from;
        _dragCurrent = to;
        CancelDrag("smoke");
        if (_dragAnchor is not null ||
            _playerCommands.Count != cancelledCount ||
            _checksum != cancelledChecksum)
        {
            throw new InvalidOperationException(
                "A cancelled selection left a trace in the command log or in the world.");
        }

        var before = _playerCommands.Count;
        ApplyBrushStroke(from, to);
        Advance(1);
        if (_playerCommands.Count != before + 1)
        {
            throw new InvalidOperationException(
                $"A 4x3 drag emitted {_playerCommands.Count - before} commands instead of one.");
        }

        if (_playerCommands[^1] is not ZonePaintCommand { Tiles.Count: 12 })
        {
            throw new InvalidOperationException(
                "The rectangle command did not carry all twelve cells of the selection.");
        }

        // Partially applied marking must not exist: either the whole rectangle is
        // zoned or none of it is.
        foreach (var tile in BrushSelection.Rectangle(from, to))
        {
            if (!_state!.Zones[ZoneKind.TrainingGround].Contains(tile))
            {
                throw new InvalidOperationException(
                    $"({tile.X},{tile.Y}) is inside the applied rectangle but is not zoned.");
            }
        }

        CancelBrush("rectangle smoke");
    }

    /// <summary>
    /// Issue #58, through the adapter rather than through the unit tests: a mark
    /// accepted while time is stopped is on the map at once, a withdrawal is off
    /// it at once, and the tick that finally records either of them does not
    /// change what is drawn.
    ///
    /// The last claim is the one a picture cannot make. Marking is only useful
    /// while paused if unpausing does not visibly redo it, so the check compares
    /// the set of cells that read as designated across the very tick that applies
    /// the command and requires it to be the same set.
    ///
    /// It leaves the world exactly as it found it — mark, apply, withdraw, apply —
    /// so the excavation smoke below still starts from no designations.
    /// </summary>
    private void VerifyPausedMarkingSmoke()
    {
        // In the excavation pocket and used by no other smoke in this file.
        var tile = new GridPoint(26, 3);
        var commandsBefore = _playerCommands.Count;
        var tickBefore = _state!.Tick;

        SelectEditMode(BrushMode.Dig);
        ApplyBrushStroke(tile, tile);

        if (_state!.Tick != tickBefore)
        {
            throw new InvalidOperationException(
                "Accepting a brush stroke advanced the simulation. Marking is not a time control.");
        }

        if (_playerCommands.Count != commandsBefore + 1)
        {
            throw new InvalidOperationException("The paused stroke did not emit exactly one command.");
        }

        if (_state.DigDesignations.Any(item => item.Tile == tile))
        {
            throw new InvalidOperationException(
                "Canonical state applied a command before its own tick ran.");
        }

        if (!_projection!.IsDesignatedForDigging(tile) ||
            !_projection.PendingDigMarks.Contains(tile))
        {
            throw new InvalidOperationException(
                "A designation accepted while paused is not on the map until time moves (Issue #58).");
        }

        if (BrushSelection.Accepts(_projection, BrushMode.Dig, _brushZone, tile))
        {
            throw new InvalidOperationException(
                "The dig brush offered a cell that already carries a mark waiting for its tick.");
        }

        var drawnBefore = DesignatedTiles();
        Advance(1);
        if (!_state!.DigDesignations.Any(item => item.Tile == tile) ||
            _projection!.PendingDigMarks.Count != 0)
        {
            throw new InvalidOperationException("The tick did not apply the paused designation.");
        }

        if (!drawnBefore.SequenceEqual(DesignatedTiles()))
        {
            throw new InvalidOperationException(
                "The cells drawn as designated changed when the command was applied: " +
                "unpausing redraws the marking instead of refining it.");
        }

        SelectEditMode(BrushMode.CancelDig);
        ApplyBrushStroke(tile, tile);
        if (_projection!.IsDesignatedForDigging(tile))
        {
            throw new InvalidOperationException(
                "A withdrawal accepted while paused stayed on the map until the next tick.");
        }

        if (!_state!.DigDesignations.Any(item => item.Tile == tile))
        {
            throw new InvalidOperationException(
                "The withdrawal reached canonical state before its own tick ran.");
        }

        Advance(1);
        if (_state!.DigDesignations.Any(item => item.Tile == tile) ||
            _projection!.HasPendingMarking)
        {
            throw new InvalidOperationException("The tick did not apply the paused withdrawal.");
        }

        CancelBrush("paused marking smoke");
    }

    /// <summary>Every cell that currently reads as designated, drawn or waiting.</summary>
    private IReadOnlyList<GridPoint> DesignatedTiles() =>
    [
        .. _projection!.DigDesignations
            .Select(item => item.Tile)
            .Concat(_projection.PendingDigMarks)
            .Order(),
    ];

    /// <summary>
    /// An input-seam check for the excavation brushes: a stroke marks several
    /// tiles, a stroke over floor and over the map boundary changes nothing, the
    /// cancel brush withdraws exactly one mark, and Esc leaves edit mode.
    /// </summary>
    private void VerifyDigBrushSmoke()
    {
        var strokeStart = _playerCommands.Count;
        _editMode = BrushMode.Dig;
        foreach (var tile in new GridPoint[] { new(25, 1), new(26, 1), new(25, 2) })
        {
            ApplyBrushStroke(tile, tile);
        }

        Advance(1);
        if (_playerCommands.Count != strokeStart + 3)
        {
            throw new InvalidOperationException("The dig brush did not mark three tiles.");
        }

        foreach (var tile in new GridPoint[] { new(25, 1), new(26, 1), new(25, 2) })
        {
            if (!_state!.DigDesignations.Any(item => item.Tile == tile))
            {
                throw new InvalidOperationException(
                    $"The dig brush did not designate ({tile.X},{tile.Y}).");
            }
        }

        var guardedChecksum = _checksum;
        var guardedCount = _playerCommands.Count;
        ApplyBrushStroke(new GridPoint(12, 12), new GridPoint(12, 12));
        ApplyBrushStroke(new GridPoint(0, 0), new GridPoint(0, 0));
        ApplyBrushStroke(PrototypeMapGate, PrototypeMapGate);
        ApplyBrushStroke(new GridPoint(25, 1), new GridPoint(25, 1));
        if (_playerCommands.Count != guardedCount || _checksum != guardedChecksum)
        {
            throw new InvalidOperationException(
                "The dig brush emitted a command for a tile the simulation forbids.");
        }

        _editMode = BrushMode.CancelDig;
        ApplyBrushStroke(new GridPoint(26, 1), new GridPoint(26, 1));
        ApplyBrushStroke(new GridPoint(12, 12), new GridPoint(12, 12));
        Advance(1);
        if (_playerCommands.Count != guardedCount + 1 ||
            _state!.DigDesignations.Any(item => item.Tile == new GridPoint(26, 1)) ||
            _state.DigDesignations.Count != 2)
        {
            throw new InvalidOperationException("The cancel-dig brush did not withdraw one mark.");
        }

        CancelBrush("dig smoke");
        if (_editMode != BrushMode.Inspect)
        {
            throw new InvalidOperationException("The dig brush did not return to inspect mode.");
        }

        // The whole point of the step: nobody was ordered, yet the rock changes.
        for (var guard = 0; guard < 400 && _state!.Economy.DigsCompleted == 0; guard++)
        {
            Advance(1);
        }

        if (_state!.Economy.DigsCompleted == 0 || _state.Stocks.LooseStone == 0)
        {
            throw new InvalidOperationException(
                "No designation was excavated autonomously inside the smoke budget.");
        }
    }

    /// <summary>
    /// An input-seam check for the [M] shortcut and the stockpile brush: one key
    /// selects both the zone and Paint, a stroke over rock, a feature and the gate
    /// emits nothing, painting works on plain floor, and the whole loose → carried
    /// → stored chain then runs without a single order.
    /// </summary>
    private void VerifyStockpileBrushSmoke()
    {
        SelectStockpileBrush();
        if (_editMode != BrushMode.Paint || _brushZone != ZoneKind.MaterialStockpile)
        {
            throw new InvalidOperationException("[M] did not select MaterialStockpile and Paint.");
        }

        var guardedChecksum = _checksum;
        var guardedCount = _playerCommands.Count;
        ApplyBrushStroke(new GridPoint(7, 1), new GridPoint(7, 1));   // internal rock
        ApplyBrushStroke(new GridPoint(0, 0), new GridPoint(0, 0));   // map boundary
        ApplyBrushStroke(PrototypeMapGate, PrototypeMapGate);
        ApplyBrushStroke(new GridPoint(14, 7), new GridPoint(14, 7));  // larder feature
        ApplyBrushStroke(new GridPoint(2, 1), new GridPoint(2, 1));   // mushroom bed
        if (_playerCommands.Count != guardedCount || _checksum != guardedChecksum)
        {
            throw new InvalidOperationException(
                "The stockpile brush emitted a command for a tile the simulation forbids.");
        }

        var stockpile = new GridPoint[] { new(22, 1), new(23, 1) };
        foreach (var tile in stockpile)
        {
            ApplyBrushStroke(tile, tile);
        }

        Advance(1);
        if (_playerCommands.Count != guardedCount + stockpile.Length)
        {
            throw new InvalidOperationException("The stockpile brush did not paint two cells.");
        }

        foreach (var tile in stockpile)
        {
            if (!_state!.StockpileCells.Any(cell => cell.Position == tile))
            {
                throw new InvalidOperationException(
                    $"The stockpile brush did not create a cell at ({tile.X},{tile.Y}).");
            }
        }

        CancelBrush("stockpile smoke");
        if (_editMode != BrushMode.Inspect)
        {
            throw new InvalidOperationException("The stockpile brush did not return to inspect mode.");
        }

        // The point of the step: nobody is addressed, yet the stone moves and the
        // total amount of stone in the world never changes.
        var produced = _state!.Economy.StoneProduced;
        for (var guard = 0; guard < 900 && _state.Stocks.StoredStone == 0; guard++)
        {
            Advance(1);
            var stocks = _state.Stocks;
            if (stocks.LooseStone + stocks.CarriedStone + stocks.StoredStone !=
                _state.Economy.StoneProduced)
            {
                throw new InvalidOperationException(
                    $"Stone conservation broke at tick {_state.Tick}: produced " +
                    $"{_state.Economy.StoneProduced}, loose {stocks.LooseStone}, " +
                    $"carried {stocks.CarriedStone}, stored {stocks.StoredStone}.");
            }
        }

        if (_state.Stocks.StoredStone == 0 || produced == 0)
        {
            throw new InvalidOperationException(
                "No stone reached the material stockpile inside the smoke budget.");
        }
    }

    /// <summary>
    /// An input-seam check for [C] and [V]: a stroke over rock, a feature and the
    /// gate emits nothing, a blueprint lands on ground the player dug, withdrawing
    /// it works, and then the whole chain — deliver, build, drill — runs with no
    /// order given and no stone lost.
    /// </summary>
    private void VerifyBuildBrushSmoke()
    {
        SelectEditMode(BrushMode.Build);
        if (_editMode != BrushMode.Build)
        {
            throw new InvalidOperationException("[C] did not select the build brush.");
        }

        var guardedChecksum = _checksum;
        var guardedCount = _playerCommands.Count;
        ApplyBrushStroke(new GridPoint(7, 1), new GridPoint(7, 1));   // internal rock
        ApplyBrushStroke(new GridPoint(0, 0), new GridPoint(0, 0));   // map boundary
        ApplyBrushStroke(PrototypeMapGate, PrototypeMapGate);
        ApplyBrushStroke(new GridPoint(14, 7), new GridPoint(14, 7));  // larder feature
        ApplyBrushStroke(new GridPoint(10, 2), new GridPoint(10, 2));  // an existing post
        ApplyBrushStroke(new GridPoint(22, 1), new GridPoint(22, 1));  // a stockpile cell
        if (_playerCommands.Count != guardedCount || _checksum != guardedChecksum)
        {
            throw new InvalidOperationException(
                "The build brush emitted a command for a tile the simulation forbids.");
        }

        // (25,1) and (25,2) are floor only because the dig smoke above excavated
        // them, which is the claim this step makes: a room out of carved space.
        var site = new GridPoint(25, 2);
        ApplyBrushStroke(new GridPoint(25, 1), new GridPoint(25, 1));
        ApplyBrushStroke(site, site);
        Advance(1);
        if (_playerCommands.Count != guardedCount + 2 ||
            !_state!.BuildSites.Any(item => item.Tile == site))
        {
            throw new InvalidOperationException("The build brush did not mark two blueprints.");
        }

        SelectEditMode(BrushMode.CancelBuild);
        ApplyBrushStroke(new GridPoint(25, 1), new GridPoint(25, 1));
        ApplyBrushStroke(new GridPoint(12, 12), new GridPoint(12, 12));
        Advance(1);
        if (_playerCommands.Count != guardedCount + 3 ||
            _state!.BuildSites.Count != 1)
        {
            throw new InvalidOperationException("The unbuild brush did not withdraw one blueprint.");
        }

        _editMode = BrushMode.Paint;
        _brushZone = ZoneKind.TrainingGround;
        ApplyBrushStroke(site, site);
        TryApplyPlayerCommand(new SetPriorityCommand(_state!.Tick, JobKind.Drill, 3));
        CancelBrush("build smoke");
        if (_editMode != BrushMode.Inspect)
        {
            throw new InvalidOperationException("The build brush did not return to inspect mode.");
        }

        // Nobody is addressed, yet the post appears and stone stops being a number.
        for (var guard = 0; guard < 900 && _state!.Economy.BuildsCompleted == 0; guard++)
        {
            Advance(1);
            var stocks = _state.Stocks;
            if (stocks.LooseStone + stocks.CarriedStone + stocks.StoredStone +
                stocks.SiteStone + _state.Economy.StoneConsumed !=
                _state.Economy.StoneProduced)
            {
                throw new InvalidOperationException(
                    $"Stone conservation broke at tick {_state.Tick}: produced " +
                    $"{_state.Economy.StoneProduced}, loose {stocks.LooseStone}, " +
                    $"carried {stocks.CarriedStone}, stored {stocks.StoredStone}, " +
                    $"on site {stocks.SiteStone}, consumed {_state.Economy.StoneConsumed}.");
            }
        }

        if (_state!.Economy.BuildsCompleted == 0 ||
            !_state.Map.BuiltPostTiles.Contains(site))
        {
            throw new InvalidOperationException(
                "No training post was built autonomously inside the smoke budget.");
        }

        for (var guard = 0; guard < 200 &&
             !_state.Jobs.Any(job => job.Kind == JobKind.Drill && job.Origin == site);
             guard++)
        {
            Advance(1);
        }

        if (!_state.Jobs.Any(job => job.Kind == JobKind.Drill && job.Origin == site))
        {
            throw new InvalidOperationException(
                "The built post produced no Drill job inside the smoke budget.");
        }
    }

    private static GridPoint PrototypeMapGate => new(27, 13);

    private void CaptureScreenshot(string path)
    {
        try
        {
            var resolved = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            var result = GetViewport().GetTexture().GetImage().SavePng(resolved);
            if (result != Error.Ok)
            {
                throw new IOException($"SavePng returned {result}.");
            }

            GD.Print(JsonSerializer.Serialize(new
            {
                @event = "godot_graybox_screenshot",
                status = "ok",
                fixture = _fixture,
                seed = _state!.Seed,
                tick = _state!.Tick,
                checksum = _checksum,
                path = resolved,
                view = ViewState(),
                // The frame carries its own conservation evidence, so a reviewer
                // never has to trust the picture alone.
                stoneProduced = _state.Economy.StoneProduced,
                looseStone = _state.Stocks.LooseStone,
                carriedStone = _state.Stocks.CarriedStone,
                storedStone = _state.Stocks.StoredStone,
                siteStone = _state.Stocks.SiteStone,
                stoneConsumed = _state.Economy.StoneConsumed,
                stockpileCapacity = _state.Stocks.StockpileCapacity,
                buildsCompleted = _state.Economy.BuildsCompleted,
                ui = UiText(),
                labelFit = LabelFit(),
                controlStrips = ControlStripFit(),
                loadedSpriteStates = _loadedSpriteStates,
                missingSpriteStates = _missingSpriteStates,
                fallbackSpriteDraws = _fallbackSpriteDraws,
                runtimeDiagnostics = _diagnostics,
            }));
        }
        catch (Exception exception)
        {
            RecordDiagnostic("screenshot", exception);
            GD.PushError(exception.ToString());
            GetTree().Quit(1);
        }
    }

    private void VerifyDeterministicFixture(string fixture)
    {
        var first = PrototypeScenario.Run(PrototypeCommandDocument.Load(FixturePath(fixture)), _state!.Tick);
        var second = PrototypeScenario.Run(PrototypeCommandDocument.Load(FixturePath(fixture)), _state.Tick);
        if (!first.CanonicalJson.AsSpan().SequenceEqual(second.CanonicalJson))
        {
            throw new InvalidOperationException("Fixture replay produced different canonical state.");
        }
    }

    private void PrintResult(string eventName, string status, Exception? exception)
    {
        try
        {
            GD.Print(JsonSerializer.Serialize(new
            {
                @event = eventName,
                status,
                fixture = _fixture,
                seed = _state?.Seed,
                tick = _state?.Tick,
                checksum = _checksum,
                canonicalStateOwner = "DungeonFortress.Simulation.PrototypeWorld",
                view = ViewState(),
                // The same conservation evidence a screenshot carries. A headless run
                // is now a complete frame report, so the golden UI state does not need
                // a window to be captured.
                stoneProduced = _state?.Economy.StoneProduced,
                looseStone = _state?.Stocks.LooseStone,
                carriedStone = _state?.Stocks.CarriedStone,
                storedStone = _state?.Stocks.StoredStone,
                siteStone = _state?.Stocks.SiteStone,
                stoneConsumed = _state?.Economy.StoneConsumed,
                stockpileCapacity = _state?.Stocks.StockpileCapacity,
                buildsCompleted = _state?.Economy.BuildsCompleted,
                ui = _state is null ? null : UiText(),
                labelFit = _state is null || _hudRoot is null ? null : LabelFit(),
                controlStrips = ControlStripFit(),
                loadedSpriteStates = _loadedSpriteStates,
                missingSpriteStates = _missingSpriteStates,
                fallbackSpriteDraws = _fallbackSpriteDraws,
                runtimeDiagnostics = _diagnostics,
                errorType = exception?.GetType().Name,
                message = exception?.Message,
            }));
        }
        catch (Exception reportingException) when (exception is not null)
        {
            // Error reporting must not hide the original startup failure. Keep
            // this fallback independent of nodes and snapshots that may not exist.
            GD.Print(JsonSerializer.Serialize(new
            {
                @event = eventName,
                status = "error",
                fixture = _fixture,
                errorType = exception.GetType().Name,
                message = exception.Message,
                reportingErrorType = reportingException.GetType().Name,
                reportingMessage = reportingException.Message,
            }));
        }
    }

    private static string FormatNumber(double value) =>
        value.ToString("G17", CultureInfo.InvariantCulture);

    private static string FormatPoint(ViewPoint point) =>
        $"({FormatNumber(point.X)}, {FormatNumber(point.Y)})";

    private static string FormatVector(Vector2 vector) =>
        $"({FormatNumber(vector.X)}, {FormatNumber(vector.Y)})";

    private static string FormatSize(ViewSize size) =>
        $"{FormatNumber(size.Width)}x{FormatNumber(size.Height)}";

    private void RecordDiagnostic(string scope, Exception exception)
    {
        _diagnostics.Add(new RuntimeDiagnostic(scope, exception.GetType().Name, exception.Message));
        if (_diagnostics.Count > 12)
        {
            _diagnostics.RemoveAt(0);
        }
    }

    private static string FixturePath(string fixture)
    {
        foreach (var startingDirectory in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(startingDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "scenarios", "prototype1", $"{fixture}.commands.v2.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"Could not locate prototype fixture '{fixture}'.");
    }

    // Adapter-side alias for the pure bounds check, so hit testing and drawing
    // read the same as before the seam landed.
    private static bool IsMapCell(GridPoint cell) => MapBounds.Contains(cell);

    private Vector2 CellTopLeft(GridPoint cell)
    {
        var point = CameraView.CellTopLeft(cell, _tileSize);
        return new Vector2((float)point.X, (float)point.Y);
    }

    private Vector2 CellCenter(GridPoint cell)
    {
        var point = CameraView.CellCenter(cell, _tileSize);
        return new Vector2((float)point.X, (float)point.Y);
    }
}
