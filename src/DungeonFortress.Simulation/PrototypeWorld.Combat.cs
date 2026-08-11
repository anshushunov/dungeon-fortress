namespace DungeonFortress.Simulation;

// The raid: waves announced and arriving, raiders acting, the fight
// resolved, and what it leaves behind — morale, renown, memory of place.
public sealed partial class PrototypeWorld
{
    /// <summary>
    /// A wave's composition is fixed at its own announce tick from the renown
    /// standing right then. Deciding it once and storing it is what makes the
    /// announcement honest: the countdown the player reads names the raiders
    /// that will actually walk through the gate.
    /// </summary>
    private void AnnounceWaves()
    {
        foreach (var wave in _waves.Where(item => !item.Announced && item.AnnounceTick <= CurrentTick))
        {
            var renown = Renown();
            wave.Announced = true;
            wave.RenownAtAnnounce = renown;
            wave.RaiderCount = Math.Min(
                PrototypeTuning.WaveMaxRaiders,
                PrototypeTuning.WaveBaseRaiders + renown / PrototypeTuning.RenownPerExtraRaider);
            wave.RaiderMight = PrototypeTuning.RaiderMightBase +
                renown / PrototypeTuning.RenownPerRaiderMight;
        }
    }

    /// <summary>
    /// The wave in hand: the one that arrived and has not been resolved,
    /// otherwise the next one that has not arrived yet. Null only once the last
    /// wave is over.
    /// </summary>
    private WaveState? CurrentWave() =>
        _waves.FirstOrDefault(wave => wave.Outcome is null);

    private WaveState? ActiveWave() =>
        _waves.FirstOrDefault(wave => wave.Outcome is null && wave.ArriveTick <= CurrentTick);

    private void EnterRaiders()
    {
        if (ActiveWave() is not { } wave)
        {
            return;
        }

        while (wave.Entered < wave.RaiderCount &&
               CurrentTick >= wave.ArriveTick + wave.Entered * PrototypeTuning.RaiderEntryInterval)
        {
            // Who walks in is decided by NextRaiderOf; how many walk in is decided
            // above it by the wave's own composition, which nothing in slice 5
            // touches. A returning raider therefore takes one of these places and
            // never adds one (Issue #358).
            _raiders.Add(NextRaiderOf(wave));
            wave.Entered++;
        }

        if (wave.Entered == wave.RaiderCount)
        {
            CloseReturnsFor(wave);
        }

        if (!wave.Arrived)
        {
            // Renown is credited the moment a wave reaches the domain, not when
            // it is beaten: the domain is now a place raiders travel to, and
            // that fact cannot be taken back by losing the fight.
            wave.Arrived = true;
            // The trend the HUD draws is measured from here, so both numbers are
            // read once, at the wave that just landed, and never recomputed.
            _renownAtPreviousWave = Renown();
            _strengthAtPreviousWave = DomainStrength();
        }
    }

    private void UpdateCombatParticipation()
    {
        if (ActiveWave() is not { } wave)
        {
            return;
        }

        // Asked first and asked every tick, because being unable to stand is not
        // a question of canvassing: the recheck period below is about how often
        // the domain is asked for volunteers.
        LeaveTheLineWhenTooHungryToStandInIt(wave);

        // The first check happens on the wave's own arrival tick; after that the
        // rest of the domain is asked again on the same period as before. Both
        // are relative to this wave, not to a single session-wide raid tick.
        var sinceArrival = CurrentTick - wave.ArriveTick;
        if (sinceArrival != 0 && sinceArrival % PrototypeTuning.CombatJoinRecheck != 0)
        {
            return;
        }

        foreach (var creature in _creatures.Where(c => c.Mode is not (CreatureMode.Fighting or CreatureMode.Fled or CreatureMode.Downed)).OrderBy(c => c.Id))
        {
            var failed = new Dictionary<string, int> { ["wave"] = wave.Number };
            if (creature.Injury == InjuryKind.Heavy)
            {
                failed["injured"] = 1;
                RecordDecision(creature, "combat_refused_injured", failed);
                continue;
            }
            if (creature.Satiety < PrototypeTuning.CombatJoinSatiety)
            {
                failed["satiety"] = creature.Satiety;
                failed["threshold"] = PrototypeTuning.CombatJoinSatiety;
                RecordDecision(creature, "combat_refused_starving", failed);
                continue;
            }

            if (ResentmentOutweighsTheLine(creature))
            {
                failed["grudge"] = creature.Loyalty.Grudge;
                failed["holding"] = HoldingTheLine(creature);
                SpendGrudge(creature);
                RecordDecision(creature, "combat_refused_grudge", failed);
                continue;
            }

            var distance = _map.Distance(creature.Position, PrototypeMap.LarderTiles[0], _zones[ZoneKind.Forbidden]);
            if (!creature.IsMustering && distance is > PrototypeTuning.EngageRadius)
            {
                RecordDecision(creature, "combat_absent_unreachable", new Dictionary<string, int> { ["distance"] = distance ?? -1, ["wave"] = wave.Number });
                continue;
            }

            if (creature.CurrentJob is not null)
            {
                CancelJob(creature, "combat_joined");
            }
            creature.IsMustering = false;
            creature.MusterNeedsRation = false;
            creature.MealReserved = false;
            creature.Mode = CreatureMode.Fighting;
            RecordDecision(creature, "combat_joined", new Dictionary<string, int> { ["readiness"] = ComputeReadiness(creature), ["wave"] = wave.Number });
        }
    }

