using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rede.Core.Protocol;

public static class ProtocolSerializer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Creates a client message (no ts — server sets it).
    /// Mirrors: createClientMessage(type, payload) in protocol.js
    /// Payload fields are spread first, then v/type override.
    /// </summary>
    public static string CreateClientMessage(string type, JsonObject? payload = null)
    {
        var obj = payload ?? new JsonObject();
        // Ensure v and type override any payload fields (spread payload first, then set v/type)
        obj["v"] = Msg.ProtocolVersion;
        obj["type"] = type;
        return obj.ToJsonString(JsonOpts);
    }

    /// <summary>
    /// Parse a raw JSON string into a JsonObject. Returns null on invalid JSON.
    /// Mirrors: parseMessage(raw) in protocol.js
    /// </summary>
    public static JsonObject? Parse(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            return node?.AsObject();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get the message type from a parsed message.
    /// </summary>
    public static string? GetType(JsonObject msg)
    {
        return msg["type"]?.GetValue<string>();
    }

    /// <summary>
    /// Get a string field from a message, or null.
    /// </summary>
    public static string? GetString(JsonObject msg, string field)
    {
        return msg[field]?.GetValue<string>();
    }

    /// <summary>
    /// Get an int field from a message, or default.
    /// </summary>
    public static int GetInt(JsonObject msg, string field, int defaultValue = 0)
    {
        var node = msg[field];
        if (node is null) return defaultValue;
        try { return node.GetValue<int>(); }
        catch { return defaultValue; }
    }

    /// <summary>
    /// Get a long field (for timestamps).
    /// </summary>
    public static long GetLong(JsonObject msg, string field, long defaultValue = 0)
    {
        var node = msg[field];
        if (node is null) return defaultValue;
        try { return node.GetValue<long>(); }
        catch { return defaultValue; }
    }

    /// <summary>
    /// Helper to build a JsonObject from key-value pairs.
    /// </summary>
    public static JsonObject Payload(params (string key, JsonNode? value)[] fields)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in fields)
        {
            obj[key] = value?.DeepClone();
        }
        return obj;
    }
}
