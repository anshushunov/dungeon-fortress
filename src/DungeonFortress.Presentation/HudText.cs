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
    /// — which is <b>96.5 %</b> of the journal, and never under 92 % for any one
    /// creature. The header carries both counts, and the inspector next to it
    /// carries every place the creature avoids.
    /// </para>
    ///
    /// <para>
    /// That share is a run and not a remembered number:
    /// <c>CreatureStoryTests.Most_of_what_a_creature_decides_is_routine</c>
    /// prints it per party and per creature and fails if it drops under the 90 %
    /// this file and the two documents rely on. It is quoted here, in contract
    /// §11.1 and in <c>PROTOTYPE_GRAYBOX.md</c>; an earlier draft of all three
    /// said 89 %, which was four particular codes of one particular creature
    /// generalised to the journal, and nothing could have caught that but a run.
    /// </para>
    /// </summary>
    public const int CreatureStoryLines = 4;

    /// <summary>
    /// How many of the domain's own turning points the event feed shows at once,
    /// with nothing selected.
    ///
    /// <para>
    /// <b>The question is "how many lines does it take to read what is happening
    /// in the domain", and the honest answer is larger than this number.</b> The
    /// domain is its nine creatures, and what a player must be able to read
    /// without clicking is which of them something happened to and what it was; on
    /// the shipped <c>baseline</c> party at tick 2400 that is <b>nine</b> lines,
    /// because all nine have had something that mattered
    /// (<c>evidence/145-feed-after.json</c>). Nine does not fit, and saying so is
    /// the whole point of asking the question first: Issue #128 asked only "how
    /// many lines fit", got four, and a technical limit silently became a product
    /// answer.
    /// </para>
    ///
    /// <para>
    /// <b>What the frame can draw.</b> The panel holds ten drawn lines at the
    /// tightest frame the HUD guard checks — viewport 2048x1440 at UI scale 2, a
    /// 287-pixel column — and unlike the story panel the feed also carries the
    /// session's diagnostics counter and the blank line above it.
    /// <b>What binds is the sentences, not the header.</b> A turning point carries
    /// its numbers with it — "Мотылёк broke and ran: 79% health, 5 raiders close,
    /// 0 ally down." — and three of those need seven drawn lines, where three
    /// routine sentences needed six. That is why this issue had to pay for its own
    /// third line by trimming the diagnostics note (see <see cref="Feedback"/>):
    /// with the note the panel measured 11 of 10, without it exactly 10.
    /// </para>
    ///
    /// <para>
    /// <c>Main.AssertLabelsFit</c> is what refuses four, measured and not argued:
    /// <em>"'feedback' needs 11 lines but only 10 fit in (287, 200) at viewport
    /// (2048, 1440), UI scale 2"</em> (<c>evidence/145-bound.json</c>). And
    /// unlike the story panel it is the <b>live</b> label that fails rather than a
    /// padded worst case, because the feed is the shape every entry point actually
    /// carries.
    /// </para>
    ///
    /// <para>
    /// <b>The slack is zero</b>, exactly as it is for the story panel at four
    /// lines, and for the same reason: both panels are the tallest thing their
    /// label can hold. Any sentence in <see cref="EventNarration"/> that grows past
    /// two drawn lines reddens the <c>godot</c> stage rather than quietly losing a
    /// line, which is the right failure but not a comfortable one.
    /// </para>
    ///
    /// <para>
    /// <b>What stays off the feed</b>, therefore, and is named rather than
    /// hidden: six of the nine crew, every earlier beat of the three that are
    /// shown, and all of the routine — 96.5 % of the journal. The header carries
    /// the two counts that say so, and one click puts any creature's own four
    /// beats on the same panel (<see cref="CreatureStory"/>).
    /// </para>
    ///
    /// <para>
    /// <b>The feed is a digest and no longer a ticker</b>, and that is the second
    /// thing given up. Ranking by what a decision meant means the top of the feed
    /// can stand still for hundreds of ticks while the crew works — which is
    /// exactly right when nothing has happened and would be wrong on a panel whose
    /// job was to prove the world is running. The summary line above it already
    /// carries the tick, the wave and the stocks, and the routine fill still moves
    /// whenever nothing has mattered yet.
    /// </para>
    /// </summary>
    public const int DomainFeedLines = 3;

    /// <summary>
    /// How much one decision means for the creature that took it. The story panel
    /// spends its four lines from the top of this scale down, so routine never
    /// displaces a turning point (Issue #140).
    ///
    /// <para>
    /// The domain feed spends its own lines down the <b>same</b> scale (Issue
    /// #145), and that is a decision rather than an economy. There is no fact in
    /// the canonical journal that means something to a domain and nothing to any
    /// of its creatures: a domain has no body to be put down and no memory of its
    /// own, and every entry it could be told about happened to somebody. What the
    /// two panels do differ in is <b>what counts as one beat</b>, which is
    /// <see cref="DomainSelection"/> against <see cref="StorySelection"/> and not
    /// this scale.
    /// </para>
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
    /// this is what the creature spends its life doing, 96.5 % of the journal —
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
    /// The event panel. With nothing selected it is the domain's feed: up to
    /// <see cref="DomainFeedLines"/> of the crew's turning points, newest first,
    /// plus the diagnostics count. With a creature selected it becomes <b>that
    /// creature's story</b> — up to <see cref="CreatureStoryLines"/> of the
    /// decisions that meant something for its fate, newest first (Issue #128,
    /// reordered by Issue #140, and the feed beside it by Issue #145).
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
    /// <b>The diagnostics counter says how many and no longer where to look.</b>
    /// It used to read "Diagnostics: 0 (structured JSON is emitted by
    /// smoke/capture)", which wraps onto two drawn lines and with the blank line
    /// above it took three of the ten this panel has. That was affordable while
    /// the feed showed the newest three entries, because the newest three are
    /// routine and routine sentences are short. It stopped being affordable the
    /// moment the feed started showing what mattered: a turning point carries its
    /// numbers with it — "broke and ran: 79% health, 5 raiders close, 0 ally down"
    /// — and three of those need seven drawn lines rather than six. Measured on
    /// the two frames <c>verify.ps1</c> photographs, the panel needed 11 of 10
    /// (<c>evidence/145-bound.json</c>).
    /// </para>
    ///
    /// <para>
    /// So the developer's note moved here and the count stayed. Where the
    /// structured diagnostics are written is a fact about the tooling that a
    /// player never needs and a developer reads once; how many there are is a
    /// fact about this session, and it is the half worth a line of the HUD.
    /// </para>
    /// </summary>
    public static string Feedback(HudViewState view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var state = view.Snapshot;
        // The diagnostics counter is a fact about the session, so it stays on the
        // session's own feed. Giving it up while a creature is selected is not a
        // tidy-up: the line and the blank line above it are two of the ten drawn
        // lines this panel has, and two lines is more than one entry of a
        // creature's story. The count is one click away and the story is what the
        // panel was clicked for.
        return view.SelectedCreatureId is { } creatureId
            ? CreatureStory(state, creatureId)
            : DomainFeed(state) + $"\n\nDiagnostics: {view.DiagnosticCount}";
    }

    /// <summary>
    /// The domain in at most <see cref="DomainFeedLines"/> lines: <b>who</b> in
    /// the crew something happened to, and what, newest first.
    ///
    /// <para>
    /// It used to be the newest three entries of the journal, and that made the
    /// panel unreadable for exactly the reason Issue #140 measured about the story
    /// panel — 96.5 % of what a creature decides is waiting for stock, being
    /// blocked in a corridor and stepping aside, so the newest three almost always
    /// are. Measured on the shipped <c>baseline</c> party, sampled every 50 ticks
    /// to tick 2400: <b>3 of 48</b> windows of the feed carried anything that
    /// mattered, and at tick 2400 all three lines were one creature stopped in one
    /// corridor (<c>evidence/145-feed-before.json</c>). That is the screen a
    /// player opens on, because nothing is selected until they click.
    /// </para>
    ///
    /// <para>
    /// So the feed is now the same rule as the story panel with one argument
    /// changed: <b>the beat of a domain is a creature, where the beat of a
    /// creature is a kind of decision</b>. One line per creature, its most
    /// significant decision; the crew ranked by <see cref="StoryWeight"/> and then
    /// by recency; cut to <see cref="DomainFeedLines"/>; put back in time order.
    /// The scale is the same one and deliberately so (see
    /// <see cref="StoryWeight"/>); what changes is the grouping, because the same
    /// refusal from a second creature is news about the domain while the same
    /// refusal from the same creature is not, and the defect being fixed is
    /// literally three lines about one creature.
    /// </para>
    ///
    /// <para>
    /// Routine still fills whatever the turning points leave over, so the feed is
    /// never blank and the first thousand ticks of a party — which have no turning
    /// points at all — still read as what the crew is doing. Before the edit those
    /// ticks were three lines about one or two creatures; they are now one line
    /// each about <see cref="DomainFeedLines"/> of them.
    /// </para>
    ///
    /// <para>
    /// The header carries what is off the panel, in the idiom the story header
    /// already uses: how many of the crew who have decided anything are shown, and
    /// how much of the journal mattered at all. "EVENT FEEDBACK · 3 of 9 crew ·
    /// 155 of 4425 mattered" is a panel that does not pretend to be the domain.
    /// </para>
    ///
    /// <para>
    /// The empty panel no longer repeats its own header. That doubling was
    /// accidental — the header was concatenated in front of a body that already
    /// had one — and a header that now carries counts cannot be printed twice
    /// without lying about them.
    /// </para>
    ///
    /// <para>
    /// Nothing here runs a tick. Every input is a field of the snapshot that has
    /// already been published, and the ranking is a function of the reason code
    /// alone: this is the projection side of the same "projection against world"
    /// line <see cref="CreatureStory"/> and <c>MapAccents</c> are written along.
    /// </para>
    /// </summary>
    private static string DomainFeed(PrototypeSnapshot state)
    {
        if (state.Events.Count == 0)
        {
            return "EVENT FEEDBACK\nNo events yet. Step or unpause to watch autonomous choices.";
        }

        var shown = DomainSelection(state.Events);
        var crew = state.Events.Select(@event => @event.CreatureId).Distinct().Count();
        var mattered = state.Events.Count(@event => StoryWeight(@event.ReasonCode) > 0);
        var head = string.Create(
            CultureInfo.InvariantCulture,
            $"EVENT FEEDBACK · {shown.Count} of {crew} crew · {mattered} of {state.Events.Count} mattered");
        return head + "\n" + string.Join(
            "\n",
            shown.Select(@event =>
                $"t{@event.LastTick} · {EventNarration.Describe(state, @event)}"));
    }

    /// <summary>
    /// Which of the domain's entries the feed spends its lines on, newest first.
    /// Public for the same reason <see cref="StorySelection"/> is: the test that
    /// says "the feed never disagrees with the journal" has to be able to state
    /// the rule rather than restate the code.
    ///
    /// <para>
    /// It is <see cref="StorySelection"/> with one argument changed — the beat is
    /// the creature rather than the kind of decision — and that is the whole of
    /// the difference between the two panels. Both run
    /// <see cref="MostSignificant"/>, so a change to how significance is spent is
    /// a change to both by construction and cannot drift apart into two rules that
    /// disagree.
    /// </para>
    ///
    /// <para>
    /// <b>One line per creature</b> is what makes the ranking worth anything, and
    /// it is the direct answer to what was measured: at tick 2400 the old feed's
    /// three lines were one creature stopped in one corridor three times. A
    /// creature's whole party is one line here — its most significant decision,
    /// and the newest of those if it took several — because the domain has nine of
    /// them and the question the panel answers is <em>who</em>. The rest of that
    /// creature's story is one click away.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PrototypeEvent> DomainSelection(IEnumerable<PrototypeEvent> journal) =>
        MostSignificant(journal, @event => @event.CreatureId, EqualityComparer<int>.Default, DomainFeedLines);

    /// <summary>
    /// One creature's party in at most <see cref="CreatureStoryLines"/> lines:
    /// the decisions that <b>meant something for its fate</b>, newest first.
    ///
    /// <para>
    /// It used to be the newest four entries of the journal, and that made the
    /// panel unreadable for the reason the numbers say: a creature's journal is
    /// 96.5 % waiting for stock, being blocked in a corridor and stepping aside,
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
    /// Routine still fills whatever the beats leave over, so a creature that
    /// nothing has happened to yet has a panel rather than a blank, and so the
    /// panel keeps saying what the creature has been doing lately.
    /// </para>
    ///
    /// <para>
    /// <b>Early in a party the panel is shorter than four lines, and that is the
    /// price of one line per kind.</b> The panel is as tall as the creature has
    /// <em>kinds</em> of decision, not entries: measured on <c>baseline</c>, at
    /// tick 20 eight creatures of nine have made one kind of decision and show
    /// one line, at tick 40 three of nine still do, and by tick 600 every one of
    /// them is back to four. Before Issue #140 all nine showed four lines from
    /// the first ticks — four lines that were the same sentence repeated, which
    /// is the shorter panel wearing a costume. The header still says what is
    /// behind it: "3 of 41 · 0 mattered" is a creature that has taken 41
    /// decisions of three kinds and had nothing happen to it yet.
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
    /// All three steps live in <see cref="MostSignificant"/>, which the domain
    /// feed runs with a different beat (Issue #145). What is stated there and
    /// matters here: <b>"the newest of that kind" means the last one written</b>,
    /// not the first one found at the highest tick, because a creature can leave
    /// two entries of one kind on one tick and only write order tells them apart —
    /// and on <c>baseline</c> it does, 14 times out of 48
    /// <c>combat_joined</c> entries.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PrototypeEvent> StorySelection(IEnumerable<PrototypeEvent> story) =>
        MostSignificant(story, @event => @event.ReasonCode, StringComparer.Ordinal, CreatureStoryLines);

    /// <summary>
    /// The one rule both panels are made of: <b>one line per beat, the beat's most
    /// significant entry, the beats that mean most, put back in time order</b>.
    /// What a beat is comes in as an argument — a kind of decision for one
    /// creature's story (Issue #140), a creature for the domain's feed (Issue
    /// #145) — and nothing else differs between them.
    ///
    /// <para>
    /// It is a shared function rather than two similar ones on purpose. The
    /// alternative was to copy the ranking into the feed, and a copy is how the
    /// two panels would come to disagree about what matters after the next change
    /// to <see cref="StoryWeight"/> — which is the failure Issue #145 was opened
    /// about, told forwards instead of backwards: the story panel was fixed by
    /// Issue #140 and the feed beside it was left showing the newest three
    /// entries.
    /// </para>
    ///
    /// <para>
    /// <b>The representative of a beat is its best by "what it meant, then
    /// when"</b>, and the later-written of two that tie. For a story that reduces
    /// to "the newest of that kind", because every entry in a reason-code group
    /// weighs the same; for the feed it is what makes a creature's line the worst
    /// thing that happened to it rather than the last thing it did.
    /// </para>
    ///
    /// <para>
    /// <b>"Later-written" and not "first found at the highest tick".</b> A
    /// creature can leave two entries of one kind on one tick, and then only the
    /// order the world wrote them in can tell them apart. On the shipped
    /// <c>baseline</c> party 14 of the 48 <c>combat_joined</c> entries are written
    /// one line before a second <c>combat_joined</c> on the same tick and only the
    /// second carries the wave number; <c>MaxBy</c> took the first and the panel
    /// read "joined the fight for wave ?." (Issue #140).
    /// </para>
    ///
    /// <para>
    /// Ties that survive that are broken by the order the journal is in, which is
    /// again the order the world wrote it in, so the same snapshot always produces
    /// the same panel.
    /// </para>
    /// </summary>
    /// <param name="journal">The entries to choose from. Already scoped by the caller.</param>
    /// <param name="beat">What counts as one line's worth of story.</param>
    /// <param name="sameBeat">When two entries belong to the same beat.</param>
    /// <param name="lines">How many lines the panel is worth.</param>
    private static IReadOnlyList<PrototypeEvent> MostSignificant<TBeat>(
        IEnumerable<PrototypeEvent> journal,
        Func<PrototypeEvent, TBeat> beat,
        IEqualityComparer<TBeat> sameBeat,
        int lines)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal
            .GroupBy(beat, sameBeat)
            .Select(group => group.Aggregate((best, next) =>
                Meaning(next).CompareTo(Meaning(best)) >= 0 ? next : best))
            .OrderByDescending(Meaning)
            .Take(lines)
            .OrderByDescending(@event => @event.LastTick)
            .ThenByDescending(@event => @event.FirstTick)
            .ToArray();
    }

    /// <summary>
    /// What an entry means and when it happened, in that order — the key both
    /// panels rank by, and the key a beat's representative is chosen by.
    /// </summary>
    private static (int Weight, int Tick) Meaning(PrototypeEvent @event) =>
        (StoryWeight(@event.ReasonCode), @event.LastTick);

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
