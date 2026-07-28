namespace DungeonFortress.Presentation;

/// <summary>
/// Who a generated icon belongs to. The distinction exists because the icon pack
/// of Issue #54 is one deliverable and the interface is two: the toolbar moved to
/// icons in Issue #55, the HUD header did not.
/// </summary>
public enum UiIconConsumer
{
    /// <summary>Drawn on a button of one of the two control strips.</summary>
    Toolbar,

    /// <summary>
    /// Belongs to the resource header. The header is still text in Issue #55, so
    /// these two files are declared and deliberately unused rather than silently
    /// missing from the manifest — an icon nobody references is exactly the defect
    /// <see cref="UiIconManifest"/> exists to make visible.
    /// </summary>
    Header,
}

/// <param name="FileName">The file under <see cref="UiIconManifest.Directory"/>.</param>
/// <param name="OwnerId">
/// The control id that draws it, or the header readout it belongs to. One owner
/// per file and one file per owner: both directions are checked.
/// </param>
/// <param name="Consumer">Which part of the interface the file serves.</param>
public sealed record UiIcon(string FileName, string OwnerId, UiIconConsumer Consumer);

/// <summary>
/// The icon pack of <a href="https://github.com/anshushunov/dungeon-fortress/issues/54">Issue #54</a>,
/// as data rather than as a table in a document.
///
/// The manifest is what lets the two deliverables proceed in parallel: this
/// assembly names the files, the adapter loads them by name and draws a
/// placeholder for anything missing, and dropping the real PNGs in requires no
/// code change at all.
///
/// It also makes a class of defect visible that a diff cannot show: an icon that
/// was generated and never wired up, and a button that was wired up without an
/// icon. Neither shows as a changed line anywhere — one is a file nobody mentions,
/// the other is a button that quietly draws its placeholder forever. The manifest
/// integrity test asserts the bijection in both directions.
/// </summary>
public static class UiIconManifest
{
    /// <summary>
    /// Where the files live, relative to the Godot project root. Kept here so the
    /// path is stated once for the adapter, the tests and the documentation.
    /// </summary>
    public const string Directory = "assets/icons";

    /// <summary>The Godot resource path of a manifest file.</summary>
    public static string ResourcePath(string fileName) => $"res://{Directory}/{fileName}";

    /// <summary>
    /// Sixteen files, each owned by exactly one element. Fourteen are toolbar
    /// buttons; the two header readouts are recorded with their owner so that the
    /// day the header moves to icons, the entry is already there and the test
    /// starts holding it.
    /// </summary>
    public static IReadOnlyList<UiIcon> All { get; } =
    [
        new("icon_inspect.png", UiControlIds.Inspect, UiIconConsumer.Toolbar),
        new("icon_paint.png", UiControlIds.Paint, UiIconConsumer.Toolbar),
        new("icon_erase.png", UiControlIds.Erase, UiIconConsumer.Toolbar),
        new("icon_dig.png", UiControlIds.Dig, UiIconConsumer.Toolbar),
        new("icon_dig_cancel.png", UiControlIds.DigCancel, UiIconConsumer.Toolbar),
        new("icon_stockpile.png", UiControlIds.Stockpile, UiIconConsumer.Toolbar),
        new("icon_build.png", UiControlIds.Build, UiIconConsumer.Toolbar),
        new("icon_build_cancel.png", UiControlIds.BuildCancel, UiIconConsumer.Toolbar),
        new("icon_zone.png", UiControlIds.Zone, UiIconConsumer.Toolbar),
        new("icon_priority.png", UiControlIds.Priority, UiIconConsumer.Toolbar),
        new("icon_rule.png", UiControlIds.Rule, UiIconConsumer.Toolbar),
        new("icon_play.png", UiControlIds.Run, UiIconConsumer.Toolbar),
        new("icon_pause.png", UiControlIds.Pause, UiIconConsumer.Toolbar),
        new("icon_step.png", UiControlIds.Step, UiIconConsumer.Toolbar),
        new("icon_food.png", HeaderFood, UiIconConsumer.Header),
        new("icon_stone.png", HeaderStone, UiIconConsumer.Header),
    ];

    /// <summary>
    /// The two readouts of the resource line. They are owners without a button:
    /// Issue #55 moves the two control strips to icons and deliberately leaves the
    /// header as text, because the header band ends where the time strip begins and
    /// a row of icons does not fit it without moving the map.
    /// </summary>
    public const string HeaderFood = "header_food";

    /// <inheritdoc cref="HeaderFood"/>
    public const string HeaderStone = "header_stone";

    /// <summary>Every file a toolbar button is expected to draw.</summary>
    public static IReadOnlyList<UiIcon> Toolbar { get; } =
        All.Where(icon => icon.Consumer == UiIconConsumer.Toolbar).ToArray();

    /// <summary>
    /// Every file that is declared, owned by the header and therefore not drawn
    /// yet. Named rather than omitted: a reader of the manifest sees two pending
    /// files instead of a pack that silently disagrees with the icon Issue.
    /// </summary>
    public static IReadOnlyList<UiIcon> HeaderReserved { get; } =
        All.Where(icon => icon.Consumer == UiIconConsumer.Header).ToArray();

    /// <summary>The file a control draws, or <c>null</c> for a text-only control.</summary>
    public static string? FileFor(string controlId) =>
        All.FirstOrDefault(icon => icon.OwnerId == controlId)?.FileName;
}
