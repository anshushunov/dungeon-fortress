using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #77, second vertical subtask: the runtime draws the v2 creature pack.
///
/// <para>
/// Six states instead of four, on a 272x192 canvas instead of a 96x96 one. The
/// two new poses — <c>windup</c> and <c>flinch</c> — are loaded and reachable
/// here, and reachable is as far as this subtask goes: what the simulation says
/// today cannot tell anyone when a creature is drawing back or being struck, so
/// the adapter passes <see cref="BodyActionPhase.None"/> and the pose that gets
/// drawn is the one the creature's mode chose before. The seam is real code with
/// real cases, and it is checked here rather than described, so that the subtask
/// which makes a blow readable has to supply a phase and nothing else.
/// </para>
/// </summary>
public sealed class BodySpriteTests
{
    /// <summary>
    /// The pack has six states and the runtime asks for exactly those six. A pose
    /// the key functions can return but the adapter never loaded would be a
    /// missing texture in a frame — the fallback circle
    /// <c>Main.DrawGoblin</c> falls back to, which <c>verify.ps1</c> requires to
    /// have been drawn zero times.
    /// </summary>
    [Fact]
    public void Every_pose_the_view_can_choose_is_a_state_the_runtime_loads()
    {
        Assert.Equal(
            new[] { "idle", "work", "combat", "windup", "flinch", "downed" },
            BodySprites.States);

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phase in Enum.GetValues<BodyActionPhase>())
        {
            foreach (var mode in Enum.GetValues<CreatureMode>())
            {
                reachable.Add(BodySprites.CrewKey(mode, phase));
            }

            foreach (var mode in Enum.GetValues<RaiderMode>())
            {
                reachable.Add(BodySprites.RaiderKey(mode, false, phase));
                reachable.Add(BodySprites.RaiderKey(mode, true, phase));
            }
        }

        Assert.Empty(reachable.Except(BodySprites.States));

