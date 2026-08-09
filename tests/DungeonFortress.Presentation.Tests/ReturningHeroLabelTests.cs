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
        Assert.All(
            ReturningHeroLabel.Layout(state),
            caption => Assert.NotNull(caption.Raider.ReturnedFromWave));
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
    /// Raiders bunch — they all walk to the same larder tile — so two captions at
    /// one height over neighbouring bodies are printed on top of each other. A
    /// caption that would collide takes the next band up.
    /// </summary>
    [Fact]
    public void Captions_that_would_collide_are_laid_in_bands_above_one_another()
    {
        var state = PresentationFixtures.Baseline(PrototypeTuning.FirstRaidTick + 5);
        var shoulderToShoulder = state with
        {
            Raiders =
            [
                Raider(id: 1, remembered: null) with { Position = new GridPoint(20, 7) },
                Raider(id: 2, name: "Секира", remembered: null) with { Position = new GridPoint(21, 7) },
                Raider(id: 3, name: "Гвоздь", remembered: null) with { Position = new GridPoint(22, 8) },
                // Far enough away that nothing above reaches it, so it is back in
                // the band directly over its own head.
                Raider(id: 4, name: "Клык", remembered: null) with { Position = new GridPoint(4, 7) },
            ],
        };

        var layout = ReturningHeroLabel.Layout(shoulderToShoulder);

        Assert.Equal(
            new[] { ("Клык", 0), ("Крюк Немой", 0), ("Секира", 1), ("Гвоздь", 2) },
            layout.Select(caption => (caption.Raider.Name, caption.Slot)));

        // A higher band is further up the screen, and the two lines of a caption in
        // one band never reach into the band above it.
        Assert.True(ReturningHeroLabel.TopRefOf(1) < ReturningHeroLabel.TopRefOf(0));
        Assert.True(ReturningHeroLabel.SlotHeightRef > ReturningHeroLabel.LineHeightRef * 2);
    }

    [Fact]
    public void The_caption_sits_above_the_body_where_the_hp_bar_is_not()
    {
        // Negative is up in screen space, and the HP bar of a creature is drawn at
        // +8 reference pixels from the centre (Main.DrawCreatureInformation).
        Assert.True(ReturningHeroLabel.LabelTopRef < 0);
        Assert.True(ReturningHeroLabel.LabelTopRef + ReturningHeroLabel.LineHeightRef < 0);
        Assert.True(ReturningHeroLabel.LineHeightRef > 0);
        Assert.True(ReturningHeroLabel.StoryTextRef < ReturningHeroLabel.NameTextRef);
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
