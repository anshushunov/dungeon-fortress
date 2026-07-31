using System.Globalization;

using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// The text of the four HUD panels. Moved here from the Godot adapter unchanged:
/// every branch already took a snapshot and returned a string, so the only thing
/// the move alters is where the code lives and whether a test can reach it.
///
/// <c>tests/golden/ui/*.json</c> is the evidence that the wording did not shift.
/// A regenerated golden file is a defect report, not a chore.
/// </summary>
public static class HudText
{
    /// <summary>
    /// All four panels for one frame. The adapter assigns them to labels and does
    /// nothing else, so "what the HUD says" and "how it is drawn" stop being the
    /// same code.
    /// </summary>
    /// <param name="view">The frame.</param>
    /// <param name="projection">
    /// The frame's map projection. A caller that already has one — the adapter
    /// builds exactly one per snapshot — passes it so the HUD does not build a
    /// second; omitting it derives the same value from the same snapshot.
    /// </param>
    public static HudPanels Build(HudViewState view, MapProjection? projection = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        projection ??= view.Projection;
        return new(
            Summary(view, projection),
            Inspector(view, projection),
            Feedback(view),
            Roster(view));
    }

    /// <summary>
    /// Two lines and no more: the summary label ends where the time toolbar
    /// begins, so a third wrapped line would be drawn over the buttons.
    /// Session identity, the two numbers the party is read by and the head count
    /// go on the first line; the second line is the wave and the resources.
    ///
    /// Renown and domain strength are printed as numbers with a trend arrow and
    /// never as bars. A bar states a share of a maximum, and neither the head
    /// count nor the strength of a domain has one, so a bar would be a lie. The
    /// arrow answers the only question that has an answer — better or worse than
    /// at the previous wave — and the gap between the two numbers is left for
    /// the player to read. Nothing here says "you are doing badly".
    ///
    /// Stone is reported as three separate facts on purpose — loose on the
    /// floor, on someone's back, put away — because one combined number would
    /// hide exactly the part of the chain that is moving.
    ///
    /// Every number here is formatted with the invariant culture. This text is
    /// a checked artefact — the golden UI state compares it across two machines
    /// with two different cultures — so a decimal separator that follows the
    /// machine would make "0.5x" pass locally and fail in CI for a reason no
    /// diff would explain. Localisation, if it ever arrives, will be a decision
    /// of its own and not a side effect of where the build ran (Issue #46).
    /// </summary>
    public static string Summary(HudViewState view) => Summary(view, view.Projection);

    private static string Summary(HudViewState view, MapProjection projection)
    {
        var state = view.Snapshot;
        var stock = state.Stocks;
        var domain = state.Domain;
        return
            $"{view.Fixture.ToUpperInvariant()}  •  t{state.Tick}  •  {(view.Paused ? "PAUSED" : Speed(view.Speed))}" +
            $"  •  jobs {state.Jobs.Count}  •  {view.Checksum[..8]}" +
            $"  •  renown {domain.Renown}{Trend(domain.Renown, domain.RenownAtPreviousWave)}" +
            $"  •  strength {domain.Strength}{Trend(domain.Strength, domain.StrengthAtPreviousWave)}" +
            $"  •  crew {domain.LivingCreatures}" +
            $"\n{WavePhase(state)}  •  food {stock.Meals}+{stock.LooseMeals}" +
            $"  •  raw {stock.RawMushroom}+{stock.LooseRawMushroom}" +
            $"  •  stone {stock.LooseStone}L {stock.CarriedStone}C " +
            $"{stock.StoredStone}/{stock.StockpileCapacity}S" +
            // The mark the player just made counts here even though the tick that
            // records it has not run: it is in the command log, and reporting it
            // as absent is the same defect as not drawing it (Issue #58).
            $"  •  dug {state.Economy.DigsCompleted}  •  marks {projection.DigDesignationCount}";
    }

    /// <summary>
    /// The playback speed, in the only culture the HUD is allowed to speak.
    /// It is the single fractional number in the whole of the HUD and the
    /// inspector, which is why the rule is cheap to fix now and expensive to
    /// fix after the second one appears.
    /// </summary>
    public static string Speed(double speed) =>
        speed.ToString("0.#", CultureInfo.InvariantCulture) + "x";

