using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoReview.PerfAnalysis;

/// <summary>summary.json: a percentile of an empty group is NaN in memory; write it as JSON <c>null</c> so consumers always see a number or null, never a string.</summary>
internal sealed class NaNAsNullDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
    }
}
