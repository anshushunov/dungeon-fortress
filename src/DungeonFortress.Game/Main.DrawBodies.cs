using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

// Drawing the bodies: the cutout rig, the side silhouette, the blow
// flash, sparks and streaks, damage numbers and the information over a body.
public partial class Main
{
    private void DrawCreature(PrototypeCreatureSnapshot creature, Vector2 center)
    {
        // The body and its carried item hang off the interpolated point supplied
        // to Y-order. Informational affordances are projected in a later pass so
        // wall volume can occlude the body without erasing its state.
        var body = new BodyRef(BodyKind.Creature, creature.Id);
        DrawSidedBody(center, CrewSpriteKey(creature), BodyRelation.Own, body);
        if (creature.Carrying is null)
        {
            return;
        }

        // In the body's own frame, because a load is carried by the body: left
        // outside it, a mushroom would hang in the air beside a walking creature
        // and stay on its right while the creature faced left.
        PushBodyPose(center, body);
        var carried = BodyLocalCenter();
        if (creature.Carrying is ResourceKind.Stone)
        {
            // Stone rides as a rimmed grey square, the same shape a stockpile pip
            // uses, so "carrying" and "stored" read as the same material.
            DrawRect(
                new Rect2(carried + ScaleWorld(3, -9), ScaleWorld(6, 6)),
                new Color("#e2e8f0"));
            DrawRect(
                new Rect2(carried + ScaleWorld(3, -9), ScaleWorld(6, 6)),
                new Color("#0f172a"),
                false,
                ScaleWorld(1.0f));
        }
        else
        {
            DrawCircle(
                carried + ScaleWorld(6, -6),
                ScaleWorld(2.5f),
                creature.Carrying == ResourceKind.Meal
                    ? new Color("#fde68a")
                    : new Color("#a3e635"));
        }

        ClearBodyPose();
    }

    private void DrawRaider(PrototypeRaiderSnapshot raider, Vector2 center)
    {
        DrawSidedBody(
            center,
            RaiderSpriteKey(raider),
            BodyRelation.Hostile,
            new BodyRef(BodyKind.Raider, raider.Id));
    }

    /// <summary>
    /// Тело вместе с тем, что говорит, на чьей оно стороне. Контур идёт перед
    /// спрайтом, спрайт поверх — наружу выходит только бахрома.
    ///
    /// <para>
    /// Заменяет кольцо стороны Issue #177. Кольцо было видимым, но при клетке
    /// 40 px имело диаметр 98.18 px против 40 px клетки, которую занимает одно
    /// тело: девять тел в куче превращали карту в наложенные дуги. Контур
    /// занимает площадь силуэта, поэтому плотность толпы его не ломает, и
    /// выводится он из альфы спрайта — значит любой будущий пак рас получает
    /// индикацию без ручной разметки. Спека — docs/design/SIDE_INDICATOR.md.
    /// </para>
    /// </summary>
    private void DrawSidedBody(
        Vector2 center,
        string key,
        BodyRelation relation,
        BodyRef body)
    {
        // Both drawings go into the body's own frame, so the outline is the
        // silhouette of the body as it is actually turned rather than of the body
        // the art was drawn as.
        PushBodyPose(center, body);
        DrawGoblinOutline(key, relation, body);
        DrawGoblin(key, body);
        ClearBodyPose();
    }

    /// <summary>
    /// Whether this pose is drawn from the cutout rig of ADR 0020 rather than
    /// from one of the flat states of the connected pack.
    ///
    /// <para>
    /// <see cref="BodyRig.RiggedStates"/> is the list, and it deliberately leaves
    /// <c>work</c> and <c>downed</c> out: ADR 0020's probe is about the blow, and
    /// the Issue's non-goals say the other states are not converted. A run whose
    /// rig did not load falls back to the flat pack for everything, which is the
    /// same standing the missing-sprite fallback already has — art must not stop a
    /// deterministic build.
    /// </para>
    /// </summary>
    private bool UsesRigPose(string key) =>
        !_flatBody &&
        _bodyRig is not null &&
        _missingRigParts.Count == 0 &&
        BodyRig.RiggedStates.Contains(key, StringComparer.Ordinal);

