using System.Text.Json;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.Model;

public sealed class LenientEnumNumericStringTests
{
    private enum Shade
    {
        Dark = 0,
        Mid = 2,
        Light = 5,
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new LenientEnumConverter<Shade>(Shade.Light) },
    };

    [Fact(DisplayName = "A JSON string holding a defined enum number is read as that value, an undefined number as the default")]
    public void Read_NumericString_ResolvesDefinedValue()
    {
        Assert.Equal(Shade.Mid, JsonSerializer.Deserialize<Shade>("\"2\"", Options));
        Assert.Equal(Shade.Dark, JsonSerializer.Deserialize<Shade>("\" 0 \"", Options));
        Assert.Equal(Shade.Light, JsonSerializer.Deserialize<Shade>("\"3\"", Options));
    }

    [Fact(DisplayName = "A dictionary key holding a defined enum number is read as that value")]
    public void ReadAsPropertyName_NumericKey_ResolvesDefinedValue()
    {
        var map = JsonSerializer.Deserialize<Dictionary<Shade, int>>("""{ "2": 7 }""", Options);

        Assert.NotNull(map);
        Assert.Equal(7, map[Shade.Mid]);
    }
}
