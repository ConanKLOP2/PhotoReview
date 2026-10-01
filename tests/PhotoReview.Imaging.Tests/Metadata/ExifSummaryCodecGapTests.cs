using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Tests.Metadata;

/// <summary>RV-T53 (codec part): text longer than 255 bytes cut mid-UTF-8 sequence, and a present mask bit with zero-length text.</summary>
[Trait("Category", "HotPath")]
public sealed class ExifSummaryCodecGapTests
{
    [Fact]
    public void Encode_TextWhoseUtf8IsLongerThan255Bytes_CutsMidSequenceAndStillDecodesToTheSanitisedText()
    {
        // 200 x 'e acute' = 400 UTF-8 bytes; the 255-byte cut lands in the middle of a two-byte sequence.
        var summary = new ExifSummary { CameraMake = new string('\u00E9', 200) };

        var encoded = ExifSummaryCodec.Encode(summary);

        Assert.Equal(2 + 1 + 255, encoded.Length);
        Assert.Equal(255, encoded[2]);
        var decoded = ExifSummaryCodec.Decode(encoded);
        Assert.NotNull(decoded);
        // The broken tail (U+FFFD) is beyond the 64-character cap, so the sanitised text is the clean prefix.
        Assert.Equal(new string('\u00E9', ExifSummary.MaxTextLength), decoded!.CameraMake);
    }

    [Fact]
    public void Decode_MakeBitSetWithZeroLengthText_YieldsNoMakeButKeepsTheOtherFields()
    {
        // version 1, mask Make|Iso, make length 0, ISO 400 (little-endian int32)
        byte[] data = [1, 2 | 16, 0, 0x90, 0x01, 0x00, 0x00];

        var decoded = ExifSummaryCodec.Decode(data);

        Assert.NotNull(decoded);
        Assert.Null(decoded!.CameraMake);
        Assert.Equal(400, decoded.Iso);
    }

    [Fact]
    public void Decode_OnlyAZeroLengthTextPresent_IsNull() =>
        Assert.Null(ExifSummaryCodec.Decode([1, 2, 0]));
}