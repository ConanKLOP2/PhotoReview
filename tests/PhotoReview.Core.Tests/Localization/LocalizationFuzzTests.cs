using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;

namespace PhotoReview.Core.Tests.Localization;

/// <summary>Randomized, oracle-based and culture tests for the translation pipeline (untrusted catalog files).</summary>
[Collection("GlobalState")] // switches CurrentCulture / Localizer.Current
public sealed partial class LocalizationFuzzTests
{
    private static readonly string[] Pieces =
    [
        "{", "}", "{{", "}}", "a", "b1", " ", "é", "İ", "ı", "{x}", "{name}", "{count}", "{1}", "{ x}", "{}", "{x y}",
        "日本語", "مرحبا", "😀", "\n", "{{x}}", "{x}}", "{{x}", "%s", "{0}", "{X}", "{ñ}",
    ];

    private static LocTemplate Parse(string text)
    {
        Assert.True(LocTemplate.TryParse(text, out var template), text);
        return template;
    }

    private static string RandomTemplateText(Random r)
    {
        var sb = new StringBuilder();
        for (var i = r.Next(0, 9); i > 0; i--) sb.Append(Pieces[r.Next(Pieces.Length)]);
        return sb.ToString();
    }

    [GeneratedRegex(@"^(?:\{\{|\}\}|\{[A-Za-z][A-Za-z0-9]*\}|[^{}])*$", RegexOptions.Singleline)]
    private static partial Regex ValidTemplate();

    [GeneratedRegex(@"\{\{|\}\}|\{([A-Za-z][A-Za-z0-9]*)\}|[^{}]", RegexOptions.Singleline)]
    private static partial Regex Token();

