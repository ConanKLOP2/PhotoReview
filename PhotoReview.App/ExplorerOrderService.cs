using System.Collections;
using System.IO;
using System.Runtime.InteropServices;

namespace PhotoReview.App;

public enum ExplorerOrderStatus { Available, NoMatchingWindow, NativeViewUnavailable, InvalidSnapshot, TimedOut, Canceled, Failed }
public enum ExplorerSortDirection { Unknown, Ascending, Descending }
public enum ExplorerGroupState { None, Active, Unknown }
public sealed record ExplorerSortColumn(Guid PropertySet, uint PropertyId, ExplorerSortDirection Direction);
public sealed record ExplorerViewSnapshot(string Folder, IReadOnlyList<string> OrderedPaths, IReadOnlyList<ExplorerSortColumn> SortColumns,
    ExplorerGroupState GroupState, ExplorerOrderStatus Status, string? Reason, DateTime CapturedUtc);

public interface IExplorerOrderProvider
{
    Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class ExplorerOrderService : IExplorerOrderProvider
{
    public async Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var canonicalFolder = ExplorerSnapshotValidator.CanonicalizeFolder(folder);
        if (cancellationToken.IsCancellationRequested) return Unavailable(canonicalFolder, ExplorerOrderStatus.Canceled, "Request canceled");
        var completion = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.TrySetResult(QueryShell(canonicalFolder)); }
            catch (Exception ex) { AppLog.Error("Explorer native view query failed", ex); completion.TrySetResult(Unavailable(canonicalFolder, ExplorerOrderStatus.Failed, ex.GetType().Name)); }
        }) { IsBackground = true, Name = "PhotoReview Explorer view" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            return await completion.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            var status = cancellationToken.IsCancellationRequested ? ExplorerOrderStatus.Canceled : ExplorerOrderStatus.TimedOut;
            return Unavailable(canonicalFolder, status, status == ExplorerOrderStatus.TimedOut ? "Native view query exceeded timeout" : "Request canceled");
        }
    }

    private static ExplorerViewSnapshot QueryShell(string folder)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return Unavailable(folder, ExplorerOrderStatus.NativeViewUnavailable, "Shell.Application unavailable");
        object? shell = null, windows = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            windows = shell!.GetType().InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
            var windowsInspected = 0;
            foreach (var window in (IEnumerable)windows!)
            {
                try
                {
                    windowsInspected++;
                    var location = (string?)((dynamic)window).LocationURL;
                    if (!TryCanonicalizeLocation(location, out var current)) continue;
                    if (!ExplorerSnapshotValidator.SamePath(current, folder)) continue;
                    try { return TryReadNativeView(window, folder); }
                    catch (Exception ex) { return Unavailable(folder, ExplorerOrderStatus.Failed, $"Native view failed: {ex.GetType().Name}, HRESULT=0x{ex.HResult:X8}"); }
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                finally { Release(window); }
            }
            return Unavailable(folder, ExplorerOrderStatus.NoMatchingWindow, $"No matching Explorer window among {windowsInspected} window(s)");
        }
        finally { Release(windows); Release(shell); }
    }

    private static ExplorerViewSnapshot TryReadNativeView(object window, string folder)
    {
        IntPtr windowPtr = IntPtr.Zero, servicePtr = IntPtr.Zero, browserPtr = IntPtr.Zero, shellViewPtr = IntPtr.Zero, folderViewPtr = IntPtr.Zero;
        try
        {
            windowPtr = Marshal.GetIUnknownForObject(window);
            var serviceIid = ExplorerComInterop.IidServiceProvider;
            var providerResult = Marshal.QueryInterface(windowPtr, in serviceIid, out servicePtr);
            if (providerResult < 0 || servicePtr == IntPtr.Zero) return Unavailable(folder, ExplorerOrderStatus.NativeViewUnavailable, $"QueryInterface(IServiceProvider) failed: 0x{providerResult:X8}");
            var service = ExplorerComInterop.SidTopLevelBrowser; var browserIid = ExplorerComInterop.IidShellBrowser;
            var serviceResult = ExplorerNativeVtable.QueryService(servicePtr, ref service, ref browserIid, out browserPtr);
            if (serviceResult < 0 || browserPtr == IntPtr.Zero) return Unavailable(folder, ExplorerOrderStatus.NativeViewUnavailable, $"QueryService(IShellBrowser) failed: 0x{serviceResult:X8}");
            var activeViewResult = ExplorerNativeVtable.QueryActiveShellView(browserPtr, out shellViewPtr);
            if (activeViewResult < 0 || shellViewPtr == IntPtr.Zero) return Unavailable(folder, ExplorerOrderStatus.NativeViewUnavailable, $"QueryActiveShellView failed: 0x{activeViewResult:X8}");
            var viewIid = ExplorerComInterop.IidFolderView2;
            var folderViewResult = Marshal.QueryInterface(shellViewPtr, in viewIid, out folderViewPtr);
            if (folderViewResult < 0 || folderViewPtr == IntPtr.Zero) return Unavailable(folder, ExplorerOrderStatus.NativeViewUnavailable, $"QueryInterface(IFolderView2) failed: 0x{folderViewResult:X8}");
            var countResult = ExplorerNativeVtable.ItemCount(folderViewPtr, ExplorerComInterop.SvgioAllView, out var count);
            if (countResult < 0 || count <= 0) return Unavailable(folder, ExplorerOrderStatus.NativeViewUnavailable, $"IFolderView2.ItemCount failed/empty: 0x{countResult:X8}, count={count}");
            var paths = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                IntPtr itemPtr = IntPtr.Zero;
                try
                {
                    var itemIid = ExplorerComInterop.IidShellItem;
                    var itemResult = ExplorerNativeVtable.GetItem(folderViewPtr, index, ref itemIid, out itemPtr);
                    if (itemResult < 0 || itemPtr == IntPtr.Zero) return Unavailable(folder, ExplorerOrderStatus.NativeViewUnavailable, $"IFolderView2.GetItem({index}) failed: 0x{itemResult:X8}");
                    var nameResult = ExplorerNativeVtable.GetDisplayName(itemPtr, ExplorerComInterop.SigdnFileSystemPath, out var namePtr);
                    if (nameResult < 0) return Unavailable(folder, ExplorerOrderStatus.NativeViewUnavailable, $"IShellItem.GetDisplayName({index}) failed: 0x{nameResult:X8}");
                    try { paths.Add(Marshal.PtrToStringUni(namePtr) ?? string.Empty); }
                    finally { Marshal.FreeCoTaskMem(namePtr); }
                }
                finally { if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr); }
            }
            var sorts = ReadSortColumns(folderViewPtr);
            var grouped = ExplorerNativeVtable.GetGroupBy(folderViewPtr, out var groupKey, out _) >= 0 && (groupKey.fmtid != Guid.Empty || groupKey.pid != 0);
            return new ExplorerViewSnapshot(folder, paths, sorts, grouped ? ExplorerGroupState.Active : ExplorerGroupState.None,
                ExplorerOrderStatus.Available, null, DateTime.UtcNow);
        }
        finally
        {
            if (folderViewPtr != IntPtr.Zero) Marshal.Release(folderViewPtr);
            if (shellViewPtr != IntPtr.Zero) Marshal.Release(shellViewPtr);
            if (browserPtr != IntPtr.Zero) Marshal.Release(browserPtr);
            if (servicePtr != IntPtr.Zero) Marshal.Release(servicePtr);
            if (windowPtr != IntPtr.Zero) Marshal.Release(windowPtr);
        }
    }

    private static IReadOnlyList<ExplorerSortColumn> ReadSortColumns(IntPtr view)
    {
        if (ExplorerNativeVtable.GetSortColumnCount(view, out var count) < 0 || count <= 0 || count > 32) return [];
        var native = new SORTCOLUMN[count];
        if (ExplorerNativeVtable.GetSortColumns(view, native, count) < 0) return [];
        return native.Select(c => new ExplorerSortColumn(c.propkey.fmtid, c.propkey.pid,
            c.direction == 1 ? ExplorerSortDirection.Ascending : c.direction == -1 ? ExplorerSortDirection.Descending : ExplorerSortDirection.Unknown)).ToArray();
    }

    private static bool TryCanonicalizeLocation(string? location, out string folder)
    {
        folder = string.Empty;
        if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(location, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        folder = ExplorerSnapshotValidator.CanonicalizeFolder(uri.LocalPath); return true;
    }

    private static ExplorerViewSnapshot Unavailable(string folder, ExplorerOrderStatus status, string reason)
        => new(folder, [], [], ExplorerGroupState.Unknown, status, reason, DateTime.UtcNow);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}

