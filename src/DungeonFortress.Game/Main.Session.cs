using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Godot;

namespace DungeonFortress.Game;

// What a run loads and what it refreshes: sprites and the cutout rig,
// the fixture, one advanced tick, and the labels that follow it.
public partial class Main
{
    private void LoadGoblinSprites()
    {
        _spritesHaveMipmaps = true;
        foreach (var state in BodySprites.States)
        {
            var path = "res://assets/generated/goblins/" + BodySprites.FileName(state);
            if (ResourceLoader.Exists(path) && GD.Load<Texture2D>(path) is { } imported)
            {
                // Overview zooms sample a sprite below its authored draw size.
                // The repository intentionally ignores generated *.import files,
                // so mipmaps are created from the imported image here instead of
                // depending on one editor profile's local import options.
                var image = imported.GetImage();
                var mipmapResult = image.GenerateMipmaps();
                if (mipmapResult != Error.Ok || !image.HasMipmaps())
                {
                    throw new InvalidOperationException(
                        $"Could not generate mipmaps for '{path}': {mipmapResult}.");
                }

                var texture = ImageTexture.CreateFromImage(image);
                _spritesHaveMipmaps &= texture.GetImage().HasMipmaps();
                _goblinSprites.Add(state, texture);
                _goblinSilhouettes.Add(state, BuildSilhouette(imported.GetImage()));
                _loadedSpriteStates.Add(state);
            }
            else
            {
                _spritesHaveMipmaps = false;
                _missingSpriteStates.Add(state);
            }
        }
    }

    /// <summary>
    /// The cutout rig and its parts, read from the asset rather than from a copy
    /// of it in code.
    ///
    /// <para>
    /// The JSON is the contract Issue #243 shipped, and its provenance says in as
    /// many words that this Issue "may convert these source-space values to
    /// runtime scale; it must not retype or replace the pivots".
    /// <see cref="BodyRig.Parse"/> is where a rig this runtime cannot draw — a
    /// missing part, a parent that is not a part, a layer order that is not the
    /// rig's own — becomes a refusal to start instead of a body drawn wrong.
    /// </para>
    /// </summary>
    private void LoadGoblinRig()
    {
        var folder = "res://" + BodyRig.AssetFolder;
        var rigPath = folder + "/" + BodyRig.FileName;
        if (!Godot.FileAccess.FileExists(rigPath))
        {
            _missingRigParts.Add(BodyRig.FileName);
            return;
        }

        _bodyRig = BodyRig.Parse(Godot.FileAccess.GetFileAsString(rigPath));
        foreach (var part in _bodyRig.Parts)
        {
            var path = folder + "/" + part.File;
            if (!ResourceLoader.Exists(path) || GD.Load<Texture2D>(path) is not { } imported)
            {
                _missingRigParts.Add(part.File);
                continue;
            }

            // Mipmaps for the same reason the flat pack has them: the two
            // overview zoom levels sample a part below its authored draw size,
            // and the repository ignores generated *.import files on purpose.
            var image = imported.GetImage();
            var mipmaps = image.GenerateMipmaps();
            if (mipmaps != Error.Ok || !image.HasMipmaps())
            {
                throw new InvalidOperationException(
                    $"Could not generate mipmaps for '{path}': {mipmaps}.");
            }

            _rigParts[part.Name] = ImageTexture.CreateFromImage(image);
            _rigPartSilhouettes[part.Name] = BuildSilhouette(imported.GetImage());
        }
    }

    private void AssertRigLoaded()
    {
        if (_missingRigParts.Count == 0 && _bodyRig is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            "The goblin cutout rig could not be loaded: " +
            string.Join(", ", _missingRigParts) +
            ". Run scripts/run-game.ps1 so its Godot asset-import preflight can " +
            "create the local .godot/import cache.");
    }