    /// <summary>
    /// Where every part of the rig is, for one body at one moment: its own frame
    /// inside the body's frame, and the rectangle its texture is drawn into.
    ///
    /// <para>
    /// <b>The order is the rig's.</b> The list is walked in
    /// <see cref="BodyRig.LayerOrder"/>, which <c>BodyRigTests</c> holds to the
    /// rig file's own <c>z_index</c>. Swapping two names there puts an arm inside
    /// a chest, and it fails a check rather than a frame.
    /// </para>
    ///
    /// <para>
    /// <b>The frames are resolved parent first, and the layers are drawn back to
    /// front.</b> They are not the same order — <c>leg_far</c> is drawn before the
    /// <c>torso</c> it hangs off — and building both from one loop is how a child
    /// silently loses its parent's rotation.
    /// </para>
    /// </summary>
    private IReadOnlyList<(Transform2D Frame, Rect2 Rect, string Part)> RigLayout(
        string key,
        BodyRef body)
    {
        var rig = _bodyRig!;
        var canvas = BodyLocalRect();
        var scale = canvas.Size.X / (float)CameraView.SpriteCanvasWidth;
        var phase = BodyPhase(body.Kind, body.Id);
        var beat = TickAlpha();
        var frames = new Dictionary<string, Transform2D>(StringComparer.Ordinal);

        Transform2D FrameOf(string name)
        {
            if (frames.TryGetValue(name, out var known))
            {
                return known;
            }

            var part = rig.Part(name);
            var pose = StrikeChain.PoseOf(phase, name, beat);
            var pivot = RigLocalPoint(part.Joint, canvas, scale);
            var slide = new Vector2(
                (float)(pose.OffsetX * rig.SourceToCanvas) * scale,
                (float)(pose.OffsetY * rig.SourceToCanvas) * scale);
            var turn = (float)(pose.Degrees * Math.PI / 180.0);
            // x -> R(x - pivot) + pivot + slide, written as one matrix.
            var own = new Transform2D(turn, pivot + slide - pivot.Rotated(turn));
            var frame = part.Parent is { } parent ? FrameOf(parent) * own : own;
            frames[name] = frame;
            return frame;
        }

        var armed = BodyRig.ArmedStates.Contains(key, StringComparer.Ordinal);
        var layout = new List<(Transform2D, Rect2, string)>();
        foreach (var name in BodyRig.LayerOrder)
        {
            if (!armed && string.Equals(name, BodyRig.WeaponPart, StringComparison.Ordinal))
            {
                continue;
            }

            var part = rig.Part(name);
            var size = _rigParts.TryGetValue(name, out var texture)
                ? new Vector2(texture.GetWidth(), texture.GetHeight())
                : Vector2.Zero;
            layout.Add((
                FrameOf(name),
                new Rect2(
                    RigLocalPoint(part.RestPosition, canvas, scale),
                    size * (float)rig.SourceToCanvas * scale),
                name));
        }

        return layout;
    }

    /// <summary>
    /// A point of the rig's source cell, in the local pixels of the body frame.
    /// The conversion is <see cref="BodyRig.CanvasPointOf"/> and a scale, so the
    /// rig lands inside exactly the rectangle the flat pack was drawn into and the
    /// two bodies stand on the same line.
    /// </summary>
    private Vector2 RigLocalPoint(ViewPoint source, Rect2 canvas, float scale)
    {
        var point = _bodyRig!.CanvasPointOf(source);
        return canvas.Position + (new Vector2((float)point.X, (float)point.Y) * scale);
    }