    /// <summary>
    /// Somebody too hungry to stand in the line leaves it, and leaves it here.
    ///
    /// <para><b>Why this method exists at all.</b> The departure already happened
    /// before Issue #333 — it just happened in the wrong place. A fighter whose
    /// satiety fell below <see cref="PrototypeTuning.EatThreshold"/> was taken out
    /// of the line by <c>DecideNeedsAndMuster</c> two phases later, by having its
    /// mode overwritten: nothing in the journal said the line had lost anybody,
    /// the wave never heard of it, and the next recheck could hand the creature
    /// back just as quietly. Removing the overwrite and stopping there was
    /// measured and rejected: with hunger unable to end anybody's fight, a party
    /// that stopped feeding at t1400 outscored one that kept feeding, 138 against
    /// 133, and «обнищание не окупается» is a promise rather than a corridor
    /// (<c>evidence/333-variants.json</c>). So the departure is kept and moved to
    /// the one phase that is allowed to decide who is in the line.</para>
    ///
    /// <para><b>Entering is harder than staying</b> — owner's decision of
    /// 2026-08-11. <see cref="PrototypeTuning.CombatJoinSatiety"/> is 30 and
    /// <see cref="PrototypeTuning.CombatHoldSatiety"/> is 20, and the ten points
    /// between them are the whole point: one threshold for both would let a
    /// creature that left be re-admitted at the very satiety it left at, which is
    /// the walking in and out this issue exists to remove. Both numbers and what
    /// is known about how well they are chosen are argued in
    /// <see cref="PrototypeTuning"/>.</para>
    ///
    /// <para><b>It does not fire on any shipped journal, and that is stated
    /// rather than hidden.</b> Ten points of satiety is fifty ticks of a wave, and
    /// the longest spell anybody spends in the line over the nine shipped runs is
    /// 43. A rule the fixtures cannot reach is exactly what independent review of
    /// PR #328 deleted from this same method, so this one is not left to a fixture
    /// to prove: it is asserted on a party built for it in
    /// <c>PrototypeCombatModeHoldTests</c>, and there is a mutant against that
    /// assertion.</para>
    /// </summary>
    private void LeaveTheLineWhenTooHungryToStandInIt(WaveState wave)
    {
        foreach (var creature in _creatures
                     .Where(creature =>
                         creature.Mode == CreatureMode.Fighting &&
                         creature.Satiety < PrototypeTuning.CombatHoldSatiety)
                     .OrderBy(creature => creature.Id)
                     .ToArray())
        {
            creature.Mode = CreatureMode.Waiting;
            // Counted with the wave's other losses, because from the wave's side
            // this is the same fact as a defender running: the domain has one
            // fewer body in the line for the rest of the fight. What tells the two
            // apart is the journal entry, which names the cause — the same split
            // the codebase already keeps between a tally and a reason.
            wave.CountDefenderLeftStarving();
            RecordDecision(
                creature,
                "combat_left_starving",
                new Dictionary<string, int>
                {
                    ["satiety"] = creature.Satiety,
                    ["threshold"] = PrototypeTuning.CombatHoldSatiety,
                    ["wave"] = wave.Number,
                });
        }
    }

    /// <summary>
    /// Whether accumulated resentment outweighs everything that would hold this
    /// creature in the line: what the domain has given it, what it is afraid of,
    /// and its own steadiness.
    ///
    /// <para>A contest and not a gate, and that distinction is half (b) of the
    /// fifth condition of admissibility of a verdict value (Issue #167) in
    /// executable form: no single verdict can either cause or prevent this on its
    /// own. A punishment adds fear, which both holds the creature and hides the
    /// grudge; a reward adds benefit on the other side of the same
    /// comparison.</para>
    ///
    /// <para><b>Asked only of creatures that are not in the line yet</b>, and the
    /// omission is a finding rather than an oversight. A second pass over the
    /// people already fighting existed and wrote <c>combat_left_grudge</c>;
    /// independent review of PR #328 showed by probe that it is <b>structurally
    /// unreachable</b>. Joining and leaving were decided by this very comparison,
    /// and nothing inside a fight moves either side of it: a grudge is credited
    /// by coercion and none happens in combat, while fear only grows there, so a
    /// creature that passed the test on the way in can never fail it on the way
    /// out. A term the mechanic cannot reach is a promise the contract does not
    /// keep, so it was removed rather than argued for.</para>
    /// </summary>
    private static bool ResentmentOutweighsTheLine(CreatureState creature) =>
        ReleasedGrudge(creature) * PrototypeTuning.LoyaltyRefuseGrudgeWeight >
        HoldingTheLine(creature);

