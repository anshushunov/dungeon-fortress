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

        // The middle frame is read off the summary rather than the inspector.
        // Its cell used to hold the pile a carrier was still walking to; on the
        // dungeon of Issue #117 the walk is longer and by this tick the pile has
        // been lifted, so the tile is bare and the panel about it says nothing
        // about the haul. What "in transit" means is unchanged and is on the
        // summary line: one block on somebody's back, three already put away.
        Assert.Contains("stone 0L 1C 3/4S", HudText.Summary(views[1]), StringComparison.Ordinal);
        Assert.Contains("stone 4L 0C 0/0S", HudText.Summary(views[0]), StringComparison.Ordinal);
        Assert.Contains("stone 0L 0C 4/4S", HudText.Summary(views[2]), StringComparison.Ordinal);
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