    /// <summary>Independent reference renderer: literal braces from {{ }}, {name} from args, unknown names written back.</summary>
    private static string Reference(string text, Dictionary<string, string> args)
    {
        var sb = new StringBuilder();
        foreach (Match m in Token().Matches(text))
        {
            if (m.Value == "{{") sb.Append('{');
            else if (m.Value == "}}") sb.Append('}');
            else if (m.Groups[1].Success) sb.Append(args.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
            else sb.Append(m.Value);
        }
        return sb.ToString();
    }

    [Theory(DisplayName = "LocTemplate fuzz: parse validity and rendering match an independent regex reference")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void LocTemplate_MatchesReference(int seed)
    {
        var r = new Random(seed);
        var args = new Dictionary<string, string> { ["x"] = "<X>", ["name"] = "<N>", ["count"] = "<C>", ["X"] = "<BIG>" };
        var locArgs = args.Select(kv => new LocArg(kv.Key, kv.Value)).ToArray();

        for (var i = 0; i < 3000; i++)
        {
            var text = RandomTemplateText(r);
            var expectedValid = ValidTemplate().IsMatch(text);

            var ok = LocTemplate.TryParse(text, out var template);

            Assert.True(expectedValid == ok, $"'{text}' oracle={expectedValid} parser={ok}");
            if (!ok) continue;
            Assert.Equal(Reference(text, args), template.Render(locArgs));
            Assert.Equal(Reference(text, []), template.Render([]));

            // Identity transform and appending text keep the placeholders and stay parseable.
            var same = template.TransformLiterals(s => s);
            Assert.True(LocTemplate.TryParse(same.Text, out var reparsed), same.Text);
            Assert.Equal(template.PlaceholderNames, reparsed.PlaceholderNames);
            Assert.Equal(template.Render(locArgs), reparsed.Render(locArgs));
            var appended = template.AppendLiteral("}{ {x} ");
            Assert.True(LocTemplate.TryParse(appended.Text, out var appendedReparsed), appended.Text);
            Assert.Equal(template.Render(locArgs) + "}{ {x} ", appended.Render(locArgs));
            Assert.Equal(appended.Render(locArgs), appendedReparsed.Render(locArgs));
        }
    }

    [Fact(DisplayName = "Render never throws when an argument's ToString throws or returns null, or the value is a huge number")]
    public void Render_HostileArguments_DoNotThrowForFormatting()
    {
        var t = Parse("[{a}] [{b}] [{c}]");

        var text = t.Render([new LocArg("a", null), new LocArg("b", new NullToString()), new LocArg("c", decimal.MinValue)]);

        Assert.Equal("[] [] [" + decimal.MinValue.ToString(null, CultureInfo.CurrentCulture) + "]", text);
    }

    private sealed class NullToString
    {
        public override string? ToString() => null;
    }

    [Theory(DisplayName = "Numbers in a rendered string follow the UI culture (decimal separator, digits), the template text does not")]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("")]
    public void Render_NumbersUseCurrentCulture(string culture)
    {
        var t = Parse("{n} | {n2} | {date}");
        var ci = CultureInfo.GetCultureInfo(culture);
        var date = new DateTime(2026, 9, 26, 13, 5, 0, DateTimeKind.Utc);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = ci;

            var text = t.Render([new LocArg("n", 1234.5), new LocArg("n2", 1234L), new LocArg("date", date)]);

            Assert.Equal(1234.5.ToString(null, ci) + " | " + 1234L.ToString(null, ci) + " | " + date.ToString(null, ci), text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact(DisplayName = "Rendering is thread safe: parallel renders of different templates never mix their output")]
    public void Render_ParallelThreads_NoCrossTalk()
    {
        var templates = Enumerable.Range(0, 8).Select(i =>
        {
            var t = Parse("T" + i + "-{a}-" + new string((char)('a' + i), 40) + "-{b}");
            return t;
        }).ToArray();

        var failures = new List<string>();
        Parallel.For(0, 8, i =>
        {
            var expected = "T" + i + "-A" + i + "-" + new string((char)('a' + i), 40) + "-B" + i;
            for (var n = 0; n < 20_000; n++)
            {
                var got = templates[i].Render([new LocArg("a", "A" + i), new LocArg("b", "B" + i)]);
                if (got != expected) { lock (failures) failures.Add(got); return; }
            }
        });

        Assert.Empty(failures);
    }

    [Fact(DisplayName = "A nested render (an argument whose ToString renders another template) does not corrupt the outer text")]
    public void Render_Nested_KeepsOuterText()
    {
        var outer = Parse("outer[{inner}]end");
        var inner = Parse("in{v}ner");

        var text = outer.Render([new LocArg("inner", new RendersTemplate(inner))]);

        Assert.Equal("outer[in<V>ner]end", text);
    }

    private sealed class RendersTemplate(LocTemplate template)
    {
        public override string ToString() => template.Render([new LocArg("v", "<V>")]);
    }

    // ---- catalogs ----

    private static string ValidCatalogJson =>
        """
        {
          "_meta": { "code": "xx", "name": "Test", "nativeName": "Tést", "plural": "one-other", "authors": ["a"] },
          "_comment": "c",
          "plain": "Plain",
          "greet": "Hello {name}",
          "files.one": "{count} file",
          "files.other": "{count} files"
        }
        """;

    [Theory(DisplayName = "Fuzz: byte-flipped and truncated catalog files never throw from TryParse or Localizer.Create")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Catalog_CorruptedBytes_NeverThrow(int seed)
    {
        var r = new Random(seed);
        var original = Encoding.UTF8.GetBytes(ValidCatalogJson);
        for (var i = 0; i < 1500; i++)
        {
            var bytes = (byte[])original.Clone();
            switch (r.Next(3))
            {
                case 0: bytes[r.Next(bytes.Length)] = (byte)r.Next(256); break;
                case 1: bytes = bytes[..r.Next(bytes.Length)]; break;
                default:
                    for (var k = 0; k < 4; k++) bytes[r.Next(bytes.Length)] = (byte)r.Next(256);
                    break;
            }
            var text = Encoding.UTF8.GetString(bytes);
            var warnings = new List<string>();

            var ex = Record.Exception(() =>
            {
                if (LanguageCatalog.TryParse(text, "fuzz.json", out var catalog, warnings))
                {
                    var localizer = Localizer.Create(LocTestCatalogs.English, [catalog]);
                    foreach (var key in localizer.Keys) _ = localizer.Get(key);
                    _ = localizer.FormatPlural("files", 1, new LocArg("count", 1));
                }
            });

            Assert.True(ex is null, text + "\n" + ex);
        }
    }

    [Fact(DisplayName = "A hostile overlay cannot inject keys, placeholders or braces into the merged localizer")]
    public void Overlay_Hostile_IsContained()
    {
        var overlay = LocTestCatalogs.Overlay("xx",
            """
            "greet": "Hi {name} {evil}", "move": "{{{count}}}", "plain": "}{", "files.one": "{count", "not.a.key": "x", "_x": "y",
            "files.other": "{count} {{files}}"
            """);

        var localizer = Localizer.Create(LocTestCatalogs.English, [overlay]);

        Assert.False(localizer.Contains("not.a.key"));
        Assert.Equal("Hello Ann", localizer.Format("greet", new LocArg("name", "Ann")));      // unknown placeholder: English kept
        Assert.Equal("{5}", localizer.Format("move", new LocArg("count", 5), new LocArg("folder", "f"))); // escaped braces around a placeholder
        Assert.Equal("Plain text", localizer.Get("plain"));                                    // unbalanced braces: English kept
        Assert.Equal("1 file", localizer.FormatPlural("files", 1, new LocArg("count", 1)));    // broken .one: English kept
        Assert.Equal("3 {files}", localizer.FormatPlural("files", 3, new LocArg("count", 3)));
        Assert.Contains(localizer.Warnings, w => w.Contains("not.a.key", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "FormatPlural handles extreme and negative counts for both plural rules")]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(long.MaxValue)]
    public void FormatPlural_ExtremeCounts(long count)
    {
        var oneOther = Localizer.Create(LocTestCatalogs.English, []);
        var none = Localizer.Create(LocTestCatalogs.English, [LocTestCatalogs.Overlay("xx", "", plural: "none")]);

        var a = oneOther.FormatPlural("files", count, new LocArg("count", count));
        var b = none.FormatPlural("files", count, new LocArg("count", count));

        Assert.Equal(count == 1 ? "1 file" : count.ToString("G", CultureInfo.CurrentCulture) + " files", a);
        Assert.Equal(count.ToString("G", CultureInfo.CurrentCulture) + " files", b);
        Assert.Equal("missing.other", oneOther.FormatPlural("missing", count));
    }

    [Fact(DisplayName = "A catalog with 50 000 keys and a 900 KB value parses and merges")]
    public void Catalog_Large_Loads()
    {
        var sb = new StringBuilder("{ \"_meta\": {\"code\":\"xx\"}, \"big\": \"").Append('y', 900_000).Append('"');
        for (var i = 0; i < 50_000; i++) sb.Append(",\"k").Append(i).Append("\":\"v").Append(i).Append('"');
        sb.Append('}');

        Assert.True(LanguageCatalog.TryParse(sb.ToString(), "big.json", out var catalog, new List<string>()));
        var localizer = Localizer.Create(LocTestCatalogs.English, [catalog]);

        Assert.Equal("Plain text", localizer.Get("plain"));
        Assert.Equal(50_001, catalog.Entries.Count);
    }

    [Theory(DisplayName = "Language codes are lower-cased culture-invariantly (Turkish I) and resolve case-insensitively")]
    [InlineData("tr-TR")]
    [InlineData("en-US")]
    public void CatalogCode_TurkishI(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var warnings = new List<string>();

            Assert.True(LanguageCatalog.TryParse("{\"_meta\":{\"code\":\"ID\"}}", "s", out var id, warnings));
            Assert.True(LanguageCatalog.TryParse("{\"_meta\":{\"code\":\"IT\"}}", "s", out var it, warnings));

            Assert.Equal("id", id.Code);
            Assert.Equal("it", it.Code);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory(DisplayName = "Every shipped catalog renders every key with sample arguments and as pseudo-locale in every culture")]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    [InlineData("vi-VN")]
    public void ShippedCatalogs_RenderEverywhere(string culture)
    {
        var loader = new LanguageLoader(new PhysicalFileSystem(), Path.Combine(AppContext.BaseDirectory, "Languages"), null);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            foreach (var code in loader.DiscoverLanguages().Select(l => l.Code))
            {
                var localizer = loader.Load(code);
                var sources = new[] { localizer, TranslatorModes.CreatePseudo(localizer), TranslatorModes.CreateShowKeys(localizer) };
                foreach (var source in sources)
                {
                    foreach (var key in source.Keys)
                    {
                        var args = new[]
                        {
                            new LocArg("count", 1234567L), new LocArg("name", "日本語 مرحبا 😀"), new LocArg("path", @"C:\x\y"),
                            new LocArg("value", 3.14159), new LocArg("folder", "f"), new LocArg("size", 1024L), new LocArg("index", 3),
                        };
                        var text = source.Format(key, args);
                        Assert.False(text.Contains("{{", StringComparison.Ordinal) || text.Contains("}}", StringComparison.Ordinal), $"{code}:{key} -> {text}");
                    }
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact(DisplayName = "Pseudo-locale keeps RTL, CJK and surrogate pairs intact and every placeholder renderable")]
    public void Pseudo_KeepsExoticText()
    {
        var english = LocTestCatalogs.Overlay("en", """"
            "rtl": "\u0645\u0631\u062D\u0628\u0627 {name}", "cjk": "\u65E5\u672C\u8A9E {name} \u3067\u3059", "emoji": "\ud83d\ude00 {name} \ud83d\ude00"
            """");
        var localizer = Localizer.Create(english, []);

        var pseudo = TranslatorModes.CreatePseudo(localizer);

        foreach (var key in new[] { "rtl", "cjk", "emoji" })
        {
            var text = pseudo.Format(key, new LocArg("name", "N"));
            Assert.StartsWith("[", text, StringComparison.Ordinal);
            Assert.EndsWith("]", text, StringComparison.Ordinal);
            Assert.Contains("N", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\uFFFD', text);
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i])) Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), key);
            }
        }
    }
}
