using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Input;
using PhotoReview.Benchmark.Cli;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Tests.Raw;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Wave 2 tooling-b hardening (T-B-01, 03, 04, 05, 06, 07, 08, 10, 11, 13, 15): the benchmark CLI must not touch files it does not own,
/// must survive one bad image and must reject malformed options. Every test uses temp folders and fakes; nothing here touches a real
/// photo folder or the real Recycle Bin.
/// </summary>
public sealed class BenchmarkCliHardeningTests : IDisposable
{
    private readonly TempRoot _root = new("cli-hardening");

    public void Dispose() => _root.Dispose();

    private static string Scenario => "scenario.json";

    private string Photos(string name = "photos")
    {
        var dir = _root.Dir(name);
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [1, 2, 3]);
        return dir;
    }

    // ---- T-B-01 ----------------------------------------------------------------------------------

    [Fact(DisplayName = "T-B-01: with the default shortcuts a key step may not send Move to (M), Copy to (Y) or Open folder (O)")]
    public void CollectForbiddenKeys_DefaultShortcuts_BlockMoveCopyAndOpenFolder()
    {
        var forbidden = PerfSession.CollectForbiddenKeys(new AppSettings());

        Assert.Contains(Key.M, forbidden);
        Assert.Contains(Key.Y, forbidden);
        Assert.Contains(Key.O, forbidden);
        Assert.Contains(Key.Delete, forbidden);
        Assert.Contains(Key.Enter, forbidden);
        Assert.Contains(Key.PageDown, forbidden);
        Assert.Contains(Key.F11, forbidden);
        // The scenarios in tools/diag/scenarios only walk and zoom.
        Assert.DoesNotContain(Key.Right, forbidden);
        Assert.DoesNotContain(Key.Left, forbidden);
        Assert.DoesNotContain(Key.Home, forbidden);
        Assert.DoesNotContain(Key.F, forbidden);
    }

    [Fact(DisplayName = "T-B-01: every ShortcutMappings shortcut is forbidden unless it is on the navigation/zoom allow-list (fail closed for future shortcuts)")]
    public void CollectForbiddenKeys_ReflectionOverShortcutMappings_AllowsOnlyTheSafeList()
    {
        var properties = typeof(ShortcutMappings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string)).ToArray();
        Assert.All(PerfSession.SafeKeyShortcuts, name => Assert.Contains(properties, p => p.Name == name)); // no stale allow-list entry

        // Give every shortcut its own key so a forbidden one cannot hide behind another property's key.
        var settings = new AppSettings { Actions = [] }; // configured actions are forbidden on top; keep them out of the allow-list check
        var assigned = new Dictionary<string, Key>();
        var next = Key.F1;
        foreach (var property in properties)
        {
            property.SetValue(settings.Shortcuts, next.ToString());
            assigned[property.Name] = next;
            next++;
        }

        var forbidden = PerfSession.CollectForbiddenKeys(settings);

        foreach (var (name, key) in assigned)
        {
            var safe = PerfSession.SafeKeyShortcuts.Contains(name);
            Assert.True(forbidden.Contains(key) != safe, $"{name} ({key}): forbidden={forbidden.Contains(key)} but allow-listed={safe}");
        }

        foreach (var name in new[] { nameof(ShortcutMappings.MoveToFolder), nameof(ShortcutMappings.CopyToFolder), nameof(ShortcutMappings.OpenFolder),
                     nameof(ShortcutMappings.SendToRecycleBin), nameof(ShortcutMappings.MoveToFolder2), nameof(ShortcutMappings.Undo),
                     nameof(ShortcutMappings.NextFolder), nameof(ShortcutMappings.PreviousFolder), nameof(ShortcutMappings.Fullscreen), nameof(ShortcutMappings.CustomZoom) })
            Assert.Contains(assigned[name], forbidden);
    }

    // ---- T-B-04 ----------------------------------------------------------------------------------

    private string RawCorpus(string name)
    {
        var dir = _root.Dir(name);
        File.WriteAllBytes(Path.Combine(dir, "a.dng"), SyntheticRawBuilder.BuildTiff(littleEndian: true));
        return dir;
    }

    [Fact(DisplayName = "T-B-04: --raw-survey --markdown pointing at an existing RAW leaves the RAW untouched and exits 2")]
    public async Task RawSurvey_MarkdownPointsAtARawFile_IsRefusedAndTheFileIsUntouched()
    {
        var dir = RawCorpus("survey");
        var victim = Path.Combine(_root.Dir("elsewhere"), "IMG_0001.CR2");
        File.WriteAllBytes(victim, [9, 8, 7]);

        var exit = await RawSurvey.RunAsync(["--raw-survey", dir, "--markdown", victim]);

        Assert.Equal(2, exit);
        Assert.Equal([9, 8, 7], File.ReadAllBytes(victim));
    }

    [Fact(DisplayName = "T-B-04: an existing .md is replaced only with --force")]
    public async Task RawSurvey_ExistingMarkdown_NeedsForce()
    {
        var dir = RawCorpus("survey-force");
        var md = Path.Combine(_root.Dir("out"), "report.md");
        File.WriteAllText(md, "old");

        Assert.Equal(2, await RawSurvey.RunAsync(["--raw-survey", dir, "--markdown", md]));
        Assert.Equal("old", File.ReadAllText(md));

        Assert.Equal(0, await RawSurvey.RunAsync(["--raw-survey", dir, "--markdown", md, "--force"]));
        Assert.Contains("Camera RAW Survey", File.ReadAllText(md), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "T-B-04: the report may not be written inside the surveyed folder")]
    public async Task RawSurvey_MarkdownInsideSurveyedFolder_IsRefused()
    {
        var dir = RawCorpus("survey-inside");
        var md = Path.Combine(dir, "report.md");

        var exit = await RawSurvey.RunAsync(["--raw-survey", dir, "--markdown", md]);

        Assert.Equal(2, exit);
        Assert.False(File.Exists(md));
    }

    [Theory(DisplayName = "T-B-04: --markdown must name a .md file")]
    [InlineData("report.txt")]
    [InlineData("report")]
    [InlineData("report.cr2")]
    public async Task RawSurvey_NonMarkdownExtension_IsRefused(string fileName)
    {
        var dir = RawCorpus("survey-ext");
        var target = Path.Combine(_root.Dir("out-ext"), fileName);

        var exit = await RawSurvey.RunAsync(["--raw-survey", dir, "--markdown", target]);

        Assert.Equal(2, exit);
        Assert.False(File.Exists(target));
    }

    [Fact(DisplayName = "T-B-04: --force is only valid together with --markdown and only once")]
    public void RawSurvey_ForceParsing()
    {
        Assert.True(RawSurvey.TryParseArgs(["--raw-survey", "d", "--markdown", "o.md", "--force"], out var path, out var force, out _));
        Assert.Equal("o.md", path);
        Assert.True(force);
        Assert.False(RawSurvey.TryParseArgs(["--raw-survey", "d", "--force"], out _, out _, out var error));
        Assert.Contains("--markdown", error, StringComparison.Ordinal);
        Assert.False(RawSurvey.TryParseArgs(["--raw-survey", "d", "--markdown", "o.md", "--force", "--force"], out _, out _, out _));
    }

    // ---- T-B-10 ----------------------------------------------------------------------------------

    [Fact(DisplayName = "T-B-10: a JPEG candidate without an EOI is rejected, a complete one is still found")]
    public void ScanEmbeddedJpegs_CandidateWithoutEoi_IsRejected()
    {
        var complete = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        Assert.Equal([0xFF, 0xD9], complete[^2..]);
        var truncated = complete[..^2];

        var withEoi = _root.File("with-eoi.bin", complete);
        var withoutEoi = _root.File("without-eoi.bin", truncated);

        var found = Assert.Single(RawSurvey.ScanEmbeddedJpegs(withEoi));
        Assert.Equal(complete.Length, found.Length);
        Assert.Empty(RawSurvey.ScanEmbeddedJpegs(withoutEoi));
    }

    // ---- T-B-05 / T-B-13 -------------------------------------------------------------------------

    private void ValidateCache(string cacheDir, string? source = null, string? outDir = null)
    {
        source ??= Photos("cache-photos");
        outDir ??= _root.Combine("cache-out");
        PerfSession.ValidatePaths(source, outDir, Path.Combine(outDir, "data"), Path.Combine(outDir, "copy"), cacheDir);
    }

    [Fact(DisplayName = "T-B-05: --cache-dir that is, contains or sits inside the photo folder is refused (its cache\\*.png would be deleted)")]
    public void CacheDir_OverlappingThePhotoFolder_IsRefused()
    {
        var photos = Photos("cache-photos");
        Directory.CreateDirectory(Path.Combine(photos, "cache"));
        var exported = Path.Combine(photos, "cache", "export.png");
        File.WriteAllBytes(exported, [1]);

        Assert.Throws<InvalidOperationException>(() => ValidateCache(photos, photos));
        Assert.Throws<InvalidOperationException>(() => ValidateCache(Path.Combine(photos, "sub"), photos));
        Assert.Throws<InvalidOperationException>(() => ValidateCache(Directory.GetParent(photos)!.FullName, photos));
        Assert.True(File.Exists(exported));
    }

    [Fact(DisplayName = "T-B-05: --cache-dir may not overlap outDir or the real app data folder")]
    public void CacheDir_OverlappingOutDirOrAppData_IsRefused()
    {
        var outDir = _root.Combine("cache-out");
        Assert.Throws<InvalidOperationException>(() => ValidateCache(outDir, outDir: outDir));
        Assert.Throws<InvalidOperationException>(() => ValidateCache(Path.Combine(outDir, "data", "cache"), outDir: outDir));
        Assert.Throws<InvalidOperationException>(() => ValidateCache(ToolPathGuard.AppDataFolder()));
        Assert.Throws<InvalidOperationException>(() => ValidateCache(Path.Combine(ToolPathGuard.AppDataFolder(), "cache")));
    }

    [Fact(DisplayName = "T-B-05: a non-empty --cache-dir needs the tool marker; a missing, empty or marked one is accepted")]
    public void CacheDir_NeedsToBeEmptyOrMarked()
    {
        var foreign = _root.Dir("foreign-cache");
        File.WriteAllBytes(Path.Combine(foreign, "keep.png"), [1]);
        var ex = Assert.Throws<InvalidOperationException>(() => ValidateCache(foreign));
        Assert.Contains("not empty", ex.Message, StringComparison.Ordinal);

        ValidateCache(_root.Combine("missing-cache"));
        ValidateCache(_root.Dir("empty-cache"));

        var owned = _root.Dir("owned-cache");
        ToolPathGuard.ClaimCacheDirectory(owned);
        File.WriteAllBytes(Path.Combine(owned, "x.png"), [1]);
        ValidateCache(owned); // second run in the same batch keeps working
        Assert.True(File.Exists(Path.Combine(owned, ToolPathGuard.CacheMarker)));
    }

    [Fact(DisplayName = "T-B-05: --perf-session --cache-dir is parsed to a full path and validated by ValidatePaths")]
    public void CacheDir_ParseArgsKeepsTheFlag()
    {
        var options = PerfSession.ParseArgs(["--perf-session", Scenario, "C:/photos", "C:/out", "--cache-dir", "C:/batch/cache"]);
        Assert.Equal(Path.GetFullPath("C:/batch/cache"), options.CacheDir);
        Assert.Null(PerfSession.ParseArgs(["--perf-session", Scenario, "C:/photos", "C:/out"]).CacheDir);
    }

    [Fact(DisplayName = "T-B-03: the shared output guard refuses equal, inside and ancestor folders and accepts a sibling")]
    public void OutputDirectoryGuard_EqualInsideAncestor()
    {
        var photos = Photos("guard-photos");
        Assert.Throws<InvalidOperationException>(() => ToolPathGuard.EnsureOutputDirectory(photos, photos));
        Assert.Throws<InvalidOperationException>(() => ToolPathGuard.EnsureOutputDirectory(photos + Path.DirectorySeparatorChar, photos));
        Assert.Throws<InvalidOperationException>(() => ToolPathGuard.EnsureOutputDirectory(Path.Combine(photos, "reports"), photos));
        Assert.Throws<InvalidOperationException>(() => ToolPathGuard.EnsureOutputDirectory(_root.Path, photos));
        Assert.Throws<InvalidOperationException>(() => ToolPathGuard.EnsureOutputDirectory(photos.ToUpperInvariant(), photos));

        ToolPathGuard.EnsureOutputDirectory(_root.Combine("guard-photos-reports"), photos); // a sibling whose name merely starts the same
        Assert.False(ToolPathGuard.Overlaps(_root.Combine("guard-photos-reports"), photos));
    }

    [Fact(DisplayName = "T-B-03: --decoder-bench, --io-decode-split and --decoder-bench --raw refuse an outDir inside the photo folder and write nothing there")]
    public async Task ReportModes_OutDirInsideThePhotoFolder_AreRefused()
    {
        var photos = Photos("report-photos");
        var inside = Path.Combine(photos, "reports");

        await Assert.ThrowsAsync<InvalidOperationException>(() => DecoderBenchmark.RunAsync(["--decoder-bench", photos, inside, "Wpf", "0", "1"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => IoDecodeSplit.RunAsync(photos, photos, [0], 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RawDecoderBenchmark.RunAsync(["--decoder-bench", "--raw", photos, inside]));

        Assert.False(Directory.Exists(inside));
        Assert.Equal(["a.jpg"], Directory.GetFileSystemEntries(photos).Select(Path.GetFileName));
    }

    [Fact(DisplayName = "T-B-03: --benchmark-all/--benchmark-actions report folder override equal to or inside the photo folder is refused")]
    public void ResolveReportDirectory_Overlapping_IsRefused()
    {
        var photos = Photos("batch-photos");
        Assert.Throws<InvalidOperationException>(() => BenchmarkCliArguments.ResolveReportDirectory(photos, photos));
        Assert.Throws<InvalidOperationException>(() => BenchmarkCliArguments.ResolveReportDirectory(photos, Path.Combine(photos, "r")));
        var sibling = _root.Combine("batch-reports");
        Assert.Equal(Path.GetFullPath(sibling), BenchmarkCliArguments.ResolveReportDirectory(photos, sibling));
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Reports"), BenchmarkCliArguments.ResolveReportDirectory(photos, null), StringComparison.OrdinalIgnoreCase);
    }

    // ---- T-B-15 ----------------------------------------------------------------------------------

    private static IoDecodeSplit.Measurement M(double v) => new(v, v, v, 10);

    private static IoDecodeSplit.FileResult FakeResult(int index, StringBuilder raw)
    {
        raw.Append(index).Append(",read,0,0,1.000,10\n");
        var result = new IoDecodeSplit.FileResult { Index = index, SourceBytes = 1000, OriginalWidth = 4000, OriginalHeight = 3000 };
        result.Read = result.HeaderOnly = result.PngEncode = result.PngDecode = result.Decode2560Mem = M(1);
        result.DecodeFromMem[0] = M(2);
        result.DecodeFromFile[0] = M(3);
        return result;
    }

    private string ThreeImages()
    {
        var dir = _root.Dir("io-photos");
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" }) File.WriteAllBytes(Path.Combine(dir, name), [1, 2, 3]);
        return dir;
    }

    [Fact(DisplayName = "T-B-15: one failing image becomes an error row, the other images are measured, reports are written, exit code is 1")]
    public async Task IoDecodeSplit_OneBadImage_IsReportedAndTheRunContinues()
    {
        var photos = ThreeImages();
        var outDir = _root.Combine("io-out");

        var exit = await IoDecodeSplit.RunAsync(photos, outDir, [0], 10, (index, _, _, raw) =>
        {
            if (index == 1)
            {
                raw.Append("1,read,0,0,9.000,10\n"); // a half-measured file leaves rows behind
                throw new NotSupportedException("No imaging component suitable");
            }

            return FakeResult(index, raw);
        });

        Assert.Equal(1, exit);
        var rawLines = File.ReadAllLines(Path.Combine(outDir, "raw.csv"));
        Assert.Contains("1,error,0,0,0,0", rawLines);
        Assert.DoesNotContain("1,read,0,0,9.000,10", rawLines);
        Assert.Contains("0,read,0,0,1.000,10", rawLines);
        Assert.Contains("2,read,0,0,1.000,10", rawLines);
        var summary = File.ReadAllText(Path.Combine(outDir, "summary.md"));
        Assert.Contains("Files failed: 1", summary, StringComparison.Ordinal);
        Assert.Contains("index 1: NotSupportedException", summary, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "T-B-15: a non-recoverable error (cancellation) aborts the run but the files measured so far are still written")]
    public async Task IoDecodeSplit_FatalError_StillWritesTheReportsInFinally()
    {
        var photos = ThreeImages();
        var outDir = _root.Combine("io-out-fatal");

        await Assert.ThrowsAsync<OperationCanceledException>(() => IoDecodeSplit.RunAsync(photos, outDir, [0], 10, (index, _, _, raw) =>
            index == 1 ? throw new OperationCanceledException() : FakeResult(index, raw)));

        Assert.Contains("0,read,0,0,1.000,10", File.ReadAllLines(Path.Combine(outDir, "raw.csv")));
        Assert.True(File.Exists(Path.Combine(outDir, "summary.md")));
    }

    [Fact(DisplayName = "T-B-15: every image undecodable (real measurement on garbage bytes) gives reports, an error row each and exit code 1")]
    public async Task IoDecodeSplit_AllGarbage_RealMeasurement_ExitsOne()
    {
        var photos = ThreeImages();
        var outDir = _root.Combine("io-out-real");

        var exit = await IoDecodeSplit.RunAsync(photos, outDir, [0], 10);

        Assert.Equal(1, exit);
        var rawLines = File.ReadAllLines(Path.Combine(outDir, "raw.csv"));
        Assert.Equal(3, rawLines.Count(l => l.Contains(",error,", StringComparison.Ordinal)));
        Assert.Contains("No file could be measured.", File.ReadAllText(Path.Combine(outDir, "summary.md")), StringComparison.Ordinal);
    }

    [Theory(DisplayName = "T-B-15: exit code is 0 only for a fully measured run")]
    [InlineData(3, 0, 0)]
    [InlineData(2, 1, 1)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 3, 1)]
    public void IoDecodeSplit_ExitCode(int measured, int failed, int expected) =>
        Assert.Equal(expected, IoDecodeSplit.ComputeExitCode(measured, failed));

    // ---- T-B-06 ----------------------------------------------------------------------------------

    [Theory(DisplayName = "T-B-06: --benchmark / --benchmark-all reject a surplus argument instead of ignoring it")]
    [InlineData("--benchmark-all", "folder", "out", "extra")]
    [InlineData("--benchmark-actions", "folder", "out", "extra")]
    [InlineData("--benchmark", "folder", "recommended-auto", "extra")]
    public void SurplusBenchmarkArguments_AreRejected(params string[] args) =>
        Assert.Throws<ArgumentException>(() => BenchmarkCliArguments.RejectSurplusBenchmarkArguments(args));

    [Fact(DisplayName = "T-B-06: the expected-failure filter covers IO, argument, unsupported-operation and access errors but not cancellation or OOM")]
    public void IsExpectedToolFailure_Classifies()
    {
        Assert.True(BenchmarkCliArguments.IsExpectedToolFailure(new DirectoryNotFoundException()));
        Assert.True(BenchmarkCliArguments.IsExpectedToolFailure(new UnauthorizedAccessException()));
        Assert.True(BenchmarkCliArguments.IsExpectedToolFailure(new NotSupportedException()));
        Assert.True(BenchmarkCliArguments.IsExpectedToolFailure(new ArgumentException()));
        Assert.False(BenchmarkCliArguments.IsExpectedToolFailure(new OperationCanceledException()));
        Assert.False(BenchmarkCliArguments.IsExpectedToolFailure(new OutOfMemoryException())); // APP-T18: the OOM case was a duplicated cancellation line
    }

    // ---- T-B-07 / T-B-08 -------------------------------------------------------------------------

    [Fact(DisplayName = "T-B-07: the decoder-benchmark summary.md title is plain ASCII, not double-encoded mojibake")]
    public void DecoderMarkdown_TitleIsNotGarbled()
    {
        var md = DecoderBenchmark.GenerateMarkdownReport(new DecoderBenchmark.BenchmarkSummary());

        Assert.Equal("# PhotoReview - Decoder Benchmark Report", md.Split('\n')[0].TrimEnd('\r'));
    }

    [Fact(DisplayName = "T-B-08: repeated backend names and numeric spellings of the same backend run once; undefined numbers are unknown")]
    public void DecoderBackends_AreDistinctAndDefined()
    {
        var unknown = new List<string>();

        var backends = DecoderBenchmark.ResolveBackends(["Wpf", "wpf", "99", "TurboJpeg", "nonsense", ((int)DecoderBackend.Wpf).ToString(System.Globalization.CultureInfo.InvariantCulture)], unknown.Add);

        Assert.Equal([DecoderBackend.Wpf, DecoderBackend.TurboJpeg], backends);
        Assert.Equal(["99", "nonsense"], unknown);
        var (names, _, _) = DecoderBenchmark.ParseOptions(["--decoder-bench", "f", "o", "Wpf,WPF, wpf ,TurboJpeg"]);
        Assert.Equal(["Wpf", "TurboJpeg"], names);
    }

    [Fact(DisplayName = "T-B-08: a bad option is rejected before the output directory is created")]
    public async Task DecoderBench_BadWidth_DoesNotCreateTheOutputDirectory()
    {
        var photos = Photos("decoder-photos");
        var outDir = _root.Combine("decoder-out");

        await Assert.ThrowsAsync<ArgumentException>(() => DecoderBenchmark.RunAsync(["--decoder-bench", photos, outDir, "Wpf", "abc"]));

        Assert.False(Directory.Exists(outDir));
    }

    [Fact(DisplayName = "T-B-08: the total decode count does not overflow int for a large --iterations")]
    public void TotalDecodes_IsALongProduct()
    {
        var total = DecoderBenchmark.TotalDecodes(64, 100_000_000, 4, 3);

        Assert.Equal(64L * 100_000_000 * 4 * 3, total);
        Assert.True(total > int.MaxValue);
    }

    // ---- T-B-11 ----------------------------------------------------------------------------------

    private static string[] Session(params string[] extra) => ["--perf-session", Scenario, "C:/photos", "C:/out", .. extra];

    [Theory(DisplayName = "T-B-11: --mode / --decoder reject undefined numeric enum values and unknown names")]
    [InlineData("--mode", "99")]
    [InlineData("--mode", "nonsense")]
    [InlineData("--decoder", "99")]
    [InlineData("--decoder", "-1")]
    public void PerfSession_UndefinedEnums_AreRejected(string option, string value) =>
        Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(Session(option, value)));

    [Theory(DisplayName = "T-B-11: --repeat and --slow-link-latency-ms accept digits only (invariant), in range")]
    [InlineData("--repeat", "+5")]
    [InlineData("--repeat", " 5")]
    [InlineData("--repeat", "5.0")]
    [InlineData("--repeat", "0")]
    [InlineData("--repeat", "1001")]
    [InlineData("--slow-link-latency-ms", "-1")]
    [InlineData("--slow-link-latency-ms", "1e3")]
    public void PerfSession_IntegerOptions_AreStrict(string option, string value) =>
        Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(Session(option, value)));

    [Theory(DisplayName = "T-B-11: --slow-link-bandwidth-mbps rejects non-finite and non-positive numbers")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("NaN")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1e999")]
    public void PerfSession_Bandwidth_MustBeFiniteAndPositive(string value) =>
        Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(Session("--slow-link-bandwidth-mbps", value)));

    [Fact(DisplayName = "T-B-11: valid options still parse (invariant decimals under vi-VN)")]
    public void PerfSession_ValidOptions_Parse()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("vi-VN");
            var options = PerfSession.ParseArgs(Session("--mode", "preview", "--decoder", "TurboJpeg", "--repeat", "3",
                "--slow-link-latency-ms", "20", "--slow-link-bandwidth-mbps", "2.5"));

            Assert.Equal("Preview", options.Mode);
            Assert.Equal("TurboJpeg", options.Decoder);
            Assert.Equal(3, options.Repeat);
            Assert.Equal(20, options.SlowLinkLatencyMs);
            Assert.Equal(2.5, options.SlowLinkBandwidthMbps);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Theory(DisplayName = "T-B-11: a repeated option is rejected instead of silently last-wins")]
    [InlineData("--mode", "Fast", "--mode", "Preview")]
    [InlineData("--repeat", "1", "--repeat", "2")]
    [InlineData("--cache-dir", "C:/a", "--CACHE-DIR", "C:/b")]
    [InlineData("--alias", "x", "--alias", "y")]
    public void PerfSession_RepeatedOptions_AreRejected(params string[] extra)
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(Session(extra)));
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }
}
