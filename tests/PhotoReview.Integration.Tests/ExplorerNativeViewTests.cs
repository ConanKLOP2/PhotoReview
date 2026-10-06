using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Platform.Windows.Explorer;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// The native half of <see cref="ExplorerOrderService"/> (IServiceProvider -> IShellBrowser -> IShellView -> IFolderView2 ->
/// IEnumIDList / IShellItem) driven against a hand-built fake COM object graph: real unmanaged vtables whose slots point at test
/// delegates, a managed window exposed through a real COM callable wrapper, and the real shell32 PIDL calls on real files in a temp
/// folder. No Explorer window, no Shell.Application and no user data is involved. Every fake object counts AddRef/Release so each
/// test can assert that the production code released every interface pointer it was handed.
/// </summary>
public sealed class ExplorerNativeViewTests : IDisposable
{
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int EFail = unchecked((int)0x80004005);
    private const int SFalse = 1;

    private readonly string _root;
    private readonly List<IDisposable> _disposables = [];
    private readonly ExplorerOrderService _service = new(null, null);

    public ExplorerNativeViewTests()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PhotoReviewNative_" + Guid.NewGuid().ToString("N")));
        _root = LongPath(dir.FullName); // the shell reports long names; a short (8.3) temp path would not compare equal
    }

    public void Dispose()
    {
        _service.Dispose();
        for (var i = _disposables.Count - 1; i >= 0; i--) _disposables[i].Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW", ExactSpelling = true)]
    private static extern int GetLongPathName(string shortPath, [Out] char[] longPath, int length);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern IntPtr ILClone(IntPtr pidl);

    private static string LongPath(string path)
    {
        var buffer = new char[1024];
        var length = GetLongPathName(path, buffer, buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, length) : path;
    }

    private string[] MakeFiles(params string[] names)
    {
        var paths = new string[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            paths[i] = Path.Combine(_root, names[i]);
            File.WriteAllBytes(paths[i], [1]);
        }
        return paths;
    }

    // ---- the fake COM objects ----

    private delegate int QueryActiveShellViewFn(IntPtr self, out IntPtr view);
    private delegate int ItemCountFn(IntPtr self, uint flags, out int count);
    private delegate int ItemsFn(IntPtr self, uint flags, ref Guid iid, out IntPtr result);
    private delegate int GetGroupByFn(IntPtr self, out PROPERTYKEY key, out int ascending);
    private delegate int GetSortColumnCountFn(IntPtr self, out int count);
    private delegate int GetSortColumnsFn(IntPtr self, IntPtr columns, int count);
    private delegate int GetItemFn(IntPtr self, int index, ref Guid iid, out IntPtr item);
    private delegate int GetDisplayNameFn(IntPtr self, uint kind, out IntPtr name);
    private delegate int EnumNextFn(IntPtr self, uint count, IntPtr items, out uint fetched);

    /// <summary>
    /// An unmanaged object: <c>[vtable pointer]</c> plus a vtable of native thunks (UnmanagedCallersOnly methods, so the
    /// production code's <c>GetDelegateForFunctionPointer</c> wraps genuinely native code), reference counting and a
    /// QueryInterface table. A thunk finds its managed handler through the object pointer it is called with.
    /// </summary>
    private sealed unsafe class NativeObject : IDisposable
    {
        private const int SlotCount = 40;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, NativeObject> Registry = new();
        private readonly IntPtr _vtable = Marshal.AllocHGlobal(SlotCount * IntPtr.Size);
        private readonly Dictionary<int, Delegate> _handlers = [];
        private readonly Dictionary<Guid, NativeObject> _interfaces = [];
        private int _refs;

        public NativeObject()
        {
            for (var i = 0; i < SlotCount; i++) Marshal.WriteIntPtr(_vtable, i * IntPtr.Size, IntPtr.Zero);
            Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(Pointer, _vtable);
            Registry[Pointer] = this;
            Marshal.WriteIntPtr(_vtable, 0, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&Thunks.QueryInterface);
            Marshal.WriteIntPtr(_vtable, IntPtr.Size, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Thunks.AddRef);
            Marshal.WriteIntPtr(_vtable, 2 * IntPtr.Size, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Thunks.Release);
        }

        public IntPtr Pointer { get; }
        public int Refs => Volatile.Read(ref _refs);

        public int AddRef() => Interlocked.Increment(ref _refs);
        public int Release() => Interlocked.Decrement(ref _refs);

        /// <summary>A new reference to this object, the way a COM method hands out an interface pointer.</summary>
        public IntPtr Acquire()
        {
            AddRef();
            return Pointer;
        }

        /// <summary>Installs <paramref name="thunk"/> in a vtable slot; it dispatches to <paramref name="handler"/>.</summary>
        public void On(int slot, Delegate handler, IntPtr thunk)
        {
            _handlers[slot] = handler;
            Marshal.WriteIntPtr(_vtable, slot * IntPtr.Size, thunk);
        }

        public void Supports(Guid iid, NativeObject target) => _interfaces[iid] = target;
        public void Forget(Guid iid) => _interfaces.Remove(iid);

        internal static NativeObject Find(IntPtr self) => Registry[self];
        internal T Handler<T>(int slot) where T : Delegate => (T)_handlers[slot];

        internal int QueryInterface(ref Guid iid, out IntPtr obj)
        {
            if (_interfaces.TryGetValue(iid, out var target))
            {
                obj = target.Acquire();
                return 0;
            }
            obj = IntPtr.Zero;
            return ENoInterface;
        }

        public void Dispose()
        {
            Registry.TryRemove(Pointer, out _);
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(_vtable);
        }
    }

    private static unsafe class Thunks
    {
        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static int QueryInterface(IntPtr self, Guid* iid, IntPtr* obj)
        {
            var result = NativeObject.Find(self).QueryInterface(ref *iid, out var found);
            *obj = found;
            return result;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static uint AddRef(IntPtr self) => (uint)NativeObject.Find(self).AddRef();

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static uint Release(IntPtr self) => (uint)NativeObject.Find(self).Release();

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static int QueryActiveShellView(IntPtr self, IntPtr* view)
        {
            var result = NativeObject.Find(self).Handler<QueryActiveShellViewFn>(15)(self, out var found);
            *view = found;
            return result;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static int ItemCount(IntPtr self, uint flags, int* count)
        {
            var result = NativeObject.Find(self).Handler<ItemCountFn>(7)(self, flags, out var found);
            *count = found;
            return result;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static int Items(IntPtr self, uint flags, Guid* iid, IntPtr* items)
        {
            var requested = *iid;
            var result = NativeObject.Find(self).Handler<ItemsFn>(8)(self, flags, ref requested, out var found);
            *items = found;
            return result;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static int GetGroupBy(IntPtr self, PROPERTYKEY* key, int* ascending)
        {
            var result = NativeObject.Find(self).Handler<GetGroupByFn>(18)(self, out var foundKey, out var foundAscending);
            *key = foundKey;
            *ascending = foundAscending;
            return result;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static int GetSortColumnCount(IntPtr self, int* count)
        {
            var result = NativeObject.Find(self).Handler<GetSortColumnCountFn>(26)(self, out var found);
            *count = found;
            return result;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static int GetSortColumns(IntPtr self, IntPtr columns, int count)
            => NativeObject.Find(self).Handler<GetSortColumnsFn>(28)(self, columns, count);

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static int GetItem(IntPtr self, int index, Guid* iid, IntPtr* item)
        {
            var requested = *iid;
            var result = NativeObject.Find(self).Handler<GetItemFn>(29)(self, index, ref requested, out var found);
            *item = found;
            return result;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static int GetDisplayName(IntPtr self, uint kind, IntPtr* name)
        {
            var result = NativeObject.Find(self).Handler<GetDisplayNameFn>(5)(self, kind, out var found);
            *name = found;
            return result;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        public static int EnumNext(IntPtr self, uint count, IntPtr items, uint* fetched)
        {
            var result = NativeObject.Find(self).Handler<EnumNextFn>(3)(self, count, items, out var found);
            *fetched = found;
            return result;
        }
    }

    /// <summary>What the fake Explorer window shows, and where each COM call fails.</summary>
    private sealed unsafe class View : IDisposable
    {
        private readonly NativeObject _browser = new();
        private readonly NativeObject _shellView = new();
        private readonly NativeObject _folderView = new();
        private readonly NativeObject _enumerator = new();
        private readonly NativeObject _item = new();
        private int _enumPosition;
        private int _currentIndex;

        public View(string folder, string[] paths)
        {
            Folder = folder;
            Paths = paths;
            _browser.On(15, new QueryActiveShellViewFn(QueryActiveShellView), (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&Thunks.QueryActiveShellView);
            _shellView.Supports(ExplorerComInterop.IidFolderView2, _folderView);
            _folderView.On(7, new ItemCountFn(ItemCount), (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int*, int>)&Thunks.ItemCount);
            _folderView.On(8, new ItemsFn(Items), (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)&Thunks.Items);
            _folderView.On(18, new GetGroupByFn(GetGroupBy), (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, PROPERTYKEY*, int*, int>)&Thunks.GetGroupBy);
            _folderView.On(26, new GetSortColumnCountFn(GetSortColumnCount), (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, int>)&Thunks.GetSortColumnCount);
            _folderView.On(28, new GetSortColumnsFn(GetSortColumns), (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)&Thunks.GetSortColumns);
            _folderView.On(29, new GetItemFn(GetItem), (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, IntPtr*, int>)&Thunks.GetItem);
            _enumerator.On(3, new EnumNextFn(EnumNext), (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint*, int>)&Thunks.EnumNext);
            _item.On(5, new GetDisplayNameFn(GetDisplayName), (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)&Thunks.GetDisplayName);
        }

        public string Folder { get; }
        public string[] Paths { get; }

        // where it fails / what it reports
        public int BrowserServiceHr { get; set; }
        public int ActiveViewHr { get; set; }
        public bool SupportsFolderView2 { get; set; } = true;
        public int ItemCountHr { get; set; }
        public int? ReportedCount { get; set; }
        public int ItemsHr { get; set; }
        public int EnumHidesLast { get; set; }          // the enumerator yields fewer items than the view reports
        public int EnumChunk { get; set; } = int.MaxValue; // items handed out per Next call (S_OK while more follow)
        public int EnumNextHr { get; set; }
        public int GetItemFailsAt { get; set; } = -1;
        public int DisplayNameHr { get; set; }
        public int SortCountHr { get; set; }
        public int SortCount { get; set; }
        public int SortsHr { get; set; }
        public SORTCOLUMN[] Sorts { get; set; } = [];
        public int GroupHr { get; set; }
        public PROPERTYKEY GroupKey { get; set; }
        public Action<int>? OnGetItem { get; set; }

        // what the production code asked
        public uint ItemsFlags { get; private set; }
        public Guid ItemsIid { get; private set; }
        public List<int> ItemsRequested { get; } = [];
        public int EnumNextCalls { get; private set; }

        public NativeObject[] AllObjects => [_browser, _shellView, _folderView, _enumerator, _item];
        public IntPtr AcquireBrowser() => _browser.Acquire();

        private int QueryActiveShellView(IntPtr self, out IntPtr view)
        {
            view = IntPtr.Zero;
            if (ActiveViewHr < 0) return ActiveViewHr;
            if (!SupportsFolderView2) _shellView.Forget(ExplorerComInterop.IidFolderView2);
            view = _shellView.Acquire();
            return 0;
        }

        private int ItemCount(IntPtr self, uint flags, out int count)
        {
            count = ReportedCount ?? Paths.Length;
            return ItemCountHr;
        }

        private int Items(IntPtr self, uint flags, ref Guid iid, out IntPtr result)
        {
            ItemsFlags = flags;
            ItemsIid = iid;
            result = IntPtr.Zero;
            if (ItemsHr < 0) return ItemsHr;
            _enumPosition = 0;
            result = _enumerator.Acquire();
            return 0;
        }

        private int GetGroupBy(IntPtr self, out PROPERTYKEY key, out int ascending)
        {
            key = GroupKey;
            ascending = 1;
            return GroupHr;
        }

        private int GetSortColumnCount(IntPtr self, out int count)
        {
            count = SortCount;
            return SortCountHr;
        }

        private int GetSortColumns(IntPtr self, IntPtr columns, int count)
        {
            var stride = Marshal.SizeOf<SORTCOLUMN>();
            for (var i = 0; i < Math.Min(count, Sorts.Length); i++) Marshal.StructureToPtr(Sorts[i], columns + i * stride, fDeleteOld: false);
            return SortsHr;
        }

        private int GetItem(IntPtr self, int index, ref Guid iid, out IntPtr item)
        {
            ItemsRequested.Add(index);
            OnGetItem?.Invoke(index);
            item = IntPtr.Zero;
            if (index == GetItemFailsAt) return EFail;
            _currentIndex = index;
            item = _item.Acquire(); // one shared object (one vtable), so the production code's per-vtable delegate cache is exercised
            return 0;
        }

        private int GetDisplayName(IntPtr self, uint kind, out IntPtr name)
        {
            name = IntPtr.Zero;
            if (DisplayNameHr < 0) return DisplayNameHr;
            name = Marshal.StringToCoTaskMemUni(Paths[_currentIndex]);
            return 0;
        }

        private int EnumNext(IntPtr self, uint count, IntPtr items, out uint fetched)
        {
            EnumNextCalls++;
            fetched = 0;
            if (EnumNextHr < 0) return EnumNextHr;
            var available = Math.Max(0, Paths.Length - EnumHidesLast - _enumPosition);
            var n = (int)Math.Min(Math.Min(count, (uint)EnumChunk), available);
            for (var i = 0; i < n; i++)
            {
                // A child PIDL (the item's last ID) in its own CoTaskMem block, like a real IEnumIDList hands out.
                var hr = ExplorerComInterop.SHParseDisplayName(Paths[_enumPosition + i], IntPtr.Zero, out var absolute, 0, out _);
                if (hr < 0) return hr; // never throw across the native boundary
                var child = ILClone(ILFindLastID(absolute));
                Marshal.FreeCoTaskMem(absolute);
                Marshal.WriteIntPtr(items, i * IntPtr.Size, child);
            }
            _enumPosition += n;
            fetched = (uint)n;
            return n == count || n > 0 && available > n ? 0 : SFalse; // S_OK while more follow, S_FALSE at the end
        }

        public void Dispose()
        {
            foreach (var obj in AllObjects) obj.Dispose();
        }
    }

    /// <summary>The managed Explorer window: a real COM callable wrapper that answers IServiceProvider like a shell browser window.</summary>
    [ComVisible(true), Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFakeServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [ComVisible(true)]
    public sealed class FakeExplorerWindow : IFakeServiceProvider
    {
        internal Func<IntPtr>? Browser { get; set; }
        internal int ServiceHr { get; set; }
        public string LocationURL { get; set; } = string.Empty;
        public Guid LastService { get; private set; }
        public Guid LastInterface { get; private set; }

        public int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject)
        {
            LastService = guidService;
            LastInterface = riid;
            ppvObject = IntPtr.Zero;
            if (ServiceHr < 0) return ServiceHr;
            if (guidService != ExplorerComInterop.SidTopLevelBrowser || riid != ExplorerComInterop.IidShellBrowser) return ENoInterface;
            ppvObject = Browser!();
            return 0;
        }
    }

    private View NewView(params string[] fileNames)
    {
        var view = new View(_root, MakeFiles(fileNames));
        _disposables.Add(view);
        return view;
    }

    private static FakeExplorerWindow WindowFor(View view, string? location = null)
        => new() { Browser = view.AcquireBrowser, LocationURL = location ?? new Uri(view.Folder + @"\").AbsoluteUri };

    private sealed class SyncProgress : IProgress<ExplorerQueryProgress>
    {
        public List<ExplorerQueryProgress> Reports { get; } = [];
        public void Report(ExplorerQueryProgress value) => Reports.Add(value);
    }

    private static void AssertNothingLeaked(View view)
        => Assert.All(view.AllObjects, o => Assert.Equal(0, o.Refs)); // every interface pointer the code was handed was released

    private static SORTCOLUMN Sort(Guid set, uint id, int direction) => new() { propkey = new PROPERTYKEY { fmtid = set, pid = id }, direction = direction };

    // ---- the happy paths ----

    [Fact]
    public void BatchedRead_ReturnsThePathsInViewOrderWithSortAndGrouping()
    {
        var view = NewView("c.jpg", "a.jpg", "b.jpg"); // view order, not alphabetical
        var nameKey = Guid.NewGuid();
        view.SortCount = 2;
        view.Sorts = [Sort(nameKey, 14, 1), Sort(Guid.Empty, 7, -1)];
        var progress = new SyncProgress();

        var snapshot = _service.TryReadNativeView(WindowFor(view), _root, progress, 16, CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Null(snapshot.Reason);
        Assert.Equal(_root, snapshot.Folder);
        Assert.Equal(view.Paths, snapshot.OrderedPaths);
        Assert.Equal(ExplorerGroupState.None, snapshot.GroupState);
        Assert.Equal(
            [new ExplorerSortColumn(nameKey, 14, ExplorerSortDirection.Ascending), new ExplorerSortColumn(Guid.Empty, 7, ExplorerSortDirection.Descending)],
            snapshot.SortColumns);
        Assert.Equal([new ExplorerQueryProgress(3, 3, 1)], progress.Reports); // one progress report for the whole batched read
        Assert.Empty(view.ItemsRequested); // no per-item GetItem call was needed
        Assert.Equal(ExplorerComInterop.SvgioAllView | ExplorerComInterop.SvgioFlagViewOrder, view.ItemsFlags);
        Assert.Equal(ExplorerComInterop.IidEnumIdList, view.ItemsIid);
        AssertNothingLeaked(view);
    }

    [Fact]
    public void BatchedRead_EnumeratorHandsOutItemsInSeveralCalls_CollectsAllOfThem()
    {
        var view = NewView("a.jpg", "b.jpg", "c.jpg");
        view.EnumChunk = 1;

        var snapshot = _service.TryReadNativeView(WindowFor(view), _root, null, 16, CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Equal(view.Paths, snapshot.OrderedPaths);
        Assert.True(view.EnumNextCalls >= 3);
        AssertNothingLeaked(view);
    }

    [Fact]
    public void BatchedRead_GroupedView_ReportsActiveGrouping()
    {
        var view = NewView("a.jpg");
        view.GroupKey = new PROPERTYKEY { fmtid = Guid.NewGuid(), pid = 3 };

        var snapshot = _service.TryReadNativeView(WindowFor(view), _root, null, 16, CancellationToken.None);

        Assert.Equal(ExplorerGroupState.Active, snapshot.GroupState);
        Assert.Empty(snapshot.SortColumns); // the view reported no sort columns: the genuine "no sort" answer
        AssertNothingLeaked(view);
    }

    // ---- fallback to the per-item read ----

    [Fact]
    public void PerItemRead_WhenTheEnumeratorCannotBeOpened_ReadsEveryItemOneByOne()
    {
        var view = NewView("c.jpg", "a.jpg", "b.jpg");
        view.ItemsHr = EFail;
        view.SortCount = 1;
        view.Sorts = [Sort(Guid.Empty, 9, -1)];
        view.GroupKey = new PROPERTYKEY { fmtid = Guid.Empty, pid = 5 };
        var progress = new SyncProgress();

        var snapshot = _service.TryReadNativeView(WindowFor(view), _root, progress, 2, CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Equal(view.Paths, snapshot.OrderedPaths);
        Assert.Equal([0, 1, 2], view.ItemsRequested);
        Assert.Equal([new ExplorerSortColumn(Guid.Empty, 9, ExplorerSortDirection.Descending)], snapshot.SortColumns);
        Assert.Equal(ExplorerGroupState.Active, snapshot.GroupState);
        // 1 call for the item count, then GetItem + GetDisplayName per item.
        Assert.Equal([new ExplorerQueryProgress(1, 3, 3), new ExplorerQueryProgress(2, 3, 5), new ExplorerQueryProgress(3, 3, 7)], progress.Reports);
        AssertNothingLeaked(view);
    }

    [Fact]
    public void PerItemRead_WhenTheEnumeratorYieldsFewerItemsThanTheView_FallsBackAndIsComplete()
    {
        var view = NewView("a.jpg", "b.jpg", "c.jpg");
        view.EnumHidesLast = 1; // the batched read would return 2 of 3 items: never accepted as complete

        var snapshot = _service.TryReadNativeView(WindowFor(view), _root, null, int.MaxValue, CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Equal(view.Paths, snapshot.OrderedPaths);
        Assert.Equal([0, 1, 2], view.ItemsRequested);
        AssertNothingLeaked(view);
    }

    [Fact]
    public void PerItemRead_WhenTheEnumeratorFails_FallsBack()
    {
        var view = NewView("a.jpg", "b.jpg");
        view.EnumNextHr = EFail;

        var snapshot = _service.TryReadNativeView(WindowFor(view), _root, null, 16, CancellationToken.None);

        Assert.Equal(view.Paths, snapshot.OrderedPaths);
        Assert.Equal([0, 1], view.ItemsRequested);
        AssertNothingLeaked(view);
    }

    [Fact]
    public void PerItemRead_WhenTheFolderCannotBeParsedToAPidl_FallsBack()
    {
        var view = NewView("a.jpg", "b.jpg");

        var snapshot = _service.TryReadNativeView(WindowFor(view), Path.Combine(_root, "no-such-folder", "deeper"), null, 16, CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Equal(view.Paths, snapshot.OrderedPaths);
        Assert.Equal([0, 1], view.ItemsRequested);
        AssertNothingLeaked(view);
    }

    [Fact]
    public void PerItemRead_GetItemFails_ReportsTheFailingIndexAndStops()
    {
        var view = NewView("a.jpg", "b.jpg", "c.jpg");
        view.ItemsHr = EFail;
        view.GetItemFailsAt = 1;

        var snapshot = _service.TryReadNativeView(WindowFor(view), _root, null, 16, CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.NativeViewUnavailable, snapshot.Status);
        Assert.StartsWith(ExplorerReason.ComCallFailed + "|IFolderView2.GetItem(1)|0x80004005", snapshot.Reason, StringComparison.Ordinal);
        Assert.Empty(snapshot.OrderedPaths);
        Assert.Equal([0, 1], view.ItemsRequested);
        AssertNothingLeaked(view);
    }

    [Fact]
    public void PerItemRead_GetDisplayNameFails_ReportsItAndStops()
    {
        var view = NewView("a.jpg", "b.jpg");
        view.ItemsHr = EFail;
        view.DisplayNameHr = EFail;

        var snapshot = _service.TryReadNativeView(WindowFor(view), _root, null, 16, CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.NativeViewUnavailable, snapshot.Status);
        Assert.StartsWith(ExplorerReason.ComCallFailed + "|IShellItem.GetDisplayName(0)|0x80004005", snapshot.Reason, StringComparison.Ordinal);
        AssertNothingLeaked(view);
    }

    [Fact]
    public void PerItemRead_CancelledMidWay_ThrowsAndStillReleasesEverything()
    {
        var view = NewView("a.jpg", "b.jpg", "c.jpg");
        view.ItemsHr = EFail;
        using var cts = new CancellationTokenSource();
        view.OnGetItem = index => { if (index == 1) cts.Cancel(); };

        Assert.Throws<OperationCanceledException>(() => _service.TryReadNativeView(WindowFor(view), _root, null, 16, cts.Token));

        Assert.Equal([0, 1], view.ItemsRequested); // the loop head saw the cancellation before asking for item 2
        AssertNothingLeaked(view);
    }

    [Fact]
    public void BatchedRead_CancelledBeforeTheFirstBatch_Throws()
    {
        var view = NewView("a.jpg");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _service.TryReadNativeView(WindowFor(view), _root, null, 16, cts.Token));

        AssertNothingLeaked(view);
    }

    // ---- every COM step that can fail ----

    [Fact]
    public void Window_WithoutIServiceProvider_IsNativeViewUnavailable()
    {
        var snapshot = _service.TryReadNativeView(new object(), _root, null, 16, CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.NativeViewUnavailable, snapshot.Status);
        Assert.StartsWith(ExplorerReason.ComCallFailed + "|QueryInterface(IServiceProvider)|0x80004002", snapshot.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_BrowserServiceFails_ReportsQueryService()
    {
        var view = NewView("a.jpg");
        var window = WindowFor(view);
        window.ServiceHr = EFail;

        var snapshot = _service.TryReadNativeView(window, _root, null, 16, CancellationToken.None);

        Assert.StartsWith(ExplorerReason.ComCallFailed + "|QueryService(IShellBrowser)|0x80004005", snapshot.Reason, StringComparison.Ordinal);
        AssertNothingLeaked(view);
    }

    [Fact]
    public void Window_AsksTheTopLevelBrowserServiceForIShellBrowser()
    {
        var view = NewView("a.jpg");
        var window = WindowFor(view);

        _ = _service.TryReadNativeView(window, _root, null, 16, CancellationToken.None);

        Assert.Equal(ExplorerComInterop.SidTopLevelBrowser, window.LastService);
        Assert.Equal(ExplorerComInterop.IidShellBrowser, window.LastInterface);
    }

    [Fact]
    public void Window_ActiveShellViewFails_ReportsIt()
    {
        var view = NewView("a.jpg");
        view.ActiveViewHr = EFail;

        var snapshot = _service.TryReadNativeView(WindowFor(view), _root, null, 16, CancellationToken.None);

        Assert.StartsWith(ExplorerReason.ComCallFailed + "|QueryActiveShellView|0x80004005", snapshot.Reason, StringComparison.Ordinal);
        AssertNothingLeaked(view);
    }

    [Fact]
    public void Window_ViewWithoutIFolderView2_ReportsIt()
    {
        var view = NewView("a.jpg");
        view.SupportsFolderView2 = false;

        var snapshot = _service.TryReadNativeView(WindowFor(view), _root, null, 16, CancellationToken.None);

        Assert.StartsWith(ExplorerReason.ComCallFailed + "|QueryInterface(IFolderView2)|0x80004002", snapshot.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(EFail, 5)]
    public void Window_EmptyOrUncountableView_IsReportedAsEmptyView(int hr, int count)
    {
        var view = NewView("a.jpg");
        view.ItemCountHr = hr;
        view.ReportedCount = count;

        var snapshot = _service.TryReadNativeView(WindowFor(view), _root, null, 16, CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.NativeViewUnavailable, snapshot.Status);
        Assert.StartsWith(ExplorerReason.EmptyView + "|0x", snapshot.Reason, StringComparison.Ordinal);
        Assert.Empty(snapshot.OrderedPaths);
        AssertNothingLeaked(view);
    }

    // ---- ReadSortColumns / ReadGroupState ----

    private static IntPtr ViewPointer(View view)
    {
        // Resolve the folder-view object the way the production code reaches it: through the shell view's QueryInterface.
        var browser = view.AcquireBrowser();
        Assert.Equal(0, ExplorerNativeVtable.QueryActiveShellView(browser, out var shellView));
        var iid = ExplorerComInterop.IidFolderView2;
        Assert.Equal(0, Marshal.QueryInterface(shellView, in iid, out var folderView));
        Marshal.Release(shellView);
        Marshal.Release(browser);
        return folderView;
    }

    [Fact]
    public void ReadSortColumns_MapsDirections_AndFlagsUnknownOnes()
    {
        var view = NewView("a.jpg");
        var set = Guid.NewGuid();
        view.SortCount = 3;
        view.Sorts = [Sort(set, 1, 1), Sort(set, 2, -1), Sort(set, 3, 0)];
        var folderView = ViewPointer(view);

        var columns = ExplorerOrderService.ReadSortColumns(folderView);
        Marshal.Release(folderView);

        Assert.Equal(
            [new ExplorerSortColumn(set, 1, ExplorerSortDirection.Ascending), new ExplorerSortColumn(set, 2, ExplorerSortDirection.Descending), new ExplorerSortColumn(set, 3, ExplorerSortDirection.Unknown)],
            columns);
        AssertNothingLeaked(view);
    }

    [Theory]
    [InlineData(EFail, 2, 0, false)]  // the count cannot be read
    [InlineData(0, 33, 0, false)]     // more than 32 columns is nonsense, not a sort
    [InlineData(0, 2, EFail, false)]  // the columns cannot be read
    [InlineData(0, 0, 0, true)]       // genuinely not sorted
    public void ReadSortColumns_FailuresAreUnknown_AndNoSortIsEmpty(int countHr, int count, int sortsHr, bool expectEmpty)
    {
        var view = NewView("a.jpg");
        view.SortCountHr = countHr;
        view.SortCount = count;
        view.SortsHr = sortsHr;
        var folderView = ViewPointer(view);

        var columns = ExplorerOrderService.ReadSortColumns(folderView);
        Marshal.Release(folderView);

        if (expectEmpty) Assert.Empty(columns);
        else Assert.Same(ExplorerOrderService.UnknownSortColumns, columns);
        Assert.Equal(ExplorerSortDirection.Unknown, ExplorerOrderService.UnknownSortColumns[0].Direction);
    }

    [Theory]
    [InlineData(EFail, false, ExplorerGroupState.Unknown)]
    [InlineData(0, false, ExplorerGroupState.None)]
    [InlineData(0, true, ExplorerGroupState.Active)]
    public void ReadGroupState_DistinguishesNoGroupingFromAFailedQuery(int hr, bool grouped, ExplorerGroupState expected)
    {
        var view = NewView("a.jpg");
        view.GroupHr = hr;
        view.GroupKey = grouped ? new PROPERTYKEY { fmtid = Guid.NewGuid(), pid = 0 } : default;
        var folderView = ViewPointer(view);

        var state = ExplorerOrderService.ReadGroupState(folderView);
        Marshal.Release(folderView);

        Assert.Equal(expected, state);
    }

    // ---- the whole query through the service (shell access seam) ----

    private ExplorerOrderService ServiceWith(ExplorerOrderService.ShellAccess shell)
    {
        var service = new ExplorerOrderService(null, null, shell: shell);
        _disposables.Add(service);
        return service;
    }

    [Fact]
    public async Task Query_PicksTheWindowShowingTheFolderAndReadsItsView()
    {
        var view = NewView("b.jpg", "a.jpg");
        var other = NewView("x.jpg");
        var unrelated = WindowFor(other, "file:///Z:/nowhere/");
        var matching = WindowFor(view);
        var service = ServiceWith(new(() => true, () => (null, new List<object> { unrelated, matching })));

        var snapshot = await service.TryGetSnapshotAsync(_root, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Equal(view.Paths, snapshot.OrderedPaths);
        Assert.Empty(other.ItemsRequested);
        AssertNothingLeaked(view);
        AssertNothingLeaked(other); // the unrelated window was never even asked
    }

    [Fact]
    public async Task Query_NoWindowShowsTheFolder_IsNoMatchingWindow()
    {
        var view = NewView("a.jpg");
        var service = ServiceWith(new(() => true, () => (null, new List<object> { WindowFor(view, "file:///Z:/nowhere/") })));

        var snapshot = await service.TryGetSnapshotAsync(_root, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.NoMatchingWindow, snapshot.Status);
        Assert.Equal(ExplorerReason.Format(ExplorerReason.NoMatchingWindow, "1"), snapshot.Reason);
    }

    [Fact]
    public async Task Query_ShellAutomationMissing_IsNativeViewUnavailable()
    {
        var opened = false;
        var service = ServiceWith(new(() => false, () => { opened = true; return (null, new List<object>()); }));

        var snapshot = await service.TryGetSnapshotAsync(_root, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.NativeViewUnavailable, snapshot.Status);
        Assert.Equal(ExplorerReason.ShellUnavailable, snapshot.Reason);
        Assert.False(opened);
    }

    [Fact]
    public async Task Query_CancelledWhileTheQueryStarts_IsCanceledWithoutOpeningTheShell()
    {
        using var cts = new CancellationTokenSource();
        var opened = false;
        // The caller gives up after the pump thread started the query but before it reached the shell.
        var service = ServiceWith(new(() => { cts.Cancel(); return true; }, () => { opened = true; return (null, new List<object>()); }));

        var snapshot = await service.TryGetSnapshotAsync(_root, TimeSpan.FromSeconds(30), cts.Token);

        Assert.Equal(ExplorerOrderStatus.Canceled, snapshot.Status);
        Assert.False(opened);
    }

    [Fact]
    public async Task Query_OpeningTheShellThrows_IsReportedAsFailed()
    {
        var service = ServiceWith(new(() => true, () => throw new InvalidOperationException("no shell")));

        var snapshot = await service.TryGetSnapshotAsync(_root, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.Failed, snapshot.Status);
        Assert.Equal(ExplorerReason.Format(ExplorerReason.QueryFailed, nameof(InvalidOperationException)), snapshot.Reason);
    }

    // ---- pump + helpers ----

    [Fact]
    public async Task Pump_EnqueueAfterDispose_FailsWithObjectDisposed()
    {
        var pump = new ExplorerOrderService.StaThreadPump(NullLog.Instance);
        pump.Dispose();

        var task = pump.Enqueue(() => ExplorerOrderService.Unavailable(_root, ExplorerOrderStatus.Failed, "x")); // must not throw synchronously
        Assert.True(task.IsFaulted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
        pump.Dispose(); // idempotent
    }

    [Fact]
    public void Release_NullAndManagedObjects_AreNoOps()
    {
        ExplorerOrderService.Release(null);
        ExplorerOrderService.Release(new object()); // not a COM object: left to the GC
        ExplorerOrderService.Release(new List<int>());

        Assert.Equal(ExplorerOrderStatus.Canceled, ExplorerOrderService.Unavailable(_root, ExplorerOrderStatus.Canceled, ExplorerReason.Canceled).Status);
    }
}
