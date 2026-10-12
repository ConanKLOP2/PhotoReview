using System.Runtime.InteropServices;
using PhotoReview.Platform.Windows.Shell;

namespace PhotoReview.Platform.Windows.Explorer;

internal static partial class ExplorerComInterop
{
    internal static Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    internal static Guid IidServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    internal static Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    internal static Guid IidFolderView2 = new("1AF3A467-214F-4298-908E-06B03E0B39F9");
    internal static Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    internal static Guid IidEnumIdList = new("000214F2-0000-0000-C000-000000000046");
    /// <summary>CLSID_ShellWindows: the collection of open Explorer / Internet Explorer windows (what <c>Shell.Application.Windows()</c> returns).</summary>
    internal static Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    internal static Guid IidShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    internal static Guid IidWebBrowserApp = new("0002DF05-0000-0000-C000-000000000046");
    /// <summary>REGDB_E_CLASSNOTREG: the shell automation class is not registered (no Explorer shell).</summary>
    internal const int ClassNotRegistered = unchecked((int)0x80040154);
    internal const uint SvgioAllView = 2;
    internal const uint SvgioFlagViewOrder = 0x80000000;
    internal const uint SigdnFileSystemPath = 0x80058000;
    internal const int SFalse = 1;

    // WP-33 / L-AOT: source-generated P/Invoke (the same shell32 entry points the DllImport declarations had).
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint sfgaoIn, out uint sfgaoOut);

    [LibraryImport("shell32.dll")]
    internal static partial nint ILCombine(nint parent, nint child);

    [LibraryImport("shell32.dll")]
    internal static partial int SHGetNameFromIDList(nint pidl, uint sigdn, out nint name);
}

/// <summary>
/// One Explorer window. <see cref="Dispose"/> releases it (once). <see cref="LocationUrl"/> may throw <see cref="COMException"/>
/// when the window closed or is not a folder view (the selector skips such a window).
/// </summary>
internal interface IExplorerWindow : IDisposable
{
    string? LocationUrl { get; }

    /// <summary>A NEW reference to the window's IUnknown; the caller releases it with <see cref="Marshal.Release"/>.</summary>
    nint AcquireUnknown();
}

/// <summary>The set of open Explorer windows plus the object that owns it; <see cref="Dispose"/> releases the owner (not the windows).</summary>
internal interface IExplorerWindowSource : IDisposable
{
    /// <summary>Lazy walk; each window is released by the caller. A failure while advancing surfaces from <c>MoveNext</c>.</summary>
    IEnumerable<IExplorerWindow> Windows { get; }
}

/// <summary><c>CLSID_ShellWindows</c> through <c>IShellWindows</c> (get_Count / Item): typed vtable slots, no IDispatch, no reflection.</summary>
internal sealed class ShellWindowsSource : IExplorerWindowSource
{
    private nint _shellWindows;

    /// <summary>Takes ownership of one reference to the IShellWindows pointer.</summary>
    internal ShellWindowsSource(nint shellWindows) => _shellWindows = shellWindows;

    /// <summary>Creates the collection (out of process: explorer.exe). Throws <see cref="COMException"/>, <c>REGDB_E_CLASSNOTREG</c> when absent.</summary>
    internal static ShellWindowsSource Open()
    {
        var pointer = ShellCom.CreateInstance(ExplorerComInterop.ClsidShellWindows, ExplorerComInterop.IidShellWindows);
        return new ShellWindowsSource(pointer);
    }

    public IEnumerable<IExplorerWindow> Windows => Enumerate();

    private IEnumerable<IExplorerWindow> Enumerate()
    {
        var self = _shellWindows;
        ObjectDisposedException.ThrowIf(self == 0, typeof(ShellWindowsSource));
        ShellCom.Check(ExplorerNativeVtable.ShellWindowsCount(self, out var count));
        for (var index = 0; index < count; index++)
        {
            ShellCom.Check(ExplorerNativeVtable.ShellWindowsItem(self, index, out var dispatch));
            if (dispatch == 0) continue; // the window closed between Count and Item
            yield return new ShellWindow(dispatch);
        }
    }

    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref _shellWindows, 0);
        if (pointer != 0) Marshal.Release(pointer);
    }
}

/// <summary>An <c>IWebBrowser2</c> window object (an <c>IDispatch</c> pointer owned here).</summary>
internal sealed class ShellWindow : IExplorerWindow
{
    private nint _dispatch;

    internal ShellWindow(nint dispatch) => _dispatch = dispatch;

    public string? LocationUrl
    {
        get
        {
            var self = _dispatch;
            ObjectDisposedException.ThrowIf(self == 0, typeof(ShellWindow));
            var iid = ExplorerComInterop.IidWebBrowserApp;
            ShellCom.Check(Marshal.QueryInterface(self, in iid, out var app));
            try
            {
                ShellCom.Check(ExplorerNativeVtable.WebBrowserLocationUrl(app, out var bstr));
                if (bstr == 0) return null;
                try { return Marshal.PtrToStringBSTR(bstr); }
                finally { Marshal.FreeBSTR(bstr); }
            }
            finally { Marshal.Release(app); }
        }
    }

    public nint AcquireUnknown()
    {
        var self = _dispatch;
        ObjectDisposedException.ThrowIf(self == 0, typeof(ShellWindow));
        Marshal.AddRef(self);
        return self;
    }

    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref _dispatch, 0);
        if (pointer != 0) Marshal.Release(pointer);
    }
}

