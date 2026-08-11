using System.Text.Json;

using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The three committed <c>--demo-stone</c> frames, rebuilt from the command log
/// they come from and compared against <c>tests/golden/ui/*.json</c> without an
/// engine.
///
/// <c>scripts/verify.ps1</c> already compares these frames, but only by starting
/// Godot, which the "Pure .NET" CI job does not do. Until this test existed the
/// golden UI state was checked on the owner's machine and nowhere else. What runs
/// here is exactly the code the adapter now calls, so a wording change fails in CI
/// on the pull request rather than at the next local verify.
///
/// The command log below mirrors <c>Main.ApplyDemoStone</c>, and the control
/// feedback line is the one that demo sets. If the demo changes, this test and the
/// golden files change together — which is the same coupling the golden files
/// already have.
/// </summary>
public sealed class GoldenUiTextTests
{
    private const int DemoStoneZoneTick = 200;

    private const string DemoControlFeedback =
        "Demo: DIG marked (25,1) (25,2) (25,3) (26,1); [M] paints the material " +
        "stockpile (22,1) (23,1) at tick 200. Nobody was ordered to carry anything.";

    public static TheoryData<string, int, string> Frames() => new()
    {
        { "stone-t190-loose-no-stockpile", 190, "25,3" },
        { "stone-t336-in-transit", 336, "25,1" },
        { "stone-t950-stockpile-full", 950, "23,1" },
    };

    [Theory]
    [MemberData(nameof(Frames))]
    public void The_golden_HUD_text_is_reproduced_without_Godot(
        string name,
        int tick,
        string selectCell)
    {
        var golden = ReadGolden(name);
        var view = RebuildFrame(tick, selectCell);
        var panels = HudText.Build(view);

        // The frame really is the one the golden file describes, so a text match
        // cannot be an accident of comparing the wrong tick.
        Assert.Equal(tick, view.Snapshot.Tick);
        Assert.Equal(golden.GetProperty("frame").GetProperty("tick").GetInt32(), view.Snapshot.Tick);
        AssertStone(golden.GetProperty("stone"), view.Snapshot);

        var ui = golden.GetProperty("ui");
        Assert.Equal(ui.GetProperty("summary").GetString(), panels.Summary);
        Assert.Equal(ui.GetProperty("inspector").GetString(), panels.Inspector);
        Assert.Equal(ui.GetProperty("feedback").GetString(), panels.Feedback);
        Assert.Equal(ui.GetProperty("roster").GetString(), panels.Roster);
        Assert.Equal(ui.GetProperty("controlFeedback").GetString(), view.ControlFeedback);

        var selected = ui.GetProperty("selectedCreatureId");
        Assert.Equal(
            selected.ValueKind == JsonValueKind.Null ? null : selected.GetInt32(),
            view.SelectedCreatureId);
        Assert.Equal(
            CommandLineArguments.ParseCell(selectCell),
            view.SelectedCell);
    }

