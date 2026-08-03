using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The structural half of Issue #210, by the same method
/// <see cref="SideOutlineAdapterTests"/> and <see cref="WorldDrawPassGuardTests"/>
/// use: the adapter is read as text, the engine is not started (ADR 0011).
///
/// <para>
/// A reading that nothing draws is a rule about nothing, and a pose the pack
/// contains but the adapter never asks for is exactly the state this Issue was
/// opened about — <c>windup</c> and <c>flinch</c> shipped in the v2 pack, were
/// loaded at start-up, and were unreachable because both callers passed an
/// unconditional <c>BodyActionPhase.None</c>. These checks are what makes that
/// unable to come back quietly.
/// </para>
/// </summary>
public sealed class BlowAdapterTests
{
    /// <summary>
    /// The two poses become reachable, and they stay reachable. Neither sprite key
    /// hands <c>BodySprites</c> a literal phase, and the literal that made them
    /// unreachable is nowhere in the adapter at all — including the spellings a
    /// per-call check would miss, such as a phase folded into a helper.
    /// </summary>
    [Fact]
    public void Neither_sprite_key_passes_a_literal_phase()
    {
        var crew = Assert.Single(AdapterSource.CallsTo(
            AdapterSource.Body("CrewSpriteKey"),
            $"{nameof(BodySprites)}.{nameof(BodySprites.CrewKey)}"));
        Assert.Equal(2, crew.Arguments.Count);
        Assert.Contains("BodyPhase(", crew.Arguments[1], StringComparison.Ordinal);

        var raider = Assert.Single(AdapterSource.CallsTo(
            AdapterSource.Body("RaiderSpriteKey"),
            $"{nameof(BodySprites)}.{nameof(BodySprites.RaiderKey)}"));
        Assert.Equal(3, raider.Arguments.Count);
        Assert.Contains("BodyPhase(", raider.Arguments[2], StringComparison.Ordinal);

        Assert.DoesNotContain(
            $"{nameof(BodyActionPhase)}.{nameof(BodyActionPhase.None)}",
            AdapterSource.Masked,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And the phase itself comes from the journal reading rather than from a
    /// decision taken here. A <c>BodyPhase</c> that answered on its own would leave
    /// the check above green while putting every body back in its idle pose.
    /// </summary>
    [Fact]
    public void The_phase_comes_from_the_reading_built_for_this_tick()
    {
        var phase = AdapterSource.Body("BodyPhase");
        Assert.Contains($".{nameof(BlowReading.PhaseOf)}(", phase, StringComparison.Ordinal);
        Assert.Contains(nameof(BodyRef), phase, StringComparison.Ordinal);

        // Built once per tick, where the snapshot is taken, and from the canonical
        // journal plus the hit points measured before the tick ran.
        var refresh = AdapterSource.Body("RefreshState");
        var built = Assert.Single(AdapterSource.CallsTo(
            refresh,
            $"{nameof(BlowReadout)}.{nameof(BlowReadout.Of)}"));
        Assert.Equal(2, built.Arguments.Count);
        Assert.Contains(
            $"{nameof(BlowReadout)}.{nameof(BlowReadout.Of)}",
            AdapterSource.Masked,
            StringComparison.Ordinal);

        // The hit points are captured before the world runs, not after: measured
        // afterwards they would equal the values they are supposed to differ from.
        var advance = AdapterSource.Body("Advance");
        Assert.Single(AdapterSource.CallsTo(advance, "RememberCreatureHitPoints"));
        Assert.True(
            advance.IndexOf("RememberCreatureHitPoints(", StringComparison.Ordinal) <
            advance.IndexOf("RunTicks(", StringComparison.Ordinal),
            "Advance remembers the hit points after running the tick, so every " +
            "difference it could measure has already been overwritten.");
    }
}
