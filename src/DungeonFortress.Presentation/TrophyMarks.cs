using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>One gold mark beside the near arm of a creature carrying a trophy.</summary>
public readonly record struct TrophyMark(ViewPoint OffsetRef, string Color);

/// <summary>
/// docs/design/TROPHY_WEAPON.md §5: the blade is visible without the inspector,
/// the way a wound is (InjuryMarks). One mark, one colour, no tiers — the
/// bonus is read off the panel, not off the mark. Drawn as BodyState, like the
/// wound: it is what this body carries now, not what has just happened.
/// </summary>
public static class TrophyMarks
{
    /// <summary>Just outside the arm anchor of <see cref="InjuryMarks"/>, so a wounded arm and a blade never overlap.</summary>
    public static readonly ViewPoint OffsetRef = new(-10.5, -7.5);

    public const double RadiusRef = InjuryMarks.RadiusRef;
    public const double RimWidthRef = InjuryMarks.RimWidthRef;
    public const string Color = "#eab308";
    public const string RimColor = "#713f12";

    public static TrophyMark? Of(PrototypeCreatureSnapshot creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Weapon is null || creature.Mode == CreatureMode.Downed)
        {
            return null;
        }

        return new TrophyMark(OffsetRef, Color);
    }
}
