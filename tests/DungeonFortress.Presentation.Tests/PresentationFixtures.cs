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
    internal static readonly GridPoint[] Pocket =
    [
        new(25, 1), new(25, 2), new(25, 3), new(26, 1),
    ];

    internal static readonly GridPoint StockLeft = new(22, 1);
    internal static readonly GridPoint StockRight = new(23, 1);

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

    internal static PrototypeSnapshot Baseline(int ticks) => PrototypeScenario.Run(
        new PrototypeCommandLog("baseline", PrototypeTuning.DefaultSeed, []),
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
