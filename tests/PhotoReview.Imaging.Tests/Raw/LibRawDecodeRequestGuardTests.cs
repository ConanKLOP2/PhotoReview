using System.IO;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Request validation of <see cref="LibRawDecoder.Decode(DecodeRequest, CancellationToken)"/> that happens before the full-decode gate is
/// entered and before any native call (the decode body is replaced by the <c>CoreOverride</c> seam, so no LibRaw code runs).
/// </summary>
[Collection(LibRawNativeDecodeGate.Name)] // the full-decode slot is process-wide
public sealed class LibRawDecodeRequestGuardTests
{
    private static LibRawDecoder DecoderWhoseBodyMustNotRun(Action onBody) =>
        new(WpfBitmapSourceCodec.Instance) { CoreOverride = (_, _, _) => { onBody(); throw new InvalidOperationException("the decode body must not run"); } };

    [Fact(DisplayName = "An un-oriented decode is refused as NotSupported without entering the gate or running the decode body")]
    public void Decode_ApplyOrientationFalse_IsRefusedBeforeTheGateAndTheBody()
    {
        var bodyRan = false;
        var decoder = DecoderWhoseBodyMustNotRun(() => bodyRan = true);
        var slotsBefore = LibRawDecoder.FullDecodeSlotsAvailable; Assert.Equal(1, slotsBefore);

        var ex = Assert.Throws<NotSupportedException>(() => decoder.Decode(new DecodeRequest("x.cr3", 100, ApplyOrientation: false)));

        Assert.Contains("orientation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(bodyRan);
        Assert.Equal(slotsBefore, LibRawDecoder.FullDecodeSlotsAvailable);
    }

    [Theory(DisplayName = "A null, empty or blank path is rejected before the gate and the decode body")]
    [InlineData("")]
    [InlineData("   ")]
    public void Decode_BlankPath_IsRejectedBeforeTheBody(string path)
    {
        var bodyRan = false;
        var decoder = DecoderWhoseBodyMustNotRun(() => bodyRan = true);

        Assert.Throws<ArgumentException>(() => decoder.Decode(new DecodeRequest(path, 100)));

        Assert.False(bodyRan);
    }

    [Fact(DisplayName = "A valid oriented request reaches the decode body holding the gate slot, which is released afterwards")]
    public void Decode_ValidRequest_RunsTheBodyUnderTheGateAndReleasesTheSlot()
    {
        var slotsBefore = LibRawDecoder.FullDecodeSlotsAvailable; Assert.Equal(1, slotsBefore);
        int? slotsInBody = null;
        var decoder = new LibRawDecoder(WpfBitmapSourceCodec.Instance)
        {
            CoreOverride = (_, _, _) =>
            {
                slotsInBody = LibRawDecoder.FullDecodeSlotsAvailable;
                throw new InvalidDataException("stop here");
            },
        };

        Assert.Throws<InvalidDataException>(() => decoder.Decode(new DecodeRequest("x.cr3", 100)));

        Assert.Equal(0, slotsInBody); // the single slot is taken inside the body
        Assert.Equal(slotsBefore, LibRawDecoder.FullDecodeSlotsAvailable);
    }
}
