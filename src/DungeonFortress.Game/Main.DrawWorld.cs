using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

// The view state a frame is drawn from, and drawing the world it
// describes: floors, rooms, zones, walls, routes and loose items.
public partial class Main
{
    private object ViewState()
    {
        var viewport = GetViewportRect().Size;
        var world = _worldViewport is null ? (Rect2?)null : WorldViewportScreenRect();
        var camera = _worldViewport is null ? (CameraFrame?)null : CurrentCameraFrame();
        var cameraNodePosition = _camera?.Position;
        return new
        {
            frameSize = new[] { viewport.X, viewport.Y },
            requestedFrameSize = _requestedFrameSize is { } requested
                ? new[] { requested.Width, requested.Height }
                : null,
            // Which half of the startup rule this run took. "explicit" is the
            // only value a capture can ever report, because a capture must
            // declare --frame-size and --ui-scale before anything else runs.
            frameMode = _requestedFrameSize is null ? "auto" : "explicit",
            uiScaleMode = _uiScaleIsAutomatic ? "auto" : "explicit",
            // "auto" survives a resize and dies on the first turn of the wheel,
            // so this field also answers "has the player chosen a zoom yet".
            cameraZoomMode = _cameraZoomIsAutomatic ? "auto" : "explicit",
            autoFrameSize = _autoFrameSize is { } automatic
                ? new[] { automatic.Width, automatic.Height }
                : null,
            // Machine-specific and therefore reported only by the runs that
            // actually consulted it, which are never the reproducible ones.
            screenUsableRect = _screenUsableRect is { } usable
                ? new[] { usable.X, usable.Y, usable.Width, usable.Height }
                : null,
            // What the readability policy measured on this frame and on every
            // supported one. It replaces startupFramePolicyChecks, which counted
            // assertions nothing compared with anything (Issue #86).
            hudReadability = HudReadabilityFit(),
            worldViewport = world is { } worldRect
                ? new[]
                {
                    worldRect.Position.X,
                    worldRect.Position.Y,
                    worldRect.Size.X,
                    worldRect.Size.Y,
                }
                : null,
            tileSize = _tileSize,
            // Height, and width beside it since Issue #77 connected a 17:12 pack:
            // a run that reports only one of the two cannot be asked whether the
            // canvas is being drawn in the shape it was authored in.
            goblinWorldSize = CameraView.GoblinDrawSize(_tileSize),
            goblinScreenSize = CameraView.GoblinDrawSize(_tileSize) * _cameraZoom,
            goblinWorldWidth = CameraView.GoblinDrawWidth(_tileSize),
            goblinScreenWidth = CameraView.GoblinDrawWidth(_tileSize) * _cameraZoom,
            cameraPosition = new[] { _cameraCenter.X, _cameraCenter.Y },
            cameraNodePosition = cameraNodePosition is { } nodePosition
                ? new[] { nodePosition.X, nodePosition.Y }
                : null,
            cameraZoom = _cameraZoom,
            zoomLevel = Array.IndexOf(CameraView.ZoomLevels.ToArray(), _cameraZoom),
            visibleWorldSize = camera is { } frame
                ? new[]
                {
                    frame.VisibleWorldSize.Width,
                    frame.VisibleWorldSize.Height,
                }
                : null,
            uiScale = _uiScale,
            displayServer = DisplayServer.GetName(),
            textureFilter = TextureFilter.ToString(),
            spriteMipmaps = _spritesHaveMipmaps,
            // Issue #244 / ADR 0020. What the rig delivered and what the duel
            // scene stopped on, so a captured frame can be asked which body it is
            // showing instead of being looked at.
            bodyRig = _bodyRig is null
                ? null
                : new
                {
                    parts = _bodyRig.Parts.Count,
                    layers = BodyRig.LayerOrder,
                    loaded = _rigParts.Count,
                    missing = _missingRigParts,
                    sourceToCanvas = _bodyRig.SourceToCanvas,
                },
            strikeScrub = _strikeScrub,
            flatBody = _flatBody,
            duel = _duelPair is { } duel
                ? new
                {
                    attacker = new[] { (int)duel.Attacker.Kind, duel.Attacker.Id },
                    target = new[] { (int)duel.Target.Kind, duel.Target.Id },
                    tick = _state?.Tick ?? 0,
                    // How many other standing bodies are close enough to be in
                    // the picture. Zero is what makes the scene a duel, and a
                    // frame that reports anything else is a crowd scene wearing
                    // the name.
                    crowd = DuelCrowd(duel),
                }
                : null,
            cameraInputChecks = _cameraInputChecks,
            cameraBoundsChecks = _cameraBoundsChecks,
            cameraPanChecks = _cameraPanChecks,
            cameraTransformChecks = _cameraTransformChecks,
            cameraSynchronizedAfterLayout = _cameraSynchronizedAfterLayout,
            hudInputRejected = _hudInputRejected,
        };
    }

