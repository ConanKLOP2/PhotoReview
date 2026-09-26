using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoReview.App;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Integration.Tests.Infrastructure;
using Xunit;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// T14c: locks INV-5 on the real <see cref="MainWindow"/>, driven through the T14a
/// seam (<c>TestHostHooks</c> via <c>TestAppHost</c>) on <see cref="StaTestHost"/>.
/// <para>
/// INV-5: An action that completes after the user has navigated away to a different folder
/// must not modify the catalog of the newly opened folder. APP-03 (Q-R25 option B): a late
/// action that succeeded is still registered for Undo; a late failure is dropped (no restore
/// into the new folder's catalog).
/// </para>
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Slow")]
public sealed class MainWindowBehaviorFolderSwitchTests
{
    private const Key ActionKey = Key.F3;

    private static readonly TimeSpan PresentTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainWindow = TimeSpan.FromMilliseconds(400);

    [Fact(DisplayName = "Control: Move without folder switch registers undo and updates catalog upon undo")]
    public async Task MoveWithoutFolderSwitchRegistersUndoAndUpdatesCatalog()
    {
        using var root = new TempRoot("t14c-ctrl");
        using var dataRoot = new DataRootFixture();
        var folder = root.Dir("images");
        var destination = root.Dir("sorted");
        var first = Path.Combine(folder, "a1.png");
        var second = Path.Combine(folder, "a2.png");

        var presented = new List<string>();
        var moveInvocations = 0;
        var moveCompleted = 0;
        MainWindow? window = null;

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(folder, "a1.png", "a2.png");
                var hooks = new TestHostHooks
                {
                    OnPresented = presented.Add,
                    MoveOverride = async (source, target) =>
                    {
                        Interlocked.Increment(ref moveInvocations);
                        await Task.Run(() => File.Move(source, target));
                        Volatile.Write(ref moveCompleted, 1);
                    },
                };
                window = TestAppHost.CreateMainWindow(folder, hooks);

                Assert.True(
                    await StaTestHost.WaitForAsync(() => presented.Count > 0, PresentTimeout),
                    $"The folder never presented its first image. StatusText={window.StatusText.Text}");
                Assert.Equal(first, presented[0], ignoreCase: true);

                InstallTestAction(window, destination);
                Assert.True(PressKey(window, ActionKey), "The key press did not reach the action branch of Window_KeyDown.");

                Assert.True(
                    await StaTestHost.WaitForAsync(() => Volatile.Read(ref moveCompleted) == 1 && FileActionInProgress(window) == 0, SettleTimeout),
                    "The move action did not complete.");

                await DrainAsync(DrainWindow);

                // Verification of normal (non-switched) Move outcome:
                var moveHistory = GetMoveHistory(window);
                Assert.Single(moveHistory);
                Assert.NotNull(GetLastUndoAction(window));

                var catalogAfterMove = window.Files;
                Assert.DoesNotContain(first, catalogAfterMove, StringComparer.OrdinalIgnoreCase);
                Assert.Contains(second, catalogAfterMove, StringComparer.OrdinalIgnoreCase);

                var movedTarget = Path.Combine(destination, "a1.png");
                Assert.True(File.Exists(movedTarget), "The file was not moved to the destination.");
                Assert.False(File.Exists(first), "The source file still exists in the source folder.");

                // Now execute Undo:
                await TriggerUndoAsync(window);
                Assert.True(
                    await StaTestHost.WaitForAsync(() => FileActionInProgress(window) == 0, SettleTimeout),
                    "The undo action never released its in-flight guard.");
                await DrainAsync(DrainWindow);

                // Undo restored the file to the source and re-added it to the catalog:
                Assert.True(File.Exists(first), "The undone file was not restored to the source folder.");
                Assert.False(File.Exists(movedTarget), "The destination file still exists after undo.");

                var catalogAfterUndo = window.Files;
                Assert.Contains(first, catalogAfterUndo, StringComparer.OrdinalIgnoreCase);
                Assert.Empty(GetMoveHistory(window));
                Assert.Null(GetLastUndoAction(window));

                // AR04 / ADR 0005: open folder -> present -> move -> present next -> undo -> present,
                // all through the production graph; no sink update may arrive off the UI thread.
                Assert.Equal(0, window.Metrics.Snapshot().CrossThreadPresentCount);
            });
        }
        finally
        {
            await CloseAsync(window);
        }
    }

    [Fact(DisplayName = "INV-5 + APP-03: Move completing after folder switch leaves the new catalog alone, registers undo, and Undo restores into the old folder")]
    public async Task MoveCompletingAfterFolderSwitchDoesNotMutateNewCatalogOrRegisterUndo()
    {
        using var root = new TempRoot("t14c-inv5");
        using var dataRoot = new DataRootFixture();
        var folderA = root.Dir("imagesA");
        var folderB = root.Dir("imagesB");
        var destination = root.Dir("sorted");

        var fileA1 = Path.Combine(folderA, "a1.png");
        var fileA2 = Path.Combine(folderA, "a2.png");
        var fileB1 = Path.Combine(folderB, "b1.png");
        var fileB2 = Path.Combine(folderB, "b2.png");

        var presented = new List<string>();
        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moveInvocations = 0;
        var moveCompleted = 0;
        MainWindow? window = null;

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(folderA, "a1.png", "a2.png");
                WriteTestImages(folderB, "b1.png", "b2.png");

                var hooks = new TestHostHooks
                {
                    OnPresented = presented.Add,
                    MoveOverride = async (source, target) =>
                    {
                        Interlocked.Increment(ref moveInvocations);
                        // Block on the gate so folder switch happens while the move is in flight
                        await moveGate.Task;
                        await Task.Run(() => File.Move(source, target));
                        Volatile.Write(ref moveCompleted, 1);
                    },
                };
                window = TestAppHost.CreateMainWindow(folderA, hooks);

                // 1. Initial folder A presentation
                Assert.True(
                    await StaTestHost.WaitForAsync(() => presented.Count > 0, PresentTimeout),
                    $"Folder A never presented its first image. StatusText={window.StatusText.Text}");
                Assert.Equal(fileA1, presented[0], ignoreCase: true);

                InstallTestAction(window, destination);

                // 2. Trigger Move for a1.png
                Assert.True(PressKey(window, ActionKey), "The key press did not reach the action branch of Window_KeyDown.");

                // The move is now parked in MoveOverride waiting on moveGate:
                Assert.Equal(1, Volatile.Read(ref moveInvocations));
                Assert.Equal(0, Volatile.Read(ref moveCompleted));
                Assert.Equal(1, FileActionInProgress(window));

                // 3. Switch to folder B while move is still in flight
                await LoadFolderAsync(window, folderB);

                // Wait for folder B to finish loading and present its first image (b1.png)
                Assert.True(
                    await StaTestHost.WaitForAsync(() => presented.Any(p => string.Equals(p, fileB1, StringComparison.OrdinalIgnoreCase)), PresentTimeout),
                    $"Folder B was not presented. StatusText={window.StatusText.Text}");

                var catalogB = window.Files;
                Assert.Contains(fileB1, catalogB, StringComparer.OrdinalIgnoreCase);
                Assert.Contains(fileB2, catalogB, StringComparer.OrdinalIgnoreCase);
                Assert.DoesNotContain(fileA1, catalogB, StringComparer.OrdinalIgnoreCase);
                Assert.DoesNotContain(fileA2, catalogB, StringComparer.OrdinalIgnoreCase);

                // 4. Now release the gate so the Move of a1.png completes
                moveGate.SetResult();

                Assert.True(
                    await StaTestHost.WaitForAsync(() => Volatile.Read(ref moveCompleted) == 1 && FileActionInProgress(window) == 0, SettleTimeout),
                    "The parked move action never completed or released the in-flight guard.");

                await DrainAsync(DrainWindow);

                // 5. INV-5 Invariant Assertions:
                // (a) Catalog of Folder B is untouched:
                var finalCatalogB = window.Files;
                Assert.Equal(2, finalCatalogB.Count);
                Assert.Contains(fileB1, finalCatalogB, StringComparer.OrdinalIgnoreCase);
                Assert.Contains(fileB2, finalCatalogB, StringComparer.OrdinalIgnoreCase);
                Assert.DoesNotContain(fileA1, finalCatalogB, StringComparer.OrdinalIgnoreCase);
                Assert.DoesNotContain(fileA2, finalCatalogB, StringComparer.OrdinalIgnoreCase);

                // (b) APP-03 (Q-R25 option B): the late move is real on disk, so it IS registered for Undo
                // (only the new folder's catalog/session are left alone):
                var movedTarget = Path.Combine(destination, "a1.png");
                var moveHistory = GetMoveHistory(window);
                var entry = Assert.Single(moveHistory);
                Assert.Equal(fileA1, entry.Source, ignoreCase: true);
                Assert.Equal(movedTarget, entry.Destination, ignoreCase: true);

                // (c) The filesystem move of a1.png itself succeeded on disk:
                Assert.True(File.Exists(movedTarget), "The file was not moved to destination.");
                Assert.False(File.Exists(fileA1), "The source file still exists in folder A.");

                // (d) Folder B files on disk were never touched:
                Assert.True(File.Exists(fileB1), "b1.png was unexpectedly affected.");
                Assert.True(File.Exists(fileB2), "b2.png was unexpectedly affected.");

                // (e) Undo restores a1.png to folder A (not into folder B's catalog) and reopens folder A at it (R7-2):
                await TriggerUndoAsync(window);
                Assert.True(
                    await StaTestHost.WaitForAsync(() => window.Files.Any(p => string.Equals(p, fileA1, StringComparison.OrdinalIgnoreCase)), PresentTimeout),
                    $"Undo did not reopen folder A at the restored file. StatusText={window.StatusText.Text}");
                await DrainAsync(DrainWindow);

                Assert.True(File.Exists(fileA1), "Undo did not restore a1.png to folder A.");
                Assert.False(File.Exists(movedTarget), "Undo left a1.png in the destination.");
                Assert.Empty(GetMoveHistory(window));
                Assert.DoesNotContain(fileB1, window.Files, StringComparer.OrdinalIgnoreCase);
                Assert.True(File.Exists(fileB1) && File.Exists(fileB2), "Undo touched folder B's files.");
            });
        }
        finally
        {
            moveGate.TrySetResult();
            await CloseAsync(window);
        }
    }

    [Fact(DisplayName = "INV-5: a Move failing after a folder switch does not restore its source into the new folder")]
    public async Task MoveFailingAfterFolderSwitchDoesNotRestoreSourceIntoNewCatalog()
    {
        using var root = new TempRoot("t14c-inv5-fail");
        using var dataRoot = new DataRootFixture();
        var folderA = root.Dir("imagesA");
        var folderB = root.Dir("imagesB");
        var destination = root.Dir("sorted");

        var fileA1 = Path.Combine(folderA, "a1.png");
        var fileB1 = Path.Combine(folderB, "b1.png");
        var fileB2 = Path.Combine(folderB, "b2.png");

        var presented = new List<string>();
        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moveInvocations = 0;
        MainWindow? window = null;

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(folderA, "a1.png", "a2.png");
                WriteTestImages(folderB, "b1.png", "b2.png");

                var hooks = new TestHostHooks
                {
                    OnPresented = presented.Add,
                    MoveOverride = async (_, _) =>
                    {
                        Interlocked.Increment(ref moveInvocations);
                        await moveGate.Task;
                        throw new IOException("T14c: simulated move failure after the folder switch");
                    },
                };
                window = TestAppHost.CreateMainWindow(folderA, hooks);

                Assert.True(
                    await StaTestHost.WaitForAsync(() => presented.Count > 0, PresentTimeout),
                    $"Folder A never presented its first image. StatusText={window.StatusText.Text}");
                Assert.Equal(fileA1, presented[0], ignoreCase: true);

                InstallTestAction(window, destination);
                Assert.True(PressKey(window, ActionKey), "The key press did not reach the action branch of Window_KeyDown.");
                Assert.Equal(1, Volatile.Read(ref moveInvocations));

                await LoadFolderAsync(window, folderB);
                Assert.True(
                    await StaTestHost.WaitForAsync(() => presented.Any(p => string.Equals(p, fileB1, StringComparison.OrdinalIgnoreCase)), PresentTimeout),
                    $"Folder B was not presented. StatusText={window.StatusText.Text}");

                moveGate.SetResult();
                Assert.True(
                    await StaTestHost.WaitForAsync(() => FileActionInProgress(window) == 0, SettleTimeout),
                    "The failed move never released the in-flight guard.");
                await DrainAsync(DrainWindow);

                // Without the stale-folder guard the failure branch would Restore(a1) into folder B's catalog.
                Assert.Equal(
                    new[] { fileB1, fileB2 },
                    window.Files.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
                Assert.Empty(GetMoveHistory(window));
                Assert.True(File.Exists(fileA1), "a1.png left folder A although the move failed.");
                Assert.False(File.Exists(Path.Combine(destination, "a1.png")), "A failed move reached the destination.");
            });
        }
        finally
        {
            moveGate.TrySetResult();
            await CloseAsync(window);
        }
    }

    private static void InstallTestAction(MainWindow window, string destinationFolder)
    {
        // Through SettingsStore.Save, as the Settings window does: MainWindow rebuilds its ShortcutRouter from the
        // store's Changed event, so mutating the live _settings object would leave the action key unmapped.
        var store = Field<SettingsStore>(window, "_settingsStore");
        var settings = store.Current;
        settings.Shortcuts = ShortcutMappings.Default();
        settings.Actions =
        [
            new ReviewAction
            {
                Name = "T14c",
                Shortcut = ActionKey.ToString(),
                Operation = FileOperationType.Move,
                Destination = destinationFolder,
                Confirm = false,
            },
        ];
        store.Save(settings);
    }

    private static bool PressKey(MainWindow window, Key key)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var source = PresentationSource.FromVisual(window)
            ?? HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("The hosted MainWindow has no PresentationSource to raise key input from.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        window.RaiseEvent(args);
        return args.Handled;
    }

    private static async Task LoadFolderAsync(MainWindow window, string folder)
    {
        var method = typeof(MainWindow).GetMethod("LoadFolderAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, [typeof(string), typeof(string)])
            ?? throw new InvalidOperationException("MainWindow.LoadFolderAsync no longer exists.");
        await (Task)method.Invoke(window, [folder, null])!;
    }

    private static async Task TriggerUndoAsync(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod("UndoLastActionAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("MainWindow.UndoLastActionAsync no longer exists.");
        await (Task)method.Invoke(window, null)!;
    }

    private static int FileActionInProgress(MainWindow window) => window.IsFileActionInProgress ? 1 : 0;

    private static Stack<(string Source, string Destination)> GetMoveHistory(MainWindow window)
        => window.ViewModel.UndoService.MoveHistory;

    private static object? GetLastUndoAction(MainWindow window)
        => window.ViewModel.UndoService.LastUndoAction;

    private static T Field<T>(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"MainWindow.{name} no longer exists.");
        return (T)field.GetValue(window)!;
    }

    private static async Task DrainAsync(TimeSpan duration)
    {
        // TC09: Replace Task.Delay with multiple dispatcher pumps.
        // Each pump ensures queued work from the previous invocation is processed.
        // Pump multiple times to ensure deferred work executes.
        var estimatedPumps = Math.Max(40, (int)(duration.TotalMilliseconds / 10));
        for (int i = 0; i < estimatedPumps; i++)
        {
            await StaTestHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
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

    private static void WriteTestImages(string folder, params string[] names)
    {
        const int Size = 16;
        const int Stride = Size * 4;
        var pixels = new byte[Stride * Size];
        Array.Fill(pixels, (byte)0x90);
        foreach (var name in names)
        {
            var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Stride);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, name));
            encoder.Save(stream);
        }
    }
}
