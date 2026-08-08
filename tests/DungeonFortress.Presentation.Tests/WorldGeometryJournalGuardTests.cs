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
    /// The two arguments the journal is allowed not to record, and the reason
    /// each is exempt. Anything else a primitive is handed has to reach the
    /// record.
    /// </summary>
    private static readonly string[] ArgumentsDeliberatelyNotRecorded =
    [
        // The glyphs themselves. What a caption says is golden UI's business
        // (tests/golden/ui), held by a different Issue, and the same string in
        // two reference files is two things to regenerate for one change.
        "text",

        // One shared ThemeDB.FallbackFont instance with no stable identity to
        // write down. What is geometry about a font — the size — is a separate
        // argument and is recorded.
        "font",
    ];

    /// <summary>
    /// The claim the whole record depends on, as a check rather than as a
    /// sentence: a hidden primitive hands the journal every argument it was
    /// given.
    ///
    /// <para>
    /// This test exists because the first version of the journal did not, and
    /// its absence cost a review round. <c>alignment</c>, the wrap width, the
    /// outline size and <c>transpose</c> were dropped while the docstring said
    /// "every argument", and review proved the claim empty with the obvious
    /// mutant: <c>HorizontalAlignment.Left</c> to <c>Center</c> in
    /// <c>DrawSelectionCount</c> slides the caption across a 52 px box without
    /// moving the position argument at all, and the record said ok.
    /// </para>
    ///
    /// <para>
    /// An argument that reaches the primitive and not the record is a way for a
    /// mark to move invisibly, which is the exact defect this Issue was opened
    /// about. Two exemptions are allowed and both are named above.
    /// </para>
    /// </summary>
    [Fact]
    public void A_hidden_primitive_hands_the_journal_every_argument_it_was_given()
    {
        foreach (var primitive in ExpectedPrimitives)
        {
            var recorded = Identifiers(JournalCall(AdapterSource.Body(primitive)));
            foreach (var parameter in AdapterSource.ParameterNames(primitive))
            {
                if (ArgumentsDeliberatelyNotRecorded.Contains(parameter, StringComparer.Ordinal))
                {
                    continue;
                }

                Assert.True(
                    recorded.Contains(parameter),
                    $"'{primitive}' is given '{parameter}' and does not hand it to the " +
                    "journal, so a change to it moves the map and not the record. " +
                    "Either record it, or add it to ArgumentsDeliberatelyNotRecorded " +
                    "with the reason.");
            }
        }
    }

    /// <summary>
    /// The second hop, and the half the first-version guard could not see.
    ///
    /// <para>
    /// The check above demands that a hidden primitive hand the journal every
    /// argument it was given; it is silent about what the journal does with
    /// them next. <c>WorldDrawJournal.Text</c> could keep <c>alignment</c> in
    /// its signature and drop it from the call it records, and the record would
    /// stay green while the caption slid across its box — the review mutant of
    /// PR #326 did exactly that and seven tests held.
    /// </para>
    ///
    /// <para>
    /// This is the other end: every parameter of a journal method that records
    /// text has to reach the text that is recorded. The body's
    /// <c>pass.Point</c> and <c>pass.Size</c> statements do not count — the
    /// extent and the sizes are a readable summary beside the record, not the
    /// record — and a parameter that survives only there is the same defect
    /// wearing a second costume. The <c>alignment</c> of a caption is neither a
    /// point nor a size, so its only path into the record is the text itself.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_argument_the_journal_is_given_reaches_the_recorded_text()
    {
        var recording = AdapterSource.JournalRecordingMethods();
        Assert.Equal(
            new[]
            {
                "Arc", "Circle", "Line", "Polyline", "Rect", "Text",
                "TextureRect", "Transform", "TransformMatrix",
            },
            recording
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal));

        foreach (var method in recording)
        {
            var recorded = Identifiers(RecordedTextRegion(method.Body));
            foreach (var parameter in method.Parameters)
            {
                Assert.True(
                    recorded.Contains(parameter),
                    $"'{method.Name}' is given '{parameter}' and its value never reaches " +
                    "the text that is recorded, so a change to it moves the map and not " +
                    "the record. The parameter has to appear where the record is written, " +
                    "not only in the signature or in the extent and sizes beside it.");
            }
        }
    }

    /// <summary>
    /// The reference is about geometry, so nothing that is not geometry may sit
    /// inside the text that is compared.
    ///
    /// <para>
    /// The canonical checksum of the simulation used to. Every Issue that
    /// writes in <c>DungeonFortress.Simulation</c> moves that number, so the
    /// first such merge would have turned the <c>godot</c> stage red with the
    /// words "The map is drawn with different geometry" over a map drawn
    /// exactly as before — a false red on somebody else's PR, raised by a check
    /// they have no reason to suspect. Keeping the two apart by scheduling the
    /// merges would be an agreement that lasts until the first time somebody
    /// forgets; this is a mechanism.
    /// </para>
    /// </summary>
    [Fact]
    public void The_reference_holds_nothing_but_geometry()
    {
        var reference = File.ReadAllText(Path.Combine(
            PresentationFixtures.FindRepositoryRoot(),
            "tests",
            "golden",
            "world",
            "draw-calls.json"));

        Assert.DoesNotContain("checksum", reference, StringComparison.OrdinalIgnoreCase);

        // A 64-character hex run is what a canonical checksum looks like, under
        // whatever name somebody gives it later. The 16-character pass digests
        // are shorter than that on purpose, and they are geometry.
        Assert.DoesNotMatch("[0-9a-f]{64}", reference);
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

    /// <summary>
    /// The arguments of the one <c>journal.*(...)</c> call a hidden primitive
    /// makes, as written.
    /// </summary>
    private static string JournalCall(string body)
    {
        var start = body.IndexOf("journal.", StringComparison.Ordinal);
        Assert.True(start >= 0, "A hidden primitive makes no call on the journal at all.");

        var open = body.IndexOf('(', start);
        var depth = 0;
        for (var index = open; index < body.Length; index++)
        {
            depth += body[index] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return body[(open + 1)..index];
            }
        }

        throw new InvalidOperationException("The call on the journal is unbalanced.");
    }

    private static HashSet<string> Identifiers(string text)
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var current = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            var isPart = index < text.Length &&
                (char.IsLetterOrDigit(text[index]) || text[index] == '_');
            if (isPart)
            {
                continue;
            }

            if (index > current)
            {
                identifiers.Add(text[current..index]);
            }

            current = index + 1;
        }

        return identifiers;
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

    /// <summary>
    /// The recorded-text half of a journal method's body: every
    /// <c>pass.Point</c> and <c>pass.Size</c> statement blanked, because the
    /// extent and the sizes are not the text that is compared.
    /// </summary>
    private static string RecordedTextRegion(string body)
    {
        var characters = body.ToCharArray();
        foreach (var call in new[] { "pass.Point", "pass.Size" })
        {
            for (var index = 0;
                 (index = body.IndexOf(call, index, StringComparison.Ordinal)) >= 0;)
            {
                var open = index + call.Length;
                if (open >= body.Length || body[open] != '(')
                {
                    index++;
                    continue;
                }

                var close = MatchingParenthesis(body, open);
                for (var current = index; current <= close; current++)
                {
                    characters[current] = ' ';
                }

                index = close + 1;
            }
        }

        return new string(characters);
    }

    private static int MatchingParenthesis(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                default:
                    continue;
            }

            if (depth == 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException("The call is unbalanced.");
    }
}
