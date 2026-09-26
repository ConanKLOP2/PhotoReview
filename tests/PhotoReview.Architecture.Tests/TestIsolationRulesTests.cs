using System.Text.RegularExpressions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// TEST-09: a test that changes process-wide environment variables (directly or through
/// <c>DataRootFixture</c>) must run in the non-parallel "GlobalState" collection, otherwise it can
/// leak PHOTOREVIEW_DATA_ROOT into a concurrently running test. The check is per class (R2-A-11):
/// an attributed class must not vouch for an unattributed sibling in the same file.
/// </summary>
public sealed class TestIsolationRulesTests
{
    // Built by concatenation so the literals stay greppable-clean.
    private const string SetEnvCall = "Environment." + "SetEnvironmentVariable(";
    private const string DataRootFixtureName = "DataRoot" + "Fixture";
    private const string CollectionAttribute = "[Collection(\"GlobalState\")]";

    [Fact(DisplayName = "Rule TEST-09: test classes that mutate environment variables are in the GlobalState collection")]
    public void EnvironmentMutatingTests_AreInGlobalStateCollection()
    {
        // Partial classes may carry the attribute on any one part, so collect attributed names across all files first.
        var attributed = new HashSet<string>(StringComparer.Ordinal);
        var mutating = new List<(string Key, string Display)>();

        foreach (var file in RepoScan.CsFiles("tests"))
        {
            var relative = RepoScan.Relative(file);
            // The fixture itself, shared helpers and this rule file (its docs name the fixture) are not tests.
            if (relative.StartsWith("tests/PhotoReview.TestSupport", StringComparison.Ordinal) || relative.EndsWith("/TestIsolationRulesTests.cs", StringComparison.Ordinal)) continue;

            var project = string.Join('/', relative.Split('/').Take(2));
            foreach (var type in FindTypes(RepoScan.Text(file)))
            {
                var key = $"{project}::{type.Name}";
                // A nested helper is not a test class of its own: it runs inside (and is covered by) its enclosing class.
                if ((type.AncestorHeaders + type.Header).Contains(CollectionAttribute, StringComparison.Ordinal)) attributed.Add(key);
                if (MutatesEnvironment(type.Body)) mutating.Add((key, $"{relative}: class {type.Name}"));
            }
        }

        var violations = mutating.Where(m => !attributed.Contains(m.Key)).Select(m => m.Display).ToList();
        Assert.True(mutating.Count > 0, "The scan found no environment-mutating test classes; the rule is not checking anything.");
        Assert.True(
            violations.Count == 0,
            "These test classes mutate environment variables (or use DataRootFixture) but lack " +
            $"{CollectionAttribute}:\n{string.Join("\n", violations)}");
    }

    [Fact(DisplayName = "Rule TEST-09 scanner is per class: an unattributed sibling class in an attributed file is found")]
    public void Scanner_DistinguishesClassesWithinOneFile()
    {
        var source = "namespace N;\n"
            + "[Collection(\"GlobalState\")]\n"
            + "public sealed class Safe { void A() { var s = \"}\"; /* { */ " + SetEnvCall + "\"X\", \"1\"); } }\n"
            + "public sealed class Leaky { void B() { using var f = new " + DataRootFixtureName + "(); } }\n"
            + "public sealed class Clean { void C() { } }\n";
        var types = FindTypes(source);

        Assert.Equal(["Safe", "Leaky", "Clean"], types.Select(t => t.Name).ToArray());
        Assert.Contains(CollectionAttribute, types[0].Header, StringComparison.Ordinal);
        Assert.True(MutatesEnvironment(types[0].Body));
        Assert.DoesNotContain(CollectionAttribute, types[1].Header, StringComparison.Ordinal);
        Assert.True(MutatesEnvironment(types[1].Body));
        Assert.False(MutatesEnvironment(types[2].Body));
    }

    private static bool MutatesEnvironment(string body) =>
        body.Contains(SetEnvCall, StringComparison.Ordinal) || body.Contains(DataRootFixtureName, StringComparison.Ordinal);

    private sealed record TypeSpan(string Name, string Header, string Body, string AncestorHeaders);

