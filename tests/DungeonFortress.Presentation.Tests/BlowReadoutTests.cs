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
    /// The tick the shipped <c>prepared</c> journal lands two blows and a kill on.
    /// Snapshot <c>Tick</c> 1318 draws step 1317: Кремень (1) strikes raider 1 for
    /// 5, Тишина (8) strikes it for 2 and puts it down.
    /// </summary>
    private const int BlowTick = 1318;

    private static PrototypeSnapshot Battle() =>
        PresentationFixtures.RunFixture("prepared", BlowTick);

    private static PrototypeSnapshot Quiet() =>
        PresentationFixtures.RunFixture("prepared", 600);

    [Fact]
    public void A_recorded_blow_names_the_striker_the_target_and_the_damage()
    {
        var reading = BlowReadout.Of(Battle());

        Assert.Equal(2, reading.Blows.Count);
        Assert.All(reading.Blows, blow => Assert.Equal(BlowEvidence.Recorded, blow.Evidence));

        var hit = Assert.Single(reading.Blows, blow => blow.Outcome == BlowOutcome.Hit);
        Assert.Equal(new BodyRef(BodyKind.Creature, 1), hit.Attacker);
        Assert.Equal(new BodyRef(BodyKind.Raider, 1), hit.Target);
        Assert.Equal(5, hit.Damage);

        var kill = Assert.Single(reading.Blows, blow => blow.Outcome == BlowOutcome.Downed);
        Assert.Equal(new BodyRef(BodyKind.Creature, 8), kill.Attacker);
        Assert.Equal(new BodyRef(BodyKind.Raider, 1), kill.Target);
        Assert.Equal(2, kill.Damage);
    }

    /// <summary>
    /// The two poses the pack has and the adapter could not reach before this
    /// change. Striking draws back, being struck recoils.
    /// </summary>
    [Fact]
    public void The_striker_winds_up_and_the_target_flinches()
    {
        var reading = BlowReadout.Of(Battle());

        Assert.Equal(
            BodyActionPhase.Windup,
            reading.PhaseOf(new BodyRef(BodyKind.Creature, 1)));
        Assert.Equal(
            BodyActionPhase.Windup,
            reading.PhaseOf(new BodyRef(BodyKind.Creature, 8)));
        Assert.Equal(
            BodyActionPhase.Flinch,
            reading.PhaseOf(new BodyRef(BodyKind.Raider, 1)));
    }

    /// <summary>
    /// A body nothing happened to keeps the pose its mode chooses, and a moment
    /// with no blow in it is the shared <see cref="BlowReading.Empty"/>. Most ticks
    /// of a party are that moment.
    /// </summary>
    [Fact]
    public void A_body_no_blow_touches_has_no_phase_and_a_quiet_tick_has_no_blows()
    {
        var battle = BlowReadout.Of(Battle());
        Assert.Equal(
            BodyActionPhase.None,
            battle.PhaseOf(new BodyRef(BodyKind.Creature, 5)));
        Assert.Equal(BodyActionPhase.None, battle.PhaseOf(new BodyRef(BodyKind.Raider, 3)));

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
        Assert.NotEmpty(BlowReadout.Of(PresentationFixtures.RunFixture("prepared", 1318)).Blows);

        // Step 1317 is the one with the kill in it. One tick later the world has
        // moved on and the same entries are stale.
        var later = BlowReadout.Of(PresentationFixtures.RunFixture("prepared", 1319));
        Assert.DoesNotContain(
            later.Blows,
            blow => blow.Outcome == BlowOutcome.Downed &&
                blow.Target == new BodyRef(BodyKind.Raider, 1));
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
        var reading = BlowReadout.Of(Battle());
        var hit = Assert.Single(reading.Blows, blow => blow.Outcome == BlowOutcome.Hit);
        var downed = Assert.Single(reading.Blows, blow => blow.Outcome == BlowOutcome.Downed);
        var untouched = new BodyRef(BodyKind.Raider, 3);

        Assert.NotEqual(hit.Outcome, downed.Outcome);

        Assert.Null(reading.OutcomeOf(untouched));
        Assert.Empty(reading.Struck(untouched));
        Assert.Equal(BodyActionPhase.None, reading.PhaseOf(untouched));

        Assert.Equal(1, PrototypeTuning.DamageFloor);
    }

    /// <summary>
    /// Two blows on one body at once: both are kept, and the flash carries the
    /// harder outcome. Losing the kill to a scratch that arrived in the same tick
    /// would say the raider is still up.
    /// </summary>
    [Fact]
    public void Several_blows_on_one_body_keep_the_worse_outcome()
    {
        var target = new BodyRef(BodyKind.Raider, 1);
        var reading = BlowReadout.Of(Battle());

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
