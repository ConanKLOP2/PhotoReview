using System.Runtime.InteropServices;

namespace PhotoReview.Platform.Windows.Explorer;

internal static class ExplorerComInterop
{
    internal static Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    internal static Guid IidServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    internal static Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    internal static Guid IidFolderView2 = new("1AF3A467-214F-4298-908E-06B03E0B39F9");
    internal static Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    internal static Guid IidEnumIdList = new("000214F2-0000-0000-C000-000000000046");
    internal const uint SvgioAllView = 2;
    internal const uint SvgioFlagViewOrder = 0x80000000;
    internal const uint SigdnFileSystemPath = 0x80058000;
    internal const int SFalse = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern IntPtr ILCombine(IntPtr parent, IntPtr child);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);
}

internal static class ExplorerNativeVtable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryServiceDelegate(IntPtr self, ref Guid service, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryActiveShellViewDelegate(IntPtr self, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ItemCountDelegate(IntPtr self, uint flags, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetItemDelegate(IntPtr self, int index, ref Guid iid, out IntPtr item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetDisplayNameDelegate(IntPtr self, uint kind, out IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSortColumnCountDelegate(IntPtr self, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSortColumnsDelegate(IntPtr self, [Out] SORTCOLUMN[] columns, int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetGroupByDelegate(IntPtr self, out PROPERTYKEY key, [MarshalAs(UnmanagedType.Bool)] out bool ascending);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ItemsDelegate(IntPtr self, uint flags, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumNextDelegate(IntPtr self, uint count, [Out] IntPtr[] items, out uint fetched);

    /// <summary>IFolderView::Items (slot 8): the view's items as one object (here an IEnumIDList).</summary>
    internal static int Items(IntPtr self, uint flags, ref Guid iid, out IntPtr result) => Get<ItemsDelegate>(self, 8)(self, flags, ref iid, out result);

    /// <summary>IEnumIDList::Next (slot 3): up to <paramref name="count"/> child PIDLs per (cross-process) call.</summary>
    internal static int EnumNext(IntPtr self, uint count, IntPtr[] items, out uint fetched) => Get<EnumNextDelegate>(self, 3)(self, count, items, out fetched);

    internal static int QueryService(IntPtr self, ref Guid service, ref Guid iid, out IntPtr result) => Get<QueryServiceDelegate>(self, 3)(self, ref service, ref iid, out result);
    internal static int QueryActiveShellView(IntPtr self, out IntPtr view) => Get<QueryActiveShellViewDelegate>(self, 15)(self, out view);
    internal static int ItemCount(IntPtr self, uint flags, out int count) => Get<ItemCountDelegate>(self, 7)(self, flags, out count);
    internal static int GetGroupBy(IntPtr self, out PROPERTYKEY key, out bool ascending) => Get<GetGroupByDelegate>(self, 18)(self, out key, out ascending);
    internal static int GetSortColumnCount(IntPtr self, out int count) => Get<GetSortColumnCountDelegate>(self, 26)(self, out count);
    internal static int GetSortColumns(IntPtr self, SORTCOLUMN[] columns, int count) => Get<GetSortColumnsDelegate>(self, 28)(self, columns, count);
    internal static int GetItem(IntPtr self, int index, ref Guid iid, out IntPtr item) => Get<GetItemDelegate>(self, 29)(self, index, ref iid, out item);
    internal static int GetDisplayName(IntPtr self, uint kind, out IntPtr name) => Get<GetDisplayNameDelegate>(self, 5)(self, kind, out name);
    internal static GetItemDelegate ResolveGetItem(IntPtr self) => Get<GetItemDelegate>(self, 29);
    internal static GetDisplayNameDelegate ResolveGetDisplayName(IntPtr self) => Get<GetDisplayNameDelegate>(self, 5);

    private static T Get<T>(IntPtr self, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(self);
        var method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }
}

[StructLayout(LayoutKind.Sequential)] internal struct PROPERTYKEY { internal Guid fmtid; internal uint pid; }
[StructLayout(LayoutKind.Sequential)] internal struct SORTCOLUMN { internal PROPERTYKEY propkey; internal int direction; }
