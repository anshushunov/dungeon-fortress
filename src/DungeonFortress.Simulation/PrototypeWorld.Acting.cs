namespace DungeonFortress.Simulation;

// The act a creature performs on its tick: the dispatcher, and what a
// creature does when it is off duty.
public sealed partial class PrototypeWorld
{
    private void ActCreatures()
    {
        // Nerve is asked before anybody acts, so a defender that broke this tick
        // leaves instead of striking, and everyone reads the same world when
        // they answer. It sits here rather than in the raiders' subphase because
        // fear is now a standing condition rather than a reflex to one event:
        // the tick after an ally falls is the earliest anyone can react to it.
        ApplyMorale();

        foreach (var creature in _creatures.OrderBy(creature => creature.Id))
        {
            // Anything that occupies a creature ends its idleness, and the count
            // starts again from zero afterwards (Issue #201). It is written here,
            // before the branches, because each of those branches leaves the loop
            // and the count would otherwise survive a whole fight: a creature
            // would then walk off on the first tick after the wave rather than
            // after standing about for OffDutyDelayTicks.
            if (creature.IsMustering ||
                creature.Mode is CreatureMode.Fighting
                    or CreatureMode.Fled
                    or CreatureMode.Downed
                    or CreatureMode.Eating)
            {
                creature.IdleTicks = 0;
                creature.OffDutyTarget = null;
            }

            if (creature.Mode == CreatureMode.Fighting)
            {
                ActCombatant(creature);
                continue;
            }

            if (creature.Mode == CreatureMode.Fled)
            {
                // A runner honours a yield the same way a worker does, and for
                // the same reason: `TryPlanYield` writes `chosen_traffic_yield`
                // into the canonical log and books the tile for this tick. A mode
                // that took the booking and then walked its own way would make
                // both of those a lie — and it did, for tens of ticks a party,
                // because a broken defender now spends real time in a corridor
                // instead of vanishing to the far wall.
                if (creature.TrafficTarget is { } refugeYield)
                {
                    if (Move(creature, refugeYield))
                    {
                        creature.YieldCount++;
                        creature.LastYieldTick = CurrentTick;
                    }

                    creature.TrafficTarget = null;
                    continue;
                }

                RunFromTheFight(creature);
                continue;
            }

            if (creature.Mode == CreatureMode.Downed)
            {
                continue;
            }

            if (creature.TrafficTarget is { } trafficTarget)
            {
                if (Move(creature, trafficTarget))
                {
                    creature.YieldCount++;
                    creature.LastYieldTick = CurrentTick;
                }

                creature.TrafficTarget = null;
                continue;
            }

            if (creature.IsMustering)
            {
                ActMuster(creature);
                continue;
            }

            if (creature.Mode == CreatureMode.Eating)
            {
                ActEating(creature);
                continue;
            }

            if (creature.CurrentJob is { } job)
            {
                creature.IdleTicks = 0;
                creature.OffDutyTarget = null;
                creature.LeftTheFight = false;
                ActJob(creature, job);
                continue;
            }

            ActOffDuty(creature);
        }
    }

