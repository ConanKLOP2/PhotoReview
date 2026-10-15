using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// FEAT-RENAME PR2: Ctrl+Z of a rename (<see cref="FileActionController.UndoLastAsync"/>) -- re-keyed in place, protected like
/// the rename itself, and its interplay with the single Undo slot (Q3).
/// </summary>
public sealed class FileActionControllerRenameUndoTests : IDisposable
{
    private readonly RenameHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task UndoLast_AfterSingleRename_RekeysBackInPlaceWithoutInsertingOrPresenting()
    {
        var (a, b, c) = _h.LoadThree(current: 1);
        var x = _h.P("x.jpg");
        await _h.Controller.RenameAsync(null, b, "x");
        var renameCatalogChanges = _h.Sink.CatalogChanged.Count;
        _h.Sink.Presented.Clear();
        _h.Sink.Renamed.Clear();
        _h.Log.Clear();

        var result = await _h.Controller.UndoLastAsync(_h.Root);

        Assert.True(result!.Succeeded);
        Assert.Equal([a, b, c], _h.Catalog.Paths);
        Assert.Equal(1, _h.Catalog.CurrentIndex);
        Assert.Equal(3, _h.Catalog.Count);
        Assert.Equal(2222, _h.Catalog.EntriesSnapshot()[1].Length); // still the entry the listing had, not a re-inserted one
        Assert.True(File.Exists(b));
        Assert.False(File.Exists(x));
        var map = Assert.Single(_h.Sink.Renamed);
        Assert.Equal(new Dictionary<string, string> { [x] = b }, map);
        Assert.Empty(_h.Sink.Presented);
        Assert.Equal(renameCatalogChanges, _h.Sink.CatalogChanged.Count); // no InsertRestoredMove / OnCatalogChanged
        Assert.Equal(Tr.StatusRenameUndone("b.jpg"), _h.Sink.LastStatus);
        Assert.Equal(["Cancel", "UndoIO", "OnFilesRenamed", "Preload:1"], _h.Log);
    }

    [Fact]
    public async Task UndoLast_ClockAndPreloadAreStoppedBeforeTheUndoIo()
    {
        var (_, b, _) = _h.LoadThree(current: 1);
        await _h.Controller.RenameAsync(null, b, "x");
        var navigation = _h.Clock.CurrentNavigation;

        await _h.Controller.UndoLastAsync(_h.Root);

        var io = _h.UndoIo!;
        Assert.True(io.Navigation > navigation, "StopForAction must precede the undo's file move");
        Assert.Contains(_h.P("x.jpg"), io.CatalogPaths);
    }

    [Fact]
    public async Task UndoLast_SinkCannotKeepShownImage_PresentsTheCurrentIndex()
    {
        var (_, b, _) = _h.LoadThree(current: 1);
        await _h.Controller.RenameAsync(null, b, "x");
        _h.Sink.Presented.Clear();
        _h.Sink.Keep = false;

        await _h.Controller.UndoLastAsync(_h.Root);

        Assert.Equal([1], _h.Sink.Presented);
    }

    [Fact]
    public async Task UndoLast_AfterCaptureGroupRename_UsesTheRememberedNamesForEveryMember()
    {
        var (before, jpeg, raw, xmp, after) = _h.LoadCapture(withXmp: true);
        await _h.Controller.RenameAsync(null, jpeg, "pic");
        _h.Sink.Renamed.Clear();
        _h.Sink.Presented.Clear();

        var result = await _h.Controller.UndoLastAsync(_h.Root);

        Assert.True(result!.Succeeded);
        Assert.Equal([before, jpeg, after], _h.Catalog.Paths);
        var group = _h.Catalog.EntriesSnapshot()[1].CaptureGroup;
        Assert.NotNull(group);
        Assert.Equal(jpeg, group!.JpegPath);
        Assert.Equal(raw, group.RawPath);
        Assert.Equal(xmp, group.XmpPath);
        var map = Assert.Single(_h.Sink.Renamed);
        Assert.Equal(_h.P("pic.jpg"), map.Where(pair => pair.Value == jpeg).Select(pair => pair.Key).Single());
        Assert.Equal(_h.P("pic.cr2"), map.Where(pair => pair.Value == raw).Select(pair => pair.Key).Single());
        Assert.True(File.Exists(jpeg) && File.Exists(raw) && File.Exists(xmp!));
        Assert.Empty(_h.Sink.Presented);
    }