    /// <summary>
    /// The body itself, part by part, in the depth pass. <paramref name="shift"/>
    /// is the side outline's offset copy and is zero for the body proper.
    /// </summary>
    private void DrawRigBody(
        string key,
        BodyRef body,
        bool silhouette,
        Color tint,
        Vector2 shift)
    {
        var textures = silhouette ? _rigPartSilhouettes : _rigParts;
        foreach (var (frame, rect, part) in RigLayout(key, body))
        {
            if (!textures.TryGetValue(part, out var texture))
            {
                continue;
            }

            DrawSetTransformMatrix(_bodyFrame.TranslatedLocal(shift) * frame);
            DrawTextureRect(texture, rect, false, tint);
        }

        DrawSetTransformMatrix(_bodyFrame);
    }

    /// <summary>
    /// Восемь смещённых копий белого силуэта позы в цвете отношения. Смещения,
    /// цвет и ширина — <see cref="SideOutline"/>: адаптер не решает, как
    /// выглядит сторона, потому что решение, принятое здесь, принято там, где
    /// его не проверяет джоб «Pure .NET» (ADR 0011).
    /// </summary>
    private void DrawGoblinOutline(string key, BodyRelation relation, BodyRef body)
    {
        var color = new Color(SideOutline.Color(relation));
        var width = ScaleWorld(SideOutline.WidthRef(relation));
        if (UsesRigPose(key))
        {
            // The outline of the pose the rig is actually in, not of the pose the
            // art was drawn as: the same claim the flat path already makes, one
            // level further down. Eight offset copies of the whole assembly.
            foreach (var (x, y) in SideOutline.Offsets)
            {
                DrawRigBody(key, body, true, color, new Vector2(x * width, y * width));
            }

            return;
        }

        if (!_goblinSilhouettes.TryGetValue(key, out var silhouette))
        {
            // Пак не загрузился: DrawGoblin рисует зелёный кружок-заглушку,
            // одинаковый для обеих сторон, и обводить нечего. Кольцо в цвете
            // отношения — минимум, чтобы сторона читалась и здесь. До этой
            // задачи её рисовал DrawArc безусловно, и терять признак на том
            // самом пути, ради которого существует счётчик
            // _fallbackSpriteDraws, было бы регрессией.
            DrawArc(BodyLocalCenter(), ScaleWorld(9), 0, Mathf.Tau, 16, color, width);
            return;
        }

        var rect = BodyLocalRect();
        foreach (var (x, y) in SideOutline.Offsets)
        {
            DrawTextureRect(
                silhouette,
                new Rect2(rect.Position + new Vector2(x * width, y * width), rect.Size),
                false,
                color);
        }
    }

    private void DrawBodyInformationOverlays()
    {
        var creatureCenters = SceneCreatures().ToDictionary(
            creature => creature.Id,
            CreatureRenderCenter);
        var raiderCenters = SceneRaiders()
            .ToDictionary(raider => raider.Id, RaiderRenderCenter);
        var creatures = SceneCreatures().ToDictionary(creature => creature.Id);
        var raiders = SceneRaiders().ToDictionary(raider => raider.Id);
        var items = creatureCenters
            .Select(pair => WorldRenderGeometry.ForBody(
                WorldRenderKind.Creature,
                pair.Key,
                new ViewPoint(pair.Value.X, pair.Value.Y)))
            .Concat(raiderCenters.Select(pair => WorldRenderGeometry.ForBody(
                WorldRenderKind.Raider,
                pair.Key,
                new ViewPoint(pair.Value.X, pair.Value.Y))));

        // Under the bodies' own readouts and above every body: a blow joins two of
        // them, so it belongs to neither one's Y-order slot.
        DrawBlowStreaks(creatureCenters, raiderCenters);
        // And the spark on top of the streak, because the streak is where the blow
        // came from and the spark is where it arrived.
        DrawContactSparks(creatureCenters, raiderCenters);

        foreach (var item in WorldRenderOrder.BackToFront(items))
        {
            switch (item.Kind)
            {
                case WorldRenderKind.Creature:
                    DrawCreatureInformation(
                        creatures[item.StableId],
                        creatureCenters[item.StableId]);
                    break;
                case WorldRenderKind.Raider:
                    DrawRaiderInformation(
                        raiders[item.StableId],
                        raiderCenters[item.StableId]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(item.Kind), item.Kind, null);
            }
        }
    }

