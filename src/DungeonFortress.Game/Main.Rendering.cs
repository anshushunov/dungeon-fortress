using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

// Rendering primitives the drawing files call: colours and legend words,
// which sprite a body uses, which way it faces, how a blow moves it, and
// the small draws that sit at the end of the source.
public partial class Main
{
    private Color FloorTileColor(GridPoint cell)
    {
        // Freshly excavated ground reads as new: brighter than the original floor.
        if (_state!.Map.ExcavatedTiles.Contains(cell)) return new Color("#3b5a7a");

        if (_state!.Beds.Any(bed => bed.Position == cell)) return new Color("#31572c");
        if (_state.Stations.Any(station => station.Position == cell && station.Kind == TileKind.Kitchen)) return new Color("#7c4a22");
        if (_state.Stations.Any(station => station.Position == cell && station.Kind == TileKind.Post)) return new Color("#134e4a");
        if (_projection!.IsInZone(ZoneKind.Larder, cell)) return new Color("#5b3a32");
        if (cell is { X: 20 or 21, Y: 3 } or { X: 21 or 22, Y: 4 }) return new Color("#3b4252");
        if (cell == new GridPoint(27, 13)) return new Color("#854d0e");
        return new Color("#243244");
    }

    /// <summary>
    /// The palette of a room, and nothing else. Which reading a room has is
    /// decided in <c>DungeonFortress.Presentation.MapAccents</c>, where a unit test
    /// can compare it against the world's own status code; this file is not built
    /// by the "Pure .NET" CI job, so a decision made here is a decision nothing
    /// checks.
    ///
    /// A working room wears its purpose colour, so the floor covering says what
    /// the room is for. The three ways of not working are deliberately *not*
    /// purpose colours: "this one is not doing anything" has to read the same
    /// wherever it happens, or the player learns eight of them instead of one.
    /// </summary>
    private static Color RoomColor(RoomAccent accent, ZoneKind purpose) => accent switch
    {
        RoomAccent.Unfinished => new Color("#fbbf24"),
        RoomAccent.BlockedByPriority => new Color("#94a3b8"),
        RoomAccent.Unreachable => new Color("#f87171"),
        _ => ZoneColor(purpose),
    };

    private static Color ZoneColor(ZoneKind zone) => zone switch
    {
        ZoneKind.Farm => new Color("#84cc16"),
        ZoneKind.Kitchen => new Color("#fb923c"),
        ZoneKind.Larder => new Color("#facc15"),
        ZoneKind.Quarters => new Color("#a78bfa"),
        ZoneKind.TrainingGround => new Color("#22d3ee"),
        ZoneKind.Watch => new Color("#f472b6"),
        ZoneKind.Forbidden => new Color("#ef4444"),
        ZoneKind.MaterialStockpile => new Color("#cbd5e1"),
        _ => new Color("#ffffff"),
    };

    /// <summary>
    /// Food and stone share one <c>Haul</c> kind but not one destination, so they
    /// must not share one route colour on the map.
    /// </summary>
    private static Color HaulRouteColor(PrototypeJobSnapshot job)
    {
        return job is { Kind: JobKind.Haul, Resource: ResourceKind.Stone }
            ? new Color("#cbd5e1")
            : JobColor(job.Kind);
    }

    private static Color JobColor(JobKind job) => job switch
    {
        JobKind.Harvest => new Color("#a3e635"),
        JobKind.Haul => new Color("#facc15"),
        JobKind.Cook => new Color("#fb923c"),
        JobKind.Rest => new Color("#a78bfa"),
        JobKind.Drill => new Color("#22d3ee"),
        JobKind.Watch => new Color("#f472b6"),
        JobKind.Dig => new Color("#f59e0b"),
        JobKind.Build => new Color("#2dd4bf"),
        _ => new Color("#ffffff"),
    };

    private string RaidLegend() =>
        "BATTLE LEGEND\n" +
        "teal outline = crew  •  red outline = raider\n" +
        "bar = HP  •  white X = DOWNED\n" +
        "dot: green work, amber combat,\n" +
        "gray downed, pink fled";

    // Adapter-side alias for the pure state abbreviation, so the map name labels
    // read the same as before the seam landed.
    private static string CreatureStateShort(PrototypeCreatureSnapshot creature) =>
        HudText.CreatureStateShort(creature);

    private static Color DefenderColor(PrototypeCreatureSnapshot creature) => creature.Mode switch
    {
        CreatureMode.Fighting => new Color("#fbbf24"),
        CreatureMode.Fled => new Color("#f472b6"),
        CreatureMode.Downed => new Color("#64748b"),
        CreatureMode.Working => new Color("#22d3ee"),
        _ => new Color("#38bdf8"),
    };

    private static Color CreatureStateColor(PrototypeCreatureSnapshot creature) => creature.Mode switch
    {
        CreatureMode.Downed => new Color("#94a3b8"),
        CreatureMode.Fled => new Color("#f472b6"),
        CreatureMode.Fighting => new Color("#fbbf24"),
        CreatureMode.Working => new Color("#4ade80"),
        _ => new Color("#bfdbfe"),
    };

