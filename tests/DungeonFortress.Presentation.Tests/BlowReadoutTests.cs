using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// What the picture is told about a blow, checked without starting the engine
/// (ADR 0011).
///
/// The snapshots are real: a shipped journal run to the tick a blow happens on.
/// Where a case the shipped party does not produce has to be covered — a raider's
/// blow that a defender survives, two blows on one body — the event list is
/// replaced with <c>with</c>, which is stating the input rather than faking the
/// answer, exactly as <see cref="PresentationFixtures"/> describes.
/// </summary>
public sealed class BlowReadoutTests
{
    /// <summary>
    /// The scene every check in this class reads: the tick the shipped
    /// <c>prepared</c> journal lands two blows and a kill on, one body taking both.
    ///
    /// <para><b>The tick is found and no longer written down, and that is the
    /// repair rather than a convenience.</b> It was pinned by number twice — 1318
    /// until Issue #361 made the jitter live, then 1313 — and the balance change
    /// of Issue #333 moved it a third time, to 1325 with a different cast
    /// entirely: the old one, Кремень (1) and Тишина (8) on raider 1, does not
    /// occur in this journal at all any more. A number that has to be re-pinned
    /// every time the party is re-tuned is a trap for whoever re-pins it next, so
    /// the scene is now located by the shape that makes it the right scene.</para>
    ///
    /// <para><b>The shape, stated exactly, because the obvious recipe is the wrong
    /// one.</b> The first tick carrying two recorded blows on ONE body of which
    /// one is a kill. «First» alone used to be insufficient when the point was to
    /// land on one nominated cast; it is sufficient now precisely because nothing
    /// below names a cast — every identity is read off the scene that was found.
    /// The scene is asserted to exist, so a party that stopped producing it fails
    /// loudly instead of silently testing nothing.</para>
    /// </summary>
    private static (PrototypeSnapshot State, int Tick) BattleScene()
    {
        var world = new PrototypeWorld(PresentationFixtures.LogOf("prepared"));
        while (!world.IsComplete)
        {
            world.Step();
            var state = world.GetSnapshot();
            var reading = BlowReadout.Of(state);
            if (reading.Blows.Count == 2 &&
                reading.Blows[0].Target == reading.Blows[1].Target &&
                reading.Blows.Any(blow => blow.Outcome == BlowOutcome.Downed) &&
                reading.Blows.Any(blow => blow.Outcome == BlowOutcome.Hit))
            {
                return (state, state.Tick);
            }
        }

        throw new InvalidOperationException(
            "the shipped `prepared` journal no longer contains a tick on which one body takes " +
            "two recorded blows and the second puts it down. Every check in this class reads " +
            "that scene, so this is a change of what the party does and not a broken test.");
    }

    private static PrototypeSnapshot Battle() => BattleScene().State;

    private static PrototypeSnapshot Quiet() =>
        PresentationFixtures.RunFixture("prepared", 600);

    /// <summary>
    /// The claim of the name: a recorded blow names its striker, its target and
    /// its damage, and the three agree with the journal entry the reading was made
    /// from. The identities are read off the scene rather than written down here —
    /// what is asserted is that the reading transcribes the journal faithfully,
    /// which is the adapter's whole job and is what a literal id could never check.
    /// </summary>
    [Fact]
    public void A_recorded_blow_names_the_striker_the_target_and_the_damage()
    {
        var (state, tick) = BattleScene();
        var reading = BlowReadout.Of(state);

        Assert.Equal(2, reading.Blows.Count);
        Assert.All(reading.Blows, blow => Assert.Equal(BlowEvidence.Recorded, blow.Evidence));

        var hit = Assert.Single(reading.Blows, blow => blow.Outcome == BlowOutcome.Hit);
        var kill = Assert.Single(reading.Blows, blow => blow.Outcome == BlowOutcome.Downed);

        // One body, two strikers, and both strikers named.
        Assert.Equal(hit.Target, kill.Target);
        Assert.Equal(BodyKind.Raider, hit.Target.Kind);
        Assert.NotNull(hit.Attacker);
        Assert.NotNull(kill.Attacker);
        Assert.Equal(BodyKind.Creature, hit.Attacker!.Value.Kind);
        Assert.Equal(BodyKind.Creature, kill.Attacker!.Value.Kind);
        Assert.NotEqual(hit.Attacker, kill.Attacker);

        // And the naming is the journal's own, not the adapter's invention. Read
        // independently from the events of the tick that has just run.
        var acted = state.Tick - 1;
        var struck = state.Events
            .Where(item => item.LastTick == acted &&
                item.ReasonCode == BlowReadout.AttackReason &&
                item.Details["raiderId"] == hit.Target.Id)
            .ToArray();
        Assert.Contains(
            struck,
            item => item.CreatureId == hit.Attacker!.Value.Id && item.Details["damage"] == hit.Damage);
        Assert.Contains(
            struck,
            item => item.CreatureId == kill.Attacker!.Value.Id && item.Details["damage"] == kill.Damage);
        Assert.True(
            hit.Damage > 0 && kill.Damage > 0,
            $"tick {tick}: a recorded blow reported {hit.Damage} and {kill.Damage} damage.");
    }