    /// <summary>
    /// Та же поза с белым цветом и сохранённой альфой, чтобы отрисовка с
    /// modulate давала плоскую фигуру этого цвета. Байтовый проход, а не
    /// попиксельный: 192x272 на шесть поз — это 313 тысяч пикселей, и делать их
    /// через <c>SetPixel</c> значит платить за загрузку заметную задержку.
    /// </summary>
    private static ImageTexture BuildSilhouette(Image source)
    {
        // Своя копия перед конверсией: Convert правит объект на месте, а
        // GetImage() отдаёт его разделяемым. Что именно разделяемым, доказано
        // этим же кодом — мип-уровни, созданные на строке выше по загрузке,
        // пришли в буфер следующего вызова GetImage(). Сегодня конверсия
        // безвредна (пак уже RGBA8, и она no-op), но первый пак в другом
        // формате превратил бы её в правку чужого кэшированного ресурса.
        var working = Image.CreateFromData(
            source.GetWidth(),
            source.GetHeight(),
            source.HasMipmaps(),
            source.GetFormat(),
            source.GetData());
        working.Convert(Image.Format.Rgba8);
        var data = working.GetData();
        for (var index = 0; index + 3 < data.Length; index += 4)
        {
            data[index] = 255;
            data[index + 1] = 255;
            data[index + 2] = 255;
        }

        // Флаг мип-уровней читается у источника, а не задаётся константой.
        // Загрузка выше уже вызвала GenerateMipmaps, и буфер приходит длиннее
        // одного уровня: 278508 байт против 208896 для 272x192 RGBA8. Жёсткий
        // `false` здесь означал 90 строк ERROR за кадр — «Expected Image data
        // size … got 278508 bytes instead», и поймала это стадия ui.
        // Побеление проходит по всему буферу, включая мип-уровни: формат у них
        // тот же, и уменьшенные копии силуэта обязаны быть такими же белыми.
        var silhouette = Image.CreateFromData(
            working.GetWidth(),
            working.GetHeight(),
            working.HasMipmaps(),
            Image.Format.Rgba8,
            data);
        if (!silhouette.HasMipmaps())
        {
            silhouette.GenerateMipmaps();
        }

        return ImageTexture.CreateFromImage(silhouette);
    }

    private void AssertRequiredSpritesLoaded()
    {
        if (_missingSpriteStates.Count == 0)
        {
            if (_spritesHaveMipmaps)
            {
                return;
            }

            throw new InvalidOperationException(
                "Required goblin sprites loaded without mipmaps, but the camera has zoom levels below 1x.");
        }

        throw new InvalidOperationException(
            "Required goblin sprites could not be loaded: " + string.Join(", ", _missingSpriteStates) +
            ". Run scripts/run-game.ps1 so its Godot asset-import preflight can create the local .godot/import cache.");
    }

