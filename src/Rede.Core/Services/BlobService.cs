using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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

    private static readonly string BlobBaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rede", "blobs");

    private readonly RedeConnection _conn;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _uploadPending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]?>> _fetchPending = new();

    // In-memory cache (blobId → decrypted bytes) for hot reads.
    private const int MaxCacheEntries = 100;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new();
    private readonly Queue<string> _cacheOrder = new();

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

        // Cache the plaintext locally + persist the ciphertext to disk so the
        // image survives restarts even if the server has dropped the blob.
        CacheBlob(blobId, plainData);
        TryWriteCipherToDisk(blobId, cipherData);

        var att = new AttachmentInfo
        {
            BlobId = blobId,
            Key = key,
            Nonce = nonce,
            Name = fileName,
            MimeType = mimeType,
            Size = plainData.Length,
        };

        // Zero the local key copy (AttachmentInfo now owns its own copy)
        CryptographicOperations.ZeroMemory(key);

        return att;
    }

    /// <summary>
    /// Fetch and decrypt a blob by its attachment info. Returns decrypted bytes or null.
    /// </summary>
    public async Task<byte[]?> FetchAsync(AttachmentInfo att)
    {
        // Hot in-memory cache first.
        if (_cache.TryGetValue(att.BlobId, out var cached))
            return cached;

        // Local on-disk cache: ciphertext we wrote on a previous send/fetch.
        // The Key/Nonce live in the (passphrase-encrypted) chat history, so
        // ciphertext on disk leaks nothing the server didn't already hold.
        var diskCipher = TryReadCipherFromDisk(att.BlobId);
        if (diskCipher is not null)
        {
            try
            {
                var plain = SecretBox.Open(diskCipher, att.Nonce, att.Key);
                CacheBlob(att.BlobId, plain);
                return plain;
            }
            catch { /* corrupted on disk — fall through to network */ }
        }

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
            var plain = SecretBox.Open(cipherData, att.Nonce, att.Key);
            CacheBlob(att.BlobId, plain);
            // Persist ciphertext locally so we don't have to round-trip the
            // server next time — and so the image still resolves after the
            // server has expired its copy.
            TryWriteCipherToDisk(att.BlobId, cipherData);
            return plain;
        }
        catch
        {
            OnSystemMessage?.Invoke("Failed to decrypt attachment.");
            return null;
        }
    }

    private void CacheBlob(string blobId, byte[] data)
    {
        _cache[blobId] = data;
        _cacheOrder.Enqueue(blobId);
        while (_cacheOrder.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out var evictId))
        {
            if (_cache.TryRemove(evictId, out var evicted))
                CryptographicOperations.ZeroMemory(evicted);
        }
    }

    /// <summary>Check if a blob is image-like based on MIME type.</summary>
    public static bool IsImage(AttachmentInfo att)
        => att.MimeType is not null && att.MimeType.StartsWith("image/");

    private string? GetBlobDir()
    {
        var uid = Profile?.UserId;
        if (string.IsNullOrEmpty(uid)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid))).ToLowerInvariant();
        return Path.Combine(BlobBaseDir, hash);
    }

    private string? GetBlobPath(string blobId)
    {
        var dir = GetBlobDir();
        if (dir is null) return null;
        // Reject anything that isn't pure hex so a malformed blobId can't
        // escape the cache directory via path traversal.
        foreach (var ch in blobId)
        {
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return null;
        }
        return Path.Combine(dir, $"{blobId}.bin");
    }

    private void TryWriteCipherToDisk(string blobId, byte[] cipher)
    {
        try
        {
            var path = GetBlobPath(blobId);
            if (path is null) return;
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(dir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            // Atomic-ish: write to temp + move.
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best-effort cache; chat still works without it */ }
    }

    private byte[]? TryReadCipherFromDisk(string blobId)
    {
        try
        {
            var path = GetBlobPath(blobId);
            if (path is null || !File.Exists(path)) return null;
            return File.ReadAllBytes(path);
        }
        catch { return null; }
    }

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