    /// <summary>
    /// Every class/record/struct declaration of a file, nested ones included: name, the attribute/modifier text before the
    /// keyword and the braces-delimited body without the bodies of nested types (a nested type is judged on its own).
    /// Braces inside comments and literals are ignored.
    /// </summary>
    private static List<TypeSpan> FindTypes(string text)
    {
        var result = new List<TypeSpan>();
        Collect(text, Blank(text), 0, text.Length, result);
        return result;
    }

    private static readonly Regex Declaration = new(@"\b(?:class|record|struct)\s+(?<name>[A-Za-z_]\w*)[^{;=]*\{");

    private static int MatchingBrace(string code, int open, int end)
    {
        var depth = 0;
        for (var k = open; k < end; k++)
        {
            if (code[k] == '{') depth++;
            else if (code[k] == '}' && --depth == 0) return k;
        }
        return end - 1;
    }

    private static void Collect(string text, string code, int start, int end, List<TypeSpan> result, string ancestors = "")
    {
        var position = start;
        while (position < end && Declaration.Match(code, position, end - position) is { Success: true } match)
        {
            var open = match.Index + match.Length - 1;
            var close = MatchingBrace(code, open, end);
            var headerStart = code.LastIndexOfAny(['}', ';', '{'], Math.Max(0, match.Index - 1)) + 1;
            var own = text.ToCharArray(open, close + 1 - open);
            var inner = open + 1;
            while (inner < close && Declaration.Match(code, inner, close - inner) is { Success: true } nested)
            {
                var nestedClose = MatchingBrace(code, nested.Index + nested.Length - 1, close);
                for (var k = nested.Index; k <= nestedClose; k++) own[k - open] = ' ';
                inner = nestedClose + 1;
            }
            result.Add(new TypeSpan(match.Groups["name"].Value, text[headerStart..match.Index], new string(own), ancestors));
            Collect(text, code, open + 1, close, result, ancestors + text[headerStart..match.Index]);
            position = close + 1;
        }
    }

    /// <summary>Same-length copy of <paramref name="text"/> with comment and string/char literal contents blanked out.</summary>
    internal static string Blank(string text)
    {
        var chars = text.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (Starts(text, i, "//"))
            {
                while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
            }
            else if (Starts(text, i, "/*"))
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? chars.Length : end + 2;
                while (i < end) Erase(chars, i++);
            }
            else if (Starts(text, i, "\"\"\""))
            {
                // A raw string literal opens and closes with the same run of 3+ quotes (so a 4-quote literal may contain """).
                var quotes = 3;
                while (i + quotes < text.Length && text[i + quotes] == '"') quotes++;
                var fence = new string('"', quotes);
                var end = text.IndexOf(fence, i + quotes, StringComparison.Ordinal);
                end = end < 0 ? chars.Length : end + quotes;
                while (i < end) Erase(chars, i++);
            }
            else if (chars[i] == '"')
            {
                var verbatim = i > 0 && (text[i - 1] == '@' || (i > 1 && text[i - 2] == '@' && text[i - 1] == '$'));
                Erase(chars, i++);
                while (i < chars.Length)
                {
                    if (verbatim && chars[i] == '"' && i + 1 < chars.Length && text[i + 1] == '"') { Erase(chars, i++); Erase(chars, i++); continue; }
                    if (!verbatim && chars[i] == '\\' && i + 1 < chars.Length) { Erase(chars, i++); Erase(chars, i++); continue; }
                    if (chars[i] == '"') { Erase(chars, i++); break; }
                    Erase(chars, i++);
                }
            }
            else if (chars[i] == '\'')
            {
                var end = i + 1;
                while (end < chars.Length && text[end] != '\'' && text[end] != '\n') end += text[end] == '\\' ? 2 : 1;
                if (end - i <= 4) { while (i <= end && i < chars.Length) Erase(chars, i++); }
                else i++;
            }
            else i++;
        }
        return new string(chars);
    }

    private static bool Starts(string text, int index, string token) =>
        string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    private static void Erase(char[] chars, int index)
    {
        if (chars[index] != '\n') chars[index] = ' ';
    }
}
