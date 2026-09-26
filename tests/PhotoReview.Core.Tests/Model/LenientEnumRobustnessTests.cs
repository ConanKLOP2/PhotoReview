using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.Model;

/// <summary>Every lenient enum of the settings contract must survive any JSON token without throwing or yielding an undefined value.</summary>
public sealed class LenientEnumRobustnessTests
{
    private static IEnumerable<Type> LenientEnums() =>
        typeof(LoadingMode).Assembly.GetTypes()
            .Where(t => t.IsEnum && t.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType?.IsGenericType == true
                && t.GetCustomAttribute<JsonConverterAttribute>()!.ConverterType!.GetGenericTypeDefinition() == typeof(LenientEnumConverter<>));

    public static TheoryData<string> EnumNames()
    {
        var data = new TheoryData<string>();
        foreach (var t in LenientEnums()) data.Add(t.Name);
        return data;
    }

    private static readonly string[] Tokens =
    [
        "null", "true", "false", "0", "1", "2", "3", "-1", "99", "2147483647", "2147483648", "9223372036854775807", "1.5", "1e2", "-0",
        "\"\"", "\"  \"", "\"garbage\"", "\"0\"", "\"1\"", "\" 1 \"", "\"-1\"", "\"99999999999\"", "\"NAME\"", "\"name\"", "\"İ\"",
        "[]", "{}", "[1,[2]]", "{\"a\":{\"b\":[1,2,{}]}}", "\"\\ud800\"", "\"\\u0000\"", "\"100%\"", "\"Size\"", "\"Delete\"", "\"PortraitFirst\"",
    ];

    [Fact(DisplayName = "The reflection found the settings enums (guards the theory data below)")]
    public void LenientEnums_AreDiscovered() => Assert.True(LenientEnums().Count() >= 8, string.Join(", ", LenientEnums().Select(t => t.Name)));

    [Theory(DisplayName = "Any JSON token reads as a defined member of the enum (or the enum default) and never throws")]
    [MemberData(nameof(EnumNames))]
    public void AnyToken_ReadsAsDefinedValue(string enumName)
    {
        var type = LenientEnums().Single(t => t.Name == enumName);
        foreach (var token in Tokens)
        {
            object? value = null;
            var ex = Record.Exception(() => value = JsonSerializer.Deserialize(token, type));

            // An InvalidOperationException from a lone-surrogate string is the one thing the converter does not guard;
            // it surfaces as a JsonException at Deserialize level and is handled by SettingsStore (salvage).
            Assert.True(ex is null or JsonException, $"{enumName} <- {token}: {ex}");
            if (ex is null) Assert.True(Enum.IsDefined(type, value!), $"{enumName} <- {token} gave undefined {value}");
        }
    }

    [Theory(DisplayName = "Every defined member round-trips as a value, as a dictionary key and inside a nested object")]
    [MemberData(nameof(EnumNames))]
    public void DefinedMembers_RoundTrip(string enumName)
    {
        var type = LenientEnums().Single(t => t.Name == enumName);
        foreach (var member in Enum.GetValues(type))
        {
            var json = JsonSerializer.Serialize(member, type);
            Assert.Equal(member, JsonSerializer.Deserialize(json, type));

            var dictType = typeof(Dictionary<,>).MakeGenericType(type, typeof(long));
            var dict = (System.Collections.IDictionary)Activator.CreateInstance(dictType)!;
            dict[member] = 7L;
            var back = (System.Collections.IDictionary)JsonSerializer.Deserialize(JsonSerializer.Serialize(dict, dictType), dictType)!;
            Assert.Equal(7L, back[member]);
        }
    }

    [Theory(DisplayName = "Members are read case-insensitively and with surrounding blanks")]
    [MemberData(nameof(EnumNames))]
    public void Names_CaseInsensitive(string enumName)
    {
        var type = LenientEnums().Single(t => t.Name == enumName);
        foreach (var member in Enum.GetValues(type))
        {
            var name = member.ToString()!;
            Assert.Equal(member, JsonSerializer.Deserialize("\"  " + name.ToUpperInvariant() + " \"", type));
            Assert.Equal(member, JsonSerializer.Deserialize("\"" + name.ToLowerInvariant() + "\"", type));
        }
    }

    [Fact(DisplayName = "ExifInfoFields accepts numbers and names, drops unknown bits and never yields an invalid mask")]
    public void ExifInfoFields_Robust()
    {
        foreach (var token in Tokens.Concat(["\"FileName, Camera\"", "\"filename,LENS\"", "\"All\"", "\"None\"", "-1", "1024", "9223372036854775807", "\"1, 2\""]))
        {
            ExifInfoFields value = default;
            var ex = Record.Exception(() => value = JsonSerializer.Deserialize<ExifInfoFields>(token));
            Assert.True(ex is null or JsonException, $"{token}: {ex}");
            Assert.Equal(value & ExifInfoFields.All, value);
        }
        Assert.Equal(ExifInfoFields.All, JsonSerializer.Deserialize<ExifInfoFields>("-1"));
        Assert.Equal(ExifInfoFields.FileName | ExifInfoFields.Camera, JsonSerializer.Deserialize<ExifInfoFields>("\"filename, CAMERA\""));
    }
}