    private static int HoldingTheLine(CreatureState creature) =>
        creature.Loyalty.Benefit + creature.Loyalty.Fear +
        creature.Grit * PrototypeTuning.LoyaltyRefuseGritWeight;

    private void ActCombatant(CreatureState creature)
    {
        var target = _raiders.Where(raider => raider.Mode == RaiderMode.Raiding)
            .OrderBy(raider => Manhattan(creature.Position, raider.Position))
            .ThenBy(raider => raider.Id)
            .FirstOrDefault();
        if (target is null)
        {
            return;
        }

        // Reach is read from the attack, not written into the rule. Raising
        // MeleeAttackRange is the whole edit a ranged weapon would need here.
        if (Manhattan(creature.Position, target.Position) > PrototypeTuning.MeleeAttackRange)
        {
            var destination = ApproachTile(creature, target);
            var next = _map.NextStep(creature.Position, destination, _zones[ZoneKind.Forbidden]);
            if (next is { } step)
            {
                _ = Move(creature, step);
            }
            return;
        }

        var damage = Math.Max(PrototypeTuning.DamageFloor,
            creature.Might + ComputeReadiness(creature) / PrototypeTuning.DamageReadinessDivisor + CombatJitter(PrototypeTuning.DamageJitter));
        target.Hp -= damage;
        // The raider writes down what this cost it and where it was standing. It
        // is the source of both the scar and the memory of place a return carries
        // (Issue #358), and it is recorded here rather than derived later because
        // "where" stops being answerable the moment the raider takes its next step.
        target.RecordBlow(damage, CurrentTick);
        RecordDecision(creature, "combat_attack", new Dictionary<string, int> { ["raiderId"] = target.Id, ["damage"] = damage });
        if (target.Hp <= 0)
        {
            target.Hp = 0;
            DropRaiderMeals(target);
            target.Mode = RaiderMode.Downed;
            _raidersDownedTotal++;
            // The deed the moment of truth is mostly about. ADR 0019's own
            // example of a card the domain owes an answer to is «он убил героя,
            // а ты не наградил», so the count is kept — and deliberately never
            // converted into a magnitude of standing, because paying it out as
            // benefit here would be the game handing out the reward the player
            // is being asked about.
            creature.Loyalty.RaidersDownedSinceLastCard++;
            RecordDecision(creature, "combat_raider_downed", new Dictionary<string, int> { ["raiderId"] = target.Id });
        }
    }

    /// <summary>
    /// Where a fighter walks when it walks towards a raider: a free tile
    /// **beside** the raider, and the raider's own tile only when there is no
    /// free one to be had.
    ///
    /// The destination is the whole of Issue #129. Sending everybody to the
    /// raider's own tile gives every fighter that shares a nearest enemy one and
    /// the same point to path to, and <see cref="PrototypeMap.NextStep"/> is a
    /// BFS with one tie-break, so it hands them all the same corridor: they
    /// arrive in a column and only the head of it is ever in reach. Measured on
    /// the seed matrix before this method existed, three defenders were never
    /// simultaneously adjacent to one raider in six whole parties, while a free
    /// tile beside the target went unused on 158 to 333 fighter-ticks a party
    /// (<c>evidence/129-before.json</c>).
    ///
    /// The rule, stated so that it can be checked rather than read:
    ///
    /// <list type="number">
    /// <item><description>the candidates are the four neighbours of the target,
    /// visited in the map's own order — north, east, south, west — which is the
    /// same order <see cref="PrototypeMap.NextStep"/> breaks its ties
    /// in;</description></item>
    /// <item><description>a candidate is **free** when it is passable, is not in
    /// the <see cref="ZoneKind.Forbidden"/> zone, carries no other creature of
    /// the domain — a body on the floor counts, exactly as it does in
    /// <see cref="Move"/> — and carries no raider. The last clause is the
    /// occupancy rule of contract 4.1 rather than the implementation's: standing
    /// on a raider is not a place to stand. It is also the one clause here that
    /// no check holds on its own, and the reason is measured rather than assumed:
    /// with it a fighter shares a tile with a raider on 8 fighter-ticks over the
    /// matrix and without it on 12, because most of that overlap is the raider
    /// walking onto the fighter — <c>ActRaiders</c> assigns a position with no
    /// occupancy check at all (<c>evidence/129-mutations.json</c>);</description></item>
    /// <item><description>of the free candidates, the fighter takes the one
    /// nearest **to itself** by the map, and the first in the order above when
    /// two are equally near. Nearest to itself is what spreads the fighters out:
    /// a single fixed corner would just move the column one tile
    /// sideways;</description></item>
    /// <item><description>if no neighbour is both free and reachable, the
    /// destination stays the raider's own tile, which is what it always was. The
    /// rule prefers a free place to the queue; it does not invent a new way to
    /// stand still.</description></item>
    /// </list>
    ///
    /// Everything here is a function of the published world and is evaluated in
    /// ascending creature id inside the tick, so two fighters never quietly pick
    /// the same tile: the second one sees the first already standing on it. What
    /// this method deliberately does not do is walk around an occupied tile —
    /// <see cref="PrototypeMap.NextStep"/> still ignores bodies, and making it
    /// see them is <see href="https://github.com/anshushunov/dungeon-fortress/issues/76">Issue #76</see>.
    /// </summary>
    private GridPoint ApproachTile(CreatureState creature, RaiderState target)
    {
        GridPoint? chosen = null;
        var shortest = int.MaxValue;
        foreach (var candidate in PrototypeMap.Neighbors(target.Position))
        {
            if (!IsFreeApproachTile(creature, candidate))
            {
                continue;
            }

            if (_map.Distance(creature.Position, candidate, _zones[ZoneKind.Forbidden])
                is not { } steps ||
                steps >= shortest)
            {
                continue;
            }

            shortest = steps;
            chosen = candidate;
        }

        return chosen ?? target.Position;
    }

