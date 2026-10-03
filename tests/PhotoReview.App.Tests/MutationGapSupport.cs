using System.IO;
using System.Threading;
using PhotoReview.Core.Diagnostics;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.App.Tests;

/// <summary>
/// Routes <see cref="AppLog"/> to a private log file for the lifetime of the object so a test can assert what the app
/// logged (and, as importantly, what it did NOT log). Callers must be in the "GlobalState" collection: it swaps the
/// process-wide <see cref="AppLog.Instance"/>.
/// </summary>
internal sealed class CapturedAppLog : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PhotoReview_AppLogCapture_" + Guid.NewGuid().ToString("N"));
    private readonly FileLog _log;

    public CapturedAppLog()
    {
        Directory.CreateDirectory(_directory);
        _log = new FileLog(Path.Combine(_directory, "capture.log")) { Enabled = true };
        AppLog.Instance = _log;
    }

    /// <summary>Everything logged so far (flushes the writer first).</summary>
    public string Text()
    {
        _log.Flush();
        return File.Exists(_log.FilePath) ? File.ReadAllText(_log.FilePath) : string.Empty;
    }

    public void Dispose()
    {
        AppLog.Instance = null!; // back to FileLog.Default
        _log.Dispose();
        try { Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Runs a body on a fresh STA thread with the real-dialog guard installed (same pattern as the other UI tests), so WPF
/// elements can be created and measured off the xUnit worker threads. Windows created by a body only live as long as it.
/// </summary>
internal static class StaUi
{
    public static void Run(Action body)
    {
        Exception? failure = null;
        InvalidOperationException? dialog = null;
        var thread = new Thread(() =>
        {
            using var guard = Win32DialogGuard.InstallOnCurrentThread();
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                dialog = guard.CreateException();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("The STA body did not finish.");
        // A real dialog is only a symptom of what the body did; report it ahead of the body's own exception.
        if (dialog is not null) throw dialog;
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
