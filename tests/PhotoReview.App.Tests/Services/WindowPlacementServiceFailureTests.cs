using System.IO;
using System.Windows;
using System.Windows.Interop;
using PhotoReview.TestSupport;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// The window placement file is best effort: a damaged file or an unwritable folder must never stop the app from
/// starting or closing, and must leave the window and any previous file as they were.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class WindowPlacementServiceFailureTests
{
    private static string PlacementOf(Window window, TempRoot root, string name)
    {
        var path = root.Combine(name);
        WindowPlacementService.Save(window, path);
        return File.ReadAllText(path);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public void Restore_ADamagedPlacementFile_DoesNotThrowAndLeavesTheWindowUnchanged(string content)
    {
        using var root = new TempRoot("placement-bad");
        StaUi.Run(() =>
        {
            var window = new Window();
            new WindowInteropHelper(window).EnsureHandle();
            var before = PlacementOf(window, root, "before.json");
            var damaged = root.Combine("damaged.json");
            File.WriteAllText(damaged, content);

            WindowPlacementService.Restore(window, damaged);

            Assert.Equal(before, PlacementOf(window, root, "after.json"));
            window.Close();
        });
    }

    [Fact]
    public void Restore_AMissingPlacementFile_LeavesTheWindowUnchanged()
    {
        using var root = new TempRoot("placement-missing");
        StaUi.Run(() =>
        {
            var window = new Window();
            new WindowInteropHelper(window).EnsureHandle();
            var before = PlacementOf(window, root, "before.json");

            WindowPlacementService.Restore(window, root.Combine("never-written.json"));

            Assert.Equal(before, PlacementOf(window, root, "after.json"));
            window.Close();
        });
    }

    [Fact]
    public void Save_WhenTheFolderCannotBeCreated_DoesNotThrowAndWritesNothing()
    {
        using var root = new TempRoot("placement-unwritable");
        StaUi.Run(() =>
        {
            var blocker = root.File("not-a-folder.txt", 1);
            var path = Path.Combine(blocker, "sub", "placement.json"); // a directory cannot be created below a file
            var window = new Window();
            new WindowInteropHelper(window).EnsureHandle();

            WindowPlacementService.Save(window, path);

            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.Combine(blocker, "sub")));
            Assert.Equal([blocker], Directory.GetFileSystemEntries(root.Path));
            window.Close();
        });
    }

    [Fact]
    public void Save_WhenThePlacementFileIsAFolder_KeepsTheFolderAndLeavesNoTempFile()
    {
        using var root = new TempRoot("placement-dir");
        StaUi.Run(() =>
        {
            var path = root.Dir("placement.json"); // the target name is taken by a directory, so the final move fails
            var window = new Window();
            new WindowInteropHelper(window).EnsureHandle();

            WindowPlacementService.Save(window, path);

            Assert.True(Directory.Exists(path));
            Assert.Equal([path], Directory.GetFileSystemEntries(root.Path)); // the *.tmp from the failed write is cleaned up
            window.Close();
        });
    }
}
