using System.Text.Json;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.Core.Tests.Model;

public class LenientEnumConverterTests
{
    private static readonly JsonSerializerOptions DefaultOptions = new();

    private static readonly JsonSerializerOptions CustomDefaultOptions = new()
    {
        Converters = { new LenientEnumConverter<LoadingMode>(LoadingMode.Preview) }
    };

    private static readonly JsonSerializerOptions FactoryOptions = new()
    {
        Converters = { new LenientEnumConverterFactory() }
    };

    private sealed record TestConfig(
        LoadingMode LoadingMode = LoadingMode.Fast,
        ImageSortMode ImageSortMode = ImageSortMode.Name,
        InitialViewMode InitialViewMode = InitialViewMode.Fit,
        FileOperationType FileOperation = FileOperationType.Move,
        JournalState JournalState = JournalState.Prepared,
        DecoderBackend DecoderBackend = DecoderBackend.Wpf,
        ScalingQuality ScalingQuality = ScalingQuality.Linear
    );

    [Theory]
    [InlineData("\"Preview\"", LoadingMode.Preview)]
    [InlineData("\"preview\"", LoadingMode.Preview)]
    [InlineData("\"PREVIEW\"", LoadingMode.Preview)]
    [InlineData("\"Original\"", LoadingMode.Original)]
    [InlineData("\"original\"", LoadingMode.Original)]
    [InlineData("\"Fast\"", LoadingMode.Fast)]
    [InlineData("\"fast\"", LoadingMode.Fast)]
    public void ReadLoadingModeMatchesStandardNamesCaseInsensitive(string json, LoadingMode expected)
    {
        var result = JsonSerializer.Deserialize<LoadingMode>(json, DefaultOptions);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\"Size\"", ImageSortMode.SizeDescending)]
    [InlineData("\"size\"", ImageSortMode.SizeDescending)]
    [InlineData("\"PortraitFirst\"", ImageSortMode.Name)]
    [InlineData("\"portraitfirst\"", ImageSortMode.Name)]
    [InlineData("\"Name\"", ImageSortMode.Name)]
    [InlineData("\"SizeAscending\"", ImageSortMode.SizeAscending)]
    public void ReadImageSortModeResolvesAliasesAndNames(string json, ImageSortMode expected)
    {
        var result = JsonSerializer.Deserialize<ImageSortMode>(json, DefaultOptions);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\"Delete\"", FileOperationType.Recycle)]
    [InlineData("\"delete\"", FileOperationType.Recycle)]
    [InlineData("\"Recycle\"", FileOperationType.Recycle)]
    [InlineData("\"Move\"", FileOperationType.Move)]
    [InlineData("\"Copy\"", FileOperationType.Copy)]
    public void ReadFileOperationTypeResolvesDeleteAliasToRecycle(string json, FileOperationType expected)
    {
        var result = JsonSerializer.Deserialize<FileOperationType>(json, DefaultOptions);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\"Fit\"", InitialViewMode.Fit)]
    [InlineData("\"fit\"", InitialViewMode.Fit)]
    [InlineData("\"100%\"", InitialViewMode.Percent100)]
    [InlineData("\"200%\"", InitialViewMode.Percent200)]
    [InlineData("\"400%\"", InitialViewMode.Percent400)]
    [InlineData("\"Percent100\"", InitialViewMode.Percent100)]
    [InlineData("\"percent200\"", InitialViewMode.Percent200)]
    [InlineData("\"Percent400\"", InitialViewMode.Percent400)]
    public void ReadInitialViewModeResolvesPercentageAliasesAndNames(string json, InitialViewMode expected)
    {
        var result = JsonSerializer.Deserialize<InitialViewMode>(json, DefaultOptions);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\"Prepared\"", JournalState.Prepared)]
    [InlineData("\"Committed\"", JournalState.Committed)]
    [InlineData("\"Failed\"", JournalState.Failed)]
    [InlineData("\"committed\"", JournalState.Committed)]
    public void ReadJournalStateResolvesCorrectly(string json, JournalState expected)
    {
        var result = JsonSerializer.Deserialize<JournalState>(json, DefaultOptions);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\"Wpf\"", DecoderBackend.Wpf)]
    [InlineData("\"WicDirect\"", DecoderBackend.WicDirect)]
    [InlineData("\"TurboJpeg\"", DecoderBackend.TurboJpeg)]
    [InlineData("\"turbojpeg\"", DecoderBackend.TurboJpeg)]
    public void ReadDecoderBackendResolvesCorrectly(string json, DecoderBackend expected)
    {
        var result = JsonSerializer.Deserialize<DecoderBackend>(json, DefaultOptions);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\"Linear\"", ScalingQuality.Linear)]
    [InlineData("\"HighQuality\"", ScalingQuality.HighQuality)]
    [InlineData("\"highquality\"", ScalingQuality.HighQuality)]
    public void ReadScalingQualityResolvesCorrectly(string json, ScalingQuality expected)
    {
        var result = JsonSerializer.Deserialize<ScalingQuality>(json, DefaultOptions);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"UnknownValue\"")]
    [InlineData("\"Gibberish123!@#\"")]
    [InlineData("null")]
    public void ReadUnrecognizedOrEmptyFallsBackToDefault(string json)
    {
        var loadingMode = JsonSerializer.Deserialize<LoadingMode>(json, DefaultOptions);
        Assert.Equal(LoadingMode.Fast, loadingMode);

        var sortMode = JsonSerializer.Deserialize<ImageSortMode>(json, DefaultOptions);
        Assert.Equal(ImageSortMode.Name, sortMode);

        var viewMode = JsonSerializer.Deserialize<InitialViewMode>(json, DefaultOptions);
        Assert.Equal(InitialViewMode.Fit, viewMode);

        var opType = JsonSerializer.Deserialize<FileOperationType>(json, DefaultOptions);
        Assert.Equal(FileOperationType.Move, opType);
    }

    [Fact]
    public void ReadCustomDefaultValueReturnsSpecifiedDefault()
    {
        var result = JsonSerializer.Deserialize<LoadingMode>("\"InvalidTrashValue\"", CustomDefaultOptions);
        Assert.Equal(LoadingMode.Preview, result);
    }

    [Theory]
    [InlineData("0", LoadingMode.Fast)]
    [InlineData("1", LoadingMode.Preview)]
    [InlineData("2", LoadingMode.Original)]
    public void ReadValidIntegerMapsCorrectly(string json, LoadingMode expected)
    {
        var result = JsonSerializer.Deserialize<LoadingMode>(json, DefaultOptions);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    public void ReadInvalidIntegerFallsBackToDefault(string json)
    {
        var result = JsonSerializer.Deserialize<LoadingMode>(json, DefaultOptions);
        Assert.Equal(LoadingMode.Fast, result);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{\"foo\": \"bar\"}")]
    [InlineData("[1, 2, 3]")]
    public void ReadUnexpectedJsonTokensDoesNotCrashAndFallsBackToDefault(string json)
    {
        var result = JsonSerializer.Deserialize<LoadingMode>(json, DefaultOptions);
        Assert.Equal(LoadingMode.Fast, result);
    }

    [Fact]
    public void WriteInitialViewModeOutputsLegacyCompatibleStrings()
    {
        Assert.Equal("\"Fit\"", JsonSerializer.Serialize(InitialViewMode.Fit, DefaultOptions));
        Assert.Equal("\"100%\"", JsonSerializer.Serialize(InitialViewMode.Percent100, DefaultOptions));
        Assert.Equal("\"200%\"", JsonSerializer.Serialize(InitialViewMode.Percent200, DefaultOptions));
        Assert.Equal("\"400%\"", JsonSerializer.Serialize(InitialViewMode.Percent400, DefaultOptions));
    }

    [Fact]
    public void WriteOtherEnumsOutputsStandardPascalCaseNames()
    {
        Assert.Equal("\"Preview\"", JsonSerializer.Serialize(LoadingMode.Preview, DefaultOptions));
        Assert.Equal("\"SizeDescending\"", JsonSerializer.Serialize(ImageSortMode.SizeDescending, DefaultOptions));
        Assert.Equal("\"Recycle\"", JsonSerializer.Serialize(FileOperationType.Recycle, DefaultOptions));
        Assert.Equal("\"Committed\"", JsonSerializer.Serialize(JournalState.Committed, DefaultOptions));
        Assert.Equal("\"TurboJpeg\"", JsonSerializer.Serialize(DecoderBackend.TurboJpeg, DefaultOptions));
        Assert.Equal("\"HighQuality\"", JsonSerializer.Serialize(ScalingQuality.HighQuality, DefaultOptions));
    }

    [Fact]
    public void RoundTripRecordWithAllEnumsPreservesValues()
    {
        var original = new TestConfig(
            LoadingMode: LoadingMode.Original,
            ImageSortMode: ImageSortMode.SizeAscending,
            InitialViewMode: InitialViewMode.Percent200,
            FileOperation: FileOperationType.Recycle,
            JournalState: JournalState.Committed,
            DecoderBackend: DecoderBackend.TurboJpeg,
            ScalingQuality: ScalingQuality.HighQuality
        );

        var json = JsonSerializer.Serialize(original, DefaultOptions);
        var restored = JsonSerializer.Deserialize<TestConfig>(json, DefaultOptions);

        Assert.NotNull(restored);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void FactoryWhenRegisteredInOptionsAppliesToEnumsWithoutDirectAttribute()
    {
        var result = JsonSerializer.Deserialize<LoadingMode>("\"preview\"", FactoryOptions);
        Assert.Equal(LoadingMode.Preview, result);
    }
}