    // Which pose a body is drawn in is BodySprites' answer, not this file's, for
    // the same reason as the rectangle below: it has cases, and cases are checked
    // without starting the engine (ADR 0011).
    //
    // Both callers used to pass an unconditional none, because nothing in the
    // snapshot was thought to say when a creature is drawing back or being struck.
    // The canonical journal does say it, and BlowReadout is where that reading
    // lives; this file only asks it about one body at a time.
    private string RaiderSpriteKey(PrototypeRaiderSnapshot raider) =>
        BodySprites.RaiderKey(
            raider.Mode,
            raider.ReturningToGate,
            BodyPhase(BodyKind.Raider, raider.Id));

    private string CrewSpriteKey(PrototypeCreatureSnapshot creature) =>
        BodySprites.CrewKey(creature.Mode, BodyPhase(BodyKind.Creature, creature.Id));

    /// <summary>
    /// The pose one body owes to the blow it is in, read off the reading built for
    /// this tick. A body no blow touches gets <see cref="BodyActionPhase.None"/>
    /// from <see cref="BlowReading.PhaseOf"/> and keeps the pose its mode chooses.
    /// </summary>
    private BodyActionPhase BodyPhase(BodyKind kind, int id) =>
        _blows.PhaseOf(new BodyRef(kind, id));

    /// <summary>
    /// Which way every body is turned on the tick that has just been read.
    ///
    /// <para>
    /// Both answers come from what the view already had: the step is the
    /// difference between the cell a body came from — the motion buffer
    /// <see cref="RememberMotionOrigin"/> keeps — and the cell the snapshot puts
    /// it on, and the blow is the reading <see cref="BlowReadout"/> builds from
    /// the canonical journal. No new field on a snapshot, and nothing written
    /// back to one.
    /// </para>
    /// </summary>
    private void TurnBodies()
    {
        foreach (var creature in _state!.Creatures)
        {
            TurnBody(
                new BodyRef(BodyKind.Creature, creature.Id),
                SidewaysStep(creature.Position, _creatureMotionOrigin, creature.Id));
        }

        foreach (var raider in _state.Raiders)
        {
            TurnBody(
                new BodyRef(BodyKind.Raider, raider.Id),
                SidewaysStep(raider.Position, _raiderMotionOrigin, raider.Id));
        }

        // A blow wins over a step, and it wins for both bodies it names. A body
        // that both moved and struck on one tick is turned towards what it struck,
        // for the reason the flinch pose beats the wind-up in
        // BlowReading.PhaseOf: the blow is the thing the player is being asked to
        // read. The body being struck is turned by the same rule and for the same
        // reason — until Issue #259 it kept whatever its own step had left it
        // with, which on the duel scene is a body standing with its back to the
        // spear.
        foreach (var blow in _blows.Blows)
        {
            if (blow.Attacker is { } attacker &&
                BodyPosition(attacker) is { } from &&
                BodyPosition(blow.Target) is { } to)
            {
                TurnExchange(attacker, blow.Target, to.X - from.X);
            }
        }
    }

    private void TurnBody(BodyRef body, double dx) =>
        _bodyFacing[body] = BodyMotion.Turn(BodyFacingOf(body), dx);

    /// <summary>
    /// Turns the two bodies of one blow. The decision is
    /// <see cref="BodyMotion.TurnToExchange"/>'s, including what a blow struck
    /// along a column answers; this hands it the two facings and the difference
    /// between the struck body's cell and the striker's, and writes back the pair
    /// it gets.
    /// </summary>
    private void TurnExchange(BodyRef attacker, BodyRef target, double dx)
    {
        var facing = BodyMotion.TurnToExchange(
            BodyFacingOf(attacker),
            BodyFacingOf(target),
            dx);
        _bodyFacing[attacker] = facing.Attacker;
        _bodyFacing[target] = facing.Target;
    }

    private BodyFacing BodyFacingOf(BodyRef body) =>
        _bodyFacing.TryGetValue(body, out var facing) ? facing : BodyMotion.RestingFacing;

    /// <summary>
    /// How far sideways a body moved into the cell it is on. Zero when it stood
    /// still, and zero when the step was straight up or down — which is a facing
    /// this method has nothing to say about rather than a facing to the right.
    /// </summary>
    private static int SidewaysStep(
        GridPoint position,
        Dictionary<int, GridPoint> origins,
        int id) =>
        origins.TryGetValue(id, out var origin) ? position.X - origin.X : 0;

