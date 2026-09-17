using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoReview.App;
using PhotoReview.Tests.Unit.Infrastructure;
using Xunit;

namespace PhotoReview.Tests.Unit;

/// <summary>
/// T14b: locks INV-3 and INV-4 on the real <see cref="MainWindow"/>, driven through the T14a
/// seam (<c>MainWindowTestHooks</c>) on <see cref="StaTestHost"/>.
/// <para>
/// The action is triggered the way the app triggers it: a <c>PreviewKeyDown</c> raised through
/// WPF's own routed-event system, which runs the real <c>Window_KeyDown</c> handler and from
/// there the real <c>ExecuteActionAsync</c>. No OS-level input is simulated (repo rule 4 in
/// <c>PERF-DIAGNOSIS-TASKS.md</c>: never <c>SendInput</c>/<c>SendKeys</c>/<c>SetForegroundWindow</c>);
/// <c>RaiseEvent</c> never leaves the process and cannot land in another window.
/// </para>
/// <para>
/// The window is class <c>GlobalState</c> because it mutates PHOTOREVIEW_DATA_ROOT; the
/// redirection itself is owned by <see cref="DataRootFixture"/> (never set by hand here), per
/// the T14a follow-up note.
/// </para>
/// </summary>
[Collection("GlobalState")]
public sealed class MainWindowBehaviorActionTests
{
    /// <summary>Not bound by <see cref="ShortcutMappings.Default"/>, so the action branch is reached.</summary>
    private const Key ActionKey = Key.F3;

