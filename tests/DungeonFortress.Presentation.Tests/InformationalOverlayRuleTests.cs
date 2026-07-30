using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The rule itself, as a value: <b>a mark that can share a cell with a body must
/// not hide it.</b>
///
/// <see cref="WorldDrawPassGuardTests"/> checks that the adapter obeys the
/// declaration. This file checks the declaration is worth obeying — including the
/// two claims a manifest is normally trusted on and which are checked here
/// against a real session instead: that no body ever stands on rock, which is why
/// the dig mark is allowed to be opaque, and that bodies really do stand on the
/// cells the other marks explain, which is why they are not.
/// </summary>
public sealed class InformationalOverlayRuleTests
{
    /// <summary>
    /// The rule. Every mark that explains a cell a body can stand on must answer
    /// with translucency or with skipping — never "draw it as it is".
    /// </summary>
    [Fact]
    public void A_mark_that_can_share_a_cell_with_a_body_is_never_drawn_as_it_is()
    {
        var governed = InformationalOverlays.GovernedByTheRule().ToArray();
        Assert.NotEmpty(governed);

        foreach (var rule in governed)
        {
            Assert.True(
                rule.Policy != OverlayMarkPolicy.Opaque,
                $"{rule.Mark} explains a cell a body can stand on and is declared " +
                $"{rule.Policy}. The simulation puts a body on exactly the cell " +
                "such a mark describes, so an opaque one lands on the sprite it " +
                $"is explaining. Declared reason: {rule.Reason}");

            Assert.True(
                rule.Policy != OverlayMarkPolicy.TranslucentFill ||
                (rule.FillAlpha is > 0 and < 1 && rule.AccentAlpha is > 0 and < 1),
                $"{rule.Mark} is declared translucent but draws at fill " +
                $"{rule.FillAlpha} / accent {rule.AccentAlpha}. An alpha of 1 is " +
                "the defect this policy exists to prevent, and an alpha of 0 is " +
                "an invisible mark.");
        }
    }

    /// <summary>
    /// The two marks that are opaque on purpose, and why the rule does not reach
    /// them. Stated as an assertion rather than as prose, so relabelling a cell
    /// mark as a body or gesture readout to escape the rule is a visible edit to
    /// this list rather than a silent one.
    /// </summary>
    [Fact]
    public void Only_a_body_readout_and_the_gesture_readout_stay_opaque_over_a_body()
    {
        var exempt = InformationalOverlays.All
            .Where(rule => rule.Subject != OverlayMarkSubject.Cell)
            .Select(rule => rule.Mark)
            .OrderBy(mark => mark)
            .ToArray();

        Assert.Equal(new[] { OverlayMark.BodyState, OverlayMark.SelectionCount }, exempt);
        Assert.Equal(
            OverlayMarkSubject.Body,
            InformationalOverlays.For(OverlayMark.BodyState).Subject);
        Assert.Equal(
            OverlayMarkSubject.Gesture,
            InformationalOverlays.For(OverlayMark.SelectionCount).Subject);
    }

    /// <summary>
    /// The manifest and the enum are the same set. A value with no rule would get
    /// a policy from nowhere, which is the state Issue #90 was opened about.
    /// </summary>
    [Fact]
    public void Every_mark_has_exactly_one_rule()
    {
        var declared = InformationalOverlays.All.Select(rule => rule.Mark).ToArray();

        Assert.Equal(declared.Length, declared.Distinct().Count());
        Assert.Equal(
            Enum.GetValues<OverlayMark>().OrderBy(mark => mark),
            declared.OrderBy(mark => mark));
        Assert.All(
            InformationalOverlays.All,
            rule => Assert.False(string.IsNullOrWhiteSpace(rule.Reason)));
    }

    /// <summary>
    /// Every mark is drawn by at least one routine, and no routine claims a mark
    /// the manifest does not declare.
    /// </summary>
    [Fact]
    public void Every_mark_is_drawn_by_a_declared_routine()
    {
        foreach (var mark in Enum.GetValues<OverlayMark>())
        {
            var routines = WorldDrawOrder.RoutinesOf(mark).ToArray();
            Assert.True(routines.Length > 0, $"No routine draws {mark}.");
            Assert.All(
                routines,
                routine => Assert.True(
                    routine.Pass is WorldDrawPass.Informational or WorldDrawPass.Interaction,
                    $"'{routine.Name}' carries mark {mark} but draws in the " +
                    $"{routine.Pass} pass, where depth order already answers the " +
                    "question this policy exists for."));
        }

        Assert.All(
            WorldDrawOrder.All.Where(routine =>
                routine.Pass is WorldDrawPass.Informational or WorldDrawPass.Interaction),
            routine => Assert.NotNull(routine.Mark));
        Assert.All(
            WorldDrawOrder.All.Where(routine =>
                routine.Pass is WorldDrawPass.BelowDepth or WorldDrawPass.Depth),
            routine => Assert.Null(routine.Mark));
    }