    /// <summary>
    /// The four passes, and nothing else. This routine draws no primitive of its
    /// own on purpose: every mark belongs to a named routine the manifest in
    /// <c>DungeonFortress.Presentation.WorldDrawOrder</c> declares, and a mark
    /// drawn inline here would be a mark with no declared policy in the one place
    /// the manifest cannot see. <c>DrawMap_draws_nothing_of_its_own</c> is the
    /// check that keeps it that way.
    ///
    /// <para>
    /// The four <see cref="BeginWorldDrawPass"/> calls draw nothing and change
    /// nothing about the frame. They name, in the running code, the pass
    /// boundaries the manifest declares, so the world-geometry journal of
    /// Issue #295 can say <em>which</em> pass moved instead of only that
    /// something did. A run that is not recording does not notice them, and
    /// <c>WorldGeometryJournalGuardTests</c> holds the four to the four passes
    /// of <see cref="WorldDrawPass"/>, in the declared order and each opened
    /// before the first step that belongs to it.
    /// </para>
    /// </summary>
    private void DrawMap()
    {
        var rockTiles = _state!.Map.RockTiles.ToHashSet();
        var diggableTiles = _state.Map.DiggableTiles.ToHashSet();
        BeginWorldDrawPass(WorldDrawPass.BelowDepth);
        DrawMapBackground();
        DrawFloorTiles(rockTiles);
        // A room's floor is laid straight over the plain floor and under
        // everything else on the ground: the Dungeon Keeper answer of ADR 0013,
        // where a purpose has its own covering instead of a film over a shared
        // one. Sites, cells, beds and piles are drawn on top of it, because they
        // are things standing on the floor rather than the floor.
        DrawRoomFloors();
        DrawBuildSites();
        DrawStockpileCells();
        DrawBeds();
        DrawLooseItems();
        // A room's border is a line on the floor, so it is drawn on the floor and
        // whoever stands on it is drawn afterwards (Issue #156).
        DrawRoomBorders(rockTiles);
        BeginWorldDrawPass(WorldDrawPass.Depth);
        DrawElevatedWorld(rockTiles, diggableTiles);
        // Flat informational marks are projected above elevated geometry. A wall
        // must not erase the destination of an active job — nor the one part of a
        // room's border that a wall standing in front of it would swallow whole.
        BeginWorldDrawPass(WorldDrawPass.Informational);
        DrawRoomBordersOverWalls(rockTiles);
        DrawZoneOutlines();
        DrawJobRoutes();
        // A dig mark is a player-intent overlay on the wall, not wall material.
        // Drawing it after the depth pass keeps it readable on both top and face.
        DrawDigDesignations(rockTiles);
        DrawBuildSiteInformationOverlays();
        DrawStockpileInformationOverlays();
        DrawBodyInformationOverlays();
        DrawRoomLabels(rockTiles);
        DrawUnroomedObjects();
        DrawRememberedPlaces(rockTiles);
        DrawReturningHeroLabels();
        BeginWorldDrawPass(WorldDrawPass.Interaction);
        DrawCellInteractionOverlays(rockTiles);
        DrawBrushPreview(rockTiles);
    }