    private bool IsFreeApproachTile(CreatureState creature, GridPoint tile)
    {
        return _map.IsPassable(tile) &&
            !_zones[ZoneKind.Forbidden].Contains(tile) &&
            !_creatures.Any(other => other != creature && other.Position == tile) &&
            !_raiders.Any(raider => raider.Mode == RaiderMode.Raiding && raider.Position == tile);
    }

    private void ActRaiders()
    {
        foreach (var raider in _raiders.Where(raider => raider.Mode == RaiderMode.Raiding).OrderBy(raider => raider.Id))
        {
            var defender = _creatures.Where(creature => creature.Mode == CreatureMode.Fighting)
                .OrderBy(creature => Manhattan(creature.Position, raider.Position))
                .ThenBy(creature => creature.Id)
                .FirstOrDefault();
            if (defender is not null &&
                Manhattan(defender.Position, raider.Position) <= PrototypeTuning.RaiderAttackRange)
            {
                var damage = Math.Max(PrototypeTuning.DamageFloor,
                    raider.Might - ComputeReadiness(defender) / PrototypeTuning.ArmourReadinessDivisor + CombatJitter(PrototypeTuning.DamageJitter));
                defender.Hp -= damage;
                if (defender.Hp * 100 <= defender.MaxHp * PrototypeTuning.LightInjuryShare && defender.Injury == InjuryKind.None)
                {
                    defender.Injury = InjuryKind.Light;
                    defender.RecoveryTicks = 0;
                }
                if (defender.Hp <= 0)
                {
                    defender.Hp = 0;
                    defender.Injury = InjuryKind.Heavy;
                    defender.Mode = CreatureMode.Downed;
                    CurrentWave()?.CountDefenderDowned();
                    Remember(defender, "wound");
                    RecordDecision(defender, "combat_downed", new Dictionary<string, int> { ["raiderId"] = raider.Id, ["damage"] = damage });
                }
                continue;
            }

            var target = raider.ReturningToGate
                ? PrototypeMap.Gate
                : PrototypeMap.LarderTiles[0];
            if (raider.Position == PrototypeMap.LarderTiles[0] &&
                raider.CarryingMeals < PrototypeTuning.CarryCapacity)
            {
                if (_stockMeals == 0)
                {
                    raider.ReturningToGate = true;
                    target = PrototypeMap.Gate;
                }
                else
                {
                    raider.StealTicks++;
                    if (raider.StealTicks < PrototypeTuning.StealPeriod)
                    {
                        continue;
                    }

                    _stockMeals--;
                    raider.CarryingMeals++;
                    raider.StealTicks = 0;
                    if (raider.CarryingMeals >= PrototypeTuning.CarryCapacity)
                    {
                        raider.ReturningToGate = true;
                    }
                    continue;
                }
            }
            // Round the place the domain nearly finished him last time, when there
            // is a way round: the raider side of memory of place (Issue #358).
            var next = RaiderStep(raider, target);
            if (next is { } step)
            {
                raider.Position = step;
            }
            if (target == PrototypeMap.Gate && raider.Position == PrototypeMap.Gate)
            {
                raider.Mode = RaiderMode.Escaped;
                RecordSurvivor(raider);
            }
        }

        ResolveWave();
    }

