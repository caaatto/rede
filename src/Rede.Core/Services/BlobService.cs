using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Rede.Core.Networking;
using Rede.Core.Protocol;
using Rede.Core.Storage;
using Sodium;

namespace Rede.Core.Services;

/// <summary>
/// Encrypted blob upload/download for file attachments.
/// Blobs are AES-encrypted client-side — server stores opaque data.
/// Max blob size: 10MB.
/// </summary>
public class BlobService : IDisposable
{
    public const int MaxBlobSize = 10 * 1024 * 1024; // 10MB

    private readonly RedeConnection _conn;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _uploadPending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]?>> _fetchPending = new();

    // Local blob cache (blobId → decrypted bytes) to avoid re-fetching
    private readonly ConcurrentDictionary<string, byte[]> _cache = new();

    public event Action<string>? OnSystemMessage;

    public Profile? Profile { get; set; }
    public byte[]? Passphrase { get; set; }

    public BlobService(RedeConnection conn)
    {
        _conn = conn;
        _conn.On(Msg.BlobUploadOk, HandleBlobUploadOk);
        _conn.On(Msg.BlobUploadFail, HandleBlobUploadFail);
        _conn.On(Msg.BlobData, HandleBlobData);
        _conn.On(Msg.BlobDataFail, HandleBlobDataFail);
    }

    public void Dispose() { GC.SuppressFinalize(this); }

    /// <summary>
    /// Encrypt and upload a file. Returns the AttachmentInfo to embed in the message envelope.
    /// </summary>
    public async Task<AttachmentInfo?> UploadAsync(string fileName, string? mimeType, byte[] plainData)
    {
        if (plainData.Length > MaxBlobSize)
        {
            OnSystemMessage?.Invoke($"File too large ({plainData.Length / 1024}KB). Max: {MaxBlobSize / 1024 / 1024}MB.");
            return null;
        }

        // Generate per-blob encryption key + nonce
        var key = SecretBox.GenerateKey();
        var nonce = SecretBox.GenerateNonce();
        var cipherData = SecretBox.Create(plainData, nonce, key);

        // Generate blob ID
        var blobId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // Upload as base64 (simple approach — avoids binary WS frame complexity for now)
        var tcs = new TaskCompletionSource<bool>();
        _uploadPending[blobId] = tcs;

        _conn.Send(Msg.BlobUpload, ProtocolSerializer.Payload(
            ("blobId", JsonValue.Create(blobId)),
            ("size", JsonValue.Create(cipherData.Length)),
            ("data", JsonValue.Create(Convert.ToBase64String(cipherData)))
        ));

        // Wait for server confirmation (timeout 30s)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => tcs.TrySetResult(false));

        var success = await tcs.Task;
        _uploadPending.TryRemove(blobId, out _);

        if (!success)
        {
            OnSystemMessage?.Invoke("Upload failed.");
            return null;
        }

        // Cache the plaintext locally
        _cache[blobId] = plainData;

        return new AttachmentInfo
        {
            BlobId = blobId,
            Key = Convert.ToBase64String(key),
            Nonce = Convert.ToBase64String(nonce),
            Name = fileName,
            MimeType = mimeType,
            Size = plainData.Length,
        };
    }

    /// <summary>
    /// Fetch and decrypt a blob by its attachment info. Returns decrypted bytes or null.
    /// </summary>
    public async Task<byte[]?> FetchAsync(AttachmentInfo att)
    {
        // Check cache first
        if (_cache.TryGetValue(att.BlobId, out var cached))
            return cached;

        var tcs = new TaskCompletionSource<byte[]?>();
        _fetchPending[att.BlobId] = tcs;

        _conn.Send(Msg.BlobFetch, ProtocolSerializer.Payload(
            ("blobId", JsonValue.Create(att.BlobId))
        ));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => tcs.TrySetResult(null));

        var cipherData = await tcs.Task;
        _fetchPending.TryRemove(att.BlobId, out _);

        if (cipherData is null) return null;

        try
        {
            var key = Convert.FromBase64String(att.Key);
            var nonce = Convert.FromBase64String(att.Nonce);
            var plain = SecretBox.Open(cipherData, nonce, key);
            _cache[att.BlobId] = plain;
            return plain;
        }
        catch
        {
            OnSystemMessage?.Invoke("Failed to decrypt attachment.");
            return null;
        }
    }

    /// <summary>Check if a blob is image-like based on MIME type.</summary>
    public static bool IsImage(AttachmentInfo att)
        => att.MimeType is not null && att.MimeType.StartsWith("image/");

    // --- Handlers ---

    private void HandleBlobUploadOk(JsonObject msg)
    {
        var blobId = ProtocolSerializer.GetString(msg, "blobId");
        if (blobId is not null && _uploadPending.TryGetValue(blobId, out var tcs))
            tcs.TrySetResult(true);
    }

    private void HandleBlobUploadFail(JsonObject msg)
    {
        var blobId = ProtocolSerializer.GetString(msg, "blobId");
        var reason = ProtocolSerializer.GetString(msg, "reason") ?? "unknown";
        if (blobId is not null && _uploadPending.TryGetValue(blobId, out var tcs))
            tcs.TrySetResult(false);
        OnSystemMessage?.Invoke($"Upload failed: {reason}");
    }

    private void HandleBlobData(JsonObject msg)
    {
        var blobId = ProtocolSerializer.GetString(msg, "blobId");
        var dataB64 = ProtocolSerializer.GetString(msg, "data");
        if (blobId is null) return;

        if (_fetchPending.TryGetValue(blobId, out var tcs))
        {
            if (dataB64 is not null)
            {
                try
                {
                    tcs.TrySetResult(Convert.FromBase64String(dataB64));
                }
                catch
                {
                    tcs.TrySetResult(null);
                }
            }
            else
            {
                tcs.TrySetResult(null);
            }
        }
    }

    private void HandleBlobDataFail(JsonObject msg)
    {
        var blobId = ProtocolSerializer.GetString(msg, "blobId");
        if (blobId is not null && _fetchPending.TryGetValue(blobId, out var tcs))
            tcs.TrySetResult(null);
    }
}
