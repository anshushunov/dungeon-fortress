using System.Globalization;

using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// Reason codes turned into sentences with a name in front of them.
///
/// This is the minimal event adapter of step 2 of
/// <c>docs/product/PITCH.md</c> section 11, and Issue #117 asks for exactly the
/// minimum: "имя и предложение вместо reason code". Everything the feed used to
/// say was <c>t1325 · Мотылёк / combat_fled_morale</c> — a line a player cannot
/// read and cannot retell. What it says now is a sentence about a named creature,
/// built from the same canonical facts.
///
/// Two rules keep this from drifting into a second copy of the simulation:
///
/// <list type="bullet">
/// <item>every sentence is written out of the event's own <c>details</c>, its
/// job kind and its target, and nothing else. This layer computes nothing;</item>
/// <item>an unknown reason code is <b>refused</b> rather than guessed. There is
/// no catch-all arm. The same choice <c>HudText.WavePhase</c> made about the end
/// of a party, for the same reason: a code nobody taught the feed about would
/// otherwise be rendered as one of the codes it knows, and the player would read
/// a sentence that is not true.
/// <c>EventNarrationTests.Every_reason_code_the_matrix_produces_has_a_sentence</c>
/// runs the shipped fixtures and fails if a code reaches the feed without
/// one.</item>
/// </list>
/// </summary>
public static class EventNarration
{
    /// <summary>
    /// One line of the event feed: who, and what they decided.
    /// </summary>
    public static string Describe(PrototypeSnapshot state, PrototypeEvent @event)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(@event);
        var name = HudText.CreatureName(state, @event.CreatureId);
        var repeats = @event.Repeats > 1
            ? string.Create(CultureInfo.InvariantCulture, $" (x{@event.Repeats})")
            : string.Empty;
        return $"{name} {Sentence(@event.ReasonCode, @event.Details, @event.JobKind, @event.Target)}{repeats}";
    }

    /// <summary>
    /// The same for a creature's own last decision, which the inspector shows
    /// beside what it is doing now.
    /// </summary>
    public static string Describe(PrototypeSnapshot state, PrototypeCreatureSnapshot creature)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(creature);
        var decision = creature.LastDecision;
        return $"{creature.Name} {Sentence(decision.ReasonCode, decision.Details, decision.JobKind, decision.Target)}";
    }

    /// <summary>
    /// The verb phrase alone, without the name. Kept public because the map and
    /// the inspector both need it and neither wants the name repeated.
    /// </summary>
    public static string Sentence(
        string reasonCode,
        IReadOnlyDictionary<string, int> details,
        JobKind? jobKind,
        GridPoint? target)
    {
        ArgumentNullException.ThrowIfNull(reasonCode);
        ArgumentNullException.ThrowIfNull(details);
        var work = jobKind is { } kind ? Work(kind) : "work";
        var where = target is { } tile
            ? string.Create(CultureInfo.InvariantCulture, $" at ({tile.X},{tile.Y})")
            : string.Empty;

        return reasonCode switch
        {
            // Choosing work.
            "chosen_highest_priority" => $"took {work}{where}: you said it matters most.",
            "chosen_bottleneck" => $"took {work}{where}: the tightest link right now.",
            "chosen_affinity_match" => $"took {work}{where}: it is what they are good at.",
            "chosen_nearest" => $"took {work}{where}: the nearest job on offer.",
            "chosen_only_option" => $"took {work}{where}: the only work in reach.",
            "chosen_tie_break" => $"took {work}{where}: nothing told it from the next.",
            "chosen_need_hunger" => "went to eat: hunger came first.",
            "chosen_need_fatigue" => "went to lie down: exhaustion came first.",
            "chosen_muster" => "dropped everything for the muster point.",
            "chosen_ration" => "ate on the way to the muster point.",
            "chosen_traffic_yield" =>
                $"stepped aside{where} for #{Number(details, "beneficiaryId", "?")}.",
            "chosen_off_duty" =>
                $"went off to the quarters{where} after wave {Number(details, "wave", "?")}: " +
                "no work left.",

            // Waiting.
            "waiting_no_job_available" => "is standing about: nothing to do.",
            "waiting_input_missing" => $"waits on {work}: nothing to work with.",
            "waiting_storage_full" => "is waiting: the larder is full.",
            "waiting_stock_sufficient" => $"waits on {work}: enough already.",
            "waiting_crop_not_ripe" => "is waiting: nothing is ripe.",
            "waiting_blocked_by_other" => $"stopped{where}: somebody in the way.",
            "waiting_no_designation" => "would dig: no rock marked.",
            "waiting_no_blueprint" => "would build: no site marked.",
            "waiting_no_stockpile" => "left the stone: no stockpile.",
            "waiting_stockpile_full" => "left the stone: every cell spoken for.",

            // Refusing.
            "refused_zone_not_designated" => $"will not do {work}: no zone for it.",
            "refused_zone_unreachable" => $"cannot reach{where}: walled off or forbidden.",
            "refused_priority_zero" => $"stopped doing {work}: you set it to zero.",
            "refused_rule_reserve" => "went hungry: your ration reserve holds.",
            "refused_rule_min_satiety" =>
                $"will not train on {Number(details, "satiety", "that")} satiety: " +
                $"your rule says {Number(details, "threshold", "more")}.",
            "refused_too_exhausted" => "is too far gone to work.",
            "refused_injured" => "is too badly hurt to work.",
            "refused_place_of_panic" =>
                $"will not take {work}{where}: nerve broke {Place(details)}.",
            "refused_place_of_wound" =>
                $"will not take {work}{where}: put down {Place(details)}.",

            // The fight.
            "combat_joined" => $"joined the fight for wave {Number(details, "wave", "?")}.",
            "combat_refused_starving" => "was too hungry for the fight.",
            "combat_refused_injured" => "was too hurt for the fight.",
            "combat_absent_unreachable" => "was too far to reach the fight.",
            "combat_attack" =>
                $"struck raider {Number(details, "raiderId", "?")} for " +
                $"{Number(details, "damage", "?")}.",
            "combat_raider_downed" => $"put raider {Number(details, "raiderId", "?")} down.",
            "combat_fled_morale" =>
                $"broke and ran: {Number(details, "hpPercent", "?")}% health, " +
                $"{Number(details, "raidersNear", "?")} raiders close, " +
                $"{Number(details, "downedAlliesNear", "?")} ally down.",
            "combat_downed" => $"was put down by raider {Number(details, "raiderId", "?")}.",
            "combat_returned" => "came back to work after the wave.",
            // The two sentences that make a verdict readable as a cause rather
            // than as a coincidence (Issue #312). Both are exact
            // counterfactuals: the simulation only writes them when the grudge
            // is what decided, which is why they may say so plainly.
            "combat_refused_grudge" =>
                $"would not stand for wave {Number(details, "wave", "?")}: " +
                $"a grudge of {Number(details, "grudge", "?")} against " +
                $"{Number(details, "holding", "?")} holding it.",

            // <b>The contest of the wounded</b> (Issue #431, §4). Two sentences
            // and not one, because "did not stand" is an effect visible only by
            // absence: slice 3 twice took an `ADJUST` for a mechanic that was
            // true and unreadable, and the answer both times was to name the
            // cause at the moment it happened rather than to leave the player
            // counting who is missing from the line.
            //
            // <b>The verdict is named as the cause only where it decided</b>
            // (§3.5). For `combat_spared_wound` that is the published
            // `verdictDecided`, which the simulation computes by replaying the
            // contest without the two terms a verdict writes. The sign is not in
            // the details and does not need to be: on a `spared` outcome only a
            // reward can be what flipped it — removing the verdict's terms lowers
            // both sides, so an outcome that stops being `spared` without them is
            // one where the reward's own share of the benefit outweighed the fear
            // of the domain it was weighed against.
            //
            // `combat_pressed_wound` carries no such flag because the simulation
            // only writes that code where the fear of the domain <em>was</em> the
            // reason: without it the sparing side would have won. So it may say
            // so plainly, exactly as the two sentences of Issue #312 above may.
            "combat_spared_wound" =>
                $"would not stand for wave {Number(details, "wave", "?")}: " +
                $"sparing a hurt {InjuredPartName(details)}" +
                $"{(Number(details, "verdictDecided", "0") == "1" ? ", and your reward is what tipped it" : string.Empty)} " +
                $"({Number(details, "spare", "?")} against {Number(details, "press", "?")}).",
            "combat_pressed_wound" =>
                $"stood for wave {Number(details, "wave", "?")} on a hurt " +
                $"{InjuredPartName(details)}: it fears you more than the wound " +
                $"({Number(details, "press", "?")} against {Number(details, "spare", "?")}); " +
                $"{Number(details, "grudge", "?")} grudge for it.",

            // The moment of truth.
            "verdict_rewarded" =>
                $"was rewarded for wave {Number(details, "wave", "?")}; " +
                $"stands at {Number(details, "benefit", "?")} benefit.",
            "verdict_punished" =>
                $"was punished for wave {Number(details, "wave", "?")}, and had it coming; " +
                $"stands at {Number(details, "fear", "?")} fear.",
            "verdict_punished_without_fault" =>
                $"was punished for wave {Number(details, "wave", "?")} without fault; " +
                $"{Number(details, "fear", "?")} fear now, {Number(details, "grudge", "?")} " +
                "grudge later.",
            "verdict_ignored" =>
                $"was left unanswered after wave {Number(details, "wave", "?")}; " +
                $"the grudge stands at {Number(details, "grudge", "?")}.",

            // Wounds. The localised one names the part, because naming it is the
            // whole of Issue #409: «конкретный Кремень без глаза» is a sentence
            // the player has to be able to read, and a feed that said only "was
            // hurt" would leave the localisation inside the snapshot.
            "injury_localised" => details.TryGetValue("severity", out var severity) &&
                severity >= (int)InjuryKind.Heavy
                    ? $"took a crippling blow to the {InjuredPartName(details)}."
                    : $"took a blow to the {InjuredPartName(details)}.",
            "injury_limped" => "lost a step to a bad leg.",
            "injury_stunned" => "reeled from a blow to the head and lost the moment.",
            "injury_tended" => "was carried off the floor, badly hurt.",
            "injury_mending" => "is mending: the wound is no longer bad.",
            "injury_healed" => "is whole again.",

            // Digging.
            "dig_started" => $"started cutting the rock{where}.",
            "dig_completed" => $"cut through{where}; the block is down.",
            "dig_cancelled" => $"stopped digging{where}.",
            "dig_unreachable" => $"cannot reach the rock{where}: nowhere to stand.",

            // Stone.
            "stone_picked_up" => $"picked up the stone{where}.",
            "stone_stored" => $"put the stone away{where}.",
            "stone_spilled" => $"could not fit it all; left the rest{where}.",
            "stone_target_replanned" => $"changed where the stone goes{where}.",
            "stone_haul_cancelled" => $"put the stone down{where}: no cell takes it.",
            "stone_unreachable" => "cannot reach a stockpile with the stone.",
            "stone_delivered" => $"delivered the stone to the site{where}.",

            // Building.
            "build_started" => $"started building{where}.",
            "build_completed" => $"finished the post{where}.",
            "build_cancelled" => $"stopped building{where}.",
            "build_no_stone" => "would build: no stone anywhere.",
            "build_waiting_material" => "would build: the stone is not here yet.",
            "build_unreachable" => $"cannot get onto the site{where}.",

            _ => throw new ArgumentOutOfRangeException(
                nameof(reasonCode),
                reasonCode,
                "The event feed has no sentence for this reason code and will not invent one. " +
                "Add it to EventNarration.Sentence next to the code it belongs with."),
        };
    }

    /// <summary>
    /// The name a player would use for a kind of work, rather than the enum's.
    /// </summary>
    public static string Work(JobKind kind) => kind switch
    {
        JobKind.Harvest => "harvesting",
        JobKind.Haul => "carrying",
        JobKind.Cook => "cooking",
        JobKind.Rest => "resting",
        JobKind.Drill => "training",
        JobKind.Watch => "the watch",
        JobKind.Dig => "digging",
        JobKind.Build => "building",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unnamed kind of work."),
    };

    /// <summary>
    /// Where a memory of place points, read from the two numbers the event
    /// carries. It is a phrase rather than a coordinate pair on its own so that
    /// the two refusal sentences read as sentences.
    /// </summary>
    private static string Place(IReadOnlyDictionary<string, int> details) =>
        details.TryGetValue("placeX", out var x) && details.TryGetValue("placeY", out var y)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"at ({x},{y}) t{(details.TryGetValue("sinceTick", out var tick) ? tick : 0)}")
            : "there";

    /// <summary>
    /// The part a journal entry names, in the player's words. It is read out of
    /// <see cref="BodyParts.All"/> rather than out of a list of its own, so a
    /// fifth part cannot exist in the simulation and be missing from the feed.
    /// An index outside the four is reported as such and not guessed at.
    /// </summary>
    private static string InjuredPartName(IReadOnlyDictionary<string, int> details) =>
        details.TryGetValue("part", out var part) && part >= 0 && part < BodyParts.Count
            ? BodyParts.All[part] switch
            {
                BodyPart.Head => "head",
                BodyPart.Torso => "body",
                BodyPart.Arm => "arm",
                BodyPart.Leg => "leg",
                _ => "body",
            }
            : "body";

    private static string Number(IReadOnlyDictionary<string, int> details, string key, string fallback) =>
        details.TryGetValue(key, out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : fallback;
}
