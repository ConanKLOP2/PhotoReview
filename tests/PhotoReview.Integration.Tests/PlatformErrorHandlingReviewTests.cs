using System.IO;
using PhotoReview.Benchmarking;
using PhotoReview.PerfAnalysis;

namespace PhotoReview.Integration.Tests;

/// <summary>Error-handling review fixes in Benchmarking / PerfAnalysis (limiter chunking, tolerant rules/session loading).</summary>
public sealed class PlatformErrorHandlingReviewTests
{
    [Fact(DisplayName = "SharedBandwidthLimiter.Consume of more than one second's budget completes (chunked) instead of spinning forever")]
    public async Task Limiter_ReadLargerThanPerSecondBudget_Completes()
    {
        var limiter = new SharedBandwidthLimiter(bytesPerSecond: 100_000);
        var consume = Task.Run(() => limiter.Consume(250_000));

        await consume.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(consume.IsCompletedSuccessfully);
    }

    [Fact(DisplayName = "RulesConfig.Load ignores a null section and wraps a malformed file with its path")]
    public void RulesConfig_Load_NullSectionAndMalformed()
    {
        var dir = Directory.CreateTempSubdirectory("photoreview-rules-").FullName;
        try
        {
            var ok = Path.Combine(dir, "ok.json");
            File.WriteAllText(ok, "{\"R-X\": null, \"R-Y\": {\"k\": 5}}");
            var rules = RulesConfig.Load(ok);
            Assert.Equal(7, rules.Get("R-X", "k", 7));
            Assert.Equal(5, rules.Get("R-Y", "k", 7));

            var bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(bad, "{ not json");
            var ex = Assert.Throws<InvalidDataException>(() => RulesConfig.Load(bad));
            Assert.Contains("bad.json", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact(DisplayName = "RunFileMeta.Load tolerates a session.json whose root is not an object")]
    public void RunFileMeta_Load_NonObjectSessionJson()
    {
        var dir = Directory.CreateTempSubdirectory("photoreview-meta-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "session.json"), "[1,2,3]");
            File.WriteAllText(Path.Combine(dir, "process.json"), "\"text\"");
            var file = new PerfCsvFile
            {
                Path = Path.Combine(dir, "perf-1.csv"),
                CommitVersion = "",
                DiagFlags = new Dictionary<string, string>(),
                QpcFrequency = 1,
                DroppedRows = 0,
                Rows = [],
            };

            var meta = RunFileMeta.Load(file);

            Assert.Equal("unknown", meta.Scenario);
            Assert.Null(meta.GcTimePercent);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
