using DungeonFortress.Simulation;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Manifest integrity: every control that draws an icon has a file, and every
/// file has somebody who draws it.
///
/// This catches a class of defect a diff cannot show. An icon that was generated
/// and never wired up is a file nobody mentions — there is no changed line to
/// review. A button wired up without an icon is a button that quietly draws its
/// placeholder forever, and a placeholder looks like art that has not landed yet
/// rather than like a mistake. Both survive review indefinitely.
///
/// The two directions are checked separately because they fail for different
/// reasons and at different times: the first the moment a button is added, the
/// second the moment a pack is delivered.
/// </summary>
public sealed class UiIconManifestTests
{
    /// <summary>
    /// Every state the toolbar can be in that changes which ids exist. Only one
    /// button has two identities — run and pause — but the union is taken rather
    /// than that one case special-cased, so a future second one is covered too.
    /// </summary>
    private static IReadOnlyList<UiControl> AllControls() =>
    [
        .. UiControls.Build(ViewState(paused: true)),
        .. UiControls.Build(ViewState(paused: false)),
    ];

    private static UiControlsViewState ViewState(bool paused) => new(
        BrushMode.Inspect,
        ZoneKind.Farm,
        JobKind.Harvest,
        2,
        "ration_reserve",
        3,
        paused,
        1.0,
        "baseline",
        false);

    [Fact]
    public void The_manifest_is_the_sixteen_files_the_icon_issue_produces()
    {
        Assert.Equal(16, UiIconManifest.All.Count);
        Assert.Equal(14, UiIconManifest.Toolbar.Count);
        Assert.Equal(2, UiIconManifest.HeaderReserved.Count);

        var files = UiIconManifest.All.Select(icon => icon.FileName).ToArray();
        Assert.Equal(files.Length, files.Distinct(StringComparer.Ordinal).Count());
        Assert.All(files, file => Assert.StartsWith("icon_", file, StringComparison.Ordinal));
        Assert.All(files, file => Assert.EndsWith(".png", file, StringComparison.Ordinal));

        var owners = UiIconManifest.All.Select(icon => icon.OwnerId).ToArray();
        Assert.Equal(owners.Length, owners.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>First direction: no control draws an icon the manifest does not name.</summary>
    [Fact]
    public void Every_control_that_draws_an_icon_has_a_manifest_entry()
    {
        foreach (var control in AllControls().Where(control => control.Icon is not null))
        {
            var entry = UiIconManifest.All.SingleOrDefault(icon => icon.FileName == control.Icon);
            Assert.True(
                entry is not null,
                $"Control '{control.Id}' draws '{control.Icon}', which the manifest does not list.");
            Assert.Equal(control.Id, entry!.OwnerId);
            Assert.Equal(UiIconConsumer.Toolbar, entry.Consumer);
        }
    }

    /// <summary>
    /// Second direction: no toolbar file is left without somebody drawing it. This
    /// is the half that fails when an icon is generated for a button that was
    /// never added, or kept after its button was removed.
    /// </summary>
    [Fact]
    public void Every_toolbar_icon_is_drawn_by_exactly_one_control()
    {
        var drawn = AllControls()
            .Where(control => control.Icon is not null)
            .GroupBy(control => control.Icon!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(c => c.Id).Distinct().ToArray());

        foreach (var icon in UiIconManifest.Toolbar)
        {
            Assert.True(
                drawn.ContainsKey(icon.FileName),
                $"'{icon.FileName}' is in the manifest but no control draws it.");
            Assert.Single(drawn[icon.FileName]);
        }

        Assert.Equal(UiIconManifest.Toolbar.Count, drawn.Count);
    }

    /// <summary>
    /// The two files this step deliberately does not use. Issue #55 moves the two
    /// control strips to icons and leaves the resource header as text, so
    /// <c>icon_food</c> and <c>icon_stone</c> have an owner declared and no button.
    ///
    /// They are named here rather than omitted from the manifest, which is the
    /// difference between a deferred decision and a silent one: the moment the
    /// header moves to icons, this test is what says the entries are already
    /// there, and until then it says exactly two files are waiting.
    /// </summary>
    [Fact]
    public void The_two_header_icons_are_declared_and_deliberately_undrawn()
    {
        Assert.Equal(
            new[] { "icon_food.png", "icon_stone.png" },
            UiIconManifest.HeaderReserved.Select(icon => icon.FileName).OrderBy(
                name => name, StringComparer.Ordinal));

        var drawn = AllControls().Select(control => control.Icon).ToHashSet(StringComparer.Ordinal);
        foreach (var icon in UiIconManifest.HeaderReserved)
        {
            Assert.DoesNotContain(icon.FileName, drawn);
        }
    }

    /// <summary>
    /// Third direction, against the filesystem: a PNG that reached the assets
    /// folder without a manifest entry is an icon nobody will ever draw.
    /// </summary>
    [Fact]
    public void No_icon_file_exists_outside_the_manifest()
    {
        if (!Directory.Exists(IconDirectory()))
        {
            return;
        }

        var declared = UiIconManifest.All.Select(icon => icon.FileName).ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(IconDirectory(), "*.png"))
        {
            var name = Path.GetFileName(path);
            Assert.True(
                declared.Contains(name),
                $"'{name}' is in {UiIconManifest.Directory} but no manifest entry names it.");
        }
    }

    /// <summary>
    /// Fourth direction: the pack arrives whole.
    ///
    /// The files come from Issue #54, which runs in parallel, so an empty folder
    /// is the declared state of this branch rather than a failure — the adapter
    /// draws a placeholder and says so in its structured output. What must never
    /// happen is a folder holding *some* of them: a half-delivered pack is a
    /// toolbar where a few buttons are placeholders and nothing says which, which
    /// is precisely the silent state this file exists to prevent.
    /// </summary>
    [Fact]
    public void The_icon_pack_is_delivered_whole_or_not_at_all()
    {
        var directory = IconDirectory();
        var present = UiIconManifest.All
            .Where(icon => File.Exists(Path.Combine(directory, icon.FileName)))
            .Select(icon => icon.FileName)
            .ToArray();

        if (present.Length == 0)
        {
            return;
        }

        var missing = UiIconManifest.All
            .Select(icon => icon.FileName)
            .Except(present, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"{present.Length} of {UiIconManifest.All.Count} icons are present; " +
            $"missing: {string.Join(", ", missing)}. A partial pack leaves buttons on " +
            "placeholders with nothing saying which ones.");
    }

    private static string IconDirectory() => Path.Combine(
        PresentationFixtures.FindRepositoryRoot(),
        "src",
        "DungeonFortress.Game",
        UiIconManifest.Directory.Replace('/', Path.DirectorySeparatorChar));
}