    /// <summary>
    /// The two poses the pack has and the adapter could not reach before this
    /// change. Striking draws back, being struck recoils.
    /// </summary>
    [Fact]
    public void The_striker_winds_up_and_the_target_flinches()
    {
        var reading = BlowReadout.Of(Battle());

        // Whoever struck in this scene winds up and whoever was struck flinches.
        // Asked of every body the scene names rather than of three written-down
        // ids, so it keeps meaning the same thing when the party is re-tuned.
        foreach (var blow in reading.Blows)
        {
            Assert.Equal(BodyActionPhase.Windup, reading.PhaseOf(blow.Attacker!.Value));
            Assert.Equal(BodyActionPhase.Flinch, reading.PhaseOf(blow.Target));
        }
    }

    /// <summary>
    /// A body nothing happened to keeps the pose its mode chooses, and a moment
    /// with no blow in it is the shared <see cref="BlowReading.Empty"/>. Most ticks
    /// of a party are that moment.
    /// </summary>
    [Fact]
    public void A_body_no_blow_touches_has_no_phase_and_a_quiet_tick_has_no_blows()
    {
        var state = Battle();
        var battle = BlowReadout.Of(state);
        var touched = battle.Blows
            .SelectMany(blow => new[] { blow.Attacker!.Value, blow.Target })
            .ToHashSet();

        // Every body of the scene that no blow touched, rather than two written
        // down ids that a re-tuned party may well have put in the fight.
        var untouched = state.Creatures
            .Select(creature => new BodyRef(BodyKind.Creature, creature.Id))
            .Concat(state.Raiders.Select(raider => new BodyRef(BodyKind.Raider, raider.Id)))
            .Where(body => !touched.Contains(body))
            .ToArray();
        Assert.NotEmpty(untouched);
        Assert.All(untouched, body => Assert.Equal(BodyActionPhase.None, battle.PhaseOf(body)));

        var quiet = BlowReadout.Of(Quiet());
        Assert.Empty(quiet.Blows);
        Assert.False(quiet.Landed);
        Assert.Same(BlowReading.Empty, quiet);
    }

    /// <summary>
    /// The blows of the tick that has just run, and not the tick before it. This
    /// is the one-off that would be invisible on a screenshot and wrong on every
    /// frame: <c>PrototypeWorld</c> stamps an event with the tick number it had
    /// while the step ran, and increments afterwards.
    /// </summary>
    [Fact]
    public void Only_the_tick_that_has_just_run_is_drawn()
    {
        var (state, tick) = BattleScene();
        var now = BlowReadout.Of(state);
        Assert.NotEmpty(now.Blows);
        var felled = Assert.Single(now.Blows, blow => blow.Outcome == BlowOutcome.Downed).Target;

        // One tick later the world has moved on and the same entries are stale.
        var later = BlowReadout.Of(PresentationFixtures.RunFixture("prepared", tick + 1));
        Assert.DoesNotContain(
            later.Blows,
            blow => blow.Outcome == BlowOutcome.Downed && blow.Target == felled);
    }

