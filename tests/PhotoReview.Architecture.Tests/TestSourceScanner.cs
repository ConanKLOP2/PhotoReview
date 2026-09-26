using System.Text.RegularExpressions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// One [Fact]/[Theory] method found in a test source file.
/// <see cref="Body"/> is the method body with comments and string/char literal contents blanked (same length as the
/// source, so tokens in code can be matched without false hits from text); <see cref="RawBody"/> is the original text.
/// <see cref="Attributes"/> holds the method's attribute lists and <see cref="EnclosingHeaders"/> the attribute/modifier
/// text in front of every enclosing type, both in original text (so Skip reasons and Trait values are readable).
/// </summary>
internal sealed record ScannedTest(
    string File, int Line, string Name, string Attributes, string EnclosingHeaders, string Body, string RawBody)
{
    public string Key => $"{File}::{Name}";

    public string SkipReason => TestSourceScanner.SkipReasonPattern.Match(Attributes) is { Success: true } m ? m.Groups["r"].Value : "";

    public bool IsSkipped => TestSourceScanner.SkipMarker.IsMatch(Attributes);

    public IReadOnlyList<string> Categories =>
        TestSourceScanner.CategoryPattern.Matches(Attributes + "\n" + EnclosingHeaders).Select(m => m.Groups["c"].Value).ToArray();
}

/// <summary>
/// Syntax-light scanner shared by the test-quality guards: finds every [Fact]/[Theory] method of a source text with its
/// blanked body, attributes and enclosing type headers. No Roslyn: comment/literal blanking plus brace matching is enough
/// for the questions the guards ask (does the body contain an assertion / a delay / a real-OS token, which categories apply).
/// </summary>
internal static class TestSourceScanner
{
    internal static readonly Regex SkipMarker = new(@"\bSkip\s*=", RegexOptions.Compiled);
    internal static readonly Regex SkipReasonPattern = new("\\bSkip\\s*=\\s*\"(?<r>[^\"]*)\"", RegexOptions.Compiled);
    internal static readonly Regex CategoryPattern = new("Trait\\(\\s*\"Category\"\\s*,\\s*\"(?<c>\\w+)\"", RegexOptions.Compiled);

    private static readonly Regex TestAttribute = new(@"\[\s*(?:Fact|Theory)\b", RegexOptions.Compiled);
    private static readonly Regex TypeDeclaration = new(@"\b(?:class|record|struct)\s+[A-Za-z_]\w*[^{;=]*\{", RegexOptions.Compiled);
    private static readonly Regex LastIdentifier = new(@"([A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*$", RegexOptions.Compiled);

    /// <summary>All test methods of every *.cs file under tests/ (build output excluded).</summary>
    public static IReadOnlyList<ScannedTest> AllTests()
    {
        var result = new List<ScannedTest>();
        foreach (var file in RepoScan.CsFiles("tests"))
            result.AddRange(Scan(RepoScan.Relative(file), RepoScan.Text(file)));
        return result;
    }

    public static List<ScannedTest> Scan(string relativeFile, string text)
    {
        var code = TestIsolationRulesTests.Blank(text);
        var types = FindTypes(text, code);
        var result = new List<ScannedTest>();

        foreach (Match attribute in TestAttribute.Matches(code))
        {
            var start = attribute.Index;
            // Attribute lists written above the [Fact] (e.g. [Trait]) belong to the method too.
            while (true)
            {
                var before = start - 1;
                while (before >= 0 && char.IsWhiteSpace(code[before])) before--;
                if (before < 0 || code[before] != ']') break;
                var open = MatchBackward(code, before);
                if (open < 0) break;
                start = open;
            }

            var position = attribute.Index;
            while (true)
            {
                while (position < code.Length && char.IsWhiteSpace(code[position])) position++;
                if (position >= code.Length || code[position] != '[') break;
                position = MatchForward(code, position, '[', ']') + 1;
            }

            var paren = code.IndexOf('(', position);
            if (paren < 0) continue;
            var name = LastIdentifier.Match(code[position..paren]) is { Success: true } id ? id.Groups[1].Value : "?";
            var parametersEnd = MatchForward(code, paren, '(', ')');

            var bodyStart = parametersEnd + 1;
            while (bodyStart < code.Length && code[bodyStart] is not ('{' or ';') && !StartsWith(code, bodyStart, "=>")) bodyStart++;
            if (bodyStart >= code.Length || code[bodyStart] == ';') continue; // abstract / no body

            int bodyEnd;
            if (code[bodyStart] == '{')
            {
                bodyEnd = MatchForward(code, bodyStart, '{', '}') + 1;
            }
            else
            {
                bodyEnd = bodyStart;
                var depth = 0;
                while (bodyEnd < code.Length)
                {
                    var c = code[bodyEnd];
                    if (c is '(' or '{' or '[') depth++;
                    else if (c is ')' or '}' or ']') depth--;
                    else if (c == ';' && depth <= 0) break;
                    bodyEnd++;
                }
            }

            var headers = string.Concat(types.Where(t => t.Open < attribute.Index && attribute.Index < t.Close).Select(t => t.Header));
            result.Add(new ScannedTest(
                relativeFile,
                text.AsSpan(0, attribute.Index).Count('\n') + 1,
                name,
                text[start..position],
                headers,
                code[bodyStart..bodyEnd],
                text[bodyStart..bodyEnd]));
        }

        return result;
    }

    private readonly record struct TypeSpan(int Open, int Close, string Header);

    private static List<TypeSpan> FindTypes(string text, string code)
    {
        var spans = new List<TypeSpan>();
        foreach (Match match in TypeDeclaration.Matches(code))
        {
            var open = match.Index + match.Length - 1;
            var close = MatchForward(code, open, '{', '}');
            var headerStart = code.LastIndexOfAny(['}', ';', '{'], Math.Max(0, match.Index - 1)) + 1;
            spans.Add(new TypeSpan(open, close, text[headerStart..match.Index]));
        }
        return spans;
    }

    private static int MatchForward(string code, int open, char openChar, char closeChar)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == openChar) depth++;
            else if (code[i] == closeChar && --depth == 0) return i;
        }
        return code.Length - 1;
    }

    private static int MatchBackward(string code, int close)
    {
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            if (code[i] == ']') depth++;
            else if (code[i] == '[' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool StartsWith(string text, int index, string token) => string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
}