    /// <summary>
    /// A mark nobody declared has no policy at all. This is the failure the
    /// adapter would otherwise inherit silently: a default of "opaque" would make
    /// the whole manifest optional.
    /// </summary>
    [Fact]
    public void An_undeclared_mark_has_no_policy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InformationalOverlays.For((OverlayMark)(-1)));
    }

    /// <summary>
    /// The third of the three answers the rule allows. No mark chooses it today,
    /// so the branch is covered here rather than left as an untested option that
    /// would have to be debugged the first time somebody picked it.
    /// </summary>
    [Theory]
    [InlineData(OverlayMarkPolicy.SkipWhenOccupied, true, false)]
    [InlineData(OverlayMarkPolicy.SkipWhenOccupied, false, true)]
    [InlineData(OverlayMarkPolicy.TranslucentFill, true, true)]
    [InlineData(OverlayMarkPolicy.StrokeOnly, true, true)]
    [InlineData(OverlayMarkPolicy.Opaque, true, true)]
    public void Only_a_skipping_mark_disappears_under_a_body(
        OverlayMarkPolicy policy,
        bool cellHoldsBody,
        bool expected)
    {
        Assert.Equal(expected, InformationalOverlays.IsDrawn(policy, cellHoldsBody));
    }

    /// <summary>
    /// And the per-mark answer is the per-policy answer. Nothing declares
    /// <see cref="OverlayMarkPolicy.SkipWhenOccupied"/> today, so every mark is
    /// drawn whether or not a body is standing there — which is worth pinning,
    /// because a mark quietly disappearing under a creature would read as a
    /// rendering bug rather than as a policy.
    /// </summary>
    [Fact]
    public void No_mark_currently_vanishes_under_a_body()
    {
        foreach (var rule in InformationalOverlays.All)
        {
            Assert.True(InformationalOverlays.IsDrawn(rule.Mark, cellHoldsBody: true));
            Assert.Equal(
                InformationalOverlays.IsDrawn(rule.Policy, true),
                InformationalOverlays.IsDrawn(rule.Mark, true));
        }
    }

    /// <summary>
    /// The one declaration that lets a cell mark stay opaque, checked against the
    /// world rather than believed. Rock is impassable, so a dig mark never has a
    /// sprite underneath — and if that ever stopped being true, the dig mark
    /// would be the fourth mark in a row to land on a creature.
    /// </summary>
    [Fact]
    public void No_body_ever_stands_on_rock_which_is_why_a_dig_mark_may_be_opaque()
    {
        Assert.False(InformationalOverlays.For(OverlayMark.DigDesignation).CellCanHoldBody);
        Assert.Equal(
            OverlayMarkPolicy.Opaque,
            InformationalOverlays.For(OverlayMark.DigDesignation).Policy);

        // A session long enough to hold both halves of the map: the excavation
        // pocket, and the raid waves that put a second faction of bodies on it.
        var world = new PrototypeWorld(PresentationFixtures.BuildChain());
        var checkedBodies = 0;
        var checkedRaiders = 0;
        for (var step = 0; step < 1_800; step++)
        {
            var state = world.GetSnapshot();
            var rock = state.Map.RockTiles.ToHashSet();
            Assert.NotEmpty(rock);
            foreach (var cell in BodyOccupancy.Of(state))
            {
                Assert.DoesNotContain(cell, rock);
                checkedBodies++;
            }

            checkedRaiders += state.Raiders.Count(raider => raider.Mode != RaiderMode.Escaped);
            world.Step();
        }

        Assert.True(checkedBodies > 1_000, $"only {checkedBodies} body positions were checked");
        Assert.True(checkedRaiders > 100, $"only {checkedRaiders} raider positions were checked");
    }

    /// <summary>
    /// The other direction, and the reason the rule is not theoretical: over a
    /// real session bodies do stand on the cells the translucent marks explain.
    /// <c>Build</c> holds the site cell for every one of its ticks, storing stone
    /// holds the stockpile cell, and <c>Drill</c> holds the post cell — which is
    /// where each of the three review rounds of Issue #83 found its defect.
    /// </summary>
    [Fact]
    public void Bodies_really_do_stand_on_the_cells_the_translucent_marks_explain()
    {
        var world = new PrototypeWorld(PresentationFixtures.BuildChain());
        var shared = new HashSet<GridPoint>();
        var explained = new HashSet<GridPoint>();
        var sharedFrames = 0;
        for (var step = 0; step < 1_400; step++)
        {
            var state = world.GetSnapshot();
            var bodies = BodyOccupancy.Of(state);
            foreach (var cell in Explained(state))
            {
                explained.Add(cell);
                if (bodies.Contains(cell))
                {
                    shared.Add(cell);
                    sharedFrames++;
                }
            }

            world.Step();
        }

        Assert.NotEmpty(explained);
        Assert.NotEmpty(shared);
        Assert.True(
            sharedFrames > 10,
            $"a body stood on an explained cell in only {sharedFrames} frames, " +
            "which would make the rule an edge case rather than the normal one");
        Assert.All(
            InformationalOverlays.All.Where(rule =>
                rule.Subject == OverlayMarkSubject.Cell &&
                rule.Policy == OverlayMarkPolicy.TranslucentFill),
            rule => Assert.True(rule.CellCanHoldBody));
    }

    private static IEnumerable<GridPoint> Explained(PrototypeSnapshot state)
    {
        foreach (var site in state.BuildSites)
        {
            yield return site.Tile;
        }

        foreach (var cell in state.StockpileCells)
        {
            yield return cell.Position;
        }

        foreach (var post in state.Map.BuiltPostTiles)
        {
            yield return post;
        }
    }
}
