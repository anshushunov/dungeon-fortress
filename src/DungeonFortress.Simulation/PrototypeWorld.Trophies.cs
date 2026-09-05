namespace DungeonFortress.Simulation;

/// <summary>
/// Trophies (docs/design/TROPHY_WEAPON.md): where a weapon comes from, who
/// picks it up, and what that costs whom.
/// </summary>
public sealed partial class PrototypeWorld
{
    /// <summary>
    /// One point of might for a raider of base might, one more for every point
    /// above it. Never below <see cref="PrototypeTuning.TrophyBonusBase"/>: a
    /// raider whose jitter took it under base still drops something worth
    /// picking up, otherwise the first wave would drop nothing and the slice
    /// would have nothing to show.
    /// </summary>
    private static int TrophyBonusOf(int raiderMight) =>
        Math.Max(
            PrototypeTuning.TrophyBonusBase,
            PrototypeTuning.TrophyBonusBase + raiderMight - PrototypeTuning.RaiderMightBase);

    /// <summary>Every tile a weapon lies on, once each, in canonical order.</summary>
    private IEnumerable<GridPoint> LooseWeaponTiles() =>
        _looseWeapons.Select(entry => entry.Position).Distinct().OrderBy(tile => tile);

    /// <summary>
    /// A raider that has just been put down leaves its weapon on the tile it
    /// fell on. Called once per raider, from the one place a raider goes down.
    /// </summary>
    private void DropRaiderWeapon(RaiderState raider, CreatureState downedBy)
    {
        _looseWeapons.Add((
            raider.Position,
            new WeaponState(raider.Name, raider.Id, raider.Wave, TrophyBonusOf(raider.Might), downedBy.Id)));
    }

    /// <summary>
    /// The creature standing on the tile takes the first weapon there in
    /// canonical order (wave, then name). Whoever put the raider down and did
    /// not get the blade pays a grudge — if still standing: a creature on the
    /// floor or on the run has other things to resent (spec §3) — but only
    /// once. The same <see cref="WeaponState"/> keeps circulating every time
    /// its holder falls and somebody else picks it back up (spec §2.8), and
    /// <see cref="WeaponState.DownedBy"/> never changes; without a debt flag
    /// every later pickup would re-grudge the raider's original downer for
    /// the one kill it already paid for (trophy slice fix round 3).
    /// </summary>
    private void TakeWeapon(CreatureState creature, JobState job)
    {
        var entry = _looseWeapons
            .Where(entry => entry.Position == job.Origin)
            .OrderBy(entry => entry.Weapon.Wave)
            .ThenBy(entry => entry.Weapon.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (entry.Weapon is null || creature.Weapon is not null)
        {
            // The weapon went before this creature arrived, or the creature
            // armed itself in the meantime. Nothing to take; the job ends.
            return;
        }

        _looseWeapons.Remove(entry);
        creature.Weapon = entry.Weapon;
        Accrue(creature, LoyaltyAxis.Benefit, "benefit_trophy", PrototypeTuning.LoyaltyBenefitTrophy);
        RecordDecision(
            creature,
            "trophy_taken",
            new Dictionary<string, int>
            {
                ["raiderId"] = entry.Weapon.RaiderId,
                ["bonus"] = entry.Weapon.Bonus,
                ["downedBy"] = entry.Weapon.DownedBy,
            },
            JobKind.Claim,
            job.Origin);

        // The debt is paid once. Read before it is set, so this same pickup
        // is the last one that can still owe it.
        var alreadyPaid = entry.Weapon.Taken;
        entry.Weapon.Taken = true;
        if (alreadyPaid || entry.Weapon.DownedBy == creature.Id)
        {
            return;
        }

        var downer = _creatures.FirstOrDefault(other => other.Id == entry.Weapon.DownedBy);
        if (downer is null || downer.Mode is CreatureMode.Downed or CreatureMode.Fled)
        {
            return;
        }

        Accrue(downer, LoyaltyAxis.Grudge, "grudge_trophy_taken", PrototypeTuning.LoyaltyGrudgeTrophyTaken);
        RecordDecision(
            downer,
            "trophy_lost",
            new Dictionary<string, int>
            {
                ["raiderId"] = entry.Weapon.RaiderId,
                ["takenBy"] = creature.Id,
            });
    }

    /// <summary>
    /// A holder that goes down or breaks leaves the blade where it stood, and
    /// it waits for the first again (spec §2.8). Recorded before the decision
    /// that put the creature there, so the journal keeps both and the panel
    /// keeps the later-written one.
    /// </summary>
    private void DropCreatureWeapon(CreatureState creature)
    {
        if (creature.Weapon is not { } weapon)
        {
            return;
        }

        _looseWeapons.Add((creature.Position, weapon));
        creature.Weapon = null;
        RecordDecision(
            creature,
            "trophy_dropped",
            new Dictionary<string, int>
            {
                ["raiderId"] = weapon.RaiderId,
                ["bonus"] = weapon.Bonus,
            });
    }
}
