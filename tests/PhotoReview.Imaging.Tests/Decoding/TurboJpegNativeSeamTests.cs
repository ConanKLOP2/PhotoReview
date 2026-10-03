using PhotoReview.Imaging.TurboJpeg;
using PhotoReview.Imaging.TurboJpeg.Native;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// Mutation-testing seams for the TurboJPEG native layer, all with fakes (no turbojpeg.dll needed): the availability probe's
/// load / init failure handling, the <c>tj3Init</c> failure check and the safe handle's release rules.
/// </summary>
public sealed class TurboJpegNativeSeamTests
{
    private sealed class ProbeFakes
    {
        public int FreeCalls;
        public nint FreedHandle;
        public int Disposed;
        public bool LoadResult = true;
        public Exception? CreateThrows;

        public (bool Available, string? Reason) Run() => TurboJpegAvailability.RunProbe(
            (out nint handle) =>
            {
                handle = 0x1234;
                return LoadResult;
            },
            handle =>
            {
                FreeCalls++;
                FreedHandle = handle;
            },
            () =>
            {
                if (CreateThrows is not null) throw CreateThrows;
                return new DisposeCounter(this);
            });
    }

    private sealed class DisposeCounter(ProbeFakes owner) : IDisposable
    {
        public void Dispose() => owner.Disposed++;
    }

    [Fact]
    public void RunProbe_LibraryDoesNotLoad_ReportsUnavailableWithReasonAndInitsNothing()
    {
        var fakes = new ProbeFakes { LoadResult = false, CreateThrows = new InvalidOperationException("must not be called") };

        var (available, reason) = fakes.Run();

        Assert.False(available);
        Assert.Contains(TurboJpegNative.DllName, reason);
        Assert.Contains("Could not load", reason);
        Assert.Equal(0, fakes.FreeCalls); // nothing was loaded, so nothing is freed
        Assert.Equal(0, fakes.Disposed);
    }

    [Fact]
    public void RunProbe_DecompressorCreated_IsAvailableWithNoReasonAndReleasesEverything()
    {
        var fakes = new ProbeFakes();

        var (available, reason) = fakes.Run();

        Assert.True(available);
        Assert.Null(reason);
        Assert.Equal(1, fakes.Disposed);
        Assert.Equal(1, fakes.FreeCalls);
        Assert.Equal((nint)0x1234, fakes.FreedHandle);
    }

    [Theory]
    [InlineData(typeof(DllNotFoundException))]
    [InlineData(typeof(BadImageFormatException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(EntryPointNotFoundException))]
    public void RunProbe_InitThrowsANonFatalException_ReportsUnavailableWithItsMessageAndFreesTheLibrary(Type exceptionType)
    {
        var fakes = new ProbeFakes { CreateThrows = (Exception)Activator.CreateInstance(exceptionType, "boom-detail")! };

        var (available, reason) = fakes.Run();

        Assert.False(available);
        Assert.Contains("loaded but failed to initialize", reason);
        Assert.Contains("boom-detail", reason);
        Assert.Contains(TurboJpegNative.DllName, reason);
        Assert.Equal(1, fakes.FreeCalls);
        Assert.Equal(0, fakes.Disposed);
    }

    // CA2201 forbids `new OutOfMemoryException()`; an instance is still needed to stand in for the runtime's.
    private static OutOfMemoryException OutOfMemory() => Activator.CreateInstance<OutOfMemoryException>();

    [Fact]
    public void RunProbe_InitRunsOutOfMemory_IsNotSwallowedButTheLibraryIsStillFreed()
    {
        var fakes = new ProbeFakes { CreateThrows = OutOfMemory() };

        Assert.Throws<OutOfMemoryException>(() => fakes.Run());

        Assert.Equal(1, fakes.FreeCalls);
    }

    [Fact]
    public void WrapInitResult_NullPointer_ThrowsInvalidOperationNamingTheDecompressor()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TurboJpegNative.WrapInitResult(IntPtr.Zero));

        Assert.Contains("decompressor", ex.Message);
    }

    [Fact]
    public void WrapInitResult_NonNullPointer_ReturnsAnOwningValidHandle()
    {
        var handle = TurboJpegNative.WrapInitResult(new IntPtr(0x40));
        try
        {
            Assert.False(handle.IsInvalid);
            Assert.False(handle.IsClosed);
            Assert.Equal(new IntPtr(0x40), handle.DangerousGetHandle());
        }
        finally
        {
            handle.SetHandleAsInvalid(); // the pointer is fake: never hand it to the real tj3Destroy
        }
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(-1L, true)]
    [InlineData(1L, false)]
    [InlineData(0x1000L, false)]
    public void IsInvalid_ReflectsZeroAndMinusOne(long value, bool expected)
    {
        using var handle = new SafeTurboJpegHandle(new IntPtr(value), ownsHandle: false);

        Assert.Equal(expected, handle.IsInvalid);
    }

    [Fact]
    public void Dispose_OwnedValidHandle_DestroysItExactlyOnceWithItsValue()
    {
        var destroyed = new List<IntPtr>();
        var handle = new SafeTurboJpegHandle(new IntPtr(0x40), ownsHandle: true, destroyed.Add);

        handle.Dispose();
        handle.Dispose();

        Assert.Equal([new IntPtr(0x40)], destroyed);
        Assert.True(handle.IsClosed);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Dispose_ZeroOrMinusOneHandle_NeverCallsDestroy(long value)
    {
        var destroyed = new List<IntPtr>();
        var handle = new SafeTurboJpegHandle(new IntPtr(value), ownsHandle: true, destroyed.Add);

        handle.Dispose();

        Assert.Empty(destroyed);
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void Dispose_HandleThatIsNotOwned_NeverCallsDestroy()
    {
        var destroyed = new List<IntPtr>();
        var handle = new SafeTurboJpegHandle(new IntPtr(0x40), ownsHandle: false, destroyed.Add);

        handle.Dispose();

        Assert.Empty(destroyed);
    }

    [Fact]
    public void Dispose_DestroyThrows_PropagatesTheFailureAndTheHandleStaysClosed()
    {
        var handle = new SafeTurboJpegHandle(new IntPtr(0x40), ownsHandle: true, _ => throw new InvalidOperationException("destroy failed"));

        var ex = Assert.Throws<InvalidOperationException>(handle.Dispose);

        Assert.Equal("destroy failed", ex.Message);
        Assert.True(handle.IsClosed);
    }
}