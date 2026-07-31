using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Snapshots for the tests. Two ways of getting one, used for different jobs:
///
/// <list type="bullet">
/// <item>run the real simulation, for anything that must stay honest about what
/// the world actually produces;</item>
/// <item>take a real snapshot and override one list with <c>with</c>, for branch
/// coverage of an explanation whose <c>statusCode</c> a command log cannot be
/// steered to on demand. The explanation is a pure function of the snapshot, so
/// stating the snapshot is stating the input, not faking the result.</item>
/// </list>
/// </summary>
internal static class PresentationFixtures
{
    /// <summary>
    /// The snapshot as the map shows it. The brush and the inspector deliberately
    /// take a <see cref="MapProjection"/> and not a snapshot — building one per
    /// call would cost a projection per map cell per frame — so a test that only
    /// cares about canonical state says so here in one word.
    /// </summary>
    internal static MapProjection Shown(this PrototypeSnapshot state) => MapProjection.Of(state);

    /// <summary>
    /// The quarry at the back of the dungeon, where the shipped demo journals
    /// dig. Everything that needs to <em>watch</em> the chain uses it, because a
    /// long walk is what makes "on the way to the pile", "carrying to a cell" and
    /// "the stone is promised elsewhere" last longer than one tick.
    /// </summary>
    internal static readonly GridPoint[] Pocket =
    [
        new(25, 1), new(25, 2), new(25, 3), new(26, 1),
    ];

    internal static readonly GridPoint StockLeft = new(22, 1);
    internal static readonly GridPoint StockRight = new(23, 1);

    /// <summary>The blueprint of the functional-room chain, on excavated ground.</summary>
    internal static readonly GridPoint Site = new(25, 2);

    /// <summary>
    /// The wall between the hearth and the spine, east of the larder's stair, and
    /// a stockpile two tiles from it. Every tile of it is rock at tick 0, so
    /// digging it still creates ground that did not exist.
    ///
    /// It exists because of the dungeon of Issue #117. Anything that needs the
    /// chain to actually <em>finish</em> — a post standing, a cell holding part
    /// of a load — has to run here: at the far quarry the stone loses every
    /// scoring comparison to a food haul fifteen tiles nearer, so the post is
    /// never raised and the explanation under test has no subject.
    /// </summary>
    internal static readonly GridPoint[] NearWall =
    [
        new(17, 9), new(18, 9), new(19, 9), new(20, 9),
    ];

    internal static readonly GridPoint NearStockLeft = new(16, 8);
    internal static readonly GridPoint NearStockRight = new(17, 8);
    internal static readonly GridPoint NearSite = new(18, 9);

    /// <summary>The same chain as <see cref="BuildChain"/>, run next to the hearth.</summary>
    internal static PrototypeCommandLog NearBuildChain() => Log(
        new DigDesignateCommand(0, NearWall),
        new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [NearStockLeft, NearStockRight]),
        new BuildDesignateCommand(BlueprintTick, [NearSite]),
        new ZonePaintCommand(BlueprintTick, ZoneKind.TrainingGround, [NearSite]),
        new SetPriorityCommand(BlueprintTick, JobKind.Drill, 3));

    internal const int BlueprintTick = 1_000;

    /// <summary>A session that digs a pocket and stores the stone.</summary>
    internal static PrototypeSnapshot FullChain(int ticks) => PrototypeScenario.Run(
        Log(
            new DigDesignateCommand(0, Pocket),
            new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight])),
        ticks).State;

    /// <summary>A session that digs but has nowhere to put the stone.</summary>
    internal static PrototypeSnapshot DigOnly(int ticks) => PrototypeScenario.Run(
        Log(new DigDesignateCommand(0, Pocket)),
        ticks).State;

    /// <summary>
    /// The whole Issue #48 chain: dig, store, mark a blueprint on the excavated
    /// ground, deliver the stone back out of the stockpile, build, and zone the
    /// post as a training ground.
    /// </summary>
    internal static PrototypeCommandLog BuildChain() => Log(
        new DigDesignateCommand(0, Pocket),
        new ZonePaintCommand(0, ZoneKind.MaterialStockpile, [StockLeft, StockRight]),
        new BuildDesignateCommand(BlueprintTick, [Site]),
        new ZonePaintCommand(BlueprintTick, ZoneKind.TrainingGround, [Site]),
        new SetPriorityCommand(BlueprintTick, JobKind.Drill, 3));

    internal static PrototypeSnapshot BuildChainAt(int ticks) =>
        PrototypeScenario.Run(BuildChain(), ticks).State;

    /// <summary>
    /// A session far enough along that the post actually stands. It runs the
    /// near chain: see <see cref="NearWall"/> for why the far quarry cannot
    /// raise one.
    /// </summary>
    internal static PrototypeSnapshot BuiltPost(int ticks)
    {
        var state = PrototypeScenario.Run(NearBuildChain(), ticks).State;
        if (state.Map.BuiltPostTiles.Count == 0)
        {
            throw new InvalidOperationException(
                $"The build chain has no post at tick {ticks}.");
        }

        return state;
    }

    internal static PrototypeBuildSiteSnapshot BuildSite(
        GridPoint tile,
        int delivered = 0,
        string statusCode = "build_waiting_carrier") =>
        new(
            tile,
            delivered,
            PrototypeTuning.BuildStoneCost,
            0,
            null,
            null,
            0,
            PrototypeTuning.BuildTicks,
            true,
            statusCode);

    internal static PrototypeSnapshot Baseline(int ticks) => PrototypeScenario.Run(
        new PrototypeCommandLog("baseline", PrototypeTuning.DefaultSeed, []),
        ticks).State;

    /// <summary>
    /// A shipped scenario, run from its own command log rather than from a log
    /// written here. Anything that claims to be about what the game produces has
    /// to read the same journals the game ships with.
    /// </summary>
    internal static PrototypeSnapshot RunFixture(string fixtureName, int ticks) =>
        PrototypeScenario.Run(
            PrototypeCommandDocument.Load(Path.Combine(
                FindRepositoryRoot(), "scenarios", "prototype1", $"{fixtureName}.commands.v2.json")),
            ticks).State;

    internal static PrototypeCommandLog Log(params PrototypeCommand[] commands) =>
        new("custom", PrototypeTuning.DefaultSeed, commands);

    internal static PrototypeStockpileCellSnapshot Cell(
        string statusCode,
        int stored = 0,
        int incomingReserved = 0,
        int capacity = 2,
        bool reachable = true) =>
        new(StockRight, stored, capacity, incomingReserved, reachable, statusCode);

    internal static PrototypeDigDesignationSnapshot Designation(
        string statusCode,
        GridPoint tile,
        int? reservedBy = null,
        GridPoint? workTile = null,
        int progressTicks = 0,
        int requiredTicks = 0) =>
        new(tile, null, reservedBy, workTile, progressTicks, requiredTicks, true, statusCode);

    internal static PrototypeJobSnapshot StoneHaul(
        GridPoint origin,
        int? reservedBy,
        GridPoint? storeCell,
        int storeReserved = 1,
        bool pickedUp = false) =>
        new(
            JobId: 1,
            Key: "haul:stone",
            Kind: JobKind.Haul,
            Origin: origin,
            Target: storeCell ?? origin,
            Resource: ResourceKind.Stone,
            Quantity: 1,
            PersonalCreatureId: null,
            ReservedBy: reservedBy,
            RemainingTicks: 0,
            ProgressTicks: 0,
            PickedUp: pickedUp,
            StoreCell: storeCell,
            StoreReserved: storeReserved);

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DungeonFortress.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