    /// <summary>
    /// Better, worse or unchanged since the previous wave landed. Empty before
    /// the first wave, because "compared to what?" has no answer yet and an
    /// arrow that always points somewhere would be decoration.
    /// </summary>
    public static string Trend(int current, int? atPreviousWave) => atPreviousWave switch
    {
        null => string.Empty,
        { } previous when current > previous => "↑",
        { } previous when current < previous => "↓",
        _ => "→",
    };

    /// <summary>
    /// The whole side-panel explanation for the current selection.
    /// </summary>
    public static string Inspector(HudViewState view) => Inspector(view, view.Projection);

    private static string Inspector(HudViewState view, MapProjection projection) =>
        InspectorText.Build(projection, view.SelectedCreatureId, view.SelectedCell);

    /// <summary>
    /// How many of a creature's own decisions the story panel shows at once.
    ///
    /// <para>
    /// A party leaves a creature with up to 961 entries in the canonical journal
    /// — measured, by
    /// <c>CreatureStoryTests.The_story_is_bounded_and_the_bound_is_on_the_panel</c>
    /// — and the panel holds ten drawn lines at the tightest frame the HUD guard
    /// checks. The bound is therefore arithmetic and not a preference: a story
    /// sentence runs to about sixty characters and wraps onto two drawn lines in
    /// a 287-pixel panel, so four of them plus the header is nine, and text that
    /// does not fit is dropped or drawn over the panel below. That is what
    /// <c>Main.AssertLabelsFit</c> refuses, and it refused six.
    /// </para>
    ///
    /// <para>
    /// What keeps four honest is that the header says how many entries are
    /// behind them.
    /// </para>
    /// </summary>
    public const int CreatureStoryLines = 4;

    /// <summary>
    /// The event panel. With nothing selected it is the domain's feed: the last
    /// three autonomous choices, newest first, plus the diagnostics count. With a
    /// creature selected it becomes <b>that creature's story</b> — the last
    /// <see cref="CreatureStoryLines"/> decisions it took this party, newest
    /// first (Issue #128).
    ///
    /// <para>
    /// The owner played the first slice of memory of place and said: "Метки вижу,
    /// они остаются на месте боев. Но без лога событий по каждому персонажу
    /// трудно понять, как он реагирует на них." The marks were readable and the
    /// reaction to them was not, because the only feed there was is the whole
    /// domain's and one creature cannot be found in it. The exit criterion of the
    /// slice ends "…и как это изменило его следующее решение", and that is the
    /// third of it this panel answers.
    /// </para>
    ///
    /// <para>
    /// It is the same surface rather than a new one on purpose. A player asking
    /// "what happened to this one" is already reading the feed; scoping the feed
    /// to the creature they clicked puts the answer where they are looking,
    /// costs the side column no height, and is undone by clicking anywhere else.
    /// </para>
    ///
    /// <para>
    /// Nothing here is computed. Every line is one entry of the canonical journal
    /// — <c>state.Events</c> filtered by creature id — rendered by
    /// <see cref="EventNarration"/> from that entry's own code, details, job kind
    /// and target. Selecting a creature therefore needs no tick to run: the facts
    /// are already published, and this is a projection of them. Since Issue #117
    /// the line is a sentence rather than the raw reason code; the code has not
    /// gone anywhere, it is still what the canonical state and the canonical
    /// event log carry, and its existence is an invariant of
    /// <see href="../../docs/decisions/0010-contract-invariants-and-tuning.md">
    /// ADR 0010</see>. What a player sees instead of it is presentation.
    /// </para>
    ///
    /// <para>
    /// The header is deliberately part of both the empty and the populated case,
    /// which is why the empty panel repeats it.
    /// </para>
    /// </summary>
    public static string Feedback(HudViewState view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var state = view.Snapshot;
        // The diagnostics counter is a fact about the session, so it stays on the
        // session's own feed. Giving it up while a creature is selected is not a
        // tidy-up: the line and the blank line above it are three of the ten
        // drawn lines this panel has, and three lines is more than one entry of
        // a creature's story. The count is one click away and the story is what
        // the panel was clicked for.
        return view.SelectedCreatureId is { } creatureId
            ? CreatureStory(state, creatureId)
            : DomainFeed(state) +
                $"\n\nDiagnostics: {view.DiagnosticCount} (structured JSON is emitted by smoke/capture).";
    }

