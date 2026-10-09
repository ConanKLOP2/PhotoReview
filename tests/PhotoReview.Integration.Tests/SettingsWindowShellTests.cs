using System.IO;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>Settings > General > Explorer integration, against a fake registry (the real one is covered by the Native test).</summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class SettingsWindowShellTests
{
    private const string Exe = @"C:\Apps\PhotoReview\PhotoReview.App.exe";

    private sealed class FakeShell : IShellIntegration
    {
        public string? Folder { get; set; }
        public string? Background { get; set; }
        public List<string> Registered { get; } = [];
        public int Unregistered { get; private set; }
        public Exception? FailWith { get; set; }

        public ShellMenuState GetState(string executablePath) => ShellMenuCommand.Evaluate(Folder, Background, executablePath);

        public void Register(string executablePath)
        {
            if (FailWith is { } ex) throw ex;
            Registered.Add(executablePath);
            Folder = ShellMenuCommand.ForFolder(executablePath);
            Background = ShellMenuCommand.ForBackground(executablePath);
        }

        public void Unregister()
        {
            if (FailWith is { } ex) throw ex;
            Unregistered++;
            Folder = null;
            Background = null;
        }
    }

    private static async Task WithWindowAsync(FakeShell shell, string? exe, Action<SettingsWindow> body)
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings()) { ShellIntegration = shell, ExecutablePath = exe };
            try
            {
                window.RefreshShellStatus();
                body(window);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    private static void Click(System.Windows.Controls.Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    [Fact(DisplayName = "Not registered: the status says so, Add is enabled and Remove is not")]
    public async Task NotRegistered_OffersAddOnly()
    {
        await WithWindowAsync(new FakeShell(), Exe, window =>
        {
            Assert.Equal(Tr.SettingsShellStatusNotRegistered, window.ShellStatusText.Text);
            Assert.Equal(Tr.SettingsShellAdd, window.ShellAddButton.Content);
            Assert.True(window.ShellAddButton.IsEnabled);
            Assert.False(window.ShellRemoveButton.IsEnabled);
        });
    }

    [Fact(DisplayName = "Add registers this exe and the status switches to registered")]
    public async Task Add_RegistersThisExe()
    {
        var shell = new FakeShell();
        await WithWindowAsync(shell, Exe, window =>
        {
            Click(window.ShellAddButton);

            Assert.Equal([Exe], shell.Registered);
            Assert.Equal(Tr.SettingsShellStatusRegistered, window.ShellStatusText.Text);
            Assert.False(window.ShellAddButton.IsEnabled);
            Assert.True(window.ShellRemoveButton.IsEnabled);
        });
    }

    [Fact(DisplayName = "Registered for another exe path: the status warns and the button becomes Update path, which repairs it")]
    public async Task MovedExe_OffersUpdatePath()
    {
        var shell = new FakeShell { Folder = ShellMenuCommand.ForFolder(@"D:\Old\PhotoReview.App.exe"), Background = ShellMenuCommand.ForBackground(@"D:\Old\PhotoReview.App.exe") };
        await WithWindowAsync(shell, Exe, window =>
        {
            Assert.Equal(Tr.SettingsShellStatusElsewhere, window.ShellStatusText.Text);
            Assert.Equal(Tr.SettingsShellUpdate, window.ShellAddButton.Content);
            Assert.True(window.ShellAddButton.IsEnabled);

            Click(window.ShellAddButton);

            Assert.Equal([Exe], shell.Registered);
            Assert.Equal(Tr.SettingsShellStatusRegistered, window.ShellStatusText.Text);
        });
    }

    [Fact(DisplayName = "Remove unregisters and the status goes back to not registered")]
    public async Task Remove_Unregisters()
    {
        var shell = new FakeShell { Folder = ShellMenuCommand.ForFolder(Exe), Background = ShellMenuCommand.ForBackground(Exe) };
        await WithWindowAsync(shell, Exe, window =>
        {
            Assert.Equal(Tr.SettingsShellStatusRegistered, window.ShellStatusText.Text);

            Click(window.ShellRemoveButton);

            Assert.Equal(1, shell.Unregistered);
            Assert.Equal(Tr.SettingsShellStatusNotRegistered, window.ShellStatusText.Text);
            Assert.False(window.ShellRemoveButton.IsEnabled);
        });
    }

    [Fact(DisplayName = "A registry failure is shown in the status line instead of crashing the window")]
    public async Task RegistryFailure_IsShownInTheStatus()
    {
        var shell = new FakeShell { FailWith = new UnauthorizedAccessException("denied by policy") };
        await WithWindowAsync(shell, Exe, window =>
        {
            Click(window.ShellAddButton);

            Assert.Equal(Tr.SettingsShellStatusError("denied by policy"), window.ShellStatusText.Text);
            Assert.Empty(shell.Registered);
        });
    }

    [Theory(DisplayName = "No usable exe path (null, relative, or the dotnet host): the integration is unavailable and nothing is touched")]
    [InlineData(null)]
    [InlineData("PhotoReview.App.exe")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    public async Task NoUsableExe_IsUnavailable(string? exe)
    {
        var shell = new FakeShell();
        await WithWindowAsync(shell, exe, window =>
        {
            Assert.Equal(Tr.SettingsShellStatusUnavailable, window.ShellStatusText.Text);
            Assert.False(window.ShellAddButton.IsEnabled);
            Assert.False(window.ShellRemoveButton.IsEnabled);
            Assert.Empty(shell.Registered);
        });
    }
}
