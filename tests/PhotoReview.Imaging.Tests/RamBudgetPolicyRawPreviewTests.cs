using PhotoReview.Core.Catalog;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Preload;
using Xunit;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// C4: a RAW's container size is the SENSOR size, but the decoded image is the embedded JPEG, which can be larger
/// (Canon 7D sRAW2: 2592x1728 sensor, 5184x3456 preview). The folder estimate must not under-count that.
/// </summary>
public sealed class RamBudgetPolicyRawPreviewTests
{
    private const long SRawPreviewBytes = 5184L * 3456 * 4;

    private static CatalogEntry[] SRawEntries(int count) => Enumerable.Range(0, count)
        .Select(i => new CatalogEntry($"sraw-{i:D3}.cr2").WithMetadata(10_000_000 + i, DateTime.UnixEpoch, 2592, 1728))
        .ToArray();

    [Fact]
    public void OriginalMode_SRawWithPreviewLargerThanSensor_IsNotUnderEstimated()
    {
        var estimate = RamBudgetPolicy.EstimateFolderPreviewBytes(SRawEntries(10), DecodeBox.Unbounded);

        Assert.True(estimate >= 10 * SRawPreviewBytes, $"Estimated {estimate} for a folder that decodes to {10 * SRawPreviewBytes}.");
    }

    [Fact]
    public void BoundedBoxLargerThanTheSensor_SRawIsEstimatedAtTheBoxNotTheSensor()
    {
        var box = new DecodeBox(5184, 3456);

        var estimate = RamBudgetPolicy.EstimateFolderPreviewBytes(SRawEntries(10), box);

        Assert.True(estimate >= 10 * SRawPreviewBytes, $"Estimated {estimate}.");
    }

    [Fact]
    public void BoundedBoxSmallerThanTheSensor_IsStillCappedAtTheBox()
    {
        var box = new DecodeBox(1920, 1080);
        var entries = Enumerable.Range(0, 50)
            .Select(i => new CatalogEntry($"raw-{i:D3}.arw").WithMetadata(25_000_000 + i, DateTime.UnixEpoch, 6000, 4000))
            .ToArray();

        var estimate = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, box);

        Assert.InRange(estimate, 50L * 1620 * 1080 * 4, 50L * 1620 * 1080 * 4);
    }

    [Fact]
    public void MeasuredMean_StillOverridesTheGeometryEstimateForRaw()
    {
        var estimate = RamBudgetPolicy.EstimateFolderPreviewBytes(SRawEntries(10), DecodeBox.Unbounded, measuredMeanPreviewBytes: 1_000_000);

        Assert.Equal(10L * 1_250_000, estimate);
    }
}
