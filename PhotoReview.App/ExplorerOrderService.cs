using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace PhotoReview.App;

public sealed class ExplorerOrderService
{
    public async Task<IReadOnlyList<string>?> TryGetOrderAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var task = Task.Run(() => QueryShell(folder), timeoutCts.Token);
            var completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token));
            if (completed != task) { AppLog.Info($"Explorer order unavailable/timeout: {folder}"); return null; }
            return await task;
        }
        catch (OperationCanceledException) { AppLog.Info($"Explorer order canceled: {folder}"); return null; }
        catch (Exception ex) { AppLog.Error($"Explorer order failed: {folder}", ex); return null; }
    }

    private static IReadOnlyList<string>? QueryShell(string folder)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return null;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            dynamic windows = shell!.GetType().InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null)!;
            foreach (dynamic window in (IEnumerable)windows)
            {
                try
                {
                    var location = (string?)window.LocationURL;
                    if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(location, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
                    var current = Path.GetFullPath(uri.LocalPath).TrimEnd(Path.DirectorySeparatorChar);
                    if (!string.Equals(current, folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
                    var ordered = new List<string>();
                    foreach (dynamic item in (IEnumerable)window.Document.Folder.Items())
                    {
                        var path = (string?)item.Path;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) ordered.Add(Path.GetFullPath(path));
                    }
                    return ordered.Count > 0 ? ordered : null;
                }
                catch (COMException) { }
                catch (RuntimeBinderException) { }
            }
            return null;
        }
        finally { if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
    }
}
