using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.Core.Tests.Model;

/// <summary>
/// Pins the names and numeric values (0..n-1, in declaration order) of every persisted enum:
/// settings/journal JSON can carry the numeric form (see LenientEnumConverterTests), so reordering
/// or renumbering a member silently changes what old files mean.
/// </summary>
[Trait("Category", "HotPath")]
public class ModelEnumTests
{
    public static TheoryData<Type, string[]> PersistedEnums => new()
    {
        { typeof(LoadingMode), ["Fast", "Preview", "Original"] },
        { typeof(ImageSortMode), ["Name", "SizeDescending", "SizeAscending", "Default", "NameAscending", "NameDescending"] },
        { typeof(InitialViewMode), ["Fit", "Percent100", "Percent200", "Percent400"] },
        { typeof(FileOperationType), ["Move", "Copy", "Recycle"] },
        { typeof(JournalState), ["Prepared", "Committed", "Failed", "Dismissed"] },
        { typeof(DecoderBackend), ["Wpf", "WicDirect", "TurboJpeg"] },
        { typeof(ScalingQuality), ["Linear", "HighQuality"] },
    };

    [Theory]
    [MemberData(nameof(PersistedEnums))]
    public void ValuesAndNamesMatchSpecification(Type enumType, string[] expectedNames)
    {
        var values = Enum.GetValues(enumType).Cast<object>().ToArray();

        Assert.Equal(expectedNames, values.Select(v => Enum.GetName(enumType, v)).ToArray());
        Assert.Equal(Enumerable.Range(0, expectedNames.Length), values.Select(v => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
