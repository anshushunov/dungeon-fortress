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
    /// The directory the adapter's files live in, and the prefix that tells them
    /// from every other type in the same folder.
    /// </summary>
    private const string AdapterDirectory = "src/DungeonFortress.Game";
    private const string AdapterFilePrefix = "Main.";

    /// <summary>
    /// Every file of the adapter, in a fixed order.
    ///
    /// <c>Main</c> is one class spread over several files since Issue #281, and a
    /// reader that opened only <c>Main.cs</c> would have gone on answering
    /// questions about a routine it could no longer see — silently, because a
    /// missing declaration reads the same as a declaration that was never there.
    /// The set is discovered rather than listed so that a file added to the class
    /// is guarded from the moment it exists, and it is ordinal-sorted so that an
    /// offset into <see cref="Masked"/> means the same thing on every machine.
    /// </summary>
    internal static IReadOnlyList<string> FullPaths() => Files;

    private static readonly string[] Files = Directory
        .GetFiles(
            Path.Combine(
                PresentationFixtures.FindRepositoryRoot(),
                AdapterDirectory.Replace('/', Path.DirectorySeparatorChar)),
            "*.cs")
        .Where(path => Path.GetFileName(path).StartsWith(AdapterFilePrefix, StringComparison.Ordinal))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// The adapter with comments, string literals and character literals blanked
    /// out, so a routine named in a doc comment is not mistaken for a call and a
    /// brace inside a string cannot end a method body.
    /// </summary>
    internal static string Masked { get; } =
        string.Join('\n', Files.Select(path => Mask(File.ReadAllText(path))));

    /// <summary>
    /// The adapter exactly as it is written, for the two checks that are about
    /// what is inside a string literal and would be blanked by
    /// <see cref="Masked"/>.
    /// </summary>
    internal static string Raw { get; } =
        string.Join('\n', Files.Select(File.ReadAllText));

    internal static string FullPath() => Path.Combine(
        PresentationFixtures.FindRepositoryRoot(),
        RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Every method whose name starts with the prefix, declared in the adapter
    /// — its own routines, not the engine primitives it hides.
    ///
    /// <para>
    /// The exclusion is what Issue #295 added and it is narrow on purpose. A
    /// declaration carrying the <c>new</c> modifier does not introduce a
    /// routine: it redefines a method the engine already declares, which is how
    /// the world-geometry journal reaches every mark without a single call site
    /// being touched. Counting those as routines would make
    /// <c>Every_drawing_routine_of_the_adapter_is_declared</c> demand that
    /// <c>WorldDrawOrder</c> declare <c>DrawRect</c> as a mark with a pass and a
    /// policy, which it is not.
    /// </para>
    ///
    /// <para>
    /// The escape this opens — hiding a real routine behind the modifier — is
    /// closed rather than accepted: <see cref="HiddenEnginePrimitives"/> is
    /// compared against the primitives the adapter actually calls, and every
    /// one of them has to forward to <c>base</c> and do nothing else
    /// (<c>WorldGeometryJournalGuardTests</c>).
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> DeclaredMethods(string prefix) =>
        NamesWithPrefix(prefix)
            .Where(name => TryFindDeclaration(name, out _))
            .Where(name => !HidesEnginePrimitive(name))
            .ToArray();

    /// <summary>
    /// Every method whose name starts with the prefix that the adapter
    /// <em>calls</em>, declared here or not. An engine primitive is exactly a
    /// name that is called and belongs to no manifest, which is what makes the
    /// completeness check of the shims self-maintaining.
    /// </summary>
    internal static IReadOnlyList<string> CalledMethods(string prefix) =>
        NamesWithPrefix(prefix)
            .Where(name => CallPositions(Masked, name).Any() ||
                ReceiversOfAny(name).Count > 0)
            .ToArray();

    /// <summary>
    /// Every method the adapter declares with the <c>new</c> modifier: the
    /// engine primitives it hides in order to journal them.
    /// </summary>
    internal static IReadOnlyList<string> HiddenEnginePrimitives(string prefix) =>
        NamesWithPrefix(prefix)
            .Where(HidesEnginePrimitive)
            .ToArray();

    /// <summary>Every receiver of <paramref name="method"/>, <c>base</c> included.</summary>
    internal static IReadOnlyList<string> ReceiversOfAny(string method) =>
        Receivers(method, includeBase: true);

    /// <summary>
    /// Every distinct identifier starting with the prefix that appears anywhere
    /// in the adapter, ordinal-sorted.
    /// </summary>
    private static IReadOnlyList<string> NamesWithPrefix(string prefix)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
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

            names.Add(Masked[start..end]);
        }

        return [.. names];
    }

    /// <summary>
    /// Whether the declaration of <paramref name="method"/> carries the
    /// <c>new</c> modifier.
    ///
    /// <para>
    /// The modifier is looked for between the end of the previous statement or
    /// block and the method's own name, which is exactly the run of modifiers
    /// and the return type. <c>new</c> as an operator always stands inside an
    /// expression and therefore after the last <c>;</c>, <c>{</c> or <c>}</c>
    /// of some statement — never in that run.
    /// </para>
    /// </summary>
    private static bool HidesEnginePrimitive(string method)
    {
        if (!TryFindDeclarationName(method, out var nameStart))
        {
            return false;
        }

        var start = nameStart;
        while (start > 0 && Masked[start - 1] is not (';' or '{' or '}'))
        {
            start--;
        }

        foreach (var token in Masked[start..nameStart]
                     .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, "new", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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

    private static bool TryFindDeclaration(string method, out int bodyStart) =>
        TryFindDeclaration(method, out bodyStart, out _);

    private static bool TryFindDeclarationName(string method, out int nameStart) =>
        TryFindDeclaration(method, out _, out nameStart);

    private static bool TryFindDeclaration(string method, out int bodyStart, out int nameStart)
    {
        nameStart = -1;
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
                nameStart = start;
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
    internal static IReadOnlyList<string> ReceiversOf(string method) =>
        Receivers(method, includeBase: false);

    private static IReadOnlyList<string> Receivers(string method, bool includeBase)
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

            var receiver = Masked[receiverStart..(start - 1)];
            if (!includeBase && string.Equals(receiver, "base", StringComparison.Ordinal))
            {
                // The one receiver that is not a mark. `base.DrawRect(...)`
                // exists in exactly one place — inside the declaration that
                // hides `DrawRect` — and it is the forwarding call that makes
                // the picture identical when nothing is being journalled
                // (Issue #295). Every occurrence is held to that by
                // WorldGeometryJournalGuardTests, which is what keeps this
                // exemption from becoming the hole the guard was opened
                // against; ReceiversOfAny still reports them.
                continue;
            }

            receivers.Add(receiver);
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