public static class ExplorerSnapshotValidator
{
    public static bool TryValidate(ExplorerViewSnapshot snapshot, IReadOnlyCollection<string> scannedFiles, out IReadOnlyList<string> ordered, out string? reason)
    {
        ordered = []; reason = null;
        if (snapshot.Status != ExplorerOrderStatus.Available) { reason = snapshot.Reason ?? snapshot.Status.ToString(); return false; }
        var folder = CanonicalizeFolder(snapshot.Folder);
        var expected = new HashSet<string>(scannedFiles.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(expected.Count); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in snapshot.OrderedPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate)) { reason = "Native view returned an empty path"; return false; }
            var path = Path.GetFullPath(candidate);
            if (!SamePath(Path.GetDirectoryName(path) ?? string.Empty, folder)) { reason = "Native view returned an item outside the folder"; return false; }
            if (!seen.Add(path)) { reason = "Native view returned a duplicate item"; return false; }
            if (expected.Contains(path)) result.Add(path);
        }
        if (result.Count != expected.Count || !expected.SetEquals(result)) { reason = "Native view did not contain the complete image snapshot"; return false; }
        ordered = result; return true;
    }
    public static string CanonicalizeFolder(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    public static bool SamePath(string first, string second) => string.Equals(CanonicalizeFolder(first), CanonicalizeFolder(second), StringComparison.OrdinalIgnoreCase);
}