    /// <summary>
    /// A raider's blow that a defender survives is recorded nowhere, so the drop
    /// in hit points is the whole of the evidence — and the striker stays
    /// unnamed rather than guessed.
    /// </summary>
    [Fact]
    public void A_blow_the_journal_does_not_record_is_read_off_the_hit_points()
    {
        var state = Quiet();
        var wounded = state.Creatures[2];
        var reading = BlowReadout.Of(
            state,
            new Dictionary<int, int> { [wounded.Id] = wounded.Hp + 4 });

        var blow = Assert.Single(reading.Blows);
        Assert.Equal(BlowEvidence.Inferred, blow.Evidence);
        Assert.Null(blow.Attacker);
        Assert.Equal(new BodyRef(BodyKind.Creature, wounded.Id), blow.Target);
        Assert.Equal(4, blow.Damage);
        Assert.Equal(BlowOutcome.Hit, blow.Outcome);
        Assert.Equal(
            BodyActionPhase.Flinch,
            reading.PhaseOf(new BodyRef(BodyKind.Creature, wounded.Id)));
    }

    /// <summary>
    /// Healing is not a blow, and neither is a creature the previous reading has
    /// never seen. Both would otherwise flash somebody for nothing.
    /// </summary>
    [Fact]
    public void Regained_or_unknown_hit_points_are_not_a_blow()
    {
        var state = Quiet();
        var creature = state.Creatures[0];
        var reading = BlowReadout.Of(
            state,
            new Dictionary<int, int> { [creature.Id] = creature.Hp - 3 });

        Assert.Empty(reading.Blows);
        Assert.Empty(BlowReadout.Of(state, new Dictionary<int, int>()).Blows);
    }

    /// <summary>
    /// The journal wins over the hit points where it speaks. A defender put down
    /// loses hit points too, and counting both would draw the same blow twice —
    /// once with a striker and once without.
    /// </summary>
    [Fact]
    public void A_defender_put_down_is_one_blow_and_not_two()
    {
        var state = Quiet();
        var defender = state.Creatures[3];
        var struck = state with
        {
            Events =
            [
                Event(state.Tick - 1, defender.Id, BlowReadout.DefenderDownedReason,
                    new Dictionary<string, int> { ["raiderId"] = 7, ["damage"] = 6 }),
            ],
        };

        var reading = BlowReadout.Of(
            struck,
            new Dictionary<int, int> { [defender.Id] = defender.Hp + 6 });

        var blow = Assert.Single(reading.Blows);
        Assert.Equal(BlowEvidence.Recorded, blow.Evidence);
        Assert.Equal(new BodyRef(BodyKind.Raider, 7), blow.Attacker);
        Assert.Equal(new BodyRef(BodyKind.Creature, defender.Id), blow.Target);
        Assert.Equal(6, blow.Damage);
        Assert.Equal(BlowOutcome.Downed, blow.Outcome);
    }

    /// <summary>
    /// Hit, no blow and going down are three different readings, and the
    /// difference is checked here rather than judged on a screenshot. There is no
    /// fourth reading for a miss because the simulation has no miss: an attack in
    /// reach always lands and the damage is floored at
    /// <see cref="PrototypeTuning.DamageFloor"/>. "Not this tick" is what takes its
    /// place, and it is the absence of every mark.
    /// </summary>
    [Fact]
    public void A_hit_a_body_left_alone_and_a_body_put_down_read_differently()
    {
        var state = Battle();
        var reading = BlowReadout.Of(state);
        var hit = Assert.Single(reading.Blows, blow => blow.Outcome == BlowOutcome.Hit);
        var downed = Assert.Single(reading.Blows, blow => blow.Outcome == BlowOutcome.Downed);
        // A body of the found scene that nobody struck, read off the scene for the
        // reason the scene itself is: `Raider 3` was the cast of a tick this class
        // was pinned to, and a literal beside a scene that moves is the trap the
        // pinning was taken out for. Its sibling
        // `Several_blows_on_one_body_keep_the_worse_outcome` was red on exactly
        // that; this one was green by luck.
        var untouched = new BodyRef(
            BodyKind.Raider,
            state.Raiders
                .First(raider => reading.Blows.All(blow =>
                    blow.Target != new BodyRef(BodyKind.Raider, raider.Id)))
                .Id);

        Assert.NotEqual(BlowEffects.DamageColor(hit), BlowEffects.DamageColor(downed));
        Assert.NotEqual(
            BlowEffects.FlashColor(BlowOutcome.Hit),
            BlowEffects.FlashColor(BlowOutcome.Downed));

        Assert.Null(reading.OutcomeOf(untouched));
        Assert.Empty(reading.Struck(untouched));
        Assert.Equal(BodyActionPhase.None, reading.PhaseOf(untouched));

        Assert.Equal(1, PrototypeTuning.DamageFloor);
    }

