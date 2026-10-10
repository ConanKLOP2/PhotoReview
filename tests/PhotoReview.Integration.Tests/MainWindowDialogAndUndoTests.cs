using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.App;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Audit D-02 / D-05 on the real <see cref="MainWindow"/>: the user-facing dialogs of a Delete really appear (recorded through
/// <see cref="TestHostHooks.Dialogs"/>, never a real MessageBox), and an Undo of a Delete puts the photo back in place without
/// reloading the folder (#386). Nothing touches the real Recycle Bin: the bin is <see cref="StashRecycleBin"/>, a folder inside
/// the test's temp root.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class MainWindowDialogAndUndoTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DrainWindow = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// D-02 (a): a Delete on a drive without a Recycle Bin (permanent delete off, the default) is refused with ONE error
    /// dialog that names the file; the photo stays on screen at its place, nothing is asked, nothing reaches the bin, the file stays.
    /// </summary>
    [Fact]
    public async Task Delete_OnDriveWithoutRecycleBin_ShowsOneErrorAndKeepsThePhotoInPlace()
    {
        using var root = new TempRoot("ui-dialog-nobin");
        using var dataRoot = new DataRootFixture();
        var folder = root.Dir("images");
        var bin = new StashRecycleBin(root.Dir("bin")) { CanRecycleAnything = false };
        var dialogs = new RecordingDialogs();
        var presented = new List<string>();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteImages(folder, "a.png", "b.png", "c.png");
                window = TestAppHost.CreateMainWindow(folder, new TestHostHooks { OnPresented = presented.Add, RecycleBin = bin, Dialogs = dialogs });
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, Timeout), $"No image presented. {window.StatusText.Text}");
                var before = window.Files.ToArray();
                Assert.False(window.Settings.AllowPermanentDeleteWithoutRecycleBin);

                await window.ViewModel.RecycleAsync();
                await StaTestHost.DrainAsync(DrainWindow);

                var error = Assert.Single(dialogs.Errors);
                Assert.Equal(Tr.DialogRecycleNoBinTitle, error.Title);
                Assert.Contains("a.png", error.Message, StringComparison.Ordinal);
                Assert.Empty(dialogs.Confirmations);
                Assert.Empty(bin.Recycled);
                Assert.True(File.Exists(Path.Combine(folder, "a.png")));
                Assert.Equal(before, window.Files.ToArray());
                Assert.Equal(Path.Combine(folder, "a.png"), window.ViewModel.Catalog.Current?.Path, ignoreCase: true);
                // The refused Delete must not have advanced the view (INV-3 applies only to an action that reaches its I/O).
                Assert.Equal([Path.Combine(folder, "a.png")], presented.ToArray(), StringComparer.OrdinalIgnoreCase);
            });
        }
        finally
        {
            await CloseAsync(window);
        }
    }

    /// <summary>D-02 (b): "confirm before delete" asks BEFORE anything is deleted; answering No leaves everything as it was.</summary>
    [Fact]
    public async Task Delete_WithConfirmBeforeDelete_AsksFirst_AndNoKeepsTheFile()
    {
        using var root = new TempRoot("ui-dialog-confirm-no");
        using var dataRoot = new DataRootFixture();
        var folder = root.Dir("images");
        var bin = new StashRecycleBin(root.Dir("bin"));
        var dialogs = new RecordingDialogs { ConfirmationAnswer = (_, _) => false };
        var presented = new List<string>();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteImages(folder, "a.png", "b.png");
                window = TestAppHost.CreateMainWindow(folder, new TestHostHooks { OnPresented = presented.Add, RecycleBin = bin, Dialogs = dialogs });
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, Timeout), $"No image presented. {window.StatusText.Text}");
                EnableConfirmBeforeDelete(window);
                var before = window.Files.ToArray();

                await window.ViewModel.RecycleAsync();
                await StaTestHost.DrainAsync(DrainWindow);

                var asked = Assert.Single(dialogs.Confirmations);
                Assert.Equal(Tr.DialogConfirmActionTitle, asked.Title);
                Assert.Empty(dialogs.Errors);
                Assert.Empty(bin.Recycled);
                Assert.True(File.Exists(Path.Combine(folder, "a.png")));
                Assert.Equal(before, window.Files.ToArray());
                Assert.Equal([Path.Combine(folder, "a.png")], presented.ToArray(), StringComparer.OrdinalIgnoreCase);
            });
        }
        finally
        {
            await CloseAsync(window);
        }
    }

    /// <summary>D-02 (b), the other answer: Yes deletes, and only after the question was asked.</summary>
    [Fact]
    public async Task Delete_WithConfirmBeforeDelete_YesDeletesOnlyAfterTheQuestion()
    {
        using var root = new TempRoot("ui-dialog-confirm-yes");
        using var dataRoot = new DataRootFixture();
        var folder = root.Dir("images");
        var bin = new StashRecycleBin(root.Dir("bin"));
        var recycledWhenAsked = -1;
        var dialogs = new RecordingDialogs { ConfirmationAnswer = (_, _) => true };
        dialogs.OnConfirmation = (_, _) => recycledWhenAsked = bin.Recycled.Count;
        var presented = new List<string>();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteImages(folder, "a.png", "b.png");
                window = TestAppHost.CreateMainWindow(folder, new TestHostHooks { OnPresented = presented.Add, RecycleBin = bin, Dialogs = dialogs });
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, Timeout), $"No image presented. {window.StatusText.Text}");
                EnableConfirmBeforeDelete(window);

                await window.ViewModel.RecycleAsync();
                Assert.True(await StaTestHost.WaitForAsync(() => !window.ViewModel.IsFileActionInProgress && bin.Recycled.Count == 1, Timeout));

                Assert.Single(dialogs.Confirmations);
                Assert.Equal(0, recycledWhenAsked);
                Assert.Empty(dialogs.Errors);
                Assert.Equal([Path.Combine(folder, "a.png")], bin.Recycled.ToArray(), StringComparer.OrdinalIgnoreCase);
                Assert.False(File.Exists(Path.Combine(folder, "a.png")));
                Assert.Equal([Path.Combine(folder, "b.png")], window.Files.ToArray(), StringComparer.OrdinalIgnoreCase);
            });
        }
        finally
        {
            await CloseAsync(window);
        }
    }

    /// <summary>
    /// D-05 (#386): Delete the photo being viewed, then Undo. The photo comes back at its old position, the photo on screen
    /// stays the one the user moved on to, and the folder is not reloaded (no second present of anything).
    /// </summary>
    [Fact]
    public async Task UndoOfDelete_RestoresThePhotoInPlace_WithoutReloadingTheView()
    {
        using var root = new TempRoot("ui-undo-inplace");
        using var dataRoot = new DataRootFixture();
        var folder = root.Dir("images");
        var bin = new StashRecycleBin(root.Dir("bin"));
        var dialogs = new RecordingDialogs();
        var presented = new List<string>();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteImages(folder, "a.png", "b.png", "c.png", "d.png");
                string P(string name) => Path.Combine(folder, name);
                window = TestAppHost.CreateMainWindow(folder, new TestHostHooks { OnPresented = presented.Add, RecycleBin = bin, Dialogs = dialogs });
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, Timeout), $"No image presented. {window.StatusText.Text}");

                await window.ViewModel.NextAsync(); // now viewing b
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count == 2, Timeout));
                Assert.Equal(P("b.png"), window.ViewModel.Catalog.Current?.Path, ignoreCase: true);

                await window.ViewModel.RecycleAsync(); // deletes b, the view moves on to c
                Assert.True(await StaTestHost.WaitForAsync(
                    () => !window.ViewModel.IsFileActionInProgress && presented.Count == 3 && bin.Recycled.Count == 1, Timeout));
                Assert.Equal([P("a.png"), P("c.png"), P("d.png")], window.Files.ToArray(), StringComparer.OrdinalIgnoreCase);
                Assert.Equal(P("c.png"), window.ViewModel.Catalog.Current?.Path, ignoreCase: true);
                var presentedBeforeUndo = presented.Count;

                await window.ViewModel.UndoAsync();
                await StaTestHost.DrainAsync(DrainWindow);

                Assert.Equal([P("b.png")], bin.Restored.ToArray(), StringComparer.OrdinalIgnoreCase);
                Assert.True(File.Exists(P("b.png")));
                // Back at its review position, not appended or re-sorted into a reloaded folder.
                Assert.Equal([P("a.png"), P("b.png"), P("c.png"), P("d.png")], window.Files.ToArray(), StringComparer.OrdinalIgnoreCase);
                // The photo on screen is still c and nothing was presented again (a reload would present b).
                Assert.Equal(P("c.png"), window.ViewModel.Catalog.Current?.Path, ignoreCase: true);
                Assert.Equal(presentedBeforeUndo, presented.Count);
                Assert.Empty(dialogs.Errors);
            });
        }
        finally
        {
            await CloseAsync(window);
        }
    }

    private static void EnableConfirmBeforeDelete(MainWindow window)
    {
        var store = (SettingsStore)typeof(MainWindow).GetField("_settingsStore", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.GetValue(window)!;
        var settings = store.Current;
        settings.ConfirmBeforeDelete = true;
        store.Save(settings);
    }

    private static async Task CloseAsync(MainWindow? window)
    {
        if (window is null) return;
        await StaTestHost.RunAsync(() =>
        {
            try { window.Close(); } catch (InvalidOperationException) { }
            return Task.CompletedTask;
        });
    }

    /// <summary>Written on the STA thread: the encoder is a DispatcherObject.</summary>
    private static void WriteImages(string folder, params string[] names)
    {
        var pixels = new byte[16 * 4 * 16];
        Array.Fill(pixels, (byte)0x90);
        foreach (var name in names)
        {
            var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, name));
            encoder.Save(stream);
        }
    }

    /// <summary>
    /// "Recycling" moves the file into a stash folder of the test's temp root and "restoring" moves it back, so Undo has a
    /// real file to bring back; nothing reaches the user's Recycle Bin.
    /// </summary>
    private sealed class StashRecycleBin(string stash) : IRecycleBin
    {
        public bool CanRecycleAnything { get; init; } = true;
        public List<string> Recycled { get; } = [];
        public List<string> Restored { get; } = [];

        public bool CanRecycle(string path) => CanRecycleAnything;

        public void SendToRecycleBin(string path)
        {
            if (!CanRecycleAnything) throw new InvalidOperationException("No Recycle Bin on this volume (test).");
            File.Move(path, Path.Combine(stash, Path.GetFileName(path)));
            Recycled.Add(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            var stashed = Path.Combine(stash, Path.GetFileName(originalPath));
            if (!File.Exists(stashed)) return false;
            File.Move(stashed, originalPath);
            Restored.Add(originalPath);
            return true;
        }
    }
}