    /// <summary>
    /// What a creature does when there is no work for it: after
    /// <see cref="PrototypeTuning.OffDutyDelayTicks"/> ticks of standing about it
    /// walks to the quarters instead of staying where it happens to be.
    ///
    /// <para><b>Why the quarters and not somewhere else.</b> Issue #201 names two
    /// candidates, the quarters and a watch post, and asks that the choice be made
    /// once and argued. The quarters are the only zone in the prototype whose
    /// meaning is "where a creature is when it is not working" — it is the zone
    /// the player paints for bunks, and the one the rest job already sends people
    /// to. A watch post is the other thing: standing on one **is** work
    /// (<see cref="JobKind.Watch"/>), so if a post needed a body,
    /// <see cref="MatchJobs"/> would already have given somebody that job. Sending
    /// the jobless there would stage work that does not exist, and it would
    /// collide with the second meaning the watch zone carries — it is where
    /// <see cref="MusterTargetFor"/> assembles everybody when a wave is coming.
    /// Mixing "нечего делать" with "сбор по тревоге" on the same tiles would make
    /// both unreadable.</para>
    ///
    /// <para><b>Why it is not a job.</b> Going off duty produces no
    /// <see cref="JobState"/>, holds no reservation and blocks nothing: a creature
    /// on its way to the bunks is available to the matching on every tick of the
    /// walk, and the moment work appears it takes it. Modelling it as a job would
    /// have made idleness compete with work, which is the opposite of the
    /// intent.</para>
    ///
    /// <para><b>What it deliberately does not fix.</b> The jam itself — bodies
    /// blocking each other — is cell occupancy, Issue #76, left on slice 6 by the
    /// owner's decision of 2026-08-03. And a creature standing because it cannot
    /// reach its zone (<c>refused_zone_unreachable</c>) is a different question
    /// that this rule must not be credited with answering: it moves such a
    /// creature too, but the reason it was standing is not "there is no
    /// work".</para>
    /// </summary>
    private void ActOffDuty(CreatureState creature)
    {
        if (!creature.LeftTheFight || creature.Mode != CreatureMode.Waiting)
        {
            creature.IdleTicks = 0;
            creature.OffDutyTarget = null;
            return;
        }

        creature.IdleTicks++;
        if (creature.IdleTicks <
            PrototypeTuning.OffDutyDelayTicks +
            creature.Id * PrototypeTuning.OffDutyStaggerTicks)
        {
            return;
        }

        if (OffDutyTargetFor(creature) is not { } target)
        {
            creature.OffDutyTarget = null;
            return;
        }

        if (creature.Position == target)
        {
            // Arrived: the creature is off the ground the fight was fought on, so
            // the trigger is spent. It keeps standing here until work appears —
            // the ordinary idle behaviour, which this rule deliberately leaves
            // alone.
            creature.LeftTheFight = false;
            creature.OffDutyTarget = null;
            return;
        }

        if (creature.OffDutyTarget != target)
        {
            creature.OffDutyTarget = target;
            RecordDecision(
                creature,
                "chosen_off_duty",
                // The tile and the wave it followed — no tick count. Two things
                // are being got right here, and both were measured rather than
                // reasoned.
                //
                // A varying `idleTicks` would make every departure its own event
                // even when nothing else about it differed; the story panel shows
                // one line per **kind** of decision, and the panel would fill with
                // one sentence.
                //
                // The wave number answers "after which fight", which is the
                // honest clock for this decision: a creature goes off duty after
                // a fight, not after a tick count.
                //
                // What it does **not** do is make two departures of one creature
                // distinguishable as sentences — an earlier version of this
                // comment claimed that, and independent review of PR #217
                // disproved it by measurement: creatures #3, #5 and #7 of
                // `baseline` each leave twice while wave 1 is still the last
                // resolved one, so both entries carry wave=1 and render word for
                // word alike. What tells them apart on the panel is the tick
                // prefix of the line, and nothing here needs them told apart —
                // the story panel keeps one entry per kind of decision.
                new Dictionary<string, int>
                {
                    ["targetX"] = target.X,
                    ["targetY"] = target.Y,
                    ["wave"] = _waves.LastOrDefault(wave => wave.Outcome is not null)?.Number ?? 0,
                },
                target: target);
        }

        creature.Mode = CreatureMode.Moving;
        _ = Move(creature, target);
    }

    /// <summary>
    /// The tile in the quarters this creature goes to when it is off duty, or
    /// null when there is no quarters zone at all.
    ///
    /// <para>One tile per creature, chosen by id the same way
    /// <see cref="MusterTargetFor"/> does, and for the same reason: a rule that
    /// sends everybody to the nearest free tile would make the group converge on
    /// one doorway and produce exactly the clinch this issue is trying to reduce.
    /// Choosing by id is also what keeps the result deterministic — it does not
    /// depend on who asked first or on where anybody is standing.</para>
    ///
    /// <para><b>Bunks are excluded, and that is not a detail.</b> A creature
    /// standing idle on a bunk occupies the tile a <see cref="JobKind.Rest"/> job
    /// needs, and <see cref="Move"/> refuses a step onto an occupied tile — so
    /// the first version of this rule parked the jobless on the beds and the
    /// tired could not lie down. Measured, not reasoned: it turned twelve tests
    /// of the simulation red, among them
    /// <c>Rest_jobs_are_personal_start_at_fifty_and_preempt_only_above_seventy_five</c>
    /// and <c>A_party_that_wins_its_fights_does_not_end_it_starving</c>. Off duty
    /// means standing in the quarters, not lying in somebody's bed.</para>
    ///
    /// <para>When there are more creatures than free tiles in the zone, the
    /// overflow stands next to it: passable tiles ordered by their distance to
    /// the zone, then by tile, which is the same overflow rule as the
    /// muster's.</para>
    /// </summary>
    private GridPoint? OffDutyTargetFor(CreatureState creature)
    {
        var zone = _zones[ZoneKind.Quarters];
        var standing = zone.Where(tile => _map[tile] != TileKind.Bunk).ToList();
        if (standing.Count == 0)
        {
            return null;
        }

        if (creature.Id < standing.Count)
        {
            return standing[creature.Id];
        }

        return _map.PassableTiles()
            .Where(tile => !zone.Contains(tile) && tile != PrototypeMap.Gate)
            .Select(tile => new
            {
                Tile = tile,
                Distance = zone
                    .Select(target => _map.Distance(tile, target, _zones[ZoneKind.Forbidden]))
                    .Where(distance => distance is not null)
                    .Min() ?? int.MaxValue,
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Tile)
            .ElementAtOrDefault(creature.Id - standing.Count)
            ?.Tile;
    }
}