    /// <summary>
    /// A name tag pinned to a map cell. These are map annotations rather than
    /// HUD: they follow a creature, so they are positioned in map pixels and are
    /// not part of the Control layout.
    /// </summary>
    private Label MakeMapLabel(Vector2 size, int fontSize, Color color)
    {
        var label = new Label
        {
            Size = size,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        AddChild(label);
        return label;
    }

    private void LoadFixture(string fixture, int ticks)
    {
        if (fixture is not ("baseline" or "prepared" or "neglected"))
        {
            throw new ArgumentException("Fixture must be baseline, prepared or neglected.", nameof(fixture));
        }

        _fixtureLog = PrototypeCommandDocument.Load(FixturePath(fixture));
        _playerCommands.Clear();
        var world = new PrototypeWorld(_fixtureLog);
        var target = Math.Clamp(ticks, 0, PrototypeTuning.SessionTicks);
        // All of it but the last tick. The last one runs on its own below, so the
        // frame knows which way every body just stepped.
        world.RunTicks(Math.Max(0, target - 1));
        _world = world;
        _fixture = fixture;
        _paused = true;
        _tickAccumulator = 0;
        _selectedCreatureId = null;
        _selectedCell = null;
        // A fixture is run in one go, so there is no "before" to measure a fall in
        // hit points against. The journal still names every blow of the last tick,
        // which is what a captured frame is drawn from.
        _creatureHitPointsBefore.Clear();
        _creatureMotionOrigin.Clear();
        _raiderMotionOrigin.Clear();
        _motionOriginPending = false;
        _bodyFacing.Clear();
        _strikeScrub = null;
        _duelPair = null;
        RefreshState();

        // The last tick, with the previous cells kept. A captured frame is drawn
        // at alpha 1 — the canonical position — so this changes nothing about
        // where a body is; what it changes is that the picture can now tell a
        // body that has just walked from one that is standing still, which is the
        // whole of the procedural motion this Issue adds. Canonical state does not
        // notice: the same number of ticks is run either way, and the checksum the
        // capture prints is the evidence (evidence/221-after.json).
        if (target > 0 && !world.IsComplete)
        {
            RememberMotionOrigin();
            world.RunTicks(1);
            RefreshState();
        }
    }

    private void Advance(int ticks)
    {
        if (_world is null || _world.IsComplete)
        {
            return;
        }

        // A tick is a new blow, so the frame the last one was scrubbed to means
        // nothing any more.
        _strikeScrub = null;
        // Presentation only, and here rather than in RememberMotionOrigin because
        // every way of running a tick has to be covered: a raider's blow on a
        // defender that survives is recorded nowhere, so a fall in hit points is
        // the only evidence of it, and a STEP or an accepted command must show it
        // as readily as a running clock does.
        RememberCreatureHitPoints();
        _world.RunTicks(Math.Min(ticks, PrototypeTuning.SessionTicks - _world.CurrentTick));
        RefreshState();
    }

    /// <summary>
    /// What every creature's hit points were before the tick about to run. Written
    /// from snapshots and never read by <see cref="PrototypeWorld"/>.
    /// </summary>
    private void RememberCreatureHitPoints()
    {
        _creatureHitPointsBefore.Clear();
        if (_state is null)
        {
            return;
        }

        foreach (var creature in _state.Creatures)
        {
            _creatureHitPointsBefore[creature.Id] = creature.Hp;
        }
    }

    private void RefreshState()
    {
        _state = _world!.GetSnapshot();
        // One projection per state. Everything that draws or reads the map takes
        // this instance — the map, the brush, the HUD panels and the structured
        // output — so they cannot be looking at four moments of the same tick.
        _projection = MapProjection.Of(_state);
        // Only a refresh that follows RememberMotionOrigin has something to lerp
        // from: a single STEP, an accepted command and a replay are drawn at the
        // canonical position straight away.
        //
        // Loading a fixture used to be in that list and is not any more. Its last
        // tick runs on its own (LoadFixture), so a freshly loaded world does have
        // a previous cell for every body — which is the whole point, because a
        // captured frame is drawn from one. It changes no drawn position while the
        // load is paused, and a load is always paused: MotionAlpha answers 1 while
        // _paused, which is the canonical position. What it does change is the
        // first tick's worth of frames after RUN is pressed, which are now
        // interpolated from that cell like every other tick's; that is the whole
        // of the +4 frames at 20 fps and +10 at 60 fps the frame-pacing probe
        // reports in evidence/221-invariants.json — one tick that became
        // interpolated, not one extra tick of the world.
        _interpolatesMotion = _motionOriginPending;
        _motionOriginPending = false;
        // Once per tick and not once per frame: the journal is the whole party's
        // history and the reading is the same for every frame drawn from this
        // state.
        _blows = BlowReadout.Of(_state, _creatureHitPointsBefore);
        // After the reading, because a body that strikes turns towards what it
        // struck, and that answer comes from the reading.
        TurnBodies();
        _checksum = PrototypeScenario.Capture(_world).Checksum;
        UpdateHud();
        UpdateCreatureLabels();
        QueueRedraw();
    }

    private void UpdateCreatureLabels()
    {
        foreach (var creature in _state!.Creatures)
        {
            if (!_nameLabels.TryGetValue(creature.Id, out var label))
            {
                label = MakeMapLabel(
                    ScaleWorld(98, 17),
                    Math.Max(1, (int)Math.Round(ScaleWorld(10))),
                    CreatureColors[creature.Id]);
                _nameLabels.Add(creature.Id, label);
            }

            // A name per overlapping creature made the economy unreadable. Names are
            // now an intentional inspection affordance: selected or hovered only.
            var visible = creature.Id == _selectedCreatureId || creature.Id == _hoverCreatureId;
            label.Visible = visible;
            if (!visible)
            {
                continue;
            }

            label.Text = $"{creature.Name} {CreatureStateShort(creature)}";
            // Follows the interpolated body, not the canonical tile, so the tag
            // does not snap a whole tile ahead of the creature it names.
            label.Position = CreatureRenderCenter(creature) +
                new Vector2(
                    ScaleWorld(2) - (_tileSize / 2f),
                    -ScaleWorld(14) - (_tileSize / 2f));
        }
    }

    /// <summary>
    /// What the four panels currently say is decided in
    /// <c>DungeonFortress.Presentation</c>, which does not reference Godot and is
    /// covered by unit tests running in CI. All this node does is put the
    /// resulting strings on the labels, so the adapter can no longer be the only
    /// place a wording change is observable.
    /// </summary>
    private void UpdateHud()
    {
        var panels = HudText.Build(CurrentHudView(), _projection);
        _inspector!.Text = panels.Inspector;
        _summary!.Text = panels.Summary;
        _feedback!.Text = panels.Feedback;
        _roster!.Text = panels.Roster;
        RefreshControls();
    }

    /// <summary>
    /// Verification-only fault injection. It changes no game state and exists so
    /// the HUD guard proves it rejects a real overflow at the required logical
    /// width instead of merely reporting that today's layout happens to fit.
    /// </summary>
    private void InjectHudGuardRegression()
    {
        _inspector!.Text = string.Join(
            "\n",
            Enumerable.Range(1, 80).Select(line => $"HUD guard regression line {line}"));
    }

    /// <summary>
    /// Re-authors the first legend row at four pixels. Nothing about the text
    /// changes, so the overflow guard stays green and only the readability
    /// policy can notice — which is exactly the shape of the defect Issue #86
    /// was opened about, and the reason a run with this flag is required to fail.
    /// </summary>
    private void InjectHudReadabilityRegression() =>
        _legendLines[0].AddThemeFontSizeOverride("font_size", 4);

    /// <summary>
    /// The tooltip counterpart of <see cref="InjectHudReadabilityRegression"/>
    /// (Issue #127): shrinks the readability guard's own tooltip sample
    /// instead of a legend row, so a run with this flag proves the guard
    /// rejects an unreadable tooltip instead of trusting that the fix keeps it
    /// covered forever.
    /// </summary>
    private void InjectHudTooltipReadabilityRegression()
    {
        var body = (Label)_tooltipReadabilitySample!.FindChild(
            "TooltipBody", recursive: true, owned: false)!;
        body.AddThemeFontSizeOverride("font_size", 4);
    }

    /// <summary>
    /// The adapter state the HUD text is allowed to depend on, gathered in one
    /// place. Everything else about this node — labels, viewport, brushes,
    /// pointer — stays on this side of the seam on purpose.
    /// </summary>
    private HudViewState CurrentHudView() => new(
        _state!,
        _fixture,
        _checksum,
        _paused,
        _speed,
        _selectedCreatureId,
        _selectedCell,
        _controlFeedback,
        _playerCommands,
        _diagnostics.Count);
}