    /// <summary>
    /// The bodies the picture draws. Everybody, unless the duel scene of ADR 0020
    /// is running — then the two the blow names and nobody else.
    ///
    /// <para>
    /// <b>Why the scene hides bodies rather than framing them out.</b> ADR 0020
    /// asks the probe for «сцена один на один, крупно» and says why in the same
    /// sentence: «качество самой анимации важнее поведения в толпе на этом шаге».
    /// The shipped raid journal has no such moment to point a camera at — the crew
    /// musters as a block, and over the whole session the emptiest recorded blow
    /// still has three other standing bodies within two cells of it (measured;
    /// the number is <c>duel.crowd</c> in a run's own view state). So a scene
    /// built only out of the camera would be the stack of bodies the review of
    /// vertical 3 rejected, and rightly.
    /// </para>
    ///
    /// <para>
    /// It changes pixels and nothing else: the hidden bodies go on fighting, keep
    /// their hit points and reach the same checksum, which is the whole difference
    /// between a scene and a rule. Off by default and reachable only through
    /// <c>--demo-duel</c>.
    /// </para>
    /// </summary>
    private IEnumerable<PrototypeCreatureSnapshot> SceneCreatures() =>
        _state!.Creatures.Where(creature =>
            IsInScene(new BodyRef(BodyKind.Creature, creature.Id)));

    /// <inheritdoc cref="SceneCreatures"/>
    private IEnumerable<PrototypeRaiderSnapshot> SceneRaiders() =>
        _state!.Raiders.Where(raider =>
            raider.Mode != RaiderMode.Escaped &&
            IsInScene(new BodyRef(BodyKind.Raider, raider.Id)));

    private bool IsInScene(BodyRef body) =>
        _duelPair is not { } duel || body == duel.Attacker || body == duel.Target;

    /// <summary>
    /// Whether this body is drawn favouring a leg — «хромающая походка» of pitch
    /// 6.13 (Issue #409). Read off the published snapshot and off nothing else,
    /// so the gait is a projection of the domain rather than a state the view
    /// keeps: a replay draws the same walk.
    ///
    /// <para>Raiders never limp, because raiders carry no localised wound in the
    /// snapshot. That is the model's shape and not an omission here — a scar is
    /// what a raider carries, and it is drawn as a caption.</para>
    /// </summary>
    private bool IsLimping(BodyRef body) =>
        body.Kind == BodyKind.Creature &&
        _state!.Creatures
            .FirstOrDefault(creature => creature.Id == body.Id)
            ?.Injuries.Any(injury => injury.Part == BodyPart.Leg) == true;

    private GridPoint? BodyPosition(BodyRef body) =>
        body.Kind == BodyKind.Creature
            ? _state!.Creatures
                .FirstOrDefault(creature => creature.Id == body.Id)?.Position
            : _state!.Raiders
                .FirstOrDefault(raider => raider.Id == body.Id)?.Position;

    /// <summary>
    /// Puts the canvas into the frame one body is drawn in: the origin on its
    /// feet, and the body turned the way <see cref="TurnBodies"/> last left it.
    ///
    /// <para>
    /// It is a transform rather than a rectangle computed per call because the
    /// same frame has to hold three drawings of the same body — the side outline,
    /// the sprite and the blow flash — and they are made in two different passes
    /// (<see cref="WorldDrawOrder"/>). A flip applied to one of them and not to
    /// the others is a body wearing somebody else's silhouette, which is exactly
    /// the defect <c>BlowAdapterTests</c> already holds the flash to.
    /// </para>
    ///
    /// <para>
    /// The feet are the pivot for the same reason <see cref="CameraView.GoblinDrawRect"/>
    /// stands the sprite on <see cref="CameraView.GoblinFootLine"/>: a body may
    /// grow, turn and lean, but the ground it stands on is not the drawing's to
    /// move.
    /// </para>
    /// </summary>
    private void PushBodyPose(Vector2 center, BodyRef body)
    {
        var (from, to) = BodyStep(body);
        var alpha = MotionAlpha();
        var beat = TickAlpha();
        var phase = BodyPhase(body.Kind, body.Id);
        var bob = ScaleWorld((float)BodyMotion.BobOffsetRef(
            BodyMotion.PathCells(from, to, alpha),
            from != to,
            IsLimping(body)));
        var axis = BlowAxis(body);
        _bodyFrame = new Transform2D(
            (float)BodyMotion.LeanRadians(to.X - from.X) + StrikeLean(phase, axis, beat),
            new Vector2(
                (float)(BodyMotion.FlipScale(BodyFacingOf(body)) *
                    BodyMotion.BlowWidthScale(phase, alpha)),
                (float)BodyMotion.BlowHeightScale(phase, alpha)),
            0f,
            center +
                new Vector2(0f, (float)CameraView.GoblinFootLine(_tileSize) + bob) +
                BodyRecoil(phase, axis, beat));
        DrawSetTransformMatrix(_bodyFrame);
    }