    private static string DomainFeed(PrototypeSnapshot state)
    {
        var eventText = state.Events.Count == 0
            ? "EVENT FEEDBACK\nNo events yet. Step or unpause to watch autonomous choices."
            : string.Join(
                "\n",
                state.Events.TakeLast(3).Reverse().Select(@event =>
                    $"t{@event.LastTick} · {EventNarration.Describe(state, @event)}"));
        return "EVENT FEEDBACK\n" + eventText;
    }

    /// <summary>
    /// One creature's decisions this party, newest first and bounded by
    /// <see cref="CreatureStoryLines"/>.
    ///
    /// <para>
    /// The header carries the name and the bound together — "last 6 of 43" —
    /// because a panel that silently shows six of forty-three is a panel that
    /// lies about how much there is. The name is on the header and not on every
    /// line: the whole panel is about one creature, and repeating the name six
    /// times would spend the width the sentences need.
    /// </para>
    ///
    /// <para>
    /// An entry the journal folded over several ticks prints its whole span —
    /// <c>t1204-1240</c> — and how many ticks it held. That is not decoration:
    /// "it refused this for thirty-six ticks" and "it refused this once" are
    /// different stories, and the deduplication rule of contract 11.1 is the
    /// reason the difference is a count rather than thirty-six lines.
    /// </para>
    /// </summary>
    public static string CreatureStory(PrototypeSnapshot state, int creatureId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var story = state.Events.Where(@event => @event.CreatureId == creatureId).ToArray();
        var name = CreatureName(state, creatureId);
        if (story.Length == 0)
        {
            return
                $"STORY · {name}\n" +
                "Nothing decided yet. Step or unpause, and what it chooses shows up here.";
        }

        var shown = Math.Min(CreatureStoryLines, story.Length);
        var head = story.Length > shown
            ? string.Create(CultureInfo.InvariantCulture, $"STORY · {name} · last {shown} of {story.Length}")
            : string.Create(CultureInfo.InvariantCulture, $"STORY · {name} · {story.Length} in all");
        return head + "\n" + string.Join(
            "\n",
            story.TakeLast(shown).Reverse().Select(StoryLine));
    }

    /// <summary>
    /// One line of one creature's story: when it decided, and what it decided.
    /// </summary>
    private static string StoryLine(PrototypeEvent @event)
    {
        var when = @event.FirstTick == @event.LastTick
            ? string.Create(CultureInfo.InvariantCulture, $"t{@event.LastTick}")
            : string.Create(CultureInfo.InvariantCulture, $"t{@event.FirstTick}-{@event.LastTick}");
        var held = @event.Repeats > 1
            ? string.Create(CultureInfo.InvariantCulture, $" (x{@event.Repeats})")
            : string.Empty;
        var sentence = EventNarration.Sentence(
            @event.ReasonCode,
            @event.Details,
            @event.JobKind,
            @event.Target);
        return $"{when} · {sentence}{held}";
    }

    /// <summary>
    /// Crew line, control feedback line and the tail of the command log.
    /// </summary>
    public static string Roster(HudViewState view)
    {
        var state = view.Snapshot;
        return "CREW  " + string.Join("  •  ", state.Creatures.Select(creature => $"{creature.Name} {CreatureStateShort(creature)}")) +
            "\n" + view.ControlFeedback +
            "\nLOG " + (view.PlayerCommands.Count == 0
                ? "empty"
                : string.Join(" | ", view.PlayerCommands.TakeLast(2).Select(DescribeCommand)));
    }

