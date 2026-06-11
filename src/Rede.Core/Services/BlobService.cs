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
/// Max blob size: 700 KB (base64-expanded BLOB_DATA must fit the 1 MB WS frame
/// cap on both the server and RedeConnection's inbound limit — bigger blobs
/// would upload fine but be undeliverable on fetch).
/// </summary>
public class BlobService : IDisposable
{
    // FU1: attachments are chunked, so the cap is no longer bound by the WS frame.
    public const int MaxBlobSize = 8 * 1024 * 1024; // 8 MB plaintext

    // Per-chunk ciphertext size. base64 expands ×4/3, so 480 KB cipher → ~640 KB
    // on the wire, safely under the 700 KB budget within the 1 MB frame cap.
    private const int ChunkCipherSize = 480_000;

    // Upper bound on chunks the receiver will fetch/reassemble. ChunkCount comes
    // from the (attacker-controlled) message envelope, so an unbounded value would
    // let a sender trigger millions of BLOB_FETCH round-trips and unbounded memory
    // growth. A legitimate max-size blob is ~(8 MB + 16)/480 KB ≈ 18 chunks.
    private const int MaxChunkCount = 24;
    // Hard ceiling on reassembled ciphertext before SecretBox.Open (MAC + slack).
    private const int MaxCipherReassembleSize = MaxBlobSize + 4096;

    private static string ChunkBlobId(string blobId, int index) => $"{blobId}.{index}";

    private static readonly string BlobBaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rede", "blobs");

