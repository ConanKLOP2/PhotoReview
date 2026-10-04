using System.Collections.Generic;
using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.IO;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>RV-A08: sibling-folder navigation (direction validation, race with a folder switch, unreadable siblings, root folder).</summary>
public sealed class SiblingFolderNavigatorTests : IDisposable
{
    private sealed class RecordingSink : ISiblingNavigatorSink
    {
        public List<string> Statuses { get; } = [];
        public List<string> Opened { get; } = [];
        public void SetStatusText(string status) => Statuses.Add(status);
        public Task OpenFolderAsync(string folder, string? initialPath = null)
        {
            Opened.Add(folder);
            return Task.CompletedTask;
        }
    }

    /// <summary>Delegates to the real file system; EnumerateFiles can be hooked (throw / bump the clock) per directory.</summary>
    private sealed class HookedFileSystem(IFileSystem inner) : IFileSystem
    {
        public Action<string>? OnEnumerateFiles { get; set; }
        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*")
        {
            OnEnumerateFiles?.Invoke(directory);
            return inner.EnumerateFiles(directory, pattern);
        }
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public FileStat? GetFileStat(string path) => inner.GetFileStat(path);
        public void Move(string source, string destination) => inner.Move(source, destination);
        public void Copy(string source, string destination) => inner.Copy(source, destination);
        public void Delete(string path) => inner.Delete(path);
        public Stream OpenReadShared(string path, int bufferSize = 65536) => inner.OpenReadShared(path, bufferSize);
        public Stream OpenAppend(string path, bool durable) => inner.OpenAppend(path, durable);
        public void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-sibnav-" + Guid.NewGuid().ToString("N"));
    private readonly GenerationClock _clock = new();
    private readonly ReviewCatalog _catalog = new();
    private readonly RecordingSink _sink = new();
    private readonly HookedFileSystem _fs = new(new PhysicalFileSystem());

    public SiblingFolderNavigatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best effort temp cleanup */ }
    }

    private string MakeFolder(string name, bool withImage)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (withImage) File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [1]);
        return dir;
    }

    private SiblingFolderNavigator Create(string currentFolder) =>
        new(_clock, _catalog, _fs, _sink, () => new SessionState { Folder = currentFolder });

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-2)]
    public async Task NavigateSiblingFolderAsync_DirectionOtherThanPlusMinusOne_Throws(int direction)
    {
        // The middle folder has no image siblings on either side: with direction 0 the old loop spun forever.
        var current = MakeFolder("b", withImage: false);
        MakeFolder("a", withImage: false);
        MakeFolder("c", withImage: false);
        var navigator = Create(current);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => navigator.NavigateSiblingFolderAsync(direction).WaitAsync(Wait.DefaultTimeout));
        Assert.Empty(_sink.Opened);
    }

    [Fact]
    public async Task NavigateSiblingFolderAsync_Next_OpensTheNextImageFolder()
    {
        var a = MakeFolder("a", withImage: true);
        MakeFolder("b", withImage: false);
        var c = MakeFolder("c", withImage: true);

        await Create(a).NavigateSiblingFolderAsync(1).WaitAsync(Wait.DefaultTimeout);

        Assert.Equal([c], _sink.Opened);
    }

    [Fact]
    public async Task NavigateSiblingFolderAsync_FolderGenerationChangesDuringSearch_DoesNotOpenAnything()
    {
        var a = MakeFolder("a", withImage: true);
        var b = MakeFolder("b", withImage: true);
        _fs.OnEnumerateFiles = dir =>
        {
            if (string.Equals(dir, b, StringComparison.OrdinalIgnoreCase)) _clock.NextFolder(); // the user switched folder meanwhile
        };

        await Create(a).NavigateSiblingFolderAsync(1).WaitAsync(Wait.DefaultTimeout);

        Assert.Empty(_sink.Opened);
        Assert.Empty(_sink.Statuses);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NavigateSiblingFolderAsync_UnreadableSibling_IsSkipped(bool accessDenied)
    {
        var a = MakeFolder("a", withImage: true);
        var b = MakeFolder("b", withImage: true);
        var c = MakeFolder("c", withImage: true);
        _fs.OnEnumerateFiles = dir =>
        {
            if (string.Equals(dir, b, StringComparison.OrdinalIgnoreCase))
            {
                throw accessDenied ? new UnauthorizedAccessException("denied") : new IOException("locked");
            }
        };

        await Create(a).NavigateSiblingFolderAsync(1).WaitAsync(Wait.DefaultTimeout);

        Assert.Equal([c], _sink.Opened);
    }

    [Fact]
    public async Task NavigateSiblingFolderAsync_RootFolderWithoutParent_IsANoOp()
    {
        var driveRoot = Path.GetPathRoot(_root)!;

        await Create(driveRoot).NavigateSiblingFolderAsync(1).WaitAsync(Wait.DefaultTimeout);

        Assert.Empty(_sink.Opened);
    }
}