    /// <summary>
    /// Two blows on one body at once: both are kept, and the flash carries the
    /// harder outcome. Losing the kill to a scratch that arrived in the same tick
    /// would say the raider is still up.
    ///
    /// <para><b>The body is read off the scene and no longer written down.</b> It
    /// was <c>Raider 1</c> — the cast of the tick this class used to be pinned to.
    /// The repair that took the class off a tick number left this one literal
    /// behind, so the check went on asking about a raider the found scene does not
    /// strike, and it went red the moment the party moved. Same trap, one layer
    /// down: the scene is located by its shape and the identity beside it is
    /// still a number.</para>
    /// </summary>
    [Fact]
    public void Several_blows_on_one_body_keep_the_worse_outcome()
    {
        var reading = BlowReadout.Of(Battle());
        var target = reading.Blows[0].Target;

        Assert.Equal(2, reading.Struck(target).Count);
        Assert.Equal(BlowOutcome.Downed, reading.OutcomeOf(target));
        Assert.True(reading.Landed);
    }

    /// <summary>
    /// A body that strikes and is struck on the same tick is drawn recoiling,
    /// whichever order the two blows arrive in.
    /// </summary>
    [Fact]
    public void Being_struck_wins_over_striking_in_either_order()
    {
        var state = Quiet();
        var fighter = state.Creatures[1];
        var attack = Event(state.Tick - 1, fighter.Id, BlowReadout.AttackReason,
            new Dictionary<string, int> { ["raiderId"] = 2, ["damage"] = 3 });
        var struckDown = Event(state.Tick - 1, fighter.Id, BlowReadout.DefenderDownedReason,
            new Dictionary<string, int> { ["raiderId"] = 2, ["damage"] = 4 });

        foreach (var events in new[]
                 {
                     new[] { attack, struckDown },
                     [struckDown, attack],
                 })
        {
            var reading = BlowReadout.Of(state with { Events = events });
            Assert.Equal(
                BodyActionPhase.Flinch,
                reading.PhaseOf(new BodyRef(BodyKind.Creature, fighter.Id)));
        }
    }

    /// <summary>
    /// A kill entry whose blow the journal did not put next to it still draws a
    /// blow. The shipped journals never produce this, which is precisely why the
    /// branch is stated here: the ordering is the simulation's business.
    /// </summary>
    [Fact]
    public void A_kill_without_its_own_blow_is_still_drawn()
    {
        var state = Quiet();
        var reading = BlowReadout.Of(state with
        {
            Events =
            [
                Event(state.Tick - 1, 4, BlowReadout.RaiderDownedReason,
                    new Dictionary<string, int> { ["raiderId"] = 6 }),
            ],
        });

        var blow = Assert.Single(reading.Blows);
        Assert.Equal(new BodyRef(BodyKind.Creature, 4), blow.Attacker);
        Assert.Equal(new BodyRef(BodyKind.Raider, 6), blow.Target);
        Assert.Equal(BlowOutcome.Downed, blow.Outcome);
        Assert.Equal(0, blow.Damage);
    }

    [Fact]
    public void A_snapshot_is_required()
    {
        Assert.Throws<ArgumentNullException>(() => BlowReadout.Of(null!));
    }

    private static PrototypeEvent Event(
        int tick,
        int creatureId,
        string reason,
        Dictionary<string, int> details) =>
        new(tick, tick, creatureId, reason, details, 1, null, null);
}