    /// <summary>
    /// Where the selected creature will not go back to (Issue #117).
    ///
    /// It is drawn only for the creature the player is looking at, for the same
    /// reason the selection ring is: nine creatures' memories at once would be a
    /// map full of crosses saying nothing about anybody. Selecting one and
    /// watching it walk around its own corner of the larder is the reading.
    ///
    /// A ring and a diagonal, no fill: <see cref="OverlayMark.RememberedPlace"/>
    /// is declared <see cref="OverlayMarkPolicy.StrokeOnly"/>, and the whole
    /// point of the mark is that somebody else is visibly still working on the
    /// tile next to it.
    /// </summary>
    private void DrawRememberedPlaces(IReadOnlySet<GridPoint> rockTiles)
    {
        if (_selectedCreatureId is not { } selected)
        {
            return;
        }

        var creature = _state!.Creatures.SingleOrDefault(item => item.Id == selected);
        if (creature is null)
        {
            return;
        }

        foreach (var place in creature.RememberedPlaces)
        {
            var rect = CellInteractionRect(place.Place, rockTiles);
            var color = place.Cause == "wound"
                ? new Color("#f87171")
                : new Color("#fbbf24");
            DrawRect(rect.Grow(-3), color, false, 2.0f);
            DrawLine(rect.Position + rect.Size * 0.25f, rect.End - rect.Size * 0.25f, color, 2.0f);
        }
    }

    /// <summary>
    /// The raider the domain has already met, saying so (Issue #358).
    ///
    /// Who gets a caption, what it says and where it sits are all decided by
    /// <c>DungeonFortress.Presentation.ReturningHeroLabel</c>; this routine
    /// multiplies its reference geometry by the tile scale and hands the strings
    /// to the engine. That split is the same one every other mark of this pass
    /// takes, and it is why the wording of the caption can be checked by a CI job
    /// that never opens Godot (ADR 0011).
    /// </summary>
    private void DrawReturningHeroLabels()
    {
        foreach (var caption in ReturningHeroLabel.Layout(_state!))
        {
            DrawReturningHeroLabel(caption, RaiderRenderCenter(caption.Raider));
        }
    }

    /// <inheritdoc cref="DrawReturningHeroLabels"/>
    private void DrawReturningHeroLabel(ReturningHeroCaption caption, Vector2 center)
    {
        var width = ScaleWorld((float)ReturningHeroLabel.LabelWidthRef);
        var outline = Math.Max(1, (int)Math.Round(ScaleWorld((float)ReturningHeroLabel.OutlineRef)));
        var lines = caption.Lines;
        var sizes = new[] { ReturningHeroLabel.NameTextRef, ReturningHeroLabel.StoryTextRef };
        var colors = new[] { ReturningHeroLabel.NameColor, ReturningHeroLabel.StoryColor };
        for (var index = 0; index < lines.Count; index++)
        {
            var origin = center + ScaleWorld(
                0,
                (float)(ReturningHeroLabel.TopRefOf(caption.Slot) +
                    (index * ReturningHeroLabel.LineHeightRef))) -
                new Vector2(width / 2f, 0);
            var size = Math.Max(1, (int)Math.Round(ScaleWorld((float)sizes[index])));

            // The rim first, for the reason the damage numbers of Issue #210 have
            // one: a caption drawn straight over a goblin cannot be read, and a
            // plate under it would be the fill this mark is declared not to have.
            DrawStringOutline(
                ThemeDB.FallbackFont,
                origin,
                lines[index],
                HorizontalAlignment.Center,
                width,
                size,
                outline,
                new Color(ReturningHeroLabel.OutlineColor));
            DrawString(
                ThemeDB.FallbackFont,
                origin,
                lines[index],
                HorizontalAlignment.Center,
                width,
                size,
                new Color(colors[index]));
        }
    }

    private void DrawMapBackground() =>
        DrawRect(new Rect2(Vector2.Zero, MapPixelSize), new Color("#111827"));

    private void DrawFloorTiles(IReadOnlySet<GridPoint> rockTiles)
    {
        for (var y = 0; y < PrototypeTuning.MapHeight; y++)
        {
            for (var x = 0; x < PrototypeTuning.MapWidth; x++)
            {
                var cell = new GridPoint(x, y);
                var rect = new Rect2(CellTopLeft(cell), new Vector2(_tileSize - 1, _tileSize - 1));
                if (!rockTiles.Contains(cell))
                {
                    DrawRect(rect, FloorTileColor(cell));
                }
            }
        }
    }