    /// <summary>
    /// The line one blow of this tick travels along, for a body that is one of
    /// its two ends: <b>from the striker towards what it struck</b>, as a unit
    /// vector, or <c>null</c> for a body no recorded blow touches.
    ///
    /// <para>
    /// The direction is the whole of the polarity this Issue's second mutant
    /// attacks. Subtracted the other way round, <see cref="BodyRecoil"/> pulls a
    /// striker <em>into</em> the thing it just speared and throws its target
    /// towards the spear — which compiles, draws a whole fight and looks, at a
    /// glance, like an animation.
    /// <c>StrikeAdapterTests.The_recoil_of_a_blow_runs_from_the_striker_towards_the_struck</c>
    /// is what refuses it.
    /// </para>
    ///
    /// <para>
    /// Cells, not drawn centres. The drawn centre already carries the recoil this
    /// answer feeds, and a direction that fed on its own output would drift a
    /// little further every frame.
    /// </para>
    /// </summary>
    private Vector2? BlowAxis(BodyRef body)
    {
        foreach (var blow in _blows.Blows)
        {
            if (blow.Attacker is not { } attacker ||
                (attacker != body && blow.Target != body) ||
                BodyPosition(attacker) is not { } from ||
                BodyPosition(blow.Target) is not { } to)
            {
                continue;
            }

            var axis = new Vector2(to.X - from.X, to.Y - from.Y);
            if (axis != Vector2.Zero)
            {
                return axis.Normalized();
            }
        }

        return null;
    }

    /// <summary>
    /// How far this body is thrown along that line, in world pixels. Both ends of
    /// a blow move: <see cref="StrikeChain.RecoilOffsetRef"/> answers negative for
    /// the striker after contact and positive for the body it struck, and the one
    /// axis below turns those two numbers into two bodies moving apart.
    /// </summary>
    private Vector2 BodyRecoil(BodyActionPhase phase, Vector2? axis, float tickAlpha) =>
        axis is { } direction
            ? direction * ScaleWorld((float)StrikeChain.RecoilOffsetRef(
                StrikeChain.RoleOf(phase),
                tickAlpha))
            : Vector2.Zero;

    /// <summary>
    /// How far the whole body tips into the blow, in radians.
    ///
    /// <para>
    /// A rotation of the body's own frame and never a rotation of the torso part
    /// against the legs: nothing moves relative to anything, so a lean of any size
    /// costs no seam at all. That is not a convenience — turning the torso against
    /// the legs opens the widest gap of any joint in this rig, which is what
    /// <c>evidence/244-measure-rig-gaps.py</c> measured before the chain was
    /// written.
    /// </para>
    /// </summary>
    private static float StrikeLean(BodyActionPhase phase, Vector2? axis, float tickAlpha) =>
        axis is { } direction
            ? (float)(StrikeChain.LeanDegrees(phase, tickAlpha) * Math.PI / 180.0) *
                direction.X
            : 0f;

    /// <summary>
    /// The step a body is in the middle of: the cell it left when the tick being
    /// drawn started, and the cell the snapshot puts it on. They are the same cell
    /// when the body did not move, and that is what "walking" means here — the
    /// body's own two cells, not a mode, not a clock.
    /// </summary>
    private (GridPoint From, GridPoint To) BodyStep(BodyRef body)
    {
        var to = BodyPosition(body) ?? default;
        var origins = body.Kind == BodyKind.Creature
            ? _creatureMotionOrigin
            : _raiderMotionOrigin;
        return (origins.TryGetValue(body.Id, out var from) ? from : to, to);
    }

