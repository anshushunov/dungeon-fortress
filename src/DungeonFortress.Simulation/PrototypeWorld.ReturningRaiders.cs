namespace DungeonFortress.Simulation;

// The raider who left alive and comes back: his name, the debt his escape leaves
// the domain, the place in the next-but-one wave he takes rather than adds, and
// the tile he will not walk over again. Slice 5 of the pitch's order of proof
// (section 6.8); design contract docs/design/SLICE_05_RETURNING_HERO.md.
public sealed partial class PrototypeWorld
{
    /// <summary>
    /// One name out of <see cref="PrototypeRaiderNames"/>, drawn from the party's
    /// own stream and never handed out twice.
    ///
    /// <para><b>Uniqueness is guaranteed; termination with a name is not, and the
    /// difference is stated rather than glossed.</b> Never handed out twice is a
    /// property of the <c>HashSet.Add</c> below and holds unconditionally. What
    /// the loops do, however, is <b>sample with replacement</b>: each turn draws a
    /// nickname — possibly one already drawn this call — and then up to
    /// <see cref="PrototypeRaiderNames.Epithets"/><c>.Count</c> epithets for it,
    /// also with replacement. The bounds are attempt counts, not an enumeration of
    /// the pool, so the method can in principle fall through to the throw while
    /// free names remain. It is improbable, not impossible.</para>
    ///
    /// <para>What actually keeps it from happening is the size of the pool against
    /// the size of a party, and the shape of the greedy: bare nicknames are taken
    /// first, so a raider only ever competes for the epithets of a nickname that is
    /// already in use. <see cref="PrototypeRaiderNames.Capacity"/> is 240 against
    /// the 48 raiders the largest possible party can field
    /// (<c>T.wave_max_raiders</c> × <c>T.wave_count</c>), which is the margin
    /// <c>Every_name_a_party_can_need_fits_in_the_pool</c> asserts. That check is
    /// about the margin and not about this loop, and saying so is the point: it
    /// cannot rule the fall-through out.</para>
    ///
    /// <para>Rejection rather than a hash of the id, and the difference is the
    /// point: a hash gives the same name to two raiders as soon as it collides,
    /// and two raiders with one name make "this is the one you met" unprovable —
    /// which is the whole claim of the slice.</para>
    /// </summary>
    private string DrawRaiderName()
    {
        for (var attempt = 0; attempt < PrototypeRaiderNames.Nicknames.Count; attempt++)
        {
            var nickname = PrototypeRaiderNames.Nicknames[
                _raiderNameRandom.NextInt32(PrototypeRaiderNames.Nicknames.Count)];
            if (_raiderNames.Add(nickname))
            {
                return nickname;
            }

            for (var epithetAttempt = 0;
                 epithetAttempt < PrototypeRaiderNames.Epithets.Count;
                 epithetAttempt++)
            {
                var candidate = nickname + " " + PrototypeRaiderNames.Epithets[
                    _raiderNameRandom.NextInt32(PrototypeRaiderNames.Epithets.Count)];
                if (_raiderNames.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        // Reached when the draws above ran out of attempts, which is not the same
        // thing as the pool running out of names: the draw samples with
        // replacement, so this is the unlucky branch and not the impossible one.
        // It throws rather than inventing a name because either reason for being
        // here is a fact worth stopping on — a party grown past the pool, or a
        // draw that lost a bet the margin was supposed to make unlosable.
        throw new InvalidOperationException(
            "The raider name pool ran out of attempts before it found a free name. " +
            $"Capacity is {PrototypeRaiderNames.Capacity} names; if a party can now field " +
            "that many raiders, widen PrototypeRaiderNames.");
    }

    /// <summary>
    /// The raider that walks in next through the gate for this wave: a survivor
    /// who is due back, if there is one, otherwise a stranger.
    ///
    /// <para><b>Taking a place, never adding one.</b> The caller has already
    /// decided how many raiders this wave has — <see cref="AnnounceWaves"/> read
    /// it off renown at the announce tick — and this method only decides who
    /// stands in the next of those places. That is what keeps the invariant slice
    /// 1 proved: the strength of a wave is what the domain's own visibility
    /// bought, and a slice about stories may not quietly add to it.</para>
    /// </summary>
    private RaiderState NextRaiderOf(WaveState wave)
    {
        var jitter = CombatJitter(PrototypeTuning.RaiderMightJitter);
        var returning = _survivors
            .Where(survivor => survivor.Status == "awaiting" && survivor.ReturnWave == wave.Number)
            .OrderBy(survivor => survivor.EscapedWave)
            .ThenBy(survivor => survivor.EscapedTick)
            .ThenBy(survivor => survivor.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (returning is null)
        {
            return new RaiderState(
                _nextRaiderId++,
                wave.Number,
                PrototypeTuning.RaiderHp,
                wave.RaiderMight + jitter,
                PrototypeMap.Gate,
                DrawRaiderName());
        }

        var raider = new RaiderState(
            _nextRaiderId++,
            wave.Number,
            // The same health every raider walks in with. The strengthening is one
            // knob and it is might; see PrototypeTuning for the measurement that
            // retired the second one.
            PrototypeTuning.RaiderHp,
            // His own wave's might plus the same jitter every raider gets, and the
            // bonus on top. Carrying over the might he had last time was the other
            // option and is wrong: renown only grows, so an old might is often
            // *below* what this wave brings, and «возвращается сильнее» would
            // quietly be false for the raider the slice is about.
            wave.RaiderMight + jitter + PrototypeTuning.ReturningRaiderMightBonus,
            PrototypeMap.Gate,
            returning.Name)
        {
            ReturnedFromWave = returning.EscapedWave,
            ScarFromLastTime = returning.Scar,
            RememberedPlace = returning.RememberedPlace,
        };
        returning.Status = "returned";
        returning.ReturnedAsRaiderId = raider.Id;
        return raider;
    }

    /// <summary>
    /// A raider has just walked out of the gate alive. The domain owes him a
    /// return, and this is where the debt is written down.
    ///
    /// <para>The wave he is due back in is <c>escapedWave +
    /// T.returning_raider_wave_gap</c>, decided here and never recomputed, so the
    /// snapshot can be asked the question rather than made to derive it. When that
    /// wave is past the end of the party the record is written anyway, with
    /// <c>no_wave_left</c>: a return the domain never has to answer is a fact
    /// about the party and not an absence.</para>
    /// </summary>
    private void RecordSurvivor(RaiderState raider)
    {
        var scar = raider.Scar;
        var returnWave = raider.Wave + PrototypeTuning.ReturningRaiderWaveGap;
        _survivors.Add(new SurvivorState(
            raider.Name,
            raider.Wave,
            CurrentTick,
            returnWave,
            scar,
            // No scar, no memory, and the two are one decision rather than two:
            // the place a raider remembers is the place he was hit hardest, so a
            // raider nobody reached has nothing to remember. Reading the memory
            // off the scar is what keeps the mechanic from inventing a grievance
            // for somebody who walked through untouched.
            scar == InjuryKind.None || raider.WorstBlow is not { } blow
                ? null
                : new PrototypeRememberedPlace(blow.Place, blow.Tick, "wound"))
        {
            Status = returnWave > PrototypeTuning.WaveCount ? "no_wave_left" : "awaiting",
        });
    }

    /// <summary>
    /// The wave has finished entering. Anybody still due back in it did not get a
    /// place, because the composition rule gives the wave exactly as many bodies
    /// as renown bought and no more.
    /// </summary>
    private void CloseReturnsFor(WaveState wave)
    {
        foreach (var survivor in _survivors.Where(item =>
                     item.Status == "awaiting" && item.ReturnWave == wave.Number))
        {
            survivor.Status = "no_room_in_wave";
        }
    }

    /// <summary>
    /// Where this raider may not step, on top of the tiles nobody may step on.
    ///
    /// <para>This is the whole of «память меняет поведение» for a raider, and it
    /// is deliberately the same mechanism a forbidden zone already uses: the tile
    /// joins the set <see cref="PrototypeMap.NextStep"/> refuses to route through,
    /// so the returning raider walks round the place the domain nearly finished
    /// him instead of over it.</para>
    ///
    /// <para><b>A memory takes away a road and never the objective.</b> If the
    /// remembered tile is the destination itself, there is nothing to walk round —
    /// the larder is where he is going — and the avoidance does not apply. The
    /// same answer covers the case where the tile is the only way through: the
    /// unavoided path is used, and the domain gets to see him walk over it. Both
    /// are the raider side of the bound Issue #171 put on a creature's memory,
    /// which may take away a place and may not take away the party.</para>
    /// </summary>
    /// <para><b>And the bodies of the other raiders, since Issue #76</b>, which is
    /// criterion 1 of it: a raider does not walk onto a tile another raider is
    /// already standing on, so a corridor one tile wide limits how many of them
    /// reach the defenders at once. It is the same mechanism as the memory above
    /// and it carries the same bound — a body takes away a road, never the
    /// objective, so the destination itself is never treated as occupied and a
    /// raider with no way round takes the crowded one.</para>
    private IReadOnlySet<GridPoint> RaiderBlockedTiles(RaiderState raider, GridPoint target)
    {
        var forbidden = _zones[ZoneKind.Forbidden];
        var blocked = new HashSet<GridPoint>(forbidden);

        foreach (var other in _raiders)
        {
            if (other != raider &&
                other.Mode == RaiderMode.Raiding &&
                other.Position != target)
            {
                blocked.Add(other.Position);
            }
        }

        if (raider.RememberedPlace is { } place &&
            place.Place != target &&
            place.Place != raider.Position)
        {
            blocked.Add(place.Place);
        }

        blocked.Remove(raider.Position);
        return blocked;
    }

    /// <summary>
    /// One step of a raider towards <paramref name="target"/>, round its
    /// remembered place and round the other raiders when there is a way round,
    /// and straight through when there is not.
    ///
    /// <para>The step onto an occupied tile is refused outright even when the
    /// fallback path leads to one, because criterion 1 of Issue #76 is about where
    /// a raider may stand and not only about how it prefers to walk. A raider with
    /// nowhere to go waits, which is what makes the corridor a throat.</para>
    ///
    /// <para><b>The destination is not exempt from this last check, and that is
    /// deliberate.</b> Exempting it would have been the safe-looking choice and it
    /// would have defeated the whole rule: every raider of a wave walks to the same
    /// larder tile, so an exemption for the destination is an exemption for exactly
    /// the tile the owner watched them pile onto. It does not deadlock, because an
    /// occupant always leaves on its own — it fills <c>CarryCapacity</c> and turns
    /// for the gate, or the larder runs out, or it is felled — so the queue behind
    /// it drains. The occupied destination is the bottleneck the rule exists to
    /// create.</para>
    /// </summary>
    private GridPoint? RaiderStep(RaiderState raider, GridPoint target)
    {
        var blocked = RaiderBlockedTiles(raider, target);
        var step = _map.NextStep(raider.Position, target, blocked)
            ?? _map.NextStep(raider.Position, target, _zones[ZoneKind.Forbidden]);
        if (step is not { } next || next == raider.Position)
        {
            return step;
        }

        var occupied = _raiders.Any(other =>
            other != raider && other.Mode == RaiderMode.Raiding && other.Position == next);
        if (!occupied)
        {
            raider.BlockedTicks = 0;
            return step;
        }

        // Waited long enough: take the crowded tile rather than stand for ever.
        // Without this the queue behind an occupied larder never drains, no wave
        // resolves and the party never ends - measured, see RaiderBlockedPatience.
        raider.BlockedTicks++;
        if (raider.BlockedTicks > PrototypeTuning.RaiderBlockedPatience)
        {
            raider.BlockedTicks = 0;
            return step;
        }

        return null;
    }

    private IEnumerable<PrototypeSurvivorSnapshot> SurvivorSnapshots() =>
        _survivors
            .OrderBy(survivor => survivor.EscapedWave)
            .ThenBy(survivor => survivor.EscapedTick)
            .ThenBy(survivor => survivor.Name, StringComparer.Ordinal)
            .Select(survivor => new PrototypeSurvivorSnapshot(
                survivor.Name,
                survivor.EscapedWave,
                survivor.EscapedTick,
                survivor.ReturnWave,
                survivor.Status,
                survivor.Scar,
                survivor.RememberedPlace,
                survivor.ReturnedAsRaiderId));
}
