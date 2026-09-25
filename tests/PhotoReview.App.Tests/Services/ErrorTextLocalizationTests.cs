using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Caching;
using PhotoReview.TestSupport;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// User-visible error text raised by App services / shown by App coordinators (ADR 0006, keys <c>err.io.*</c>):
/// exception types stay as they are, the UI shows the localized sentence in the current language.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "HotPath")]
public sealed class ErrorTextLocalizationTests : IDisposable
{
    private readonly TempRoot _root = new("app-error-text");

    public void Dispose() => _root.Dispose();

    private static (string English, string Vietnamese) Describe(Exception ex)
    {
        string english, vietnamese;
        using (TestLocalization.Use(TestLocalization.English)) english = UserFacingError.Describe(ex);
        using (TestLocalization.Use(TestLocalization.Vietnamese)) vietnamese = UserFacingError.Describe(ex);
        return (english, vietnamese);
    }

    [Fact(DisplayName = "FileHashService: a file modified while hashed (stream path) throws a localized IOException")]
    public async Task Hash_FileChangedWhileHashing_IsLocalized()
    {
        var path = _root.File("big.bin", new byte[32 * 1024 * 1024]);
        var service = new FileHashService();

        IOException ex;
        using (new FileToucher(path))
        {
            ex = await Assert.ThrowsAsync<IOException>(() => service.GetAsync(path));
        }

        var (english, vietnamese) = Describe(ex);
        Assert.Equal($"The file changed while its hash was being computed: {path}", english);
        Assert.Equal($"Tệp đã thay đổi trong lúc tính hash: {path}", vietnamese);
        Assert.Equal($"File changed while hashing: {path}", ex.Message);
    }

    [Fact(DisplayName = "FileHashService: a file modified while hashed from the source-bytes cache throws the same localized error")]
    public async Task Hash_FileChangedWhileHashing_SourceBytesPath_IsLocalized()
    {
        var path = _root.File("big-cached.bin", new byte[32 * 1024 * 1024]);
        var service = new FileHashService(new SourceBytesCache(256L * 1024 * 1024));

        IOException ex;
        using (new FileToucher(path))
        {
            ex = await Assert.ThrowsAsync<IOException>(() => service.GetAsync(path));
        }

        // Either the bytes read or the hash verification notices the change; both are localized.
        Assert.True(UserFacingError.IsLocalized(ex));
        Assert.Contains(path, UserFacingError.Describe(ex), StringComparison.Ordinal);
        Assert.NotEqual(ex.Message, UserFacingError.Describe(ex));
    }

    [Fact(DisplayName = "Duplicate check failure shows the localized sentence in the status bar (English and Vietnamese)")]
    public async Task DuplicateCheckFailure_ShowsLocalizedSentence()
    {
        var photo = _root.File("a.jpg", 1, 2, 3);
        var twin = _root.File("b.jpg", 1, 2, 3); // same size: the finder must hash both
        var catalog = new ReviewCatalog();
        catalog.Reset([photo, twin]);
        var paths = new AppPaths(_root.Dir("data"));
        var fileSystem = new PhysicalFileSystem();
        var fileActions = new FileActionService(new OperationJournal(paths, fileSystem, new SystemClock()), fileSystem, new SystemClock(), new NoRecycleBin());

        foreach (var (localizer, expected) in new[]
        {
            (TestLocalization.Vietnamese, "Lỗi kiểm tra trùng lặp: Không thể kiểm tra trùng lặp vì thiếu dịch vụ hash tệp."),
            (TestLocalization.English, "Duplicate check failed: Duplicate detection is unavailable because the file hash service is missing."),
        })
        {
            var sink = new RecordingSink();
            var controller = new DuplicateCleanupController(
                new GenerationClock(), catalog, fileActions, hashService: null, fileSystem, dialogService: null,
                new InlineUiScheduler(), preloadController: null, thumbnailCache: null, previewService: null, sink);

            using (TestLocalization.Use(localizer))
            {
                await controller.RemoveDuplicatesAsync(removeNumbered: false);
            }

            Assert.Equal(expected, sink.LastStatus);
        }
    }

    private sealed class RecordingSink : IDuplicateCleanupSink
    {
        public string? LastStatus { get; private set; }
        public void SetStatusText(string status) => LastStatus = status;
        public Task OpenFolderAsync(string folder, string? initialPath = null) => Task.CompletedTask;
    }

    private sealed class InlineUiScheduler : IUiScheduler
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class NoRecycleBin : IRecycleBin
    {
#pragma warning disable CA1822 // interface members
        public void Recycle(string path) { }
        public void SendToRecycleBin(string path) { }
        public bool TryRestore(string path, long length, DateTime lastWriteUtc) => false;
        public bool IsAccessible => true;
#pragma warning restore CA1822
    }
}
