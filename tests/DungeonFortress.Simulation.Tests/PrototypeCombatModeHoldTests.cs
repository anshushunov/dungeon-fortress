using System.Globalization;
using System.Text;

using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Simulation.Tests;

/// <summary>
/// Whether the fact "this creature is in the line" survives the tick it was
/// decided in — Issue #333, measured over the shipped journals rather than
/// argued about.
///
/// <para>The issue carries two sentences and they turned out to belong to
/// different layers. «После боя остаются добивать труп», said by the owner on
/// the playtest of 2026-08-08, <b>is not in the simulation at all</b>: nobody
/// strikes a raider that is already out on any of the nine runs, and
/// <see cref="Measurement.StrikesOnARaiderAlreadyOut"/> is asserted here so it
/// stays that way. Where the owner's picture comes from is the view, and the
/// numbers that locate it are in <c>evidence/333-before.json</c>; the fix
/// belongs to <see href="https://github.com/anshushunov/dungeon-fortress/issues/334">Issue
/// #334</see>.</para>
///
/// <para>The second sentence — a creature that left the line can be back in it
/// inside one tick — reproduced, and its cause is one: participation is decided
/// by <c>UpdateCombatParticipation</c> at phase 4 and carried by
/// <see cref="CreatureMode"/>, which <c>DecideNeedsAndMuster</c> at phase 6 used
/// to overwrite for its own reasons. Three assertions below hold the three
/// different things that cost, and they are separate on purpose: a creature
/// taken out of the line for a tick, a runner that stopped running, and a body
/// that got up — the last of which is not lost for a tick but for the rest of
/// the party, because <c>RaiseTheDowned</c> only ever raises a creature whose
/// mode is still <see cref="CreatureMode.Downed"/>.</para>
///
/// <para>Everything is read from <c>GetSnapshot()</c> after every step and from
/// the canonical event log, never from the internals: what is asserted is what
/// the world publishes.</para>
/// </summary>
public sealed class PrototypeCombatModeHoldTests(ITestOutputHelper output)
{
    private static readonly ulong[] MatrixSeeds = [20_260_726UL, 20_260_727UL, 20_260_728UL];

    private static readonly string[] Fixtures = ["baseline", "prepared", "neglected"];

    /// <summary>
    /// The nine parties, walked once and shared. A party takes about a second,
    /// and four checks over the same nine would otherwise pay for it four times.
    /// </summary>
    private static IReadOnlyList<Measurement> Matrix => MatrixMeasurements.Value;

    private static readonly Lazy<IReadOnlyList<Measurement>> MatrixMeasurements =
        new(() =>
        [
            .. Fixtures.SelectMany(_ => MatrixSeeds, (fixtureName, seed) => Measure(fixtureName, seed)),
        ]);