    /// <summary>Back to the canvas everything else is drawn in.</summary>
    private void ClearBodyPose()
    {
        _bodyFrame = Transform2D.Identity;
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    /// <summary>
    /// The rectangle a body's sprite is drawn into, in the frame
    /// <see cref="PushBodyPose"/> establishes — the same
    /// <see cref="CameraView.GoblinDrawRect"/> as before, asked about a body whose
    /// feet are at the origin.
    /// </summary>
    private Rect2 BodyLocalRect() =>
        ToRect2(CameraView.GoblinDrawRect(
            new ViewPoint(0.0, -CameraView.GoblinFootLine(_tileSize)),
            _tileSize));

    /// <summary>
    /// The render centre of a body in that same frame. The two fallbacks below —
    /// the circle drawn when a pose has no sprite and the ring drawn around it —
    /// are still centred exactly where they were before the frame moved to the
    /// feet.
    /// </summary>
    private Vector2 BodyLocalCenter() =>
        new(0f, -(float)CameraView.GoblinFootLine(_tileSize));

    private void DrawGoblin(string key, BodyRef body)
    {
        if (UsesRigPose(key))
        {
            DrawRigBody(key, body, false, Colors.White, Vector2.Zero);
            return;
        }

        if (_goblinSprites.TryGetValue(key, out var sprite))
        {
            // Where the rectangle goes is CameraView's answer, not this method's,
            // so that Issue #77's 170 %, the placement it grows by and the 17:12
            // shape of the connected pack can be measured without the engine.
            DrawTextureRect(sprite, BodyLocalRect(), false);
            return;
        }

        // Missing exploratory art must not prevent a deterministic playable build.
        _fallbackSpriteDraws++;
        DrawCircle(BodyLocalCenter(), ScaleWorld(6), new Color("#84cc16"));
    }

    private void DrawHpBar(Vector2 topLeft, int hp, int maxHp, Color color)
    {
        var width = ScaleWorld(14);
        var height = ScaleWorld(3);
        DrawRect(new Rect2(topLeft, new Vector2(width, height)), new Color("#0f172a"));
        DrawRect(
            new Rect2(
                topLeft,
                new Vector2(width * Math.Clamp(hp / (float)maxHp, 0, 1), height)),
            color);
    }

    /// <summary>
    /// Every room says what it is for and how it is doing, on the tile it is named
    /// after.
    ///
    /// This replaces five hard-coded captions pinned to five hard-coded tiles of
    /// the four default zones plus the gym. That list could not describe a second
    /// farm, said nothing about a room that was not working, and vanished entirely
    /// if the player erased the one tile it was nailed to.
    /// </summary>
    private void DrawRoomLabels(IReadOnlySet<GridPoint> rockTiles)
    {
        foreach (var room in _state!.Rooms)
        {
            DrawRoomLabel(room, rockTiles);
        }
    }

    /// <summary>
    /// A room whose border is drawn deep because it borders a wall
    /// (<see cref="RoomGeometry.BorderInsetFor"/>, Issues #139 and #147) pushes
    /// its caption and icon down with it via <see cref="RoomGeometry.LabelTop"/>,
    /// so the border does not cut through either — the regression independent
    /// review found in F1 of #139's second round. Both this and
    /// <see cref="DrawRoomBorder"/> read the inset from the same method, so
    /// neither can pick one the other did not.
    /// </summary>
    private void DrawRoomLabel(PrototypeRoomSnapshot room, IReadOnlySet<GridPoint> rockTiles)
    {
        var accent = RoomColor(MapAccents.Room(_projection!, room), room.Purpose);
        var anchor = RoomGeometry.LabelAnchor(room.Perimeter, _tileSize);
        var origin = new Vector2((float)anchor.X, (float)anchor.Y);
        var icon = ScaleWorld((float)RoomGeometry.LabelIconSize);

        var purposeInset =
            RoomGeometry.BorderInsetFor(room.Purpose, room.Perimeter, rockTiles);
        var labelTop = ScaleWorld((float)RoomGeometry.LabelTop(purposeInset));

        DrawRoomIcon(room.Purpose, origin + new Vector2(ScaleWorld(2), labelTop), icon, accent);
        DrawString(
            ThemeDB.FallbackFont,
            origin + new Vector2(ScaleWorld(3) + icon, labelTop + icon),
            RoomLabels.Caption(room, _projection!),
            HorizontalAlignment.Left,
            -1,
            Math.Max(1, (int)Math.Round(ScaleWorld(7))),
            accent);
    }

    /// <summary>
    /// The purpose glyph, scaled out of the unit-square strokes declared in
    /// <c>DungeonFortress.Presentation.RoomIcons</c>. The shape is decided there
    /// because this file is not built by the "Pure .NET" CI job; here it is
    /// multiplied and translated and nothing else.
    /// </summary>
    private void DrawRoomIcon(ZoneKind purpose, Vector2 origin, float size, Color accent)
    {
        foreach (var stroke in RoomIcons.Of(purpose))
        {
            for (var index = 1; index < stroke.Count; index++)
            {
                DrawLine(
                    origin + new Vector2((float)stroke[index - 1].X, (float)stroke[index - 1].Y) * size,
                    origin + new Vector2((float)stroke[index].X, (float)stroke[index].Y) * size,
                    accent,
                    ScaleWorld(1.4f));
            }
        }
    }

    /// <summary>
    /// An object standing outside every room that could use it — the other half of
    /// the silence ADR 0013 names, and the one the shipped fixture starts in: four
    /// training posts in the north store and no gym painted over them.
    ///
    /// A ring and a bar, no fill: the mark lands on the very cell a creature would
    /// be working on if the zone existed.
    /// </summary>
    private void DrawUnroomedObjects()
    {
        foreach (var orphan in RoomObjects.Unroomed(_projection!))
        {
            var center = CellCenter(orphan.Position);
            var accent = new Color("#fbbf24");
            DrawArc(center, ScaleWorld(7.5f), 0, Mathf.Tau, 20, accent, ScaleWorld(1.6f), false);
            DrawLine(
                center + ScaleWorld(0, -4),
                center + ScaleWorld(0, 1.5f),
                accent,
                ScaleWorld(1.8f));
            DrawLine(
                center + ScaleWorld(0, 3),
                center + ScaleWorld(0, 4),
                accent,
                ScaleWorld(1.8f));
        }
    }

    // EditMode used to be declared here. It is DungeonFortress.Presentation's
    // BrushMode now, because everything that has to be said about a brush — its
    // name, its tooltip, which cells a stroke over it would take — is text, and
    // text on this side of the seam is text no test in CI can read.

    // ---------------------------------------------------------------------
    // The world-geometry journal (Issue #295)
    //
    // Every mark the map draws goes through one of the engine primitives
    // declared below. They hide CanvasItem's own methods, so no call site had
    // to be touched: a bare DrawRect(...) written anywhere in this class now
    // resolves here first. While _worldDrawJournal is null — which is every
    // frame a player or a capture ever sees — each of them forwards its
    // arguments to the base method unchanged and does nothing else, so the
    // picture is exactly the picture it was.
    //
    // While it is not null, the primitive records what it was given and
    // returns without drawing. That is what makes the journal capturable
    // outside _Draw at all: Godot refuses a draw command issued anywhere else,
    // and the recording pass of VerifyWorldGeometry runs from _Ready.
    //
    // Why this and not a golden PNG: evidence/295-method-choice.json.
    // ---------------------------------------------------------------------
    private WorldDrawJournal? _worldDrawJournal;

    /// <summary>
    /// Opens one of the four declared passes of
    /// <see cref="WorldDrawOrder"/> in the journal. It draws nothing and is
    /// invisible to a run that is not recording.
    ///
    /// <para>
    /// The boundaries are named in <c>DrawMap</c> rather than derived from a
    /// stack walk on purpose: an inlined routine has no frame, so a stack walk
    /// would attribute a mark to whichever caller the JIT happened to leave
    /// standing, and a reference file built from that would move without the
    /// drawing moving. Named boundaries cost four lines and are the same on
    /// every machine.
    /// </para>
    /// </summary>
    private void BeginWorldDrawPass(WorldDrawPass pass) =>
        _worldDrawJournal?.BeginPass(pass);

    private new void DrawRect(Rect2 rect, Color color, bool filled = true, float width = -1f)
    {
        if (_worldDrawJournal is { } journal)
        {
            journal.Rect(rect, color, filled, width);
            return;
        }

        base.DrawRect(rect, color, filled, width);
    }

    private new void DrawLine(
        Vector2 from,
        Vector2 to,
        Color color,
        float width = -1f,
        bool antialiased = false)
    {
        if (_worldDrawJournal is { } journal)
        {
            journal.Line(from, to, color, width, antialiased);
            return;
        }

        base.DrawLine(from, to, color, width, antialiased);
    }

    private new void DrawCircle(Vector2 position, float radius, Color color)
    {
        if (_worldDrawJournal is { } journal)
        {
            journal.Circle(position, radius, color);
            return;
        }

        base.DrawCircle(position, radius, color);
    }

    private new void DrawArc(
        Vector2 center,
        float radius,
        float startAngle,
        float endAngle,
        int pointCount,
        Color color,
        float width = -1f,
        bool antialiased = false)
    {
        if (_worldDrawJournal is { } journal)
        {
            journal.Arc(
                center, radius, startAngle, endAngle, pointCount, color, width, antialiased);
            return;
        }

        base.DrawArc(center, radius, startAngle, endAngle, pointCount, color, width, antialiased);
    }

    private new void DrawPolyline(
        Vector2[] points,
        Color color,
        float width = -1f,
        bool antialiased = false)
    {
        if (_worldDrawJournal is { } journal)
        {
            journal.Polyline(points, color, width, antialiased);
            return;
        }

        base.DrawPolyline(points, color, width, antialiased);
    }

    private new void DrawTextureRect(
        Texture2D texture,
        Rect2 rect,
        bool tile,
        Color? modulate = null,
        bool transpose = false)
    {
        if (_worldDrawJournal is { } journal)
        {
            journal.TextureRect(texture, rect, tile, modulate, transpose);
            return;
        }

        base.DrawTextureRect(texture, rect, tile, modulate, transpose);
    }

    private new void DrawString(
        Font font,
        Vector2 pos,
        string text,
        HorizontalAlignment alignment = (HorizontalAlignment)0,
        float width = -1f,
        int fontSize = 16,
        Color? modulate = null,
        TextServer.JustificationFlag justificationFlags = (TextServer.JustificationFlag)3,
        TextServer.Direction direction = (TextServer.Direction)0,
        TextServer.Orientation orientation = (TextServer.Orientation)0)
    {
        if (_worldDrawJournal is { } journal)
        {
            journal.Text(
                "String", pos, alignment, width, fontSize, 0, modulate,
                justificationFlags, direction, orientation);
            return;
        }

        base.DrawString(
            font, pos, text, alignment, width, fontSize, modulate,
            justificationFlags, direction, orientation);
    }

    private new void DrawStringOutline(
        Font font,
        Vector2 pos,
        string text,
        HorizontalAlignment alignment = (HorizontalAlignment)0,
        float width = -1f,
        int fontSize = 16,
        int size = 1,
        Color? modulate = null,
        TextServer.JustificationFlag justificationFlags = (TextServer.JustificationFlag)3,
        TextServer.Direction direction = (TextServer.Direction)0,
        TextServer.Orientation orientation = (TextServer.Orientation)0)
    {
        if (_worldDrawJournal is { } journal)
        {
            journal.Text(
                "StringOutline", pos, alignment, width, fontSize, size, modulate,
                justificationFlags, direction, orientation);
            return;
        }

        base.DrawStringOutline(
            font, pos, text, alignment, width, fontSize, size, modulate,
            justificationFlags, direction, orientation);
    }

    private new void DrawSetTransformMatrix(Transform2D xform)
    {
        if (_worldDrawJournal is { } journal)
        {
            journal.TransformMatrix(xform);
            return;
        }

        base.DrawSetTransformMatrix(xform);
    }

    private new void DrawSetTransform(Vector2 position, float rotation = 0f, Vector2? scale = null)
    {
        if (_worldDrawJournal is { } journal)
        {
            journal.Transform(position, rotation, scale);
            return;
        }

        base.DrawSetTransform(position, rotation, scale);
    }

    /// <summary>
    /// The ordered record of one <c>DrawMap</c>, pass by pass.
    ///
    /// <para>
    /// It keeps two kinds of thing about every pass. One is a digest of the
    /// whole ordered stream of calls. The other is the handful of numbers a
    /// person can read in a diff: how many calls of each primitive, the
    /// rectangle the pass painted inside, and the stroke widths and radii it
    /// used. A change that moves only the digest says "something moved"; the
    /// readable numbers usually say what.
    /// </para>
    ///
    /// <para>
    /// <b>What goes into the digest, exactly.</b> Every argument of every
    /// primitive except two, and the two are named rather than left to be
    /// discovered: the <em>string</em> a text primitive draws, and the
    /// <see cref="Font"/> object it draws with. The text is golden UI's
    /// business (<c>tests/golden/ui</c>) and repeating it here would put the
    /// same caption in two reference files held by two different Issues; the
    /// font is one shared <c>ThemeDB.FallbackFont</c> instance with no stable
    /// identity to record. Everything else — alignment, wrap width, outline
    /// size, justification, direction, orientation, tiling, transposition,
    /// antialiasing — is in, because each of them moves or reshapes a mark.
    /// </para>
    ///
    /// <para>
    /// This paragraph is narrow because the first version of it was not, and
    /// review measured the difference: it claimed "every argument" while
    /// <c>alignment</c>, the wrap width, the outline size and
    /// <c>transpose</c> were dropped, and proved the claim empty with a mutant
    /// — <c>HorizontalAlignment.Left</c> to <c>Center</c> in
    /// <c>DrawSelectionCount</c> physically moves the caption across a 52 px
    /// box and the record did not notice. That is the same defect this Issue
    /// exists to close, wearing the detector's own clothes.
    /// </para>
    /// </summary>
    private sealed class WorldDrawJournal
    {
        private readonly List<PassJournal> _passes = [];
        private PassJournal? _current;

        internal IReadOnlyList<PassJournal> Passes => _passes;

        internal void BeginPass(WorldDrawPass pass)
        {
            _current = new PassJournal(pass);
            _passes.Add(_current);
        }

        internal void Rect(Rect2 rect, Color color, bool filled, float width)
        {
            var pass = Current();
            pass.Point(rect.Position);
            pass.Point(rect.End);
            pass.Size(width);
            pass.Call(
                "Rect",
                Number(rect.Position.X), Number(rect.Position.Y),
                Number(rect.Size.X), Number(rect.Size.Y),
                Paint(color), filled ? "filled" : "outline", Number(width));
        }

        internal void Line(
            Vector2 from,
            Vector2 to,
            Color color,
            float width,
            bool antialiased)
        {
            var pass = Current();
            pass.Point(from);
            pass.Point(to);
            pass.Size(width);
            pass.Call(
                "Line",
                Number(from.X), Number(from.Y), Number(to.X), Number(to.Y),
                Paint(color), Number(width), Flag(antialiased));
        }

        internal void Circle(Vector2 position, float radius, Color color)
        {
            var pass = Current();
            pass.Point(position);
            pass.Size(radius);
            pass.Call("Circle", Number(position.X), Number(position.Y), Number(radius), Paint(color));
        }

        internal void Arc(
            Vector2 center,
            float radius,
            float startAngle,
            float endAngle,
            int pointCount,
            Color color,
            float width,
            bool antialiased)
        {
            var pass = Current();
            pass.Point(center);
            pass.Size(radius);
            pass.Size(width);
            pass.Call(
                "Arc",
                Number(center.X), Number(center.Y), Number(radius),
                Number(startAngle), Number(endAngle),
                pointCount.ToString(CultureInfo.InvariantCulture),
                Paint(color), Number(width), Flag(antialiased));
        }

        internal void Polyline(Vector2[] points, Color color, float width, bool antialiased)
        {
            var pass = Current();
            var parts = new List<string> { "Polyline" };
            foreach (var point in points)
            {
                pass.Point(point);
                parts.Add(Number(point.X));
                parts.Add(Number(point.Y));
            }

            pass.Size(width);
            parts.Add(Paint(color));
            parts.Add(Number(width));
            parts.Add(Flag(antialiased));
            pass.Call([.. parts]);
        }

        internal void TextureRect(
            Texture2D texture,
            Rect2 rect,
            bool tile,
            Color? modulate,
            bool transpose)
        {
            var pass = Current();
            pass.Point(rect.Position);
            pass.Point(rect.End);
            pass.Call(
                "TextureRect",
                texture.ResourcePath.Length == 0 ? "generated" : texture.ResourcePath,
                Number(rect.Position.X), Number(rect.Position.Y),
                Number(rect.Size.X), Number(rect.Size.Y),
                tile ? "tiled" : "stretched",
                modulate is { } tint ? Paint(tint) : "none",
                transpose ? "transposed" : "upright");
        }

        /// <summary>
        /// Text is journalled by everything except what it says: the caption of
        /// a room is golden UI's business (<c>tests/golden/ui</c>), and
        /// repeating it here would put the same string in two reference files
        /// held by two different Issues.
        ///
        /// <para>
        /// Everything else about it is geometry and is recorded. Where it sits
        /// — <c>RoomGeometry.LabelTop</c> moves it with the border's inset. How
        /// it is aligned inside <paramref name="width"/>, which decides where
        /// the glyphs actually land: <c>DrawSelectionCount</c> centres a count
        /// in a 52 px box, and switching that to <c>Left</c> slides the caption
        /// across the box while the position argument does not move at all.
        /// The outline <paramref name="size"/>, which is how thick a halo is
        /// drawn round a name. And the three text-server flags, which decide
        /// justification, direction and orientation.
        /// </para>
        /// </summary>
        internal void Text(
            string primitive,
            Vector2 position,
            HorizontalAlignment alignment,
            float width,
            int fontSize,
            int size,
            Color? modulate,
            TextServer.JustificationFlag justificationFlags,
            TextServer.Direction direction,
            TextServer.Orientation orientation)
        {
            var pass = Current();
            pass.Point(position);
            pass.Size(fontSize);
            pass.Size(width);
            pass.Size(size);
            pass.Call(
                primitive,
                Number(position.X), Number(position.Y),
                alignment.ToString(),
                Number(width),
                fontSize.ToString(CultureInfo.InvariantCulture),
                size.ToString(CultureInfo.InvariantCulture),
                modulate is { } tint ? Paint(tint) : "none",
                ((int)justificationFlags).ToString(CultureInfo.InvariantCulture),
                direction.ToString(),
                orientation.ToString());
        }

        internal void TransformMatrix(Transform2D xform) =>
            Current().Call(
                "SetTransform",
                Number(xform.X.X), Number(xform.X.Y),
                Number(xform.Y.X), Number(xform.Y.Y),
                Number(xform.Origin.X), Number(xform.Origin.Y));

        internal void Transform(Vector2 position, float rotation, Vector2? scale)
        {
            var applied = scale ?? Vector2.One;
            Current().Call(
                "SetTransform",
                Number(position.X), Number(position.Y), Number(rotation),
                Number(applied.X), Number(applied.Y));
        }

        private PassJournal Current() =>
            _current ?? throw new InvalidOperationException(
                "A world mark was drawn outside every declared pass. DrawMap opens " +
                "each pass of WorldDrawOrder with BeginWorldDrawPass, so a mark " +
                "drawn before the first of them belongs to no pass at all.");

        private static string Paint(Color color) => color.ToHtml(true);

        private static string Flag(bool value) => value ? "on" : "off";

        internal static string Number(double value)
        {
            var rounded = Math.Round(value, 3, MidpointRounding.AwayFromZero);
            return (rounded == 0 ? 0 : rounded).ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>One pass of one recorded <c>DrawMap</c>.</summary>
    private sealed class PassJournal(WorldDrawPass pass)
    {
        private readonly StringBuilder _stream = new();
        private readonly SortedDictionary<string, int> _primitives = new(StringComparer.Ordinal);
        private readonly SortedSet<double> _sizes = [];
        private double _minX = double.PositiveInfinity;
        private double _minY = double.PositiveInfinity;
        private double _maxX = double.NegativeInfinity;
        private double _maxY = double.NegativeInfinity;

        internal WorldDrawPass Pass { get; } = pass;

        internal int Calls { get; private set; }

        internal IReadOnlyDictionary<string, int> Primitives => _primitives;

        internal IReadOnlyCollection<double> Sizes => _sizes;

        internal bool HasExtent => Calls > 0 && double.IsFinite(_minX);

        internal double[] Extent =>
            HasExtent ? [_minX, _minY, _maxX, _maxY] : [];

        internal void Point(Vector2 point)
        {
            _minX = Math.Min(_minX, RoundedOf(point.X));
            _minY = Math.Min(_minY, RoundedOf(point.Y));
            _maxX = Math.Max(_maxX, RoundedOf(point.X));
            _maxY = Math.Max(_maxY, RoundedOf(point.Y));
        }

        /// <summary>
        /// A stroke width or a radius. Godot's "use the default" is a negative
        /// width and says nothing about geometry, so it is not collected.
        /// </summary>
        internal void Size(double value)
        {
            if (value > 0)
            {
                _sizes.Add(RoundedOf(value));
            }
        }

        internal void Call(params string[] parts)
        {
            Calls++;
            _primitives[parts[0]] = _primitives.TryGetValue(parts[0], out var seen) ? seen + 1 : 1;
            _stream.Append(string.Join(' ', parts)).Append('\n');
        }

        internal string Digest()
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(_stream.ToString()));
            return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        }

        private static double RoundedOf(double value)
        {
            var rounded = Math.Round(value, 3, MidpointRounding.AwayFromZero);
            return rounded == 0 ? 0 : rounded;
        }
    }

    private sealed record RuntimeDiagnostic(string Scope, string Type, string Message);
}
