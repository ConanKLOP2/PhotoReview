using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// RV-T65 (Copy half): "Copy path" must swallow a busy-clipboard <see cref="COMException"/>. Another thread holds the real
/// clipboard open (read-only: nothing is ever written to it) so WPF's retrying SetText really fails with CLIPBRD_E_CANT_OPEN.
/// RV-T65 (Show half): "Show in Explorer" goes through <see cref="RecoveryPathPanel.StartExplorer"/>, replaced here by a recorder so a
/// test never starts the user's real explorer.exe.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class RecoveryPathPanelTests
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    private static readonly MethodInfo CopyClick =
        typeof(RecoveryPathPanel).GetMethod("Copy_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ShowClick =
        typeof(RecoveryPathPanel).GetMethod("Show_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Runs Show_Click on a panel showing <paramref name="path"/> with Explorer replaced by <paramref name="start"/>; returns the failure messages.</summary>
    private static async Task<List<string>> ClickShowAsync(string path, Action<ProcessStartInfo> start)
    {
        var failures = new List<string>();
        await StaTestHost.RunAsync(() =>
        {
            var panel = new RecoveryPathPanel { StartExplorer = start };
            panel.ExplorerFailed += failures.Add;
            panel.PathBox.Text = path;
            ShowClick.Invoke(panel, [panel, new RoutedEventArgs()]);
            return Task.CompletedTask;
        });
        return failures;
    }

    [Fact]
    public async Task Show_Click_ExistingFile_SelectsItInExplorer()
    {
        var dir = Directory.CreateTempSubdirectory("pr-recovery-panel-").FullName;
        try
        {
            var file = Path.Combine(dir, "a.jpg");
            File.WriteAllBytes(file, [1]);
            var started = new List<ProcessStartInfo>();

            var failures = await ClickShowAsync(file, started.Add);

            var info = Assert.Single(started);
            Assert.Equal("explorer.exe", info.FileName);
            Assert.Equal($"/select,\"{file}\"", info.Arguments);
            Assert.Empty(failures);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Show_Click_MissingFile_OpensTheNearestExistingFolder()
    {
        var dir = Directory.CreateTempSubdirectory("pr-recovery-panel-").FullName;
        try
        {
            var started = new List<ProcessStartInfo>();

            var failures = await ClickShowAsync(Path.Combine(dir, "gone", "deeper", "a.jpg"), started.Add);

            var info = Assert.Single(started);
            Assert.Equal("explorer.exe", info.FileName);
            Assert.Equal($"\"{dir}\"", info.Arguments);
            Assert.Empty(failures);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Show_Click_PathWithNoExistingAncestor_StartsNothing()
    {
        var started = new List<ProcessStartInfo>();

        var failures = await ClickShowAsync("no-such-relative-file.jpg", started.Add); // no directory part: nothing to open

        Assert.Empty(started);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Show_Click_ExplorerCannotStart_RaisesExplorerFailedWithTheOsMessage()
    {
        var dir = Directory.CreateTempSubdirectory("pr-recovery-panel-").FullName;
        try
        {
            var file = Path.Combine(dir, "a.jpg");
            File.WriteAllBytes(file, [1]);

            var failures = await ClickShowAsync(file, _ => throw new Win32Exception("explorer exploded"));

            Assert.Equal(["explorer exploded"], failures);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Show_Click_FolderFallbackExplorerThrowsInvalidOperation_RaisesExplorerFailed()
    {
        var dir = Directory.CreateTempSubdirectory("pr-recovery-panel-").FullName;
        try
        {
            var failures = await ClickShowAsync(Path.Combine(dir, "gone.jpg"), _ => throw new InvalidOperationException("no shell"));

            Assert.Equal(["no shell"], failures);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Copy_Click_ClipboardHeldByAnotherThread_DoesNotThrow()
    {
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var opened = false;
        var holder = new Thread(() =>
        {
            opened = OpenClipboard(IntPtr.Zero);
            held.Set();
            release.Wait();
            if (opened) CloseClipboard();
        }) { IsBackground = true, Name = "clipboard holder" };
        holder.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(opened, "could not take the clipboard to simulate a busy one");
            await StaTestHost.RunAsync(() =>
            {
                var panel = new RecoveryPathPanel();
                panel.PathBox.Text = @"C:\photos\a.jpg";

                var ex = Record.Exception(() => CopyClick.Invoke(panel, [panel, new RoutedEventArgs()]));

                Assert.Null(ex);
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(60));
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(10));
        }
    }
}
