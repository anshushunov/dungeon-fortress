using System.Text;

namespace DungeonFortress.Presentation.Tests;

/// <summary>One call found in the adapter, with its arguments already split.</summary>
/// <param name="Name">The method called.</param>
/// <param name="Arguments">Top-level arguments, in source order, trimmed.</param>
/// <param name="Text">The whole call as it appears, for a failure message.</param>
internal sealed record SourceCall(string Name, IReadOnlyList<string> Arguments, string Text);

/// <summary>
/// The Godot adapter, read as text.
///
/// This is the deliberate consequence of the root cause Issue #90 names: no test
/// project references <c>DungeonFortress.Game</c>, and none should — the assembly
/// needs the engine runtime, which is exactly what
/// <see href="../../docs/decisions/0011-presentation-layer-without-engine.md">ADR
/// 0011</see> keeps out of the "Pure .NET" job. So the rules move to
/// <c>DungeonFortress.Presentation</c>, where they are ordinary data, and this
/// reader answers the one remaining question a pure test cannot ask of a value:
/// <em>does the adapter actually do what the declaration says?</em>
///
/// It reads structure, not behaviour. The four things it has to be right about —
/// which methods exist, which calls each one makes, how many arguments a call has
/// and whether an argument mentions a name — survive reformatting, and the
/// alternative (a Roslyn dependency in a test project restored from the engine's
/// bundled package source) costs more than it settles.
///
/// <see cref="UiIconManifestTests"/> already reads the adapter's asset folder for
/// the same reason: a manifest is only a contract while something compares it
/// with the thing it describes.
/// </summary>
internal static class AdapterSource
{
    internal const string RelativePath = "src/DungeonFortress.Game/Main.cs";

    /// <summary>
    /// The adapter with comments, string literals and character literals blanked
    /// out, so a routine named in a doc comment is not mistaken for a call and a
    /// brace inside a string cannot end a method body.
    /// </summary>
    internal static string Masked { get; } = Mask(File.ReadAllText(FullPath()));

