using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Bộ chuyển đổi JSON tha thứ (lenient) cho kiểu enum:
/// - Đọc không phân biệt hoa thường.
/// - Nhận alias qua <see cref="JsonAliasAttribute"/>.
/// - Trả về giá trị mặc định khi gặp chuỗi không nhận dạng được, chuỗi rỗng, null hoặc cấu trúc rác.
/// - Khi ghi: chuyển <see cref="InitialViewMode"/> thành "Fit", "100%", "200%", "400%" để tương thích ngược; các enum khác ghi tên PascalCase chuẩn.
/// </summary>
/// <typeparam name="T">Kiểu enum cần chuyển đổi.</typeparam>
public sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<string, T> LookupTable = BuildLookupTable();
    private readonly T _defaultValue;

    public LenientEnumConverter() : this(default)
    {
    }

    public LenientEnumConverter(T defaultValue)
    {
        _defaultValue = defaultValue;
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return ParseText(reader.GetString());

            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var intVal) && Enum.IsDefined(typeof(T), intVal))
                {
                    return (T)(object)intVal;
                }

                return _defaultValue;

            case JsonTokenType.Null:
                return _defaultValue;

            default:
                // Đối với token bất thường (StartObject, StartArray, True, False...), bỏ qua để không crash
                reader.Skip();
                return _defaultValue;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(ToText(value));
    }

    // Dictionary keys (e.g. ReviewMetricsSnapshot.DecoderFallbacks: Dictionary<DecoderBackend, long>).
    // Without these overrides System.Text.Json throws NotSupportedException as soon as such a
    // dictionary is non-empty.
    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ParseText(reader.GetString());

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(ToText(value));
    }

    private T ParseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return _defaultValue;
        }

        var trimmed = text.Trim();
        if (LookupTable.TryGetValue(trimmed, out var value))
        {
            return value;
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt)
            && Enum.IsDefined(typeof(T), parsedInt))
        {
            return (T)(object)parsedInt;
        }

        return _defaultValue;
    }

    private static string ToText(T value)
    {
        if (typeof(T) == typeof(InitialViewMode))
        {
            return (InitialViewMode)(object)value switch
            {
                InitialViewMode.Fit => "Fit",
                InitialViewMode.Percent100 => "100%",
                InitialViewMode.Percent200 => "200%",
                InitialViewMode.Percent400 => "400%",
                _ => value.ToString()
            };
        }

        return value.ToString();
    }

    private static Dictionary<string, T> BuildLookupTable()
    {
        var dict = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        // 1. Quét tất cả giá trị enum và attribute [JsonAlias]
        var fields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static);
        foreach (var field in fields)
        {
            if (field.GetValue(null) is T enumVal)
            {
                // Tên chuẩn của enum
                dict[field.Name] = enumVal;

                // Các alias qua attribute [JsonAlias]
                var aliasAttrs = field.GetCustomAttributes<JsonAliasAttribute>(inherit: false);
                foreach (var attr in aliasAttrs)
                {
                    foreach (var alias in attr.Aliases)
                    {
                        if (!string.IsNullOrWhiteSpace(alias))
                        {
                            dict[alias.Trim()] = enumVal;
                        }
                    }
                }
            }
        }

        return dict;
    }
}

/// <summary>
/// Factory đăng ký <see cref="LenientEnumConverter{T}"/> cho tất cả các kiểu enum khi cấu hình <see cref="JsonSerializerOptions"/>.
/// </summary>
public sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return typeToConvert.IsEnum;
    }

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        var converterType = typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter?)Activator.CreateInstance(converterType);
    }
}
