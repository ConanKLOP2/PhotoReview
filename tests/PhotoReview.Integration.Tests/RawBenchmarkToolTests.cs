using System.IO;
using PhotoReview.Benchmark.Cli;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Raw;
using Measurement = PhotoReview.Benchmark.Cli.RawDecoderBenchmark.Measurement;

namespace PhotoReview.Integration.Tests;

/// <summary>--decoder-bench --raw: per-file failure handling, exit codes, cold/warm split and like-for-like preview selection.</summary>
public sealed class RawDecoderBenchmarkTests : IDisposable
{
    private readonly TempRoot _root = new("raw-bench");
    public void Dispose() => _root.Dispose();

    private static Measurement Row(string op, bool ok, int iteration = 0, double ms = 1, int width = 0, string? preview = null) =>
        new("a.dng", "Dng", op, width, iteration, ms, ok ? 10 : 0, ok ? 10 : 0, ok, ok ? null : "boom", preview);

    [Theory(DisplayName = "IsMeasurementFailure treats the RAW readers InvalidDataException and WPF FileFormatException as per-file failures")]
    [InlineData(typeof(InvalidDataException), true)]
    [InlineData(typeof(FileFormatException), true)]
    [InlineData(typeof(EndOfStreamException), true)]
    [InlineData(typeof(FileNotFoundException), true)]
    [InlineData(typeof(DllNotFoundException), true)]
    [InlineData(typeof(EntryPointNotFoundException), true)]
    [InlineData(typeof(BadImageFormatException), true)]
    [InlineData(typeof(NotSupportedException), true)]
    [InlineData(typeof(OutOfMemoryException), false)]
    [InlineData(typeof(OperationCanceledException), false)]
    public void IsMeasurementFailure_ClassifiesExceptions(Type type, bool expected) =>
        Assert.Equal(expected, RawDecoderBenchmark.IsMeasurementFailure((Exception)Activator.CreateInstance(type)!));

    [Fact(DisplayName = "exit code is non-zero when nothing was measured")]
    public void ComputeExitCode_NoFilesOrNoRows_Fails()
    {
        Assert.Equal(1, RawDecoderBenchmark.ComputeExitCode([], 0));
        Assert.Equal(1, RawDecoderBenchmark.ComputeExitCode([], 3));
        Assert.Equal(1, RawDecoderBenchmark.ComputeExitCode([Row("HeaderParse", false)], 1));
    }

    [Fact(DisplayName = "exit code is non-zero when LibRawFullDecode never succeeded even though other rows did (libraw.dll failing to load)")]
    public void ComputeExitCode_LibRawAllFailed_Fails()
    {
        var rows = new[] { Row("HeaderParse", true), Row("RawPreview", true, width: 1920), Row("LibRawFullDecode", false) };

        Assert.Equal(1, RawDecoderBenchmark.ComputeExitCode(rows, 1));
    }

    [Fact(DisplayName = "exit code is non-zero when any required row failed for one file, zero when only the comparison row failed")]
    public void ComputeExitCode_RequiredVersusComparisonRows()
    {
        var good = new[] { Row("HeaderParse", true), Row("RawPreview", true, width: 1920), Row("LibRawFullDecode", true) };

        Assert.Equal(0, RawDecoderBenchmark.ComputeExitCode(good, 1));
        Assert.Equal(1, RawDecoderBenchmark.ComputeExitCode([.. good, Row("RawPreview", false, width: 1920)], 1));
        Assert.Equal(1, RawDecoderBenchmark.ComputeExitCode([.. good, Row("FileProcess", false)], 2));
        Assert.Equal(0, RawDecoderBenchmark.ComputeExitCode([.. good, Row("EmbeddedJpegDirect", false, width: 1920)], 1));
    }