    /// <summary>
    /// "End of combat" is now measured against the arrival of the wave in hand:
    /// the first tick at or after it in which none of that wave's raiders is
    /// still on the map. The session fuse is not part of the rule.
    ///
    /// Resolving a wave also ends its consequences: whoever ran is back at work,
    /// because a party of four waves cannot spend a creature on one panic.
    /// Whoever was put down is carried off the floor by
    /// <see cref="RaiseTheDowned"/> a step later, with a heavy wound that keeps
    /// them out of the next fight until it has been mended.
    /// </summary>
    private void ResolveWave()
    {
        if (ActiveWave() is not { } wave ||
            wave.Entered < wave.RaiderCount ||
            WaveRaiders(wave).Any(raider => raider.Mode == RaiderMode.Raiding))
        {
            return;
        }

        var raiders = WaveRaiders(wave).ToArray();
        var downed = raiders.Count(raider => raider.Mode == RaiderMode.Downed);
        var casualties = wave.DefendersDowned + wave.DefendersFled;
        wave.Outcome = downed == wave.RaiderCount
            ? casualties == 0 ? "repelled_clean" : "repelled_costly"
            : downed == 0 ? "overrun" : "larder_raided";
        wave.EndTick = CurrentTick;
        wave.RaidersDowned = downed;
        wave.MealsStolen = raiders
            .Where(raider => raider.Mode == RaiderMode.Escaped)
            .Sum(raider => raider.CarryingMeals);

        // The wave is over, so the domain owes its people an answer. The cards
        // are not built here: the tick is still running, and a card built from
        // half a tick would report a fight that has not finished settling.
        _pendingMomentOfTruth = wave;

        foreach (var creature in _creatures.Where(creature => creature.Mode is CreatureMode.Fled or CreatureMode.Fighting))
        {
            var returning = creature.Mode == CreatureMode.Fled;
            creature.Mode = CreatureMode.Waiting;
            // The fight is over and this creature is standing where it ended.
            // From here it is off duty until something gives it work: that is the
            // whole trigger of Issue #201, and it is deliberately tied to the end
            // of a wave rather than to idleness in general — see ActOffDuty.
            creature.LeftTheFight = true;
            creature.IdleTicks = 0;
            if (returning)
            {
                RecordDecision(
                    creature,
                    "combat_returned",
                    new Dictionary<string, int> { ["wave"] = wave.Number });
            }
        }
    }

    private IEnumerable<RaiderState> WaveRaiders(WaveState wave) =>
        _raiders.Where(raider => raider.Wave == wave.Number);

    /// <summary>
    /// Once no wave is on the map the domain picks its people up off the floor.
    /// They stand with a heavy wound and one point of health: barred from the
    /// next fight, and worth a bunk and a portion for as long as it takes to
    /// mend. That is the price of a lost wave — a domain that meets the next one
    /// short-handed — rather than a counter running backwards.
    /// </summary>
    private void RaiseTheDowned()
    {
        if (_sessionOutcome is not null || ActiveWave() is not null)
        {
            return;
        }

        foreach (var creature in _creatures
                     .Where(creature => creature.Mode == CreatureMode.Downed)
                     .OrderBy(creature => creature.Id))
        {
            creature.Mode = CreatureMode.Waiting;
            creature.Hp = Math.Max(1, creature.Hp);
            creature.RecoveryTicks = 0;
            RecordDecision(
                creature,
                "injury_tended",
                new Dictionary<string, int>
                {
                    ["hp"] = creature.Hp,
                    ["maxHp"] = creature.MaxHp,
                });
        }
    }

    /// <summary>
    /// The end of the party, checked once a tick after everything else has
    /// happened. Three forms, because two were telling a lie: a domain that lost
    /// a wave outright, watched its larder carried off twice and ended with an
    /// empty pantry was still reported as having held, and slice 1 exists to
    /// make that feedback honest.
    ///
    /// - `fallen`  — nobody left who can work and defend;
    /// - `held`    — survived, and every wave was actually repelled;
    /// - `raided`  — survived, but at least one wave got through.
    ///
    /// The line between the last two is drawn on the wave outcomes rather than
    /// on the number of portions carried away, for two reasons. It is what ADR
    /// 0015 says literally — "отражены все волны" — and `repelled_clean` and
    /// `repelled_costly` are precisely the outcomes whose names say the wave was
    /// repelled. And counting portions instead would let a domain whose larder
    /// was already empty be called victorious for losing nothing: the raiders
    /// walked in and out unopposed, which is not holding.
    /// </summary>
    private void ResolveSession()
    {
        if (_sessionOutcome is not null)
        {
            return;
        }

        if (HasFallen())
        {
            _sessionOutcome = "fallen";
            _sessionEndTick = CurrentTick;
            return;
        }

        if (_waves.All(wave => wave.Outcome is not null))
        {
            _sessionOutcome = _waves.All(WasRepelled) ? "held" : "raided";
            _sessionEndTick = CurrentTick;
        }
    }

    private static bool WasRepelled(WaveState wave) =>
        wave.Outcome is "repelled_clean" or "repelled_costly";

    /// <summary>
    /// "Nobody left who can work and defend", stated so that it is a fact about
    /// the world rather than a mood. Every creature is either on the floor or
    /// below the exhaustion threshold, and there is not one portion left in the
    /// larder, on the ground or on anybody's back.
    ///
    /// That state cannot be walked out of: an exhausted creature refuses work,
    /// so nothing will be harvested, nothing will be cooked, and no portion will
    /// ever appear again. Requiring the empty larder is what keeps a domain that
    /// is merely hungry from being declared dead while it still has supper.
    /// </summary>
    private bool HasFallen()
    {
        if (_creatures.All(creature => creature.Mode == CreatureMode.Downed))
        {
            return true;
        }

        if (_creatures.Any(CanWorkAndDefend))
        {
            return false;
        }

        return _stockMeals == 0 &&
            LooseCount(ResourceKind.Meal) == 0 &&
            _creatures.All(creature => creature.Carrying != ResourceKind.Meal);
    }