    /// <summary>
    /// Which wave the domain is dealing with and when it lands, or how the party
    /// ended. Kept short on purpose: the excavation counters share this line, and
    /// the battle wording lives in the side-panel legend.
    ///
    /// The end of the party outranks the wave in hand, and an arriving wave
    /// outranks its own countdown.
    ///
    /// The party score is printed here and nowhere else, because here is the
    /// only place that exists once the party is over. During the party the
    /// summary keeps the same two numbers it always had — renown and domain
    /// strength — and the gap between them is still the only thing to read.
    /// "How am I doing" and "how did I play" are different questions asked at
    /// different moments (ADR 0016), so the second one never appears while the
    /// first is still open.
    /// </summary>
    public static string WavePhase(PrototypeSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var threat = state.Threat;
        var waves = $"{threat.WaveNumber}/{threat.WaveCount}";
        if (state.SessionResult.Outcome is { } outcome)
        {
            var score = state.SessionResult.Score is { } value ? $" · score {value}" : string.Empty;
            // Three outcomes, three different words in the same place, so which
            // one happened is read at a glance and never needs the inspector.
            // "Raided" carries how many waves were actually turned back, because
            // that is the number the player will want next.
            //
            // A fourth outcome is a defect and is refused rather than drawn. A
            // catch-all arm would render an end nobody taught the HUD about as
            // one of the ends it knows — and the one it used to pick was "the
            // domain fell", which is the worst thing to say by accident.
            return outcome switch
            {
                "held" => $"DOMAIN HELD {threat.WaveCount}/{threat.WaveCount}{score}",
                "raided" =>
                    $"DOMAIN RAIDED · {state.SessionResult.WavesRepelled}/{threat.WaveCount} repelled{score}",
                "fallen" => $"DOMAIN FELL · wave {waves}{score}",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(state),
                    outcome,
                    "The HUD has no wording for this end of a party and will not " +
                    "guess one. Teach it the new outcome instead."),
            };
        }

        if (threat.Active)
        {
            return $"WAVE {waves} ACTIVE ×{threat.RaiderCount}";
        }

        return threat.Announced
            ? $"WAVE {waves} IN {threat.TicksRemaining}t ×{threat.RaiderCount}"
            : $"WAVE {waves} · warn t{threat.AnnounceTick}";
    }

    public static string CreatureStateShort(PrototypeCreatureSnapshot creature) => creature.Mode switch
    {
        CreatureMode.Downed => "DOWN",
        CreatureMode.Fled => "FLED",
        CreatureMode.Fighting => "FIGHT",
        CreatureMode.Working => "WORK",
        CreatureMode.Moving => "MOVE",
        _ => "READY",
    };

    public static string CreatureLifeState(PrototypeCreatureSnapshot creature) => creature.Mode switch
    {
        CreatureMode.Downed => "DOWNED",
        CreatureMode.Fled => "FLED",
        CreatureMode.Fighting => "ALIVE / FIGHTING",
        _ => "ALIVE",
    };

    /// <summary>
    /// An id is never shown raw when a name exists, and never hidden when it does
    /// not: an event about a creature the snapshot no longer carries still reads.
    /// </summary>
    public static string CreatureName(PrototypeSnapshot state, int id) =>
        state.Creatures.SingleOrDefault(creature => creature.Id == id)?.Name ?? $"#{id}";

    public static string DescribeCommand(PrototypeCommand command) => command switch
    {
        ZonePaintCommand paint => $"t{paint.Tick} paint {paint.ZoneKind} ({paint.Tiles.Count})",
        ZoneEraseCommand erase => $"t{erase.Tick} erase {erase.ZoneKind} ({erase.Tiles.Count})",
        DigDesignateCommand designate => $"t{designate.Tick} dig_designate ({designate.Tiles.Count})",
        DigCancelCommand cancel => $"t{cancel.Tick} dig_cancel ({cancel.Tiles.Count})",
        BuildDesignateCommand build => $"t{build.Tick} build_designate ({build.Tiles.Count})",
        BuildCancelCommand unbuild => $"t{unbuild.Tick} build_cancel ({unbuild.Tiles.Count})",
        SetPriorityCommand priority => $"t{priority.Tick} priority {priority.JobKind}={priority.Value}",
        SetRuleCommand rule => $"t{rule.Tick} rule {rule.RuleId}={rule.Value}",
        _ => command.GetType().Name,
    };
}