    /// <summary>
    /// The struck body lights up in its own silhouette.
    ///
    /// <para>
    /// It is drawn above the depth pass, for the reason the HP bar is: the first
    /// review round of Issue #83 found a raised wall top hiding a body's readout
    /// completely while the body itself stayed visible, and a flash is a readout of
    /// the same kind. Bodies stack too — three raiders share one larder tile in the
    /// first wave of the shipped journal, and the one that is struck is not always
    /// the one drawn last — so a flash left in the depth pass would be erased by
    /// the neighbour standing on the same cell.
    /// </para>
    ///
    /// <para>
    /// The silhouette is the one <see cref="DrawGoblinOutline"/> already builds at
    /// load time: an alpha mask of the pose, so the tint is the shape of the body
    /// rather than the goblin's own light and shade multiplied by a colour. Colour
    /// and opacity come from <see cref="BlowEffects"/>, which is where a decision
    /// with cases can be checked without the engine (ADR 0011).
    /// </para>
    /// </summary>
    private void DrawBlowFlash(Vector2 center, string key, BodyRef body)
    {
        // Nothing before the spear arrives. The flash used to burn for the whole
        // tick, which was right while a blow was one pose and one moment; at the
        // duel's zoom a body lit up ahead of the blow is the first thing an eye
        // finds. StrikeChain.HasLanded is the same instant the strike pose and
        // hit-stop are at, and it still holds at alpha 1 so a captured frame keeps
        // the mark.
        if (_blows.OutcomeOf(body) is not { } outcome ||
            !StrikeChain.HasLanded(TickAlpha()))
        {
            return;
        }

        var colour = new Color(BlowEffects.FlashColor(outcome))
        {
            A = (float)BlowEffects.FlashAlpha(MotionAlpha()),
        };
        // The same frame the body itself is drawn in, and for the same reason the
        // silhouette is the pose's: a flash that ignored the flip would light up
        // a body facing the other way.
        PushBodyPose(center, body);
        if (UsesRigPose(key))
        {
            DrawRigFlash(key, body, colour);
        }
        else if (_goblinSilhouettes.TryGetValue(key, out var silhouette))
        {
            DrawTextureRect(silhouette, BodyLocalRect(), false, colour);
        }

        ClearBodyPose();
    }

    /// <summary>
    /// The rig's own share of the flash: the same silhouettes the side outline
    /// draws, in the same posed frames, tinted by the blow.
    ///
    /// <para>
    /// It is a routine of its own rather than a call into
    /// <see cref="DrawRigBody"/> because the two draw in different passes — the
    /// body in the depth pass, everything a blow says above it — and
    /// <c>WorldDrawPassGuardTests.A_routine_only_calls_routines_of_its_own_pass</c>
    /// is the check that would otherwise be talked out of the very defect it was
    /// written for.
    /// </para>
    /// </summary>
    private void DrawRigFlash(string key, BodyRef body, Color tint)
    {
        foreach (var (frame, rect, part) in RigLayout(key, body))
        {
            if (!_rigPartSilhouettes.TryGetValue(part, out var texture))
            {
                continue;
            }

            DrawSetTransformMatrix(_bodyFrame * frame);
            DrawTextureRect(texture, rect, false, tint);
        }

        DrawSetTransformMatrix(_bodyFrame);
    }