    /// <summary>
    /// One of the people the domain still has: on their feet and fed enough to
    /// do something about it. The party score counts exactly these as survivors,
    /// which is why a fallen domain scores none of them without anyone writing
    /// a special case — "nobody left who can work and defend" is the same
    /// sentence read over the whole population.
    /// </summary>
    private static bool CanWorkAndDefend(CreatureState creature) =>
        creature.Mode != CreatureMode.Downed &&
        creature.Satiety >= PrototypeTuning.CollapseThreshold;

    /// <summary>
    /// Whether each defender still holds, asked of every one of them separately
    /// once a tick rather than of all of them at once when an ally goes down.
    ///
    /// The old shape was a single domain-wide count of the fallen against a
    /// single threshold, evaluated only at the instant somebody dropped. Two
    /// properties of that shape made panic a herd rather than a decision, and
    /// neither was a mistake in the arithmetic. The pressure term was the same
    /// number for everybody, so one casualty raised the bar for the whole
    /// company at the same moment; and the resisting side — grit plus readiness
    /// — barely moves during a fight, so whoever sat in the band the bar had
    /// just crossed all broke on that one tick. Measured on the seed matrix
    /// before this change: five and six of a nine-strong domain leaving on a
    /// single tick, every wave.
    ///
    /// What replaces it is the same question asked from where the creature is
    /// standing. Dread is what this defender can see: allies down within
    /// <see cref="PrototypeTuning.MoraleWitnessRadius"/> and raiders pressing
    /// within <see cref="PrototypeTuning.MoralePressRadius"/>. Nerve now carries
    /// the defender's own wounds beside its character. Both sides change tick by
    /// tick, and they change differently for each creature, because raiders pick
    /// their target by distance and the wounded are not the same people as the
    /// crowded ones. So the moment of breaking spreads by itself, out of facts
    /// the snapshot already publishes, without a hidden counter and without a
    /// combat trait — the latter is deliberately somebody else's work
    /// (Issue #101 non-goals).
    ///
    /// Asking every tick rather than once per casualty also raises how often the
    /// question can be answered "no", and that is a real cost rather than a
    /// rounding error: at the weights the shape was first written with, the whole
    /// line left every wave and `defendersDowned` fell to 0..1 a party. The
    /// weights in <see cref="PrototypeTuning"/> were re-measured against that,
    /// and what they are worth now is argued there.
    ///
    /// Distance is Manhattan rather than a path: the question is "what can I see
    /// from here", and a breadth-first search per defender per fallen ally per
    /// tick would buy a corner case at a price the whole party pays.
    /// </summary>
    private void ApplyMorale()
    {
        foreach (var creature in _creatures
                     .Where(creature => creature.Mode == CreatureMode.Fighting)
                     .OrderBy(creature => creature.Id))
        {
            var downedNear = _creatures.Count(other =>
                other != creature &&
                other.Mode == CreatureMode.Downed &&
                Manhattan(creature.Position, other.Position) <= PrototypeTuning.MoraleWitnessRadius);
            var raidersNear = _raiders.Count(raider =>
                raider.Mode == RaiderMode.Raiding &&
                Manhattan(creature.Position, raider.Position) <= PrototypeTuning.MoralePressRadius);
            // Loyalty is deliberately absent from this sum, and the omission is
            // load-bearing rather than an oversight (Issue #312).
            //
            // Fear cannot be here: one number carries two different frights —
            // of the domain, which makes a creature obey, and of the fight it is
            // standing in, which makes it run — because Issue #312 names both a
            // punishment and a wound among its sources. Adding it to nerve says
            // that being hit by a raider makes a defender braver, and it was
            // measured to do exactly that: the line stopped breaking, and
            // contract invariant 4 ("подготовка делает набег дешевле") failed on
            // seed 20260728 with prepared paying 421 against baseline's 353.
            //
            // A grudge cannot be here either, and the reason is a distinction
            // rather than a number: **a grudge is not panic**. A creature that
            // walks out because it resents the domain has not broken under
            // dread, and putting resentment into nerve made the simulation call
            // it a flight. That was measured too: on prepared/20260727 only 6 of
            // 13 "broken" defenders left the tile they broke on, against the
            // floor `Most_broken_defenders_actually_leave_the_tile_they_broke_on`
            // holds — because half of them were not fleeing anything. Resentment
            // leaves the line through UpdateCombatParticipation instead, which is
            // its own sentence in the journal and does not pretend to be fear.
            var nerve = creature.Grit * PrototypeTuning.MoraleGritWeight +
                ComputeReadiness(creature) / PrototypeTuning.MoraleReadinessDivisor +
                creature.Hp * PrototypeTuning.MoraleHealthWeight / creature.MaxHp;
            var dread = PrototypeTuning.MoraleBase +
                PrototypeTuning.MoralePerDowned * downedNear +
                PrototypeTuning.MoralePerRaiderNear * raidersNear;
            if (nerve >= dread)
            {
                continue;
            }

            creature.Mode = CreatureMode.Fled;
            CurrentWave()?.CountDefenderFled();
            Remember(creature, "panic");
            RecordDecision(
                creature,
                "combat_fled_morale",
                new Dictionary<string, int>
                {
                    ["downedAlliesNear"] = downedNear,
                    ["raidersNear"] = raidersNear,
                    ["hpPercent"] = creature.Hp * 100 / creature.MaxHp,
                });
        }
    }

