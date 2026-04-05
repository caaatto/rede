using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rede.Core.Crypto;

/// <summary>
/// Serializes byte[] as base64 string on the wire (JSON/profile disk format)
/// while allowing in-memory storage as byte[] so key material can be zeroed.
/// Replaces the old practice of storing key material as base64 strings in models.
/// </summary>
public sealed class Base64BytesJsonConverter : JsonConverter<byte[]?>
{
    public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected base64 string for byte[] field");
        var s = reader.GetString();
        if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { throw new JsonException("Invalid base64 in byte[] field"); }
    }

    public override void Write(Utf8JsonWriter writer, byte[]? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(Convert.ToBase64String(value));
    }
}