    [Fact]
    public void Report_combat_mode_hold_over_the_seed_matrix()
    {
        var report = new StringBuilder();
        foreach (var measurement in Matrix)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{measurement}");
        }

        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// The owner's first sentence, held as a floor rather than as a report.
    ///
    /// <para>A blow is a blow on somebody who is still standing. Measured over
    /// the nine shipped runs both before and after the edit of this branch, no
    /// creature ever struck a raider that had already left the fight — the target
    /// of <c>ActCombatant</c> is re-read from the world on every call, so a
    /// raider that went down stops being a candidate the instant it does. The
    /// check exists because the sentence was reported from a playtest and the
    /// answer «not in the simulation» is only worth anything if something keeps
    /// it true.</para>
    /// </summary>
    [Fact]
    public void No_blow_lands_on_a_raider_that_was_already_out()
    {
        var offenders = Matrix.Where(run => run.StrikesOnARaiderAlreadyOut > 0).ToArray();

        Assert.True(
            offenders.Length == 0,
            $"A blow landed on a raider that had already left the fight.{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// A creature is taken out of the line by the fight and by nothing else.
    ///
    /// <para>Three counts, because the same loss shows up in three places and a
    /// check that saw only one of them would pass on half a defect. Across a tick
    /// boundary: <see cref="Measurement.LeftTheLineWithoutLeavingTheFight"/> —
    /// <see cref="CreatureMode.Fighting"/> became something that is neither
    /// <see cref="CreatureMode.Fled"/> nor <see cref="CreatureMode.Downed"/>
    /// while the wave was still running. Inside one tick:
    /// <see cref="Measurement.JoinedAndLeftTheLineInTheSameTick"/> — a
    /// <c>combat_joined</c> that stands alone on its tick, whose creature is out
    /// of the line by the end of that very tick. And the consequence:
    /// <see cref="Measurement.BackInTheLineAfterASilentLeave"/>, the creature
    /// walking back in later, which is the sentence the issue is titled
    /// after.</para>
    ///
    /// <para>Leaving into <see cref="CreatureMode.Fled"/> or
    /// <see cref="CreatureMode.Downed"/> is not counted by any of the three, and
    /// neither is the whole line standing down on the tick <c>ResolveWave</c>
    /// gives the wave its outcome. Those are the fight's own decisions and they
    /// are written into the journal as such; what this check forbids is the line
    /// being emptied by something that is not about the fight and says nothing in
    /// the journal about the line at all.</para>
    /// </summary>
    [Fact]
    public void A_creature_leaves_the_line_only_by_a_decision_of_the_fight()
    {
        var acrossTicks = Matrix.Sum(run => run.LeftTheLineWithoutLeavingTheFight);
        var insideOneTick = Matrix.Sum(run => run.JoinedAndLeftTheLineInTheSameTick);
        var cameBack = Matrix.Sum(run => run.BackInTheLineAfterASilentLeave);

        Assert.True(
            acrossTicks == 0 && insideOneTick == 0 && cameBack == 0,
            $"Somebody was taken out of the line by something other than the fight: " +
            $"{acrossTicks} across a tick, {insideOneTick} inside the tick they joined on, " +
            $"{cameBack} of them back in the line later.{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// A creature that was put down stays down until the domain picks it up.
    ///
    /// <para>Its own check and not a fourth count in the one above, because the
    /// loss is of a different kind. A fighter pulled out of the line is missing
    /// for a few ticks; a body that gets up is missing for the rest of the party.
    /// <c>RaiseTheDowned</c> is the only thing allowed to end
    /// <see cref="CreatureMode.Downed"/>, and it looks for exactly that mode — so
    /// a creature whose mode was overwritten while it lay there is never carried
    /// off the floor, keeps its heavy wound and its single hit point, and walks
    /// around the domain as somebody the fight already spent. Measured once
    /// before the edit, on <c>prepared/20260726</c> at t1720: creature #8 went
    /// from <see cref="CreatureMode.Downed"/> to
    /// <see cref="CreatureMode.Eating"/> with no health at all
    /// (<c>evidence/333-before.json</c>).</para>
    /// </summary>
    [Fact]
    public void A_creature_that_was_put_down_stays_down_until_the_domain_picks_it_up()
    {
        var gotUp = Matrix.Sum(run => run.DownedGotUpWithoutBeingRaised);

        Assert.True(
            gotUp == 0,
            $"{gotUp} creature-ticks where somebody left CreatureMode.Downed without RaiseTheDowned " +
            $"raising them, which also means they will never be raised at all." +
            $"{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// A defender who broke keeps running until the wave is over.
    ///
    /// <para><see cref="CreatureMode.Fled"/> ends in exactly one place —
    /// <c>ResolveWave</c>, which puts the runner back to work from wherever the
    /// end of the fight found it, and writes <c>combat_returned</c> while doing
    /// so. A run that ends anywhere else is a panic that quietly turned into an
    /// errand: the domain watched somebody break and then watched them walk to
    /// the larder, which is neither the flight the journal recorded nor the
    /// return it did not.</para>
    /// </summary>
    [Fact]
    public void A_defender_that_broke_keeps_running_until_the_wave_is_over()
    {
        var stopped = Matrix.Sum(run => run.FledStoppedFleeing);

        Assert.True(
            stopped == 0,
            $"{stopped} creature-ticks where a broken defender left CreatureMode.Fled without the " +
            $"wave being resolved.{Environment.NewLine}{Detail()}");
    }

    /// <summary>
    /// The same exchange gives a different result — criterion 3 of Issue #336,
    /// held as a rule instead of as a column of the report above.
    ///
    /// <para>A defender's blow is <c>might·W + readiness/D + jitter</c>, so two
    /// blows that share a profile <c>m{Might}r{Readiness}</c> share every term of
    /// that sum except the draw. A profile carrying more than one damage value is
    /// therefore the whole of «одинаковые размены дают разный урон», and it is the
    /// only part of the sum <see cref="PrototypeTuning.DamageJitter"/> can
    /// reach — which is why zeroing that amplitude is the mutant this check owes
    /// (<c>evidence/333-mutants.json</c>, M2).</para>
    ///
    /// <para><b>The floor is one and the unit is the party</b>, for the same
    /// reason the memory floor of <c>PrototypeMemoryTests</c> is: «observable» has
    /// exactly one number in it, the boundary between observed and not observed,
    /// and the matrix is how this suite tells a property of the world from a
    /// coincidence of one party (13.4). A party in which nobody ever struck a
    /// raider is excluded rather than failed — <c>neglected</c> falls before the
    /// first wave arrives and carries no exchange at all — and the guard against
    /// excluding everything is asserted separately. Measured on the shipped
    /// matrix: 25..35 scattered profiles in each of the six parties that fight.
    /// </para>
    /// </summary>
    [Fact]
    public void The_same_exchange_gives_a_different_result()
    {
        var fought = Matrix.Where(run => run.DefenderBlows > 0).ToArray();

        Assert.True(
            fought.Length > 0,
            $"Not one party of the matrix landed a single blow, so nothing below was asked at " +
            $"all.{Environment.NewLine}{Detail()}");

        var flat = fought
            .Where(run => !run.DamageByProfile.Any(profile => profile.Value.Count > 1))
            .ToArray();

        Assert.True(
            flat.Length == 0,
            $"In {flat.Length} of {fought.Length} parties that fought, every profile " +
            $"m{{might}}r{{readiness}} carried exactly one damage value: the same fighter striking " +
            $"in the same condition always did the same damage. Damage has no spread in those " +
            $"parties, which is the state Issue #336 was raised out of.{Environment.NewLine}{Detail()}");
    }

    private static string Detail() =>
        string.Join(Environment.NewLine, Matrix.Select(measurement => measurement.ToString()));

    private static Measurement Measure(string fixtureName, ulong seed)
    {
        var world = new PrototypeWorld(LoadFixture(fixtureName) with { Seed = seed });
        var tally = new Measurement(fixtureName, seed);
        var previous = world.GetSnapshot();
        var modeByTick = new Dictionary<int, Dictionary<int, CreatureMode>>();

        while (!world.IsComplete)
        {
            world.Step();
            var current = world.GetSnapshot();
            if (current.Tick == previous.Tick)
            {
                tally.StepsThatMovedNoTick++;

                // What the view will draw on this frame, by BlowReadout's own
                // rule — the fresh entries of the journal are the ones stamped
                // `Tick - 1`. The rule is restated rather than called, the same
                // way PrototypeCombatApproachTests restates ActCombatant's target
                // rule: a measurement that asks the code what it did cannot
                // disagree with it. The tick does not move while the window is
                // open, so `Tick - 1` keeps naming the tick the wave ended on and
                // the same blows are drawn for the whole window.
                var bodies = current.Raiders
                    .Where(raider => raider.Mode == RaiderMode.Downed)
                    .Select(raider => raider.Id)
                    .ToHashSet();
                var drawn = current.Events
                    .Where(entry =>
                        entry.LastTick == current.Tick - 1 &&
                        entry.ReasonCode is "combat_attack" or "combat_raider_downed")
                    .ToArray();
                var onBodies = drawn.Count(entry =>
                    entry.Details.TryGetValue("raiderId", out var id) && bodies.Contains(id));
                tally.BlowsDrawnWhilePaused += drawn.Length;
                tally.BlowsDrawnOnABodyWhilePaused += onBodies;
                if (!tally.WindowOpen)
                {
                    tally.WindowOpen = true;
                    tally.Windows.Add($"t{current.Tick - 1}:{onBodies}/{drawn.Length}");
                }

                previous = current;
                // A step spent waiting for a verdict runs no phase and moves no
                // tick (Issue #312). Counting it would read the same decision of
                // the same tick again against a world that has already moved on
                // — the whole of the 39 phantom "strikes on a downed raider"
                // this probe reported before the guard existed.
                continue;
            }

            tally.WindowOpen = false;
            var acted = current.Tick - 1;
            modeByTick[acted] = current.Creatures.ToDictionary(c => c.Id, c => c.Mode);
            Observe(previous, current, acted, tally);
            previous = current;
        }

        tally.Ticks = previous.Tick;
        tally.Score = previous.SessionResult.Score;
        tally.Outcome = previous.SessionResult.Outcome ?? "unfinished";
        tally.WavesResolved = previous.Domain.WavesResolved;
        tally.MealsStolen = previous.SessionResult.MealsStolen;
        tally.DefendersDowned = previous.SessionResult.DefendersDowned;
        tally.DefendersFled = previous.SessionResult.DefendersFled;
        tally.MealsProduced = previous.Stocks.MealsProduced;
        tally.RaidCost =
            previous.SessionResult.MealsStolen * PrototypeTuning.ScorePerMealStolen +
            (previous.SessionResult.DefendersDowned + previous.SessionResult.DefendersFled) *
                PrototypeTuning.ScorePerDefenderLost;
        ScanJoins(previous, modeByTick, tally);
        foreach (var creature in previous.Creatures.OrderBy(creature => creature.Id))
        {
            foreach (var place in creature.RememberedPlaces.OrderBy(place => place.Tick))
            {
                tally.MemoriesHeldAtTheEnd.Add($"#{creature.Id}@t{place.Tick}({place.Place.X},{place.Place.Y}){place.Cause[0]}");
            }
        }

        return tally;
    }

    /// <summary>
    /// How long an exchange lasts and how far a blow scatters, read off the same
    /// nine parties — the measuring column Issue #336 asks for.
    ///
    /// <para><b>A blow on a raider is counted from the journal and a blow on a
    /// defender from the health it lost</b>, and the asymmetry is the journal's
    /// rather than this probe's: <c>ActCombatant</c> writes <c>combat_attack</c>
    /// with the damage in it, while <c>ActRaiders</c> writes nothing at all until
    /// the defender goes down. So the raider side is an exact count of blows and
    /// the defender side is a count of <b>ticks in which health fell</b>, which
    /// undercounts whenever two raiders reach the same defender on one tick. The
    /// two are therefore reported under different names and never added
    /// together.</para>
    ///
    /// <para><b>The spread is shown by profile.</b> A defender's blow is
    /// <c>might·W + readiness/D + jitter</c>, and both <c>Might</c> and
    /// <c>Readiness</c> are published in the snapshot — so two blows that share a
    /// profile share every term of the sum except the jitter. A profile that
    /// carries more than one damage value is the whole of criterion 3: the same
    /// exchange, a different result.</para>
    /// </summary>
    private static void ObserveTheExchange(
        PrototypeSnapshot previous,
        PrototypeSnapshot current,
        int acted,
        Measurement tally)
    {
        var raiderHpBefore = previous.Raiders.ToDictionary(raider => raider.Id, raider => raider.Hp);
        var creatureBefore = previous.Creatures.ToDictionary(creature => creature.Id);

        foreach (var creature in current.Creatures)
        {
            var decision = creature.LastDecision;
            if (decision.Tick != acted)
            {
                continue;
            }

            if (decision.ReasonCode == "combat_attack" &&
                decision.Details.TryGetValue("raiderId", out var struck) &&
                decision.Details.TryGetValue("damage", out var damage))
            {
                tally.BlowsOnARaider[struck] = tally.BlowsOnARaider.GetValueOrDefault(struck) + 1;
                tally.RaiderUnderFireSince.TryAdd(struck, acted);
                var profile = $"m{creature.Might}r{creature.Readiness}";
                if (!tally.DamageByProfile.TryGetValue(profile, out var seen))
                {
                    seen = [];
                    tally.DamageByProfile[profile] = seen;
                }

                seen.Add(damage);
                tally.DefenderBlows++;
                tally.DefenderDamageTotal += damage;
                tally.DefenderDamageLowest = Math.Min(tally.DefenderDamageLowest, damage);
                tally.DefenderDamageHighest = Math.Max(tally.DefenderDamageHighest, damage);
            }

            if (decision.ReasonCode == "combat_raider_downed" &&
                decision.Details.TryGetValue("raiderId", out var felled))
            {
                // The killing blow is written as `combat_raider_downed` and
                // overwrites the `combat_attack` of the same tick, so it is added
                // back by hand rather than lost.
                var blows = tally.BlowsOnARaider.GetValueOrDefault(felled) + 1;
                tally.BlowsToFellARaider.Add(blows);
                if (tally.RaiderUnderFireSince.TryGetValue(felled, out var since))
                {
                    tally.TicksARaiderStoodUnderFire.Add(acted - since + 1);
                    tally.RaiderUnderFireSince.Remove(felled);
                }

                tally.BlowsOnARaider.Remove(felled);
            }
        }

        foreach (var raider in current.Raiders)
        {
            if (!raiderHpBefore.TryGetValue(raider.Id, out var was) || raider.Hp >= was)
            {
                continue;
            }

            tally.HealthTakenOffRaiders += was - raider.Hp;
        }

        foreach (var creature in current.Creatures)
        {
            if (!creatureBefore.TryGetValue(creature.Id, out var was) || creature.Hp >= was.Hp)
            {
                continue;
            }

            tally.HitTicksOnADefender[creature.Id] = tally.HitTicksOnADefender.GetValueOrDefault(creature.Id) + 1;
            tally.DefenderUnderFireSince.TryAdd(creature.Id, acted);
            tally.HealthTakenOffDefenders += was.Hp - creature.Hp;
            if (creature.Mode != CreatureMode.Downed || was.Mode == CreatureMode.Downed)
            {
                continue;
            }

            tally.HitTicksToFellADefender.Add(tally.HitTicksOnADefender[creature.Id]);
            if (tally.DefenderUnderFireSince.TryGetValue(creature.Id, out var since))
            {
                tally.TicksADefenderStoodUnderFire.Add(acted - since + 1);
            }

            tally.HitTicksOnADefender.Remove(creature.Id);
            tally.DefenderUnderFireSince.Remove(creature.Id);
        }

        foreach (var wave in current.Waves.Where(wave => wave.EndTick == acted && wave.Outcome is not null))
        {
            tally.WaveSpans.Add(acted - wave.ArriveTick);
        }

        // The price of memory of place, watched from the side hunger switches it
        // off from (Issue #171, PrototypeTuning.MemoryYieldsSatiety). A creature
        // that holds a memory the avoidance still reaches is only able to refuse
        // work while it is fed enough to be choosy, so these two counts say
        // whether a fixture that stops refusing has stopped remembering or has
        // simply gone hungry.
        foreach (var creature in current.Creatures)
        {
            var live = creature.RememberedPlaces.Count(place =>
                acted - place.Tick <= PrototypeTuning.MemoryAvoidTicks);
            if (live == 0)
            {
                continue;
            }

            tally.CreatureTicksHoldingALiveMemory++;
            if (creature.Satiety < PrototypeTuning.MemoryYieldsSatiety)
            {
                tally.CreatureTicksAMemoryWasSwitchedOffByHunger++;
            }
        }

        foreach (var entry in current.Events)
        {
            if (entry.LastTick == acted &&
                entry.ReasonCode is "refused_place_of_panic" or "refused_place_of_wound")
            {
                tally.RefusalsByMemory++;
            }
        }
    }

    /// <summary>
    /// Everything that can be read by comparing the snapshot after tick T-1 with
    /// the snapshot after tick T.
    /// </summary>
    private static void Observe(
        PrototypeSnapshot previous,
        PrototypeSnapshot current,
        int acted,
        Measurement tally)
    {
        ObserveTheExchange(previous, current, acted, tally);
        var raidersBefore = previous.Raiders.ToDictionary(raider => raider.Id, raider => raider.Mode);
        var standing = current.Raiders.Count(raider => raider.Mode == RaiderMode.Raiding);
        var bodies = current.Raiders.Count(raider => raider.Mode == RaiderMode.Downed);
        tally.MaxRaiderBodiesOnTheMap = Math.Max(tally.MaxRaiderBodiesOnTheMap, bodies);
        tally.RaiderBodyTicks += bodies;
        if (standing == 0)
        {
            tally.FightingTicksWithNothingToFight +=
                current.Creatures.Count(creature => creature.Mode == CreatureMode.Fighting);
        }

        foreach (var creature in current.Creatures)
        {
            var decision = creature.LastDecision;
            if (decision.Tick == acted &&
                decision.ReasonCode is "combat_attack" or "combat_raider_downed" &&
                decision.Details.TryGetValue("raiderId", out var raiderId) &&
                raidersBefore.TryGetValue(raiderId, out var modeBefore) &&
                modeBefore != RaiderMode.Raiding)
            {
                tally.StrikesOnARaiderAlreadyOut++;
                tally.FirstStrikeOnARaiderAlreadyOut ??= $"t{acted} creature#{creature.Id} raider#{raiderId} was {modeBefore}";
            }
        }

        // The ration hypothesis, measured and not fixed (Issue #333): what the
        // muster's ration actually leaves a defender at, read on the tick its
        // wave lands.
        foreach (var wave in current.Waves.Where(wave => wave.ArriveTick == acted && wave.Arrived))
        {
            tally.SatietyAtArrival.Add(
                $"w{wave.Number}:" + string.Join(',', current.Creatures
                    .OrderBy(creature => creature.Id)
                    .Select(creature => creature.Satiety)));
        }

        tally.YieldsBookedByAFighter += current.Creatures.Count(creature =>
            creature.Mode == CreatureMode.Fighting &&
            creature.LastDecision.Tick == acted &&
            creature.LastDecision.ReasonCode == "chosen_traffic_yield");

        foreach (var fighter in current.Creatures.Where(creature => creature.Mode == CreatureMode.Fighting))
        {
            tally.SpellInTheLine[fighter.Id] = tally.SpellInTheLine.GetValueOrDefault(fighter.Id) + 1;
            tally.LongestSpellInTheLine = Math.Max(
                tally.LongestSpellInTheLine, tally.SpellInTheLine[fighter.Id]);
            tally.LowestSatietyInTheLine = Math.Min(tally.LowestSatietyInTheLine, fighter.Satiety);
        }

        foreach (var resting in current.Creatures.Where(creature => creature.Mode != CreatureMode.Fighting))
        {
            tally.SpellInTheLine.Remove(resting.Id);
        }

        var before = previous.Creatures.ToDictionary(creature => creature.Id);
        var waveResolvedThisTick = current.Waves.Any(wave =>
            wave.Outcome is not null && wave.EndTick == acted);

        foreach (var creature in current.Creatures)
        {
            var was = before[creature.Id].Mode;
            var now = creature.Mode;
            if (was == now)
            {
                continue;
            }

            if (was == CreatureMode.Fighting)
            {
                if (now is CreatureMode.Fled or CreatureMode.Downed)
                {
                    continue;
                }

                if (waveResolvedThisTick && now == CreatureMode.Waiting)
                {
                    continue;
                }

                tally.LeftTheLineWithoutLeavingTheFight++;
                tally.LeftInto[now] = tally.LeftInto.GetValueOrDefault(now) + 1;
                tally.LeftBecause[creature.LastDecision.ReasonCode] =
                    tally.LeftBecause.GetValueOrDefault(creature.LastDecision.ReasonCode) + 1;
                tally.FirstSilentLeave ??=
                    $"t{acted} creature#{creature.Id} Fighting->{now} ({creature.LastDecision.ReasonCode}@t{creature.LastDecision.Tick}, satiety={creature.Satiety})";
                tally.OutOfTheLineSince[creature.Id] = acted;
                continue;
            }

            if (was == CreatureMode.Downed && now != CreatureMode.Waiting)
            {
                tally.DownedGotUpWithoutBeingRaised++;
                tally.DownedGotUpInto[now] = tally.DownedGotUpInto.GetValueOrDefault(now) + 1;
                tally.FirstDownedGotUp ??=
                    $"t{acted} creature#{creature.Id} Downed->{now} ({creature.LastDecision.ReasonCode}@t{creature.LastDecision.Tick}, hp={creature.Hp}, injury={creature.Injury})";
            }

            if (was == CreatureMode.Fled && now is not (CreatureMode.Waiting or CreatureMode.Downed))
            {
                tally.FledStoppedFleeing++;
                tally.FledStoppedInto[now] = tally.FledStoppedInto.GetValueOrDefault(now) + 1;
                tally.FirstFledStopped ??=
                    $"t{acted} creature#{creature.Id} Fled->{now} ({creature.LastDecision.ReasonCode}@t{creature.LastDecision.Tick})";
            }

            if (now == CreatureMode.Fighting &&
                tally.OutOfTheLineSince.TryGetValue(creature.Id, out var leftAt))
            {
                tally.BackInTheLineAfterASilentLeave++;
                tally.FirstBackInTheLine ??=
                    $"t{acted} creature#{creature.Id} back in the line, gap={acted - leftAt} ticks";
                tally.OutOfTheLineSince.Remove(creature.Id);
            }
        }
    }

    /// <summary>
    /// The same-tick half of the claim, read off the canonical event log: a
    /// `combat_joined` that stands alone on one tick, and the creature is not in
    /// the line at the end of that very tick.
    /// </summary>
    private static void ScanJoins(
        PrototypeSnapshot final,
        Dictionary<int, Dictionary<int, CreatureMode>> modeByTick,
        Measurement tally)
    {
        // The tick a wave is given its outcome stands the whole line down, and
        // that is the fight's own decision — the same one `Observe` already steps
        // over on the across-a-tick side. Somebody who joined on that very tick
        // is counted here as a join and never as a silent departure: the line was
        // dissolved under them by ResolveWave, which writes the wave's outcome
        // while doing it.
        var standDownTicks = final.Waves
            .Where(wave => wave.Outcome is not null && wave.EndTick is not null)
            .Select(wave => wave.EndTick!.Value)
            .ToHashSet();

        foreach (var entry in final.Events.Where(e => e.ReasonCode == "combat_joined" && e.Repeats == 1))
        {
            if (!modeByTick.TryGetValue(entry.FirstTick, out var modes) ||
                !modes.TryGetValue(entry.CreatureId, out var mode))
            {
                continue;
            }

            tally.JoinsSeen++;
            if (mode == CreatureMode.Fighting)
            {
                continue;
            }

            if (mode == CreatureMode.Waiting && standDownTicks.Contains(entry.FirstTick))
            {
                tally.JoinedAndTheWaveEndedUnderThem++;
                continue;
            }

            if (mode is CreatureMode.Fled or CreatureMode.Downed)
            {
                // The fight's own answer, given after the join by design:
                // ApplyMorale is asked at the top of ActCreatures so that a
                // defender whose nerve failed leaves instead of striking. Counted
                // apart and deliberately not asserted on — it is a decision of the
                // fight, recorded as one, and this issue is not allowed to change
                // the rules of combat.
                tally.JoinedAndTheFightTookThemBackInTheSameTick++;
                tally.JoinedIntoTheFight[mode] = tally.JoinedIntoTheFight.GetValueOrDefault(mode) + 1;
                continue;
            }

            tally.JoinedAndLeftTheLineInTheSameTick++;
            tally.JoinedIntoNothing[mode] = tally.JoinedIntoNothing.GetValueOrDefault(mode) + 1;
            tally.FirstJoinedAndOut ??=
                $"t{entry.FirstTick} creature#{entry.CreatureId} combat_joined, and ended the same tick as {mode}";
        }
    }

    private sealed class Measurement(string fixtureName, ulong seed)
    {
        public int Ticks { get; set; }

        public int StrikesOnARaiderAlreadyOut { get; set; }

        public string? FirstStrikeOnARaiderAlreadyOut { get; set; }

        public int LeftTheLineWithoutLeavingTheFight { get; set; }

        public Dictionary<CreatureMode, int> LeftInto { get; } = [];

        public Dictionary<string, int> LeftBecause { get; } = [];

        public string? FirstSilentLeave { get; set; }

        public int BackInTheLineAfterASilentLeave { get; set; }

        public string? FirstBackInTheLine { get; set; }

        public Dictionary<int, int> OutOfTheLineSince { get; } = [];

        public int DownedGotUpWithoutBeingRaised { get; set; }

        public Dictionary<CreatureMode, int> DownedGotUpInto { get; } = [];

        public string? FirstDownedGotUp { get; set; }

        public int FledStoppedFleeing { get; set; }

        public Dictionary<CreatureMode, int> FledStoppedInto { get; } = [];

        public string? FirstFledStopped { get; set; }

        public int JoinsSeen { get; set; }

        public int JoinedAndLeftTheLineInTheSameTick { get; set; }

        public int JoinedAndTheFightTookThemBackInTheSameTick { get; set; }

        public int JoinedAndTheWaveEndedUnderThem { get; set; }

        public Dictionary<CreatureMode, int> JoinedIntoTheFight { get; } = [];

        public Dictionary<CreatureMode, int> JoinedIntoNothing { get; } = [];

        public string? FirstJoinedAndOut { get; set; }

        public int StepsThatMovedNoTick { get; set; }

        public int BlowsDrawnWhilePaused { get; set; }

        public int BlowsDrawnOnABodyWhilePaused { get; set; }

        public bool WindowOpen { get; set; }

        public List<string> Windows { get; } = [];

        public int FightingTicksWithNothingToFight { get; set; }

        public int RaiderBodyTicks { get; set; }

        public int MaxRaiderBodiesOnTheMap { get; set; }

        public int YieldsBookedByAFighter { get; set; }

        public Dictionary<int, int> SpellInTheLine { get; } = [];

        public int LongestSpellInTheLine { get; set; }

        public int LowestSatietyInTheLine { get; set; } = 100;

        public int? Score { get; set; }

        public string Outcome { get; set; } = "unfinished";

        public int WavesResolved { get; set; }

        public int RaidCost { get; set; }

        public int MealsStolen { get; set; }

        public int DefendersDowned { get; set; }

        public int DefendersFled { get; set; }

        public int MealsProduced { get; set; }

        public List<string> SatietyAtArrival { get; } = [];

        public Dictionary<int, int> BlowsOnARaider { get; } = [];

        public Dictionary<int, int> RaiderUnderFireSince { get; } = [];

        public List<int> BlowsToFellARaider { get; } = [];

        public List<int> TicksARaiderStoodUnderFire { get; } = [];

        public Dictionary<int, int> HitTicksOnADefender { get; } = [];

        public Dictionary<int, int> DefenderUnderFireSince { get; } = [];

        public List<int> HitTicksToFellADefender { get; } = [];

        public List<int> TicksADefenderStoodUnderFire { get; } = [];

        public List<int> WaveSpans { get; } = [];

        public Dictionary<string, SortedSet<int>> DamageByProfile { get; } = [];

        public int DefenderBlows { get; set; }

        public int DefenderDamageTotal { get; set; }

        public int DefenderDamageLowest { get; set; } = int.MaxValue;

        public int DefenderDamageHighest { get; set; }

        public int HealthTakenOffRaiders { get; set; }

        public int HealthTakenOffDefenders { get; set; }

        public int CreatureTicksHoldingALiveMemory { get; set; }

        public int CreatureTicksAMemoryWasSwitchedOffByHunger { get; set; }

        public int RefusalsByMemory { get; set; }

        public List<string> MemoriesHeldAtTheEnd { get; } = [];

        /// <summary>
        /// The profiles that saw more than one damage value, most-scattered
        /// first. Empty means the spread is not observable on this run.
        /// </summary>
        public IEnumerable<string> ScatteredProfiles =>
            DamageByProfile
                .Where(pair => pair.Value.Count > 1)
                .OrderByDescending(pair => pair.Value.Max - pair.Value.Min)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}=>{string.Join('/', pair.Value)}");

        public override string ToString()
        {
            // Hundredths, kept as integers so the report cannot pick up a culture.
            static string Mean(List<int> values) =>
                values.Count == 0 ? "-" : $"{values.Sum() * 100 / values.Count}/100";

            static string Map<T>(Dictionary<T, int> value)
                where T : notnull =>
                value.Count == 0
                    ? "-"
                    : string.Join(',', value.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}={pair.Value}"));

            return string.Join(Environment.NewLine,
                $"{fixtureName}/{seed} ticks={Ticks}",
                $"  strikesOnARaiderAlreadyOut={StrikesOnARaiderAlreadyOut} first={FirstStrikeOnARaiderAlreadyOut ?? "-"}",
                $"  leftTheLineSilently={LeftTheLineWithoutLeavingTheFight} into=[{Map(LeftInto)}] because=[{Map(LeftBecause)}]",
                $"    first={FirstSilentLeave ?? "-"}",
                $"  backInTheLineAfterASilentLeave={BackInTheLineAfterASilentLeave} first={FirstBackInTheLine ?? "-"}",
                $"  downedGotUpWithoutBeingRaised={DownedGotUpWithoutBeingRaised} into=[{Map(DownedGotUpInto)}]",
                $"    first={FirstDownedGotUp ?? "-"}",
                $"  fledStoppedFleeing={FledStoppedFleeing} into=[{Map(FledStoppedInto)}] first={FirstFledStopped ?? "-"}",
                $"  joinsSeen={JoinsSeen} joinedAndLeftTheLineInTheSameTick={JoinedAndLeftTheLineInTheSameTick} into=[{Map(JoinedIntoNothing)}]",
                $"    first={FirstJoinedAndOut ?? "-"}",
                $"  joinedAndTheFightTookThemBackInTheSameTick={JoinedAndTheFightTookThemBackInTheSameTick} into=[{Map(JoinedIntoTheFight)}] (by design, not asserted on)",
                $"  joinedAndTheWaveEndedUnderThem={JoinedAndTheWaveEndedUnderThem} (ResolveWave stood the line down on the join tick; by design, not asserted on)",
                $"  stepsThatMovedNoTick={StepsThatMovedNoTick} blowsDrawnWhilePaused={BlowsDrawnWhilePaused} ofThemOnABody={BlowsDrawnOnABodyWhilePaused}",
                $"    windowsOpened=[{(Windows.Count == 0 ? "-" : string.Join(' ', Windows))}] (tick:blowsOnBodies/blowsDrawn on the frame that opens it)",
                $"  fightingTicksWithNothingToFight={FightingTicksWithNothingToFight} raiderBodyTicks={RaiderBodyTicks} maxRaiderBodiesOnTheMap={MaxRaiderBodiesOnTheMap} yieldsBookedByAFighter={YieldsBookedByAFighter}",
                $"  longestSpellInTheLine={LongestSpellInTheLine} lowestSatietyInTheLine={LowestSatietyInTheLine}",
                $"  LENGTH {fixtureName}/{seed} blowsToFellARaider={Mean(BlowsToFellARaider)} (n={BlowsToFellARaider.Count}) " +
                    $"ticksARaiderStoodUnderFire={Mean(TicksARaiderStoodUnderFire)} " +
                    $"hitTicksToFellADefender={Mean(HitTicksToFellADefender)} (n={HitTicksToFellADefender.Count}) " +
                    $"ticksADefenderStoodUnderFire={Mean(TicksADefenderStoodUnderFire)} " +
                    $"waveSpan={Mean(WaveSpans)} (n={WaveSpans.Count})",
                $"  SPREAD {fixtureName}/{seed} defenderBlows={DefenderBlows} " +
                    $"damage={(DefenderBlows == 0 ? "-" : $"{DefenderDamageLowest}..{DefenderDamageHighest} mean={DefenderDamageTotal * 100 / DefenderBlows}/100")} " +
                    $"profiles={DamageByProfile.Count} scattered={DamageByProfile.Count(pair => pair.Value.Count > 1)} " +
                    $"hpOffRaiders={HealthTakenOffRaiders} hpOffDefenders={HealthTakenOffDefenders}",
                $"    widest=[{string.Join(' ', ScatteredProfiles.Take(6))}]",
                $"  MEMORY {fixtureName}/{seed} refusalsByMemory={RefusalsByMemory} " +
                    $"creatureTicksHoldingALiveMemory={CreatureTicksHoldingALiveMemory} " +
                    $"ofThemTooHungryToRefuse={CreatureTicksAMemoryWasSwitchedOffByHunger}",
                $"    memoriesHeldAtTheEnd=[{string.Join(' ', MemoriesHeldAtTheEnd)}]",
                $"  SWEEP {fixtureName}/{seed} joins={JoinsSeen} score={(Score is null ? "none" : Score.ToString())} outcome={Outcome} raidCost={RaidCost} wavesResolved={WavesResolved} raidCostPerWave={(WavesResolved == 0 ? -1 : RaidCost / WavesResolved)} " +
                    $"mealsStolen={MealsStolen} mealsProduced={MealsProduced} defendersDowned={DefendersDowned} defendersFled={DefendersFled}",
                $"  RATION {fixtureName}/{seed} satietyAtWaveArrival=[{string.Join(' ', SatietyAtArrival)}]");
        }
    }

    private static PrototypeCommandLog LoadFixture(string fixtureName) =>
        PrototypeCommandDocument.Load(Path.Combine(
            FindRepositoryRoot(), "scenarios", "prototype1", $"{fixtureName}.commands.v2.json"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DungeonFortress.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
