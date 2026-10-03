using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Raw.Tiff;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// A preview whose dimensions stay unknown (lossless SOF3 frame) must not score an area of 0: it is ranked by its byte length,
/// so the largest JPEG by bytes is not always beaten by a tiny known thumbnail.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewSelectorUnknownSizeTests
{
    /// <summary>File = small known JPEG + an unresolvable (SOF3) JPEG range of <paramref name="unknownLength"/> bytes.</summary>
    private static (InMemoryRawHeaderSource Source, EmbeddedPreview Known, EmbeddedPreview Unknown) Build(
        int knownWidth, int knownHeight, int unknownLength)
    {
        var known = SyntheticRawBuilder.CreateMinimalJpeg(knownWidth, knownHeight);
        var lossless = TiffBytes.AsLosslessJpeg(SyntheticRawBuilder.CreateMinimalJpeg(6000, 4000));
        var unknownBytes = new byte[unknownLength];
        lossless.CopyTo(unknownBytes, 0);

        var source = new InMemoryRawHeaderSource([.. known, .. unknownBytes]);
        return (source,
            new EmbeddedPreview(0, 0, known.Length, EmbeddedPreviewKind.Jpeg, knownWidth, knownHeight, PreviewColorSpace.Srgb),
            new EmbeddedPreview(1, known.Length, unknownLength, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Srgb));
    }

    [Fact]
    public void SelectPreview_Unbounded_UnknownSizeButByFarLargestJpeg_BeatsATinyKnownThumbnail()
    {
        var (source, known, unknown) = Build(160, 120, unknownLength: 300_000);

        var chosen = PreviewSelector.SelectPreview(source, [known, unknown], DecodeBox.Unbounded, 1);

        Assert.Equal(unknown.Offset, chosen!.Offset);
    }

    [Fact]
    public void SelectPreview_Unbounded_SmallUnknownSizeJpeg_DoesNotBeatALargeKnownPreview()
    {
        var (source, known, unknown) = Build(1620, 1080, unknownLength: 2_000);

        var chosen = PreviewSelector.SelectPreview(source, [known, unknown], DecodeBox.Unbounded, 1);

        Assert.Equal(known.Offset, chosen!.Offset);
    }

    [Fact]
    public void SelectPreview_BoxSmallerThanAKnownPreview_StillPicksTheSmallestKnownMatch()
    {
        var (source, known, unknown) = Build(1620, 1080, unknownLength: 300_000);

        var chosen = PreviewSelector.SelectPreview(source, [known, unknown], new DecodeBox(800, 600), 1);

        Assert.Equal(known.Offset, chosen!.Offset);
    }

    [Fact]
    public void SelectPreview_BoxLargerThanEveryKnownPreview_FallsBackToTheLargestIncludingUnknownSize()
    {
        var (source, known, unknown) = Build(160, 120, unknownLength: 300_000);

        var chosen = PreviewSelector.SelectPreview(source, [known, unknown], new DecodeBox(4000, 3000), 1);

        Assert.Equal(unknown.Offset, chosen!.Offset);
    }

    [Fact]
    public void SelectPreview_Unbounded_HugeUnknownSizeJpeg_NeverOutranksAKnownPreviewOfAtLeast1000Pixels()
    {
        // 7 MB unknown scored 28M (bytes * 4) against 1620x1080 = 1.7M: the unknown entry used to win, fail to decode and
        // leave the viewer with nothing. A known viewable preview always ranks first.
        var (source, known, unknown) = Build(1620, 1080, unknownLength: 7_000_000);

        var chosen = PreviewSelector.SelectPreview(source, [known, unknown], DecodeBox.Unbounded, 1);

        Assert.Equal(known.Offset, chosen!.Offset);
    }

    [Fact]
    public void SelectPreview_TwoUnknownSizeJpegs_PicksTheLargerByBytes()
    {
        var (source, _, unknown) = Build(160, 120, unknownLength: 300_000);
        var smaller = unknown with { Index = 2, Length = 50_000 };

        var chosen = PreviewSelector.SelectPreview(source, [smaller, unknown], DecodeBox.Unbounded, 1);

        Assert.Equal(300_000, chosen!.Length);
    }

    [Fact]
    public void SelectPreview_ThreeUnknownSizePreviews_ResolvesAllSoTheChoiceUsesEveryPixelSize()
    {
        // The byte-largest candidate is NOT the pixel-largest one (heavier compression): stopping after the first walk
        // would pick the wrong preview, so every unknown-size candidate is resolved and reported as resolved for the cache.
        var jpegs = new byte[][]
        {
            [.. SyntheticRawBuilder.CreateMinimalJpeg(2000, 1500), .. new byte[5000]], // largest by bytes, 3 MP
            [.. SyntheticRawBuilder.CreateMinimalJpeg(6000, 4000), .. new byte[1000]], // 24 MP, fewer bytes
            SyntheticRawBuilder.CreateMinimalJpeg(160, 120),
        };
        var data = jpegs.SelectMany(j => j).ToArray();
        var previews = new List<EmbeddedPreview>();
        long offset = 0;
        foreach (var j in jpegs)
        {
            previews.Add(new EmbeddedPreview(previews.Count, offset, j.Length, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Srgb));
            offset += j.Length;
        }

        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(data), previews, DecodeBox.Unbounded, 1, out var resolved);

        Assert.Equal((6000, 4000), (chosen!.Width, chosen.Height));
        Assert.All(resolved, p => Assert.True(p.HeaderResolved && p.Width > 0 && p.Height > 0));
    }
}
