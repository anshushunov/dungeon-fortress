using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// A moment of a blow, as far as the picture is concerned. It is not canonical
/// state and never becomes any: nothing here reaches the snapshot, the checksum
/// or the command log, and a run that always answers <see cref="None"/> is the
/// game exactly as it was.
///
/// <para>
/// <b>Why it is a parameter and not a derivation.</b> The two poses it names are
/// in the art (<c>docs/art/goblin-v2-provenance.md</c>) and are loaded by the
/// adapter, but the simulation does not today say when a creature is drawing back
/// or being struck. What it says is nearby and not the same thing:
/// <c>PrototypeCreatureSnapshot.LastDecision</c> carries a tick and a reason code,
/// and <c>combat_attack</c> means the blow has already landed — a wind-up drawn
/// from it would be drawn after the strike it precedes. A defender that is hit
/// and survives records nothing at all, and <c>PrototypeRaiderSnapshot</c> has no
/// decision field. So the honest answer here is a seam: <see cref="BodySprites"/>
/// can choose either pose, and the subtask that makes a blow readable decides
/// where the phase comes from — an interpolation the view owns, or a new
/// presentation-only field on the snapshot.
/// </para>
/// </summary>
public enum BodyActionPhase
{
    /// <summary>No blow is being shown; the creature's mode alone chooses the pose.</summary>
    None,

    /// <summary>Drawing back, before the strike.</summary>
    Windup,

    /// <summary>Recoiling, after being struck.</summary>
    Flinch,
}

/// <summary>
/// Which state of the creature pack a body is drawn in.
///
/// <para>
/// It lives in the presentation assembly rather than in the Godot adapter for the
/// reason ADR 0011 gives: this is a decision, it has cases, and it is checkable
/// without starting the engine. The adapter's job is to hand it a snapshot and
/// hang the returned texture on the rectangle <see cref="CameraView.GoblinDrawRect"/>
/// computes.
/// </para>
/// </summary>
public static class BodySprites
{
    /// <summary>
    /// Every state the connected pack has, in the order
    /// <c>docs/art/goblin-v2-provenance.md</c> generated them. The adapter loads
    /// exactly this list, so a pose that is reachable from
    /// <see cref="CrewKey"/> or <see cref="RaiderKey"/> but missing here would be
    /// a missing texture at runtime rather than a surprise in a frame.
    /// </summary>
    public static IReadOnlyList<string> States { get; } =
        ["idle", "work", "combat", "windup", "flinch", "downed"];

    /// <summary>
    /// The generation of the pack the runtime draws. Issue #77 moved it from
    /// <c>v1</c> — four square 96x96 states — to <c>v2</c>, six states on a
    /// 272x192 canvas authored for the owner's 170 % body size.
    /// </summary>
    public const string PackVersion = "v2";

    /// <summary>
    /// The name of one state's file inside <c>assets/generated/goblins</c>. The
    /// adapter builds a <c>res://</c> path around this; the shape of the name is
    /// shared with <c>scripts/test-goblin-sprite-import.ps1</c>, which checks that
    /// a fresh Godot project really imports every one of them.
    /// </summary>
    public static string FileName(string state) => $"goblin_{state}_{PackVersion}.png";

    /// <summary>
    /// The pose a member of the crew is drawn in.
    ///
    /// <para>
    /// <paramref name="phase"/> wins over the creature's mode, because a blow is
    /// the thing a player is being shown at that moment — except for
    /// <see cref="CreatureMode.Downed"/>, which wins over everything: a body on
    /// the ground does not wind up, and drawing it standing would contradict the
    /// state the rest of the HUD reports.
    /// </para>
    /// </summary>
    public static string CrewKey(CreatureMode mode, BodyActionPhase phase = BodyActionPhase.None)
    {
        if (mode == CreatureMode.Downed)
        {
            return "downed";
        }

        if (PhaseKey(phase) is { } struck)
        {
            return struck;
        }

        return mode switch
        {
            CreatureMode.Working => "work",
            CreatureMode.Fighting => "combat",
            _ => "idle",
        };
    }

    /// <summary>
    /// The pose a raider is drawn in. A raider on its way back to the gate is
    /// carrying, which the pack's <c>work</c> pose is the closest thing to; that
    /// mapping predates Issue #77 and is kept.
    /// </summary>
    public static string RaiderKey(
        RaiderMode mode,
        bool returningToGate,
        BodyActionPhase phase = BodyActionPhase.None)
    {
        if (mode == RaiderMode.Downed)
        {
            return "downed";
        }

        if (PhaseKey(phase) is { } struck)
        {
            return struck;
        }

        return mode switch
        {
            RaiderMode.Raiding when returningToGate => "work",
            RaiderMode.Raiding => "combat",
            _ => "idle",
        };
    }

    private static string? PhaseKey(BodyActionPhase phase) => phase switch
    {
        BodyActionPhase.Windup => "windup",
        BodyActionPhase.Flinch => "flinch",
        _ => null,
    };
}
