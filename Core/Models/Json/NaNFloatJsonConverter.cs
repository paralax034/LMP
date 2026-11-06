using System.Text.Json;
using System.Text.Json.Serialization;

namespace LMP.Core.Models.Json;

/// <summary>
/// JSON converter for float that serializes NaN/Infinity as null,
/// and interprets null as <see cref="float.NaN"/> during deserialization.
/// </summary>
public sealed class NaNFloatJsonConverter : JsonConverter<float>
{
    /// <inheritdoc />
    public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => float.NaN,
            JsonTokenType.Number => reader.GetSingle(),
            JsonTokenType.String => ParseString(reader.GetString()),
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for float.")
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
    {
        if (!float.IsFinite(value) || float.IsNaN(value))
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteNumberValue(value);
    }

    private static float ParseString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return float.NaN;

        if (string.Equals(value, "NaN", StringComparison.OrdinalIgnoreCase))
            return float.NaN;

        if (string.Equals(value, "Infinity", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "+Infinity", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "-Infinity", StringComparison.OrdinalIgnoreCase))
            return float.NaN;

        return float.TryParse(value, out var result) ? result : float.NaN;
    }
}