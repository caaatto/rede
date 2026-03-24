namespace Rede.Core.Crypto;

/// <summary>
/// Client-side nonce deduplication to prevent replay attacks.
/// Mirrors: checkClientNonce, _seenNonces in crypto.js
/// Thread-safe via lock for concurrent handler access.
/// </summary>
public class NonceTracker
{
    private const long NonceMaxAge = 3600000;  // 1 hour in ms
    private const int NonceMaxSize = 10000;

    private readonly Dictionary<string, long> _seenNonces = new();
    private readonly object _lock = new();

    /// <summary>
    /// Check if a nonce is fresh (not seen before). Returns true if fresh, false if replay.
    /// Mirrors: checkClientNonce(nonceB64)
    /// </summary>
    public bool Check(string nonceB64)
    {
        lock (_lock)
        {
            if (_seenNonces.Count > NonceMaxSize)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var stale = _seenNonces.Where(kv => now - kv.Value > NonceMaxAge).Select(kv => kv.Key).ToList();
                foreach (var k in stale)
                    _seenNonces.Remove(k);
            }

            if (_seenNonces.ContainsKey(nonceB64))
                return false;

            _seenNonces[nonceB64] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return true;
        }
    }
}
