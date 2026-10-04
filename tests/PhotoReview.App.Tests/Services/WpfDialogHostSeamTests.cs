using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Services;
using PhotoReview.Core;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// Stryker gap pins (round 3) for <see cref="WpfDialogService"/> and <see cref="WpfFolderPicker"/> behind the
/// <see cref="IDialogHost"/> seam: which message box, buttons, icon, owner and start folder each call asks for, and how the
/// modal result is interpreted. Windows are constructed for real on an STA thread but never shown (the fake host records them).
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class WpfDialogHostSeamTests
{
    private sealed class FakeHost : IDialogHost
    {
        public Window? Owner { get; set; }
        public MessageBoxResult BoxResult { get; set; } = MessageBoxResult.None;
        public string? FolderResult { get; set; }
        public bool? DialogResult { get; set; }
        public List<(Window? Owner, string Message, string Title, MessageBoxButton Button, MessageBoxImage Image)> Boxes { get; } = [];
        public List<(Window? Owner, string Title, string? Initial)> Pickers { get; } = [];
        public List<Window> Dialogs { get; } = [];
        public List<Window> Shown { get; } = [];

        public MessageBoxResult ShowMessageBox(Window? owner, string message, string title, MessageBoxButton button, MessageBoxImage image)
        {
            Boxes.Add((owner, message, title, button, image));
            return BoxResult;
        }

        public string? PickFolder(Window? owner, string title, string? initialFolder)
        {
            Pickers.Add((owner, title, initialFolder));
            return FolderResult;
        }

        public bool? ShowDialog(Window window)
        {
            Dialogs.Add(window);
            return DialogResult;
        }

        public void Show(Window window) => Shown.Add(window);
    }

    private static void EnsureResourceAssembly()
    {
        try
        {
            Application.ResourceAssembly = typeof(MainWindow).Assembly;
        }
        catch
        {
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                ?.SetValue(null, typeof(MainWindow).Assembly);
        }
    }

    /// <summary>A window that has been shown (WPF refuses an owner that never was); on the hidden desktop.</summary>
    private static Window NewOwner()
    {
        var window = new Window { Width = 10, Height = 10, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        window.Show();
        return window;
    }

    private static WpfDialogService Create(FakeHost host, IServiceProvider? services = null)
        => new(services ?? new ServiceCollection().BuildServiceProvider(), host);

    [Theory]
    [InlineData(MessageBoxResult.Yes, true)]
    [InlineData(MessageBoxResult.No, false)]
    [InlineData(MessageBoxResult.None, false)]
    public void ShowConfirmation_AsksYesNoWithAQuestionIcon_AndOnlyYesConfirms(MessageBoxResult answer, bool expected)
    {
        StaUi.Run(() =>
        {
            var owner = NewOwner();
            var host = new FakeHost { Owner = owner, BoxResult = answer };

            Assert.Equal(expected, Create(host).ShowConfirmation("T", "M"));

            var box = Assert.Single(host.Boxes);
            Assert.Same(owner, box.Owner);
            Assert.Equal(("M", "T", MessageBoxButton.YesNo, MessageBoxImage.Question), (box.Message, box.Title, box.Button, box.Image));
        });
    }

    [Fact]
    public void ShowMessage_IsAnInformationBoxWithOk()
    {
        var host = new FakeHost();

        Create(host).ShowMessage("T", "M");

        var box = Assert.Single(host.Boxes);
        Assert.Null(box.Owner);
        Assert.Equal(("M", "T", MessageBoxButton.OK, MessageBoxImage.Information), (box.Message, box.Title, box.Button, box.Image));
    }

    [Fact]
    public void ShowError_IsAWarningBoxWithOk_OwnedByTheHostOwner()
    {
        StaUi.Run(() =>
        {
            var owner = NewOwner();
            var host = new FakeHost { Owner = owner };

            Create(host).ShowError("T", "M");

            var box = Assert.Single(host.Boxes);
            Assert.Same(owner, box.Owner);
            Assert.Equal(("M", "T", MessageBoxButton.OK, MessageBoxImage.Warning), (box.Message, box.Title, box.Button, box.Image));
        });
    }

    [Fact]
    public void PickFolder_StartsInAnExistingFolder_AndReturnsTheChosenOne()
    {
        var existing = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var host = new FakeHost { FolderResult = @"C:\picked" };

        var chosen = Create(host).PickFolder(existing);

        Assert.Equal(@"C:\picked", chosen);
        var call = Assert.Single(host.Pickers);
        Assert.Equal(existing, call.Initial);
        Assert.Equal(Tr.DialogPickFolderTitle, call.Title);
        Assert.Null(call.Owner);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"Z:\definitely\missing\folder")]
    public void PickFolder_WithABlankOrMissingStartFolder_LetsTheDialogChoose(string? initial)
    {
        var host = new FakeHost { FolderResult = null };

        Assert.Null(Create(host).PickFolder(initial));

        Assert.Null(Assert.Single(host.Pickers).Initial);
    }

    [Fact]
    public void FolderPicker_PassesTheTitleOwnerAndAnExistingStartFolder()
    {
        StaUi.Run(() =>
        {
            var owner = NewOwner();
            var existing = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            var host = new FakeHost { Owner = owner, FolderResult = @"C:\x" };

            var chosen = new WpfFolderPicker(host).PickFolder("Pick one", existing);

            Assert.Equal(@"C:\x", chosen);
            var call = Assert.Single(host.Pickers);
            Assert.Equal(("Pick one", existing), (call.Title, call.Initial));
            Assert.Same(owner, call.Owner);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"Z:\definitely\missing\folder")]
    public void FolderPicker_WithABlankOrMissingStartFolder_PassesNone(string? initial)
    {
        var host = new FakeHost();

        Assert.Null(new WpfFolderPicker(host).PickFolder("t", initial));

        Assert.Null(Assert.Single(host.Pickers).Initial);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void ShowBatchReview_ReturnsTrueOnlyWhenTheDialogWasAccepted(bool? dialogResult, bool expected)
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var owner = NewOwner();
            var host = new FakeHost { Owner = owner, DialogResult = dialogResult };

            Assert.Equal(expected, Create(host).ShowBatchReview([@"C:\a.jpg"]));

            var window = Assert.Single(host.Dialogs);
            Assert.Same(owner, window.Owner);
        });
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void ShowSettings_ReturnsTrueOnlyWhenTheDialogWasAccepted(bool? dialogResult, bool expected)
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var dir = Path.Combine(Path.GetTempPath(), "PhotoReview_SettingsHost_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new SettingsStore(new AppPaths(dir), new PhysicalFileSystem(), new NullLog());
                var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
                var owner = NewOwner();
                var host = new FakeHost { Owner = owner, DialogResult = dialogResult };

                Assert.Equal(expected, Create(host, services).ShowSettings());

                Assert.Same(owner, Assert.Single(host.Dialogs).Owner);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        });
    }

    [Fact]
    public void ShowDiagnostics_ShowsTheRegisteredMetrics_OrAnEmptySnapshotWithoutThem()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var metrics = new ReviewMetrics();
            metrics.RecordCacheHit();
            metrics.RecordCacheHit();
            var withMetrics = new FakeHost();

            Create(withMetrics, new ServiceCollection().AddSingleton(metrics).BuildServiceProvider()).ShowDiagnostics();
            var without = new FakeHost();
            Create(without).ShowDiagnostics();

            var shown = (DiagnosticsWindow)Assert.Single(withMetrics.Dialogs);
            Assert.Equal(2.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), shown.CacheHitsText.Text);
            var empty = (DiagnosticsWindow)Assert.Single(without.Dialogs);
            Assert.Equal(0.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), empty.CacheHitsText.Text);
        });
    }

    [Fact]
    public void ShowSkippedFiles_ShowsAModalWindowOwnedByTheHostOwner()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var owner = NewOwner();
            var host = new FakeHost { Owner = owner };

            Create(host).ShowSkippedFiles([new SkippedEntry(@"C:\x.jpg", "locked")]);

            Assert.Same(owner, Assert.Single(host.Dialogs).Owner);
        });
    }

    [Fact]
    public void ShowBenchmark_ShowsAModelessWindowOwnedByTheHostOwner()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var owner = NewOwner();
            var host = new FakeHost { Owner = owner };

            Create(host).ShowBenchmark(null);

            var window = Assert.Single(host.Shown);
            Assert.Same(owner, window.Owner);
            Assert.Empty(host.Dialogs);
        });
    }

    [Fact]
    public void ShowRecovery_WithAJournal_ShowsTheRecoveryWindowModally()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var dir = Path.Combine(Path.GetTempPath(), "PhotoReview_RecoveryHost_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var journal = new OperationJournal(new AppPaths(dir), new PhysicalFileSystem(), new SystemClock());
                var services = new ServiceCollection().AddSingleton(journal).BuildServiceProvider();
                var owner = NewOwner();
                var host = new FakeHost { Owner = owner };

                Create(host, services).ShowRecovery();

                var window = Assert.Single(host.Dialogs);
                Assert.IsType<RecoveryWindow>(window);
                Assert.Same(owner, window.Owner);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        });
    }
}
