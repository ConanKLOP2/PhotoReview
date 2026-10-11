using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Interop.Shell;
using PhotoReview.Shell.Win32.Input;

namespace PhotoReview.Shell.Tests.Input;

/// <summary>
/// WP-22: kéo-thả file vào cửa sổ (<c>AllowDrop</c>/<c>Window_PreviewDragOver</c>/<c>Window_Drop</c>): đọc CF_HDROP từ
/// <c>IDataObject</c> giả (HGLOBAL thật), hiệu ứng Copy/None, thả = mở file ĐẦU TIÊN, lỗi mở được báo.
/// </summary>
public sealed partial class DropTargetTests
{
    // ---- DROPFILES ----

    private static nint BuildDropFiles(IReadOnlyList<string> files, bool wide = true)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((uint)20));   // pFiles
        bytes.AddRange(new byte[8]);                        // pt
        bytes.AddRange(BitConverter.GetBytes(0));           // fNC
        bytes.AddRange(BitConverter.GetBytes(wide ? 1 : 0)); // fWide
        var encoding = wide ? Encoding.Unicode : Encoding.Latin1;
        foreach (var file in files)
        {
            bytes.AddRange(encoding.GetBytes(file));
            bytes.AddRange(new byte[wide ? 2 : 1]);
        }

        bytes.AddRange(new byte[wide ? 2 : 1]); // NUL kép
        var handle = Kernel32.GlobalAlloc(WindowMessages.GmemMoveable | WindowMessages.GmemZeroInit, (nuint)bytes.Count);
        var target = Kernel32.GlobalLock(handle);
        Marshal.Copy(bytes.ToArray(), 0, target, bytes.Count);
        Kernel32.GlobalUnlock(handle);
        return handle;
    }

    [GeneratedComClass]
    private sealed unsafe partial class FakeDataObject(IReadOnlyList<string>? files, bool wide = true, int getDataResult = 0) : IDataObject
    {
        public int GetDataCalls { get; private set; }

        public int GetData(FormatEtc* format, StgMedium* medium)
        {
            GetDataCalls++;
            if (getDataResult != 0 || files is null || format->ClipboardFormat != ClipboardFormats.HDrop)
            {
                return getDataResult != 0 ? getDataResult : OleErrors.DvEFormatEtc;
            }

            medium->Medium = Tymed.HGlobal;
            medium->Handle = BuildDropFiles(files, wide);
            medium->ReleaseUnknown = 0; // ReleaseStgMedium -> GlobalFree
            return 0;
        }

        public int Reserved04GetDataHere(FormatEtc* format, StgMedium* medium) => OleErrors.DvEFormatEtc;

        public int QueryGetData(FormatEtc* format) =>
            files is not null && format->ClipboardFormat == ClipboardFormats.HDrop && format->Medium == Tymed.HGlobal ? 0 : OleErrors.DvEFormatEtc;

        public int Reserved06GetCanonicalFormatEtc(FormatEtc* a, FormatEtc* b) => OleErrors.DvEFormatEtc;

        public int SetData(FormatEtc* format, StgMedium* medium, int release) => OleErrors.DvEFormatEtc;

        public int Reserved08EnumFormatEtc(uint direction, out nint enumerator)
        {
            enumerator = 0;
            return OleErrors.DvEFormatEtc;
        }

        public int Reserved09DAdvise(FormatEtc* a, uint b, nint c, out uint d)
        {
            d = 0;
            return OleErrors.DvEFormatEtc;
        }

        public int Reserved10DUnadvise(uint a) => OleErrors.DvEFormatEtc;

        public int Reserved11EnumDAdvise(out nint a)
        {
            a = 0;
            return OleErrors.DvEFormatEtc;
        }
    }

    [Fact]
    public void Files_ReadsAWideHDropList_InOrder()
    {
        var data = new FakeDataObject([@"C:\a\one.jpg", @"D:\b b\two.png", @"E:\3.jpg"]);
        Assert.True(DropData.HasFiles(data));
        Assert.Equal([@"C:\a\one.jpg", @"D:\b b\two.png", @"E:\3.jpg"], DropData.Files(data));
    }

    [Fact]
    public void Files_ReadsAnAnsiHDropList()
    {
        var data = new FakeDataObject([@"C:\a\one.jpg", @"C:\two.jpg"], wide: false);
        Assert.Equal([@"C:\a\one.jpg", @"C:\two.jpg"], DropData.Files(data));
    }

    [Fact]
    public void Files_EmptyListFormatMissingOrGetDataFailing_AreEmpty()
    {
        Assert.Empty(DropData.Files(new FakeDataObject([])));
        Assert.False(DropData.HasFiles(new FakeDataObject(null)));
        Assert.Empty(DropData.Files(new FakeDataObject(null)));
        Assert.Empty(DropData.Files(new FakeDataObject([@"C:\x.jpg"], getDataResult: unchecked((int)0x80004005))));
    }

    // ---- IDropTarget ----

    private sealed class Sink
    {
        public List<string> Opened { get; } = [];

        public List<Exception> Failures { get; } = [];

        public Func<string, Task>? Behaviour { get; set; }

        public Task Open(string path)
        {
            Opened.Add(path);
            return Behaviour?.Invoke(path) ?? Task.CompletedTask;
        }
    }

    private static ShellDropTarget Create(Sink sink) => new(sink.Open, sink.Failures.Add);

    [Fact]
    public void DragEnterAndOver_ShowCopyWhenFilesAreCarried_NoneOtherwise()
    {
        var target = Create(new Sink());
        var effect = DropEffect.Move;

        target.DragEnter(new FakeDataObject([@"C:\a.jpg"]), 0, default, ref effect);
        Assert.Equal(DropEffect.Copy, effect);
        effect = DropEffect.None;
        target.DragOver(0, default, ref effect);
        Assert.Equal(DropEffect.Copy, effect);

        target.DragLeave();
        effect = DropEffect.Copy;
        target.DragOver(0, default, ref effect); // sau DragLeave: không còn gì được kéo
        Assert.Equal(DropEffect.None, effect);

        target.DragEnter(new FakeDataObject(null), 0, default, ref effect);
        Assert.Equal(DropEffect.None, effect);
        effect = DropEffect.Copy;
        target.DragOver(0, default, ref effect);
        Assert.Equal(DropEffect.None, effect);
    }

    [Fact]
    public async Task Drop_OpensOnlyTheFirstFile_WithCopyEffect()
    {
        var sink = new Sink();
        var target = Create(sink);
        var effect = DropEffect.None;

        var hr = target.Drop(new FakeDataObject([@"C:\first.jpg", @"C:\second.jpg"]), 0, default, ref effect);
        await Task.Yield();

        Assert.Equal(0, hr);
        Assert.Equal(DropEffect.Copy, effect);
        Assert.Equal([@"C:\first.jpg"], sink.Opened);
    }

    [Fact]
    public void Drop_WithoutFiles_OpensNothing_AndShowsNone()
    {
        var sink = new Sink();
        var target = Create(sink);
        var effect = DropEffect.Copy;

        target.Drop(new FakeDataObject(null), 0, default, ref effect);

        Assert.Equal(DropEffect.None, effect);
        Assert.Empty(sink.Opened);
        Assert.Empty(sink.Failures);
    }

    [Fact]
    public async Task Drop_WhenOpeningFails_ReportsTheFailure_NotACrash()
    {
        var sink = new Sink { Behaviour = _ => Task.FromException(new IOException("gone")) };
        var target = Create(sink);
        var effect = DropEffect.None;

        target.Drop(new FakeDataObject([@"C:\x.jpg"]), 0, default, ref effect);
        await Task.Yield();
        await Task.Delay(1); // OpenFirstAsync đã chạy inline; chỉ để chắc chắn continuation

        Assert.Single(sink.Failures);
        Assert.IsType<IOException>(sink.Failures[0]);
    }

    [Fact]
    public async Task Drop_Cancellation_IsNotAFailure()
    {
        var sink = new Sink { Behaviour = _ => Task.FromCanceled(new CancellationToken(true)) };
        var target = Create(sink);
        var effect = DropEffect.None;

        target.Drop(new FakeDataObject([@"C:\x.jpg"]), 0, default, ref effect);
        await Task.Yield();

        Assert.Empty(sink.Failures);
    }

    [Fact]
    public void Drop_WhenReadingTheDataObjectThrows_IsReported()
    {
        var sink = new Sink();
        var target = new ShellDropTarget(sink.Open, sink.Failures.Add, files: _ => throw new InvalidOperationException("rpc"));
        var effect = DropEffect.None;

        target.Drop(new FakeDataObject([@"C:\x.jpg"]), 0, default, ref effect);

        Assert.Single(sink.Failures);
        Assert.Equal(DropEffect.None, effect);
        Assert.Empty(sink.Opened);
    }

    [Fact]
    public async Task TheVtableMarshalling_CarriesPointByValue_AndTheEffectByRef()
    {
        // Gọi qua COM thật (ConvertToUnmanaged -> con trỏ interface -> RCW sinh): chữ ký IDropTarget của WP-13b phải đúng slot/ABI.
        var sink = new Sink();
        var target = Create(sink);
        var wrappers = new StrategyBasedComWrappers();
        var unknown = wrappers.GetOrCreateComInterfaceForObject(target, CreateComInterfaceFlags.None);
        try
        {
            var iid = typeof(IDropTarget).GUID;
            Assert.Equal(0, Marshal.QueryInterface(unknown, in iid, out var raw));
            try
            {
                var proxy = (IDropTarget)wrappers.GetOrCreateObjectForComInstance(raw, CreateObjectFlags.UniqueInstance);
                var data = new FakeDataObject([@"C:\drag\a.jpg"]);
                var effect = DropEffect.None;
                Assert.Equal(0, proxy.DragEnter(data, 0, new PointL { X = 5, Y = 6 }, ref effect));
                Assert.Equal(DropEffect.Copy, effect);
                effect = DropEffect.None;
                Assert.Equal(0, proxy.Drop(data, 0, new PointL { X = 5, Y = 6 }, ref effect));
                Assert.Equal(DropEffect.Copy, effect);
                await Task.Yield();
                Assert.Equal([@"C:\drag\a.jpg"], sink.Opened);
            }
            finally
            {
                Marshal.Release(raw);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [Fact]
    public void Constructor_RejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new ShellDropTarget(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new ShellDropTarget(_ => Task.CompletedTask, null!));
    }

    [Fact]
    public unsafe void Parse_StopsAtTheDoubleNul_AndHonoursTheListOffset()
    {
        var handle = BuildDropFiles([@"C:\x", @"C:\y"]);
        var block = (byte*)Kernel32.GlobalLock(handle);
        try
        {
            Assert.Equal([@"C:\x", @"C:\y"], DropData.Parse(block));
        }
        finally
        {
            Kernel32.GlobalUnlock(handle);
            Kernel32.GlobalFree(handle);
        }
    }
}
