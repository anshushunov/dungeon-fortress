using System.Text;

using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #222 — legend completeness.
///
/// Both checks below read only the <em>captions</em> — the first string of
/// each tuple in the array literal <c>CreateLegend</c>'s <c>foreach</c> walks
/// — never the surrounding comments. That distinction is the point: an
/// earlier version of this guard read the whole method body as one blob of
/// text, so a comment that happened to contain the word "fighting" could
/// keep <see cref="State_dot_colors_are_named_in_legend"/> green after the
/// real caption was renamed, and an unrelated comment that happened to
/// contain the two characters <c>("</c> could push
/// <see cref="CreateLegend_authors_the_expected_number_of_legend_rows"/> from
/// green to red with no behaviour change at all. <see cref="StripComments"/>
/// removes every <c>//</c> and <c>/* */</c> comment — but not string or char
/// literal contents — before either check runs, so neither failure mode is
/// possible here: a comment cannot inflate the caption count, and it cannot
/// stand in for a caption that was actually changed.
///
/// The read is text, not syntax (ADR 0011: no engine in this test project),
/// mirroring <see cref="AdapterSource"/>'s approach but keeping string
/// literal contents intact — <see cref="AdapterSource.Masked"/> blanks them,
/// and the legend text this class checks lives inside those literals.
///
/// What this class does <em>not</em> do: verify that every marker actually
/// drawn on the map has a legend row. It only verifies that the legend's own
/// array literal is internally consistent (row count, and that the
/// state-dot row still names "fighting" and "fled"). A marker drawn
/// elsewhere in <c>Main.cs</c> with no matching legend text would not be
/// caught by this class — see the Issue #222 PR body for the raider-marker
/// decision this scope gap was raised against.
/// </summary>
public sealed class LegendCompletenessTests
{
    private static string Source { get; } =
        File.ReadAllText(AdapterSource.FullPath());

    /// <summary>
    /// <see cref="Source"/> with every <c>//</c> and <c>/* */</c> comment
    /// replaced by nothing (newlines kept, so line-oriented reasoning about
    /// the result still works). String and char literal contents are left
    /// untouched — including any <c>//</c> or <c>/*</c> they might contain —
    /// so this is not a general C# lexer, only enough of one to keep
    /// comments out of the two checks below.
    /// </summary>
    private static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
        var index = 0;
        while (index < source.Length)
        {
            var current = source[index];

            if (current is '"' or '\'')
            {
                var quote = current;
                result.Append(current);
                index++;
                while (index < source.Length && source[index] != quote)
                {
                    if (source[index] == '\\' && index + 1 < source.Length)
                    {
                        result.Append(source[index]);
                        result.Append(source[index + 1]);
                        index += 2;
                        continue;
                    }

                    result.Append(source[index]);
                    index++;
                }

                if (index < source.Length)
                {
                    result.Append(source[index]);
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length &&
                       !(source[index] == '*' && source[index + 1] == '/'))
                {
                    if (source[index] == '\n')
                    {
                        result.Append('\n');
                    }

                    index++;
                }

                index += 2;
                continue;
            }

            result.Append(current);
            index++;
        }