    private static readonly TimeSpan PresentTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A stray <c>ShowImageAsync</c> would need dispatcher time to present, so every "nothing
    /// else happened" assertion is made only after pumping for this long.
    /// </summary>
    private static readonly TimeSpan DrainWindow = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task ActionPresentsTheNextImageBeforeTheFileMoveCompletes()
    {
        using var root = new TempRoot("t14b-inv3");
        using var dataRoot = new DataRootFixture();
        var folder = root.Dir("images");
        var destination = root.Dir("sorted");
        var first = Path.Combine(folder, "a.png");
        var second = Path.Combine(folder, "b.png");
        var third = Path.Combine(folder, "c.png");

        var presented = new List<string>();
        var movePendingAtPresent = new List<bool>();
        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moveInvocations = 0;
        var movePending = 0;
        var moveCompleted = 0;
        string[] catalogWhenMoveStarted = [];
        MainWindow? window = null;

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(folder, "a.png", "b.png", "c.png");
                var hooks = new MainWindowTestHooks
                {
                    OnPresented = path =>
                    {
                        presented.Add(path);
                        movePendingAtPresent.Add(Volatile.Read(ref moveCompleted) == 0);
                    },
                    MoveOverride = async (source, target) =>
                    {
                        Interlocked.Increment(ref moveInvocations);
                        Volatile.Write(ref movePending, 1);
                        catalogWhenMoveStarted = [.. Field<List<string>>(window!, "_files")];
                        await moveGate.Task;
                        // Mirrors production's Task.Run(() => File.Move(...)) so the success
                        // path (post-move size check, journal Committed, undo registration)
                        // really runs instead of the failure branch.
                        await Task.Run(() => File.Move(source, target));
                        Volatile.Write(ref movePending, 0);
                        Volatile.Write(ref moveCompleted, 1);
                    },
                };
                window = new MainWindow(folder, hooks);

                Assert.True(
                    await StaTestHost.WaitForAsync(() => presented.Count > 0, PresentTimeout),
                    $"The folder never presented its first image. StatusText={window.StatusText.Text}");
                Assert.Equal(first, presented[0], ignoreCase: true);

                InstallTestAction(window, destination);
                Assert.True(PressKey(window, ActionKey), "The key press did not reach the action branch of Window_KeyDown.");

                // Window_KeyDown is async void, and ExecuteActionAsync runs synchronously all the
                // way to `await MoveFileAsync`, so the filesystem step is already parked on the
                // gate by the time RaiseEvent returns.
                Assert.Equal(1, Volatile.Read(ref moveInvocations));
                Assert.Equal(0, Volatile.Read(ref moveCompleted));

                // INV-3, first half: the advance happened before the filesystem step, exactly once
                // -- the source was already out of the catalog when MoveOverride was entered.
                Assert.Equal(2, catalogWhenMoveStarted.Length);
                Assert.DoesNotContain(first, catalogWhenMoveStarted, StringComparer.OrdinalIgnoreCase);
                Assert.Contains(second, catalogWhenMoveStarted, StringComparer.OrdinalIgnoreCase);
                Assert.Contains(third, catalogWhenMoveStarted, StringComparer.OrdinalIgnoreCase);

                // INV-3, second half: the next image reaches the screen while the move is still
                // in flight. An implementation that presented only after the action finished
                // would never satisfy this -- the gate is still closed.
                Assert.True(
                    await StaTestHost.WaitForAsync(() => presented.Count > 1, SettleTimeout),
                    "The next image was not presented while the file move was still in flight (INV-3).");
                Assert.Equal(second, presented[1], ignoreCase: true);
                Assert.True(movePendingAtPresent[1], "The next image was presented after the move completed (INV-3).");
                Assert.Equal(0, Volatile.Read(ref moveCompleted));

                moveGate.SetResult();
                Assert.True(
                    await StaTestHost.WaitForAsync(() => FileActionInProgress(window) == 0, SettleTimeout),
                    "The file action never released its in-flight guard.");
                await DrainAsync(DrainWindow);

                // INV-3, third half: no ShowImage after the action finished.
                Assert.Equal(1, Volatile.Read(ref moveInvocations));
                Assert.Equal(1, Volatile.Read(ref moveCompleted));
                Assert.Equal([first, second], presented.ToArray(), StringComparer.OrdinalIgnoreCase);

                Assert.True(File.Exists(Path.Combine(destination, "a.png")), "The move did not reach the destination.");
                Assert.False(File.Exists(first), "The source file is still in the source folder.");
            });
        }
        finally
        {
            // Never leave a parked continuation behind: a failed assertion above skips SetResult,
            // and the STA thread is shared with every other test in this collection.
            moveGate.TrySetResult();
            await CloseAsync(window);
        }
    }

    [Fact]
    public async Task ASecondActionIsIgnoredWhileTheFirstIsStillRunning()
    {
        using var root = new TempRoot("t14b-inv4");
        using var dataRoot = new DataRootFixture();
        var folder = root.Dir("images");
        var destination = root.Dir("sorted");
        var first = Path.Combine(folder, "a.png");
        var second = Path.Combine(folder, "b.png");

        var presented = new List<string>();
        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moveInvocations = 0;
        MainWindow? window = null;

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(folder, "a.png", "b.png", "c.png");
                var hooks = new MainWindowTestHooks
                {
                    OnPresented = presented.Add,
                    MoveOverride = async (source, target) =>
                    {
                        Interlocked.Increment(ref moveInvocations);
                        await moveGate.Task;
                        await Task.Run(() => File.Move(source, target));
                    },
                };
                window = new MainWindow(folder, hooks);

                Assert.True(
                    await StaTestHost.WaitForAsync(() => presented.Count > 0, PresentTimeout),
                    $"The folder never presented its first image. StatusText={window.StatusText.Text}");
                Assert.Equal(first, presented[0], ignoreCase: true);

                InstallTestAction(window, destination);

                // Two presses back to back, the way a user double-taps the key. The first parks in
                // MoveOverride; the second must be dropped by the in-flight guard, not queued.
                Assert.True(PressKey(window, ActionKey), "The first key press did not reach the action branch of Window_KeyDown.");
                Assert.True(PressKey(window, ActionKey), "The second key press did not reach the action branch of Window_KeyDown.");

                Assert.Equal(1, Volatile.Read(ref moveInvocations));
                await DrainAsync(DrainWindow);
                Assert.Equal(1, Volatile.Read(ref moveInvocations));
                Assert.Equal(1, FileActionInProgress(window));

                moveGate.SetResult();
                Assert.True(
                    await StaTestHost.WaitForAsync(() => FileActionInProgress(window) == 0, SettleTimeout),
                    "The file action never released its in-flight guard.");
                await DrainAsync(DrainWindow);

                // The dropped press must not resurface once the first action finishes.
                Assert.Equal(1, Volatile.Read(ref moveInvocations));
                Assert.True(File.Exists(Path.Combine(destination, "a.png")), "The first move did not reach the destination.");
                Assert.False(File.Exists(Path.Combine(destination, "b.png")), "The blocked second action moved a file anyway (INV-4).");
                Assert.True(File.Exists(second), "The blocked second action removed b.png from the source folder (INV-4).");

                // Settled normally: the guard is released, so a later action runs as usual.
                Assert.True(PressKey(window, ActionKey), "The follow-up key press did not reach the action branch of Window_KeyDown.");
                Assert.True(
                    await StaTestHost.WaitForAsync(
                        () => Volatile.Read(ref moveInvocations) == 2 && FileActionInProgress(window) == 0,
                        SettleTimeout),
                    "The window did not accept a new action after the first one completed.");
                Assert.True(File.Exists(Path.Combine(destination, "b.png")), "The follow-up move did not reach the destination.");
            });
        }
        finally
        {
            moveGate.TrySetResult();
            await CloseAsync(window);
        }
    }

    /// <summary>
    /// Points the window at a single, deterministic Move action inside this test's temp tree.
    /// <para>
    /// <c>MainWindow</c> starts from <c>AppSettings.Load()</c>, i.e. the machine's real
    /// <c>config.json</c> -- on a developer machine that can map Enter to a Move into a real photo
    /// folder. Overwriting the in-memory shortcut map and action list (nothing is saved back to
    /// disk) keeps the key route independent of whoever runs the suite and keeps every filesystem
    /// effect inside the temp root.
    /// </para>
    /// </summary>
    private static void InstallTestAction(MainWindow window, string destinationFolder)
    {
        var settings = Field<AppSettings>(window, "_settings");
        settings.Shortcuts = ShortcutMappings.Default();
        settings.Actions =
        [
            new ReviewAction
            {
                Name = "T14b",
                Shortcut = ActionKey.ToString(),
                Operation = "Move",
                Destination = destinationFolder,
                Confirm = false,
            },
        ];
    }

    /// <summary>
    /// Raises the same routed event the OS would deliver (<c>MainWindow.xaml</c> binds
    /// <c>PreviewKeyDown="Window_KeyDown"</c>), in-process only.
    /// </summary>
    /// <returns>
    /// <c>e.Handled</c>. With <see cref="ShortcutMappings.Default"/> installed, only the action
    /// branch of <c>Window_KeyDown</c> handles <see cref="ActionKey"/>, so this being true is the
    /// proof that the press really reached <c>ExecuteActionAsync</c> -- without it a press that
    /// fell out of the handler early would make these tests pass for the wrong reason.
    /// </returns>
    private static bool PressKey(MainWindow window, Key key)
    {
        // KeyEventArgs refuses a null PresentationSource, and a window that was never shown has
        // none until its HWND exists. EnsureHandle creates the handle without showing the window.
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

    private static int FileActionInProgress(MainWindow window) => Field<int>(window, "_fileActionInProgress");

    private static T Field<T>(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"MainWindow.{name} no longer exists; the T14b INV-3/INV-4 tests observe it directly.");
        return (T)field.GetValue(window)!;
    }

    /// <summary>Pumps the dispatcher for a fixed window so late work has a chance to be observed.</summary>
    private static async Task DrainAsync(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            await StaTestHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(10);
        }
    }

    private static async Task CloseAsync(MainWindow? window)
    {
        if (window is null) return;
        await StaTestHost.RunAsync(() =>
        {
            // Releases the preload scheduler, thumbnail cache and Explorer pump this window owns.
            // A never-shown window can refuse Close(); leaking it for the rest of the run is
            // acceptable, failing the test over cleanup is not.
            try { window.Close(); } catch (InvalidOperationException) { }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Written on the STA host thread: <see cref="PngBitmapEncoder"/> is a
    /// <see cref="DispatcherObject"/> and takes the affinity of the thread that creates it.
    /// </summary>
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
