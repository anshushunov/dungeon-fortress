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

    /// <param name="seedOverride">
    /// Issue #349's addendum. Replaces the seed the fixture document carries
    /// without touching the document or the fixture it names — the same record
    /// <c>with</c> pattern the simulation's own test suite already uses to run
    /// a fixture at a seed other than its own
    /// (e.g. <c>tests/DungeonFortress.Simulation.Tests/PrototypeReturningRaiderTests.cs</c>).
    /// <c>null</c> keeps the document's own seed, which is every call site of
    /// this method that predates the parameter.
    /// </param>
    private void LoadFixture(string fixture, int ticks, ulong? seedOverride = null)
    {
        if (fixture is not ("baseline" or "prepared" or "neglected"))
        {
            throw new ArgumentException("Fixture must be baseline, prepared or neglected.", nameof(fixture));
        }

        _fixtureLog = PrototypeCommandDocument.Load(FixturePath(fixture));
        if (seedOverride is { } seed)
        {
            _fixtureLog = _fixtureLog with { Seed = seed };
        }

        _playerCommands.Clear();
        var world = new PrototypeWorld(_fixtureLog);
        var target = Math.Clamp(ticks, 0, PrototypeTuning.SessionTicks);
        // All of it but the last tick. The last one runs on its own below, so the
        // frame knows which way every body just stepped.
        // Stepped to a **tick** and not for a number of steps: a step stopped
        // being a tick when the party learned to stand still between two waves
        // (Issue #312), and a fixture loaded "to tick N" must land on tick N or
        // every captured frame moves with the balance.
        while (!world.IsComplete && world.CurrentTick < Math.Max(0, target - 1))
        {
            world.Step();
        }
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
            StepOneTick(world);
            RefreshState();
        }
    }

    /// <summary>
    /// One tick of the world, however many steps that takes. While a moment of
    /// truth is open a step is spent waiting for a verdict and the tick does not
    /// happen (Issue #312); STEP has to mean the same thing either way, so it
    /// keeps stepping until the clock moves or the window closes by itself.
    /// </summary>
    private static void StepOneTick(PrototypeWorld world)
    {
        var before = world.CurrentTick;
        while (!world.IsComplete && world.CurrentTick == before)
        {
            world.Step();
        }
    }

    /// <param name="ticks">How many ticks are asked for.</param>
    /// <param name="byHand">
    /// Whether a person pressed STEP, as opposed to the running clock asking for
    /// its share of the frame. Only a deliberate press earns an explanation when
    /// the clock refuses to move (Issue #331): writing the same sentence on every
    /// frame of a running party would bury whatever the last command answered.
    /// </param>
    private void Advance(int ticks, bool byHand = false)
    {
        if (_world is null || _world.IsComplete)
        {
            return;
        }

        var tickBefore = _world.CurrentTick;

        // A tick is a new blow, so the frame the last one was scrubbed to means
        // nothing any more.
        _strikeScrub = null;
        // Presentation only, and here rather than in RememberMotionOrigin because
        // every way of running a tick has to be covered: a raider's blow on a
        // defender that survives is recorded nowhere, so a fall in hit points is
        // the only evidence of it, and a STEP or an accepted command must show it
        // as readily as a running clock does.
        RememberCreatureHitPoints();
        // Deliberately steps and not ticks: while a moment of truth is open a
        // step is spent waiting, and the player pressing STEP has to watch the
        // window count down instead of being carried through the pause they are
        // supposed to answer (Issue #312). The same is true of the running clock,
        // which comes through here.
        _world.RunTicks(Math.Min(ticks, PrototypeTuning.SessionTicks - _world.CurrentTick));
        RefreshState();

        // The step was spent waiting rather than played. Saying so is the whole
        // of criterion 4 of Issue #331: the clock standing still is correct
        // behaviour, and correct behaviour with no explanation is what the owner
        // read as a defect.
        if (byHand && _world.CurrentTick == tickBefore && _state is { MomentOfTruth.Open: true })
        {
            ExplainHeldTime();
            UpdateHud();
        }
    }

    /// <summary>
    /// Stops the clock on the frame a moment of truth opens (Issue #331, round 2).
    ///
    /// <para>
    /// <b>Why.</b> While the window is open a step of the world is spent waiting
    /// and no tick happens, so a running clock burns the window instead of the
    /// party: at <c>TicksPerSecond</c> 6 and
    /// <see cref="PrototypeTuning.MomentOfTruthWindowSteps"/> 40 the band lives
    /// 6.7 seconds at 1x and 0.42 at 16x. The owner's playtest was played with
    /// the clock running, so without this the whole band is an amber flash at the
    /// bottom of the screen followed by the next wave and silently accrued
    /// grudges — the same unplayable moment Issue #331 was opened about, in a new
    /// shape. Independent review of PR #345 measured it and called it the
    /// playtest blocker.
    /// </para>
    ///
    /// <para>
    /// <b>What it is not.</b> It changes no rule of the window. Forty steps stay
    /// forty steps, silence stays a legal answer and still costs what
    /// <c>CloseMomentOfTruth</c> charges for it; the player can press RUN again
    /// and spend the window exactly as before. What changes is that silence
    /// becomes something chosen rather than something slept through. Nothing here
    /// reaches canonical state: <c>_paused</c> is adapter tempo, and a party
    /// replayed headless prints the same checksum either way.
    /// </para>
    ///
    /// <para>
    /// It fires on the transition and not on the state, so a player who pressed
    /// RUN with the window already open is not paused again on the next frame.
    /// </para>
    /// </summary>
    private void StopTheClockWhenTheDomainAsks()
    {
        if (!ShouldStopTheClock(_state is { MomentOfTruth.Open: true }))
        {
            return;
        }

        _paused = true;
        _tickAccumulator = 0;
        _controlFeedback = MomentOfTruthPanel.TimeIsHeld(CurrentMomentOfTruth());
    }

    /// <summary>
    /// The decision behind <see cref="StopTheClockWhenTheDomainAsks"/>, kept apart
    /// from its effect so that <see cref="AssertMomentOfTruthStopsTheClock"/> can
    /// hold it against a sequence of openings without needing a party that has
    /// reached one.
    ///
    /// <para>It answers <c>true</c> exactly on the frame the window opens. Not
    /// "while it is open": a player who deliberately pressed RUN during an open
    /// window would otherwise be paused again on the very next frame, which would
    /// take away the second legal answer — waiting the window out.</para>
    ///
    /// <para>The frame-pacing probe is exempt because it is not a player: it
    /// drives <c>_Process</c> to an exact tick with nobody to lift a pause, so a
    /// pause would hang the run rather than fail it. Its target today (200) is
    /// well before the first wave, which is precisely why the exemption is
    /// written down instead of relied upon.</para>
    /// </summary>
    private bool ShouldStopTheClock(bool open)
    {
        var opened = open && !_momentOfTruthWasOpen;
        _momentOfTruthWasOpen = open;
        return opened && _framePacingTargetTick is null;
    }

    /// <summary>
    /// The clock stops on the frame the domain asks something, and on no other.
    ///
    /// <para>
    /// Independent review of PR #345 measured what the missing pause costs: with
    /// the clock running the band lives 6.7 seconds at 1x and 0.42 at 16x,
    /// because a step of an open window is spent waiting rather than played. The
    /// owner's playtest was played with the clock running, so an unread band is
    /// the same unplayable moment Issue #331 was opened about.
    /// </para>
    ///
    /// <para>
    /// This holds the decision against the sequence a party actually produces:
    /// closed, then open (stop here and only here), open again on the next frame
    /// (do not stop — the player may have chosen to run), closed, open again
    /// (stop). It runs on every entry point and needs no wave to have landed.
    /// The field it walks is restored before returning.
    /// </para>
    /// </summary>
    private void AssertMomentOfTruthStopsTheClock()
    {
        var remembered = _momentOfTruthWasOpen;
        _momentOfTruthWasOpen = false;
        var failures = new List<string>();
        foreach (var (open, expected, why) in new (bool Open, bool Expected, string Why)[]
                 {
                     (false, false, "a closed window must not stop the clock"),
                     (true, true, "the frame the window opens on must stop the clock"),
                     (true, false, "a window that was already open must not stop it again"),
                     (false, false, "a window that closed must not stop it"),
                     (true, true, "the next wave's window must stop it again"),
                 })
        {
            var actual = ShouldStopTheClock(open);
            if (actual != expected)
            {
                failures.Add($"{why}, but it answered {actual}");
            }
        }

        _momentOfTruthWasOpen = remembered;
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"The moment of truth stops the clock wrongly in {failures.Count} place(s): " +
                string.Join("; ", failures) +
                ". A band the player cannot stop on is a band the player cannot read: an open " +
                "window spends its steps at the speed of the clock, not of the party.");
        }
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
        StopTheClockWhenTheDomainAsks();
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
        RefreshMomentOfTruthBand();
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