    /// <summary>
    /// The frames must stay different from each other, otherwise three passing
    /// comparisons could be one comparison repeated.
    ///
    /// <para><b>The middle frame has stopped showing stone in transit, and that is
    /// named rather than absorbed.</b> The three moments the frame set exists for
    /// are «stone with nowhere to go», «stone in transit» and «a full stockpile».
    /// On this branch tick 336 holds four blocks already stored: the pathfinder of
    /// Issue #76 stopped the haulers walking into each other, the four-block chain
    /// finishes earlier, and by 336 nothing is on anybody's back. The moment still
    /// exists in the party — it is now somewhere between ticks 200 and 336 — but
    /// <b>which ticks the frames are taken at is not this slice's to choose</b>:
    /// they live in <c>scripts/HudVerification.ps1</c> and
    /// <c>docs/engineering/PROTOTYPE_GRAYBOX.md</c>, both foreign to it. So the
    /// second frame is compared for what it now is, the loss of the third stone
    /// state is stated, and moving the tick is left as a decision about the frame
    /// set rather than smuggled in as a change to a check.</para>
    ///
    /// <para>The assertions are the stories themselves and no longer the summary
    /// lines they used to be spelled as: a literal like <c>stone 0L 1C 3/4S</c> is
    /// a recording of a party, and this file already keeps three of those in
    /// <c>tests/golden/ui</c>.</para>
    /// </summary>
    [Fact]
    public void The_three_frames_tell_three_different_stories()
    {
        var views = Frames()
            .Select(row => RebuildFrame((int)row[1]!, (string)row[2]!))
            .ToArray();
        var inspectors = views.Select(HudText.Inspector).ToArray();

        Assert.Equal(3, inspectors.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("No material stockpile yet", inspectors[0], StringComparison.Ordinal);
        Assert.Contains("Full. Loose 0 waits", inspectors[2], StringComparison.Ordinal);

        // Frame one: dug, and nowhere to put it. The stockpile is not painted
        // until DemoStoneZoneTick, so the capacity is zero and every block is on
        // the floor.
        Assert.Equal(0, views[0].Snapshot.Stocks.StockpileCapacity);
        Assert.True(views[0].Snapshot.Stocks.LooseStone > 0);
        Assert.Equal(0, views[0].Snapshot.Stocks.StoredStone);

        // Frame three: the stockpile is full and nothing waits outside it.
        Assert.Equal(
            views[2].Snapshot.Stocks.StockpileCapacity,
            views[2].Snapshot.Stocks.StoredStone);
        Assert.Equal(0, views[2].Snapshot.Stocks.LooseStone);
        Assert.Equal(0, views[2].Snapshot.Stocks.CarriedStone);

        // Frame two: the chain has finished by this tick, so what separates it
        // from frame three is the party around it and not the stone. Asserted so
        // that the set cannot quietly become two frames and a copy of one.
        Assert.Equal(0, views[1].Snapshot.Stocks.CarriedStone);
        Assert.NotEqual(HudText.Summary(views[1]), HudText.Summary(views[2]));
    }

    private static HudViewState RebuildFrame(int tick, string selectCell)
    {
        var fixtureLog = PrototypeCommandDocument.Load(Path.Combine(
            PresentationFixtures.FindRepositoryRoot(),
            "scenarios",
            "prototype1",
            "baseline.commands.v2.json"));

        // The same commands the DIG and [M] brushes emit in Main.ApplyDemoStone:
        // one designation per stroked tile at the tick the demo runs, and the
        // stockpile scheduled for a later tick so the earlier frames legitimately
        // show stone with nowhere to go.
        PrototypeCommand[] playerCommands =
        [
            new DigDesignateCommand(1, [new GridPoint(25, 1)]),
            new DigDesignateCommand(1, [new GridPoint(25, 2)]),
            new DigDesignateCommand(1, [new GridPoint(25, 3)]),
            new DigDesignateCommand(1, [new GridPoint(26, 1)]),
            new ZonePaintCommand(
                DemoStoneZoneTick,
                ZoneKind.MaterialStockpile,
                [new GridPoint(22, 1), new GridPoint(23, 1)]),
        ];

        var log = new PrototypeCommandLog(
            fixtureLog.Scenario,
            fixtureLog.Seed,
            fixtureLog.Commands.Concat(playerCommands).OrderBy(command => command.Tick).ToArray());
        var world = new PrototypeWorld(log);
        world.RunTicks(tick);
        var state = world.GetSnapshot();

        var cell = CommandLineArguments.ParseCell(selectCell);
        return new HudViewState(
            state,
            "baseline",
            PrototypeScenario.Capture(world).Checksum,
            Paused: true,
            Speed: 1.0,
            SelectedCreatureId: state.Creatures
                .Where(creature => creature.Position == cell)
                .Select(creature => (int?)creature.Id)
                .FirstOrDefault(),
            SelectedCell: cell,
            ControlFeedback: DemoControlFeedback,
            PlayerCommands: playerCommands,
            DiagnosticCount: 0);
    }

    private static void AssertStone(JsonElement stone, PrototypeSnapshot state)
    {
        Assert.Equal(stone.GetProperty("stoneProduced").GetInt32(), state.Economy.StoneProduced);
        Assert.Equal(stone.GetProperty("looseStone").GetInt32(), state.Stocks.LooseStone);
        Assert.Equal(stone.GetProperty("carriedStone").GetInt32(), state.Stocks.CarriedStone);
        Assert.Equal(stone.GetProperty("storedStone").GetInt32(), state.Stocks.StoredStone);
        Assert.Equal(
            stone.GetProperty("stockpileCapacity").GetInt32(),
            state.Stocks.StockpileCapacity);
    }

    private static JsonElement ReadGolden(string name)
    {
        var path = Path.Combine(
            PresentationFixtures.FindRepositoryRoot(), "tests", "golden", "ui", $"{name}.json");
        Assert.True(File.Exists(path), $"Golden UI state '{name}' is missing at '{path}'.");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }
}