        // And nothing is loaded that no case can ever ask for, which would be a
        // texture in memory that no frame can contain.
        Assert.Empty(BodySprites.States.Except(reachable));
    }

    /// <summary>
    /// The pack the file names point at, in the shape
    /// <c>scripts/test-goblin-sprite-import.ps1</c> looks for in a fresh Godot
    /// import cache. Six names, all of the connected generation, and the v1 names
    /// gone: this is the only place in the .NET tree that says which pack ships.
    /// </summary>
    [Fact]
    public void The_connected_pack_is_v2_and_the_file_names_say_so()
    {
        Assert.Equal("v2", BodySprites.PackVersion);
        Assert.Equal("goblin_flinch_v2.png", BodySprites.FileName("flinch"));
        Assert.Equal(
            new[]
            {
                "goblin_idle_v2.png",
                "goblin_work_v2.png",
                "goblin_combat_v2.png",
                "goblin_windup_v2.png",
                "goblin_flinch_v2.png",
                "goblin_downed_v2.png",
            },
            BodySprites.States.Select(BodySprites.FileName));
    }

    /// <summary>
    /// The four poses that were already chosen from the creature's mode are chosen
    /// exactly as before. This subtask changes which pack is drawn and in what
    /// shape; it does not change what a player is looking at when nothing is being
    /// struck, and a silent change there would show up as a different frame with
    /// no line of code claiming it.
    /// </summary>
    [Theory]
    [InlineData(CreatureMode.Waiting, "idle")]
    [InlineData(CreatureMode.Moving, "idle")]
    [InlineData(CreatureMode.Eating, "idle")]
    [InlineData(CreatureMode.Resting, "idle")]
    [InlineData(CreatureMode.Mustering, "idle")]
    [InlineData(CreatureMode.Fled, "idle")]
    [InlineData(CreatureMode.Working, "work")]
    [InlineData(CreatureMode.Fighting, "combat")]
    [InlineData(CreatureMode.Downed, "downed")]
    public void A_crew_member_with_no_blow_to_show_is_drawn_as_it_was_before(
        CreatureMode mode,
        string expected)
    {
        Assert.Equal(expected, BodySprites.CrewKey(mode));
        Assert.Equal(expected, BodySprites.CrewKey(mode, BodyActionPhase.None));
    }

    /// <summary>The same statement for raiders, including the carry-home case.</summary>
    [Theory]
    [InlineData(RaiderMode.Queued, false, "idle")]
    [InlineData(RaiderMode.Escaped, false, "idle")]
    [InlineData(RaiderMode.Raiding, false, "combat")]
    [InlineData(RaiderMode.Raiding, true, "work")]
    [InlineData(RaiderMode.Downed, false, "downed")]
    [InlineData(RaiderMode.Downed, true, "downed")]
    public void A_raider_with_no_blow_to_show_is_drawn_as_it_was_before(
        RaiderMode mode,
        bool returningToGate,
        string expected)
    {
        Assert.Equal(expected, BodySprites.RaiderKey(mode, returningToGate));
        Assert.Equal(
            expected,
            BodySprites.RaiderKey(mode, returningToGate, BodyActionPhase.None));
    }

    /// <summary>
    /// The seam, exercised: told that a body is winding up or recoiling, the view
    /// picks the pose the pack was given for it, whatever the creature was doing
    /// otherwise. Nothing in the shipped adapter says this today — that is the
    /// point of checking it here, where the engine is not needed and the next
    /// subtask can start from a function that already works.
    /// </summary>
    [Theory]
    [InlineData(BodyActionPhase.Windup, "windup")]
    [InlineData(BodyActionPhase.Flinch, "flinch")]
    public void A_blow_being_shown_chooses_the_pose_drawn_for_it(
        BodyActionPhase phase,
        string expected)
    {
        foreach (var mode in new[]
                 {
                     CreatureMode.Waiting,
                     CreatureMode.Moving,
                     CreatureMode.Working,
                     CreatureMode.Fighting,
                     CreatureMode.Fled,
                 })
        {
            Assert.Equal(expected, BodySprites.CrewKey(mode, phase));
        }

        foreach (var mode in new[] { RaiderMode.Queued, RaiderMode.Raiding, RaiderMode.Escaped })
        {
            Assert.Equal(expected, BodySprites.RaiderKey(mode, false, phase));
            Assert.Equal(expected, BodySprites.RaiderKey(mode, true, phase));
        }
    }

    /// <summary>
    /// The one case a blow does not win: a body on the ground stays on the ground.
    /// «Downed» is canonical state that the roster, the inspector and the HP bar
    /// all report; a downed creature drawn mid-recoil would be the picture
    /// contradicting the text, which is the defect class Issue #83 exists about.
    /// </summary>
    [Fact]
    public void A_body_on_the_ground_is_never_drawn_winding_up_or_recoiling()
    {
        foreach (var phase in Enum.GetValues<BodyActionPhase>())
        {
            Assert.Equal("downed", BodySprites.CrewKey(CreatureMode.Downed, phase));
            Assert.Equal("downed", BodySprites.RaiderKey(RaiderMode.Downed, false, phase));
            Assert.Equal("downed", BodySprites.RaiderKey(RaiderMode.Downed, true, phase));
        }
    }

    /// <summary>
    /// What the simulation would have to say for a phase to be derived, stated as
    /// a check so that it is a fact about today's snapshot rather than a note
    /// somebody remembers. If any of these stops holding, the next subtask has a
    /// cheaper option than it was told it had.
    /// </summary>
    [Fact]
    public void Nothing_in_todays_snapshot_says_when_a_body_is_struck()
    {
        var state = PresentationFixtures.Baseline(1);
        var creature = state.Creatures[0];
        var creatureFields = creature.GetType().GetProperties().Select(p => p.Name).ToArray();
        var raiderFields = typeof(PrototypeRaiderSnapshot)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        // A crew member carries one decision, and that decision is about what it
        // did — combat_attack is recorded on the striker after the blow lands, so
        // a wind-up drawn from it would follow the strike it precedes.
        Assert.Contains("LastDecision", creatureFields);
        Assert.IsType<int>(creature.LastDecision.Tick);

        // Nothing says a creature was hit and survived, and nothing at all says
        // a raider did anything: raiders have no decision to read.
        Assert.DoesNotContain(raiderFields, name => name.Contains("Decision", StringComparison.Ordinal));
        foreach (var name in new[] { "Windup", "Flinch", "Struck", "AttackPhase", "LastHitTick" })
        {
            Assert.DoesNotContain(name, creatureFields);
            Assert.DoesNotContain(name, raiderFields);
        }
    }
}