    private void DrawBeds()
    {
        foreach (var bed in _state!.Beds)
        {
            DrawCircle(
                CellCenter(bed.Position),
                ScaleWorld(5),
                bed.IsRipe ? new Color("#bef264") : new Color("#4d7c0f"));
        }
    }

    private void DrawLooseItems()
    {
        foreach (var loose in _state!.LooseItems)
        {
            var color = loose.Resource switch
            {
                ResourceKind.Meal => new Color("#fde68a"),
                ResourceKind.Stone => new Color("#cbd5e1"),
                _ => new Color("#a3e635"),
            };
            var center = CellCenter(loose.Position);
            DrawCircle(center, ScaleWorld(3 + Math.Min(3, loose.Quantity)), color);
            if (loose.Resource == ResourceKind.Stone)
            {
                // A dark rim separates loose stone from a pale meal at a glance.
                DrawArc(
                    center,
                    ScaleWorld(4.5f),
                    0,
                    Mathf.Tau,
                    12,
                    new Color("#475569"),
                    ScaleWorld(1.5f));
            }
        }
    }

    /// <summary>
    /// The floor of every room, by purpose. This is the half of Issue #52 that
    /// ADR 0013 says carries most of the complaint: «читаемость в Dungeon Keeper
    /// решена не контуром, а полом. У каждой комнаты собственное покрытие, а не
    /// полупрозрачная плёнка поверх общего пола.»
    ///
    /// It is drawn below the depth pass, so it is floor rather than an
    /// informational mark, and the "a mark must not hide a body" rule does not
    /// have to reach it: whoever is standing on the tile is drawn afterwards.
    /// </summary>
    private void DrawRoomFloors()
    {
        foreach (var room in _state!.Rooms)
        {
            var accent = RoomColor(MapAccents.Room(_projection!, room), room.Purpose);
            foreach (var cell in room.Perimeter)
            {
                DrawRoomFloor(cell, accent);
            }
        }
    }

    /// <summary>
    /// One tile of a room's covering: the purpose colour mixed into the dark floor
    /// rather than laid over it, so a room reads as a different floor and not as a
    /// stain on the same one.
    /// </summary>
    private void DrawRoomFloor(GridPoint cell, Color accent)
    {
        var rect = new Rect2(CellTopLeft(cell), new Vector2(_tileSize - 1, _tileSize - 1));
        DrawRect(rect, FloorTileColor(cell).Lerp(accent, 0.30f));
    }

    /// <summary>
    /// The border of every room: one line around the whole patch, not a box round
    /// each of its cells. ADR 0013 makes this mandatory — a room whose boundary is
    /// never shown is the Dwarf Fortress failure the variant was chosen against.
    ///
    /// This is the part of it drawn <em>below</em> the depth pass, which since
    /// Issue #156 is all of it except the segments
    /// <see cref="DrawRoomBordersOverWalls"/> takes. A body standing on the line
    /// is drawn after it and reads whole.
    /// </summary>
    private void DrawRoomBorders(IReadOnlySet<GridPoint> rockTiles)
    {
        foreach (var room in _state!.Rooms)
        {
            DrawRoomBorder(room, rockTiles);
        }
    }

