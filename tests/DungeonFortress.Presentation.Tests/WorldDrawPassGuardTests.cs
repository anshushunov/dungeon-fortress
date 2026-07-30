using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The adapter is held to the manifest it consumes.
///
/// Issue #90 lists five mutations that "nothing catches", and all five are the
/// same shape: the drawing routine still compiles, still runs, still produces a
/// frame, and the frame is wrong in a way only an eye on a screenshot notices.
/// Four review rounds of Issue #83 found four of them that way. These tests are
/// the other end: they compare the declaration in
/// <see cref="WorldDrawOrder"/> and <see cref="InformationalOverlays"/> with what
/// <c>Main.DrawMap</c> actually does.
///
/// What each mutation runs into:
///
/// <list type="bullet">
/// <item><c>DrawHpBar</c> moved back inside the depth pass —
/// <see cref="A_routine_only_calls_routines_of_its_own_pass"/>;</item>
/// <item>the alpha taken off a mark above the depth pass —
/// <see cref="Every_translucent_mark_reads_its_fill_alpha_from_the_policy"/>;</item>
/// <item>a mark moved between passes —
/// <see cref="DrawMap_runs_the_declared_steps_in_the_declared_order"/>;</item>
/// <item>a new mark added to the pass with no policy —
/// <see cref="Every_drawing_routine_of_the_adapter_is_declared"/>.</item>
/// </list>
///
/// The fifth — overlay geometry drifting out of step with the drawn wall mass —
/// is not here: it was closed by construction when <c>WallVisualMass</c> became
/// the one source both sides read.
/// </summary>
public sealed class WorldDrawPassGuardTests
{
    private static IReadOnlyList<string> AdapterRoutines() =>
        AdapterSource
            .DeclaredMethods(WorldDrawOrder.RoutinePrefix)
            .Where(name => !string.Equals(name, WorldDrawOrder.Entry, StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// The reader has to be right about the file before anything it says about
    /// the file means something. A body that came back empty, or a call list that
    /// missed the engine primitives, would make every test below vacuously green.
    /// </summary>
    [Fact]
    public void The_source_reader_finds_the_adapter_and_its_bodies()
    {
        Assert.True(
            File.Exists(AdapterSource.FullPath()),
            $"{AdapterSource.RelativePath} is the file every check below reads.");

        var map = AdapterSource.Body(WorldDrawOrder.Entry);
        Assert.Contains("rockTiles", map, StringComparison.Ordinal);

        // Comments and literals are blanked, so a routine named in prose is not a
        // call and a brace inside a string cannot end a body. The adapter has no
        // preprocessor directive, so no `#` and no `//` may survive masking.
        Assert.DoesNotContain("//", AdapterSource.Masked, StringComparison.Ordinal);
        Assert.DoesNotContain("#", AdapterSource.Masked, StringComparison.Ordinal);

        // An expression-bodied method is a body too.
        Assert.Contains(
            "InformationalOverlays.FillAlpha",
            AdapterSource.Body("MarkFill"),
            StringComparison.Ordinal);
        Assert.Contains(
            "InformationalOverlays.AccentAlpha",
            AdapterSource.Body("MarkAccent"),
            StringComparison.Ordinal);

        // The engine's own primitives are reachable through the same reader, which
        // is what the fill checks below depend on.
        Assert.NotEmpty(AdapterSource.CallsTo(AdapterSource.Body("DrawZoneOutlines"), "DrawRect"));
    }

    /// <summary>
    /// Completeness, both directions. A drawing routine the adapter grew without
    /// a manifest entry has no declared policy, which is how the same defect kept
    /// coming back; a manifest entry with no routine is a rule about nothing.
    /// </summary>
    [Fact]
    public void Every_drawing_routine_of_the_adapter_is_declared()
    {
        var declared = WorldDrawOrder.All.Select(routine => routine.Name).ToArray();
        var actual = AdapterRoutines();

        Assert.Equal(
            declared.OrderBy(name => name, StringComparer.Ordinal),
            actual.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void DrawMap_runs_the_declared_steps_in_the_declared_order()
    {
        var routines = WorldDrawOrder.All.Select(routine => routine.Name).ToArray();

        var called = AdapterSource.CalledRoutines(
            AdapterSource.Body(WorldDrawOrder.Entry),
            routines);

        Assert.Equal(WorldDrawOrder.Steps, called);
    }

    /// <summary>
    /// The passes run in the declared order and nothing skips backwards, so
    /// "above the depth pass" is a property of the list rather than a habit.
    /// </summary>
    [Fact]
    public void The_declared_steps_never_move_backwards_through_the_passes()
    {
        var passes = WorldDrawOrder.Steps
            .Select(step => WorldDrawOrder.Find(step)!.Pass)
            .ToArray();

        Assert.Equal(passes.OrderBy(pass => pass), passes);
        Assert.Equal(
            new[]
            {
                WorldDrawPass.BelowDepth,
                WorldDrawPass.Depth,
                WorldDrawPass.Informational,
                WorldDrawPass.Interaction,
            },
            passes.Distinct());
    }

    /// <summary>
    /// Every declared routine is reachable from <c>DrawMap</c>. Without this the
    /// manifest could keep declaring a routine that nothing draws any more, and a
    /// mark could be quietly retired while its rule stayed green.
    /// </summary>
    [Fact]
    public void Every_declared_routine_is_reachable_from_DrawMap()
    {
        Assert.Equal(
            WorldDrawOrder.All.Select(routine => routine.Name).OrderBy(n => n, StringComparer.Ordinal),
            Reachable().OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// The pass check, and the reason it is stated as "same pass" rather than as a
    /// list of forbidden pairs: the first review round of Issue #83 found an HP
    /// bar completely hidden by a raised wall top, and the fix was to move the
    /// body's readout out of the depth pass. Calling it from <c>DrawCreature</c>
    /// again puts it back, without changing a single line of the manifest.
    /// </summary>
    [Fact]
    public void A_routine_only_calls_routines_of_its_own_pass()
    {
        var names = WorldDrawOrder.All.Select(routine => routine.Name).ToArray();
        foreach (var caller in WorldDrawOrder.All)
        {
            var body = AdapterSource.Body(caller.Name);
            foreach (var calleeName in AdapterSource.CalledRoutines(body, names))
            {
                var callee = WorldDrawOrder.Find(calleeName)!;
                Assert.True(
                    callee.Pass == caller.Pass,
                    $"'{caller.Name}' draws in the {caller.Pass} pass and calls " +
                    $"'{calleeName}', which is declared as {callee.Pass}. A mark " +
                    "drawn from the wrong pass is exactly the defect this " +
                    "manifest exists to catch.");
            }
        }
    }

    /// <summary>
    /// The alpha check. A translucent mark's fills have to come from the policy,
    /// not from a literal next to them: the literal is what the adapter had
    /// before, and a literal is invisible to every check in the repository.
    /// </summary>
    [Fact]
    public void Every_translucent_mark_reads_its_fill_alpha_from_the_policy()
    {
        foreach (var mark in MarksWithPolicy(OverlayMarkPolicy.TranslucentFill))
        {
            var fills = 0;
            foreach (var routine in WorldDrawOrder.RoutinesOf(mark))
            {
                foreach (var (fill, colorArgument) in FilledDraws(AdapterSource.Body(routine.Name)))
                {
                    fills++;
                    var color = fill.Arguments[colorArgument];
                    Assert.True(
                        color.Contains("MarkFill(", StringComparison.Ordinal) ||
                        color.Contains("MarkAccent(", StringComparison.Ordinal),
                        $"'{routine.Name}' fills with `{color}`. Mark {mark} is " +
                        $"declared {OverlayMarkPolicy.TranslucentFill}, so every " +
                        "fill has to take its alpha from InformationalOverlays " +
                        $"through MarkFill or MarkAccent. The call was: {fill.Text}");
                }
            }

            Assert.True(
                fills > 0,
                $"{mark} is declared {OverlayMarkPolicy.TranslucentFill} but none " +
                "of its routines draws a fill. Either it stopped being a fill or " +
                "the declaration is stale, and a stale declaration is a rule " +
                "about nothing.");
        }
    }

    /// <summary>
    /// The other half of the same claim: a mark declared as strokes only must not
    /// grow a fill. An outline may be opaque precisely because it covers nothing,
    /// and that stops being true the moment somebody fills it in.
    /// </summary>
    [Fact]
    public void A_stroke_only_mark_draws_no_fill_at_all()
    {
        foreach (var routine in RoutinesWithPolicy(OverlayMarkPolicy.StrokeOnly))
        {
            var fills = FilledDraws(AdapterSource.Body(routine.Name)).ToArray();
            Assert.True(
                fills.Length == 0,
                $"'{routine.Name}' draws {fills.Length} fill(s) while mark " +
                $"{routine.Mark} is declared {OverlayMarkPolicy.StrokeOnly}: " +
                $"{string.Join(" | ", fills.Select(fill => fill.Call.Text))}");
        }
    }

    /// <summary>
    /// Issue #99: the frame a drag stretches is built from the same function the
    /// hover highlight uses. The pure proof is
    /// <see cref="SelectionGeometryTests"/>; this is the half that says the
    /// adapter asks it rather than measuring the grid itself, which is what the
    /// frame used to do.
    /// </summary>
    [Fact]
    public void The_selection_frame_is_drawn_from_the_shared_geometry()
    {
        var preview = AdapterSource.Body("DrawBrushPreview");

        Assert.Contains("SelectionGeometry.Outline", preview, StringComparison.Ordinal);
        Assert.Contains("CellInteractionRect(", preview, StringComparison.Ordinal);

        // Grid arithmetic in this routine is how the two geometries came apart in
        // the first place: the frame measured the grid while every cell in it
        // measured visible mass.
        Assert.DoesNotContain("CellTopLeft(", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("_tileSize *", preview, StringComparison.Ordinal);

        Assert.Contains(
            "SelectionGeometry.CellInteractionRect",
            AdapterSource.Body("CellInteractionRect"),
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectionGeometry.CaptionBox",
            AdapterSource.Body("DrawSelectionCount"),
            StringComparison.Ordinal);
    }

    private static IEnumerable<OverlayMark> MarksWithPolicy(OverlayMarkPolicy policy) =>
        InformationalOverlays.All
            .Where(rule => rule.Policy == policy)
            .Select(rule => rule.Mark);

    private static IEnumerable<WorldDrawRoutine> RoutinesWithPolicy(OverlayMarkPolicy policy) =>
        MarksWithPolicy(policy).SelectMany(WorldDrawOrder.RoutinesOf);

    /// <summary>
    /// The two canvas primitives that can cover a sprite, and where each keeps its
    /// colour and its "filled" flag. <c>DrawRect(rect, colour, filled, width)</c>
    /// and <c>DrawCircle(centre, radius, colour, filled, width)</c> both fill
    /// unless that flag is explicitly <c>false</c>, so anything else counts as a
    /// fill. A line, an arc, a string and a texture are strokes or glyphs and are
    /// not asked about.
    /// </summary>
    private static readonly (string Name, int Color, int Filled)[] CoveringPrimitives =
    [
        ("DrawRect", 1, 2),
        ("DrawCircle", 2, 3),
    ];

    private static IEnumerable<(SourceCall Call, int ColorArgument)> FilledDraws(string body)
    {
        foreach (var (name, color, filled) in CoveringPrimitives)
        {
            foreach (var call in AdapterSource.CallsTo(body, name))
            {
                var isOutline = call.Arguments.Count > filled &&
                    string.Equals(call.Arguments[filled], "false", StringComparison.Ordinal);
                if (!isOutline && call.Arguments.Count > color)
                {
                    yield return (call, color);
                }
            }
        }
    }

    private static IReadOnlyCollection<string> Reachable()
    {
        var names = WorldDrawOrder.All.Select(routine => routine.Name).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(
            AdapterSource.CalledRoutines(AdapterSource.Body(WorldDrawOrder.Entry), names));
        while (queue.Count > 0)
        {
            var routine = queue.Dequeue();
            if (!seen.Add(routine))
            {
                continue;
            }

            foreach (var callee in AdapterSource.CalledRoutines(
                         AdapterSource.Body(routine),
                         names))
            {
                queue.Enqueue(callee);
            }
        }

        return seen;
    }
}
