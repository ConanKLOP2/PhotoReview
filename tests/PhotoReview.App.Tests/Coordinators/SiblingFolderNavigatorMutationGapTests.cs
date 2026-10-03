using System.Collections.Generic;
using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.IO;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Stryker round 1 (App): where the sibling-folder search starts, and the RAW setting it honours.</summary>
public sealed class SiblingFolderNavigatorMutationGapTests : IDisposable
{
    private sealed class RecordingSink : ISiblingNavigatorSink
    {
        public List<string> Opened { get; } = [];
        public void SetStatusText(string status) { }

        public Task OpenFolderAsync(string folder, string? initialPath = null)
        {
            Opened.Add(folder);
            return Task.CompletedTask;
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-sibnav-gap-" + Guid.NewGuid().ToString("N"));
    private readonly GenerationClock _clock = new();
    private readonly ReviewCatalog _catalog = new();
    private readonly RecordingSink _sink = new();

    public SiblingFolderNavigatorMutationGapTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best effort temp cleanup */ }
    }

    private string MakeFolder(string name, string? file = "a.jpg")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (file is not null) File.WriteAllBytes(Path.Combine(dir, file), [1]);
        return dir;
    }

    private SiblingFolderNavigator Create(Func<SessionState?> session, Func<bool>? rawEnabled = null) =>
        new(_clock, _catalog, new PhysicalFileSystem(), _sink, session, rawEnabled);

    [Fact]
    public async Task NavigateSiblingFolderAsync_NoSession_StartsFromTheFolderOfTheCurrentCatalogEntry()
    {
        var current = MakeFolder("b");
        var next = MakeFolder("c");
        _catalog.Reset([Path.Combine(current, "a.jpg")]);
        var navigator = Create(() => null);

        await navigator.NavigateSiblingFolderAsync(1).WaitAsync(Wait.DefaultTimeout);

        Assert.Equal([next], _sink.Opened);
    }

    [Fact]
    public async Task NavigateSiblingFolderAsync_NoSessionAndEmptyCatalog_DoesNothing()
    {
        MakeFolder("b");
        MakeFolder("c");
        var navigator = Create(() => null);

        await navigator.NavigateSiblingFolderAsync(1).WaitAsync(Wait.DefaultTimeout);

        Assert.Empty(_sink.Opened);
    }

    [Fact]
    public async Task NavigateSiblingFolderAsync_SessionFolderDiffersFromTheCatalogEntry_TheSessionFolderWins()
    {
        MakeFolder("a");
        var sessionFolder = MakeFolder("b");
        var expected = MakeFolder("c");
        var catalogFolder = MakeFolder("d");
        MakeFolder("e");
        _catalog.Reset([Path.Combine(catalogFolder, "a.jpg")]);
        var navigator = Create(() => new SessionState { Folder = sessionFolder });

        await navigator.NavigateSiblingFolderAsync(1).WaitAsync(Wait.DefaultTimeout);

        Assert.Equal([expected], _sink.Opened);
    }

    [Fact]
    public void FindSiblingImageFolders_CurrentFolderIsTheFirstSibling_FindsTheNextOneAndNoPrevious()
    {
        var first = MakeFolder("a");
        var second = MakeFolder("b");
        var navigator = Create(() => null);

        var result = navigator.FindSiblingImageFolders(first, CancellationToken.None);

        Assert.Null(result.Previous);
        Assert.Equal(second, result.Next);
    }

    [Fact]
    public void FindSiblingImageFolders_RawSettingOn_CountsAFolderWithOnlyRawFiles()
    {
        var current = MakeFolder("a");
        var rawOnly = MakeFolder("b", "shot.cr2");
        var withRaw = Create(() => null, () => true);
        var withoutRaw = Create(() => null, () => false);

        Assert.Equal(rawOnly, withRaw.FindSiblingImageFolders(current, CancellationToken.None).Next);
        Assert.Null(withoutRaw.FindSiblingImageFolders(current, CancellationToken.None).Next);
    }
}
