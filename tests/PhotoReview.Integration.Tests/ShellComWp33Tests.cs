using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PhotoReview.Platform.Windows;
using PhotoReview.Platform.Windows.Explorer;
using PhotoReview.Platform.Windows.Shell;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// WP-33: the shell COM paths (Recycle Bin send / restore, Explorer window list) declared with [GeneratedComInterface] and
/// unmanaged function pointers instead of Shell.Application / dynamic / ComImport. Everything here runs on fakes (managed
/// objects for the typed interfaces, hand-built native vtables for raw pointers) or on READ-ONLY queries of the shell: no test
/// here sends a file to, restores from, deletes from or empties the user's real Recycle Bin (the Native end-to-end tests in
/// App.Tests only touch their own temp items).
/// </summary>
public sealed class ShellComWp33Tests
{
    private const int HrFail = unchecked((int)0x80004005);
    private const int ENoInterface = unchecked((int)0x80004002);

    // ------------------------------------------------------------------------------------------------------------
    // Fakes
    // ------------------------------------------------------------------------------------------------------------

    private sealed class FakeShellItem(string path) : IShellItem
    {
        public string Path { get; } = path;
        public int BindToHandler(nint bindContext, in Guid handler, in Guid iid, out nint result) { result = 0; return 0; }
        public int GetParent(out nint parent) { parent = 0; return 0; }
        public int GetDisplayName(uint sigdn, out nint name) { name = 0; return 0; }
        public int GetAttributes(uint mask, out uint attributes) { attributes = 0; return 0; }
        public int Compare(IShellItem other, uint hint, out int order) { order = 0; return 0; }
    }

    private sealed class FakeFileOperation : IFileOperation
    {
        public List<string> Calls { get; } = [];
        public uint Flags { get; private set; }
        public IShellItem? Deleted { get; private set; }
        public nint DeleteSink { get; private set; } = -1;
        public int SetFlagsHr { get; set; }
        public int DeleteHr { get; set; }
        public int PerformHr { get; set; }

        public int SetOperationFlags(uint flags) { Calls.Add("flags"); Flags = flags; return SetFlagsHr; }
        public int DeleteItem(IShellItem item, nint sink) { Calls.Add("delete"); Deleted = item; DeleteSink = sink; return DeleteHr; }
        public int PerformOperations() { Calls.Add("perform"); return PerformHr; }

        public int Advise(nint sink, out uint cookie) { cookie = 0; return 0; }
        public int Unadvise(uint cookie) => 0;
        public int SetProgressMessage(nint message) => 0;
        public int SetProgressDialog(nint dialog) => 0;
        public int SetProperties(nint properties) => 0;
        public int SetOwnerWindow(nint owner) => 0;
        public int ApplyPropertiesToItem(IShellItem item) => 0;
        public int ApplyPropertiesToItems(nint items) => 0;
        public int RenameItem(IShellItem item, nint newName, nint sink) => 0;
        public int RenameItems(nint items, nint newName) => 0;
        public int MoveItem(IShellItem item, IShellItem destination, nint newName, nint sink) => 0;
        public int MoveItems(nint items, IShellItem destination) => 0;
        public int CopyItem(IShellItem item, IShellItem destination, nint copyName, nint sink) => 0;
        public int CopyItems(nint items, IShellItem destination) => 0;
        public int DeleteItems(nint items) => 0;
        public int NewItem(IShellItem destination, uint attributes, nint name, nint templateName, nint sink) => 0;
        public int GetAnyOperationsAborted(out int aborted) { aborted = 0; return 0; }
    }

    private sealed class Released
    {
        public List<object?> Order { get; } = [];
        public void Record(object? value) => Order.Add(value);
    }

    /// <summary>
    /// A native COM object: <c>[vtable pointer]</c> + a vtable of UnmanagedCallersOnly thunks. Counts references, answers
    /// QueryInterface for the listed IIDs, and (as IShellWindows / IWebBrowserApp) get_Count, Item and get_LocationURL.
    /// </summary>
    private sealed unsafe class FakeNativeCom : IDisposable
    {
        private const int SlotCount = 40;
        private static readonly ConcurrentDictionary<nint, FakeNativeCom> Live = new();
        private readonly nint _vtable = Marshal.AllocHGlobal(SlotCount * nint.Size);
        private readonly HashSet<Guid> _supported;
        private int _refs;

