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
    /// </summary>
    public static string Summary(HudViewState view) => Summary(view, view.Projection);

    private static string Summary(HudViewState view, MapProjection projection)
    {
        var state = view.Snapshot;
        var stock = state.Stocks;
        var domain = state.Domain;
        return
            $"{view.Fixture.ToUpperInvariant()}  •  t{state.Tick}  •  {(view.Paused ? "PAUSED" : $"{view.Speed:0.#}x")}" +
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
    /// The last three autonomous choices, newest first, plus the diagnostics
    /// count. The header is deliberately part of both the empty and the populated
    /// case, which is why the empty panel repeats it.
    /// </summary>
    public static string Feedback(HudViewState view)
    {
        var state = view.Snapshot;
        var eventText = state.Events.Count == 0
            ? "EVENT FEEDBACK\nNo events yet. Step or unpause to watch autonomous choices."
            : string.Join(
                "\n",
                state.Events.TakeLast(3).Reverse().Select(@event =>
                    $"t{@event.LastTick} · {CreatureName(state, @event.CreatureId)}\n{@event.ReasonCode}"));
        return
            "EVENT FEEDBACK\n" + eventText +
            $"\n\nDiagnostics: {view.DiagnosticCount} (structured JSON is emitted by smoke/capture).";
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
    /// </summary>
    public static string WavePhase(PrototypeSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var threat = state.Threat;
        var waves = $"{threat.WaveNumber}/{threat.WaveCount}";
        if (state.SessionResult.Outcome is { } outcome)
        {
            return outcome == "held"
                ? $"DOMAIN HELD {threat.WaveCount}/{threat.WaveCount}"
                : $"DOMAIN FELL · wave {waves}";
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
