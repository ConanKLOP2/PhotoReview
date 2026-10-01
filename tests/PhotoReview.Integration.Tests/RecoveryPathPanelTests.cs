using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// RV-T65 (Copy half): "Copy path" must swallow a busy-clipboard <see cref="COMException"/>. Another thread holds the real
/// clipboard open (read-only: nothing is ever written to it) so WPF's retrying SetText really fails with CLIPBRD_E_CANT_OPEN.
/// "Show in Explorer" starts the user's real explorer.exe and has no seam, so it is not driven from a test.
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
