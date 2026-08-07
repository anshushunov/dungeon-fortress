using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

// What is drawn over the cells rather than in them: brush preview and
// selection, dig designations, build sites and blueprints, stockpiles.
public partial class Main
{
    /// <summary>
    /// Input affordances belong above world depth: a selected wall and the legal
    /// targets of a held brush must remain visible even when the wall itself was
    /// deliberately drawn after a body behind it.
    /// </summary>
    private void DrawCellInteractionOverlays(IReadOnlySet<GridPoint> rockTiles)
    {
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                var cell = new GridPoint(x, y);
                var rect = CellInteractionRect(cell, rockTiles);

                // The rule is read from the same function the stroke uses, so an
                // outlined cell and an accepted cell cannot be different sets.
                if (LegalTargetOutline() is { } outline &&
                    BrushSelection.Accepts(_projection!, _editMode, _brushZone, cell))
                {
                    DrawRect(rect.Grow(-2), outline, false, 1.0f);
                }

                if (_selectedCell == cell)
                {
                    DrawRect(rect.Grow(-1), new Color("#f8fafc"), false, 2.0f);
                }
            }
        }
    }

    /// <summary>
    /// The colour every legal target of the held brush is outlined with, or
    /// <c>null</c> for a brush whose targets are already obvious on the map — a
    /// dig mark and a blueprint are drawn as themselves, so outlining them again
    /// would add noise rather than an affordance.
    /// </summary>
    private Color? LegalTargetOutline() => _editMode switch
    {
        BrushMode.Dig => new Color("#fbbf24") with { A = 0.75f },
        BrushMode.Build => new Color("#5eead4") with { A = 0.55f },
        BrushMode.Paint when _brushZone == ZoneKind.MaterialStockpile =>
            new Color("#cbd5e1") with { A = 0.55f },
        _ => null,
    };

    /// <summary>The colour a brush marks with when the cell is a legal target.</summary>
    private Color BrushAccent() => _editMode switch
    {
        BrushMode.Dig => new Color("#f59e0b"),
        BrushMode.CancelDig => new Color("#38bdf8"),
        BrushMode.Build => new Color("#2dd4bf"),
        BrushMode.CancelBuild => new Color("#38bdf8"),
        _ => ZoneColor(_brushZone),
    };

    /// <summary>
    /// What the brush would do if the button were released now.
    ///
    /// While a rectangle is being dragged this is the whole selection, cell by
    /// cell — accepted cells in the brush colour, cells the command will skip in
    /// red — plus the count, because "how many cells will this affect?" is the one
    /// question a highlighted area does not answer. With no drag in progress it is
    /// the single cell under the cursor, which is the same thing with one cell in
    /// it.
    /// </summary>
    private void DrawBrushPreview(IReadOnlySet<GridPoint> rockTiles)
    {
        if (_editMode == BrushMode.Inspect || _state is null)
        {
            return;
        }

        if (PendingStroke() is { } stroke && _dragAnchor is { } anchor)
        {
            var corner = _dragCurrent ?? anchor;
            var accepted = stroke.Tiles.ToHashSet();
            foreach (var cell in BrushSelection.Rectangle(anchor, corner))
            {
                var color = accepted.Contains(cell) ? BrushAccent() : new Color("#ef4444");
                var tile = CellInteractionRect(cell, rockTiles);
                DrawRect(
                    tile.Grow(-ScaleWorld(1)),
                    color with { A = MarkFill(OverlayMark.BrushPreview) });
            }

            // The frame follows the same shape the highlight does: the union of
            // the interaction rectangles of the cells it covers, column by
            // column. On a mixed floor/rock drag it rises only over the columns
            // whose first cell is rock, so it is neither a flat grid rectangle
            // nor the bounding box of the raised ones. Which cells the command
            // carries is unchanged and is still shown cell by cell above.
            var frame = SelectionGeometry.Outline(
                anchor,
                corner,
                rockTiles,
                _tileSize,
                ScaleWorld(1));
            DrawPolyline(
                frame.Select(ToVector2).ToArray(),
                new Color("#f8fafc"),
                ScaleWorld(1.5f));
            DrawSelectionCount(
                SelectionGeometry.Bounds(anchor, corner, rockTiles, _tileSize),
                stroke);
            return;
        }

        if (_hoverCell is not { } hovered || !IsMapCell(hovered))
        {
            return;
        }

        var preview = CellInteractionRect(hovered, rockTiles);
        var previewColor = BrushSelection.Accepts(_projection!, _editMode, _brushZone, hovered)
            ? BrushAccent()
            : new Color("#ef4444");
        DrawRect(
            preview.Grow(-ScaleWorld(1)),
            previewColor with { A = MarkFill(OverlayMark.BrushPreview) });
        DrawRect(
            preview.Grow(-ScaleWorld(1)),
            new Color("#f8fafc"),
            false,
            ScaleWorld(1.5f));
    }

    /// <summary>
    /// The number of cells the command will carry, drawn on the selection itself.
    /// It is the accepted count and not the area of the rectangle: a drag across
    /// floor and rock states how much of it the brush will actually take.
    ///
    /// Where the plate lands is decided by <c>SelectionGeometry.CaptionBox</c>:
    /// raised rock puts the top of a selection on row 0 above the map, so "keep
    /// it inside the map" stopped being a formality the moment the frame started
    /// following wall volume.
    /// </summary>
    private void DrawSelectionCount(ViewRect selection, BrushStroke stroke)
    {
        var width = ScaleWorld(58);
        var height = ScaleWorld(14);
        var text = stroke.Tiles.Count == 1 ? "1 cell" : $"{stroke.Tiles.Count} cells";
        var box = ToRect2(SelectionGeometry.CaptionBox(
            new ViewPoint(selection.X, selection.Y),
            new ViewSize(width, height),
            ScaleWorld(3),
            _tileSize));

        DrawRect(box, new Color("#0b1622"));
        DrawString(
            ThemeDB.FallbackFont,
            box.Position + new Vector2(ScaleWorld(3), height - ScaleWorld(3)),
            text,
            HorizontalAlignment.Left,
            width - ScaleWorld(6),
            Math.Max(1, (int)Math.Round(ScaleWorld(11))),
            stroke.Tiles.Count == 0 ? new Color("#fca5a5") : new Color("#f8fafc"));
    }

    /// <summary>
    /// Three distinct readings the player must get without opening the log: an
    /// intention that is waiting, an intention nobody can reach, and work in
    /// progress with how far along it is.
    /// </summary>
    private void DrawDigDesignations(IReadOnlySet<GridPoint> rockTiles)
    {
        // Accepted on this tick and not applied yet. Drawn first and drawn as the
        // designation it is about to become, accent included: the picture must not
        // change when the tick that records it runs.
        var pendingAccent = DigColor(MapAccents.PendingDig(_projection!));
        foreach (var tile in _projection!.PendingDigMarks)
        {
            DrawDigMark(tile, pendingAccent, rockTiles);
        }

        foreach (var designation in _projection.DigDesignations)
        {
            var accent = DigColor(MapAccents.Dig(_projection, designation));
            DrawDigMark(designation.Tile, accent, rockTiles);
            var center = CellInteractionRect(designation.Tile, rockTiles).GetCenter();

            if (designation.StatusCode == "dig_unreachable")
            {
                continue;
            }

            if (designation.WorkTile is { } workTile)
            {
                DrawLine(
                    CellCenter(workTile),
                    center,
                    accent with { A = 0.55f },
                    ScaleWorld(1.0f));
            }

            if (designation.ProgressTicks <= 0 || designation.RequiredTicks <= 0)
            {
                continue;
            }

            var fraction = Math.Clamp(
                designation.ProgressTicks / (float)designation.RequiredTicks,
                0f,
                1f);
            var wallRect = CellInteractionRect(designation.Tile, rockTiles);

            // The visible wall mass is eaten from the bottom up. The progress fill
            // and bar use the same raised bounds as the wall, not its flat grid
            // footprint.
            var eaten = (wallRect.Size.Y - ScaleWorld(2)) * fraction;
            DrawRect(
                new Rect2(
                    wallRect.Position +
                    new Vector2(
                        ScaleWorld(1),
                        wallRect.Size.Y - ScaleWorld(1) - eaten),
                    new Vector2(wallRect.Size.X - ScaleWorld(2), eaten)),
                new Color("#fbbf24") with { A = 0.6f });

            var barWidth = wallRect.Size.X - ScaleWorld(5);
            var barHeight = ScaleWorld(4);
            var barTopLeft = wallRect.End -
                new Vector2(wallRect.Size.X - ScaleWorld(2), ScaleWorld(7));
            DrawRect(
                new Rect2(barTopLeft, new Vector2(barWidth, barHeight)),
                new Color("#0f172a"));
            DrawRect(
                new Rect2(barTopLeft, new Vector2(barWidth * fraction, barHeight)),
                new Color("#fde047"));
        }
    }

    /// <summary>
    /// The mark itself: a tinted cell and the crossed pick that reads as "marked
    /// for excavation" at tile size. One routine for a designation the world holds
    /// and for one still waiting for its tick, so the two cannot drift apart and
    /// the moment of application cannot be seen.
    /// </summary>
    private void DrawDigMark(
        GridPoint tile,
        Color accent,
        IReadOnlySet<GridPoint> rockTiles)
    {
        var rect = CellInteractionRect(tile, rockTiles);
        DrawRect(rect.Grow(-ScaleWorld(1)), accent with { A = 0.26f });
        DrawRect(rect.Grow(-ScaleWorld(1)), accent, false, ScaleWorld(1.5f));

        var center = rect.GetCenter();
        DrawLine(
            center + ScaleWorld(-5, -5),
            center + ScaleWorld(5, 5),
            accent,
            ScaleWorld(1.5f));
        DrawLine(
            center + ScaleWorld(5, -5),
            center + ScaleWorld(-5, 5),
            accent,
            ScaleWorld(1.5f));
    }

    /// <summary>
    /// The palette, and nothing else. Which reading a mark has is decided in
    /// <c>DungeonFortress.Presentation.MapAccents</c>, where a unit test can
    /// compare the waiting reading against the applied one; this file is not
    /// built by the "Pure .NET" CI job, so a decision made here is a decision
    /// nothing checks.
    /// </summary>
    private static Color DigColor(DigMarkAccent accent) => accent switch
    {
        DigMarkAccent.InProgress => new Color("#fbbf24"),
        DigMarkAccent.Unreachable => new Color("#f87171"),
        DigMarkAccent.BlockedByPriority => new Color("#94a3b8"),
        _ => new Color("#f59e0b"),
    };

    /// <summary>
    /// A blueprint has to answer three questions at tile size: is this an
    /// intention rather than a building, how much of its material has arrived, and
    /// is anything actually happening. Delivered blocks are drawn as discrete pips
    /// so "1 of 2" is countable, and the caption keeps the graybox primitive
    /// readable without an asset — ADR 0008 is accepted but not implemented.
    /// </summary>
    private void DrawBuildSites()
    {
        // A blueprint the player marked on this tick, drawn as the blueprint it
        // becomes: nothing delivered, nothing booked, the full cost as hollow
        // pips, and the accent the same facts give it. BuildStoneCost is the same
        // tuning value the world charges.
        foreach (var tile in _projection!.PendingBuildMarks)
        {
            DrawBlueprint(
                tile,
                BuildColor(MapAccents.PendingBlueprint(_projection, tile)));
        }

        foreach (var site in _projection.BuildSites)
        {
            var accent = BuildColor(MapAccents.Blueprint(_projection, site));
            DrawBlueprint(site.Tile, accent);
        }
    }

    private void DrawBuildSiteInformationOverlays()
    {
        foreach (var tile in _projection!.PendingBuildMarks)
        {
            DrawBlueprintPips(
                tile,
                BuildColor(MapAccents.PendingBlueprint(_projection, tile)),
                0,
                0,
                PrototypeTuning.BuildStoneCost);
        }

        foreach (var site in _projection.BuildSites)
        {
            var accent = BuildColor(MapAccents.Blueprint(_projection, site));
            DrawBlueprintPips(
                site.Tile,
                accent,
                site.Delivered,
                site.IncomingReserved,
                site.Required);
            if (site.ProgressTicks <= 0 || site.RequiredTicks <= 0)
            {
                continue;
            }

            var fraction = Math.Clamp(
                site.ProgressTicks / (float)site.RequiredTicks,
                0f,
                1f);
            var barWidth = _tileSize - ScaleWorld(5);
            var barHeight = ScaleWorld(3);
            var barTopLeft = CellTopLeft(site.Tile) + ScaleWorld(2, 2);
            // Translucent, because a builder occupies the site for every one of
            // its BuildTicks: an opaque bar would sit on the sprite it explains.
            DrawRect(
                new Rect2(barTopLeft, new Vector2(barWidth, barHeight)),
                new Color("#0f172a") with { A = MarkFill(OverlayMark.BuildSiteProgress) });
            DrawRect(
                new Rect2(barTopLeft, new Vector2(barWidth * fraction, barHeight)),
                new Color("#5eead4") with { A = MarkAccent(OverlayMark.BuildSiteProgress) });
        }
    }

    /// <summary>
    /// The blueprint itself. One routine for a site the world holds and for one
    /// accepted on this tick, so applying the command changes the pips rather than
    /// making the blueprint appear.
    /// </summary>
    private void DrawBlueprint(
        GridPoint tile,
        Color accent)
    {
        var rect = new Rect2(CellTopLeft(tile), new Vector2(_tileSize - 1, _tileSize - 1));
        DrawRect(rect.Grow(-ScaleWorld(1)), accent with { A = 0.22f });
        DrawRect(rect.Grow(-ScaleWorld(1)), accent, false, ScaleWorld(1.5f));

        var topLeft = CellTopLeft(tile);
        DrawString(
            ThemeDB.FallbackFont,
            topLeft + ScaleWorld(2, 8),
            "POST?",
            HorizontalAlignment.Left,
            _tileSize - ScaleWorld(3),
            Math.Max(1, (int)Math.Round(ScaleWorld(6))),
            accent);
    }

    private void DrawBlueprintPips(
        GridPoint tile,
        Color accent,
        int delivered,
        int incomingReserved,
        int required)
    {
        var topLeft = CellTopLeft(tile);
        for (var index = 0; index < required; index++)
        {
            var pip = new Rect2(
                topLeft + new Vector2(ScaleWorld(3 + (index * 7)), _tileSize - ScaleWorld(9)),
                ScaleWorld(5, 5));
            if (index < delivered)
            {
                // The outline stays opaque so the pip is still countable; the fill
                // does not, because a builder stands on this very cell.
                DrawRect(
                    pip,
                    new Color("#e2e8f0") with { A = MarkFill(OverlayMark.BuildSiteProgress) });
                DrawRect(pip, new Color("#475569"), false, ScaleWorld(1.0f));
            }
            else if (index < delivered + incomingReserved)
            {
                DrawRect(pip, new Color("#7dd3fc"), false, ScaleWorld(1.0f));
            }
            else
            {
                DrawRect(pip, accent with { A = 0.45f }, false, ScaleWorld(1.0f));
            }
        }
    }

    /// <summary>The palette for a blueprint; the reading comes from MapAccents.</summary>
    private static Color BuildColor(BlueprintAccent accent) => accent switch
    {
        BlueprintAccent.InProgress => new Color("#5eead4"),
        BlueprintAccent.Unreachable => new Color("#f87171"),
        BlueprintAccent.BlockedByPriority => new Color("#94a3b8"),
        BlueprintAccent.WaitingForMaterial => new Color("#fbbf24"),
        _ => new Color("#2dd4bf"),
    };

    /// <summary>
    /// The end of the chain, drawn as a graybox primitive with a caption: a solid
    /// teal block so a built post reads as a built thing rather than as floor, and
    /// the word itself because the old small square cannot say "training post" on its
    /// own. The authored posts are drawn the same way, so the player cannot tell
    /// them apart — which is the claim the step is making. One post is drawn at a
    /// time because tall structures participate in the same Y-order as walls and
    /// bodies.
    /// </summary>
    private void DrawBuiltPost(PrototypeStationSnapshot station)
    {
        var topLeft = CellTopLeft(station.Position);
        var rect = new Rect2(topLeft, new Vector2(_tileSize - 1, _tileSize - 1));
        DrawRect(rect.Grow(-ScaleWorld(3)), new Color("#0f766e"));
        DrawRect(
            rect.Grow(-ScaleWorld(3)),
            new Color("#5eead4"),
            false,
            ScaleWorld(1.0f));
        DrawString(
            ThemeDB.FallbackFont,
            topLeft + ScaleWorld(2, 9),
            "POST",
            HorizontalAlignment.Left,
            _tileSize - ScaleWorld(3),
            Math.Max(1, (int)Math.Round(ScaleWorld(6))),
            new Color("#ccfbf1"));
    }

    /// <summary>
    /// A stockpile cell has to answer three questions at tile size: is this a
    /// storage slot at all, how full is it, and is its remaining room already
    /// promised to someone on the way. Stored blocks are drawn as discrete pips so
    /// "2 of 2" is countable rather than inferred from a bar.
    /// </summary>
    private void DrawStockpileCells()
    {
        // Painted on this tick and not applied yet: an empty cell, which is what
        // the world creates when it applies the paint.
        foreach (var tile in _projection!.PendingStockpileCells)
        {
            DrawStockpileCell(
                tile,
                StockpileColor(MapAccents.PendingStockpile(_projection, tile)));
        }

        foreach (var cell in _projection.StockpileCells)
        {
            DrawStockpileCell(
                cell.Position,
                StockpileColor(MapAccents.Stockpile(_projection, cell)));
        }
    }

    private void DrawStockpileInformationOverlays()
    {
        foreach (var cell in _projection!.StockpileCells)
        {
            DrawStockpilePips(cell.Position, cell.Stored, cell.IncomingReserved);
        }
    }

    /// <summary>
    /// One storage square. Shared by a cell the world holds and by one accepted on
    /// this tick, so painting a stockpile while paused draws the same square the
    /// tick would draw.
    /// </summary>
    private void DrawStockpileCell(GridPoint position, Color accent)
    {
        var rect = new Rect2(CellTopLeft(position), new Vector2(_tileSize - 1, _tileSize - 1));
        DrawRect(rect.Grow(-ScaleWorld(1)), new Color("#1f2937"));
        DrawRect(rect.Grow(-ScaleWorld(1)), accent, false, ScaleWorld(1.5f));

        // Corner ticks read as "a marked-out storage square" instead of just
        // another zone outline.
        var topLeft = CellTopLeft(position);
        foreach (var corner in new[]
                 {
                     (ScaleWorld(2, 2), ScaleWorld(6, 2), ScaleWorld(2, 6)),
                     (
                         new Vector2(_tileSize - ScaleWorld(3), ScaleWorld(2)),
                         new Vector2(_tileSize - ScaleWorld(7), ScaleWorld(2)),
                         new Vector2(_tileSize - ScaleWorld(3), ScaleWorld(6))),
                 })
        {
            DrawLine(
                topLeft + corner.Item1,
                topLeft + corner.Item2,
                accent,
                ScaleWorld(1.0f));
            DrawLine(
                topLeft + corner.Item1,
                topLeft + corner.Item3,
                accent,
                ScaleWorld(1.0f));
        }

    }

    private void DrawStockpilePips(
        GridPoint position,
        int stored,
        int incomingReserved)
    {
        var topLeft = CellTopLeft(position);
        for (var index = 0; index < stored; index++)
        {
            // Same rule as the blueprint pips: a carrier stands on the cell in the
            // tick the pip appears, so only the outline may be opaque.
            DrawRect(
                new Rect2(
                    topLeft + new Vector2(ScaleWorld(4 + (index * 7)), _tileSize - ScaleWorld(10)),
                    ScaleWorld(6, 6)),
                new Color("#e2e8f0") with { A = MarkFill(OverlayMark.StockpileOccupancy) });
            DrawRect(
                new Rect2(
                    topLeft + new Vector2(ScaleWorld(4 + (index * 7)), _tileSize - ScaleWorld(10)),
                    ScaleWorld(6, 6)),
                new Color("#475569"),
                false,
                ScaleWorld(1.0f));
        }

        // A hollow pip per booked slot: the player sees the room is taken even
        // though the carrier has not arrived yet.
        for (var index = stored; index < stored + incomingReserved; index++)
        {
            DrawRect(
                new Rect2(
                    topLeft + new Vector2(ScaleWorld(4 + (index * 7)), _tileSize - ScaleWorld(10)),
                    ScaleWorld(6, 6)),
                new Color("#7dd3fc"),
                false,
                ScaleWorld(1.0f));
        }
    }

    /// <summary>The palette for a stockpile cell; the reading comes from MapAccents.</summary>
    private static Color StockpileColor(StockpileCellAccent accent) => accent switch
    {
        StockpileCellAccent.Unreachable => new Color("#f87171"),
        StockpileCellAccent.Full => new Color("#e2e8f0"),
        StockpileCellAccent.Incoming => new Color("#7dd3fc"),
        _ => new Color("#94a3b8"),
    };
}
