using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Pure managed LibRaw interop logic: needs neither libraw.dll nor the corpus, so it runs in the default CI category.</summary>
public sealed class LibRawManagedLogicTests
{
    [Theory]
    [InlineData("0.22.2", true)]
    [InlineData("0.22.2-Release", true)]
    [InlineData("0.22.1", false)]
    [InlineData("0.22.3", false)]
    [InlineData("0.22.9-Release", false)]
    [InlineData("0.22.0", false)]
    [InlineData("0.22", false)]
    [InlineData("0.23.2", false)]
    [InlineData("1.22.2", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CheckExactVersion_OnlyTheTestedPatchReleasePasses(string? version, bool accepted)
    {
        Assert.Equal(accepted, LibRawAvailability.CheckExactVersion(version));
    }

    [Theory]
    [InlineData(-100007)]
    [InlineData(12)]
    public void CreateThumbnailFailure_InsufficientMemory_IsResourceErrorNotCorruptFile(int code)
    {
        Assert.IsType<InvalidOperationException>(LibRawDecoder.CreateThumbnailFailure(code));
    }

    [Fact]
    public void CreateThumbnailFailure_DataError_StaysInvalidData()
    {
        Assert.IsType<System.IO.InvalidDataException>(LibRawDecoder.CreateThumbnailFailure(-100008));
    }

    [Fact]
    public void Resize_MonochromeSameSize_ExpandsGrayToOpaqueBgra()
    {
        byte[] gray = [0, 90, 180, 255];
        var bgra = new byte[gray.Length * 4];

        RgbBgraResampler.Resize(gray, 2, 2, bgra, 2, 2, CancellationToken.None, channels: 1);

        Assert.Equal([(byte)0, 0, 0, 255, 90, 90, 90, 255, 180, 180, 180, 255, 255, 255, 255, 255], bgra);
    }

    [Fact]
    public void Resize_MonochromeBoxAverage_AveragesEachTargetPixel()
    {
        // 6x2 gray reduced to 2x1 (ratio 3 > 2): left 3x2 block averages (0+30)*3/6 = 15, right block (100+200)*3/6 = 150.
        byte[] gray = [0, 0, 0, 100, 100, 100, 30, 30, 30, 200, 200, 200];
        var bgra = new byte[2 * 4];

        RgbBgraResampler.Resize(gray, 6, 2, bgra, 2, 1, CancellationToken.None, channels: 1);

        Assert.Equal([(byte)15, 15, 15, 255, 150, 150, 150, 255], bgra);
    }

    [Fact]
    public void Resize_MonochromeBilinear_InterpolatesLikeTheRgbPath()
    {
        // Same ramp as the 3-channel Resize_ModerateReduction_UsesBilinear test, one byte per pixel.
        byte[] gray = [0, 90, 180, 255];
        var bgra = new byte[3 * 4];

        RgbBgraResampler.Resize(gray, 4, 1, bgra, 3, 1, CancellationToken.None, channels: 1);

        Assert.Equal([(byte)15, 15, 15, 255, 135, 135, 135, 255, 242, 242, 242, 255], bgra);
    }

    [Fact]
    public void Resize_MonochromeAndRgbWithEqualChannels_ProduceIdenticalOutput()
    {
        const int Source = 30, Target = 7;
        var gray = new byte[Source * Source];
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)(i * 37 % 251);
        var rgb = new byte[gray.Length * 3];
        for (var i = 0; i < gray.Length; i++) rgb.AsSpan(i * 3, 3).Fill(gray[i]);
        var fromGray = new byte[Target * Target * 4];
        var fromRgb = new byte[Target * Target * 4];

        RgbBgraResampler.Resize(gray, Source, Source, fromGray, Target, Target, CancellationToken.None, channels: 1);
        RgbBgraResampler.Resize(rgb, Source, Source, fromRgb, Target, Target, CancellationToken.None);

        Assert.Equal(fromRgb, fromGray);
    }

    [Fact]
    public void ValidateSourceLength_Monochrome_UsesOneBytePerPixel()
    {
        Assert.Equal(6000 * 4000, RgbBgraResampler.ValidateSourceLength(6000, 4000, 6000L * 4000, channels: 1));
        Assert.Throws<System.IO.InvalidDataException>(() => RgbBgraResampler.ValidateSourceLength(6000, 4000, 6000L * 4000 - 1, channels: 1));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    public void IsSupportedChannelCount_OnlyGrayAndRgb(int channels, bool supported)
    {
        Assert.Equal(supported, RgbBgraResampler.IsSupportedChannelCount(channels));
    }

    [Fact]
    public void ValidateSourceLength_UnsupportedChannelCount_ThrowsInvalidData()
    {
        Assert.Throws<System.IO.InvalidDataException>(() => RgbBgraResampler.ValidateSourceLength(10, 10, 1000, channels: 4));
    }
}
