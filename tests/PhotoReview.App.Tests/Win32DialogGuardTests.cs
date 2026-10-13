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
        thread.IsBackground = true; // a real hang must fail the test, not keep the test host alive
        thread.Start();

        Wait.JoinOrFail(thread, "Guarded MessageBox thread");
        // No wall-clock ceiling here: the first window of a cold process takes longer than any short fixed budget on a busy runner
        // although the guard worked. What matters is that the guard closed the dialog (a hang fails the bounded join above).

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
        thread.IsBackground = true; // a real hang must fail the test, not keep the test host alive
        thread.Start();

        Wait.JoinOrFail(thread, "Guard thread");
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
        thread.IsBackground = true; // a real hang must fail the test, not keep the test host alive
        thread.Start();

        Wait.JoinOrFail(thread, "Guard thread");
        Assert.Null(threadException);
    }
}
