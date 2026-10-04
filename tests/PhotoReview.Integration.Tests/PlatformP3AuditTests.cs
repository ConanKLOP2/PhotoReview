using System.Reflection;
using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Platform.Windows;
using PhotoReview.Platform.Windows.Explorer;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Wave 2 platform P3 audit fixes: P-EXP-01 (failed Explorer metadata reads are Unknown, not "none"), P-COM-01 (the
/// IEnumVARIANT RCW is released deterministically; exercised on Scripting.Dictionary, never on the real Recycle Bin or
/// Explorer) and P-DISP-01 (the vblank clock logs why it fell back to DWM timing).
/// </summary>
public sealed class PlatformP3AuditTests
{
    private const int SOk = 0;
    private const int EFail = unchecked((int)0x80004005);

    // ---- P-EXP-01: a fake IFolderView2 (native vtable in unmanaged memory, nothing from Explorer) ----

    // UnmanagedCallersOnly function pointers (not delegates): the production code turns a vtable slot into its own private
    // delegate type, which fails for a pointer created from a differently typed managed delegate. The scenario lives in the
    // fake object's memory, right behind the vtable pointer.
    private const int GroupHrOffset = 8, GroupKeySetOffset = 12, CountHrOffset = 16, CountOffset = 20, SortsHrOffset = 24, DirectionOffset = 28;
    private const int FakeObjectSize = 32;

    private sealed class FakeFolderView : IDisposable
    {
        private readonly IntPtr _vtable;
        public IntPtr Self { get; }

        public unsafe FakeFolderView(int groupHr, bool groupKeySet, int countHr, int count, int sortsHr, int direction)
        {
            _vtable = Marshal.AllocHGlobal(40 * IntPtr.Size);
            for (var i = 0; i < 40; i++) Marshal.WriteIntPtr(_vtable, i * IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(_vtable, 18 * IntPtr.Size, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, PROPERTYKEY*, int*, int>)&GetGroupBy);
            Marshal.WriteIntPtr(_vtable, 26 * IntPtr.Size, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, int>)&GetSortColumnCount);
            Marshal.WriteIntPtr(_vtable, 28 * IntPtr.Size, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, SORTCOLUMN*, int, int>)&GetSortColumns);
            Self = Marshal.AllocHGlobal(FakeObjectSize);
            Marshal.WriteIntPtr(Self, _vtable);
            Marshal.WriteInt32(Self, GroupHrOffset, groupHr);
            Marshal.WriteInt32(Self, GroupKeySetOffset, groupKeySet ? 1 : 0);
            Marshal.WriteInt32(Self, CountHrOffset, countHr);
            Marshal.WriteInt32(Self, CountOffset, count);
            Marshal.WriteInt32(Self, SortsHrOffset, sortsHr);
            Marshal.WriteInt32(Self, DirectionOffset, direction);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static unsafe int GetGroupBy(IntPtr self, PROPERTYKEY* key, int* ascending)
        {
            *key = Marshal.ReadInt32(self, GroupKeySetOffset) != 0 ? new PROPERTYKEY { fmtid = SortSet, pid = 10 } : default;
            *ascending = 1;
            return Marshal.ReadInt32(self, GroupHrOffset);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static unsafe int GetSortColumnCount(IntPtr self, int* count)
        {
            *count = Marshal.ReadInt32(self, CountOffset);
            return Marshal.ReadInt32(self, CountHrOffset);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static unsafe int GetSortColumns(IntPtr self, SORTCOLUMN* columns, int count)
        {
            for (var i = 0; i < count; i++)
                columns[i] = new SORTCOLUMN { propkey = new PROPERTYKEY { fmtid = SortSet, pid = 10 }, direction = Marshal.ReadInt32(self, DirectionOffset) };
            return Marshal.ReadInt32(self, SortsHrOffset);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Self);
            Marshal.FreeHGlobal(_vtable);
        }
    }

    private static readonly Guid SortSet = new("B725F130-47EF-101A-A5F1-02608C9EEBAC");

    private static FakeFolderView View(int groupHr, bool groupKeySet, int countHr, int count, int sortsHr, int direction = 1)
        => new(groupHr, groupKeySet, countHr, count, sortsHr, direction);

    [Fact]
    public void ReadGroupState_FailedGetGroupBy_IsUnknown()
    {
        using var view = View(EFail, groupKeySet: true, SOk, 0, SOk);

        Assert.Equal(ExplorerGroupState.Unknown, ExplorerOrderService.ReadGroupState(view.Self));
    }

    [Fact]
    public void ReadGroupState_EmptyKey_IsNoneAndSetKey_IsActive()
    {
        using var none = View(SOk, groupKeySet: false, SOk, 0, SOk);
        using var active = View(SOk, groupKeySet: true, SOk, 0, SOk);

        Assert.Equal(ExplorerGroupState.None, ExplorerOrderService.ReadGroupState(none.Self));
        Assert.Equal(ExplorerGroupState.Active, ExplorerOrderService.ReadGroupState(active.Self));
    }

    [Fact]
    public void ReadSortColumns_FailedCountOrFailedColumns_IsTheUnknownMarkerNotEmpty()
    {
        using var failedCount = View(SOk, false, EFail, 0, SOk);
        using var failedColumns = View(SOk, false, SOk, 2, EFail);

        var a = ExplorerOrderService.ReadSortColumns(failedCount.Self);
        var b = ExplorerOrderService.ReadSortColumns(failedColumns.Self);

        Assert.Equal(ExplorerSortDirection.Unknown, Assert.Single(a).Direction);
        Assert.Equal(ExplorerSortDirection.Unknown, Assert.Single(b).Direction);
    }

    [Fact]
    public void ReadSortColumns_GenuineAnswers_AreEmptyOrTheColumns()
    {
        using var noSort = View(SOk, false, SOk, 0, SOk);
        using var twoSorts = View(SOk, false, SOk, 2, SOk, direction: -1);

        var columns = ExplorerOrderService.ReadSortColumns(twoSorts.Self);

        Assert.Empty(ExplorerOrderService.ReadSortColumns(noSort.Self));
        Assert.Equal(2, columns.Length);
        Assert.All(columns, c => Assert.Equal(ExplorerSortDirection.Descending, c.Direction));
    }

    // ---- P-COM-01: ComEnumeration on a harmless automation object ----

    private static object NewDictionary(params string[] keys)
    {
        var type = Type.GetTypeFromProgID("Scripting.Dictionary") ?? throw new InvalidOperationException("Scripting.Dictionary is not registered");
        var dictionary = Activator.CreateInstance(type)!;
        foreach (var key in keys)
            type.InvokeMember("Add", BindingFlags.InvokeMethod, null, dictionary, [key, 1], System.Globalization.CultureInfo.InvariantCulture);
        return dictionary;
    }

    [Fact]
    public void Enumerate_ComCollection_YieldsItemsAndReleasesTheEnumeratorWhenDone()
    {
        var dictionary = NewDictionary("a", "b");
        object? enumerator = null;
        try
        {
            var items = ComEnumeration.Enumerate(dictionary, e => enumerator = e).Select(o => (string)o).ToList();

            Assert.Equal(["a", "b"], items);
            Assert.NotNull(enumerator);
            Assert.Throws<InvalidComObjectException>(() => ((ComEnumeration.IEnumVariant)enumerator!).Next(1, new object[1], out _));
        }
        finally { ComEnumeration.Release(dictionary); }
    }

    [Fact]
    public void Enumerate_AbandonedEarly_StillReleasesTheEnumerator()
    {
        var dictionary = NewDictionary("a", "b", "c");
        object? enumerator = null;
        try
        {
            foreach (var item in ComEnumeration.Enumerate(dictionary, e => enumerator = e))
            {
                Assert.Equal("a", (string)item);
                break;
            }

            Assert.NotNull(enumerator);
            Assert.Throws<InvalidComObjectException>(() => ((ComEnumeration.IEnumVariant)enumerator!).Next(1, new object[1], out _));
        }
        finally { ComEnumeration.Release(dictionary); }
    }

    [Fact]
    public void Enumerate_NonComCollection_FallsBackToPlainEnumeration()
    {
        var opened = false;

        var items = ComEnumeration.Enumerate(new List<object> { 1, 2 }, _ => opened = true).ToList();

        Assert.Equal([1, 2], items);
        Assert.False(opened);
    }

    // ---- P-DISP-01 ----

    private sealed class RecordingLog : ILog
    {
        public List<string> Warnings { get; } = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
    }

    [Fact]
    public void MarkFailed_LogsOneWarningWithTheMonitorAndTheFailingStep()
    {
        var log = new RecordingLog();
        var clock = new WindowsDisplayClock(log);

        clock.MarkFailed(new IntPtr(0x1A2B), "D3DKMTWaitForVerticalBlankEvent returned 0xC0000001");

        var warning = Assert.Single(log.Warnings);
        Assert.Contains("0x1A2B", warning, StringComparison.Ordinal);
        Assert.Contains("D3DKMTWaitForVerticalBlankEvent returned 0xC0000001", warning, StringComparison.Ordinal);
    }
}