    /// <summary>
    /// The contact effect ADR 0020 asks the probe for: one spark where the blow
    /// arrives, for the window it arrives in.
    ///
    /// <para>
    /// Why a spark and not a splash or a trail is argued in
    /// <see cref="BlowEffects"/>, where the sizes that decide it live: at a body
    /// drawn 61.8 px tall the spark's long ray is 15.5 world px and its stroke is
    /// 2.9, while a believable splash would be a dozen marks each under the pixel
    /// at which a mark stops being one.
    /// </para>
    ///
    /// <para>
    /// A blow whose striker the journal does not name has no spark, for the same
    /// reason it has no streak: the point of contact is a point on a line between
    /// two bodies, and one of the two would be a guess.
    /// </para>
    /// </summary>
    private void DrawContactSparks(
        IReadOnlyDictionary<int, Vector2> creatureCenters,
        IReadOnlyDictionary<int, Vector2> raiderCenters)
    {
        var beat = TickAlpha();
        if (!StrikeChain.ShowsContact(beat))
        {
            return;
        }

        var contact = StrikeChain.ContactAlpha(beat);
        foreach (var blow in _blows.Blows)
        {
            if (blow.Attacker is not { } attacker ||
                BodyCenter(attacker, creatureCenters, raiderCenters) is not { } from ||
                BodyCenter(blow.Target, creatureCenters, raiderCenters) is not { } to ||
                from == to)
            {
                continue;
            }

            var at = ToVector2(BlowEffects.SparkAt(
                new ViewPoint(from.X, from.Y),
                new ViewPoint(to.X, to.Y)));
            var colour = new Color(BlowEffects.SparkColor(blow.Outcome))
            {
                A = (float)BlowEffects.SparkAlpha(contact),
            };
            for (var ray = 0; ray < BlowEffects.SparkRays; ray++)
            {
                var direction = Vector2.FromAngle((float)BlowEffects.SparkRayRadians(ray));
                DrawLine(
                    at + (direction * ScaleWorld((float)BlowEffects.SparkCoreRef)),
                    at + (direction * ScaleWorld((float)BlowEffects.SparkRayRef(ray, contact))),
                    colour,
                    ScaleWorld((float)BlowEffects.SparkWidthRef));
            }
        }
    }

    /// <summary>
    /// Which way each blow of this tick travelled. The stroke is a piece of the
    /// line between the two bodies — <see cref="BlowEffects.Streak"/> decides which
    /// piece — and a blow whose striker the journal does not name has no stroke at
    /// all: an arrow drawn from a guess is indistinguishable on screen from an
    /// arrow drawn from a fact.
    /// </summary>
    private void DrawBlowStreaks(
        IReadOnlyDictionary<int, Vector2> creatureCenters,
        IReadOnlyDictionary<int, Vector2> raiderCenters)
    {
        foreach (var blow in _blows.Blows)
        {
            if (blow.Attacker is not { } attacker ||
                BodyCenter(attacker, creatureCenters, raiderCenters) is not { } from ||
                BodyCenter(blow.Target, creatureCenters, raiderCenters) is not { } to ||
                from == to)
            {
                continue;
            }

            var streak = BlowEffects.Streak(
                new ViewPoint(from.X, from.Y),
                new ViewPoint(to.X, to.Y));
            DrawLine(
                ToVector2(streak.From),
                ToVector2(streak.To),
                new Color(BlowEffects.StreakColor(blow))
                {
                    A = (float)BlowEffects.FlashAlpha(MotionAlpha()),
                },
                ScaleWorld((float)BlowEffects.StreakWidthRef));
        }
    }

    private static Vector2? BodyCenter(
        BodyRef body,
        IReadOnlyDictionary<int, Vector2> creatureCenters,
        IReadOnlyDictionary<int, Vector2> raiderCenters)
    {
        var centers = body.Kind == BodyKind.Creature ? creatureCenters : raiderCenters;
        return centers.TryGetValue(body.Id, out var center) ? center : null;
    }