        public FakeNativeCom(params Guid[] supportedInterfaces)
        {
            _supported = [.. supportedInterfaces];
            for (var i = 0; i < SlotCount; i++) Marshal.WriteIntPtr(_vtable, i * nint.Size, 0);
            Marshal.WriteIntPtr(_vtable, 0 * nint.Size, (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterfaceThunk);
            Marshal.WriteIntPtr(_vtable, 1 * nint.Size, (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRefThunk);
            Marshal.WriteIntPtr(_vtable, 2 * nint.Size, (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseThunk);
            Marshal.WriteIntPtr(_vtable, 7 * nint.Size, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&CountThunk);
            Marshal.WriteIntPtr(_vtable, 8 * nint.Size, (nint)(delegate* unmanaged[Stdcall]<nint, Variant, nint*, int>)&ItemThunk);
            Marshal.WriteIntPtr(_vtable, 30 * nint.Size, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&LocationThunk);
            Pointer = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(Pointer, _vtable);
            Live[Pointer] = this;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Variant
        {
            public ushort Vt;
            public ushort R1, R2, R3;
            public nint Data1, Data2;
        }

        public nint Pointer { get; }
        public int Refs => Volatile.Read(ref _refs);

        // IShellWindows
        public int CountHr { get; set; }
        public int Count { get; set; }
        public Func<int, (int Hr, FakeNativeCom? Item)> OnItem { get; set; } = _ => (0, null);
        public ushort LastIndexVt { get; private set; }
        public nint LastIndex { get; private set; } = -1;

        // IWebBrowserApp
        public int LocationHr { get; set; }
        public string? Location { get; set; }

        public nint Acquire()
        {
            Interlocked.Increment(ref _refs);
            return Pointer;
        }

        public void Dispose()
        {
            Live.TryRemove(Pointer, out _);
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(_vtable);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static int QueryInterfaceThunk(nint self, Guid* iid, nint* result)
        {
            var fake = Live[self];
            if (*iid == IidUnknown || fake._supported.Contains(*iid))
            {
                *result = fake.Acquire();
                return 0;
            }
            *result = 0;
            return ENoInterface;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static uint AddRefThunk(nint self) => (uint)Interlocked.Increment(ref Live[self]._refs);

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static uint ReleaseThunk(nint self) => (uint)Interlocked.Decrement(ref Live[self]._refs);

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static int CountThunk(nint self, int* count)
        {
            var fake = Live[self];
            *count = fake.Count;
            return fake.CountHr;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static int ItemThunk(nint self, Variant index, nint* item)
        {
            var fake = Live[self];
            fake.LastIndexVt = index.Vt;
            fake.LastIndex = index.Data1;
            var (hr, found) = fake.OnItem((int)index.Data1);
            *item = found is null ? 0 : found.Acquire();
            return hr;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static int LocationThunk(nint self, nint* bstr)
        {
            var fake = Live[self];
            *bstr = fake.Location is null ? 0 : Marshal.StringToBSTR(fake.Location);
            return fake.LocationHr;
        }

        private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    }

    private static readonly Guid IidShellItem = new(ShellIds.ShellItem);

    private static Exception ComFailure(int hr) => Marshal.GetExceptionForHR(hr)!;

    // ------------------------------------------------------------------------------------------------------------
    // ShellCom: Wrap / Release (the reference-counting boundary)
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Wrap_TakesTheCallersReference_AndReleaseGivesTheWrappersBack()
    {
        using var native = new FakeNativeCom(IidShellItem);
        var pointer = native.Acquire();

        var item = ShellCom.Wrap<IShellItem>(pointer);
        Assert.True(native.Refs >= 1, "the wrapper keeps its own reference");
        ShellCom.Release(item);

        Assert.Equal(0, native.Refs);
        ShellCom.Release(item); // a second release is a no-op, not a double Release
        Assert.Equal(0, native.Refs);
    }

    [Fact]
    public void Wrap_UnsupportedInterface_ThrowsInvalidCastAndLeavesNoReference()
    {
        using var native = new FakeNativeCom(IidShellItem);
        var pointer = native.Acquire();

        Assert.Throws<InvalidCastException>(() => ShellCom.Wrap<IShellItem2>(pointer));

        Assert.Equal(0, native.Refs);
    }

    [Fact]
    public void Wrap_NullPointer_Throws() => Assert.Throws<ArgumentNullException>(() => ShellCom.Wrap<IShellItem>(0));

    [Fact]
    public void Release_ManagedFakeAndNull_AreNoOps()
    {
        ShellCom.Release(null);
        ShellCom.Release(new FakeShellItem("x"));
    }

    [Fact]
    public void Check_FailureThrowsTheHresultException_SuccessAndFalseDoNot()
    {
        ShellCom.Check(0);
        ShellCom.Check(1);
        Assert.Throws<COMException>(() => ShellCom.Check(HrFail));
        Assert.Throws<FileNotFoundException>(() => ShellCom.Check(unchecked((int)0x80070002)));
    }

    [Fact]
    public void StaRunner_RunsOnAnStaThread_AndPropagatesResultAndExceptionType()
    {
        var apartment = ShellStaRunner.Run(() => Thread.CurrentThread.GetApartmentState());
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.Equal(42, ShellStaRunner.Run(() => 42));
        Assert.Throws<InvalidOperationException>(() => ShellStaRunner.Run<int>(() => throw new InvalidOperationException("boom")));
    }

    // ------------------------------------------------------------------------------------------------------------
    // ShellFileRecycler (IFileOperation)
    // ------------------------------------------------------------------------------------------------------------

    private static readonly string[] AllSteps = ["flags", "delete", "perform"];

    private static (FakeFileOperation Operation, FakeShellItem Item, Released Released) RunRecycle(string path)
    {
        var operation = new FakeFileOperation();
        FakeShellItem? item = null;
        var released = new Released();
        ShellFileRecycler.Recycle(path, () => operation, p => item = new FakeShellItem(p), released.Record);
        return (operation, item!, released);
    }

    [Fact]
    public void Recycle_QueuesOneDeleteOfTheItem_WithTheRecycleFlags_ThenPerforms()
    {
        var (operation, item, _) = RunRecycle(@"C:\photos\a.jpg");

        Assert.Equal(["flags", "delete", "perform"], operation.Calls);
        Assert.Same(item, operation.Deleted);
        Assert.Equal(@"C:\photos\a.jpg", item.Path);
        Assert.Equal(0, operation.DeleteSink);
    }

    [Fact]
    public void Recycle_FlagsAllowUndoAndSuppressConfirmationAndProgressButKeepErrorDialogs()
    {
        var (operation, _, _) = RunRecycle(@"C:\photos\a.jpg");

        Assert.Equal(0x0040u | 0x0010u | 0x0200u | 0x0004u | 0x00080000u, operation.Flags);
        Assert.Equal(ShellFileRecycler.RecycleFlags, operation.Flags);
        Assert.NotEqual(0u, operation.Flags & 0x0040); // FOF_ALLOWUNDO: it must go to the bin, never a plain delete
        Assert.NotEqual(0u, operation.Flags & 0x00080000); // FOFX_RECYCLEONDELETE
        Assert.Equal(0u, operation.Flags & 0x0400); // FOF_NOERRORUI: error dialogs stay (VB OnlyErrorDialogs)
        Assert.Equal(0u, operation.Flags & 0x0008); // FOF_RENAMEONCOLLISION is not a delete flag
    }

    [Fact]
    public void Recycle_ReleasesTheItemThenTheOperation_OnSuccess()
    {
        var (operation, item, released) = RunRecycle(@"C:\photos\a.jpg");

        Assert.Equal(2, released.Order.Count);
        Assert.Same(item, released.Order[0]);
        Assert.Same(operation, released.Order[1]);
    }

    [Theory]
    [InlineData(1)] // flags
    [InlineData(2)] // delete
    [InlineData(3)] // perform
    public void Recycle_AFailingStep_Throws_StopsTheLaterSteps_AndStillReleasesBoth(int failingStep)
    {
        var released = new Released();
        var operation = new FakeFileOperation
        {
            SetFlagsHr = failingStep == 1 ? HrFail : 0,
            DeleteHr = failingStep == 2 ? HrFail : 0,
            PerformHr = failingStep == 3 ? HrFail : 0,
        };
        var item = new FakeShellItem("x");

        Assert.Throws<IOException>(() => ShellFileRecycler.Recycle(@"C:\photos\a.jpg", () => operation, _ => item, released.Record));

        Assert.Equal(AllSteps.Take(failingStep), operation.Calls);
        Assert.Equal(2, released.Order.Count);
    }

    [Fact]
    public void Recycle_ItemCannotBeCreated_ThrowsBeforeTheOperationExists()
    {
        var released = new Released();
        var created = false;

        Assert.Throws<FileNotFoundException>(() => ShellFileRecycler.Recycle(@"C:\photos\a.jpg",
            () => { created = true; return new FakeFileOperation(); }, _ => throw new FileNotFoundException("gone"), released.Record));

        Assert.False(created);
        Assert.All(released.Order, Assert.Null); // nothing was acquired, so nothing real is released
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002), typeof(FileNotFoundException))]
    [InlineData(unchecked((int)0x80070003), typeof(DirectoryNotFoundException))]
    [InlineData(unchecked((int)0x80070005), typeof(UnauthorizedAccessException))]
    [InlineData(unchecked((int)0x800700CE), typeof(PathTooLongException))]
    [InlineData(unchecked((int)0x80070020), typeof(IOException))] // sharing violation
    [InlineData(unchecked((int)0x80004005), typeof(IOException))] // E_FAIL
    public void Throw_MapsTheHresultToTheVbEraExceptionFamily(int hr, Type expected)
    {
        var ex = Assert.ThrowsAny<Exception>(() => ShellFileRecycler.Throw(hr, @"C:\photos\a.jpg"));

        Assert.Equal(expected, ex.GetType());
        if (expected == typeof(IOException)) Assert.Equal(hr, ex.HResult);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]                                // S_FALSE
    [InlineData(unchecked((int)0x800704C7))]       // ERROR_CANCELLED: not an error here, the caller sees the file still in place
    [InlineData(unchecked((int)0x80004004))]       // E_ABORT
    public void Throw_SuccessAndCancellation_DoNotThrow(int hr) => ShellFileRecycler.Throw(hr, @"C:\photos\a.jpg");

    [Fact]
    public void RecycleFlagConstants_MatchTheSdkValues()
    {
        Assert.Equal(0x0004u, ShellFileRecycler.FofSilent);
        Assert.Equal(0x0010u, ShellFileRecycler.FofNoConfirmation);
        Assert.Equal(0x0040u, ShellFileRecycler.FofAllowUndo);
        Assert.Equal(0x0200u, ShellFileRecycler.FofNoConfirmMkdir);
        Assert.Equal(0x00080000u, ShellFileRecycler.FofxRecycleOnDelete);
    }

    [Fact]
    public void Recycle_MissingFile_ThroughTheRealShellItemFactory_IsFileNotFound_AndNothingIsDeleted()
    {
        // The real SHCreateItemFromParsingName on a path that does not exist: fails before any IFileOperation exists.
        var missing = Path.Combine(Path.GetTempPath(), "PhotoReviewWp33_" + Guid.NewGuid().ToString("N"), "nope.jpg");

        Assert.ThrowsAny<IOException>(() => ShellFileRecycler.Recycle(missing));
    }

    // ------------------------------------------------------------------------------------------------------------
    // RecycleBinRestorer (policy over a fake bin session)
    // ------------------------------------------------------------------------------------------------------------

    private sealed class FakeVerbs(params string[] names) : IRecycleVerbs
    {
        public IReadOnlyList<string> Names { get; } = names;
        public List<int> ByIndex { get; } = [];
        public List<string> Canonical { get; } = [];
        public int Disposed { get; private set; }
        public void InvokeByIndex(int index) => ByIndex.Add(index);
        public void InvokeCanonical(string verb) => Canonical.Add(verb);
        public void Dispose() => Disposed++;
    }

    private sealed class FakeEntry(string? deletedFrom, string? name, long size, DateTime modified) : IRecycleBinEntry
    {
        public FakeVerbs Verbs { get; set; } = new("&Restore", "Cut", "Delete");
        public Exception? DeletedFromFailure { get; set; }
        public bool SizeReadable { get; set; } = true;
        public bool ModifiedReadable { get; set; } = true;
        public int Disposed { get; private set; }
        public Action? OnOpenVerbs { get; set; }
        public string? DeletedFrom() => DeletedFromFailure is null ? deletedFrom : throw DeletedFromFailure;
        public string? DisplayName() => name;
        public bool TryGetSize(out long value) { value = size; return SizeReadable; }
        public bool TryGetModifiedUtc(out DateTime utc) { utc = modified; return ModifiedReadable; }
        public IRecycleVerbs OpenVerbs() { OnOpenVerbs?.Invoke(); return Verbs; }
        public void Dispose() => Disposed++;
    }

    private sealed class FakeSession(params IRecycleBinEntry[] entries) : IRecycleBinSession
    {
        public int Disposed { get; private set; }
        public Func<IEnumerable<IRecycleBinEntry>>? Override { get; set; }
        public IEnumerable<IRecycleBinEntry> Entries() => Override is null ? entries : Override();
        public void Dispose() => Disposed++;
    }

    private sealed class RecordingLog : ILog
    {
        public List<string> Errors { get; } = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) => Errors.Add(message);
    }

    private static readonly DateTime Stamp = new(2026, 10, 11, 8, 30, 15, DateTimeKind.Utc);

    /// <summary>A real temp file with a known size and write time: what <see cref="WindowsRecycleBin.WaitForRestore"/> looks for.</summary>
    private sealed class TempPhoto : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "PhotoReviewWp33_" + Guid.NewGuid().ToString("N"));
        public string Path_ { get; }
        public TempPhoto(bool present)
        {
            Directory.CreateDirectory(Folder);
            Path_ = Path.Combine(Folder, "photo.jpg");
            if (!present) return;
            File.WriteAllBytes(Path_, new byte[5]);
            File.SetLastWriteTimeUtc(Path_, Stamp);
        }
        public void Dispose() { try { Directory.Delete(Folder, true); } catch (IOException) { } }
    }

    [Fact]
    public void Restore_ExactlyOneMatch_InvokesTheRestoreVerbByIndex_AndReportsTheFileBack()
    {
        using var photo = new TempPhoto(present: true);
        var match = new FakeEntry(photo.Folder, "photo", 5, Stamp);
        var other = new FakeEntry(photo.Folder, "different", 5, Stamp);
        var session = new FakeSession(other, match);
        var log = new RecordingLog();

        var ok = RecycleBinRestorer.Restore(() => session, photo.Path_, 5, Stamp, log);

        Assert.True(ok);
        Assert.Equal([0], match.Verbs.ByIndex);
        Assert.Empty(match.Verbs.Canonical);
        Assert.Equal(1, match.Verbs.Disposed);
        Assert.Equal(1, other.Disposed);
        Assert.Equal(1, match.Disposed);
        Assert.Equal(1, session.Disposed);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Restore_NoRestoreNamedVerb_FallsBackToTheCanonicalUndelete()
    {
        using var photo = new TempPhoto(present: true);
        var match = new FakeEntry(photo.Folder, "photo.jpg", 5, Stamp) { Verbs = new("Cut", "Delete") };

        var ok = RecycleBinRestorer.Restore(() => new FakeSession(match), photo.Path_, 5, Stamp, new RecordingLog());

        Assert.True(ok);
        Assert.Empty(match.Verbs.ByIndex);
        Assert.Equal(["undelete"], match.Verbs.Canonical);
    }

    [Fact]
    public void Restore_LocalisedRestoreVerbAtAnotherPosition_IsFoundByName()
    {
        using var photo = new TempPhoto(present: true);
        var match = new FakeEntry(photo.Folder, "photo", 5, Stamp) { Verbs = new("Cắt", "&Khôi phục", "Xoá") };

        RecycleBinRestorer.Restore(() => new FakeSession(match), photo.Path_, 5, Stamp, new RecordingLog());

        Assert.Equal([1], match.Verbs.ByIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Restore_NotExactlyOneMatch_InvokesNoVerb_AndReleasesEveryEntry(int matches)
    {
        using var photo = new TempPhoto(present: true);
        var entries = Enumerable.Range(0, matches).Select(i => new FakeEntry(photo.Folder, i == 0 ? "photo" : "photo.jpg", 5, Stamp)).ToArray();
        var stranger = new FakeEntry(@"C:\elsewhere", "photo", 5, Stamp);
        var session = new FakeSession([.. entries, stranger]);

        var ok = RecycleBinRestorer.Restore(() => session, photo.Path_, 5, Stamp, new RecordingLog());

        Assert.False(ok);
        Assert.All(entries, e => { Assert.Empty(e.Verbs.ByIndex); Assert.Empty(e.Verbs.Canonical); Assert.Equal(1, e.Disposed); });
        Assert.Equal(1, stranger.Disposed);
        Assert.Equal(1, session.Disposed);
    }

    [Fact]
    public void Restore_SizeAndTimestampMustMatch()
    {
        using var photo = new TempPhoto(present: true);
        var wrongSize = new FakeEntry(photo.Folder, "photo", 6, Stamp);
        var wrongTime = new FakeEntry(photo.Folder, "photo", 5, Stamp.AddMinutes(5));

        var ok = RecycleBinRestorer.Restore(() => new FakeSession(wrongSize, wrongTime), photo.Path_, 5, Stamp, new RecordingLog());

        Assert.False(ok);
        Assert.Empty(wrongSize.Verbs.ByIndex);
        Assert.Empty(wrongTime.Verbs.ByIndex);
    }

    [Fact]
    public void Restore_UnreadableSizeOrTimestamp_SkipsThatItem_WithoutError()
    {
        using var photo = new TempPhoto(present: true);
        var noSize = new FakeEntry(photo.Folder, "photo", 5, Stamp) { SizeReadable = false };
        var noTime = new FakeEntry(photo.Folder, "photo", 5, Stamp) { ModifiedReadable = false };
        var good = new FakeEntry(photo.Folder, "photo", 5, Stamp);
        var log = new RecordingLog();

        var ok = RecycleBinRestorer.Restore(() => new FakeSession(noSize, noTime, good), photo.Path_, 5, Stamp, log);

        Assert.True(ok);
        Assert.Equal([0], good.Verbs.ByIndex);
        Assert.Empty(noSize.Verbs.ByIndex);
        Assert.Empty(noTime.Verbs.ByIndex);
        Assert.Equal(1, noSize.Disposed);
        Assert.Equal(1, noTime.Disposed);
        Assert.Empty(log.Errors);
    }

    [Theory]
    [InlineData(typeof(COMException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(FormatException))]
    public void Restore_PerItemFailure_SkipsThatItemAndLogsIt(Type failure)
    {
        using var photo = new TempPhoto(present: true);
        var broken = new FakeEntry(photo.Folder, "photo", 5, Stamp) { DeletedFromFailure = (Exception)Activator.CreateInstance(failure)! };
        var good = new FakeEntry(photo.Folder, "photo", 5, Stamp);
        var log = new RecordingLog();

        var ok = RecycleBinRestorer.Restore(() => new FakeSession(broken, good), photo.Path_, 5, Stamp, log);

        Assert.True(ok);
        Assert.Equal(1, broken.Disposed);
        Assert.Single(log.Errors);
        Assert.Contains("inspection failed", log.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_UnrelatedFailure_AbortsTheWholeRestore_AndReleasesWhatWasHeld()
    {
        using var photo = new TempPhoto(present: true);
        var first = new FakeEntry(photo.Folder, "photo", 5, Stamp);
        var broken = new FakeEntry(photo.Folder, "photo", 5, Stamp) { DeletedFromFailure = new InvalidOperationException("boom") };
        var session = new FakeSession(first, broken);
        var log = new RecordingLog();

        var ok = RecycleBinRestorer.Restore(() => session, photo.Path_, 5, Stamp, log);

        Assert.False(ok);
        Assert.Empty(first.Verbs.ByIndex);
        Assert.Equal(1, first.Disposed); // the kept candidate is released on the way out
        Assert.Equal(1, broken.Disposed);
        Assert.Equal(1, session.Disposed);
        Assert.Contains(log.Errors, e => e.StartsWith("Recycle Bin restore failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Restore_EnumerationThrowsMidWalk_ReturnsFalseAndReleasesTheSession()
    {
        using var photo = new TempPhoto(present: true);
        var session = new FakeSession { Override = () => Walk(new FakeEntry(photo.Folder, "photo", 5, Stamp)) };
        var log = new RecordingLog();

        var ok = RecycleBinRestorer.Restore(() => session, photo.Path_, 5, Stamp, log);

        Assert.False(ok);
        Assert.Equal(1, session.Disposed);
        Assert.Single(log.Errors);

        static IEnumerable<IRecycleBinEntry> Walk(IRecycleBinEntry first)
        {
            yield return first;
            throw ComFailure(HrFail);
        }
    }

    [Fact]
    public void Restore_BinUnavailable_IsFalse_WithoutAnError()
    {
        var log = new RecordingLog();

        Assert.False(RecycleBinRestorer.Restore(() => null, @"C:\x\photo.jpg", 5, Stamp, log));
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Restore_OpeningTheSessionThrows_IsFalse_AndLogged()
    {
        var log = new RecordingLog();

        Assert.False(RecycleBinRestorer.Restore(() => throw ComFailure(HrFail), @"C:\x\photo.jpg", 5, Stamp, log));
        Assert.Single(log.Errors);
    }

    [Fact]
    public void Restore_VerbInvocationFails_IsFalse_ReleasesVerbsAndEntry()
    {
        using var photo = new TempPhoto(present: false);
        var match = new FakeEntry(photo.Folder, "photo", 5, Stamp) { OnOpenVerbs = () => throw ComFailure(HrFail) };
        var session = new FakeSession(match);

        var ok = RecycleBinRestorer.Restore(() => session, photo.Path_, 5, Stamp, new RecordingLog());

        Assert.False(ok);
        Assert.Equal(1, match.Disposed);
        Assert.Equal(1, session.Disposed);
    }

    [Fact]
    public void Restore_TheFileNeverComesBack_ReportsFalseAfterTheVerbRan()
    {
        using var photo = new TempPhoto(present: false); // the shell "restored" nothing
        var match = new FakeEntry(photo.Folder, "photo", 5, Stamp);

        var ok = RecycleBinRestorer.Restore(() => new FakeSession(match), photo.Path_, 5, Stamp, new RecordingLog());

        Assert.False(ok);
        Assert.Equal([0], match.Verbs.ByIndex); // the verb DID run exactly once; only the confirmation failed
    }

    // ------------------------------------------------------------------------------------------------------------
    // The real shell, read-only (no send, no restore, no delete: only open + enumerate + read properties)
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void RealBin_OpenEnumerateAndReadProperties_IsReadOnlyAndNeverFails()
    {
        // Runs on the dedicated STA thread; every COM object lives and dies inside the call.
        var (opened, read, verbCounts) = ShellStaRunner.Run(() =>
        {
            using var session = ComRecycleBinSession.Open();
            if (session is null) return (false, 0, new List<int>());
            var read = 0;
            var counts = new List<int>();
            foreach (var entry in session.Entries())
            {
                using (entry)
                {
                    _ = entry.DeletedFrom();
                    _ = entry.DisplayName();
                    var hasSize = entry.TryGetSize(out var size);
                    var hasTime = entry.TryGetModifiedUtc(out var utc);
                    if (hasSize) Assert.True(size >= 0);
                    if (hasTime) Assert.True(utc.Year > 1990);
                    if (read == 0)
                        using (var verbs = entry.OpenVerbs()) counts.Add(verbs.Names.Count);
                }
                if (++read >= 5) break;
            }
            return (true, read, counts);
        });

        Assert.True(opened);
        Assert.True(read >= 0);
        Assert.All(verbCounts, count => Assert.True(count > 0, "a bin item always offers at least one verb"));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Explorer windows through IShellWindows / IWebBrowserApp slots
    // ------------------------------------------------------------------------------------------------------------

    private static FakeNativeCom Browser(string? location, int hr = 0, bool supportsApp = true)
        => new(supportsApp ? [ExplorerComInterop.IidWebBrowserApp] : [])
        { Location = location, LocationHr = hr };

    /// <summary>ShellWindowsSource owns one reference to the collection; hand it one the way CoCreateInstance would.</summary>
    private static ShellWindowsSource SourceOver(FakeNativeCom shellWindows) => new(shellWindows.Acquire());

    [Fact]
    public void ShellWindows_WalksCountItems_ReadsLocation_AndReleasesEverything()
    {
        using var first = Browser("file:///C:/a/");
        using var second = Browser("file:///C:/b/");
        using var collection = new FakeNativeCom { Count = 2 };
        collection.OnItem = i => (0, i == 0 ? first : second);
        var source = SourceOver(collection);

        var locations = new List<string?>();
        foreach (var window in source.Windows)
        {
            using (window) locations.Add(window.LocationUrl);
        }
        source.Dispose();

        Assert.Equal(["file:///C:/a/", "file:///C:/b/"], locations);
        Assert.Equal(0, first.Refs);
        Assert.Equal(0, second.Refs);
        Assert.Equal(0, collection.Refs);
    }

    [Fact]
    public void ShellWindows_IndexIsPassedAsAnI4Variant()
    {
        using var window = Browser("file:///C:/a/");
        using var collection = new FakeNativeCom { Count = 3 };
        collection.OnItem = _ => (0, window);
        var source = SourceOver(collection);

        var walked = 0;
        foreach (var item in source.Windows)
        {
            item.Dispose();
            Assert.Equal((ushort)3, collection.LastIndexVt); // VT_I4
            Assert.Equal((nint)walked, collection.LastIndex);
            walked++;
        }
        source.Dispose();

        Assert.Equal(3, walked);
    }

    [Fact]
    public void ShellWindows_ANullItem_IsSkipped()
    {
        using var window = Browser("file:///C:/a/");
        using var collection = new FakeNativeCom { Count = 3 };
        collection.OnItem = i => i == 1 ? (1, null) : (0, window);
        var source = SourceOver(collection);

        var count = 0;
        foreach (var item in source.Windows) { item.Dispose(); count++; }
        source.Dispose();

        Assert.Equal(2, count);
    }

    [Fact]
    public void ShellWindows_ItemFailingMidWalk_SurfacesAsComExceptionFromTheEnumerator()
    {
        using var window = Browser("file:///C:/a/");
        using var collection = new FakeNativeCom { Count = 3 };
        collection.OnItem = i => i == 1 ? (HrFail, null) : (0, window);
        var source = SourceOver(collection);
        var seen = 0;

        Assert.Throws<COMException>(() =>
        {
            foreach (var item in source.Windows) { item.Dispose(); seen++; }
        });
        source.Dispose();

        Assert.Equal(1, seen);
        Assert.Equal(0, window.Refs);
    }

    [Fact]
    public void ShellWindows_CountFailing_SurfacesAsComException()
    {
        using var collection = new FakeNativeCom { CountHr = HrFail, Count = 7 };
        var source = SourceOver(collection);

        Assert.Throws<COMException>(() => source.Windows.ToList());
        source.Dispose();
        Assert.Equal(0, collection.Refs);
    }

    [Fact]
    public void ShellWindows_UseAfterDispose_Throws_AndDisposeIsIdempotent()
    {
        using var collection = new FakeNativeCom { Count = 0 };
        var source = SourceOver(collection);
        source.Dispose();
        source.Dispose();

        Assert.Equal(0, collection.Refs);
        Assert.Throws<ObjectDisposedException>(() => source.Windows.ToList());
    }

    [Fact]
    public void ShellWindow_LocationFailures_AreComExceptions_AndAnEmptyLocationIsNull()
    {
        using var noInterface = Browser("x", supportsApp: false);
        using var failing = Browser("x", hr: HrFail);
        using var nothing = Browser(null);

        using (var window = new ShellWindow(noInterface.Acquire()))
            Assert.Throws<InvalidCastException>(() => window.LocationUrl); // E_NOINTERFACE maps to InvalidCastException; the selector skips it
        using (var window = new ShellWindow(failing.Acquire()))
            Assert.Throws<COMException>(() => window.LocationUrl);
        using (var window = new ShellWindow(nothing.Acquire()))
            Assert.Null(window.LocationUrl);

        // The interface pointer from QueryInterface is released on every path.
        Assert.Equal(0, noInterface.Refs);
        Assert.Equal(0, failing.Refs);
        Assert.Equal(0, nothing.Refs);
    }

    [Fact]
    public void ShellWindow_AcquireUnknown_IsANewReference_AndDisposeReleasesTheOwnedOneOnce()
    {
        using var native = Browser("file:///C:/a/");
        var window = new ShellWindow(native.Acquire());

        var extra = window.AcquireUnknown();
        Assert.Equal(native.Pointer, extra);
        Assert.Equal(2, native.Refs);
        Marshal.Release(extra);
        window.Dispose();
        window.Dispose();

        Assert.Equal(0, native.Refs);
        Assert.Throws<ObjectDisposedException>(() => window.AcquireUnknown());
        Assert.Throws<ObjectDisposedException>(() => window.LocationUrl);
    }

    [Fact]
    public async Task Service_ShellClassNotRegistered_IsReportedAsShellUnavailable()
    {
        var folder = Path.GetTempPath();
        using var service = new ExplorerOrderService(null, null,
            shell: new ExplorerOrderService.ShellAccess(() => true, () => throw ComFailure(ExplorerComInterop.ClassNotRegistered)));

        var snapshot = await service.TryGetSnapshotAsync(folder, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.NativeViewUnavailable, snapshot.Status);
        Assert.Equal(ExplorerReason.ShellUnavailable, snapshot.Reason);
    }

    [Fact]
    public async Task Service_OtherComFailureOpeningTheShell_IsFailedNotUnavailable()
    {
        var folder = Path.GetTempPath();
        using var service = new ExplorerOrderService(null, null,
            shell: new ExplorerOrderService.ShellAccess(() => true, () => throw ComFailure(unchecked((int)0x800706BA))));

        var snapshot = await service.TryGetSnapshotAsync(folder, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.Failed, snapshot.Status);
    }
}