    private readonly RedeConnection _conn;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _uploadPending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]?>> _fetchPending = new();

    // In-memory cache (blobId + key/nonce binding → decrypted bytes) for hot reads.
    // M3: keyed by CacheKey(), NOT bare blobId — a peer referencing a foreign
    // blobId with a different key/nonce must miss, not read someone else's
    // cached plaintext. (Disk stays keyed by blobId: it stores ciphertext, and
    // secretbox.Open authenticates — a wrong-key hit fails and falls through.)
    private const int MaxCacheEntries = 100;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new();
    private readonly Queue<string> _cacheOrder = new();

    private static string CacheKey(string blobId, byte[] key, byte[] nonce)
    {
        var bound = new byte[key.Length + nonce.Length];
        Buffer.BlockCopy(key, 0, bound, 0, key.Length);
        Buffer.BlockCopy(nonce, 0, bound, key.Length, nonce.Length);
        var hash = SHA256.HashData(bound);
        CryptographicOperations.ZeroMemory(bound);
        return blobId + ":" + Convert.ToHexString(hash).ToLowerInvariant();
    }

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
            OnSystemMessage?.Invoke($"Attachment too large ({plainData.Length / 1024 / 1024} MB, max 8 MB).");
            return null;
        }

        // Generate per-blob encryption key + nonce
        var key = SecretBox.GenerateKey();
        var nonce = SecretBox.GenerateNonce();
        var cipherData = SecretBox.Create(plainData, nonce, key);

        // Generate blob ID
        var blobId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // FU1: split the ciphertext into frame-sized chunks. A single-chunk blob
        // uploads under the bare blobId (legacy wire shape); a multi-chunk blob
        // uploads each piece under "{blobId}.{i}" and records ChunkCount.
        int chunkCount = (cipherData.Length + ChunkCipherSize - 1) / ChunkCipherSize;
        if (chunkCount <= 1)
        {
            if (!await UploadOneBlobAsync(blobId, cipherData))
            {
                OnSystemMessage?.Invoke("Upload failed.");
                return null;
            }
            chunkCount = 1;
        }
        else
        {
            for (int i = 0; i < chunkCount; i++)
            {
                int off = i * ChunkCipherSize;
                int len = Math.Min(ChunkCipherSize, cipherData.Length - off);
                var chunk = new byte[len];
                Buffer.BlockCopy(cipherData, off, chunk, 0, len);
                if (!await UploadOneBlobAsync(ChunkBlobId(blobId, i), chunk))
                {
                    OnSystemMessage?.Invoke($"Upload failed (chunk {i + 1}/{chunkCount}).");
                    return null;
                }
            }
        }

        // Cache the plaintext locally + persist the whole ciphertext to disk so the
        // image survives restarts even if the server has dropped the blob.
        CacheBlob(CacheKey(blobId, key, nonce), plainData);
        TryWriteCipherToDisk(blobId, cipherData);

        var att = new AttachmentInfo
        {
            BlobId = blobId,
            Key = (byte[])key.Clone(),
            Nonce = nonce,
            Name = fileName,
            MimeType = mimeType,
            Size = plainData.Length,
            ChunkCount = chunkCount,
        };

        // Zero the local key copy (AttachmentInfo owns its own clone)
        CryptographicOperations.ZeroMemory(key);

        return att;
    }

    /// <summary>Upload one ciphertext blob (single blob or one chunk) and await BLOB_UPLOAD_OK.</summary>
    private async Task<bool> UploadOneBlobAsync(string blobId, byte[] cipherChunk)
    {
        var tcs = new TaskCompletionSource<bool>();
        _uploadPending[blobId] = tcs;

        _conn.Send(Msg.BlobUpload, ProtocolSerializer.Payload(
            ("blobId", JsonValue.Create(blobId)),
            ("size", JsonValue.Create(cipherChunk.Length)),
            ("data", JsonValue.Create(Convert.ToBase64String(cipherChunk)))
        ));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => tcs.TrySetResult(false));
        var ok = await tcs.Task;
        _uploadPending.TryRemove(blobId, out _);
        return ok;
    }

    /// <summary>
    /// Fetch and decrypt a blob by its attachment info. Returns decrypted bytes or null.
    /// </summary>
    public async Task<byte[]?> FetchAsync(AttachmentInfo att)
    {
        // Hot in-memory cache first — keyed by blobId + key/nonce binding (M3).
        var cacheKey = CacheKey(att.BlobId, att.Key, att.Nonce);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Local on-disk cache: ciphertext we wrote on a previous send/fetch.
        // The Key/Nonce live in the (passphrase-encrypted) chat history, so
        // ciphertext on disk leaks nothing the server didn't already hold.
        var diskCipher = TryReadCipherFromDisk(att.BlobId);
        if (diskCipher is not null)
        {
            try
            {
                // secretbox authenticates: a wrong key/nonce throws → treated as
                // a miss (fail closed), never returns foreign plaintext.
                var plain = SecretBox.Open(diskCipher, att.Nonce, att.Key);
                CacheBlob(cacheKey, plain);
                return plain;
            }
            catch { /* corrupted on disk or wrong key — fall through to network */ }
        }

        // FU1: reassemble chunked ciphertext (legacy single-blob path when ChunkCount ≤ 1)
        byte[]? cipherData;
        if (att.ChunkCount > 1)
        {
            // ChunkCount is attacker-controlled (from the message envelope) — reject
            // anything beyond what a legitimate max-size blob needs before issuing
            // a single fetch, so a malicious sender can't trigger a fetch flood / OOM.
            if (att.ChunkCount > MaxChunkCount)
            {
                OnSystemMessage?.Invoke($"Attachment rejected (claims {att.ChunkCount} chunks, max {MaxChunkCount}).");
                return null;
            }
            using var ms = new MemoryStream();
            for (int i = 0; i < att.ChunkCount; i++)
            {
                var chunk = await FetchOneBlobAsync(ChunkBlobId(att.BlobId, i));
                if (chunk is null)
                {
                    OnSystemMessage?.Invoke($"Attachment fetch failed (chunk {i + 1}/{att.ChunkCount}).");
                    return null;
                }
                if (ms.Length + chunk.Length > MaxCipherReassembleSize)
                {
                    OnSystemMessage?.Invoke("Attachment rejected (reassembled size exceeds limit).");
                    return null;
                }
                ms.Write(chunk, 0, chunk.Length);
            }
            cipherData = ms.ToArray();
        }
        else
        {
            cipherData = await FetchOneBlobAsync(att.BlobId);
        }

        if (cipherData is null) return null;

        try
        {
            var plain = SecretBox.Open(cipherData, att.Nonce, att.Key);
            CacheBlob(cacheKey, plain);
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

    /// <summary>Fetch one ciphertext blob (single blob or one chunk) and await BLOB_DATA.</summary>
    private async Task<byte[]?> FetchOneBlobAsync(string blobId)
    {
        var tcs = new TaskCompletionSource<byte[]?>();
        _fetchPending[blobId] = tcs;

        _conn.Send(Msg.BlobFetch, ProtocolSerializer.Payload(
            ("blobId", JsonValue.Create(blobId))
        ));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => tcs.TrySetResult(null));
        var data = await tcs.Task;
        _fetchPending.TryRemove(blobId, out _);
        return data;
    }

    private void CacheBlob(string cacheKey, byte[] data)
    {
        _cache[cacheKey] = data;
        _cacheOrder.Enqueue(cacheKey);
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