    [Fact(DisplayName = "Summarize reports the cold iteration 0 separately from the warm median of iterations 1+")]
    public void Summarize_SplitsColdFromWarm()
    {
        var rows = new[]
        {
            Row("RawPreview", true, 0, 100, 1920), Row("RawPreview", true, 1, 10, 1920), Row("RawPreview", true, 2, 12, 1920),
            Row("RawPreview", true, 0, 40, 2560),
        };

        var summary = RawDecoderBenchmark.Summarize(rows).ToDictionary(s => s.TargetWidth);

        Assert.Equal(100, summary[1920].ColdMedianMs);
        Assert.Equal(11, summary[1920].WarmMedianMs);
        Assert.Equal(2, summary[1920].WarmSamples);
        Assert.Equal(40, summary[2560].ColdMedianMs);
        Assert.Null(summary[2560].WarmMedianMs);
    }

    [Fact(DisplayName = "SelectPreviewForWidth uses the same smallest-preview-not-below-width rule RawDecoder applies")]
    public void SelectPreviewForWidth_PicksSmallestSufficientPreview()
    {
        var small = new EmbeddedPreview(0, 0, 100, EmbeddedPreviewKind.Jpeg, 1000, 667, PreviewColorSpace.Srgb);
        var large = new EmbeddedPreview(1, 100, 900, EmbeddedPreviewKind.Jpeg, 6000, 4000, PreviewColorSpace.Srgb);
        var source = new InMemoryRawHeaderSource(new byte[2048]);

        Assert.Equal(small, RawDecoderBenchmark.SelectPreviewForWidth(source, [small, large], 800, 1));
        Assert.Equal(large, RawDecoderBenchmark.SelectPreviewForWidth(source, [small, large], 1920, 1));
        Assert.Equal(large, RawDecoderBenchmark.SelectPreviewForWidth(source, [small, large], 9000, 1));
    }

    [Fact(DisplayName = "a zero-length and a truncated RAW become failed rows, the good file is still measured, reports are written and the exit code is non-zero")]
    public async Task RunFilesAsync_BadFilesDoNotAbortTheRun()
    {
        var corpus = _root.Dir("corpus");
        var good = Path.Combine(corpus, "b-good.dng");
        File.WriteAllBytes(good, SyntheticRawBuilder.BuildTiff(littleEndian: true));
        var empty = Path.Combine(corpus, "a-empty.dng");
        File.WriteAllBytes(empty, []);
        var truncated = Path.Combine(corpus, "c-truncated.dng");
        File.WriteAllBytes(truncated, SyntheticRawBuilder.BuildTiff(littleEndian: true)[..40]);
        var outDir = _root.Combine("out");

        var (exitCode, rows) = await RawDecoderBenchmark.RunFilesAsync([empty, good, truncated], 2, corpus, outDir, [800]);

        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(Path.Combine(outDir, "raw-decoder-bench.csv")));
        Assert.True(File.Exists(Path.Combine(outDir, "raw-decoder-bench.json")));
        Assert.Contains(rows, r => r.File == "a-empty.dng" && r.Operation == "HeaderParse" && !r.Success);
        Assert.Contains(rows, r => r.File == "c-truncated.dng" && !r.Success);
        Assert.Contains(rows, r => r.File == "b-good.dng" && r.Operation == "RawPreview" && r.Success);
        Assert.Contains(rows, r => r.File == "b-good.dng" && r.Operation == "EmbeddedJpegDirect" && r.Success);
    }

    [Fact(DisplayName = "RawPreview and EmbeddedJpegDirect rows for the same width decode the same selected preview")]
    public async Task RunFilesAsync_ComparisonRowsShareTheSelectedPreview()
    {
        var corpus = _root.Dir("like-for-like");
        var good = Path.Combine(corpus, "one.dng");
        File.WriteAllBytes(good, SyntheticRawBuilder.BuildTiff(littleEndian: true));

        var (_, rows) = await RawDecoderBenchmark.RunFilesAsync([good], 1, corpus, _root.Combine("out2"), [320, 1920]);

        foreach (var width in new[] { 320, 1920 })
        {
            var raw = rows.Single(r => r.Operation == "RawPreview" && r.TargetWidth == width);
            var direct = rows.Single(r => r.Operation == "EmbeddedJpegDirect" && r.TargetWidth == width);
            Assert.False(string.IsNullOrEmpty(raw.SelectedPreview));
            Assert.Equal(raw.SelectedPreview, direct.SelectedPreview);
        }
    }
}

