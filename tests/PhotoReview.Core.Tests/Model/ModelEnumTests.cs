using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.Core.Tests.Model;

[Trait("Category", "HotPath")]
public class ModelEnumTests
{
    [Fact]
    public void LoadingModeValuesAndNamesMatchSpecification()
    {
        var values = Enum.GetValues<LoadingMode>();
        Assert.Equal(3, values.Length);
        Assert.Equal(LoadingMode.Fast, values[0]);
        Assert.Equal(LoadingMode.Preview, values[1]);
        Assert.Equal(LoadingMode.Original, values[2]);
    }

    [Fact]
    public void ImageSortModeValuesAndNamesMatchSpecification()
    {
        var values = Enum.GetValues<ImageSortMode>();
        Assert.Equal(3, values.Length);
        Assert.Equal(ImageSortMode.Name, values[0]);
        Assert.Equal(ImageSortMode.SizeDescending, values[1]);
        Assert.Equal(ImageSortMode.SizeAscending, values[2]);
    }

    [Fact]
    public void InitialViewModeValuesAndNamesMatchSpecification()
    {
        var values = Enum.GetValues<InitialViewMode>();
        Assert.Equal(4, values.Length);
        Assert.Equal(InitialViewMode.Fit, values[0]);
        Assert.Equal(InitialViewMode.Percent100, values[1]);
        Assert.Equal(InitialViewMode.Percent200, values[2]);
        Assert.Equal(InitialViewMode.Percent400, values[3]);
    }

    [Fact]
    public void FileOperationTypeValuesAndNamesMatchSpecification()
    {
        var values = Enum.GetValues<FileOperationType>();
        Assert.Equal(3, values.Length);
        Assert.Equal(FileOperationType.Move, values[0]);
        Assert.Equal(FileOperationType.Copy, values[1]);
        Assert.Equal(FileOperationType.Recycle, values[2]);
    }

    [Fact]
    public void JournalStateValuesAndNamesMatchSpecification()
    {
        var values = Enum.GetValues<JournalState>();
        Assert.Equal(4, values.Length);
        Assert.Equal(JournalState.Prepared, values[0]);
        Assert.Equal(JournalState.Committed, values[1]);
        Assert.Equal(JournalState.Failed, values[2]);
        Assert.Equal(JournalState.Dismissed, values[3]);
    }

    [Fact]
    public void DecoderBackendValuesAndNamesMatchSpecification()
    {
        var values = Enum.GetValues<DecoderBackend>();
        Assert.Equal(3, values.Length);
        Assert.Equal(DecoderBackend.Wpf, values[0]);
        Assert.Equal(DecoderBackend.WicDirect, values[1]);
        Assert.Equal(DecoderBackend.TurboJpeg, values[2]);
    }

    [Fact]
    public void ScalingQualityValuesAndNamesMatchSpecification()
    {
        var values = Enum.GetValues<ScalingQuality>();
        Assert.Equal(2, values.Length);
        Assert.Equal(ScalingQuality.Linear, values[0]);
        Assert.Equal(ScalingQuality.HighQuality, values[1]);
    }
}