    internal static string FullPath() => Path.Combine(
        PresentationFixtures.FindRepositoryRoot(),
        RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Every method whose name starts with the prefix, declared in the adapter.</summary>
    internal static IReadOnlyList<string> DeclaredMethods(string prefix)
    {
        var names = new List<string>();
        var tested = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Masked.Length;)
        {
            var start = Masked.IndexOf(prefix, index, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            index = start + 1;
            if (start > 0 && IsIdentifierPart(Masked[start - 1]))
            {
                continue;
            }

            var end = start;
            while (end < Masked.Length && IsIdentifierPart(Masked[end]))
            {
                end++;
            }

            var name = Masked[start..end];
            if (tested.Add(name) && TryFindDeclaration(name, out _))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>The body of one method: the block, or the expression after <c>=&gt;</c>.</summary>
    internal static string Body(string method)
    {
        if (!TryFindDeclaration(method, out var bodyStart))
        {
            throw new InvalidOperationException(
                $"{RelativePath} declares no method named '{method}'.");
        }

        if (Masked[bodyStart] == '{')
        {
            var end = MatchingBrace(bodyStart);
            return Masked[(bodyStart + 1)..end];
        }

        var terminator = ExpressionBodyEnd(bodyStart);
        return Masked[bodyStart..terminator];
    }

    /// <summary>
    /// Every call to <paramref name="method"/> inside <paramref name="body"/>.
    ///
    /// A call written on <c>this</c> counts, because <c>this.DrawRect(...)</c> is
    /// the same call and the review of this guard got a fully opaque build
    /// progress bar past it by writing exactly that. A call on any other receiver
    /// does not — <c>SelectionGeometry.Outline(...)</c> is a different method on a
    /// different type — and
    /// <c>WorldDrawPassGuardTests.No_covering_primitive_hides_behind_a_receiver</c>
    /// is what turns that remaining assumption into a checked one.
    /// </summary>
    internal static IReadOnlyList<SourceCall> CallsTo(string body, string method)
    {
        var calls = new List<SourceCall>();
        for (var index = 0; index < body.Length;)
        {
            var start = body.IndexOf(method, index, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            index = start + 1;
            if (!IsUnqualifiedIdentifierStart(body, start) ||
                IsIdentifierPart(NextCharacter(body, start + method.Length)))
            {
                continue;
            }

            var open = SkipWhitespace(body, start + method.Length);
            if (open >= body.Length || body[open] != '(')
            {
                continue;
            }

            var close = MatchingParenthesis(body, open);
            calls.Add(new SourceCall(
                method,
                SplitArguments(body[(open + 1)..close]),
                Compact(body[start..(close + 1)])));
            index = close;
        }

        return calls;
    }

    /// <summary>
    /// Which of <paramref name="candidates"/> the body calls, in source order and
    /// with repeats collapsed. This is the adapter's own call graph.
    /// </summary>
    internal static IReadOnlyList<string> CalledRoutines(
        string body,
        IReadOnlyCollection<string> candidates)
    {
        var found = new List<(int Position, string Name)>();
        foreach (var candidate in candidates)
        {
            foreach (var call in CallPositions(body, candidate))
            {
                found.Add((call, candidate));
            }
        }

        return found
            .OrderBy(item => item.Position)
            .Select(item => item.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<int> CallPositions(string body, string method)
    {
        for (var index = 0; index < body.Length;)
        {
            var start = body.IndexOf(method, index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            index = start + 1;
            if (!IsUnqualifiedIdentifierStart(body, start) ||
                IsIdentifierPart(NextCharacter(body, start + method.Length)))
            {
                continue;
            }

            var open = SkipWhitespace(body, start + method.Length);
            if (open < body.Length && body[open] == '(')
            {
                yield return start;
            }
        }
    }

    private static bool TryFindDeclaration(string method, out int bodyStart)
    {
        for (var index = 0; index < Masked.Length;)
        {
            var start = Masked.IndexOf(method, index, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            index = start + 1;
            if (!IsUnqualifiedIdentifierStart(Masked, start) ||
                IsIdentifierPart(NextCharacter(Masked, start + method.Length)))
            {
                continue;
            }

            var open = SkipWhitespace(Masked, start + method.Length);
            if (open >= Masked.Length || Masked[open] != '(')
            {
                continue;
            }

            var after = SkipWhitespace(Masked, MatchingParenthesis(Masked, open) + 1);
            if (after >= Masked.Length)
            {
                continue;
            }

            // A declaration is the only place a parameter list is followed by a
            // block or by the arrow of an expression body.
            if (Masked[after] == '{' ||
                (Masked[after] == '=' &&
                 after + 1 < Masked.Length &&
                 Masked[after + 1] == '>'))
            {
                bodyStart = Masked[after] == '{' ? after : after + 2;
                return true;
            }
        }

        bodyStart = -1;
        return false;
    }

    private static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var parts = new List<string>();
        var depth = 0;
        var current = new StringBuilder();
        foreach (var character in arguments)
        {
            switch (character)
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(Compact(current.ToString()));
                    current.Clear();
                    continue;
            }

            current.Append(character);
        }

        var last = Compact(current.ToString());
        if (last.Length > 0 || parts.Count > 0)
        {
            parts.Add(last);
        }

        return parts;
    }

    private static string Compact(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int MatchingBrace(int open) => MatchingPair(Masked, open, '{', '}');

    private static int MatchingParenthesis(string text, int open) =>
        MatchingPair(text, open, '(', ')');

    private static int MatchingPair(string text, int open, char opening, char closing)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == opening)
            {
                depth++;
            }
            else if (text[index] == closing)
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        throw new InvalidOperationException(
            $"{RelativePath} has an unbalanced '{opening}' at offset {open}.");
    }

    private static int ExpressionBodyEnd(int start)
    {
        var depth = 0;
        for (var index = start; index < Masked.Length; index++)
        {
            switch (Masked[index])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ';' when depth == 0:
                    return index;
            }
        }

        throw new InvalidOperationException(
            $"{RelativePath} has an unterminated expression body at offset {start}.");
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static char NextCharacter(string text, int index) =>
        index < text.Length ? text[index] : '\0';

    /// <summary>
    /// Whether the identifier at <paramref name="index"/> starts a call the
    /// adapter makes on itself: either unqualified, or written on <c>this</c>.
    /// </summary>
    private static bool IsUnqualifiedIdentifierStart(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        if (text[index - 1] != '.')
        {
            return !IsIdentifierPart(text[index - 1]);
        }

        var receiverEnd = index - 1;
        var receiverStart = receiverEnd;
        while (receiverStart > 0 && IsIdentifierPart(text[receiverStart - 1]))
        {
            receiverStart--;
        }

        return string.CompareOrdinal(text, receiverStart, "this", 0, 4) == 0 &&
            receiverEnd - receiverStart == 4 &&
            (receiverStart == 0 || !IsIdentifierPart(text[receiverStart - 1]));
    }

    /// <summary>
    /// Every receiver a call to <paramref name="method"/> is written on in the
    /// whole file, so a receiver the reader does not understand is named rather
    /// than silently skipped.
    /// </summary>
    internal static IReadOnlyList<string> ReceiversOf(string method)
    {
        var receivers = new List<string>();
        for (var index = 0; index < Masked.Length;)
        {
            var start = Masked.IndexOf(method, index, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            index = start + 1;
            if (IsIdentifierPart(NextCharacter(Masked, start + method.Length)) ||
                start == 0 ||
                Masked[start - 1] != '.')
            {
                continue;
            }

            var open = SkipWhitespace(Masked, start + method.Length);
            if (open >= Masked.Length || Masked[open] != '(')
            {
                continue;
            }

            var receiverStart = start - 1;
            while (receiverStart > 0 && IsIdentifierPart(Masked[receiverStart - 1]))
            {
                receiverStart--;
            }

            receivers.Add(Masked[receiverStart..(start - 1)]);
        }

        return receivers;
    }

    private static bool IsIdentifierPart(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    /// <summary>
    /// Blanks comments and literals while keeping every offset, so a body can be
    /// cut out of the masked text and still line up with the file.
    /// </summary>
    private static string Mask(string source)
    {
        var masked = source.ToCharArray();
        var index = 0;
        while (index < source.Length)
        {
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    masked[index++] = ' ';
                }

                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                masked[index++] = ' ';
                masked[index++] = ' ';
                while (index + 1 < source.Length &&
                       !(source[index] == '*' && source[index + 1] == '/'))
                {
                    if (source[index] != '\n')
                    {
                        masked[index] = ' ';
                    }

                    index++;
                }

                masked[index++] = ' ';
                masked[index++] = ' ';
                continue;
            }

            if (source[index] is '"' or '\'')
            {
                var quote = source[index];
                index++;
                while (index < source.Length && source[index] != quote)
                {
                    if (source[index] == '\\')
                    {
                        masked[index++] = ' ';
                        if (index < source.Length)
                        {
                            masked[index++] = ' ';
                        }

                        continue;
                    }

                    if (source[index] != '\n')
                    {
                        masked[index] = ' ';
                    }

                    index++;
                }

                index++;
                continue;
            }

            index++;
        }

        return new string(masked);
    }
}
