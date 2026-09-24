using System.IO;
using PhotoReview.Core.Localization;

namespace PhotoReview.Core.Tests.Localization;

/// <summary>
/// The source-generated <see cref="Tr"/> API (L01, from en.json) resolves through <see cref="Localizer.Current"/>.
/// Localizer.Current is process-wide, so these tests run in the serial GlobalState collection and restore it.
/// </summary>
[Collection("GlobalState")]
public sealed class GeneratedTrTests : IDisposable
{
    private readonly Localizer _previous = Localizer.Current;

    public void Dispose() => Localizer.SetCurrent(_previous);

    [Fact]
    public void TestSample_English_ReturnsEnglishText()
    {
        Localizer.SetCurrent(BuiltInCatalog.EnglishLocalizer);

        Assert.Equal("Sample", Tr.TestSample);
        Assert.Equal("test.sample", TrKeys.TestSample);
    }

    [Fact]
    public void TestGreeting_English_FillsPlaceholder()
    {
        Localizer.SetCurrent(BuiltInCatalog.EnglishLocalizer);

        Assert.Equal("Hello An", Tr.TestGreeting("An"));
    }

    [Theory]
    [InlineData(1, "1 item")]
    [InlineData(2, "2 items")]
    public void TestItems_English_PicksPluralForm(long count, string expected)
    {
        Localizer.SetCurrent(BuiltInCatalog.EnglishLocalizer);

        Assert.Equal(expected, Tr.TestItems(count));
        Assert.Equal("test.items.one", TrKeys.TestItemsOne);
        Assert.Equal("test.items.other", TrKeys.TestItemsOther);
    }

    [Fact]
    public void GeneratedMembers_Vietnamese_ReturnTranslatedText()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Languages", "vi.json");
        var warnings = new List<string>();
        Assert.True(LanguageCatalog.TryParse(File.ReadAllText(path), path, out var vi, warnings), string.Join("; ", warnings));
        Localizer.SetCurrent(Localizer.Create(BuiltInCatalog.English, [vi]));

        Assert.Equal("Mẫu", Tr.TestSample);
        Assert.Equal("Xin chào An", Tr.TestGreeting("An"));
        Assert.Equal("1 mục", Tr.TestItems(1));
        Assert.Equal("2 mục", Tr.TestItems(2));
    }
}