    [Fact]
    public async Task UndoLast_AfterAMoveRegisteredAfterTheRename_IsTheMovesUndoAndTheRenameStays()
    {
        var (_, b, c) = _h.LoadThree(current: 1);
        var x = _h.P("x.jpg");
        await _h.Controller.RenameAsync(null, b, "x");
        await _h.Controller.RunActionAsync(0, null, x); // Move x.jpg to Sorted: replaces the single Undo slot
        var movedTo = Path.Combine(_h.Root, "Sorted", "x.jpg");
        Assert.True(File.Exists(movedTo));

        var undoMove = await _h.Controller.UndoLastAsync(_h.Root);

        Assert.True(undoMove!.Succeeded);
        Assert.Equal(FileOperationType.Move, undoMove.Operation);
        Assert.True(File.Exists(x)); // back under the renamed name
        Assert.False(File.Exists(b)); // the rename was NOT undone
        Assert.False(File.Exists(movedTo));
        Assert.Contains(x, _h.Catalog.Paths);

        var second = await _h.Controller.UndoLastAsync(_h.Root);

        Assert.False(second!.Succeeded);
        Assert.True(File.Exists(x));
        Assert.False(File.Exists(b));
        Assert.Contains(c, _h.Catalog.Paths);
    }

    [Fact]
    public async Task UndoLast_RenameAfterAMove_ReplacesTheMovesUndo()
    {
        var (a, b, _) = _h.LoadThree(current: 1);
        var x = _h.P("x.jpg");
        await _h.Controller.RunActionAsync(0, null, a); // Move a.jpg to Sorted
        var movedA = Path.Combine(_h.Root, "Sorted", "a.jpg");
        Assert.True(File.Exists(movedA));
        await _h.Controller.RenameAsync(null, b, "x");
        Assert.True(File.Exists(x));

        var first = await _h.Controller.UndoLastAsync(_h.Root);

        Assert.True(first!.Succeeded);
        Assert.True(File.Exists(b)); // the rename was undone
        Assert.False(File.Exists(x));

        var second = await _h.Controller.UndoLastAsync(_h.Root);

        Assert.False(second!.Succeeded); // the Move's undo was replaced by the rename (Q3): nothing left to undo
        Assert.True(File.Exists(movedA));
        Assert.False(File.Exists(a));
    }

    [Fact]
    public async Task UndoLast_UndoIoFails_ReArmsWithAnEmptyMapAndShowsTheError()
    {
        var (a, b, c) = _h.LoadThree(current: 1);
        var x = _h.P("x.jpg");
        await _h.Controller.RenameAsync(null, b, "x");
        _h.Sink.Renamed.Clear();
        _h.Sink.Presented.Clear();
        _h.UndoHook = _ => throw new IOException("undo refused");

        var result = await _h.Controller.UndoLastAsync(_h.Root);

        Assert.False(result!.Succeeded);
        Assert.Equal([a, x, c], _h.Catalog.Paths); // still the renamed name
        Assert.True(File.Exists(x));
        var map = Assert.Single(_h.Sink.Renamed);
        Assert.Empty(map);
        Assert.Equal(result.ErrorMessage, _h.Sink.LastStatus);
        Assert.Contains("undo refused", _h.Sink.LastStatus, StringComparison.Ordinal);
        Assert.Empty(_h.Sink.Presented);
    }

    [Fact]
    public async Task UndoLast_RenameMadeInAnotherFolder_FallsThroughToTheGenericPath()
    {
        var (_, b, _) = _h.LoadThree(current: 1);
        var x = _h.P("x.jpg");
        await _h.Controller.RenameAsync(null, b, "x");
        _h.Sink.Renamed.Clear();
        var otherFolder = Path.Combine(_h.Root, "Elsewhere");
        Directory.CreateDirectory(otherFolder);

        var result = await _h.Controller.UndoLastAsync(otherFolder);

        Assert.True(result!.Succeeded);
        Assert.True(File.Exists(b)); // undone on disk
        Assert.Empty(_h.Sink.Renamed); // the rename-undo handler did not claim it: the caller reloads the folder that owns it
        Assert.Contains(x, _h.Catalog.Paths); // this folder's catalog is not touched
    }
}
