using System.Collections.Immutable;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using PhotoReview.Core.Localization;
using PhotoReview.Localization.Generator;

namespace PhotoReview.Core.Tests.Localization;

/// <summary>
/// Drives the i18n source generator directly. It had no tests of its own: a broken catalog must become a build error
/// (never silently wrong code), and whatever it generates must compile.
/// </summary>
public sealed class LocalizationGeneratorTests
{
    private sealed class MemoryText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text, Encoding.UTF8);
    }

    private static readonly ImmutableArray<MetadataReference> References =
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(System.IO.Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => MetadataReference.CreateFromFile(p))];

    private sealed record Result(ImmutableArray<Diagnostic> Generator, string? Source, ImmutableArray<Diagnostic> CompileErrors);

    private static Result Run(string enJson, string fileName = "en.json")
    {
        var compilation = CSharpCompilation.Create(
            "GeneratorHarness",
            [],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create(new TrGenerator())
            .AddAdditionalTexts([new MemoryText(@"C:\x\Languages\" + fileName, enJson)])
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var source = driver.GetRunResult().GeneratedTrees.SingleOrDefault()?.ToString();
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        return new Result(diagnostics, source, errors);
    }

    private static string Catalog(params string[] entries) => "{ \"_meta\": {\"code\":\"en\"}, " + string.Join(", ", entries) + " }";

    private static IEnumerable<string> Ids(Result r) => r.Generator.Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal);

    [Fact(DisplayName = "The shipped en.json generates code that compiles, with one TrKeys constant per key")]
    public void ShippedCatalog_GeneratesCompilableCode()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Languages", "en.json");
        var json = File.ReadAllText(path);

        var result = Run(json);

        Assert.Empty(result.Generator);
        Assert.NotNull(result.Source);
        Assert.Empty(result.CompileErrors.Where(d => d.Id != "CS0436").Select(d => d.ToString()));
        var keys = BuiltInCatalog.English.Entries.Keys;
        Assert.All(keys, k => Assert.Contains("\"" + k + "\";", result.Source, StringComparison.Ordinal));
    }

    [Theory(DisplayName = "A broken catalog is reported as the documented build error and not silently generated")]
    [InlineData("{", "PRLOC001")]
    [InlineData("[]", "PRLOC001")]
    [InlineData("{\"a\": 1}", "PRLOC001")]
    [InlineData("{\"a\": \"x\",}", null)]
    [InlineData("{\"1abc\": \"x\"}", "PRLOC002")]
    [InlineData("{\"a..b\": \"x\"}", "PRLOC002")]
    [InlineData("{\"a.\": \"x\"}", "PRLOC002")]
    [InlineData("{\"a-b\": \"x\"}", "PRLOC002")]
    [InlineData("{\"é\": \"x\"}", "PRLOC002")]
    [InlineData("{\"\": \"x\"}", "PRLOC002")]
    [InlineData("{\"a.b\": \"x\", \"aB\": \"y\"}", "PRLOC003")]
    [InlineData("{\"tr\": \"x\"}", "PRLOC003")]
    [InlineData("{\"equals\": \"x\"}", "PRLOC003")]
    [InlineData("{\"a\": \"x\", \"a\": \"y\"}", "PRLOC006")]
    [InlineData("{\"a\": \"{oops\"}", "PRLOC004")]
    [InlineData("{\"a\": \"}\"}", "PRLOC004")]
    [InlineData("{\"a\": \"{1}\"}", "PRLOC004")]
    [InlineData("{\"a\": \"{ x}\"}", "PRLOC004")]
    [InlineData("{\"a.one\": \"x\"}", "PRLOC005")]
    public void BrokenCatalog_ReportsDiagnostic(string json, string? expectedId)
    {
        var result = Run(json);

        if (expectedId is null) Assert.Empty(result.Generator);
        else Assert.Contains(expectedId, Ids(result));
        Assert.Empty(result.CompileErrors.Where(d => d.Id != "CS0436").Select(d => d.ToString()));
    }

    [Fact(DisplayName = "Awkward but valid text and placeholders (keywords, quotes, XML, line separators) generate compilable code")]
    public void AwkwardText_StillCompiles()
    {
        var json = Catalog(
            "\"k.class\": \"Use {class} and {default} and {this} and {int}\"",
            "\"k.quotes\": \"He said \\\"hi\\\" \\\\ back\"",
            "\"k.xml\": \"</summary> & <b> ]]> */ /* {{literal}}\"",
            "\"k.lines\": \"a\\u2028b\\u0085c\\u2029d\\r\\ne\\nf\"",
            "\"k.unicode\": \"\\ud83d\\ude00 \\u65e5\\u672c \\u0645\\u0631\\u062d\\u0628\\u0627\"",
            "\"k.same\": \"{a} {a} {A} {a1}\"",
            "\"files.one\": \"{count} file {extra}\"",
            "\"files.other\": \"{count} files\"",
            "\"k.count\": \"{count}\"");

        var result = Run(json);

        Assert.Empty(result.Generator);
        Assert.Empty(result.CompileErrors.Where(d => d.Id != "CS0436").Select(d => d.ToString()));
        Assert.Contains("object? @class", result.Source, StringComparison.Ordinal);
        Assert.Contains("FormatPlural", result.Source, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A file not named en.json is ignored and a 5 000-key catalog still generates compilable code")]
    public void OtherFileIgnored_AndLargeCatalogCompiles()
    {
        Assert.Null(Run(Catalog("\"a\": \"x\""), fileName: "vi.json").Source);

        var entries = Enumerable.Range(0, 5000).Select(i => $"\"group{i % 50}.item{i}\": \"Text {{n}} {i}\"").ToArray();
        var result = Run(Catalog(entries));

        Assert.Empty(result.Generator);
        Assert.Empty(result.CompileErrors.Where(d => d.Id != "CS0436").Select(d => d.ToString()));
    }

    [Theory(DisplayName = "Differential fuzz: the generator accepts exactly the texts LocTemplate accepts, with the same placeholders")]
    [InlineData(1)]
    [InlineData(2)]
    public void Placeholders_AgreeWithRuntimeTemplateParser(int seed)
    {
        var r = new Random(seed);
        string[] pieces = ["{", "}", "{{", "}}", "a", " ", "é", "{x}", "{name}", "{count}", "{1}", "{ x}", "{}", "{x y}", "{X}", "{ñ}", "日本", "{a1}"];
        var texts = Enumerable.Range(0, 1500)
            .Select(_ => string.Concat(Enumerable.Range(0, r.Next(0, 8)).Select(_ => pieces[r.Next(pieces.Length)])))
            .ToList();
        // One key per text; JSON-escape via System.Text.Json so any character is representable.
        var entries = texts.Select((t, i) => $"\"t.k{i}\": {System.Text.Json.JsonSerializer.Serialize(t)}").ToArray();

        var result = Run(Catalog(entries));

        var rejected = result.Generator.Where(d => d.Id == "PRLOC004")
            .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Select(m => System.Text.RegularExpressions.Regex.Match(m, "Text of 't\\.k(\\d+)'").Groups[1].Value)
            .Select(int.Parse)
            .ToHashSet();
        for (var i = 0; i < texts.Count; i++)
        {
            var runtimeAccepts = LocTemplate.TryParse(texts[i], out _);
            Assert.True(runtimeAccepts != rejected.Contains(i), $"'{texts[i]}' runtime={runtimeAccepts} generatorRejected={rejected.Contains(i)}");
        }
        Assert.Empty(result.Generator.Where(d => d.Id != "PRLOC004"));
    }
}
