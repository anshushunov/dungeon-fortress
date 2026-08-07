using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The pure half of Issue #295.
///
/// <para>
/// The check that actually pins map geometry is
/// <c>Main.VerifyWorldGeometry</c>: it records one <c>DrawMap</c> with every
/// number the adapter hands the engine and compares it with
/// <c>tests/golden/world/draw-calls.json</c>. It needs an engine, so it runs in
/// the <c>godot</c> stage and nothing here can execute it.
/// </para>
///
/// <para>
/// What can be checked without an engine is the machinery that makes the
/// recording complete and honest, and every claim below is one a green run of
/// the journal would otherwise be making silently:
/// </para>
///
/// <list type="bullet">
/// <item>a primitive the adapter draws with and does not hide is a mark the
/// journal never sees, and the reference file would stay green while it
/// moved;</item>
/// <item>a hidden primitive that does anything besides forward is a change to
/// the picture, which this Issue is a non-goal of;</item>
/// <item>a pass never opened is a pass whose marks belong to the previous one,
/// so the reference would name the wrong place;</item>
/// <item><c>base.</c> anywhere but inside the declaration that hides the same
/// name is exactly the escape
/// <c>WorldDrawPassGuardTests.No_covering_primitive_hides_behind_a_receiver</c>
/// was opened against, and Issue #295 taught the reader to skip that receiver.
/// </item>
/// </list>
/// </summary>
public sealed class WorldGeometryJournalGuardTests
{
    /// <summary>
    /// The engine primitives the adapter is expected to draw with. The list is
    /// stated rather than derived so that adding a primitive is a decision
    /// somebody writes down: a new one arrives here and in
    /// <c>Main.Rendering.cs</c> together, or
    /// <see cref="Every_engine_primitive_the_adapter_draws_with_is_journalled"/>
    /// fails.
    /// </summary>
    private static readonly string[] ExpectedPrimitives =
    [
        "DrawArc",
        "DrawCircle",
        "DrawLine",
        "DrawPolyline",
        "DrawRect",
        "DrawSetTransform",
        "DrawSetTransformMatrix",
        "DrawString",
        "DrawStringOutline",
        "DrawTextureRect",
    ];

    /// <summary>
    /// Completeness, and the one claim the whole journal rests on: every
    /// <c>Draw*</c> name the adapter calls is either a routine the manifest
    /// declares or a primitive the adapter hides. A primitive that is neither
    /// goes straight to the engine, draws a mark nothing records, and leaves the
    /// reference file green while the map changes.
    /// </summary>
    [Fact]
    public void Every_engine_primitive_the_adapter_draws_with_is_journalled()
    {
        var manifest = WorldDrawOrder.All
            .Select(routine => routine.Name)
            .Append(WorldDrawOrder.Entry)
            .ToHashSet(StringComparer.Ordinal);

        var primitives = AdapterSource
            .CalledMethods(WorldDrawOrder.RoutinePrefix)
            .Where(name => !manifest.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedPrimitives, primitives);
        Assert.Equal(
            ExpectedPrimitives,
            AdapterSource
                .HiddenEnginePrimitives(WorldDrawOrder.RoutinePrefix)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// A hidden primitive is a pass-through and nothing else. Each one has to
    /// forward to the very method it hides, exactly once, and no other body may
    /// reach past the adapter with <c>base.</c> — otherwise the reader's
    /// exemption for that receiver becomes the hole
    /// <c>No_covering_primitive_hides_behind_a_receiver</c> exists to close.
    /// </summary>
    [Fact]
    public void A_hidden_primitive_forwards_to_the_engine_and_nowhere_else()
    {
        foreach (var primitive in ExpectedPrimitives)
        {
            var forward = "base." + primitive + "(";
            Assert.Equal(1, Occurrences(AdapterSource.Masked, forward));
            Assert.Equal(1, Occurrences(AdapterSource.Body(primitive), forward));
            Assert.Equal(["base"], AdapterSource.ReceiversOfAny(primitive));
            Assert.Empty(AdapterSource.ReceiversOf(primitive));
        }

        Assert.Equal(ExpectedPrimitives.Length, Occurrences(AdapterSource.Masked, "base."));
    }

    /// <summary>
    /// A hidden primitive draws through the journal or through the engine, and
    /// decides which by one field. Recording it and drawing it in the same call
    /// would make a recording pass paint, which is impossible outside
    /// <c>_Draw</c> and is why the recording returns instead.
    /// </summary>
    [Fact]
    public void A_hidden_primitive_records_instead_of_drawing_while_the_journal_is_open()
    {
        foreach (var primitive in ExpectedPrimitives)
        {
            var body = AdapterSource.Body(primitive);
            Assert.Contains("_worldDrawJournal", body, StringComparison.Ordinal);
            Assert.True(
                body.IndexOf("return;", StringComparison.Ordinal) <
                body.IndexOf("base." + primitive, StringComparison.Ordinal),
                $"'{primitive}' forwards to the engine before it returns from the " +
                "recording branch, so a recording pass would try to paint.");
        }
    }

    /// <summary>
    /// The journal is only allowed to be switched on by the check that owns it,
    /// and only around one <c>DrawMap</c>. A second writer would be a second
    /// meaning for the same field, and the one place a frame could quietly stop
    /// drawing.
    /// </summary>
    [Fact]
    public void Only_the_geometry_check_opens_the_journal()
    {
        var assignments = Occurrences(AdapterSource.Masked, "_worldDrawJournal =");
        Assert.Equal(2, assignments);

        var check = AdapterSource.Body("VerifyWorldGeometry");
        Assert.Equal(2, Occurrences(check, "_worldDrawJournal ="));
        Assert.Contains("DrawMap();", check, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>DrawMap</c> opens all four declared passes, in the declared order, and
    /// each one before the first step that belongs to it. A pass opened late
    /// files its first marks under the previous pass, which would move the
    /// reference file without the drawing moving — the one failure mode that
    /// would teach a reader to distrust it.
    /// </summary>
    [Fact]
    public void DrawMap_opens_every_declared_pass_before_its_first_step()
    {
        var map = AdapterSource.Body(WorldDrawOrder.Entry);
        var passes = Enum.GetValues<WorldDrawPass>();

        var openings = passes
            .Select(pass => (Pass: pass, At: map.IndexOf(
                "BeginWorldDrawPass(WorldDrawPass." + pass + ")",
                StringComparison.Ordinal)))
            .ToArray();

        foreach (var (pass, at) in openings)
        {
            Assert.True(at >= 0, $"DrawMap never opens the {pass} pass.");
        }

        Assert.Equal(
            openings.Select(opening => opening.At).Order(),
            openings.Select(opening => opening.At));

        foreach (var step in WorldDrawOrder.Steps)
        {
            var pass = WorldDrawOrder.Find(step)!.Pass;
            var stepAt = map.IndexOf(step + "(", StringComparison.Ordinal);
            var passAt = openings.Single(opening => opening.Pass == pass).At;
            Assert.True(
                passAt < stepAt,
                $"'{step}' draws in the {pass} pass but is called before DrawMap " +
                "opens it, so the journal would file its marks under the pass before.");
        }
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
