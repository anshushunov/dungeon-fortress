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
}
