using System.IO;
using System.Text.Json;
using PhotoReview.PerfAnalysis;

namespace PhotoReview.Integration.Tests;

/// <summary>Wave 2 tooling-a PerfAnalysis findings PA-01, PA-02 and PA-05.</summary>
public sealed class PerfAnalyzeHardeningTests : IDisposable
{
    private readonly TempRoot _root = new("perf-hardening");

    public void Dispose() => _root.Dispose();

    private static PerfRow R(long t, string ev, string a = "", string text = "") =>
        new(t, t, 1, ev, "7", "p", a, "", "", "", text);

    [Fact(DisplayName = "PA-01: the Decode event is not reduced by the separate SourceRead event (DecodeFromSource logs the read before it starts the decode clock)")]
    public void DecodeEvent_IsNotReducedByTheSourceReadEvent()
    {
        var file = new PerfCsvFile
        {
            Path = "perf-x.csv", DiagFlags = new Dictionary<string, string>(), QpcFrequency = 1_000_000, DroppedRows = 0, // 1 tick = 1 us
            Rows = [R(0, "ShowStart", text: "Fast"), R(1, "Lookup", text: "miss"),
                    R(2_000, "SourceRead", a: "30"), R(3_000, "Decode", a: "40"),
                    R(90_000, "Presented", text: "final")],
        };

        var nav = PerfAnalyzeNavBuilder.Build(file).Navs.Single();

        Assert.Equal(30, nav.TReadMs);
        Assert.Equal(40, nav.TDecodeMs);
    }

    [Fact(DisplayName = "PA-01: a Decode shorter than its read is no longer floored to zero")]
    public void DecodeEvent_ShorterThanTheRead_IsKept()
    {
        var file = new PerfCsvFile
        {
            Path = "perf-x.csv", DiagFlags = new Dictionary<string, string>(), QpcFrequency = 1_000_000, DroppedRows = 0,
            Rows = [R(0, "ShowStart", text: "Fast"), R(1, "Lookup", text: "miss"),
                    R(2_000, "SourceRead", a: "50"), R(3_000, "Decode", a: "20"),
                    R(90_000, "Presented", text: "final")],
        };

        Assert.Equal(20, PerfAnalyzeNavBuilder.Build(file).Navs.Single().TDecodeMs);
    }

    [Fact(DisplayName = "PA-02: LowSampleWarning counts only the complete navigations that back the percentiles")]
    public void LowSampleWarning_CountsOnlyCompleteNavs()
    {
        var navs = Enumerable.Range(0, 25)
            .Select(i => new NavRecord { Nav = i + 1, Incomplete = i >= 10, FinalVisualMs = 10, FirstVisualMs = 5 })
            .ToList();

        var summary = GroupSummary.Build(new GroupKey("S2", "Fast", "warm", 2), navs);

        Assert.Equal(25, summary.Count);
        Assert.Equal(15, summary.Incomplete);
        Assert.True(summary.LowSampleWarning);

        var enough = GroupSummary.Build(new GroupKey("S2", "Fast", "warm", 2),
            [.. Enumerable.Range(0, 20).Select(i => new NavRecord { Nav = i + 1, Incomplete = false, FinalVisualMs = 10, FirstVisualMs = 5 })]);
        Assert.False(enough.LowSampleWarning);
    }

    [Fact(DisplayName = "PA-05: summary.json uses camelCase keys for the folder, startup and rules arrays too")]
    public void SummaryJson_FolderStartupAndRulesKeys_AreCamelCase()
    {
        var summary = GroupSummary.Build(new GroupKey("S1", "Fast", "cold", 2),
            [new NavRecord { Nav = 1, FinalVisualMs = 10, FirstVisualMs = 5 }]);
        summary.FolderGens.Add(new FolderGenSummary { Gen = 3, T1CatalogReadyMs = 1.5, T2Ms = 2.5, T3Ms = 3.5, T3Phase = "x" });
        summary.StartupRuns.Add(new Dictionary<string, double> { ["phaseA"] = 12 });
        var result = new PerfAnalyze.AnalysisResult { CsvFileCount = 1 };
        result.Groups.Add(new PerfAnalyze.GroupResult { Summary = summary, Rules = [new RuleResult("R-X", true, "evidence", "note")] });
        var path = _root.Combine("summary.json");

        PerfAnalyzeReport.WriteJson(path, result);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var group = doc.RootElement.GetProperty("groups")[0];
        var folder = group.GetProperty("folder")[0];
        Assert.Equal(3, folder.GetProperty("gen").GetInt64());
        Assert.Equal(1.5, folder.GetProperty("t1CatalogReadyMs").GetDouble());
        Assert.Equal(2.5, folder.GetProperty("t2Ms").GetDouble());
        Assert.Equal(3.5, folder.GetProperty("t3Ms").GetDouble());
        Assert.Equal("x", folder.GetProperty("t3Phase").GetString());
        var startup = group.GetProperty("startup")[0];
        Assert.Equal("phaseA", startup.GetProperty("phase").GetString());
        Assert.Equal(12, startup.GetProperty("median").GetDouble());
        Assert.Equal(1, startup.GetProperty("count").GetInt32());
        var rule = group.GetProperty("rules")[0];
        Assert.Equal("R-X", rule.GetProperty("rule").GetString());
        Assert.True(rule.GetProperty("triggered").GetBoolean());
        Assert.Equal("evidence", rule.GetProperty("evidence").GetString());
        Assert.Equal("note", rule.GetProperty("note").GetString());
        // No PascalCase leftovers anywhere in the group object.
        Assert.All(group.EnumerateObject(), p => Assert.True(char.IsLower(p.Name[0]), p.Name));
    }
}
