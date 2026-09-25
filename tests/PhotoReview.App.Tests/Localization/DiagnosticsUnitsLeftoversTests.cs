using System.IO;
using PhotoReview.App.Localization;
using PhotoReview.App.ViewModels;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Platform.Windows.Explorer;
using PhotoReview.TestSupport;

namespace PhotoReview.App.Tests.Localization;

/// <summary>
/// I18N leftovers: Explorer reason codes, skipped-file reasons, benchmark errors, units and small texts render
/// from the catalog in both shipped languages. Switches the ambient localizer, hence GlobalState.
/// </summary>
[Collection("GlobalState")]
public sealed class DiagnosticsUnitsLeftoversTests : IDisposable
{
    public void Dispose() => TestLocalization.UseVietnamese();

    private static IEnumerable<string> AllReasonCodes() =>
        typeof(ExplorerReason).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void EveryExplorerReasonCode_MapsToCatalogText_InBothLanguages()
    {
        foreach (var localizer in new[] { TestLocalization.English, TestLocalization.Vietnamese })
        {
            using var scope = TestLocalization.Use(localizer);
            foreach (var code in AllReasonCodes())
            {
                var reason = ExplorerReason.Format(code, "d0", "d1");
                var text = DiagnosticsWindow.ReasonText(reason);
                Assert.NotEqual(Tr.DiagExplorerReasonUnknown(reason), text);
                Assert.NotEqual(code, text);
                Assert.DoesNotContain('|', text); // the code|detail encoding never reaches the user
            }
        }
    }

    [Fact]
    public void ReasonText_InsertsTechnicalDetailsVerbatim_AndTranslatesTheSentence()
    {
        var reason = ExplorerReason.Format(ExplorerReason.ComCallFailed, "IFolderView2.GetItem(3)", "0x80004005");
        string english, vietnamese;
        using (TestLocalization.Use(TestLocalization.English)) english = DiagnosticsWindow.ReasonText(reason);
        using (TestLocalization.Use(TestLocalization.Vietnamese)) vietnamese = DiagnosticsWindow.ReasonText(reason);

        Assert.Equal("Explorer call IFolderView2.GetItem(3) failed (HRESULT 0x80004005).", english);
        Assert.Equal("Lệnh gọi Explorer IFolderView2.GetItem(3) thất bại (HRESULT 0x80004005).", vietnamese);
    }