    /// <summary>
    /// How much this body just lost, over its head. Several blows on one body in
    /// one tick is ordinary — two crew members reach the same raider twice in the
    /// first wave of the shipped journal — so the numbers are laid out side by side
    /// rather than on top of each other.
    /// </summary>
    private void DrawBlowDamage(Vector2 center, BodyRef body)
    {
        var landed = _blows.Struck(body);
        if (landed.Count == 0)
        {
            return;
        }

        var alpha = MotionAlpha();
        var opacity = (float)BlowEffects.DamageAlpha(alpha);
        var size = Math.Max(1, (int)Math.Round(ScaleWorld((float)BlowEffects.DamageTextRef)));
        var width = ScaleWorld(30);
        for (var index = 0; index < landed.Count; index++)
        {
            var blow = landed[index];
            var origin = center + ScaleWorld(
                (float)BlowEffects.DamageSlotOffsetRef(index, landed.Count),
                (float)BlowEffects.DamageOffsetRef(alpha)) - new Vector2(width / 2f, 0);
            var label = BlowEffects.DamageLabel(blow);

            // The rim first: a number without it is unreadable over a goblin, which
            // is the defect the first captured frame of this change showed.
            DrawStringOutline(
                ThemeDB.FallbackFont,
                origin,
                label,
                HorizontalAlignment.Center,
                width,
                size,
                Math.Max(1, (int)Math.Round(ScaleWorld((float)BlowEffects.DamageOutlineRef))),
                new Color(BlowEffects.DamageOutlineColor) { A = opacity });
            DrawString(
                ThemeDB.FallbackFont,
                origin,
                label,
                HorizontalAlignment.Center,
                width,
                size,
                new Color(BlowEffects.DamageColor(blow)) { A = opacity });
        }
    }

    private void DrawCreatureInformation(
        PrototypeCreatureSnapshot creature,
        Vector2 center)
    {
        DrawBlowFlash(center, CrewSpriteKey(creature), new BodyRef(BodyKind.Creature, creature.Id));
        var color = DefenderColor(creature);
        if (creature.Mode == CreatureMode.Downed)
        {
            DrawDownedMark(center);
        }

        DrawHpBar(center + ScaleWorld(-7, 8), creature.Hp, creature.MaxHp, color);
        DrawCircle(
            center + ScaleWorld(7, -7),
            ScaleWorld(2.25f),
            CreatureStateColor(creature));
        DrawInjuryMarks(center, creature, new BodyRef(BodyKind.Creature, creature.Id));

        // The ring round the body the inspector is pointed at. Whether it is drawn
        // at all, and every number in it, is WorldSelectionMark's answer; the
        // adapter multiplies by the tile scale and calls the engine (Issue #364).
        //
        // These lines are repeated in DrawRaiderInformation below rather than
        // shared, for the reason DrawRoomBorderOverWall repeats DrawRoomBorder's
        // loop: a private helper named DrawSomething is a drawing routine, and
        // every drawing routine DrawMap can reach has to be declared in
        // WorldDrawOrder — a manifest this Issue's partition does not hold. Naming
        // it without the prefix would put a drawing body outside every check built
        // on that manifest, which is the escape the manifest's own documentation
        // warns about. Nothing that could drift is duplicated: the condition, the
        // radius, the stroke, the segment count and the colour all come from the
        // one pure call both bodies make.
        if (WorldSelectionMark.IsRinged(
                new WorldLabelSubject(WorldLabelKind.Creature, creature.Id),
                CurrentWorldLabelFocus()))
        {
            DrawArc(
                center,
                ScaleWorld((float)WorldSelectionMark.RadiusRef),
                0,
                Mathf.Tau,
                WorldSelectionMark.Segments,
                new Color(WorldSelectionMark.Color),
                ScaleWorld((float)WorldSelectionMark.StrokeRef));
        }

        DrawBlowDamage(center, new BodyRef(BodyKind.Creature, creature.Id));
    }

