using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Platform.Windows.Explorer;

public sealed class ExplorerOrderService : IExplorerOrderProvider, IDisposable
{
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
        private readonly ILog _log;

        public StaThreadPump(ILog log)
        {
            _log = log;
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "PhotoReview Explorer view" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void RunLoop()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex) { _log.Error("Explorer STA pump action escaped unexpectedly", ex); }
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

        private int _disposed;

        public void Dispose()
        {
            // AR02c: MainWindow.Window_Closed disposes the (singleton) IExplorerOrderProvider explicitly,
            // and a DI container that owns this singleton (Benchmark.Cli/AppHost, and AR02b's integration
            // tests) disposes it again when the ServiceProvider itself is disposed. IDisposable.Dispose
            // must tolerate being called more than once (standard contract) rather than throw on the
            // second call's CompleteAdding()/Dispose() against an already-disposed BlockingCollection.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _queue.CompleteAdding();
            if (_thread.Join(TimeSpan.FromSeconds(5)))
                _queue.Dispose();
            else
                _log.Error("Explorer STA pump thread did not exit within 5s; leaking queue instead of risking ObjectDisposedException on a stuck COM call");
        }
    }

    private readonly ILog _log;
    private readonly StaThreadPump _pump;

    public ExplorerOrderService(ILog? log = null)
    {
        _log = log ?? NullLog.Instance;
        _pump = new StaThreadPump(_log);
    }

    public void Dispose()
    {
        TakePrefetch()?.Cts.Cancel();
        _pump.Dispose();
    }

    private sealed record PrefetchedQuery(string Folder, Task<ExplorerViewSnapshot> Task, CancellationTokenSource Cts);

    private readonly object _prefetchGate = new();
    private PrefetchedQuery? _prefetch;

    private PrefetchedQuery? TakePrefetch()
    {
        lock (_prefetchGate)
        {
            var taken = _prefetch;
            _prefetch = null;
            return taken;
        }
    }

    /// <inheritdoc />
    public void Prefetch(string folder, TimeSpan timeout)
    {
        var canonicalFolder = ExplorerSnapshotValidator.CanonicalizeFolder(folder);
        var cts = new CancellationTokenSource();
        var task = TryGetSnapshotCoreAsync(canonicalFolder, timeout, null, 16, cts.Token);
        PrefetchedQuery? superseded;
        lock (_prefetchGate)
        {
            superseded = _prefetch;
            _prefetch = new PrefetchedQuery(canonicalFolder, task, cts);
        }
        superseded?.Cts.Cancel();
        _log.Info($"Explorer prefetch-start: folder={canonicalFolder}");
    }

    /// <summary>Progressive variant used by the UI: enumeration yields between small batches and can be cancelled.</summary>
    public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout,
        IProgress<ExplorerQueryProgress>? progress = null, int batchSize = 16, CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 128);
        // perf(startup): a query prefetched for this folder is joined (one-shot); one prefetched for
        // another folder is cancelled so it stops occupying the single STA pump thread.
        var prefetched = TakePrefetch();
        if (prefetched is not null)
        {
            if (ExplorerSnapshotValidator.SamePath(prefetched.Folder, ExplorerSnapshotValidator.CanonicalizeFolder(folder)))
                return JoinPrefetchAsync(prefetched, timeout, cancellationToken);
            prefetched.Cts.Cancel();
        }
        return TryGetSnapshotCoreAsync(folder, timeout, progress, batchSize, cancellationToken);
    }

    /// <summary>
    /// Waits for a prefetched query under the caller's own timeout/cancellation. The query started
    /// earlier, so it has already used part of its budget; the caller's timeout still bounds how long
    /// the caller itself waits. Giving up cancels the query so the STA pump is freed.
    /// </summary>
    private static async Task<ExplorerViewSnapshot> JoinPrefetchAsync(PrefetchedQuery prefetched, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await prefetched.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            prefetched.Cts.Cancel();
            return Unavailable(prefetched.Folder, ExplorerOrderStatus.TimedOut, "Native view query exceeded timeout");
        }
        catch (OperationCanceledException)
        {
            prefetched.Cts.Cancel();
            return Unavailable(prefetched.Folder, ExplorerOrderStatus.Canceled, "Request canceled");
        }
    }

    public async Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken)
        => await TryGetSnapshotCoreAsync(folder, timeout, null, int.MaxValue, cancellationToken).ConfigureAwait(false);

    private async Task<ExplorerViewSnapshot> TryGetSnapshotCoreAsync(string folder, TimeSpan timeout,
        IProgress<ExplorerQueryProgress>? progress, int batchSize, CancellationToken cancellationToken)
    {
        var canonicalFolder = ExplorerSnapshotValidator.CanonicalizeFolder(folder);
        if (cancellationToken.IsCancellationRequested) return Unavailable(canonicalFolder, ExplorerOrderStatus.Canceled, "Request canceled");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var linkedToken = timeoutCts.Token;
        var workTask = _pump.Enqueue(() =>
        {
            try { return QueryShell(canonicalFolder, progress, batchSize, linkedToken); }
            catch (OperationCanceledException) { return Unavailable(canonicalFolder, ExplorerOrderStatus.Canceled, "Request canceled during native enumeration"); }
            catch (Exception ex) { _log.Error("Explorer native view query failed", ex); return Unavailable(canonicalFolder, ExplorerOrderStatus.Failed, ex.GetType().Name); }
        });
        try
        {
            return await workTask.WaitAsync(linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var status = cancellationToken.IsCancellationRequested ? ExplorerOrderStatus.Canceled : ExplorerOrderStatus.TimedOut;
            return Unavailable(canonicalFolder, status, status == ExplorerOrderStatus.TimedOut ? "Native view query exceeded timeout" : "Request canceled");
        }
    }

    private ExplorerViewSnapshot QueryShell(string folder, IProgress<ExplorerQueryProgress>? progress,
        int batchSize, CancellationToken cancellationToken)
    {
        var queryTimer = Stopwatch.StartNew();
        _log.Info($"Explorer query-start: folder={folder}");
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return Unavailable(folder, ExplorerOrderStatus.NativeViewUnavailable, "Shell.Application unavailable");
        // The caller's timeout only cancels the Task it is awaiting; this action already
        // started running on the single STA pump thread and must check the token itself,
        // or a stale/superseded query keeps occupying that thread and delays whatever
        // query was actually issued for it next (e.g. a fast folder switch).
        if (cancellationToken.IsCancellationRequested) return Unavailable(folder, ExplorerOrderStatus.Canceled, "Request canceled before native enumeration");
        object? shell = null, windows = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            windows = shell!.GetType().InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null, CultureInfo.InvariantCulture);
            var windowsInspected = 0;
            foreach (var window in (IEnumerable)windows!)
            {
                try
                {
                    windowsInspected++;
                    if (cancellationToken.IsCancellationRequested) return Unavailable(folder, ExplorerOrderStatus.Canceled, "Request canceled during window enumeration");
                    var location = (string?)((dynamic)window).LocationURL;
                    if (!TryCanonicalizeLocation(location, out var current)) continue;
                    if (!ExplorerSnapshotValidator.SamePath(current, folder)) continue;
                    try { return TryReadNativeView(window, folder, progress, batchSize, cancellationToken); }
                    catch (OperationCanceledException) { return Unavailable(folder, ExplorerOrderStatus.Canceled, "Request canceled during native enumeration"); }
                    catch (Exception ex) { return Unavailable(folder, ExplorerOrderStatus.Failed, $"Native view failed: {ex.GetType().Name}, HRESULT=0x{ex.HResult:X8}"); }
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                catch (Exception ex) { _log.Error($"Explorer window inspection failed after {windowsInspected} window(s)", ex); }
                finally { Release(window); }
            }
            var unavailable = Unavailable(folder, ExplorerOrderStatus.NoMatchingWindow, $"No matching Explorer window among {windowsInspected} window(s)");
            _log.Info($"Explorer query-complete: status={unavailable.Status}, windows={windowsInspected}, elapsedMs={queryTimer.ElapsedMilliseconds}");
            return unavailable;
        }
        finally { Release(windows); Release(shell); }
    }

    private ExplorerViewSnapshot TryReadNativeView(object window, string folder,
        IProgress<ExplorerQueryProgress>? progress, int batchSize, CancellationToken cancellationToken)
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
            _log.Info($"Explorer native-read-start: count={count}, itemCountElapsedMs={itemCountElapsed}");
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
            _log.Info($"Explorer native-read-complete: count={paths.Count}, getItemCalls={getItemCalls}, displayNameCalls={displayNameCalls}, elapsedMs={timer.ElapsedMilliseconds}, firstPath={first}, lastPath={last}, sortColumns={sorts.Length}, grouped={grouped}");
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

    private static ExplorerSortColumn[] ReadSortColumns(IntPtr view)
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