    [Fact]
    public void ReasonText_UnknownReason_IsWrappedNotDropped()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Assert.Equal("Unknown reason: from a newer build", DiagnosticsWindow.ReasonText("from a newer build"));
    }

    [Theory]
    [InlineData(0L, "0 byte", "0 B")]
    [InlineData(512L, "512 byte", "512 B")]
    [InlineData(1024L, "1 KB", "1 KB")]
    [InlineData(1572864L, "1.5 MB", "1.5 MB")]
    [InlineData(5368709120L, "5 GB", "5 GB")]
    public void FormatFileSize_UsesCatalogUnitWords(long bytes, string vietnamese, string english)
    {
        using (TestLocalization.Use(TestLocalization.Vietnamese)) Assert.Equal(vietnamese, StatusFormatter.FormatFileSize(bytes));
        using (TestLocalization.Use(TestLocalization.English)) Assert.Equal(english, StatusFormatter.FormatFileSize(bytes));
    }

    [Fact]
    public void SkippedEntry_WrapsTheOsMessageInATranslatedCategory()
    {
        var file = new SkippedEntry(@"C:\p\a.jpg", "OS says no");
        var listing = new SkippedEntry(@"C:\p", "disk gone", SkippedKind.ListingInterrupted);

        using (TestLocalization.Use(TestLocalization.English))
        {
            Assert.Equal(@"C:\p\a.jpg  (cannot be read: OS says no)", SkippedFilesWindow.FormatEntry(file));
            Assert.Equal(@"C:\p  (folder listing stopped early: disk gone)", SkippedFilesWindow.FormatEntry(listing));
        }
        using (TestLocalization.Use(TestLocalization.Vietnamese))
        {
            Assert.Equal(@"C:\p\a.jpg  (không đọc được: OS says no)", SkippedFilesWindow.FormatEntry(file));
            Assert.Equal(@"C:\p  (việc liệt kê thư mục dừng giữa chừng: disk gone)", SkippedFilesWindow.FormatEntry(listing));
        }
    }

    [Fact]
    public void BenchmarkDescribe_MapsLibraryProblemsToCatalogText_OtherErrorsKeepTheirMessage()
    {
        var profile = BenchmarkProfiles.Find("cache-recovery")!;
        var notImplemented = new BenchmarkProfileException(BenchmarkProfileProblem.NotImplemented, profile.Id, "English library text");

        using (TestLocalization.Use(TestLocalization.Vietnamese))
        {
            var text = BenchmarkText.Describe(notImplemented);
            Assert.Contains("chưa có bước kiểm tra thật", text, StringComparison.Ordinal);
            Assert.DoesNotContain("English library text", text, StringComparison.Ordinal);
            Assert.Equal("Lỗi không mong muốn: boom", BenchmarkText.Describe(new InvalidOperationException("boom")));
        }
        using (TestLocalization.Use(TestLocalization.English))
        {
            Assert.Contains("has no real check yet", BenchmarkText.Describe(notImplemented), StringComparison.Ordinal);
            Assert.Equal("Unexpected error: boom", BenchmarkText.Describe(new InvalidOperationException("boom")));
        }
    }

    [Fact]
    public void BenchmarkValidation_ThrowsTheTypedException_KeepingTheEnglishMessage()
    {
        var profile = BenchmarkProfiles.All[0];
        var ex = Record.Exception(() => BenchmarkProfileValidation.Validate(profile with { Workers = 0 }));

        var typed = Assert.IsType<BenchmarkProfileException>(ex);
        Assert.Equal(BenchmarkProfileProblem.InvalidSettings, typed.Problem);
        Assert.Contains(profile.Id, typed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BenchmarkRow_TimingCells_UseCatalogUnitAndNoValueText()
    {
        var profile = BenchmarkProfiles.Find("fast-sequential")!;
        var empty = new BenchmarkResultRow(profile, new BenchmarkPhaseResult(profile.Id, profile.Workload, [], BenchmarkResultStatus.InsufficientData, null));
        var measured = new BenchmarkResultRow(profile, new BenchmarkPhaseResult(profile.Id, profile.Workload, [12.0, 12.0], BenchmarkResultStatus.Pass, null));
        var failed = new BenchmarkResultRow(profile, new BenchmarkPhaseResult(profile.Id, profile.Workload, [], BenchmarkResultStatus.Fail, "raw"),
            new BenchmarkProfileException(BenchmarkProfileProblem.NotImplemented, profile.Id, "raw"));

        using (TestLocalization.Use(TestLocalization.English))
        {
            Assert.Equal("—", empty.P95Text);
            Assert.Equal("12 ms", measured.P50Text);
            Assert.StartsWith("Failed: ", failed.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain("raw", failed.StatusText, StringComparison.Ordinal);
        }
        using (TestLocalization.Use(TestLocalization.Vietnamese))
        {
            Assert.StartsWith("Thất bại: ", failed.StatusText, StringComparison.Ordinal);
        }

        // "—" and "ms" look the same in en and vi, so prove they come from the catalog with an overriding language.
        Assert.True(LanguageCatalog.TryParse("""
            { "_meta": { "code": "xx", "name": "X", "nativeName": "X", "plural": "none" },
              "bench.noValue": "n/a", "unit.milliseconds": "{value} milli" }
            """, "xx.json", out var overlay, new List<string>()));
        using (TestLocalization.Use(Localizer.Create(BuiltInCatalog.English, [overlay])))
        {
            Assert.Equal("n/a", empty.P95Text);
            Assert.Equal("12 milli", measured.P50Text);
        }
    }

    [Fact]
    public void ExportHelp_FollowsTheLanguage_AndKeepsLiteralBraces()
    {
        using (TestLocalization.Use(TestLocalization.English))
            Assert.Contains("keep {placeholders}", TranslationExport.HelpText, StringComparison.Ordinal);
        using (TestLocalization.Use(TestLocalization.Vietnamese))
        {
            Assert.Contains("giữ nguyên {placeholder}", TranslationExport.HelpText, StringComparison.Ordinal);
            Assert.Contains("<code>.json", TranslationExport.HelpText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ListSeparator_KeepsItsTrailingSpace()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Assert.Equal("; ", Tr.DiagListSeparator);
        Assert.Equal("16 ms: 3", Tr.DiagHistogramBucket("16", "3"));
    }

    [Fact]
    public async Task ExplorerService_CanceledRequest_ReportsTheStableCanceledCode_NotProse()
    {
        using var service = new ExplorerOrderService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var snapshot = await service.TryGetSnapshotAsync(Path.GetTempPath(), TimeSpan.FromSeconds(5), cts.Token);

        Assert.Equal(ExplorerOrderStatus.Canceled, snapshot.Status);
        Assert.Equal(ExplorerReason.Canceled, snapshot.Reason);
    }
}
