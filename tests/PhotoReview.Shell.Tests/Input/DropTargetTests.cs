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
    [Fact]
    public void Files_ReadsAWideHDropList_InOrder()
    {
        var data = new FakeDropData([@"C:\a\one.jpg", @"D:\b b\two.png", @"E:\3.jpg"]);
        Assert.True(DropData.HasFiles(data));
        Assert.Equal([@"C:\a\one.jpg", @"D:\b b\two.png", @"E:\3.jpg"], DropData.Files(data));
    }

    [Fact]
    public void Files_ReadsAnAnsiHDropList()
    {
        var data = new FakeDropData([@"C:\a\one.jpg", @"C:\two.jpg"], wide: false);
        Assert.Equal([@"C:\a\one.jpg", @"C:\two.jpg"], DropData.Files(data));
    }

    [Fact]
    public void Files_EmptyListFormatMissingOrGetDataFailing_AreEmpty()
    {
        Assert.Empty(DropData.Files(new FakeDropData([])));
        Assert.False(DropData.HasFiles(new FakeDropData(null)));
        Assert.Empty(DropData.Files(new FakeDropData(null)));
        Assert.Empty(DropData.Files(new FakeDropData([@"C:\x.jpg"], getDataResult: unchecked((int)0x80004005))));
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

        target.DragEnter(new FakeDropData([@"C:\a.jpg"]), 0, default, ref effect);
        Assert.Equal(DropEffect.Copy, effect);
        effect = DropEffect.None;
        target.DragOver(0, default, ref effect);
        Assert.Equal(DropEffect.Copy, effect);

        target.DragLeave();
        effect = DropEffect.Copy;
        target.DragOver(0, default, ref effect); // sau DragLeave: không còn gì được kéo
        Assert.Equal(DropEffect.None, effect);

        target.DragEnter(new FakeDropData(null), 0, default, ref effect);
        Assert.Equal(DropEffect.None, effect);
        effect = DropEffect.Copy;
        target.DragOver(0, default, ref effect);
        Assert.Equal(DropEffect.None, effect);
    }

    [Fact]
    public void Drop_OpensOnlyTheFirstFile_WithCopyEffect()
    {
        var sink = new Sink();
        var target = Create(sink);
        var effect = DropEffect.None;

        var hr = target.Drop(new FakeDropData([@"C:\first.jpg", @"C:\second.jpg"]), 0, default, ref effect);

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

        target.Drop(new FakeDropData(null), 0, default, ref effect);

        Assert.Equal(DropEffect.None, effect);
        Assert.Empty(sink.Opened);
        Assert.Empty(sink.Failures);
    }

    [Fact]
    public void Drop_WhenOpeningFails_ReportsTheFailure_NotACrash()
    {
        var sink = new Sink { Behaviour = _ => Task.FromException(new IOException("gone")) };
        var target = Create(sink);
        var effect = DropEffect.None;

        target.Drop(new FakeDropData([@"C:\x.jpg"]), 0, default, ref effect); // OpenFirstAsync chạy tại chỗ tới await đầu

        Assert.Single(sink.Failures);
        Assert.IsType<IOException>(sink.Failures[0]);
    }

    [Fact]
    public void Drop_Cancellation_IsNotAFailure()
    {
        var sink = new Sink { Behaviour = _ => Task.FromCanceled(new CancellationToken(true)) };
        var target = Create(sink);
        var effect = DropEffect.None;

        target.Drop(new FakeDropData([@"C:\x.jpg"]), 0, default, ref effect);

        Assert.Empty(sink.Failures);
    }

    [Fact]
    public void Drop_WhenReadingTheDataObjectThrows_IsReported()
    {
        var sink = new Sink();
        var target = new ShellDropTarget(sink.Open, sink.Failures.Add, files: _ => throw new InvalidOperationException("rpc"));
        var effect = DropEffect.None;

        target.Drop(new FakeDropData([@"C:\x.jpg"]), 0, default, ref effect);

        Assert.Single(sink.Failures);
        Assert.Equal(DropEffect.None, effect);
        Assert.Empty(sink.Opened);
    }

    [Fact]
    public void TheVtableMarshalling_CarriesPointByValue_AndTheEffectByRef()
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
                var data = new FakeDropData([@"C:\drag\a.jpg"]);
                var effect = DropEffect.None;
                Assert.Equal(0, proxy.DragEnter(data, 0, new PointL { X = 5, Y = 6 }, ref effect));
                Assert.Equal(DropEffect.Copy, effect);
                effect = DropEffect.None;
                Assert.Equal(0, proxy.Drop(data, 0, new PointL { X = 5, Y = 6 }, ref effect));
                Assert.Equal(DropEffect.Copy, effect);
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
        var handle = FakeDropData.BuildDropFiles([@"C:\x", @"C:\y"]);
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
