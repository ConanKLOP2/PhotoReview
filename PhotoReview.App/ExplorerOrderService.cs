using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
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

public sealed class ExplorerOrderService : IExplorerOrderProvider, IDisposable
{
    public sealed record ExplorerQueryProgress(int ItemsRead, int ItemCount, int ComCalls);

    /// <summary>
    /// A single, long-lived STA thread that serializes all Explorer COM calls onto one OS thread.
    /// Explorer's IFolderView2/related COM objects are apartment-bound and must be accessed from the
    /// same STA thread that created/obtained them; this pump avoids the cost of spinning up a brand-new
    /// STA thread per query while still guaranteeing one-call-at-a-time execution.
    /// </summary>
    private sealed class StaThreadPump : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public StaThreadPump()
        {
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "PhotoReview Explorer view" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void RunLoop()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex) { AppLog.Error("Explorer STA pump action escaped unexpectedly", ex); }
            }
        }

        public Task<ExplorerViewSnapshot> Enqueue(Func<ExplorerViewSnapshot> work)
        {
            var completion = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(() =>
                {
                    try { completion.TrySetResult(work()); }
                    catch (OperationCanceledException ex) { completion.TrySetException(ex); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                });
            }
            catch (InvalidOperationException)
            {
                // Queue was completed/disposed concurrently with shutdown.
                completion.TrySetException(new ObjectDisposedException(nameof(StaThreadPump)));
            }
            return completion.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (_thread.Join(TimeSpan.FromSeconds(5)))
                _queue.Dispose();
            else
                AppLog.Error("Explorer STA pump thread did not exit within 5s; leaking queue instead of risking ObjectDisposedException on a stuck COM call");
        }
    }

    private readonly StaThreadPump _pump = new();

    public void Dispose() => _pump.Dispose();

    /// <summary>Progressive variant used by the UI: enumeration yields between small batches and can be cancelled.</summary>
    public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout,
        CancellationToken cancellationToken, IProgress<ExplorerQueryProgress>? progress = null, int batchSize = 16)
    {
        batchSize = Math.Clamp(batchSize, 1, 128);
        return TryGetSnapshotCoreAsync(folder, timeout, cancellationToken, progress, batchSize);
    }

    public async Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken)
        => await TryGetSnapshotCoreAsync(folder, timeout, cancellationToken, null, int.MaxValue);

    private async Task<ExplorerViewSnapshot> TryGetSnapshotCoreAsync(string folder, TimeSpan timeout,
        CancellationToken cancellationToken, IProgress<ExplorerQueryProgress>? progress, int batchSize)
    {
        var canonicalFolder = ExplorerSnapshotValidator.CanonicalizeFolder(folder);
        if (cancellationToken.IsCancellationRequested) return Unavailable(canonicalFolder, ExplorerOrderStatus.Canceled, "Request canceled");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var linkedToken = timeoutCts.Token;
        var workTask = _pump.Enqueue(() =>
        {
            try { return QueryShell(canonicalFolder, linkedToken, progress, batchSize); }
            catch (OperationCanceledException) { return Unavailable(canonicalFolder, ExplorerOrderStatus.Canceled, "Request canceled during native enumeration"); }
            catch (Exception ex) { AppLog.Error("Explorer native view query failed", ex); return Unavailable(canonicalFolder, ExplorerOrderStatus.Failed, ex.GetType().Name); }
        });
        try
        {
            return await workTask.WaitAsync(linkedToken);
        }
        catch (OperationCanceledException)
        {
            var status = cancellationToken.IsCancellationRequested ? ExplorerOrderStatus.Canceled : ExplorerOrderStatus.TimedOut;
            return Unavailable(canonicalFolder, status, status == ExplorerOrderStatus.TimedOut ? "Native view query exceeded timeout" : "Request canceled");
        }
    }

    private static ExplorerViewSnapshot QueryShell(string folder, CancellationToken cancellationToken,
        IProgress<ExplorerQueryProgress>? progress, int batchSize)
    {
        var queryTimer = Stopwatch.StartNew();
        AppLog.Info($"Explorer query-start: folder={folder}");
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
                    try { return TryReadNativeView(window, folder, cancellationToken, progress, batchSize); }
                    catch (Exception ex) { return Unavailable(folder, ExplorerOrderStatus.Failed, $"Native view failed: {ex.GetType().Name}, HRESULT=0x{ex.HResult:X8}"); }
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                catch (Exception ex) { AppLog.Error($"Explorer window inspection failed after {windowsInspected} window(s)", ex); }
                finally { Release(window); }
            }
            var unavailable = Unavailable(folder, ExplorerOrderStatus.NoMatchingWindow, $"No matching Explorer window among {windowsInspected} window(s)");
            AppLog.Info($"Explorer query-complete: status={unavailable.Status}, windows={windowsInspected}, elapsedMs={queryTimer.ElapsedMilliseconds}");
            return unavailable;
        }
        finally { Release(windows); Release(shell); }
    }

    private static ExplorerViewSnapshot TryReadNativeView(object window, string folder, CancellationToken cancellationToken,
        IProgress<ExplorerQueryProgress>? progress, int batchSize)
    {
        var timer = Stopwatch.StartNew();
        var getItemCalls = 0;
        var displayNameCalls = 0;
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
            var itemCountElapsed = timer.ElapsedMilliseconds;
            AppLog.Info($"Explorer native-read-start: count={count}, itemCountElapsedMs={itemCountElapsed}");
            var paths = new List<string>(count);
            var comCalls = 1;
            var getItem = ExplorerNativeVtable.ResolveGetItem(folderViewPtr);
            // COM does not guarantee every IShellItem from IFolderView2.GetItem shares the
            // same implementation/vtable (in practice they usually do, within one folder
            // view), so a delegate resolved from one item's vtable slot cannot be safely
            // reused with another item's `this` pointer. Cache per distinct vtable address
            // instead of per call: the common case (one shared vtable) still resolves once.
            var getDisplayNameByVtable = new Dictionary<IntPtr, ExplorerNativeVtable.GetDisplayNameDelegate>();
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr itemPtr = IntPtr.Zero;
                try
                {
                    var itemIid = ExplorerComInterop.IidShellItem;
                    getItemCalls++;
                    var itemResult = getItem(folderViewPtr, index, ref itemIid, out itemPtr);
                    comCalls++;
                    if (itemResult < 0 || itemPtr == IntPtr.Zero) return Unavailable(folder, ExplorerOrderStatus.NativeViewUnavailable, $"IFolderView2.GetItem({index}) failed: 0x{itemResult:X8}");
                    var itemVtable = Marshal.ReadIntPtr(itemPtr);
                    if (!getDisplayNameByVtable.TryGetValue(itemVtable, out var getDisplayName))
                        getDisplayNameByVtable[itemVtable] = getDisplayName = ExplorerNativeVtable.ResolveGetDisplayName(itemPtr);
                    displayNameCalls++;
                    var nameResult = getDisplayName(itemPtr, ExplorerComInterop.SigdnFileSystemPath, out var namePtr);
                    comCalls++;
                    if (nameResult < 0) return Unavailable(folder, ExplorerOrderStatus.NativeViewUnavailable, $"IShellItem.GetDisplayName({index}) failed: 0x{nameResult:X8}");
                    try { paths.Add(Marshal.PtrToStringUni(namePtr) ?? string.Empty); }
                    finally { Marshal.FreeCoTaskMem(namePtr); }
                }
                finally { if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr); }
                progress?.Report(new ExplorerQueryProgress(paths.Count, count, comCalls));
                if (batchSize != int.MaxValue && paths.Count % batchSize == 0)
                    Thread.Yield();
            }
            var sorts = ReadSortColumns(folderViewPtr);
            var grouped = ExplorerNativeVtable.GetGroupBy(folderViewPtr, out var groupKey, out _) >= 0 && (groupKey.fmtid != Guid.Empty || groupKey.pid != 0);
            var first = paths.Count > 0 ? paths[0] : string.Empty;
            var last = paths.Count > 0 ? paths[^1] : string.Empty;
            AppLog.Info($"Explorer native-read-complete: count={paths.Count}, getItemCalls={getItemCalls}, displayNameCalls={displayNameCalls}, elapsedMs={timer.ElapsedMilliseconds}, firstPath={first}, lastPath={last}, sortColumns={sorts.Count}, grouped={grouped}");
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
