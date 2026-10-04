using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;
using Xunit;

#pragma warning disable CA2201 // OutOfMemoryException is thrown on purpose: the code under test treats it specially

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Managed-logic tests for the parts of <see cref="LibRawDecoder"/>, <see cref="LibRawAvailability"/> and the safe handles that sit
/// around the native calls (header validation, open retry, result mapping, cancellation callback, handle release, probe guards).
/// Like the existing CreateFailure tests, the failure paths only use libraw_strerror for the message text.
/// </summary>
public sealed class LibRawSeamGapTests
{
    private const int IoError = -100009;

    private static string LongPath => "C:\\" + new string('a', LibRawDecoder.LongPathThreshold) + ".cr3";

    private static LibRawDecoder.ProcessedImageHeader Header(int type, uint dataSize = 100, ushort width = 10, ushort height = 10, ushort colors = 3, ushort bits = 8) =>
        new() { Type = type, DataSize = dataSize, Width = width, Height = height, Colors = colors, Bits = bits };

    // ---------------------------------------------------------------- safe handles

    [Fact]
    public void SafeLibRawHandle_Dispose_ClosesTheNativeHandleOnce()
    {
        var closed = new List<IntPtr>();
        var handle = new SafeLibRawHandle((IntPtr)1234, closed.Add);

        handle.Dispose();
        handle.Dispose();

        Assert.Equal([(IntPtr)1234], closed);
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void SafeLibRawImageHandle_Dispose_ClearsTheNativeImageOnce()
    {
        var cleared = new List<IntPtr>();
        var handle = new SafeLibRawImageHandle((IntPtr)4321, cleared.Add);

        handle.Dispose();
        handle.Dispose();

        Assert.Equal([(IntPtr)4321], cleared);
    }

    [Fact]
    public void SafeLibRawHandle_ZeroHandle_IsInvalidAndNeverClosed()
    {
        var closed = new List<IntPtr>();
        var handle = new SafeLibRawHandle(IntPtr.Zero, closed.Add);

        Assert.True(handle.IsInvalid);
        handle.Dispose();

        Assert.Empty(closed);
    }

    // ---------------------------------------------------------------- header validation

    [Theory]
    [InlineData(1, 4u, true)]
    [InlineData(1, 3u, false)]
    [InlineData(1, 33554432u, true)]   // MaxThumbnailBytes
    [InlineData(1, 33554433u, false)]
    [InlineData(2, 100u, false)]       // a bitmap, not a JPEG
    [InlineData(0, 100u, false)]
    public void ValidateThumbnailHeader_AcceptsOnlyAJpegOfBoundedSize(int type, uint dataSize, bool valid)
    {
        var header = Header(type, dataSize);

        if (valid) LibRawDecoder.ValidateThumbnailHeader(header);
        else Assert.Throws<InvalidDataException>(() => LibRawDecoder.ValidateThumbnailHeader(header));
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(6000, 4000, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(-1, 1, false)]
    [InlineData(1, -1, false)]
    [InlineData(0, 0, false)]
    public void ValidateImageDimensions_RejectsANonPositiveSide(int width, int height, bool valid)
    {
        if (valid) LibRawDecoder.ValidateImageDimensions(width, height);
        else Assert.Throws<InvalidDataException>(() => LibRawDecoder.ValidateImageDimensions(width, height));
    }

    [Fact]
    public void IsSupportedBitmap_RequiresEveryPartOfTheHeaderToBeValid()
    {
        Assert.True(LibRawDecoder.IsSupportedBitmap(Header(2)));
        Assert.True(LibRawDecoder.IsSupportedBitmap(Header(2, colors: 1)));
        Assert.False(LibRawDecoder.IsSupportedBitmap(Header(1)));               // JPEG, not a bitmap
        Assert.False(LibRawDecoder.IsSupportedBitmap(Header(2, width: 0)));
        Assert.False(LibRawDecoder.IsSupportedBitmap(Header(2, height: 0)));
        Assert.False(LibRawDecoder.IsSupportedBitmap(Header(2, colors: 4)));
        Assert.False(LibRawDecoder.IsSupportedBitmap(Header(2, colors: 0)));
        Assert.False(LibRawDecoder.IsSupportedBitmap(Header(2, bits: 16)));
        Assert.False(LibRawDecoder.IsSupportedBitmap(Header(2, bits: 0)));
    }

    [Theory]
    [InlineData(100, 100, 100, 100, false)]
    [InlineData(99, 100, 100, 100, true)]
    [InlineData(100, 99, 100, 100, true)]
    [InlineData(50, 50, 100, 100, true)]
    [InlineData(101, 100, 100, 100, false)]
    [InlineData(100, 101, 100, 100, false)]
    public void IsDownscaled_IsTrueWhenEitherSideIsSmallerThanTheSource(int tw, int th, int sw, int sh, bool expected) =>
        Assert.Equal(expected, LibRawDecoder.IsDownscaled(tw, th, sw, sh));

    // ---------------------------------------------------------------- open retry

    [Fact]
    public void OpenFile_Success_OpensOnce()
    {
        var calls = new List<string>();

        LibRawDecoder.OpenFile(p => { calls.Add(p); return 0; }, LongPath, CancellationToken.None);

        Assert.Single(calls);
    }

    [Fact]
    public void OpenFile_IoErrorForAShortPath_FailsWithoutRetry()
    {
        var calls = 0;

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.OpenFile(_ => { calls++; return IoError; }, "C:\\short.cr3", CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void OpenFile_IoErrorForALongPath_RetriesInExtendedLengthForm()
    {
        var calls = new List<string>();

        LibRawDecoder.OpenFile(p => { calls.Add(p); return calls.Count == 1 ? IoError : 0; }, LongPath, CancellationToken.None);

        Assert.Equal([LongPath, "\\\\?\\" + LongPath], calls);
    }

    [Fact]
    public void OpenFile_RetryAlsoFails_ReportsTheOriginalError()
    {
        var calls = 0;
        var ex = Assert.Throws<InvalidDataException>(() => LibRawDecoder.OpenFile(_ => ++calls == 1 ? IoError : -100001, LongPath, CancellationToken.None));

        Assert.Equal(2, calls);
        Assert.Contains(LibRawNativeMethods.FormatError(IoError), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenFile_NonIoErrorForALongPath_IsNotRetried()
    {
        var calls = 0;

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.OpenFile(_ => { calls++; return -100001; }, LongPath, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    // ---------------------------------------------------------------- result mapping and cancellation

    [Fact]
    public void CheckResult_ZeroIsSuccess_AnythingElseFails()
    {
        LibRawDecoder.CheckResult(0, "op");

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.CheckResult(-100002, "op"));
        Assert.Throws<InvalidDataException>(() => LibRawDecoder.CheckResult(1, "op"));
    }

    [Fact]
    public void CheckResult_FailureWhileCancelled_IsACancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => LibRawDecoder.CheckResult(-100002, "op", cts.Token));
        LibRawDecoder.CheckResult(0, "op", cts.Token); // success is not turned into a cancellation
    }

    [Fact]
    public void CheckCancellation_ReportsTheTokenState()
    {
        using var cts = new CancellationTokenSource();
        using var state = new LibRawDecoder.CancellationState(cts.Token);

        Assert.Equal(0, LibRawDecoder.CheckCancellation(state.Pointer, 0, 0, 0));
        cts.Cancel();
        Assert.Equal(1, LibRawDecoder.CheckCancellation(state.Pointer, 0, 0, 0));
    }

    [Fact]
    public void CheckCancellation_UnrelatedTarget_DoesNotCancel_AndABadPointerDoes()
    {
        var other = GCHandle.Alloc("not a cancellation state");
        try
        {
            Assert.Equal(0, LibRawDecoder.CheckCancellation(GCHandle.ToIntPtr(other), 0, 0, 0));
        }
        finally { other.Free(); }

        Assert.Equal(1, LibRawDecoder.CheckCancellation(IntPtr.Zero, 0, 0, 0));
    }

    [Fact]
    public void CancellationState_Dispose_IsIdempotent()
    {
        var state = new LibRawDecoder.CancellationState(CancellationToken.None);
        var pointer = state.Pointer;
        Assert.Equal(0, LibRawDecoder.CheckCancellation(pointer, 0, 0, 0));

        state.Dispose();
        Assert.True(state.IsFreed); // Free() on a readonly GCHandle field only freed a copy, so the field stayed allocated and a second Dispose freed the slot again
        state.Dispose();

        // A second Dispose must not free the slot again. Had it, the runtime's handle free list would hold that slot twice
        // and two fresh handles would be handed the same slot (and corrupt each other's targets).
        var first = GCHandle.Alloc("first");
        var second = GCHandle.Alloc("second");
        try
        {
            Assert.NotEqual(GCHandle.ToIntPtr(first), GCHandle.ToIntPtr(second));
            Assert.Equal("first", first.Target);
            Assert.Equal("second", second.Target);
        }
        finally
        {
            first.Free();
            second.Free();
        }
    }

    [Fact]
    public void Decode_OutOfMemoryInTheDecodeBody_BecomesAnInvalidOperation()
    {
        var decoder = new LibRawDecoder { CoreOverride = (_, _, _) => throw new OutOfMemoryException() };

        var ex = Assert.Throws<InvalidOperationException>(() => decoder.Decode(new DecodeRequest("x.cr3", 100)));

        Assert.IsType<OutOfMemoryException>(ex.InnerException);
    }

    // ---------------------------------------------------------------- availability guards

    [Fact]
    public void GuardProbe_ReturnsTheCoreResult_AndTurnsAFailureIntoAReason()
    {
        Assert.Equal((true, (string?)null), LibRawAvailability.GuardProbe(() => (true, null)));

        var (available, reason) = LibRawAvailability.GuardProbe(() => throw new InvalidOperationException("boom"));

        Assert.False(available);
        Assert.Contains("probe failed: boom", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardProbe_MemoryExhaustion_Propagates() =>
        Assert.Throws<OutOfMemoryException>(() => LibRawAvailability.GuardProbe(() => throw new OutOfMemoryException()));

    [Fact]
    public void ComputeExactVersion_NeedsAPassingProbeAndTheExactPatch()
    {
        var versionCalls = 0;
        string? Version() { versionCalls++; return "0.22.2"; }

        Assert.False(LibRawAvailability.ComputeExactVersion(() => false, Version));
        Assert.Equal(0, versionCalls);
        Assert.True(LibRawAvailability.ComputeExactVersion(() => true, Version));
        Assert.False(LibRawAvailability.ComputeExactVersion(() => true, () => "0.22.3"));
    }

    [Fact]
    public void ComputeExactVersion_AThrowingCallIsFalse_MemoryExhaustionPropagates()
    {
        Assert.False(LibRawAvailability.ComputeExactVersion(() => throw new DllNotFoundException(), () => "0.22.2"));
        Assert.False(LibRawAvailability.ComputeExactVersion(() => true, () => throw new EntryPointNotFoundException()));
        Assert.Throws<OutOfMemoryException>(() => LibRawAvailability.ComputeExactVersion(() => throw new OutOfMemoryException(), () => "0.22.2"));
    }

    [Fact]
    public void ProbeLoadedLibrary_LibraryWithoutLibRawExports_IsRejected()
    {
        // kernel32 is already loaded in every Windows process; loading it again only bumps a reference count.
        var handle = NativeLibrary.Load("kernel32.dll");
        try
        {
            var (available, reason) = LibRawAvailability.ProbeLoadedLibrary(handle, "kernel32.dll");

            Assert.False(available);
            Assert.StartsWith("kernel32.dll does not export the required LibRaw entry point(s): ", reason, StringComparison.Ordinal);
        }
        finally { NativeLibrary.Free(handle); }
    }
}