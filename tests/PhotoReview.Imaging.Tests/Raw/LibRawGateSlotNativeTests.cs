using PhotoReview.Core.Model;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// The process-wide full-decode slot must be free again after every cancelled call. Native because the slot is shared
/// with every other LibRaw decode: it is only stable inside the serialised LibRaw collection.
/// </summary>
[Collection(LibRawNativeDecodeGate.Name)]
[Trait("Category", "Native")]
public sealed class LibRawGateSlotNativeTests
{
    private const string SampleName = "Canon - EOS 350D - RAW (3_2).CR2";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    [Fact]
    public void ReadJpegThumbnailAndDecode_WithCancelledToken_LeaveTheFullDecodeSlotFree()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => LibRawDecoder.ReadJpegThumbnail(@"Z:\missing.orf", cts.Token));
        Assert.Equal(1, LibRawDecoder.FullDecodeSlotsAvailable);
        Assert.Throws<OperationCanceledException>(() =>
            new LibRawDecoder().Decode(new DecodeRequest(@"Z:\missing.cr2", DecodeBox.Unbounded), cts.Token));
        Assert.Equal(1, LibRawDecoder.FullDecodeSlotsAvailable);
    }

    [Fact]
    public void Decode_CancelledAfterTheGateWasEntered_ReleasesTheSlotOnUnwind()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) ||
            RawCorpus.TryGetFile(SampleName) is not { } samplePath) return;
        using var cts = new CancellationTokenSource();
        var stages = new List<string>();
        // The slot is held from before DecodeCore: cancelling at "opened" proves the release after a real acquisition (a token cancelled
        // before the call throws before the gate and proves nothing about it).
        var decoder = new LibRawDecoder(stage =>
        {
            stages.Add(stage);
            if (stage == "opened")
            {
                Assert.Equal(0, LibRawDecoder.FullDecodeSlotsAvailable);
                cts.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            decoder.Decode(new DecodeRequest(samplePath, new DecodeBox(240, 180)), cts.Token));

        Assert.Contains("opened", stages);
        Assert.DoesNotContain("processed", stages); // it really stopped mid-decode
        Assert.Equal(1, LibRawDecoder.FullDecodeSlotsAvailable);
    }

    [Fact]
    public async Task Decode_QueuedWaiterCancelled_LeavesTheHolderInChargeAndFreesTheSlotAtTheEnd()
    {
        if (RawCorpus.TryGetFile(SampleName) is not { } samplePath) return;
        using var holderInside = new SemaphoreSlim(0);
        using var releaseHolder = new SemaphoreSlim(0);
        var holder = new LibRawDecoder(stage =>
        {
            if (stage != "opened") return;
            holderInside.Release();
            Assert.True(releaseHolder.Wait(Bound));
        });
        var holding = Task.Factory.StartNew(() => holder.Decode(new DecodeRequest(samplePath, new DecodeBox(240, 180))),
            TaskCreationOptions.LongRunning);
        Assert.True(holderInside.Wait(Bound), "the holder never reached the gate");
        Assert.Equal(0, LibRawDecoder.FullDecodeSlotsAvailable);

        using var cts = new CancellationTokenSource();
        var waiting = Task.Factory.StartNew(() =>
            new LibRawDecoder().Decode(new DecodeRequest(samplePath, new DecodeBox(240, 180)), cts.Token), TaskCreationOptions.LongRunning);
        Assert.True(SpinWait.SpinUntil(() => LibRawDecoder.FullDecodeQueuedWaiters == 1, Bound), "the waiter never queued behind the holder");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting).WaitAsync(Bound);
        Assert.Equal(0, LibRawDecoder.FullDecodeQueuedWaiters);

        Assert.Equal(0, LibRawDecoder.FullDecodeSlotsAvailable); // the cancelled waiter neither stole nor freed the holder's slot
        releaseHolder.Release();
        var decoded = await holding.WaitAsync(Bound);
        Assert.Equal(DecoderBackend.LibRaw, decoded.ActualBackend);
        Assert.Equal(1, LibRawDecoder.FullDecodeSlotsAvailable);
    }
}