/// <summary>
/// vtable slots called through unmanaged function pointers (explicit slot numbers from the SDK headers; no
/// <c>Marshal.GetDelegateForFunctionPointer</c>, so AOT/trim safe). The objects are foreign-process shell objects reached through
/// <c>QueryService</c>/<c>QueryInterface</c> chains, so they stay raw pointers.
/// </summary>
internal static unsafe class ExplorerNativeVtable
{
    /// <summary>VARIANT by value (24 bytes on x64, 16 on x86 through the pointer-sized fields).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        public ushort Vt;
        public ushort Reserved1, Reserved2, Reserved3;
        public nint Data1;
        public nint Data2;
    }

    private const ushort VtI4 = 3;

    private static nint Slot(nint self, int slot)
    {
        var vtable = *(nint*)self;
        return *(nint*)(vtable + slot * sizeof(nint));
    }

    /// <summary>IShellWindows::get_Count (slot 7).</summary>
    internal static int ShellWindowsCount(nint self, out int count)
    {
        int value;
        var hr = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(self, 7))(self, &value);
        count = value;
        return hr;
    }

    /// <summary>IShellWindows::Item (slot 8) with a VT_I4 index: the window's IDispatch.</summary>
    internal static int ShellWindowsItem(nint self, int index, out nint dispatch)
    {
        nint value = 0;
        var variant = new Variant { Vt = VtI4, Data1 = index };
        var hr = ((delegate* unmanaged[Stdcall]<nint, Variant, nint*, int>)Slot(self, 8))(self, variant, &value);
        dispatch = value;
        return hr;
    }

    /// <summary>IWebBrowserApp::get_LocationURL (slot 30: IDispatch 0-6, IWebBrowser 7-31).</summary>
    internal static int WebBrowserLocationUrl(nint self, out nint bstr)
    {
        nint value = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(self, 30))(self, &value);
        bstr = value;
        return hr;
    }

    /// <summary>IServiceProvider::QueryService (slot 3).</summary>
    internal static int QueryService(nint self, ref Guid service, ref Guid iid, out nint result)
    {
        nint value = 0;
        int hr;
        fixed (Guid* servicePointer = &service)
        fixed (Guid* iidPointer = &iid)
            hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)Slot(self, 3))(self, servicePointer, iidPointer, &value);
        result = value;
        return hr;
    }

    /// <summary>IShellBrowser::QueryActiveShellView (slot 15).</summary>
    internal static int QueryActiveShellView(nint self, out nint view)
    {
        nint value = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(self, 15))(self, &value);
        view = value;
        return hr;
    }

    /// <summary>IFolderView::ItemCount (slot 7).</summary>
    internal static int ItemCount(nint self, uint flags, out int count)
    {
        int value = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, int*, int>)Slot(self, 7))(self, flags, &value);
        count = value;
        return hr;
    }

    /// <summary>IFolderView::Items (slot 8): the view's items as one object (here an IEnumIDList).</summary>
    internal static int Items(nint self, uint flags, ref Guid iid, out nint result)
    {
        nint value = 0;
        int hr;
        fixed (Guid* iidPointer = &iid)
            hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(self, 8))(self, flags, iidPointer, &value);
        result = value;
        return hr;
    }

    /// <summary>IEnumIDList::Next (slot 3): up to <paramref name="count"/> child PIDLs per (cross-process) call.</summary>
    internal static int EnumNext(nint self, uint count, nint[] items, out uint fetched)
    {
        uint value = 0;
        int hr;
        fixed (nint* itemsPointer = items)
            hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Slot(self, 3))(self, count, itemsPointer, &value);
        fetched = value;
        return hr;
    }

    /// <summary>IFolderView2::GetGroupBy (slot 18).</summary>
    internal static int GetGroupBy(nint self, out PROPERTYKEY key, out bool ascending)
    {
        PROPERTYKEY keyValue = default;
        int ascendingValue = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, PROPERTYKEY*, int*, int>)Slot(self, 18))(self, &keyValue, &ascendingValue);
        key = keyValue;
        ascending = ascendingValue != 0;
        return hr;
    }

    /// <summary>IFolderView2::GetSortColumnCount (slot 26).</summary>
    internal static int GetSortColumnCount(nint self, out int count)
    {
        int value = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(self, 26))(self, &value);
        count = value;
        return hr;
    }

    /// <summary>IFolderView2::GetSortColumns (slot 28).</summary>
    internal static int GetSortColumns(nint self, SORTCOLUMN[] columns, int count)
    {
        fixed (SORTCOLUMN* columnsPointer = columns)
            return ((delegate* unmanaged[Stdcall]<nint, SORTCOLUMN*, int, int>)Slot(self, 28))(self, columnsPointer, count);
    }

    /// <summary>IFolderView::GetItem (slot 29) as a callable address: resolved once per view, called with <see cref="GetItem"/>.</summary>
    internal static nint ResolveGetItem(nint self) => Slot(self, 29);

    internal static int GetItem(nint function, nint self, int index, ref Guid iid, out nint item)
    {
        nint value = 0;
        int hr;
        fixed (Guid* iidPointer = &iid)
            hr = ((delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)function)(self, index, iidPointer, &value);
        item = value;
        return hr;
    }

    /// <summary>IShellItem::GetDisplayName (slot 5) as a callable address (cached per distinct vtable by the caller).</summary>
    internal static nint ResolveGetDisplayName(nint self) => Slot(self, 5);

    internal static int GetDisplayName(nint function, nint self, uint kind, out nint name)
    {
        nint value = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)function)(self, kind, &value);
        name = value;
        return hr;
    }
}

[StructLayout(LayoutKind.Sequential)] internal struct PROPERTYKEY { internal Guid fmtid; internal uint pid; }
[StructLayout(LayoutKind.Sequential)] internal struct SORTCOLUMN { internal PROPERTYKEY propkey; internal int direction; }