    /// <summary>
    /// A defender who broke leaves the fight on foot. The position used to be
    /// assigned outright, which put a creature half a map away inside one tick
    /// and gave the presentation layer a jump to interpolate — the one thing
    /// Presentation pass A promised would never happen, because movement that
    /// does not read as movement cannot be read at all.
    ///
    /// Running is therefore ordinary movement through <see cref="Move"/>: one
    /// tile a tick, no tile shared with anybody, no swapping past a neighbour.
    /// It also means the domain watches somebody run, which is the whole
    /// observable point — a wave usually ends before the runner reaches the far
    /// wall, and that is fine. Whoever is still on the way is put back to work
    /// by <see cref="ResolveWave"/> from wherever the end of the fight found
    /// them.
    /// </summary>
    private void RunFromTheFight(CreatureState creature)
    {
        // The same destination traffic arbitration planned around this tick, read
        // from the same place, so that what was arbitrated and what is walked
        // cannot drift apart.
        if (PrimaryDestination(creature) is not { } refuge ||
            creature.Position == refuge)
        {
            return;
        }

        _ = Move(creature, refuge);
    }

    /// <summary>
    /// Where a broken defender is heading. It used to be one tile per creature id
    /// with nothing checking it was free; a creature that flees and then comes
    /// back to work after the wave makes that shortcut visible, because two
    /// creatures on one tile break movement for both.
    ///
    /// It is recomputed every tick of the flight rather than remembered, and it
    /// is a pure function of the published world, so the run stays deterministic
    /// and needs no field of its own in the canonical snapshot.
    /// </summary>
    private GridPoint FleeTile(CreatureState creature)
    {
        bool Free(GridPoint tile) =>
            _map.IsPassable(tile) &&
            tile != PrototypeMap.Gate &&
            !_creatures.Any(other => other != creature && other.Position == tile);

        var preferred = new GridPoint(1, Math.Min(PrototypeTuning.MapHeight - 2, 1 + creature.Id));
        return Free(preferred)
            ? preferred
            : Enumerable.Range(1, PrototypeTuning.MapHeight - 2)
                .Select(y => new GridPoint(1, y))
                .FirstOrDefault(Free, creature.Position);
    }

    /// <summary>
    /// How visible the domain is from outside. Every term is a counter that only
    /// grows, so renown can never fall: a raided larder, a downed creature or a
    /// razed post cost the domain its answer to the next wave, never its score.
    /// Making impoverishment pay was the whole point — the previous evaluation
    /// marked H2 contradicted precisely because a loss metric made `overrun` the
    /// best result a player could aim for.
    ///
    /// Weights and the shape of the sum are tuning by ADR 0010; that it may not
    /// decrease is the invariant.
    /// </summary>
    private int Renown() =>
        _waves.Count(wave => wave.Arrived) * PrototypeTuning.RenownPerWaveArrived +
        _raidersDownedTotal * PrototypeTuning.RenownPerRaiderDowned +
        _digsCompleted * PrototypeTuning.RenownPerExcavation +
        _buildsCompleted * PrototypeTuning.RenownPerConstruction +
        _peakMeals / PrototypeTuning.RenownMealsPerPoint;

    /// <summary>
    /// How ready the domain is to meet the next wave. It influences nothing — it
    /// is the mirror the player holds next to renown, and the gap between the
    /// two is the answer to "am I doing well?".
    ///
    /// It counts readiness and not potential, which is the difference between a
    /// mirror and a flattering one. A domain starving to death used to show the
    /// best number of its whole party, because inborn might and drilled form
    /// survive hunger on paper; the summary then read "renown 4 against strength
    /// 86" at the exact moment the domain died. Two things stop that:
    ///
    /// - only creatures who could actually answer the call are counted, by the
    ///   same admission rule combat itself uses (10.2) minus the distance test,
    ///   which is about where somebody happens to stand rather than about the
    ///   condition of the domain;
    /// - what each of them brings is scaled by their readiness, so hunger,
    ///   exhaustion and wounds show up in the number rather than beside it.
    /// </summary>
    private int DomainStrength() =>
        _creatures
            .Where(CanAnswerTheCall)
            .Sum(creature =>
                (creature.Might * PrototypeTuning.StrengthPerMight +
                 creature.MartialForm / PrototypeTuning.StrengthMartialDivisor) *
                ComputeReadiness(creature) / PrototypeTuning.StrengthReadinessScale);

