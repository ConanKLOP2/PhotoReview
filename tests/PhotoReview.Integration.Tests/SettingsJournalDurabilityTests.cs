using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Composition;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// TEST-06 (ADR 0007, IO03): the Settings "journal durability" radio, through Save, the SettingsStore and the
/// journal's settings delegate, down to the <c>durable</c> flag the journal passes to
/// <see cref="IFileSystem.OpenAppend(string, bool)"/> when a real Move is executed. Runs on the production
/// composition graph (<see cref="AppHost.BuildServices"/>) with a temp data root; only <see cref="IFileSystem"/> is
/// replaced by a recording pass-through.
/// </summary>
[Collection("GlobalState")]
public sealed class SettingsJournalDurabilityTests
{
    [Fact(DisplayName = "PowerLossSafe selected in Settings makes journal writes durable; selecting Fast turns it off again")]
    public async Task PowerLossSafe_SelectedInSettings_JournalWritesDurably()
    {
        using var root = new TempRoot("settings-journal");
        using var dataRoot = new DataRootFixture();
        var images = root.Dir("images");
        var dest = root.Dir("dest");
        File.WriteAllBytes(Path.Combine(images, "a.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(images, "b.jpg"), [4, 5, 6]);

        var recorder = AppendRecorder.Create(new PhysicalFileSystem(), out var appends);

        await StaTestHost.RunAsync(async () =>
        {
            using var sp = AppHost.BuildServices(services => services.AddSingleton(recorder));
            var store = sp.GetRequiredService<SettingsStore>();
            var service = sp.GetRequiredService<FileActionService>();
            var journalFile = sp.GetRequiredService<IAppPaths>().JournalFile;

            // Nothing selected yet: the default is Fast.
            Assert.Equal(JournalDurability.Fast, store.Current.JournalDurability);

            SaveFromSettingsWindow(store, safe: true);
            Assert.Equal(JournalDurability.PowerLossSafe, store.Current.JournalDurability);
            var safeFlags = await MoveAndGetJournalFlagsAsync(service, appends, journalFile, Path.Combine(images, "a.jpg"), dest);
            Assert.NotEmpty(safeFlags);
            Assert.All(safeFlags, durable => Assert.True(durable, "PowerLossSafe was selected in Settings but a journal write was not durable."));

            SaveFromSettingsWindow(store, safe: false);
            Assert.Equal(JournalDurability.Fast, store.Current.JournalDurability);
            var fastFlags = await MoveAndGetJournalFlagsAsync(service, appends, journalFile, Path.Combine(images, "b.jpg"), dest);
            Assert.NotEmpty(fastFlags);
            Assert.All(fastFlags, durable => Assert.False(durable, "Fast was selected in Settings but a journal write was durable."));
        });
    }

    private static async Task<List<bool>> MoveAndGetJournalFlagsAsync(
        FileActionService service, List<(string Path, bool Durable)> appends, string journalFile, string source, string dest)
    {
        lock (appends) appends.Clear();
        var result = await service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, dest));
        Assert.True(result.Succeeded, result.Error);
        lock (appends)
            return [.. appends.Where(a => string.Equals(a.Path, journalFile, StringComparison.OrdinalIgnoreCase)).Select(a => a.Durable)];
    }

    /// <summary>
    /// Drives the real Settings window: picks the radio and runs its Save handler once the window is loaded
    /// (Save sets DialogResult, so the window must be shown modally; it is parked off-screen).
    /// </summary>
    private static void SaveFromSettingsWindow(SettingsStore store, bool safe)
    {
        var window = new SettingsWindow(store)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
        };
        var save = typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
        window.Loaded += (_, _) =>
        {
            (safe ? window.JournalSafeRadio : window.JournalFastRadio).IsChecked = true;
            save.Invoke(window, [window, new RoutedEventArgs()]);
        };
        Assert.True(window.ShowDialog());
    }

    /// <summary>Pass-through <see cref="IFileSystem"/> that records every OpenAppend(path, durable) call.</summary>
    public class AppendRecorder : DispatchProxy
    {
        private IFileSystem _inner = null!;
        private List<(string Path, bool Durable)> _appends = null!;

        internal static IFileSystem Create(IFileSystem inner, out List<(string Path, bool Durable)> appends)
        {
            var proxy = Create<IFileSystem, AppendRecorder>();
            var recorder = (AppendRecorder)(object)proxy;
            recorder._inner = inner;
            recorder._appends = appends = [];
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IFileSystem.OpenAppend) && args is [string path, bool durable])
                lock (_appends) _appends.Add((path, durable));
            else if (targetMethod.Name == nameof(IFileSystem.OpenAppendDurable) && args is [string durablePath])
                lock (_appends) _appends.Add((durablePath, true));
            try { return targetMethod.Invoke(_inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
