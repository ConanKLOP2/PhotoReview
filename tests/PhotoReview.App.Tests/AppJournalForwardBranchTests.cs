using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Composition;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Instance;
using PhotoReview.Core.Model;
using PhotoReview.TestSupport;

namespace PhotoReview.App.Tests;

/// <summary>
/// Branch coverage of <c>App.RecoverJournalAsync</c> (failed-recovery prompt, declined prompt, nothing failed, fault
/// swallowed + logged) and the <c>InvalidOperationException</c> catch of <c>App.OpenForwarded</c>. Both are private and
/// need only <c>_services</c> / <c>Application._mainWindow</c>, so they run on an uninitialised <see cref="App"/> (the
/// WPF <c>Application</c> singleton is owned by whichever test created it first) against a real <see cref="MainWindow"/>
/// built from the production composition root with a temp data root and a recording dialog service.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class AppJournalForwardBranchTests
{
    private static readonly MethodInfo RecoverJournal =
        typeof(App).GetMethod("RecoverJournalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo OpenForwarded =
        typeof(App).GetMethod("OpenForwarded", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ServicesField =
        typeof(App).GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MainWindowField =
        typeof(Application).GetField("_mainWindow", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            try { _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; }
            catch (InvalidOperationException) when (Application.Current is not null) { }
        }
        try { Application.ResourceAssembly = typeof(MainWindow).Assembly; }
        catch
        {
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                ?.SetValue(null, typeof(MainWindow).Assembly);
        }
    }

    private static App NewApp(IServiceProvider? services, Window? mainWindow = null)
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        ServicesField.SetValue(app, services);
        MainWindowField.SetValue(app, mainWindow);
        return app;
    }

    private sealed class RecordingDialog(bool confirm) : IDialogService
    {
        public List<(string Title, string Message)> Confirmations { get; } = [];
        public int RecoveryShown { get; private set; }

        public bool ShowConfirmation(string title, string message)
        {
            Confirmations.Add((title, message));
            return confirm;
        }

        public void ShowRecovery() => RecoveryShown++;
        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) { }
        public string? PickFolder(string? initialFolder = null) => null;
        public bool ShowBatchReview(IReadOnlyList<string> paths) => false;
        public void ShowDiagnostics() { }
        public bool ShowSettings() => false;
        public void ShowBenchmark(string? folder = null) { }
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }
    }

    private sealed class RecordingOwnership : IFolderOwnership
    {
        public List<string> Folders { get; } = [];

        public Task<FolderOpenDecision> BeforeOpenAsync(string folder, string? initialPath, CancellationToken cancellationToken = default)
        {
            Folders.Add(folder);
            return Task.FromResult(FolderOpenDecision.ForwardedToOtherInstance);
        }

        public void OnFolderShown(string folder) { }
        public void AfterOpen(string folder, string? shownFolder) { }
    }

    /// <summary>Runs <paramref name="body"/> on an STA thread with a real MainWindow built from the production graph.</summary>
    private static void WithWindow(RecordingDialog dialog, Action<ServiceProvider, MainWindow> body)
    {
        using var dataRoot = new DataRootFixture();
        StaUi.Run(() =>
        {
            EnsureApplication();
            using var provider = AppHost.BuildServices(services =>
            {
                services.AddSingleton<IDialogService>(dialog);
            });
            var window = provider.GetRequiredService<MainWindow>();
            window.SuppressWindowPlacement();
            body(provider, window);
        });
    }

    private static void RunRecovery(ServiceProvider provider, MainWindow window)
    {
        // Off the STA thread so the method's await has no synchronisation context to resume on.
        var task = Task.Run(() => (Task)RecoverJournal.Invoke(NewApp(provider), [window])!);
        Assert.True(task.Wait(TimeSpan.FromSeconds(30)), "RecoverJournalAsync did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static void AppendInterruptedRecycle(ServiceProvider provider, string folder)
    {
        var file = Path.Combine(folder, "interrupted.jpg");
        File.WriteAllText(file, "x");
        provider.GetRequiredService<OperationJournal>().Append(new JournalEntry(
            "r1", FileOperationType.Recycle, JournalState.Prepared, file, null, 1,
            File.GetLastWriteTimeUtc(file), DateTime.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public void RecoverJournalAsync_FailedOperations_PromptWithCountAndOpenRecoveryWhenConfirmed()
    {
        var dialog = new RecordingDialog(confirm: true);
        using var temp = new TempRoot("journal-recover-confirm");
        WithWindow(dialog, (provider, window) =>
        {
            AppendInterruptedRecycle(provider, temp.Path);

            RunRecovery(provider, window);

            var (title, message) = Assert.Single(dialog.Confirmations);
            Assert.Equal(PhotoReview.Core.Localization.Tr.DialogStartupRecoveryFailedTitle, title);
            Assert.Equal(PhotoReview.Core.Localization.Tr.DialogStartupRecoveryFailedMessage(1), message);
            Assert.Equal(1, dialog.RecoveryShown);
        });
    }

    [Fact]
    public void RecoverJournalAsync_FailedOperations_DeclinedPromptDoesNotOpenRecovery()
    {
        var dialog = new RecordingDialog(confirm: false);
        using var temp = new TempRoot("journal-recover-decline");
        WithWindow(dialog, (provider, window) =>
        {
            AppendInterruptedRecycle(provider, temp.Path);

            RunRecovery(provider, window);

            Assert.Single(dialog.Confirmations);
            Assert.Equal(0, dialog.RecoveryShown);
        });
    }

    [Fact]
    public void RecoverJournalAsync_NothingFailed_ShowsNoPrompt()
    {
        var dialog = new RecordingDialog(confirm: true);
        WithWindow(dialog, (provider, window) =>
        {
            RunRecovery(provider, window);

            Assert.Empty(dialog.Confirmations);
            Assert.Equal(0, dialog.RecoveryShown);
        });
    }

    [Fact]
    public void RecoverJournalAsync_ServiceResolutionFaults_IsLoggedNotThrown()
    {
        var dialog = new RecordingDialog(confirm: true);
        using var log = new CapturedAppLog();
        WithWindow(dialog, (_, window) =>
        {
            using var empty = new ServiceCollection().BuildServiceProvider(); // no OperationJournal registered: resolution throws
            var task = Task.Run(() => (Task)RecoverJournal.Invoke(NewApp(empty), [window])!);
            Assert.True(task.Wait(TimeSpan.FromSeconds(30)), "RecoverJournalAsync did not finish.");
            task.GetAwaiter().GetResult(); // must complete normally

            Assert.Contains("Startup journal recovery failed", log.Text(), StringComparison.Ordinal);
            Assert.Contains("OperationJournal", log.Text(), StringComparison.Ordinal);
            Assert.Empty(dialog.Confirmations);
        });
    }

    [Fact]
    public void OpenForwarded_WindowOwnedByAnotherThread_LogsWarningAndDoesNotOpenThePath()
    {
        var dialog = new RecordingDialog(confirm: true);
        var ownership = new RecordingOwnership();
        using var log = new CapturedAppLog();
        using var temp = new TempRoot("forwarded-wrong-thread");
        WithWindow(dialog, (provider, window) =>
        {
            window.ViewModel.FolderOwnership = ownership;
            var app = NewApp(provider, window);

            // The pooled thread is not the window's dispatcher thread: touching WindowState/Activate throws
            // InvalidOperationException, which OpenForwarded must absorb (the "window went away" race).
            var task = Task.Run(() => OpenForwarded.Invoke(app, [temp.Path]));
            Assert.True(task.Wait(TimeSpan.FromSeconds(30)));

            Assert.Contains("Forwarded launch: could not activate the main window", log.Text(), StringComparison.Ordinal);
            Assert.Empty(ownership.Folders);
        });
    }
}
