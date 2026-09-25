using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>A stat that fails for a reason other than "not there" must not drop the photo from the catalog.</summary>
public sealed partial class ImagePresenterTests
{
    /// <summary>Forwards to the real file system, except GetFileStat of one path throws.</summary>
    private sealed class StatThrowingFileSystem(IFileSystem inner, string badPath, Exception error) : IFileSystem
    {
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public FileStat? GetFileStat(string path) =>
            string.Equals(path, badPath, StringComparison.OrdinalIgnoreCase) ? throw error : inner.GetFileStat(path);
        public void Move(string source, string destination) => inner.Move(source, destination);
        public void Copy(string source, string destination) => inner.Copy(source, destination);
        public void Delete(string path) => inner.Delete(path);
        public Stream OpenReadShared(string path, int bufferSize = 65536) => inner.OpenReadShared(path, bufferSize);
        public Stream OpenAppendDurable(string path) => inner.OpenAppendDurable(path);
        public Stream OpenAppend(string path, bool durable) => inner.OpenAppend(path, durable);
        public void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public IEnumerable<string> ReadLines(string path) => inner.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);
        public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(string directory, string pattern = "*") => inner.EnumerateFilesWithStat(directory, pattern);
        public IEnumerable<(string Path, FileStat? Stat)> EnumerateReadableFilesWithStat(string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped) =>
            inner.EnumerateReadableFilesWithStat(directory, include, onSkipped);
        public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
    }

    private ImagePresenter CreatePresenterWith(IFileSystem fileSystem) => new(
        _catalog,
        _clock,
        _previewService,
        _thumbnailCache,
        _preloadController,
        _compareViewModel,
        _hashService,
        _metrics,
        () => _settings,
        _sessionStore,
        _sink,
        fileSystem: fileSystem,
        getSession: () => null);

    [Fact]
    public async Task PresentAsync_WhenStatFailsWithIoError_KeepsPhotoInCatalogAndReportsError()
    {
        var flaky = CreateFakeImageFile("flaky.png");
        var other = CreateFakeImageFile("other.png");
        _catalog.Reset([flaky, other]);
        var presenter = CreatePresenterWith(new StatThrowingFileSystem(new PhysicalFileSystem(), flaky, new IOException("share hiccup")));

        await presenter.PresentAsync(0);

        Assert.Equal([flaky, other], _catalog.Paths);
        Assert.Equal(0, _catalog.CurrentIndex);
        Assert.NotEmpty(_sink.Statuses);
        Assert.Contains("share hiccup", _sink.Statuses[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresentAsync_WhenStatThrowsFileNotFound_StillTreatsThePhotoAsMissing()
    {
        var gone = Path.Combine(_tempDir, "gone.png");
        var other = CreateFakeImageFile("other.png");
        _catalog.Reset([gone, other]);
        var presenter = CreatePresenterWith(new StatThrowingFileSystem(new PhysicalFileSystem(), gone, new FileNotFoundException("gone")));

        await presenter.PresentAsync(0);

        Assert.Equal([other], _catalog.Paths);
    }

    [Fact]
    public async Task RemoveMissingCatalogItemAsync_NextPhotoStatFailsWithIoError_DoesNotSkipIt()
    {
        var missing = Path.Combine(_tempDir, "missing.png");
        var flaky = CreateFakeImageFile("flaky.png");
        _catalog.Reset([missing, flaky]);
        var presenter = CreatePresenterWith(new StatThrowingFileSystem(new PhysicalFileSystem(), flaky, new IOException("share hiccup")));
        var token = _clock.NextNavigation();

        await presenter.RemoveMissingCatalogItemAsync(missing, 0, token);

        Assert.Equal([flaky], _catalog.Paths);
    }
}
