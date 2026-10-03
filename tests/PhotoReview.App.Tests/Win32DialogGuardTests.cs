using System.Diagnostics;
using System.Windows;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.App.Tests;

/// <summary>
/// Smoke tests for <see cref="Win32DialogGuard"/> on a bare, ad-hoc STA thread (no WPF
/// <see cref="Application"/> required) -- the shape every guarded thread in this project relies on.
/// The MessageBox here is shown ONLY on a thread that already has the guard installed, so it is
/// moved off-screen before painting and auto-dismissed; it never reaches the developer's desktop.
/// </summary>
[Trait("Category", "UI")]
public sealed class Win32DialogGuardTests
{
    [Fact]
    public void InstallOnCurrentThread_RealMessageBoxShown_IsRecordedAndAutoDismissedPromptly()
    {
        var sw = Stopwatch.StartNew();
        bool hasDialogsAfter = false;
        System.Collections.Generic.IReadOnlyList<string> dialogsAfter = Array.Empty<string>();
        InvalidOperationException? thrown = null;

        var thread = new Thread(() =>
        {
            using var guard = Win32DialogGuard.InstallOnCurrentThread();
            MessageBox.Show("app-guard-probe", "probe", MessageBoxButton.YesNo);
            hasDialogsAfter = guard.HasDialogs;
            dialogsAfter = guard.Dialogs;
            thrown = Assert.Throws<InvalidOperationException>(guard.ThrowIfAny);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Guarded MessageBox thread did not finish promptly.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Took {sw.Elapsed.TotalSeconds:F1}s.");

        Assert.True(hasDialogsAfter, "HasDialogs should be true after a real dialog was shown.");
        Assert.Contains(dialogsAfter, d => d.Contains("app-guard-probe", StringComparison.Ordinal));
        Assert.NotNull(thrown);
        Assert.Contains("app-guard-probe", thrown!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HasDialogs_NoDialogShown_IsFalseAndThrowIfAnyIsNoOp()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var guard = Win32DialogGuard.InstallOnCurrentThread();
                Assert.False(guard.HasDialogs);
                guard.ThrowIfAny();
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Guard thread did not finish promptly.");
        Assert.Null(threadException);
    }

    [Fact]
    public void Clear_AfterDialogRecorded_ForgetsIt()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var guard = Win32DialogGuard.InstallOnCurrentThread();
                MessageBox.Show("app-guard-clear-probe", "probe", MessageBoxButton.YesNo);
                Assert.True(guard.HasDialogs);

                guard.Clear();

                Assert.False(guard.HasDialogs);
                guard.ThrowIfAny();
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Guard thread did not finish promptly.");
        Assert.Null(threadException);
    }
}