/// <summary>--raw-survey: exit codes, per-file error handling, container-sourced sensor size and conclusions computed from the measured data.</summary>
public sealed class RawSurveyReportTests : IDisposable
{
    private readonly TempRoot _root = new("raw-survey-report");
    public void Dispose() => _root.Dispose();

    private static RawSurvey.SurveyFileResult File_(string name, string format, long size, int sensorW, int sensorH,
        int previewW, int previewH, long previewLength, bool wicOk = false, bool previewOnly = false, string? error = null) =>
        new("C:/x/" + name, name, format, size, 1, sensorW, sensorH,
            previewW > 0 ? [new RawSurvey.EmbeddedJpeg(0, 0, previewLength, previewW, previewH, "sRGB", 1)] : [],
            wicOk, previewW, previewH, 1, null, wicOk, wicOk ? previewW : 0, wicOk ? previewH : 0, 1, wicOk ? null : "no codec", previewOnly,
            sensorW > 0 ? "container" : "unknown", error);

    private static RawSurvey.SurveySummary Summary(params RawSurvey.SurveyFileResult[] files) => new("Windows test", "none", files);

    [Fact(DisplayName = "an empty corpus draws no fixed conclusions (no invented 5-15 percent or 10x claims)")]
    public void BuildConclusions_EmptyCorpus_SaysNothingIsConcluded()
    {
        var text = string.Join("\n", RawSurvey.BuildConclusions(Summary()));

        Assert.Contains("cannot be concluded", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5-15", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10x", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Verified", text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "the disk-read conclusion reports the measured preview-to-file ratio")]
    public void BuildConclusions_ReportsMeasuredRatio()
    {
        var text = string.Join("\n", RawSurvey.BuildConclusions(Summary(
            File_("a.nef", "NEF", 10_000_000, 6000, 4000, 6000, 4000, 1_000_000),
            File_("b.nef", "NEF", 20_000_000, 6000, 4000, 6000, 4000, 4_000_000))));

        Assert.Contains("10.0%-20.0%", text, StringComparison.Ordinal);
        Assert.Contains("median 15.0%", text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "preview versus sensor divergence is reported from container sensor size, and not claimed when previews match")]
    public void BuildConclusions_SensorDivergenceComesFromContainerSize()
    {
        var diverging = string.Join("\n", RawSurvey.BuildConclusions(Summary(
            File_("old.arw", "ARW", 10_000_000, 4912, 3264, 1616, 1080, 500_000))));
        var matching = string.Join("\n", RawSurvey.BuildConclusions(Summary(
            File_("new.arw", "ARW", 10_000_000, 6000, 4000, 6000, 4000, 2_000_000))));
        var unknown = string.Join("\n", RawSurvey.BuildConclusions(Summary(
            File_("nosensor.arw", "ARW", 10_000_000, 0, 0, 1616, 1080, 500_000))));

        Assert.Contains("smaller than the sensor size", diverging, StringComparison.Ordinal);
        Assert.Contains("1/1", diverging, StringComparison.Ordinal);
        Assert.Contains("No preview/sensor divergence", matching, StringComparison.Ordinal);
        Assert.Contains("cannot be concluded", unknown, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "the WIC conclusion counts real decode outcomes instead of asserting LibRaw is essential")]
    public void BuildConclusions_WicCoverageIsMeasured()
    {
        var allWicFull = string.Join("\n", RawSurvey.BuildConclusions(Summary(
            File_("a.dng", "DNG", 10_000_000, 6000, 4000, 6000, 4000, 1_000_000, wicOk: true))));
        var wicFailed = string.Join("\n", RawSurvey.BuildConclusions(Summary(
            File_("a.cr3", "CR3", 10_000_000, 6000, 4000, 6000, 4000, 1_000_000, wicOk: false))));

        Assert.Contains("does not show a need for LibRaw", allWicFull, StringComparison.Ordinal);
        Assert.Contains("failed for format(s) CR3", wicFailed, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "sensor size of a synthetic RAW comes from the container reader, not from the WIC probe")]
    public async Task SurveyFileAsync_SyntheticDng_SensorSourceIsNotWic()
    {
        var path = Path.Combine(_root.Dir("dng"), "s.dng");
        File.WriteAllBytes(path, SyntheticRawBuilder.BuildTiff(littleEndian: true));

        var result = await RawSurvey.SurveyFileAsync(path);

        Assert.Null(result.Error);
        Assert.NotEqual("jpeg", result.SensorSource);
        // The builder writes no sensor size (only a 640x480 preview): the survey must say "unknown", not borrow the preview size.
        Assert.Equal(0, result.SensorWidth);
        Assert.Equal("unknown", result.SensorSource);
    }

    [Fact(DisplayName = "an unreadable RAW is reported as a failed file (not a crash), the markdown is still written and the exit code is non-zero")]
    public async Task RunAsync_BrokenRaw_ReportsFailureAndStillWritesMarkdown()
    {
        var dir = _root.Dir("broken");
        File.WriteAllBytes(Path.Combine(dir, "zero.cr2"), []);
        var md = Path.Combine(dir, "out.md");

        var exit = await RawSurvey.RunAsync(["--raw-survey", dir, "--markdown", md]);

        Assert.Equal(1, exit);
        Assert.True(File.Exists(md));
        var text = await File.ReadAllTextAsync(md);
        Assert.Contains("zero.cr2", text, StringComparison.Ordinal);
        Assert.Contains("could not be surveyed", text, StringComparison.Ordinal);
    }

    private string CorpusOf(string name, params string[] rawNames)
    {
        var dir = _root.Dir(name);
        foreach (var file in rawNames) File.WriteAllBytes(Path.Combine(dir, file), SyntheticRawBuilder.BuildTiff(littleEndian: true));
        return dir;
    }

    [Fact(DisplayName = "a size read that throws for one file (vanished/locked between scan steps) keeps every other row and marks that file failed")]
    public async Task SurveyDirectoryAsync_SizeReadFailsForOneFile_KeepsTheOtherRows()
    {
        var dir = CorpusOf("stat-fails", "a.dng", "b.dng", "c.dng");

        var summary = await RawSurvey.SurveyDirectoryAsync(dir, path =>
            Path.GetFileName(path) == "b.dng" ? throw new FileNotFoundException("gone between scan steps") : RawSurvey.ReadFileLength(path));

        Assert.Equal(["a.dng", "b.dng", "c.dng"], summary.Files.Select(f => f.FileName));
        Assert.Contains("size:", summary.Files.Single(f => f.FileName == "b.dng").Error, StringComparison.Ordinal);
        Assert.Equal(0, summary.Files.Single(f => f.FileName == "b.dng").FileSizeBytes);
        Assert.All(summary.Files.Where(f => f.FileName != "b.dng"), f => { Assert.Null(f.Error); Assert.True(f.FileSizeBytes > 0); });
    }

    [Fact(DisplayName = "RunAsync still writes the markdown for the surveyed files when one file's stat fails, and exits 1")]
    public async Task RunAsync_SizeReadFailsForOneFile_WritesReportAndExitsOne()
    {
        var dir = CorpusOf("stat-fails-run", "a.dng", "b.dng");
        var md = Path.Combine(_root.Dir("stat-fails-out"), "out.md");

        var exit = await RawSurvey.RunAsync(["--raw-survey", dir, "--markdown", md], path =>
            Path.GetFileName(path) == "b.dng" ? throw new IOException("locked") : RawSurvey.ReadFileLength(path));

        Assert.Equal(1, exit);
        var text = await File.ReadAllTextAsync(md);
        Assert.Contains("a.dng", text, StringComparison.Ordinal);
        Assert.Contains("b.dng", text, StringComparison.Ordinal);
        Assert.Contains("could not be surveyed", text, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "a valueless or repeated --markdown, an unknown flag or a stray token is rejected with usage (exit 2), not silently ignored")]
    [InlineData("--markdown")]
    [InlineData("-o")]
    [InlineData("--markdwon", "out.md")]
    [InlineData("stray")]
    [InlineData("--markdown", "--other")]
    [InlineData("--markdown", "a.md", "--markdown", "b.md")]
    [InlineData("--markdown", "a.md", "extra")]
    public async Task RunAsync_BadArguments_ExitTwoWithoutSurveying(params string[] extra)
    {
        var dir = CorpusOf("bad-args", "a.dng");

        var exit = await RawSurvey.RunAsync([.. new[] { "--raw-survey", dir }, .. extra]);

        Assert.Equal(2, exit);
        Assert.False(RawSurvey.TryParseArgs([.. new[] { "--raw-survey", dir }, .. extra], out _, out _));
    }

    [Theory(DisplayName = "valid --markdown / -o arguments parse to the output path")]
    [InlineData("--markdown")]
    [InlineData("-o")]
    public void TryParseArgs_ValidFlag_ReturnsPath(string flag)
    {
        Assert.True(RawSurvey.TryParseArgs(["--raw-survey", "dir", flag, "out.md"], out var path, out var error));
        Assert.Equal("out.md", path);
        Assert.Null(error);
        Assert.True(RawSurvey.TryParseArgs(["--raw-survey", "dir"], out var none, out _));
        Assert.Null(none);
    }

    [Fact(DisplayName = "only RAW extensions are surveyed: a .jpg/.jpeg in a mixed folder is ignored and cannot fail the run")]
    public async Task SurveyDirectoryAsync_MixedFolder_SurveysOnlyRawFiles()
    {
        var dir = CorpusOf("mixed", "a.dng");
        File.WriteAllBytes(Path.Combine(dir, "b.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "c.JPEG"), [1, 2, 3]);

        var summary = await RawSurvey.SurveyDirectoryAsync(dir);

        Assert.Equal(["a.dng"], summary.Files.Select(f => f.FileName));
        Assert.Equal(0, await RawSurvey.RunAsync(["--raw-survey", dir]));
    }

    [Fact(DisplayName = "a folder holding only JPEGs surveys nothing and fails (not a silent success)")]
    public async Task RunAsync_OnlyJpegs_ReturnsNonZero()
    {
        var dir = _root.Dir("jpegs-only");
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [1, 2, 3]);

        Assert.Equal(1, await RawSurvey.RunAsync(["--raw-survey", dir]));
    }

    [Fact(DisplayName = "surveying a folder with no files is a failure, not a silent success")]
    public async Task RunAsync_NoFiles_ReturnsNonZero()
    {
        var dir = _root.Dir("nothing");

        Assert.Equal(1, await RawSurvey.RunAsync(["--raw-survey", dir]));
    }

    [Fact(DisplayName = "a locked RAW file is a per-file failure, not an unhandled IOException")]
    public async Task SurveyFileAsync_LockedFile_RecordsErrorInsteadOfThrowing()
    {
        var path = Path.Combine(_root.Dir("locked"), "locked.dng");
        File.WriteAllBytes(path, SyntheticRawBuilder.BuildTiff(littleEndian: true));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await RawSurvey.SurveyFileAsync(path);

        Assert.NotNull(result.Error);
    }
}
