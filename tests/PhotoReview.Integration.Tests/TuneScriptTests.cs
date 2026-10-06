using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using PhotoReview.Benchmark.Cli;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Device-tuning bench scripts (tools/diag/tune-matrix.ps1, tune-rank.ps1, tune/*.json). Temp directories only; the
/// tune-rank test builds a synthetic batch (matrix.json + per-run navs.csv/summary.json/process.json/resources.csv) with known
/// per-navigation latencies, so the pooled percentiles, A/A noise, winner rule and RAM constraint are asserted exactly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TuneScriptTests
{
    private static string TuneDir() => Path.Combine(PowerShellRunner.RepoRoot(), "tools", "diag", "tune");

    public static TheoryData<string> StageFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(TuneDir(), "*.json").OrderBy(p => p, StringComparer.Ordinal)) data.Add(Path.GetFileName(path));
        return data;
    }

    [Theory(DisplayName = "every stage file is a pure JSON array whose --set keys/values the CLI accepts and whose ids are valid")]
    [MemberData(nameof(StageFiles))]
    public void StageFile_IsValidForTheCli(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TuneDir(), file), Encoding.UTF8));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.NotEmpty(doc.RootElement.EnumerateArray());
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            Assert.Matches("^[A-Za-z0-9._-]+$", item.GetProperty("id").GetString()!);
            if (item.TryGetProperty("set", out var set))
                foreach (var p in set.EnumerateObject())
                    PerfSettingOverrides.Parse(p.Name + "=" + (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.Value.GetRawText() : p.Value.GetRawText().Trim('"')));
        }
    }

    [Theory(DisplayName = "tune-matrix -DryRun expands every stage file (no fixture, no build, nothing written)")]
    [MemberData(nameof(StageFiles))]
    public void TuneMatrix_DryRun_ExpandsStage(string file)
    {
        var script = Path.Combine(PowerShellRunner.RepoRoot(), "tools", "diag", "tune-matrix.ps1");
        var (code, output) = PowerShellRunner.Run("-File", script, "-Stage", Path.GetFileNameWithoutExtension(file), "-Profile", "quick", "-Repeat", "1", "-DryRun");
        Assert.True(code == 0, output);
        Assert.Contains("DRY RUN", output, StringComparison.Ordinal);
        Assert.Contains("recorded run(s)", output, StringComparison.Ordinal);
    }

    private static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static void WriteRun(string dir, double[] latencies, double minAvailMb, double peakWsMb)
    {
        Directory.CreateDirectory(dir);
        var navs = new StringBuilder(NavSamplesCsv.Header).AppendLine();
        for (var i = 0; i < latencies.Length; i++) navs.Append("S2-next-slow@F4/Preview/unknown/workers=?,").Append(i + 1).Append(",RamHit,1,1,").AppendLine(Inv(latencies[i]));
        File.WriteAllText(Path.Combine(dir, "navs.csv"), navs.ToString());
        var sorted = latencies.OrderBy(v => v).ToArray();
        var p95 = sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1];
        File.WriteAllText(Path.Combine(dir, "summary.json"),
            $$"""{"groups":[{"scenario":"S2-next-slow@F4","count":{{latencies.Length}},"incomplete":0,"firstVisualMs":{"p50":1,"p95":1,"max":1},"finalVisualMs":{"p50":{{Inv(sorted[sorted.Length / 2])}},"p95":{{Inv(p95)}},"max":1},"renderedFrameMs":{"p95":1},"kindCounts":{"RamHit":{{latencies.Length}}},"gcTimePercent":1.5,"frameTimeP95Ms":null,"preload":{"pausedCount":0},"rules":[]}]}""");
        File.WriteAllText(Path.Combine(dir, "process.json"), $$"""{"peakWorkingSetBytes":{{(long)(peakWsMb * 1024 * 1024)}}}""");
        File.WriteAllText(Path.Combine(dir, "session.json"), """{"errors":[]}""");
        File.WriteAllText(Path.Combine(dir, "resources.csv"),
            ResourceSampler.Header + "\n" +
            $"2026-10-06T10:00:00.0000000Z,0,{Inv(minAvailMb + 500)},1000,{Inv(peakWsMb)},1000,0,0,0\n" +
            $"2026-10-06T10:00:00.5000000Z,500,{Inv(minAvailMb)},1100,{Inv(peakWsMb)},1100,2,10,30\n");
    }

    private static string BuildBatch(TempRoot root)
    {
        // default: runs 1-2 = 20 x 10 ms (run P95 10); runs 3-4 = 16 x 10 ms + 4 x 50 ms (run P95 50). Median of run P95s = 30,
        //          but the POOLED 80 samples have P50 10 and P95 (rank 76) 50: this is what separates pooled from median-of-run P95.
        // tailfix: every run = 20 x 20 ms -> pooled P95 20 (ratio 0.4, a 60 % gain) but P50 20 (ratio 2.0: the median got slower).
        // fast:    every run = 18 x 5 ms, 1 x 6 ms, 1 x 10 ms -> pooled P50 5, P95 (rank 76) 6.
        var batch = root.Dir("batch");
        var cells = new List<object>();
        foreach (var (id, avail) in new[] { ("default", 8000.0), ("fast", 7000.0), ("tailfix", 8000.0) })
        {
            for (var run = 1; run <= 4; run++)
            {
                var values = id == "fast" ? Enumerable.Repeat(5.0, 18).Concat([6.0, 10.0]).ToArray()
                    : id == "tailfix" ? Enumerable.Repeat(20.0, 20).ToArray()
                    : run <= 2 ? Enumerable.Repeat(10.0, 20).ToArray()
                    : Enumerable.Repeat(10.0, 16).Concat(Enumerable.Repeat(50.0, 4)).ToArray();
                var dir = Path.Combine(batch, "s2-next-slow", "F4", id, "run-" + run.ToString("00", CultureInfo.InvariantCulture));
                WriteRun(dir, values, avail, 9000);
                cells.Add(new { key = $"s2-next-slow/{id}/run-{run}", scenario = "s2-next-slow", fixture = "F4", configId = id, run, round = run, warmup = false, status = "ok", outDir = dir });
            }
        }
        var matrix = new
        {
            created = "tune-test", stage = "x\\s.json", stageHash = "abc", seed = 1, repeat = 4, scenarios = new[] { "s2-next-slow" }, fixtureAlias = "F4",
            fixtureFingerprint = new { Count = 1, Bytes = 1, Fingerprint = "fp" }, fixtureChanged = false, commit = "c", cliVersion = "v", coldDiskCache = false, cooldownSeconds = 0,
            sessions = new[] { new { environment = new { onAcPower = true, powerPlan = "test", freeRamGb = 20, cpuIdlePercentAvg = 99 } } }, cells,
        };
        File.WriteAllText(Path.Combine(batch, "matrix.json"), JsonSerializer.Serialize(matrix));
        return batch;
    }

    private static string Rank(string batch, params string[] extra)
    {
        var script = Path.Combine(PowerShellRunner.RepoRoot(), "tools", "diag", "tune-rank.ps1");
        var (code, output) = PowerShellRunner.Run(["-File", script, "-BatchDir", batch, "-Baseline", "default", "-GeoScenarios", "S2", "-BootstrapResamples", "200", .. extra]);
        Assert.True(code == 0, output);
        return output + Environment.NewLine + File.ReadAllText(Path.Combine(batch, "report.md"), Encoding.UTF8);
    }

    /// <summary>Cells of the config's row in the "Ranking" table (index 0 is the empty cell before the first pipe; the Winner cell is [^2]).</summary>
    private static string[] RankingRow(string report, string config) =>
        report.Split("## Ranking")[1].Split("## Per scenario")[0].Split('\n').Select(l => l.Trim())
            .First(l => l.StartsWith("| " + config + " |", StringComparison.Ordinal)).Split('|').Select(c => c.Trim()).ToArray();

    [Fact(DisplayName = "tune-rank: pooled P50/P95, A/A noise, winner rule and the min-available-RAM constraint on a synthetic batch")]
    public void TuneRank_PooledStatistics_AndWinnerRule()
    {
        using var root = new TempRoot("tune-rank");
        var batch = BuildBatch(root);

        var report = Rank(batch);
        // pooled per-navigation percentiles, not the median of run percentiles
        Assert.Contains("| default | 4 (4) | 80 | 10.00 | 10.00 - 10.00 | 1.000 | 50.00 |", report, StringComparison.Ordinal);   // pooled P95 50, not the median of run P95s (30)
        Assert.Contains("| fast | 4 (4) | 80 | 5.00 |", report, StringComparison.Ordinal);
        Assert.Contains("| 0.500 |", report, StringComparison.Ordinal);                       // fast P50 ratio
        Assert.Contains("| 0.120 |", report, StringComparison.Ordinal);                       // fast P95 ratio = 6 / 50 (a median-of-run-P95 ranking would give 0.200)
        Assert.Contains("| 30.00 (10.00 - 50.00) |", report, StringComparison.Ordinal);      // the run P95 survives as a guard column
        // identical runs of the A/A group -> zero noise, so a 70 % gain wins
        Assert.Contains("**P50 0.0 %, P95 0.0 %**", report, StringComparison.Ordinal);
        Assert.Equal("yes", RankingRow(report, "fast")[^2]);
        Assert.Equal("", RankingRow(report, "tailfix")[^2]);   // P95 -60 % but P50 x2: the other metric must not regress, so no winner

        // a given noise of 50 % needs a gain > 100 %: the 88 % gain no longer wins (the 10 % rule alone would let it)
        Assert.Equal("", RankingRow(Rank(batch, "-NoisePct", "50"), "fast")[^2]);

        // min available RAM (from resources.csv): 7000 MB = 6.8 GB < 20 GB -> disqualified, no winner
        var tight = Rank(batch, "-MinAvailRamGb", "20");
        Assert.Contains("minAvailRAM 6.8 GB < 20", tight, StringComparison.Ordinal);
        Assert.Equal("", RankingRow(tight, "fast")[^2]);

        // run-P95 guard: the median run P95 of fast is 0.2x default's; a guard ratio of 0.1 flags it and disqualifies
        var guarded = Rank(batch, "-RunP95GuardRatio", "0.1");
        Assert.Contains("run-P95 guard 0.20x baseline > 0.1", guarded, StringComparison.Ordinal);
        Assert.Equal("", RankingRow(guarded, "fast")[^2]);
    }

    [Fact(DisplayName = "make-subset-fixture: hard-link subset of a bracketed folder, source untouched, alias registered with existing keys kept, refusals")]
    public void MakeSubsetFixture_HardLinks_AndRefusals()
    {
        using var root = new TempRoot("subset");
        var src = root.Dir("src[[x]");                       // brackets: every path must be used literally
        var sub = Directory.CreateDirectory(Path.Combine(src, "sub")).FullName;
        var rng = new Random(7);
        for (var i = 0; i < 12; i++)
        {
            var bytes = new byte[2 * 1024 * 1024];
            rng.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(i % 2 == 0 ? src : sub, $"img[{i}].jpg"), bytes);
        }
        var fixtures = root.Combine("fixtures.json");
        File.WriteAllText(fixtures, "{\n  \"note\": \"keep me\",\n  \"S\": { \"path\": \"" + src.Replace("\\", "\\\\", StringComparison.Ordinal) + "\" }\n}\n");
        var dst = root.Combine("dst");
        var script = Path.Combine(PowerShellRunner.RepoRoot(), "tools", "diag", "make-subset-fixture.ps1");

        var (code, output) = PowerShellRunner.Run("-File", script, "-Source", "S", "-Destination", dst, "-TargetGb", "0.01", "-Alias", "F-test", "-FixturesFile", fixtures);
        Assert.True(code == 0, output);
        Assert.Contains("all verified same file as the source", output, StringComparison.Ordinal);
        var linked = Directory.GetFiles(dst, "*.jpg", SearchOption.AllDirectories);
        Assert.InRange(linked.Length, 5, 8);                // 0.01 GiB = 10.7 MB of 2 MiB files; overshoot < one file
        foreach (var f in linked)
        {
            var original = Path.Combine(src, Path.GetRelativePath(dst, f));
            Assert.Equal(new FileInfo(original).Length, new FileInfo(f).Length);
            Assert.True(File.ReadAllBytes(original).AsSpan().SequenceEqual(File.ReadAllBytes(f)));
        }
        Assert.Equal(12, Directory.GetFiles(src, "*.jpg", SearchOption.AllDirectories).Length);   // source untouched
        var text = File.ReadAllText(fixtures);
        Assert.Contains("\"note\": \"keep me\"", text, StringComparison.Ordinal);
        Assert.Contains("\"S\":", text, StringComparison.Ordinal);
        Assert.Contains("\"F-test\": { \"path\": \"" + dst.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"", text, StringComparison.Ordinal);
        using (JsonDocument.Parse(text)) { }                // still valid JSON

        // a hard link really shares the file: appending through the destination name changes the source too (a copy would not)
        var probe = linked[0];
        var original0 = Path.Combine(src, Path.GetRelativePath(dst, probe));
        var before = new FileInfo(original0).Length;
        File.AppendAllText(probe, "x");
        Assert.Equal(before + 1, new FileInfo(original0).Length);

        // refusals: existing subset without -Rebuild; destination inside the source; too large a target
        var again = PowerShellRunner.Run("-File", script, "-Source", src, "-Destination", dst, "-TargetGb", "0.01");
        Assert.NotEqual(0, again.ExitCode);
        Assert.Contains("already holds a subset", again.Output, StringComparison.Ordinal);
        var nested = PowerShellRunner.Run("-File", script, "-Source", src, "-Destination", Path.Combine(src, "inner"), "-TargetGb", "0.01");
        Assert.NotEqual(0, nested.ExitCode);
        Assert.Contains("separate folders", nested.Output, StringComparison.Ordinal);
        var enclosing = PowerShellRunner.Run("-File", script, "-Source", src, "-Destination", root.Path, "-TargetGb", "0.01");   // source inside the destination
        Assert.NotEqual(0, enclosing.ExitCode);
        Assert.Contains("separate folders", enclosing.Output, StringComparison.Ordinal);
        var tooBig =PowerShellRunner.Run("-File", script, "-Source", src, "-Destination", root.Combine("dst2"), "-TargetGb", "5");
        Assert.NotEqual(0, tooBig.ExitCode);
        Assert.Contains("larger than the source", tooBig.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(root.Combine("dst2")));
    }
}
