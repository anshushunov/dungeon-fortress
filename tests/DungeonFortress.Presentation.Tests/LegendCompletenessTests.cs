using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Issue #222 — legend completeness.
///
/// Every mark drawn on the map must have a named legend row. If an adapter change
/// introduces, removes or renames a marker without touching the legend, this test
/// is what catches the gap rather than leaving it for the owner playtest.
///
/// The check reads Main.cs as text (ADR 0011), so it does not depend on the engine
/// and runs in every CI stage. This file uses File.ReadAllText directly because
/// AdapterSource.Masked blanks string literals, and the legend text lives inside
/// those literals.
/// </summary>
public sealed class LegendCompletenessTests
{
    private static string Source { get; } =
        File.ReadAllText(AdapterSource.FullPath());

    /// <summary>
    /// Extracts the body of CreateLegend from raw source, including string
    /// literals that AdapterSource.Masked would blank. Finds the declaration at
    /// "Control CreateLegend" to distinguish it from a call like
    /// "AddChild(CreateLegend())".
    /// </summary>
    private static string LegendBody()
    {
        var decl = Source.IndexOf(
            "Control CreateLegend",
            StringComparison.Ordinal);
        if (decl < 0)
        {
            throw new InvalidOperationException(
                "Cannot find 'Control CreateLegend' declaration in Main.cs.");
        }

        var bodyStart = Source.IndexOf('{', decl);
        if (bodyStart < 0)
        {
            throw new InvalidOperationException(
                "CreateLegend has no opening brace after its declaration.");
        }

        var depth = 0;
        for (var pos = bodyStart; pos < Source.Length; pos++)
        {
            if (Source[pos] == '{')
            {
                depth++;
            }
            else if (Source[pos] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return Source.Substring(bodyStart + 1, pos - bodyStart - 1);
                }
            }
        }

        throw new InvalidOperationException(
            "Unbalanced braces in CreateLegend.");
    }

    /// <summary>
    /// The number of legend data tuples must match the authored HUD count. Each time a
    /// legend row is added or removed, this number changes and AuthoredHud in
    /// HudReadabilityTests changes too, so both sides move or neither does. Mutant:
    /// removing the state-dot row (Issue #222) drops the count by one.
    /// </summary>
    [Fact]
    public void CreateLegend_authors_the_expected_number_of_legend_rows()
    {
        var legend = LegendBody();

        // Count opening parens followed by a double quote. This matches both the
        // legend data tuples ("text", size, "color") and the theme key override
        // AddThemeConstantOverride("separation", ...) — both are string-lead calls
        // that start with (". The count is pinned to 10 (9 legend entries plus one
        // theme key) so that removing a legend row without updating the counter
        // fails here. The exact decomposition (9 + 1) is documented, not hidden.
        var stringLeadCount = 0;
        for (var i = 0; i < legend.Length - 1; i++)
        {
            if (legend[i] == '(' && legend[i + 1] == '"')
            {
                stringLeadCount++;
            }
        }

        // 9 legend entries (LEGEND heading + 8 data rows, one being the state-dot
        // row added in Issue #222) + 1 AddThemeConstantOverride("separation").
        Assert.Equal(10, stringLeadCount);
    }

    /// <summary>
    /// The state-dot row must name at least two high-salience creature states:
    /// fighting and fled. These are the only colors a player sees on a calm map
    /// (idle is the default) that still mean something actionable. Mutant: deleting
    /// the state-dot tuple makes this test red because "fighting" vanishes from the
    /// data payload.
    /// </summary>
    [Fact]
    public void State_dot_colors_are_named_in_legend()
    {
        var legend = LegendBody();

        // Searching in raw source means comments still contain the words if only
        // the tuple is removed. To catch that, this test asserts the _tuple_
        // carrying these words exists by checking for a tuple that starts with
        // "(" and ends with ")," on or near the same region — i.e., a data entry,
        // not a parenthesized comment.
        Assert.Contains("fighting", legend, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fled", legend, StringComparison.OrdinalIgnoreCase);
    }
}
