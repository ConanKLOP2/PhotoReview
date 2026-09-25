using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PhotoReview.App;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>Review round 7 (2026-09-25), window-level fixes: R7-6, R7-7, R7-8, R7-9, R7-11.</summary>
[Collection("GlobalState")]
public sealed class ReviewRound7WindowTests
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Off-screen, not activated, not in the taskbar: nothing appears on the user's desktop.</summary>
    private static MainWindow ShowOffScreenMainWindow()
    {
        var window = TestAppHost.CreateMainWindow(null);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        return window;
    }

    [Fact(DisplayName = "R7-11: the test host never restores or saves the user's window-placement.json")]
    public async Task TestAppHost_SuppressesWindowPlacement()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = TestAppHost.CreateMainWindow(null);
            try { Assert.Null(window.PlacementFile); }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "R7-6: Esc with the tools popup open closes the popup, not the app")]
    public async Task Escape_WithToolsPopupOpen_ClosesPopupOnly()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = ShowOffScreenMainWindow();
            var closed = false;
            window.Closed += (_, _) => closed = true;
            using var source = new HwndSource(new HwndSourceParameters("r7-esc"));
            try
            {
                window.ToolsButton.IsChecked = true;
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };

                typeof(MainWindow).GetMethod("Window_KeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window, args]);

                Assert.True(args.Handled);
                Assert.False(window.ToolsButton.IsChecked);
                Assert.False(closed);
            }
            finally
            {
                if (!closed) window.Close();
            }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "R7-7: closing while a file action holds the gate waits for it, then closes")]
    public async Task Close_WhileFileActionRuns_ClosesAfterGateReleases()
    {
        await StaTestHost.RunAsync(async () =>
        {
            var window = ShowOffScreenMainWindow();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            var gate = (FileActionGate)typeof(MainViewModel)
                .GetField("_fileActionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.ViewModel)!;
            Assert.True(gate.TryEnter()); // a Move is running

            window.Close();
            Assert.False(closed.Task.IsCompleted);
            Assert.True(window.IsVisible);

            gate.Exit();
            var finished = await Task.WhenAny(closed.Task, Task.Delay(CloseTimeout));
            if (!ReferenceEquals(finished, closed.Task)) window.Close();
            Assert.Same(closed.Task, finished);
        });
    }

    [Fact(DisplayName = "R7-8: Settings Save refuses an escaping destination typed in the raw actions JSON")]
    public async Task SettingsSave_WithEscapingDestinationInJson_WarnsAndDoesNotSave()
    {
        using var root = new TempRoot("r7-settings-dest");
        var appPaths = new AppPaths(root.Path);
        var store = new SettingsStore(appPaths, new PhysicalFileSystem(), NullLog.Instance);
        store.Load();
        var configBefore = File.Exists(appPaths.ConfigFile) ? File.ReadAllText(appPaths.ConfigFile) : null;
        var bad = new ReviewAction { Name = "Loai-9", Shortcut = "F9", Operation = FileOperationType.Move, Destination = @"..\x" };

        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(store);
            var warnings = new List<string>();
            window.InvalidSettingsWarning = warnings.Add;
            try
            {
                window.ActionsText.Text = JsonSerializer.Serialize(new[] { bad });
                typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window, new RoutedEventArgs()]);

                Assert.Equal([ActionProfilesWindow.FindDestinationProblem([bad])!], warnings);
                Assert.Null(window.DialogResult);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });

        Assert.DoesNotContain(store.Current.Actions, action => action.Destination == @"..\x");
        Assert.Equal(configBefore, File.Exists(appPaths.ConfigFile) ? File.ReadAllText(appPaths.ConfigFile) : null);
    }

    [Fact(DisplayName = "R7-9: exporting action profiles to an unwritable path returns a message instead of failing silently")]
    public void ExportActions_UnwritablePath_ReturnsMessage_AndWritableSucceeds()
    {
        using var root = new TempRoot("r7-export");
        var actions = ReviewAction.Defaults();

        // The target is an existing directory: File.WriteAllText throws UnauthorizedAccessException.
        var error = ActionProfilesWindow.TryExport(root.Path, actions);
        Assert.False(string.IsNullOrWhiteSpace(error));

        var file = Path.Combine(root.Path, "actions.json");
        Assert.Null(ActionProfilesWindow.TryExport(file, actions));
        Assert.True(File.Exists(file));
    }
}
