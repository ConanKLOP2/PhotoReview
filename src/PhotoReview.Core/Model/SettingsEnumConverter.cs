using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// <see cref="LenientEnumConverter{T}"/> for the <c>AppSettings</c> enum properties whose default is NOT the enum's zero
/// member (RV-S01). Unparsable input (unknown text, "", null, an undefined number, an object) yields an undefined
/// sentinel instead of <c>default(T)</c>, so <c>SettingsNormalizer</c> resets the property to the <c>AppSettings</c>
/// default and lists it in <c>LastLoadRepairs</c>. Registered per property, so journal/metrics enum reads keep the
/// plain lenient behaviour.
/// </summary>
public sealed class SettingsEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private readonly LenientEnumConverter<T> _inner = new((T)Enum.ToObject(typeof(T), int.MinValue));

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        _inner.Read(ref reader, typeToConvert, options);

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        _inner.Write(writer, value, options);
}
