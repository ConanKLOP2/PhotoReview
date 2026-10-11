using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Mutation-testing gap tests (Stryker round 2) for the managed LibRaw decode admission: the memory guard's raw-structure and size
/// rules, the single-slot gate's queue counters and its racing-cancellation hand-over, and the process-wide waiter count. No libraw.dll
/// and no corpus: the native handle is replaced by a fake probe, the gate is driven directly.
/// </summary>
[Collection(LibRawNativeDecodeGate.Name)] // the full-decode slot of LibRawDecoder is process-wide
public sealed class LibRawGateAndGuardMutationGapTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // ---- DecodeMemoryGuard -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)] // monochrome mosaic
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)] // RGBG
    public void Classify_DngMosaicWithOneToFourColours_IsBayer(int colors)
    {
        var family = DecodeMemoryGuard.Classify("lossless_dng_load_raw()", new DecodeMemoryGuard.RawStructure(colors, 0x94949494u));

        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Bayer, family);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Classify_DngMosaicWithAnImplausibleColourCount_IsLinear(int colors)
    {
        var family = DecodeMemoryGuard.Classify("lossless_dng_load_raw()", new DecodeMemoryGuard.RawStructure(colors, 0x94949494u));

        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Linear, family);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void EstimatePeakBytes_OnlyOneRawSideKnown_UsesTheOutputSizeForTheRawBuffer(int rawWidth, int rawHeight)
    {
        var withHalfKnownRaw = DecodeMemoryGuard.EstimatePeakBytes(1000, 800, 500, 400, rawWidth, rawHeight);

        Assert.Equal(DecodeMemoryGuard.EstimatePeakBytes(1000, 800, 500, 400), withHalfKnownRaw);
    }

    [Fact]
    public void EstimatePeakBytes_BothRawSidesKnown_UsesTheSensorSizeForTheRawBuffer()
    {
        var estimate = DecodeMemoryGuard.EstimatePeakBytes(1000, 800, 500, 400, rawWidth: 1100, rawHeight: 900);

        // 1000x800 x (8+3) working/output + 1100x900 x 2 raw + 500x400 x 4 bitmap
        Assert.Equal((1000L * 800 * 11) + (1100L * 900 * 2) + (500L * 400 * 4), estimate);
    }

    // ---- LibRawDecoder.AdmitDecode: unknown size never consults the memory ---------------------------------------------------

    private sealed class FakeProbe(int width, int height) : LibRawDecoder.ILibRawProbe
    {
        public int Width => width;
        public int Height => height;
        public int RawWidth => width;
        public int RawHeight => height;
        public string? DecoderName => "unpacked_load_raw()";
        public DecodeMemoryGuard.RawStructure? Structure => new(3, 0x94949494u);
    }

    [Theory]
    [InlineData(0, 10_000)]
    [InlineData(10_000, 0)]
    [InlineData(0, 0)]
    public void AdmitDecode_AnUnknownImageSizeUnderMemoryPressure_IsAdmittedWithoutAnEstimate(int width, int height)
    {
        // Memory load above the total: even a zero estimate would be refused if the guard were consulted.
        var decoder = new LibRawDecoder(WpfBitmapSourceCodec.Instance) { MemoryInfo = () => (TotalAvailable: 100, Load: 1_000) };
        using var lease = new FullDecodeGate(LibRawDecoder.MaxQueuedPreloadDecodes).Enter(SourceReadPriority.Viewer, CancellationToken.None);

        decoder.AdmitDecode(new FakeProbe(width, height), new DecodeRequest(@"C:\nowhere\x.dng", new DecodeBox(100, 100)), lease);

        Assert.True(lease.IsWorked);
    }

    // ---- FullDecodeGate ----------------------------------------------------------------------------------------------------

    private sealed class Harness : IDisposable
    {
        private readonly SemaphoreSlim _queued = new(0);
        internal FullDecodeGate Gate { get; }

        internal Harness(int maxQueuedPreloads = 2, Action<SourceReadPriority>? onQueued = null) =>
            Gate = new FullDecodeGate(maxQueuedPreloads, p => { onQueued?.Invoke(p); _queued.Release(); });

        public void Dispose() => _queued.Dispose();

        internal void AwaitQueued() => Assert.True(_queued.Wait(Bound), "waiter never queued");

        internal (Task Task, SemaphoreSlim Release) Start(SourceReadPriority priority)
        {
            var release = new SemaphoreSlim(0);
            var task = Task.Factory.StartNew(() =>
            {
                using var lease = Gate.Enter(priority, CancellationToken.None);
                Assert.True(release.Wait(Bound));
            }, TaskCreationOptions.LongRunning);
            return (task, release);
        }
    }

    [Fact]
    public async Task QueuedViewers_AViewerWaitsBehindTheRunningDecode_IsCountedInItsOwnLane()
    {
        using var h = new Harness();
        var running = h.Gate.Enter(SourceReadPriority.Viewer, CancellationToken.None);
        var viewer = h.Start(SourceReadPriority.Viewer);
        h.AwaitQueued();

        Assert.Equal((1, 0), (h.Gate.QueuedViewers, h.Gate.QueuedPreloads));

        running.Dispose();
        viewer.Release.Release();
        await viewer.Task.WaitAsync(Bound);
        Assert.Equal((0, 0), (h.Gate.QueuedViewers, h.Gate.QueuedPreloads));
    }

    [Fact]
    public void Enter_SlotGrantedInTheSameInstantAsAFailureWhileQueuing_PassesTheSlotOnInsteadOfLosingIt()
    {
        FullDecodeGate.Lease? holder = null;
        // The queued-seam runs inside the guarded region: it hands the slot to this very waiter (releasing the holder) and then fails,
        // exactly the "granted and cancelled at once" race. The grant must not be lost: the slot has to end up free.
        using var h = new Harness(onQueued: _ =>
        {
            holder!.Dispose();
            throw new InvalidOperationException("seam failed after the grant");
        });
        holder = h.Gate.Enter(SourceReadPriority.Viewer, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => h.Gate.Enter(SourceReadPriority.Viewer, CancellationToken.None));

        Assert.Equal(1, h.Gate.SlotsAvailable);
        Assert.Equal(0, h.Gate.QueuedViewers);
        using var next = h.Gate.Enter(SourceReadPriority.Viewer, CancellationToken.None); // the slot really is usable again
        Assert.Equal(0, h.Gate.SlotsAvailable);
    }

    // ---- LibRawDecoder.FullDecodeQueuedWaiters -----------------------------------------------------------------------------

    private sealed class StubImage : IDecodedImage
    {
        public int PixelWidth => 1;
        public int PixelHeight => 1;
        public bool Downscaled => false;
        public int Orientation => 1;
        public long EstimatedBytes => 4;
        public object PlatformImage { get; } = new();
    }

    [Fact]
    public async Task FullDecodeQueuedWaiters_ViewerAndPreloadWaiting_CountsBothLanes()
    {
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new LibRawDecoder(WpfBitmapSourceCodec.Instance)
        {
            CoreOverride = (_, _, token) =>
            {
                holding.Set();
                Assert.True(release.Wait(Bound, token));
                return new StubImage();
            },
        };
        var instant = new LibRawDecoder(WpfBitmapSourceCodec.Instance) { CoreOverride = (_, _, _) => new StubImage() };

        var running = Task.Factory.StartNew(() => holder.Decode(new DecodeRequest(@"C:\nowhere\a.dng", DecodeBox.Unbounded)), TaskCreationOptions.LongRunning);
        Assert.True(holding.Wait(Bound));
        var viewer = Task.Factory.StartNew(() => instant.Decode(new DecodeRequest(@"C:\nowhere\b.dng", DecodeBox.Unbounded, priority: SourceReadPriority.Viewer)), TaskCreationOptions.LongRunning);
        var preload = Task.Factory.StartNew(() => instant.Decode(new DecodeRequest(@"C:\nowhere\c.dng", DecodeBox.Unbounded, priority: SourceReadPriority.Preload)), TaskCreationOptions.LongRunning);

        await Wait.UntilAsync(() => LibRawDecoder.FullDecodeQueuedWaiters == 2, "a viewer and a preload decode queued behind the running one");

        release.Set();
        await Task.WhenAll(running, viewer, preload).WaitAsync(Bound);
        Assert.Equal(0, LibRawDecoder.FullDecodeQueuedWaiters);
        Assert.Equal(1, LibRawDecoder.FullDecodeSlotsAvailable);
    }
}