    private void DrawRaiderInformation(PrototypeRaiderSnapshot raider, Vector2 center)
    {
        DrawBlowFlash(center, RaiderSpriteKey(raider), new BodyRef(BodyKind.Raider, raider.Id));
        DrawHpBar(
            center + ScaleWorld(-7, 9),
            raider.Hp,
            PrototypeTuning.RaiderHp,
            new Color("#fb7185"));
        if (raider.Mode == RaiderMode.Downed)
        {
            DrawDownedMark(center);
        }

        // Issue #222. A pale dot used to sit at the upper-right corner of every
        // non-downed raider, same corner as the creature state dot below. Unlike
        // that dot, its color never varied with RaiderMode — it was always
        // #fecaca — so it carried no information the red outline (see LEGEND)
        // does not already give. Decision: removed rather than given a legend
        // row, since a row would document a color that means nothing.

        // The other half of Issue #364: until it, a raider could not be selected at
        // all, so the map had no way to say which one the player had chosen. Same
        // rule, same numbers and the same lines as the crew above — the note there
        // says why they are repeated rather than shared.
        if (WorldSelectionMark.IsRinged(
                new WorldLabelSubject(WorldLabelKind.Raider, raider.Id),
                CurrentWorldLabelFocus()))
        {
            DrawArc(
                center,
                ScaleWorld((float)WorldSelectionMark.RadiusRef),
                0,
                Mathf.Tau,
                WorldSelectionMark.Segments,
                new Color(WorldSelectionMark.Color),
                ScaleWorld((float)WorldSelectionMark.StrokeRef));
        }

        DrawBlowDamage(center, new BodyRef(BodyKind.Raider, raider.Id));
    }

    /// <summary>
    /// The wound itself, on the part that carries it (Issue #420).
    ///
    /// <para>
    /// <b>In the body's own frame, because a wound is on the body.</b> The mark is
    /// pushed through <see cref="PushBodyPose"/> exactly like the sprite, the side
    /// outline and the blow flash, so it inherits the flip, the lean, the walking
    /// bob and the recoil of a blow. Drawn in world space beside the body instead —
    /// which is what the HP bar and the state dot do, and rightly, because those
    /// stand <em>next to</em> a body — it would sit still while the limb it names
    /// moved up to 3.3 px away from it.
    /// </para>
    ///
    /// <para>
    /// <b>Every number comes from <see cref="InjuryMarks"/>.</b> Where each part is
    /// on the silhouette, how large the disc is, its colour and the colour of its
    /// rim are decisions with cases, and a decision with cases belongs where the
    /// "Pure .NET" job can check it (ADR 0011). This routine multiplies by the tile
    /// scale and calls the engine, and that is the whole of it.
    /// </para>
    ///
    /// <para>
    /// Own bodies only. A raider carries no localised wound in the snapshot — what
    /// a raider carries is a scar, and that is drawn as a caption
    /// (<see cref="ReturningHeroLabel"/>) — so <see cref="DrawRaiderInformation"/>
    /// has nothing to ask for here. That is the model's shape and not an omission.
    /// </para>
    /// </summary>
    private void DrawInjuryMarks(
        Vector2 center,
        PrototypeCreatureSnapshot creature,
        BodyRef body)
    {
        var marks = InjuryMarks.Of(creature);
        if (marks.Count == 0)
        {
            return;
        }

        PushBodyPose(center, body);
        var radius = ScaleWorld((float)InjuryMarks.RadiusRef);
        var rim = ScaleWorld((float)InjuryMarks.RimWidthRef);
        foreach (var mark in marks)
        {
            var at = BodyLocalCenter() +
                ScaleWorld((float)mark.OffsetRef.X, (float)mark.OffsetRef.Y);
            DrawCircle(at, radius, new Color(mark.Color));
            // The rim after the fill, on the fill's own edge: a wound lands on
            // green skin, on a teal tunic and on a brown boot, and without it the
            // same mark reads as three different marks.
            DrawCircle(at, radius, new Color(InjuryMarks.RimColor), false, rim);
        }

        ClearBodyPose();
    }

    private void DrawDownedMark(Vector2 center)
    {
        DrawLine(
            center + ScaleWorld(-5, -5),
            center + ScaleWorld(5, 5),
            new Color("#f8fafc"),
            ScaleWorld(2));
        DrawLine(
            center + ScaleWorld(5, -5),
            center + ScaleWorld(-5, 5),
            new Color("#f8fafc"),
            ScaleWorld(2));
    }
}
