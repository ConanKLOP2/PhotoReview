using System.Text.RegularExpressions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// APP-03: colours belong to the theme dictionaries (<c>Themes/Dark*.xaml</c>), not to individual
/// windows. A window that needs a colour references a <c>{DynamicResource Dark.*}</c> brush, so
/// retuning the palette (or adding a theme) is a one-file change.
/// </summary>
public sealed class XamlThemeRulesTests
{
    private const string AppDir = "src/PhotoReview.App";
    private const string ThemesDir = "src/PhotoReview.App/Themes/";
    private const string AllowlistFile = "tests/PhotoReview.Architecture.Tests/xaml-hex-allowlist.txt";

    // #RGB, #ARGB, #RRGGBB and #AARRGGBB literals.
    private const string HexColor = @"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3,4})";

    private static readonly Regex QuotedHex = new(
        "[\"'>]\\s*(" + HexColor + ")\\s*[\"'<]", RegexOptions.CultureInvariant);

    [Fact]
    public void NoHexColorLiteralsOutsideTheme()
    {
        var root = RepoScan.Root;
        var allowed = ReadAllowlist(Path.Combine(root, AllowlistFile));
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, AppDir), "*.xaml", SearchOption.AllDirectories))
        {
            var relative = RepoScan.Relative(file);
            if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.Contains("/bin/", StringComparison.Ordinal) ||
                relative.StartsWith(ThemesDir, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (line, hex) in FindHexLiterals(File.ReadAllText(file)))
            {
                if (!allowed.Contains($"{relative}|{hex}".ToUpperInvariant()))
                    violations.Add($"{relative}:{line}: {hex}");
            }
        }

        Assert.True(violations.Count == 0,
            "Hex colour literals found outside Themes/. Use a {DynamicResource Dark.*} brush (add one to " +
            "Themes/DarkPalette.xaml if none fits) or list a justified exception in " + AllowlistFile + ":\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void Rule_DetectsAttributeAndSetterLiterals()
    {
        var xaml = "<Grid Background=\"#123456\">\n<Setter Property=\"Foreground\" Value=\"#80FFFFFF\"/>\n" +
                   "<Border Background=\"{DynamicResource Dark.Panel}\" Tag=\"a#b\"/></Grid>";
        var hits = FindHexLiterals(xaml).ToList();
        Assert.Equal(2, hits.Count);
        Assert.Equal((1, "#123456"), hits[0]);
        Assert.Equal((2, "#80FFFFFF"), hits[1]);
    }

    private static IEnumerable<(int Line, string Hex)> FindHexLiterals(string xaml)
    {
        // Only a value that is a colour literal on its own counts; this keeps things like "a#b" or an
        // XML entity (&#x41;) out of the rule.
        foreach (Match m in QuotedHex.Matches(xaml))
        {
            var group = m.Groups[1];
            var line = xaml.AsSpan(0, group.Index).Count('\n') + 1;
            yield return (line, group.Value);
        }
    }

    private static HashSet<string> ReadAllowlist(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Select(l => l.ToUpperInvariant())
                .ToHashSet(StringComparer.Ordinal)
            : [];
}