        return result.ToString();
    }

    private static string CommentFree { get; } = StripComments(Source);

    /// <summary>
    /// The body of <c>CreateLegend</c>, comment-free. Finds the declaration
    /// at "Control CreateLegend" to distinguish it from a call like
    /// "AddChild(CreateLegend())", then matches braces to find where the
    /// method body ends. Brace matching is safe here because comments were
    /// already stripped — a brace character inside a doc comment cannot be
    /// mistaken for the method's own.
    /// </summary>
    private static string LegendMethodBody()
    {
        var decl = CommentFree.IndexOf(
            "Control CreateLegend",
            StringComparison.Ordinal);
        if (decl < 0)
        {
            throw new InvalidOperationException(
                "Cannot find 'Control CreateLegend' declaration in Main.cs.");
        }

        return BracedBlockAfter(decl);
    }

    /// <summary>
    /// The array literal <c>CreateLegend</c>'s <c>foreach</c> walks — the
    /// actual legend data — sliced out of <see cref="LegendMethodBody"/> so
    /// that <c>legend.AddThemeConstantOverride("separation", 0)</c>, which
    /// precedes it in the same method, plays no part in either check below.
    /// </summary>
    private static string LegendArrayBody()
    {
        var method = LegendMethodBody();
        var arrayDecl = method.IndexOf(
            "new (string Text, int Size, string Color)[]",
            StringComparison.Ordinal);
        if (arrayDecl < 0)
        {
            throw new InvalidOperationException(
                "CreateLegend no longer declares the legend row array with the " +
                "expected element type; update this guard to match.");
        }

        return BracedBlockAfter(arrayDecl, method);
    }

    /// <summary>Finds the first <c>{</c> at or after <paramref name="from"/> in <paramref name="text"/> (defaulting to <see cref="CommentFree"/>) and returns everything between it and its matching <c>}</c>.</summary>
    private static string BracedBlockAfter(int from, string? text = null)
    {
        text ??= CommentFree;
        var bodyStart = text.IndexOf('{', from);
        if (bodyStart < 0)
        {
            throw new InvalidOperationException(
                "Expected an opening brace after position " + from + " in Main.cs.");
        }

        var depth = 0;
        for (var pos = bodyStart; pos < text.Length; pos++)
        {
            if (text[pos] == '{')
            {
                depth++;
            }
            else if (text[pos] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(bodyStart + 1, pos - bodyStart - 1);
                }
            }
        }

        throw new InvalidOperationException("Unbalanced braces in Main.cs.");
    }

    /// <summary>
    /// The first string literal of every tuple in <see cref="LegendArrayBody"/>
    /// — i.e. the visible caption text of each legend row, in source order.
    /// A tuple is recognised by the two literal characters <c>("</c>, which
    /// only ever open a row's <c>Text</c> argument here (the <c>Size</c> and
    /// <c>Color</c> arguments that follow are never immediately preceded by
    /// an opening parenthesis). Comments were already stripped out of
    /// <see cref="CommentFree"/>, so a comment containing the characters
    /// <c>("</c> cannot be mistaken for a row.
    /// </summary>
    private static IReadOnlyList<string> LegendRowCaptions()
    {
        var array = LegendArrayBody();
        var captions = new List<string>();
        for (var index = 0; index < array.Length - 1; index++)
        {
            if (array[index] != '(' || array[index + 1] != '"')
            {
                continue;
            }

            var start = index + 2;
            var end = array.IndexOf('"', start);
            if (end < 0)
            {
                throw new InvalidOperationException(
                    "Unterminated legend caption string in Main.cs.");
            }

            captions.Add(array[start..end]);
            index = end;
        }

        return captions;
    }

    /// <summary>
    /// The number of legend rows must match the row count
    /// <see cref="HudReadabilityTests.AuthoredHud"/> lists as
    /// <c>legend[0]</c>..<c>legend[8]</c> (nine rows). Adding or removing a
    /// row changes this count and must change that list too, so both sides
    /// move together or the mismatch is caught here. Mutant: deleting the
    /// state-dot row (Issue #222) drops the count from 9 to 8.
    /// </summary>
    [Fact]
    public void CreateLegend_authors_the_expected_number_of_legend_rows()
    {
        Assert.Equal(9, LegendRowCaptions().Count);
    }

    /// <summary>
    /// The state-dot row must name at least two high-salience creature
    /// states: fighting and fled. These are the only colors a player sees on
    /// a calm map (idle is the default) that still mean something
    /// actionable. The check looks inside the parsed captions only — not the
    /// raw method text — so renaming the caption while leaving an old word in
    /// a nearby comment does not save this test: the comment plays no part.
    /// Mutant: deleting the state-dot tuple removes "fighting" and "fled"
    /// from every caption, and both assertions fail.
    /// </summary>
    [Fact]
    public void State_dot_colors_are_named_in_legend()
    {
        var captions = LegendRowCaptions();

        Assert.Contains(
            captions,
            caption => caption.Contains("fighting", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            captions,
            caption => caption.Contains("fled", StringComparison.OrdinalIgnoreCase));
    }
}