    /// <summary>
    /// A room whose border would otherwise sit inside a wall's own drawn band —
    /// the facade hanging over it from the north (Issue #139), or the dark side
    /// seam centred on the boundary it shares with a wall to the east or west
    /// (Issue #147) — is inset further, so the line clears the wall rather than
    /// lying on it. <see cref="RoomGeometry.BorderInsetFor"/> owns that decision
    /// for both this and <see cref="DrawRoomLabel"/>.
    /// </summary>
    private void DrawRoomBorder(PrototypeRoomSnapshot room, IReadOnlySet<GridPoint> rockTiles)
    {
        var accent = RoomColor(MapAccents.Room(_projection!, room), room.Purpose);
        var purposeInset =
            RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rockTiles);
        var inset = ScaleWorld((float)purposeInset);
        foreach (var segment in RoomGeometry.Border(
                     room.Perimeter,
                     _tileSize,
                     inset,
                     rockTiles,
                     RoomBorderLayer.UnderBodies))
        {
            DrawLine(
                new Vector2((float)segment.From.X, (float)segment.From.Y),
                new Vector2((float)segment.To.X, (float)segment.To.Y),
                accent,
                ScaleWorld((float)RoomGeometry.BorderStrokeWidth));
        }
    }

    /// <summary>
    /// The other half of the same outline, drawn after the depth pass: the pieces
    /// a wall standing directly in front of the room paints over completely.
    ///
    /// A wall to the south is drawn in front of the cell behind it and covers the
    /// bottom of that cell outright — clearing it would cost more than
    /// <c>RoomGeometry.MaximumBorderInset</c>, so no inset is an answer and the
    /// answer is this pass (Issues #139, #147). What Issue #156 changed is how
    /// much of the border pays that price: only the pieces the wall really does
    /// swallow, which is a measurement <see cref="RoomGeometry.LayerOf"/> makes
    /// rather than a side of a cell somebody named. Because such a piece is inside
    /// the wall's own drawn band, it cannot cover a body the wall is not covering
    /// already.
    ///
    /// The unit is a piece and not a boundary edge, and that is not a detail:
    /// classifying whole edges left a wall in front cutting the vertical edge short
    /// of the horizontal one it meets, and the room's outline opened at that corner
    /// (<c>RoomGeometry.BorderPieces</c>).
    ///
    /// The loop below repeats <see cref="DrawRoomBorder"/>'s four lines instead of
    /// sharing them: a routine may only call routines of its own pass
    /// (<c>WorldDrawPassGuardTests.A_routine_only_calls_routines_of_its_own_pass</c>),
    /// and a shared drawing helper would be a routine in two passes at once.
    /// Everything that could actually drift — the colour, the inset, which pieces
    /// belong here — comes from the same pure calls both bodies make.
    /// </summary>
    private void DrawRoomBordersOverWalls(IReadOnlySet<GridPoint> rockTiles)
    {
        foreach (var room in _state!.Rooms)
        {
            DrawRoomBorderOverWall(room, rockTiles);
        }
    }

    /// <inheritdoc cref="DrawRoomBordersOverWalls"/>
    private void DrawRoomBorderOverWall(
        PrototypeRoomSnapshot room,
        IReadOnlySet<GridPoint> rockTiles)
    {
        var accent = RoomColor(MapAccents.Room(_projection!, room), room.Purpose);
        var purposeInset =
            RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rockTiles);
        var inset = ScaleWorld((float)purposeInset);
        foreach (var segment in RoomGeometry.Border(
                     room.Perimeter,
                     _tileSize,
                     inset,
                     rockTiles,
                     RoomBorderLayer.OverWallInFront))
        {
            DrawLine(
                new Vector2((float)segment.From.X, (float)segment.From.Y),
                new Vector2((float)segment.To.X, (float)segment.To.Y),
                accent,
                ScaleWorld((float)RoomGeometry.BorderStrokeWidth));
        }
    }

    /// <summary>
    /// A paint accepted on this tick and not applied yet — and, mirroring it, an
    /// erase accepted on this tick and not applied yet (Issue #130).
    ///
    /// The room a paint is about to join does not exist yet — which patch it
    /// lands in and whether it completes a room are questions that need the tick
    /// to run (see <c>MapAccents.Room</c>) — so the per-cell outline that used to
    /// draw every zone stays here for exactly the case Issue #58 opened: the
    /// player marks while paused and the map has to answer immediately.
    ///
    /// An erase is the same question with the sign flipped: the room still holds
    /// the cell until the tick runs, so the cell being removed is crossed out,
    /// per cell, the way the paint is outlined per cell. The cells come from
    /// <see cref="PendingZoneMarks"/> — the pure fold — so the map and the panel
    /// answer the same way.
    /// </summary>
    private void DrawZoneOutlines()
    {
        foreach (var zone in Enum.GetValues<ZoneKind>())
        {
            foreach (var cell in _projection!.Zone(zone))
            {
                if (!_projection.IsPendingZonePaint(zone, cell))
                {
                    continue;
                }

                var rect = new Rect2(
                    CellTopLeft(cell),
                    new Vector2(_tileSize - 1, _tileSize - 1));
                DrawRect(rect.Grow(-3), ZoneColor(zone), false, 1.5f);
            }

            foreach (var cell in PendingZoneMarks.Erasures(_projection!, zone))
            {
                var rect = new Rect2(
                    CellTopLeft(cell),
                    new Vector2(_tileSize - 1, _tileSize - 1));
                var inner = rect.Grow(-3);
                DrawRect(inner, ZoneColor(zone), false, 1.5f);
                DrawLine(inner.Position, inner.End, new Color("#ef4444"), 1.5f);
                DrawLine(
                    new Vector2(inner.End.X, inner.Position.Y),
                    new Vector2(inner.Position.X, inner.End.Y),
                    new Color("#ef4444"),
                    1.5f);
            }
        }
    }

    /// <summary>
    /// The alpha an informational mark's fill is drawn with, read from
    /// <c>DungeonFortress.Presentation.InformationalOverlays</c> rather than
    /// decided here.
    ///
    /// One rule governs every mark drawn above the depth pass: a mark that can
    /// share a cell with a body must not hide it. That is not a style preference
    /// — three separate marks broke it in three consecutive review rounds of
    /// Issue #83, each landing opaque on the very creature it explains — and it
    /// is not something this file can be trusted with, because no CI job builds
    /// it. The declaration, the reason and the number all live on the pure side
    /// of the seam; this method and <see cref="MarkAccent"/> are the whole of the
    /// adapter's part in it.
    /// </summary>
    private static float MarkFill(OverlayMark mark) =>
        (float)InformationalOverlays.FillAlpha(mark);

    /// <summary>
    /// The same, for a fill that carries the whole reading — a progress bar has
    /// no outline to hold its shape, so it gets its own declared value.
    /// </summary>
    private static float MarkAccent(OverlayMark mark) =>
        (float)InformationalOverlays.AccentAlpha(mark);

    private void DrawJobRoutes()
    {
        foreach (var job in _state!.Jobs)
        {
            var color = HaulRouteColor(job);
            DrawLine(
                CellCenter(job.Origin),
                CellCenter(job.Target),
                color with { A = MarkFill(OverlayMark.JobRoute) },
                ScaleWorld(1.0f));
            DrawCircle(
                CellCenter(job.Target),
                ScaleWorld(3.2f),
                color with { A = MarkFill(OverlayMark.JobRoute) });

            // A booked stockpile cell is part of the route even before pickup, so
            // the player can see where this pile is going.
            if (job.StoreCell is { } storeCell && storeCell != job.Target)
            {
                DrawLine(
                    CellCenter(job.Target),
                    CellCenter(storeCell),
                    color with { A = 0.25f },
                    ScaleWorld(1.0f));
            }
        }
    }

    /// <summary>
    /// Walls, bodies and tall structures share one painter's-order pass. The
    /// <see cref="WorldRenderItem"/> for a body is built from its interpolated
    /// center, so changing alpha can change depth without changing a tick or the
    /// canonical snapshot.
    /// </summary>
    private void DrawElevatedWorld(
        IReadOnlySet<GridPoint> rockTiles,
        IReadOnlySet<GridPoint> diggableTiles)
    {
        var items = new List<WorldRenderItem>();
        foreach (var cell in rockTiles)
        {
            items.Add(WorldRenderGeometry.ForCell(
                WorldRenderKind.Wall,
                GridCellId.Encode(cell, PrototypeTuning.MapWidth),
                cell,
                _tileSize));
        }

        var structures = _state!.Stations
            .Where(station => station.Kind == TileKind.Post)
            .ToDictionary(station =>
                GridCellId.Encode(station.Position, PrototypeTuning.MapWidth));
        foreach (var (stableId, station) in structures)
        {
            items.Add(WorldRenderGeometry.ForCell(
                WorldRenderKind.Structure,
                stableId,
                station.Position,
                _tileSize));
        }

        var creatureCenters = SceneCreatures().ToDictionary(
            creature => creature.Id,
            CreatureRenderCenter);
        foreach (var creature in SceneCreatures())
        {
            var center = creatureCenters[creature.Id];
            items.Add(WorldRenderGeometry.ForBody(
                WorldRenderKind.Creature,
                creature.Id,
                new ViewPoint(center.X, center.Y)));
        }

        var raiderCenters = SceneRaiders()
            .ToDictionary(raider => raider.Id, RaiderRenderCenter);
        foreach (var raider in SceneRaiders())
        {
            var center = raiderCenters[raider.Id];
            items.Add(WorldRenderGeometry.ForBody(
                WorldRenderKind.Raider,
                raider.Id,
                new ViewPoint(center.X, center.Y)));
        }

        var creatures = SceneCreatures().ToDictionary(creature => creature.Id);
        var raiders = SceneRaiders().ToDictionary(raider => raider.Id);
        foreach (var item in WorldRenderOrder.BackToFront(items))
        {
            switch (item.Kind)
            {
                case WorldRenderKind.Wall:
                    var cell = GridCellId.Decode(item.StableId, PrototypeTuning.MapWidth);
                    DrawWall(
                        cell,
                        WallTopology.SelectVariant(cell, rockTiles),
                        diggableTiles.Contains(cell));
                    break;
                case WorldRenderKind.Structure:
                    DrawBuiltPost(structures[item.StableId]);
                    break;
                case WorldRenderKind.Creature:
                    DrawCreature(creatures[item.StableId], creatureCenters[item.StableId]);
                    break;
                case WorldRenderKind.Raider:
                    DrawRaider(raiders[item.StableId], raiderCenters[item.StableId]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(item.Kind), item.Kind, null);
            }
        }
    }

    /// <summary>
    /// Graybox three-quarter wall geometry. The full tile is the connected top
    /// mass; an exposed observer-facing side replaces its lower strip with a dark
    /// facade. Missing cardinal neighbours add only outer seams, so connected
    /// rock has no internal checkerboard grid.
    /// </summary>
    private void DrawWall(GridPoint cell, WallTileVariant variant, bool isDiggable)
    {
        var geometry = WallRenderGeometry.ForCell(cell, variant, _tileSize);
        DrawRect(ToRect2(geometry.Top), WallTopColor(isDiggable));

        if (geometry.Facade is { } facade)
        {
            DrawRect(ToRect2(facade), WallFacadeColor(isDiggable));
        }

        foreach (var stroke in geometry.Strokes)
        {
            var color = stroke.Kind switch
            {
                WallStrokeKind.BrightEdge => WallEdgeColor(isDiggable),
                WallStrokeKind.DarkEdge => WallFacadeColor(isDiggable),
                WallStrokeKind.FacadeLip => WallLipColor(isDiggable),
                WallStrokeKind.FacadeBottom => new Color("#100d0c"),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(stroke.Kind),
                    stroke.Kind,
                    null),
            };

            // The width comes from the pure side (Issue #147): a seam centred on
            // a cell boundary paints half of itself into the neighbouring cell,
            // and the room border drawn there has to know how far.
            DrawLine(
                ToVector2(stroke.From),
                ToVector2(stroke.To),
                color,
                ScaleWorld((float)WallRenderGeometry.ReferenceStrokeWidth(stroke.Kind)));
        }
    }

    private static Color WallTopColor(bool isDiggable) =>
        isDiggable
            ? new Color("#6b6157")
            : new Color("#2a2522");

    private static Color WallFacadeColor(bool isDiggable) =>
        isDiggable
            ? new Color("#403832")
            : new Color("#171310");

    private static Color WallEdgeColor(bool isDiggable) =>
        isDiggable
            ? new Color("#a99682")
            : new Color("#55483f");

    private static Color WallLipColor(bool isDiggable) =>
        isDiggable
            ? new Color("#8b7968")
            : new Color("#3c332e");

    /// <summary>
    /// The shape a cell is highlighted and hit with. It is one call into
    /// <c>DungeonFortress.Presentation.SelectionGeometry</c> so that the hover
    /// highlight, the selected cell, the dig marks and the frame a drag stretches
    /// cannot end up describing different shapes for the same rock.
    /// </summary>
    private Rect2 CellInteractionRect(
        GridPoint cell,
        IReadOnlySet<GridPoint> rockTiles) =>
        ToRect2(SelectionGeometry.CellInteractionRect(cell, rockTiles, _tileSize));

    private static Vector2 ToVector2(ViewPoint point) =>
        new((float)point.X, (float)point.Y);

    private static Rect2 ToRect2(ViewRect rect) =>
        new(
            new Vector2((float)rect.X, (float)rect.Y),
            new Vector2((float)rect.Width, (float)rect.Height));
}
