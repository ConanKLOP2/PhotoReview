using System.Text;
using System.Text.Json;
using PhotoReview.Localization.Generator;

namespace PhotoReview.Core.Tests.Localization;

/// <summary>
/// The compile-time generator (FlatJsonReader) and the runtime loader (System.Text.Json) must agree on which catalogs are
/// valid and on what they contain: a file the generator accepts but the runtime rejects would break the build's promise
/// that every Tr member exists at run time.
/// </summary>
public sealed class FlatJsonReaderDifferentialTests
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>What System.Text.Json says about the file: entries when it is a usable flat catalog, null when not.</summary>
    private static List<(string Key, string Value)>? Oracle(string text)
    {
        try
        {
            // The runtime reads the file through a StreamReader, which strips a BOM; the generator's SourceText does too.
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];
            using var doc = JsonDocument.Parse(text, Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var entries = new List<(string, string)>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.StartsWith('_')) continue;
                if (p.Value.ValueKind != JsonValueKind.String) return null;
                entries.Add((p.Name, p.Value.GetString()!));
            }
            return entries;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static List<(string Key, string Value)>? Generator(string text)
    {
        try
        {
            return FlatJsonReader.Read(text).Select(e => (e.Key, e.Value)).ToList();
        }
        catch (FlatJsonException)
        {
            return null;
        }
    }

    private static readonly string[] Tokens =
    [
        "{", "}", "[", "]", ",", ":", "\"", "\"a\"", "\"b\"", "\"_m\"", "\"key.one\"", "\"v\"", "\"\\n\"", "\"\\u0041\"", "\"\\ud83d\\ude00\"",
        "\"\\ud800\"", "\"\\udc00x\"", "\"\\uD83D\"", "\"\\u00\"", "\"\\x\"", "\"tab\there\"", "1", "01", "-", "-0", "1.", "1.5e+3", "1e", "true", "tru", "null", "nul",
        " ", "\n", "\t", "// c\n", "/* c */", "/*", "//", "\uFEFF", "\u00A0", "\u2028", "\0", "'", "a", "日本", "\ud83d\ude00", "\r", "\u2029",
    ];

    /// <summary>Printable form of a test input (control characters and lone surrogates shown as \uXXXX).</summary>
    private static string Escape(string text) =>
        string.Concat(text.Select(c => c is < ' ' or > '~' ? "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) : c.ToString()));

    private static string Soup(Random r)
    {
        var sb = new StringBuilder();
        if (r.Next(4) != 0) sb.Append('{');
        for (var i = r.Next(0, 14); i > 0; i--) sb.Append(Tokens[r.Next(Tokens.Length)]);
        if (r.Next(3) != 0) sb.Append('}');
        return sb.ToString();
    }

    private const string Valid = """
        {
          // comment
          "_meta": { "code": "en", "n": [1, 2.5e3, -0, true, null, {"x": []}] },
          "a.b": "Hello {name}", /* block */
          "c": "\u00e9\ud83d\ude00\n\"q\"",
        }
        """;

    private static string MutateValid(Random r)
    {
        var chars = Valid.ToCharArray();
        var sb = new StringBuilder(Valid);
        for (var k = r.Next(1, 4); k > 0; k--)
        {
            var i = r.Next(sb.Length);
            switch (r.Next(3))
            {
                case 0: sb.Remove(i, 1); break;
                case 1: sb.Insert(i, Tokens[r.Next(Tokens.Length)]); break;
                default: sb[i] = chars[r.Next(chars.Length)]; break;
            }
        }
        return sb.ToString();
    }

    [Fact(DisplayName = "The valid sample is accepted by both readers with identical entries")]
    public void Valid_BothAccept()
    {
        var a = Oracle(Valid);
        var b = Generator(Valid);

        Assert.NotNull(a);
        Assert.Equal(a, b);
    }

    [Theory(DisplayName = "Differential fuzz: generator and System.Text.Json agree on validity and content of token-soup files")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Soup_Agrees(int seed)
    {
        var r = new Random(seed);
        var mismatches = new List<string>();
        for (var i = 0; i < 20_000; i++)
        {
            var text = i % 2 == 0 ? Soup(r) : MutateValid(r);
            var expected = Oracle(text);
            var actual = Generator(text);
            if (expected is null != actual is null || (expected is not null && !expected.SequenceEqual(actual!)))
            {
                mismatches.Add($"oracle={(expected is null ? "reject" : "accept")} generator={(actual is null ? "reject" : "accept")} :: {Escape(text)}");
                if (mismatches.Count >= 10) break;
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact(DisplayName = "Deeply nested _meta values and huge files are rejected or read without a stack overflow")]
    public void Deep_And_Huge()
    {
        var deep = "{\"_x\":" + new string('[', 5000) + new string(']', 5000) + "}";
        Assert.Null(Generator(deep));
        Assert.Null(Oracle(deep));

        var big = "{" + string.Join(",", Enumerable.Range(0, 100_000).Select(i => "\"k" + i + "\":\"v\"")) + "}";
        Assert.Equal(100_000, Generator(big)!.Count);
    }
}
