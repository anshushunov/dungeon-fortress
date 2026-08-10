using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The caption over the raider the domain has already met (Issue #358).
///
/// Every claim here is about text and about who gets it. Where the caption sits
/// and what colour it is are constants the adapter multiplies, and the pass it is
/// drawn in is held by <c>WorldDrawPassGuardTests</c> and
/// <c>InformationalOverlayRuleTests</c>.
/// </summary>
public sealed class ReturningHeroLabelTests
{
    private static PrototypeRaiderSnapshot Raider(
        int id = 1,
        int wave = 3,
        string name = "Крюк Немой",
        RaiderMode mode = RaiderMode.Raiding,
        int? returnedFromWave = 1,
        InjuryKind scar = InjuryKind.Heavy,
        PrototypeRememberedPlace? remembered = null) =>
        new(
            id,
            wave,
            30,
            4,
            new GridPoint(20, 7),
            0,
            0,
            false,
            mode,
            name,
            returnedFromWave,
            scar,
            remembered);

    [Fact]
    public void A_stranger_gets_no_caption()
    {
        var stranger = Raider(returnedFromWave: null, scar: InjuryKind.None);

        Assert.False(ReturningHeroLabel.IsCaptioned(stranger));
        Assert.Empty(ReturningHeroLabel.Lines(stranger));
    }

    /// <summary>
    /// The half of the decision that is easy to lose: every raider has a name in
    /// the snapshot, and only the returning one is named on screen. The panel of a
    /// single creature is already unreadable (Issue #355), and twenty captions a
    /// party would make it worse in the same place.
    /// </summary>
    [Fact]
    public void Only_the_returning_raider_is_named_on_screen()
    {
        var state = PrototypeScenario.Run(
            PrototypeCommandDocument.Load(Path.Combine(
                PresentationFixtures.FindRepositoryRoot(),
                "scenarios",
                "prototype1",
                "baseline.commands.v2.json")),
            PrototypeTuning.SessionTicks).State;

        Assert.All(state.Raiders, raider => Assert.NotEqual(string.Empty, raider.Name));
        Assert.Contains(state.Raiders, raider => raider.ReturnedFromWave is not null);
        // Asked of the layout that actually draws them (Issue #364) and with
        // nothing hovered or selected, which is the frame the rule is about: a
        // caption nobody asked for belongs only to a raider who has been here.
        Assert.All(
            WorldLabels
                .Requests(state, WorldLabelFocus.None, CameraView.DefaultTileSize)
                .Where(request => request.Subject.Kind == WorldLabelKind.Raider),
            request => Assert.NotNull(
                state.Raiders.Single(raider => raider.Id == request.Subject.Id)
                    .ReturnedFromWave));
    }

    [Theory]
    [InlineData(RaiderMode.Downed)]
    [InlineData(RaiderMode.Escaped)]
    [InlineData(RaiderMode.Queued)]
    public void A_raider_that_is_not_raiding_gets_no_caption(RaiderMode mode) =>
        Assert.False(ReturningHeroLabel.IsCaptioned(Raider(mode: mode)));

    [Fact]
    public void The_caption_is_the_name_and_one_line_about_the_last_time()
    {
        var raider = Raider(
            remembered: new PrototypeRememberedPlace(new GridPoint(24, 7), 1327, "wound"));

        Assert.Equal(
            new[] { "Крюк Немой", "волна 1 · едва не добили (24,7)" },
            ReturningHeroLabel.Lines(raider));
    }

    [Fact]
    public void A_light_scar_and_a_heavy_one_read_differently()
    {
        var place = new PrototypeRememberedPlace(new GridPoint(23, 7), 1661, "wound");

        Assert.Equal(
            "волна 1 · достали (23,7)",
            ReturningHeroLabel.Story(Raider(scar: InjuryKind.Light, remembered: place)));
        Assert.Equal(
            "волна 1 · едва не добили (23,7)",
            ReturningHeroLabel.Story(Raider(scar: InjuryKind.Heavy, remembered: place)));
    }

    /// <summary>
    /// A raider nobody reached last time carries no scar and no place, and the
    /// caption says so rather than inventing a wound for him. Scar and memory are
    /// one decision in the simulation, so a caption that guessed here would be the
    /// screen disagreeing with the canonical document.
    /// </summary>
    [Fact]
    public void A_raider_nobody_touched_last_time_is_named_and_nothing_more()
    {
        var untouched = Raider(scar: InjuryKind.None, remembered: null);

        Assert.True(ReturningHeroLabel.IsCaptioned(untouched));
        Assert.Null(ReturningHeroLabel.Story(untouched));
        Assert.Equal(new[] { "Крюк Немой" }, ReturningHeroLabel.Lines(untouched));
    }

    /// <summary>
    /// Raiders bunch — they all walk to the same larder tile — so two captions
    /// over neighbouring bodies would be printed on top of each other. Since Issue
    /// #364 <b>where</b> they go instead is decided by <c>WorldLabelLayout</c>, in
    /// one pass over every label of the frame, and is checked by
    /// <c>WorldLabelLayoutTests</c>. What stays this file's business is that the
    /// caption is only ever asked for on behalf of a raider who has been here
    /// before, which is the check above.
    ///
    /// <para>The band rule this replaces — «каждому следующему строка вверх» — is
    /// not merely superseded; it is named in
    /// <c>docs/product/REFERENCES.md</c> as the source of the defect the owner
    /// reported, because it resolved the overlap by giving up the attachment.</para>
    /// </summary>
    [Fact]
    public void The_caption_reads_as_two_sizes_with_a_rim_under_both()
    {
        // The story line is smaller than the name, because the name is the half
        // meant to be recognised at a glance.
        Assert.True(ReturningHeroLabel.StoryTextRef < ReturningHeroLabel.NameTextRef);
        // A rim rather than a plate: the mark is declared StrokeOnly, so the only
        // thing standing between the text and a goblin is an outline.
        Assert.True(ReturningHeroLabel.OutlineRef > 0);
    }

    /// <summary>
    /// The mark is declared as a body's own readout with no fill, and the whole of
    /// the "must not hide a body" rule for it is that it never fills. Asserting the
    /// declaration here is what keeps a plate from being added under the text later
    /// without the rule being re-answered.
    /// </summary>
    [Fact]
    public void The_caption_is_declared_as_a_body_readout_that_never_fills()
    {
        var rule = InformationalOverlays.For(OverlayMark.ReturningHero);

        Assert.Equal(OverlayMarkSubject.Body, rule.Subject);
        Assert.Equal(OverlayMarkPolicy.StrokeOnly, rule.Policy);
        Assert.Null(rule.CellCanHoldBody);
        Assert.True(rule.Reason.Length > 80);
    }
}
