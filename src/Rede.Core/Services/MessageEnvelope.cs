using System.Text.Json;
using System.Text.Json.Nodes;
using Rede.Core.Storage;

namespace Rede.Core.Services;

/// <summary>
/// Encodes/decodes the JSON envelope used inside Sender Keys ciphertext.
/// Backward-compatible: if the plaintext is not JSON with a "t" key,
/// it's treated as a legacy plain-text message.
/// </summary>
public static class MessageEnvelope
{
    /// <summary>
    /// Encode a message with optional reply metadata into the envelope format.
    /// Returns plain text if no structured data is needed (keeps wire compat with older clients).
    /// </summary>
    public static string Encode(string text, string? replyToMsgId = null,
        string? replyToPreview = null, string? replyToAuthor = null,
        List<AttachmentInfo>? attachments = null)
    {
        if (replyToMsgId is null && (attachments is null || attachments.Count == 0))
            return text; // No envelope needed — plain string for backward compat

        var obj = new JsonObject { ["t"] = text };
        if (replyToMsgId is not null) obj["ref"] = replyToMsgId;
        if (replyToPreview is not null) obj["rp"] = replyToPreview[..Math.Min(replyToPreview.Length, 100)];
        if (replyToAuthor is not null) obj["ra"] = replyToAuthor;
        if (attachments is not null && attachments.Count > 0)
        {
            var attArr = new JsonArray();
            foreach (var att in attachments)
            {
                attArr.Add(new JsonObject
                {
                    ["bid"] = att.BlobId,
                    ["key"] = Convert.ToBase64String(att.Key),
                    ["nonce"] = Convert.ToBase64String(att.Nonce),
                    ["name"] = att.Name,
                    ["mime"] = att.MimeType,
                    ["size"] = att.Size,
                    ["chunks"] = att.ChunkCount,
                });
            }
            obj["att"] = attArr;
        }
        return obj.ToJsonString();
    }

    /// <summary>
    /// Decode a plaintext into text + optional structured metadata.
    /// Returns the message text. Populates out parameters if envelope data is present.
    /// </summary>
    public static string Decode(string plaintext, out string? replyToMsgId,
        out string? replyToPreview, out string? replyToAuthor,
        out List<AttachmentInfo>? attachments)
    {
        replyToMsgId = null;
        replyToPreview = null;
        replyToAuthor = null;
        attachments = null;

        if (string.IsNullOrEmpty(plaintext) || plaintext[0] != '{')
            return plaintext;

        try
        {
            var obj = JsonNode.Parse(plaintext);
            if (obj is JsonObject jo && jo.ContainsKey("t"))
            {
                var text = jo["t"]?.GetValue<string>() ?? "";
                replyToMsgId = jo["ref"]?.GetValue<string>();
                replyToPreview = jo["rp"]?.GetValue<string>();
                replyToAuthor = jo["ra"]?.GetValue<string>();

                if (jo["att"] is JsonArray attArr && attArr.Count > 0)
                {
                    attachments = new List<AttachmentInfo>();
                    foreach (var node in attArr)
                    {
                        if (node is not JsonObject attObj) continue;
                        var keyStr = attObj["key"]?.GetValue<string>() ?? "";
                        var nonceStr = attObj["nonce"]?.GetValue<string>() ?? "";
                        attachments.Add(new AttachmentInfo
                        {
                            BlobId = attObj["bid"]?.GetValue<string>() ?? "",
                            Key = string.IsNullOrEmpty(keyStr) ? Array.Empty<byte>() : Convert.FromBase64String(keyStr),
                            Nonce = string.IsNullOrEmpty(nonceStr) ? Array.Empty<byte>() : Convert.FromBase64String(nonceStr),
                            Name = attObj["name"]?.GetValue<string>() ?? "",
                            MimeType = attObj["mime"]?.GetValue<string>(),
                            Size = attObj["size"]?.GetValue<long>() ?? 0,
                            ChunkCount = attObj["chunks"]?.GetValue<int>() ?? 0,
                        });
                    }
                }

                return text;
            }
        }
        catch { /* not JSON or not our envelope — treat as plain text */ }

        return plaintext;
    }

    /// <summary>Backward-compatible overload without attachments.</summary>
    public static string Decode(string plaintext, out string? replyToMsgId,
        out string? replyToPreview, out string? replyToAuthor)
    {
        return Decode(plaintext, out replyToMsgId, out replyToPreview, out replyToAuthor, out _);
    }

    /// <summary>
    /// Check if a plaintext is a control message (not a user-visible message).
    /// </summary>
    public static bool IsControlMessage(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || plaintext[0] != '{')
            return false;
        try
        {
            var obj = JsonNode.Parse(plaintext);
            return obj is JsonObject jo && jo.ContainsKey("__rede_ctrl");
        }
        catch { return false; }
    }

    /// <summary>
    /// Try to parse a control message. Returns the ctrl type and the full JsonObject, or null.
    /// </summary>
    public static (string ctrl, JsonObject obj)? TryParseControl(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || plaintext[0] != '{')
            return null;
        try
        {
            var node = JsonNode.Parse(plaintext);
            if (node is JsonObject jo && jo["__rede_ctrl"]?.GetValue<string>() is string ctrl)
                return (ctrl, jo);
        }
        catch { }
        return null;
    }

    /// <summary>Encode a reaction control message.</summary>
    public static string EncodeReaction(string msgId, string emoji, bool add)
        => new JsonObject
        {
            ["__rede_ctrl"] = "reaction",
            ["mid"] = msgId,
            ["emoji"] = emoji,
            ["action"] = add ? "add" : "remove",
        }.ToJsonString();

    /// <summary>Encode an edit control message.</summary>
    public static string EncodeEdit(string msgId, string newText)
        => new JsonObject
        {
            ["__rede_ctrl"] = "edit",
            ["mid"] = msgId,
            ["newText"] = newText,
        }.ToJsonString();

    /// <summary>Encode a delete control message.</summary>
    public static string EncodeDelete(string msgId)
        => new JsonObject
        {
            ["__rede_ctrl"] = "delete",
            ["mid"] = msgId,
        }.ToJsonString();
}
