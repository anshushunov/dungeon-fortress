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
    /// <b>The question the bound answers is "how many beats does one creature's
    /// story have", and only then "how many lines fit".</b> Issue #128 asked the
    /// second question alone, and a technical limit silently decided a product
    /// result: four lines of the newest entries is four lines of traffic
    /// (<c>evidence/140-before.json</c>). Both questions are asked here now, and
    /// they happen to give the same number.
    /// </para>
    ///
    /// <para>
    /// <b>The story.</b> A creature's party reads as four beats: it went to the
    /// wave, the wave cost it something, it came back, and it now refuses the
    /// place where that happened. Measured on the shipped <c>baseline</c> party
    /// at tick 2400, a creature ends with 17 to 21 distinct kinds of decision of
    /// which 4 to 8 mean anything for its fate, so four lines hold the whole of
    /// a short story and the latest beats of a long one.
    /// </para>
    ///
    /// <para>
    /// <b>The budget.</b> The panel holds ten drawn lines at the tightest frame
    /// the HUD guard checks. A story sentence runs to about sixty characters and
    /// wraps onto two drawn lines in a 287-pixel panel, so four of them plus the
    /// header is nine, and text that does not fit is dropped or drawn over the
    /// panel below. That is what <c>Main.AssertLabelsFit</c> refuses, and it
    /// refused six (Issue #128) and it refuses five (Issue #140,
    /// <c>evidence/140-mutations.json</c>).
    /// </para>
    ///
    /// <para>
    /// <b>What stays off the panel</b>, therefore, and is named rather than
    /// hidden: the older beats of a story with more than four, and every routine
    /// decision — waiting for stock, being blocked in a corridor, stepping aside
    /// — which is 89 % of what a creature does. The header carries both counts,
    /// and the inspector next to it carries every place the creature avoids.
    /// </para>
    /// </summary>
    public const int CreatureStoryLines = 4;

    /// <summary>
    /// How much one decision means for the creature that took it. The story panel
    /// spends its four lines from the top of this scale down, so routine never
    /// displaces a turning point (Issue #140).
    ///
    /// <para>
    /// Four levels, and each answers a different question a player asks about a
    /// creature:
    /// </para>
    ///
    /// <list type="number">
    /// <item><b>3 — how what happened changed what it does.</b> A refusal by
    /// memory of place is the only decision in the journal that is caused by the
    /// creature's own history, and it is the sentence the whole slice exists for
    /// ("…и как это изменило его следующее решение"). Nothing outranks it;</item>
    /// <item><b>2 — what the wave cost it.</b> Its nerve, its footing, its
    /// health: broke and ran, was put down, was carried off, is mending, is
    /// whole again. This is the "что с ним произошло" half of the same
    /// question, and it outranks level 1 because being put down happens to a
    /// creature while joining a wave is only where it was standing;</item>
    /// <item><b>1 — how it met the wave.</b> Joined, came back after it, put a
    /// raider down, or one of the three ways of not fighting — too hungry, too
    /// hurt, too far to reach it;</item>
    /// <item><b>0 — everything else.</b> Choosing work, waiting on stock, being
    /// blocked, stepping aside, and the blow-by-blow of a fight. Not noise —
    /// this is what the creature spends its life doing, 89 % of the journal —
    /// but not a turning point either, and the inspector beside the panel
    /// already says what it is doing now.</item>
    /// </list>
    ///
    /// <para>
    /// The scale is presentation and not contract: reason codes and their meaning
    /// stay canonical under
    /// <see href="../../docs/decisions/0010-contract-invariants-and-tuning.md">
    /// ADR 0010</see>, and what a panel does with four lines is this layer's
    /// business. A code this method has never heard of is <b>routine</b> rather
    /// than refused, which is the opposite of what
    /// <see cref="EventNarration.Sentence"/> does with one, and deliberately so:
    /// an unknown code has no sentence at all and must be refused, but an unknown
    /// code that does have one is merely something nobody has ranked yet, and
    /// promoting it to a turning point by accident would be the louder mistake.
    /// <c>CreatureStoryTests.Every_reason_code_the_matrix_produces_is_ranked_on_purpose</c>
    /// is what stops "routine by default" from becoming "routine by neglect".
    /// </para>
    /// </summary>
    public static int StoryWeight(string reasonCode) => reasonCode switch
    {
        "refused_place_of_panic" or "refused_place_of_wound" => 3,

        "combat_fled_morale" or "combat_downed" or "injury_tended" or "injury_mending"
            or "injury_healed" => 2,

        "combat_joined" or "combat_returned" or "combat_raider_downed"
            or "combat_refused_starving" or "combat_refused_injured"
            or "combat_absent_unreachable" => 1,

        _ => 0,
    };

    /// <summary>
    /// The event panel. With nothing selected it is the domain's feed: the last
    /// three autonomous choices, newest first, plus the diagnostics count. With a
    /// creature selected it becomes <b>that creature's story</b> — up to
    /// <see cref="CreatureStoryLines"/> of the decisions that meant something for
    /// its fate, newest first (Issue #128, reordered by Issue #140).
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
    /// One creature's party in at most <see cref="CreatureStoryLines"/> lines:
    /// the decisions that <b>meant something for its fate</b>, newest first.
    ///
    /// <para>
    /// It used to be the newest four entries of the journal, and that made the
    /// panel unreadable for the reason the numbers say: a creature's journal is
    /// 89 % waiting for stock, being blocked in a corridor and stepping aside,
    /// so the newest four almost always are. Measured on the shipped
    /// <c>baseline</c> party at tick 2400 — 4425 entries, 27 refusals by memory,
    /// three creatures that ever refused that way, and <b>none</b> of the three
    /// with the refusal on its panel (<c>evidence/140-before.json</c>). The
    /// slice's own question, "как это изменило его следующее решение", was
    /// answered by a sentence a player could not reach.
    /// </para>
    ///
    /// <para>
    /// So the four lines are spent from <see cref="StoryWeight"/> down and by
    /// recency inside a level, and <b>at most one line per kind of decision</b>.
    /// The second rule is what makes the first one worth anything: creature #0
    /// refused by memory fourteen times, and four lines of the same refusal is
    /// as poor a story as four lines of traffic. One line per kind turns four
    /// slots into four beats — it went to the wave, the wave cost it something,
    /// it came back, it will not go there again — which is what a story is.
    /// The line shown for a kind is that kind's newest entry.
    /// </para>
    ///
    /// <para>
    /// Routine still fills whatever the beats leave over, so a creature at tick
    /// 40 that nothing has happened to yet has a panel rather than a blank, and
    /// so the panel keeps saying what the creature has been doing lately.
    /// </para>
    ///
    /// <para>
    /// The header carries the name and three counts — "4 of 654 · 19 mattered" —
    /// because a panel that silently shows four of six hundred is a panel that
    /// lies about how much there is, and because <b>what is off the panel</b>
    /// has to be readable off the panel: 650 entries are not here, 15 of them
    /// mattered and the rest is routine. The word "last" is gone from the header
    /// on purpose; these are no longer the last four, and a header that still
    /// said so would be the same lie in the other direction.
    /// </para>
    ///
    /// <para>
    /// An entry the journal folded over several ticks prints its whole span —
    /// <c>t1204-1240</c> — and how many ticks it held. That is not decoration:
    /// "it refused this for thirty-six ticks" and "it refused this once" are
    /// different stories, and the deduplication rule of contract 11.1 is the
    /// reason the difference is a count rather than thirty-six lines.
    /// </para>
    ///
    /// <para>
    /// Nothing here runs a tick. Every input is a field of the snapshot that has
    /// already been published — the journal, its reason codes, its details — and
    /// the ranking is a function of the reason code alone. This is the same
    /// "projection against world" line <c>MapAccents</c> is written along:
    /// the projection answers what folds out of published facts, the world
    /// answers what needs a tick.
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

        var shown = StorySelection(story);
        var mattered = story.Count(@event => StoryWeight(@event.ReasonCode) > 0);
        var head = shown.Count < story.Length
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"STORY · {name} · {shown.Count} of {story.Length} · {mattered} mattered")
            : string.Create(CultureInfo.InvariantCulture, $"STORY · {name} · {story.Length} in all");
        return head + "\n" + string.Join("\n", shown.Select(StoryLine));
    }

    /// <summary>
    /// Which of a creature's entries the panel spends its lines on, newest first.
    /// Public because the test that says "the panel never disagrees with the
    /// journal" has to be able to state the rule rather than restate the code.
    ///
    /// <para>
    /// Three steps, and each one is a decision: <b>one entry per reason code</b>,
    /// the newest of that kind, so the same beat cannot fill the panel;
    /// <b>ordered by <see cref="StoryWeight"/> and then by recency</b>, so a
    /// turning point cannot be pushed off by traffic; <b>cut to
    /// <see cref="CreatureStoryLines"/></b>, because more than that does not fit
    /// the panel. What comes back out is put back in time order, newest first,
    /// so the panel is read bottom to top as the party happened.
    /// </para>
    ///
    /// <para>
    /// Ties are broken by the order the journal is in, which is the order the
    /// world wrote it in, so the same snapshot always produces the same panel.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PrototypeEvent> StorySelection(IEnumerable<PrototypeEvent> story)
    {
        ArgumentNullException.ThrowIfNull(story);
        return story
            .GroupBy(@event => @event.ReasonCode, StringComparer.Ordinal)
            .Select(kind => kind.MaxBy(@event => @event.LastTick)!)
            .OrderByDescending(@event => StoryWeight(@event.ReasonCode))
            .ThenByDescending(@event => @event.LastTick)
            .Take(CreatureStoryLines)
            .OrderByDescending(@event => @event.LastTick)
            .ThenByDescending(@event => @event.FirstTick)
            .ToArray();
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
