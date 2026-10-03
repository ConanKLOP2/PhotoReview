using PhotoReview.Imaging.Raw;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

public sealed class Cr3AndRafReaderTests
{
    [Fact]
    public void Cr3Reader_Synthetic_CanReadPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var data = SyntheticRawBuilder.BuildIsoBmff(jpeg);

        var registry = new RawContainerReaderRegistry();
        var reader = registry.FindReader(data.AsSpan(0, Math.Min(64, data.Length)), ".cr3");
        Assert.NotNull(reader);

        var headerSource = new InMemoryRawHeaderSource(data);
        var info = reader.Read(headerSource, CancellationToken.None);

        Assert.Equal(RawFormat.Cr3, info.Format);
        Assert.Single(info.Previews);
        Assert.Equal(jpeg.Length, info.Previews[0].Length);
    }

    [Fact]
    public void RafReader_Synthetic_CanReadPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var data = SyntheticRawBuilder.BuildRaf(jpeg);

        var registry = new RawContainerReaderRegistry();
        var reader = registry.FindReader(data.AsSpan(0, Math.Min(64, data.Length)), ".raf");
        Assert.NotNull(reader);

        var headerSource = new InMemoryRawHeaderSource(data);
        var info = reader.Read(headerSource, CancellationToken.None);

        Assert.Equal(RawFormat.Raf, info.Format);
        Assert.Single(info.Previews);
        Assert.Equal(160, info.Previews[0].Offset);
        Assert.Equal(jpeg.Length, info.Previews[0].Length);
    }
}