    /// <summary>
    /// Could this creature take the field if a wave arrived right now? The rule
    /// is combat's own, so the mirror cannot report strength that the fight
    /// would refuse to use.
    /// </summary>
    private static bool CanAnswerTheCall(CreatureState creature) =>
        creature.Mode != CreatureMode.Downed &&
        creature.Injury != InjuryKind.Heavy &&
        creature.Satiety >= PrototypeTuning.CombatJoinSatiety;

    /// <summary>
    /// One creature writes down where it is standing and what happened to it
    /// there. Called from exactly two places — the tick its nerve failed and the
    /// tick a raider put it down — because those are the two events Issue #117
    /// calls "паника или травма".
    ///
    /// The memory is written at <see cref="CreatureState.Position"/> and nowhere
    /// else, which is the whole of what keeps it from becoming a herd: two
    /// defenders who broke in the same fight broke on different tiles, so they
    /// avoid different places, and a third who held remembers nothing at all.
    /// That is the property #101 bought for panic and this must not spend.
    ///
    /// A place already remembered for a wound stays a wound even if the creature
    /// later panics on it. Being put down is the worse of the two and the one
    /// worth telling the player about; letting the softer cause overwrite it
    /// would make the reason a function of which event happened last rather than
    /// of what happened.
    /// </summary>
    private void Remember(CreatureState creature, string cause)
    {
        var place = creature.Position;
        if (creature.RememberedPlaces.TryGetValue(place, out var known) && known.Cause == "wound")
        {
            cause = "wound";
        }

        creature.RememberedPlaces[place] = new PrototypeRememberedPlace(place, CurrentTick, cause);
        while (creature.RememberedPlaces.Count > PrototypeTuning.MemoryPlacesMax)
        {
            var oldest = creature.RememberedPlaces.Values
                .OrderBy(item => item.Tick)
                .ThenBy(item => item.Place)
                .First();
            creature.RememberedPlaces.Remove(oldest.Place);
        }
    }

    /// <summary>
    /// Whether this creature will refuse to start work on this tile, and which
    /// memory refuses it. The nearest remembered place wins, and ties go to the
    /// newer memory and then to the tile order, so the answer never depends on
    /// the order the dictionary happens to enumerate in.
    ///
    /// <para>
    /// <b>This method is the whole price of memory of place.</b> Nothing else in
    /// the simulation reads a remembered place, so whatever a memory costs the
    /// domain, it costs it here, one (creature, job) pair at a time. Three tuning
    /// values bound that price, and Issue #171 is what happened when only the
    /// first of them existed:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><b>how far</b> — <see cref="PrototypeTuning.MemoryAvoidRadius"/>;</item>
    /// <item><b>how long</b> — <see cref="PrototypeTuning.MemoryAvoidTicks"/>, ticks
    /// since the place was written. Past it the place is still remembered and still
    /// on the panel; what has run out is the avoidance, not the memory;</item>
    /// <item><b>how much</b> — <see cref="PrototypeTuning.MemoryYieldsSatiety"/>. A
    /// creature going hungry stops refusing altogether, so the price memory can
    /// take is bounded by what the domain can survive paying.</item>
    /// </list>
    ///
    /// <para>
    /// None of the three knows what stands on the tile it refuses, and that is
    /// deliberate: a rule that charged less for a larder tile than for a corridor
    /// would be a rule about the map rather than about the creature. A memory may
    /// take away <b>a place, for a while, from a creature that can afford it</b>,
    /// and may not take away a room for a party. The before-and-after of that
    /// sentence, with the commands, is in <c>evidence/171-before.json</c> and
    /// <c>evidence/171-after.json</c>.
    /// </para>
    ///
    /// <para>
    /// A creature that yields to hunger records no refusal, because it did not
    /// refuse: the truthfulness rule of Issue #125 — a refusal names work memory
    /// actually took away — is untouched by both new bounds.
    /// </para>
    /// </summary>
    private PrototypeRememberedPlace? AvoidedPlace(CreatureState creature, GridPoint target)
    {
        return creature.RememberedPlaces.Count == 0 ||
               creature.Satiety < PrototypeTuning.MemoryYieldsSatiety
            ? null
            : creature.RememberedPlaces.Values
                .Where(place =>
                    Manhattan(place.Place, target) <= PrototypeTuning.MemoryAvoidRadius &&
                    CurrentTick - place.Tick <= PrototypeTuning.MemoryAvoidTicks)
                .OrderBy(place => Manhattan(place.Place, target))
                .ThenByDescending(place => place.Tick)
                .ThenBy(place => place.Place)
                .FirstOrDefault();
    }

    private static string AvoidanceReason(PrototypeRememberedPlace place) =>
        place.Cause == "wound" ? "refused_place_of_wound" : "refused_place_of_panic";

    private int CombatJitter(int amplitude) => _combatRandom.NextInt32(amplitude * 2 + 1) - amplitude;

    private void DropRaiderMeals(RaiderState raider)
    {
        if (raider.CarryingMeals <= 0)
        {
            return;
        }

        AddLoose(raider.Position, ResourceKind.Meal, raider.CarryingMeals);
        raider.CarryingMeals = 0;
    }
}
