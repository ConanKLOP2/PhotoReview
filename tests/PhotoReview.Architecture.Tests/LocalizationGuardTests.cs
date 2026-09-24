using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PhotoReview.Core.Localization;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// L03 (docs/refactoring/I18N-PLAN.md, ADR 0006): user-visible text must come from the JSON catalogs.
/// Existing hard-coded text is frozen in two allowlists (<c>path|count</c>) that may only shrink:
/// a new file or a higher count fails, and a lower count fails too until the allowlist is lowered,
/// so every conversion permanently tightens the guard.
/// </summary>
public sealed class LocalizationGuardTests
{
    private const string CsAllowlistFile = "tests/PhotoReview.Architecture.Tests/localization-allowlist.txt";
    private const string XamlAllowlistFile = "tests/PhotoReview.Architecture.Tests/localization-xaml-allowlist.txt";
    private const string LanguagesDir = "src/PhotoReview.Core/Localization/Languages";

    // Vietnamese letters: precomposed vowels with tone marks (Latin-1 subset + Latin Extended Additional
    // U+1EA0..U+1EF9), the Vietnamese-specific letters ăâêôơưđ (both cases), and the decomposed tone marks.
    private static readonly Regex VietnameseLetter = new(
        "[À-ÃÈ-ÊÌÍÒ-ÕÙÚÝ" +
        "à-ãè-êìíò-õùúý" +
        "ĂăĐđĨĩŨũƠơƯư" +
        "Ạ-ỵ̃̀́̃̉]",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> XamlTextAttributes = new(StringComparer.Ordinal)
    {
        "Title", "Text", "Content", "Header", "ToolTip", "AutomationProperties.Name",
    };

    // Elements whose direct text content is shown to the user.
    private static readonly HashSet<string> XamlTextElements = new(StringComparer.Ordinal)
    {
        "TextBlock", "Run", "ComboBoxItem", "ListBoxItem", "Button", "Label", "CheckBox", "RadioButton",
        "ToggleButton", "MenuItem", "Hyperlink", "TabItem", "GroupBox", "String",
    };

    private static readonly HashSet<string> NeutralWords = new(StringComparer.Ordinal) { "P50", "P95", "P99", "Max" };

    // Numbers, punctuation, symbols and spaces only ("100%", "·", "×", "…", "+", "-").
    // Emoji are surrogate pairs (\p{Cs}) plus an optional variation selector / zero-width joiner: symbols, not words.
    private static readonly Regex NeutralSymbols = new(@"^[\p{N}\p{P}\p{S}\p{Cs}️‍\s]+$", RegexOptions.CultureInvariant);

    private static readonly Regex TrMarkupKey = new(
        @"\{\s*loc:Tr(?:Extension)?\s+(?:Key\s*=\s*)?(?<key>[A-Za-z0-9_.\-]+)\s*[,}]",
        RegexOptions.CultureInvariant);

    private static readonly Regex TrElementKey = new(
        @"<loc:Tr(?:Extension)?\b[^>]*?\bKey\s*=\s*""(?<key>[^""]+)""",
        RegexOptions.CultureInvariant);

    [Fact(DisplayName = "L03: no Vietnamese string literals in src/**/*.cs outside the catalogs (shrinking allowlist)")]
    public void NoVietnameseLiteralsOutsideCatalogs()
    {
        var root = FindRepoRoot();
        var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(root, "src", "*.cs"))
        {
            var relative = Relative(root, file);
            if (relative.StartsWith(LanguagesDir + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var count = CSharpStringLiterals.Scan(File.ReadAllText(file)).Count(ContainsVietnamese);
            if (count > 0) actual[relative] = count;
        }

        AssertMatchesAllowlist(root, CsAllowlistFile, actual, "string literals with Vietnamese text");
    }

    [Fact(DisplayName = "L03: no literal text in XAML attributes/content (shrinking allowlist)")]
    public void NoLiteralTextInXaml()
    {
        var root = FindRepoRoot();
        var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(root, "src/PhotoReview.App", "*.xaml"))
        {
            var count = FindXamlLiteralText(XDocument.Load(file)).Count;
            if (count > 0) actual[Relative(root, file)] = count;
        }

        AssertMatchesAllowlist(root, XamlAllowlistFile, actual, "literal UI texts in XAML");
    }

    [Fact(DisplayName = "L03: every {loc:Tr key} used in XAML exists in en.json")]
    public void XamlTrKeysExistInEnglishCatalog()
    {
        var root = FindRepoRoot();
        var english = ParseCatalog(Path.Combine(root, LanguagesDir, "en.json"));
        var missing = new List<string>();
        foreach (var file in SourceFiles(root, "src/PhotoReview.App", "*.xaml"))
        {
            foreach (var key in ExtractTrKeys(File.ReadAllText(file)))
            {
                if (!english.Entries.ContainsKey(key)) missing.Add($"{Relative(root, file)}: '{key}'");
            }
        }

        Assert.True(missing.Count == 0, "XAML uses keys that are not in en.json:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void ExtractTrKeys_FindsShortLongAndElementForms_IgnoresOtherExtensions()
    {
        const string sample = """
            <Window Title="{loc:Tr window.main.title}"
                    Tag="{Binding Name}">
              <Button Content="{loc:Tr Key=button.ok}" ToolTip="{loc:Tr button.ok.tip, Mode=OneWay}" />
              <TextBlock Text="{loc:TrExtension status.ready}" />
              <TextBlock Text="{x:Static local:Foo.Bar}" />
              <TextBlock><TextBlock.Text><loc:Tr Key="menu.file" /></TextBlock.Text></TextBlock>
            </Window>
            """;

        Assert.Equal(
            ["window.main.title", "button.ok", "button.ok.tip", "status.ready", "menu.file"],
            ExtractTrKeys(sample));
    }

    [Fact]
    public void CSharpStringLiterals_SkipsCommentsAndCharsAndSplitsInterpolationHoles()
    {
        const string code = """"
            // "comment one"
            /* "comment two" */
            /// <summary>"doc"</summary>
            var a = "plain \" quote";   // trailing "comment"
            var b = @"verbatim ""q"" x";
            var c = '"';
            var d = $"pre {Call("inner", '}')} post {{lit}}";
            var e = """
                raw "text"
                """;
            var f = $$"""a {{x}} {b}""";
            var g = $@"v {y} ""z""";
            var url = "http://example"; // not a comment inside a string
            """";

        Assert.Equal(
            [
                "plain \\\" quote",
                "verbatim \"\"q\"\" x",
                "inner",
                "pre  post {{lit}}",
                "\n    raw \"text\"\n    ",
                "a  {b}",
                "v  \"\"z\"\"",
                "http://example",
            ],
            CSharpStringLiterals.Scan(code.Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void NoLiteralTextInXaml_Detector_FlagsTextButNotBindingsOrSymbols()
    {
        var doc = XDocument.Parse("""
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Cài đặt">
              <StackPanel>
                <Button Content="{Binding Ok}" ToolTip="{}Literal {text}" AutomationProperties.Name="Close" />
                <TextBlock Text="100%" />
                <TextBlock Text="P95" />
                <TextBlock Text="" />
                <TextBlock>Hello</TextBlock>
                <ComboBoxItem>·</ComboBoxItem>
                <Setter Property="Header" Value="Name" />
              </StackPanel>
            </Window>
            """);

        var found = FindXamlLiteralText(doc);

        Assert.Equal(["Title=Cài đặt", "ToolTip={}Literal {text}", "AutomationProperties.Name=Close", "TextBlock>Hello", "Setter.Header=Name"], found);
    }

    [Fact(DisplayName = "L03: shipped catalogs parse and have no unknown keys or placeholders")]
    public void ShippedCatalogsHaveNoUnknownKeysOrPlaceholders()
    {
        var root = FindRepoRoot();
        var dir = Path.Combine(root, LanguagesDir);
        var english = ParseCatalog(Path.Combine(dir, "en.json"));
        Assert.Equal(
            BuiltInCatalog.English.Entries.OrderBy(p => p.Key, StringComparer.Ordinal),
            english.Entries.OrderBy(p => p.Key, StringComparer.Ordinal));

        var problems = new List<string>();
        var checkedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("en.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".notes.json", StringComparison.OrdinalIgnoreCase)) continue;

            checkedFiles++;
            var parseWarnings = new List<string>();
            if (!LanguageCatalog.TryParse(File.ReadAllText(file), name, out var catalog, parseWarnings))
            {
                problems.AddRange(parseWarnings);
                continue;
            }
            problems.AddRange(parseWarnings);
            problems.AddRange(Localizer.Create(BuiltInCatalog.English, [catalog]).Warnings
                .Where(w => !w.Contains("does not use placeholder", StringComparison.Ordinal)));
        }

        Assert.True(checkedFiles > 0, "Expected at least one shipped translation (vi.json).");
        Assert.True(problems.Count == 0, "Shipped catalog problems:\n" + string.Join("\n", problems));
    }

    internal static List<string> ExtractTrKeys(string xaml)
    {
        var hits = TrMarkupKey.Matches(xaml).Concat(TrElementKey.Matches(xaml)).OrderBy(m => m.Index);
        return [.. hits.Select(m => m.Groups["key"].Value)];
    }

    /// <summary>Returns one "where=value" entry per literal UI text in the document.</summary>
    internal static List<string> FindXamlLiteralText(XDocument doc)
    {
        var found = new List<string>();
        foreach (var element in doc.Descendants())
        {
            var local = element.Name.LocalName;
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace != XNamespace.None) continue;
                if (XamlTextAttributes.Contains(attribute.Name.LocalName) && IsLiteralText(attribute.Value))
                {
                    found.Add($"{attribute.Name.LocalName}={attribute.Value}");
                }
            }

            if (local == "Setter" &&
                element.Attribute("Property")?.Value is { } property && XamlTextAttributes.Contains(property) &&
                element.Attribute("Value")?.Value is { } setterValue && IsLiteralText(setterValue))
            {
                found.Add($"Setter.{property}={setterValue}");
            }

            if (XamlTextElements.Contains(local))
            {
                foreach (var text in element.Nodes().OfType<XText>())
                {
                    var value = text.Value.Trim();
                    if (IsLiteralText(value)) found.Add($"{local}>{value}");
                }
            }
        }
        return found;
    }

    private static bool IsLiteralText(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed.StartsWith("{}", StringComparison.Ordinal)) trimmed = trimmed[2..].Trim(); // escaped literal
        else if (trimmed.StartsWith('{')) return false; // markup extension
        if (trimmed.Length == 0) return false;
        return !NeutralWords.Contains(trimmed) && !NeutralSymbols.IsMatch(trimmed);
    }

    private static bool ContainsVietnamese(string literal) => VietnameseLetter.IsMatch(literal);

    private static void AssertMatchesAllowlist(string root, string allowlistRelative, IReadOnlyDictionary<string, int> actual, string what)
    {
        var allowlistPath = Path.Combine(root, allowlistRelative);
        var allowed = ReadAllowlist(allowlistPath);
        var problems = new List<string>();
        foreach (var (file, count) in actual)
        {
            if (!allowed.TryGetValue(file, out var limit))
            {
                problems.Add($"NEW  {file}: {count} {what} (not allowed — move the text to the catalogs)");
            }
            else if (count > limit)
            {
                problems.Add($"MORE {file}: {count} {what}, allowlist says {limit} (move the new text to the catalogs)");
            }
            else if (count < limit)
            {
                problems.Add($"LESS {file}: {count} {what}, allowlist says {limit} — good! Lower the number in {allowlistRelative} to {count}");
            }
        }
        foreach (var (file, limit) in allowed)
        {
            if (!actual.ContainsKey(file))
            {
                problems.Add($"DONE {file}: 0 {what}, allowlist says {limit} — good! Remove the line from {allowlistRelative}");
            }
        }

        if (problems.Count == 0) return;
        var snapshot = new StringBuilder();
        foreach (var (file, count) in actual) snapshot.Append(file).Append('|').Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        Assert.Fail($"{allowlistRelative} does not match the source:\n{string.Join("\n", problems)}\n\n" +
                    $"Current snapshot ({actual.Count} files, {actual.Values.Sum()} total):\n{snapshot}");
    }

    private static Dictionary<string, int> ReadAllowlist(string path)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var bar = line.LastIndexOf('|');
            Assert.True(bar > 0 && int.TryParse(line.AsSpan(bar + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _),
                $"{path}: malformed line '{line}' (expected 'relative/path|count')");
            result[line[..bar].Trim()] = int.Parse(line.AsSpan(bar + 1), NumberStyles.None, CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static LanguageCatalog ParseCatalog(string path)
    {
        var warnings = new List<string>();
        Assert.True(LanguageCatalog.TryParse(File.ReadAllText(path), Path.GetFileName(path), out var catalog, warnings),
            string.Join("\n", warnings));
        return catalog;
    }

    private static IEnumerable<string> SourceFiles(string root, string relativeDir, string pattern)
    {
        var dir = Path.Combine(root, relativeDir);
        Assert.True(Directory.Exists(dir), $"Expected directory {dir}");
        return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
            .Where(f => !Relative(root, f).Split('/').Any(s => s is "bin" or "obj"))
            .Order(StringComparer.Ordinal);
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PhotoReview.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root (looking for PhotoReview.slnx)");
    }
}

/// <summary>
/// Minimal C# lexer that returns the text of every string literal (regular, verbatim, interpolated, raw,
/// and combinations) while skipping comments and char literals. Interpolation holes are scanned as code,
/// so a literal nested in a hole is returned on its own and the outer literal holds only its text parts.
/// Escape sequences are kept as written (the guard only looks for letters).
/// </summary>
internal static class CSharpStringLiterals
{
    public static List<string> Scan(string source)
    {
        var result = new List<string>();
        var i = 0;
        ScanCode(source, ref i, result, inHole: false);
        return result;
    }

    // Scans code; inside a hole, returns (without consuming) at the '}' that closes it.
    private static void ScanCode(string s, ref int i, List<string> result, bool inHole)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                var end = s.IndexOf('\n', i);
                i = end < 0 ? s.Length : end;
            }
            else if (c == '/' && next == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
            }
            else if (c == '\'')
            {
                SkipCharLiteral(s, ref i);
            }
            else if (c == '{')
            {
                depth++;
                i++;
            }
            else if (c == '}')
            {
                if (inHole && depth == 0) return;
                depth--;
                i++;
            }
            else if (!TryReadString(s, ref i, result))
            {
                i++;
            }
        }
    }

    private static void SkipCharLiteral(string s, ref int i)
    {
        i++;
        while (i < s.Length && s[i] != '\'' && s[i] != '\n')
        {
            i += s[i] == '\\' ? 2 : 1;
        }
        i++;
    }

    private static bool TryReadString(string s, ref int i, List<string> result)
    {
        var j = i;
        var dollars = 0;
        var verbatim = false;
        while (j < s.Length && (s[j] == '$' || s[j] == '@'))
        {
            if (s[j] == '$') dollars++;
            else verbatim = true;
            j++;
        }
        if (j >= s.Length || s[j] != '"') return false;

        var quotes = 0;
        while (j + quotes < s.Length && s[j + quotes] == '"') quotes++;

        var text = new StringBuilder();
        if (!verbatim && quotes >= 3)
        {
            j += quotes;
            ReadRaw(s, ref j, result, text, quotes, dollars);
        }
        else if (!verbatim && quotes == 2)
        {
            j += 2; // empty string
        }
        else
        {
            j++;
            ReadQuoted(s, ref j, result, text, verbatim, interpolated: dollars > 0);
        }

        result.Add(text.ToString());
        i = j;
        return true;
    }

    private static void ReadQuoted(string s, ref int j, List<string> result, StringBuilder text, bool verbatim, bool interpolated)
    {
        while (j < s.Length)
        {
            var c = s[j];
            var next = j + 1 < s.Length ? s[j + 1] : '\0';
            if (!verbatim && c == '\\')
            {
                text.Append(c);
                if (next != '\0') text.Append(next);
                j += 2;
            }
            else if (c == '"')
            {
                if (verbatim && next == '"')
                {
                    text.Append("\"\"");
                    j += 2;
                    continue;
                }
                j++;
                return;
            }
            else if (!verbatim && c == '\n')
            {
                return; // unterminated; resync on the next line
            }
            else if (interpolated && (c == '{' || c == '}') && next == c)
            {
                text.Append(c).Append(c);
                j += 2;
            }
            else if (interpolated && c == '{')
            {
                j++;
                ScanCode(s, ref j, result, inHole: true);
                j++; // the closing '}'
            }
            else
            {
                text.Append(c);
                j++;
            }
        }
    }

    private static void ReadRaw(string s, ref int j, List<string> result, StringBuilder text, int quotes, int dollars)
    {
        while (j < s.Length)
        {
            var c = s[j];
            if (c == '"')
            {
                var run = 0;
                while (j + run < s.Length && s[j + run] == '"') run++;
                if (run >= quotes)
                {
                    text.Append('"', run - quotes);
                    j += run;
                    return;
                }
                text.Append('"', run);
                j += run;
            }
            else if (dollars > 0 && c == '{')
            {
                var run = 0;
                while (j + run < s.Length && s[j + run] == '{') run++;
                if (run < dollars)
                {
                    text.Append('{', run);
                    j += run;
                    continue;
                }
                text.Append('{', run - dollars);
                j += run;
                ScanCode(s, ref j, result, inHole: true);
                var closing = 0;
                while (j < s.Length && s[j] == '}' && closing < dollars)
                {
                    j++;
                    closing++;
                }
            }
            else
            {
                text.Append(c);
                j++;
            }
        }
    }
}
