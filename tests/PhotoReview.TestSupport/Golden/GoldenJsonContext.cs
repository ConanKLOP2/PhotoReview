using System.Text.Json.Serialization;

namespace PhotoReview.TestSupport.Golden;

/// <summary>
/// Source-generated serializer của file golden (WP-10, C-17): phù hợp tinh thần AOT của kế hoạch và đảm bảo cùng một biểu diễn
/// khi bản WPF ghi và bản Win32 đọc. Tên thuộc tính theo PascalCase của record (không đổi tên), enum đã là chuỗi trong DTO.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(GoldenDocument<GoldenViewportCase>))]
[JsonSerializable(typeof(GoldenDocument<GoldenInputScript>))]
[JsonSerializable(typeof(GoldenDocument<GoldenKeyName>))]
[JsonSerializable(typeof(GoldenDocument<GoldenMenuCase>))]
[JsonSerializable(typeof(GoldenDocument<GoldenOverlayBox>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(GoldenViewportCase))]
[JsonSerializable(typeof(GoldenInputScript))]
[JsonSerializable(typeof(GoldenKeyName))]
[JsonSerializable(typeof(GoldenMenuCase))]
[JsonSerializable(typeof(GoldenOverlayBox))]
public sealed partial class GoldenJsonContext : JsonSerializerContext
{
}
