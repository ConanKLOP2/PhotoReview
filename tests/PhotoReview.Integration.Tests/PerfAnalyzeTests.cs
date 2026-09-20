using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PhotoReview.PerfAnalysis;

namespace PhotoReview.Integration.Tests;

/// <summary>Tests for D11 (`--perf-analyze`): CSV parsing, nav reassembly/classification,
/// percentiles, phase-share weighting, and the R-* decision rules against synthetic aggregates.</summary>
public class PerfAnalyzeTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "PhotoReview.slnx"))) return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate repo root (PhotoReview.slnx) from " + AppContext.BaseDirectory);
    }

    private static string SampleCsvPath() => Path.Combine(RepoRoot(), "tools", "diag", "samples", "perf-sample.csv");

    // ---- CSV parsing ----

    [Fact]
    public void ParsesDataRowsAndSkipsCommentsAndHeader()
    {
        var file = PerfCsvReader.Read(SampleCsvPath());

        Assert.Equal(10000000, file.QpcFrequency);
        Assert.Equal(0, file.DroppedRows);
        Assert.Equal(75, file.Rows.Count);
        Assert.DoesNotContain(file.Rows, r => r.Event.StartsWith('#'));
        Assert.DoesNotContain(file.Rows, r => r.Event == "utcTicks");
    }

    [Fact]
    public void ReadsDroppedCountFromTrailerComment()
    {
        var path = Path.Combine(Path.GetTempPath(), "PhotoReview-PerfAnalyze-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var csv = Path.Combine(path, "perf-1-x.csv");
        File.WriteAllText(csv,
            "# commit=test diag= qpcFrequency=5000000\n" +
            "utcTicks,qpcTicks,thread,event,nav,pathId,a,b,c,d,text\n" +
            "1,1000,1,ShowStart,1,,0,,,,Preview\n" +
            "# dropped=7\n");

        var file = PerfCsvReader.Read(csv);

        Assert.Equal(5000000, file.QpcFrequency);
        Assert.Equal(7, file.DroppedRows);
        Assert.Single(file.Rows);
    }

    // ---- Nav classification against the hand-built sample ----

    [Fact]
    public void ClassifiesEveryNavInTheSampleCsv()
    {
        var file = PerfCsvReader.Read(SampleCsvPath());
        var analysis = PerfAnalyzeNavBuilder.Build(file);

        Assert.Equal(8, analysis.Navs.Count);
        var byNav = analysis.Navs.ToDictionary(n => n.Nav);

        Assert.Equal("RamHit", byNav[1].Kind);
        Assert.Equal("RamHit", byNav[2].Kind);
        Assert.Equal("RamHit", byNav[3].Kind);
        Assert.Equal("SourceMiss", byNav[4].Kind);
        Assert.False(byNav[4].HasThumbnail, "nav 4 (Fast mode) must not show a thumbnail first");
        Assert.Equal("Thumbnail+SourceMiss", byNav[5].Kind);
        Assert.True(byNav[5].HasThumbnail);
        Assert.Equal("DiskCacheHit", byNav[6].Kind);
        Assert.Equal("InflightJoin", byNav[7].Kind);

        // nav 8 is superseded mid-decode: no Verify/Assign/Presented ever arrives for it.
        Assert.True(byNav[8].Incomplete);
        Assert.Equal(1, analysis.Navs.Count(n => n.Incomplete));
    }

    [Fact]
    public void PrefersTheSpecificThumbnailCacheSourceOverMainWindowsUnknownTotal()
    {
        var file = PerfCsvReader.Read(SampleCsvPath());
        var analysis = PerfAnalyzeNavBuilder.Build(file);
        var nav5 = analysis.Navs.Single(n => n.Nav == 5);

        // The CSV carries ThumbEnd(source=decode, ms=20.0) from ThumbnailCache followed by
        // ThumbEnd(source=unknown, ms=20.5) from MainWindow's own call-site total; D11 must keep
        // the specific one.
        Assert.Equal("decode", nav5.ThumbSource);
        Assert.Equal(20.0, nav5.TThumbMs);
    }

    [Fact]
    public void MatchesKeyInputToShowStartAndComputesFirstAndFinalVisual()
    {
        var file = PerfCsvReader.Read(SampleCsvPath());
        var analysis = PerfAnalyzeNavBuilder.Build(file);
        var nav1 = analysis.Navs.Single(n => n.Nav == 1);

        Assert.Equal(5.0, nav1.TInputMs);
        Assert.Equal(2.0, nav1.TPreMs!.Value, 3);
        Assert.Equal(10.1, nav1.FinalVisualMs!.Value, 3);
        Assert.Equal(nav1.FinalVisualMs, nav1.FirstVisualMs);

        var nav5 = analysis.Navs.Single(n => n.Nav == 5);
        Assert.Equal(6.0, nav5.FirstVisualMs!.Value, 3);
        Assert.Equal(63.05, nav5.FinalVisualMs!.Value, 3);
        Assert.Equal("thumbnail", "thumbnail"); // first-visual is the thumbnail Presented row
        Assert.Equal("final", nav5.FinalPresentedKind);
    }

    [Fact]
    public void BuildsFolderT0ToT2FromTheEarliestPresentedAfterFolderStart()
    {
        var file = PerfCsvReader.Read(SampleCsvPath());
        var analysis = PerfAnalyzeNavBuilder.Build(file);
        var gen1 = analysis.FolderGens.Single(g => g.Gen == 1);

        Assert.Equal(120.0, gen1.T1CatalogReadyMs);
        Assert.Equal("explorerApplied", gen1.T3Phase);
        Assert.Equal(180.0, gen1.T3Ms);
        Assert.Equal(210.1, gen1.T2Ms!.Value, 3);
    }

    [Fact]
    public void AggregatesPreloadItemsDispatcherAndPausedCounters()
    {
        var file = PerfCsvReader.Read(SampleCsvPath());
        var analysis = PerfAnalyzeNavBuilder.Build(file);

        Assert.Equal(4, analysis.PreloadItems.Count);
        Assert.Equal(1, analysis.PreloadPausedCount);
        Assert.Equal(0, analysis.PreloadCancelCount);
        Assert.Single(analysis.DispatcherLongOps);
        Assert.Equal(22.0, analysis.DispatcherLongOps[0].Ms);
        Assert.Equal("Background", analysis.DispatcherLongOps[0].Priority);
        Assert.Equal("SessionSave", analysis.DispatcherLongOps[0].Name);

        var decodedItem = analysis.PreloadItems.First(p => p.Kind == "decoded");
        Assert.True(decodedItem.QueueWaitMs > 0);
    }

    [Fact]
    public void ExcludesDispatcherLongOpsBeforeTheFirstShowStartOrFolderStart()
    {
        var path = Path.Combine(Path.GetTempPath(), "PhotoReview-PerfAnalyze-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var csv = Path.Combine(path, "perf-1-x.csv");
        File.WriteAllText(csv,
            "# commit=test diag= qpcFrequency=10000000\n" +
            "utcTicks,qpcTicks,thread,event,nav,pathId,a,b,c,d,text\n" +
            // --perf-session's own STA harness creating the WPF Window, well before Folder(start).
            "1,1000,1,DispatcherLongOp,,,700.0,,,,Normal;WindowCreate\n" +
            "1,500000,1,Folder,1,,0,,,,start\n" +
            "1,510000,1,DispatcherLongOp,,,20.0,,,,Background;SessionSave\n");

        var file = PerfCsvReader.Read(csv);
        var analysis = PerfAnalyzeNavBuilder.Build(file);

        Assert.Single(analysis.DispatcherLongOps);
        Assert.Equal("SessionSave", analysis.DispatcherLongOps[0].Name);
        Assert.Equal(1, analysis.DispatcherLongOpsBeforeStartCount);
    }

    // ---- Percentiles ----

    [Theory]
    [InlineData(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 50, 5)]
    [InlineData(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 95, 10)]
    [InlineData(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 100, 10)]
    [InlineData(new double[] { 5 }, 50, 5)]
    [InlineData(new double[] { 10, 20, 30, 40 }, 50, 20)]
    public void NearestRankPercentileMatchesHandComputedExpectations(double[] sorted, double p, double expected)
    {
        Assert.Equal(expected, PerfStats.NearestRank(sorted, p));
    }

    [Fact]
    public void NearestRankOfEmptySequenceIsNaN()
    {
        Assert.True(double.IsNaN(PerfStats.NearestRank(Array.Empty<double>(), 95)));
    }

    // ---- Phase share weighting ----

    [Fact]
    public void SlowestDecilePhaseShareMatchesHandComputedRatiosForOneNav()
    {
        // A single hand-built nav: finalVisual=100ms, t_decode=40ms (40%), t_assign=10ms (10%).
        // With only one nav, it is trivially the entire "slowest 10%" slice.
        var nav = new NavRecord
        {
            Nav = 1,
            FinalVisualMs = 100.0,
            TDecodeMs = 40.0,
            TAssignMs = 10.0,
            Incomplete = false,
        };

        var shares = PerfStats.SlowestDecilePhaseShare([nav]);

        Assert.Equal(0.40, shares["t_decode"], 6);
        Assert.Equal(0.10, shares["t_assign"], 6);
        Assert.False(shares.ContainsKey("t_disk")); // never set -> excluded, not reported as 0
    }

    [Fact]
    public void SlowestDecilePhaseShareOnlyAveragesTheSlowestTenPercent()
    {
        // 10 navs, finalVisual 10..100ms, only nav10 (100ms) has a non-trivial t_decode (50ms).
        // The slowest decile of 10 navs is exactly 1 nav (ceil(10*0.10)=1), so the share must be
        // driven entirely by nav10, not diluted by the 9 fast navs with t_decode=0.
        var navs = Enumerable.Range(1, 10).Select(i => new NavRecord
        {
            Nav = i,
            FinalVisualMs = i * 10.0,
            TDecodeMs = i == 10 ? 50.0 : 0.0,
            Incomplete = false,
        }).ToList();

        var shares = PerfStats.SlowestDecilePhaseShare(navs);

        Assert.Equal(0.50, shares["t_decode"], 6);
    }

    [Fact]
    public void IncompleteNavsAreExcludedFromPercentilesButStillCounted()
    {
        var navs = new List<NavRecord>
        {
            new() { Nav = 1, FinalVisualMs = 10, Incomplete = false, Kind = "RamHit" },
            new() { Nav = 2, FinalVisualMs = 20, Incomplete = false, Kind = "RamHit" },
            new() { Nav = 3, FinalVisualMs = 9999, Incomplete = true, Kind = "SourceMiss" }, // superseded
        };

        var summary = GroupSummary.Build(new GroupKey("S2", "Preview", "warm"), navs);

        Assert.Equal(3, summary.Count);
        Assert.Equal(1, summary.Incomplete);
        Assert.Equal(20, summary.FinalMax); // the incomplete nav's huge finalVisual must not leak in
        Assert.Equal(20, summary.FinalP95);
    }

    // ---- Rules: each has at least one triggered and one not-triggered case ----

    [Fact]
    public void RIoTriggersOnHighIoShareAndIsNotTriggeredBelowThreshold()
    {
        var cfg = RulesConfig.Default(); // default threshold 40%
        var high = new[] { NavWithIo(finalVisual: 100, open: 20, read: 30) }; // 50%
        var low = new[] { NavWithIo(finalVisual: 100, open: 5, read: 5) }; // 10%

        Assert.True(PerfRules.EvaluateRIo(cfg, high).Triggered);
        Assert.False(PerfRules.EvaluateRIo(cfg, low).Triggered);
    }

    [Fact]
    public void RIoIsNotApplicableWithoutOpenOrReadTiming()
    {
        var cfg = RulesConfig.Default();
        var navs = new[] { new NavRecord { Nav = 1, FinalVisualMs = 100, TDecodeMs = 80, Incomplete = false } };

        var result = PerfRules.EvaluateRIo(cfg, navs);

        Assert.Null(result.Triggered);
    }

    [Fact]
    public void RDecTriggersOnHighDecodeShareAndIsNotTriggeredBelowThreshold()
    {
        var cfg = RulesConfig.Default();
        var high = new[] { new NavRecord { Nav = 1, FinalVisualMs = 100, TDecodeMs = 60, Incomplete = false } };
        var low = new[] { new NavRecord { Nav = 1, FinalVisualMs = 100, TDecodeMs = 10, Incomplete = false } };

        Assert.True(PerfRules.EvaluateRDec(cfg, high).Triggered);
        Assert.False(PerfRules.EvaluateRDec(cfg, low).Triggered);
    }

    [Fact]
    public void RUiTriggersOnHighUiShareOrHighRamHitP95AndIsNotTriggeredOtherwise()
    {
        var cfg = RulesConfig.Default();
        var slowRamHit = new[] { new NavRecord { Nav = 1, FinalVisualMs = 100, TInputMs = 30, TAssignMs = 20, TRenderMs = 10, Incomplete = false } };
        var fastRamHit = new[] { new NavRecord { Nav = 1, FinalVisualMs = 100, TInputMs = 2, TAssignMs = 2, TRenderMs = 2, Incomplete = false } };

        Assert.True(PerfRules.EvaluateRUi(cfg, slowRamHit, ramHitFinalP95: 100, frameTimeP95Ms: null).Triggered);
        Assert.False(PerfRules.EvaluateRUi(cfg, fastRamHit, ramHitFinalP95: 20, frameTimeP95Ms: null).Triggered);
    }

    [Fact]
    public void RUiTriggersOnHighFrameTimeAlone()
    {
        var cfg = RulesConfig.Default();
        var result = PerfRules.EvaluateRUi(cfg, [], ramHitFinalP95: double.NaN, frameTimeP95Ms: 60.0);
        Assert.True(result.Triggered);
    }

    [Fact]
    public void RPreTriggersOnFastRamHitWithLowCoverageAndIsNotTriggeredWithGoodCoverage()
    {
        var cfg = RulesConfig.Default();
        // S2 (paced) threshold is 10%.
        Assert.True(PerfRules.EvaluateRPre(cfg, ramHitFinalP95: 20, nonRamHitSharePct: 25, isBurstScenario: false).Triggered);
        Assert.False(PerfRules.EvaluateRPre(cfg, ramHitFinalP95: 20, nonRamHitSharePct: 2, isBurstScenario: false).Triggered);
    }

    [Fact]
    public void RContTriggersOnDecodeSlowdownAndIsNotTriggeredWhenStable()
    {
        var cfg = RulesConfig.Default();
        Assert.True(PerfRules.EvaluateRCont(cfg, decodeMsLowWorkers: 40, decodeMsHighWorkers: 80, lowWorkers: 0, highWorkers: 8).Triggered);
        Assert.False(PerfRules.EvaluateRCont(cfg, decodeMsLowWorkers: 40, decodeMsHighWorkers: 42, lowWorkers: 0, highWorkers: 8).Triggered);
    }

    [Fact]
    public void RContIsNotApplicableWithoutTwoDistinctWorkerRuns()
    {
        var cfg = RulesConfig.Default();
        var result = PerfRules.EvaluateRCont(cfg, null, null, null, null);
        Assert.Null(result.Triggered);
    }

    [Fact]
    public void RThreadTriggersOnDispatcherLongOpsOrHighInputP95AndIsNotTriggeredOtherwise()
    {
        var cfg = RulesConfig.Default();
        Assert.True(PerfRules.EvaluateRThread(cfg, dispatcherLongOpCount: 3, tInputP95Ms: 5).Triggered);
        Assert.True(PerfRules.EvaluateRThread(cfg, dispatcherLongOpCount: 0, tInputP95Ms: 40).Triggered);
        Assert.False(PerfRules.EvaluateRThread(cfg, dispatcherLongOpCount: 0, tInputP95Ms: 5).Triggered);
    }

    [Fact]
    public void RGcTriggersAboveThresholdAndIsNotTriggeredBelow()
    {
        var cfg = RulesConfig.Default(); // default 10%
        Assert.True(PerfRules.EvaluateRGc(cfg, gcTimePercent: 15).Triggered);
        Assert.False(PerfRules.EvaluateRGc(cfg, gcTimePercent: 2).Triggered);
        Assert.Null(PerfRules.EvaluateRGc(cfg, gcTimePercent: null).Triggered);
    }

    [Fact]
    public void RDiskTriggersWhenDiskCacheIsNoFasterThanReDecodingAndIsNotTriggeredOtherwise()
    {
        var cfg = RulesConfig.Default();
        var diskSlow = new[] { new NavRecord { Nav = 1, TDiskMs = 90, Incomplete = false } };
        var diskFast = new[] { new NavRecord { Nav = 1, TDiskMs = 5, Incomplete = false } };
        var sourceMiss = new[] { new NavRecord { Nav = 2, TReadMs = 10, TDecodeMs = 40, Incomplete = false } }; // read+decode = 50

        Assert.True(PerfRules.EvaluateRDisk(cfg, diskSlow, sourceMiss).Triggered);
        Assert.False(PerfRules.EvaluateRDisk(cfg, diskFast, sourceMiss).Triggered);
    }

    [Fact]
    public void RFolderTriggersOnSlowFolderOpenAndIsNotTriggeredWhenFast()
    {
        var cfg = RulesConfig.Default();
        var slow = new[] { new FolderGenSummary { Gen = 1, T2Ms = 1500 } };
        var fast = new[] { new FolderGenSummary { Gen = 1, T2Ms = 200 } };

        Assert.True(PerfRules.EvaluateRFolder(cfg, slow).Triggered);
        Assert.False(PerfRules.EvaluateRFolder(cfg, fast).Triggered);
    }

    [Fact]
    public void RulesConfigOverridesDefaultThresholds()
    {
        var path = Path.Combine(Path.GetTempPath(), "PhotoReview-PerfAnalyze-Tests-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{\"R-GC\":{\"gcTimePct\":50}}");
        var cfg = RulesConfig.Load(path);

        // 30% would trigger against the default 10% threshold, but not against the overridden 50%.
        Assert.False(PerfRules.EvaluateRGc(cfg, gcTimePercent: 30).Triggered);
        Assert.True(PerfRules.EvaluateRGc(cfg, gcTimePercent: 60).Triggered);
    }

    private static NavRecord NavWithIo(double finalVisual, double open, double read) => new()
    {
        Nav = 1,
        FinalVisualMs = finalVisual,
        TOpenMs = open,
        TReadMs = read,
        TDecodeMs = 1, // marks it a real SourceMiss for classification purposes elsewhere
        Incomplete = false,
    };

    // ---- End-to-end CLI ----

    [Fact]
    public async Task PerfAnalyzeOnTheSampleDirectoryWritesSummaryMdAndJson()
    {
        var runDir = Path.Combine(Path.GetTempPath(), "PhotoReview-PerfAnalyze-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);
        File.Copy(SampleCsvPath(), Path.Combine(runDir, "perf-sample.csv"));

        var result = await PerfAnalyze.RunAsync(runDir, rulesPath: null);

        Assert.Equal(1, result.CsvFileCount);
        Assert.Single(result.Groups);
        Assert.True(File.Exists(result.SummaryMdPath));
        Assert.True(File.Exists(result.SummaryJsonPath));
        var md = File.ReadAllText(result.SummaryMdPath);
        Assert.Contains("Perf analyze summary", md);
        Assert.Contains("R-DEC", md);
        var json = File.ReadAllText(result.SummaryJsonPath);
        Assert.Contains("\"csvFileCount\": 1", json);
    }

    [Fact]
    public async Task PerfAnalyzeThrowsWhenNoCsvFilesArePresent()
    {
        var runDir = Path.Combine(Path.GetTempPath(), "PhotoReview-PerfAnalyze-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);

        await Assert.ThrowsAsync<InvalidOperationException>(() => PerfAnalyze.RunAsync(runDir, rulesPath: null));
    }

    [Fact]
    public async Task PerfAnalyzeKeepsWorkerTreatmentsSeparateAndOnlyComparesSameCondition()
    {
        var runDir = Path.Combine(Path.GetTempPath(), "PhotoReview-PerfAnalyze-Workers-" + Guid.NewGuid().ToString("N"));
        var warm0 = Path.Combine(runDir, "S2", "fixture-Preview-warm", "run-01");
        var cold8 = Path.Combine(runDir, "S2", "fixture-Preview-cold-app", "run-01");
        Directory.CreateDirectory(warm0);
        Directory.CreateDirectory(cold8);
        File.Copy(SampleCsvPath(), Path.Combine(warm0, "perf-0.csv"));
        File.Copy(SampleCsvPath(), Path.Combine(cold8, "perf-8.csv"));
        File.WriteAllText(Path.Combine(warm0, "session.json"), "{\"scenario\":\"S2\",\"mode\":\"Preview\",\"condition\":\"warm\",\"preloadWorkers\":0}");
        File.WriteAllText(Path.Combine(cold8, "session.json"), "{\"scenario\":\"S2\",\"mode\":\"Preview\",\"condition\":\"cold-app\",\"preloadWorkers\":8}");

        var result = await PerfAnalyze.RunAsync(runDir, rulesPath: null);

        Assert.Equal(2, result.Groups.Count);
        Assert.Contains(result.Groups, g => g.Summary.Key.PreloadWorkers == 0 && g.Summary.Key.Cond == "warm");
        Assert.Contains(result.Groups, g => g.Summary.Key.PreloadWorkers == 8 && g.Summary.Key.Cond == "cold-app");
        Assert.All(result.Groups, g => Assert.Null(g.Rules.Single(r => r.Rule == "R-CONT").Triggered));
    }
}
