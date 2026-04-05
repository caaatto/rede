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
    /// Load a snapshot of seen nonces (e.g. from persistent profile storage) so that
    /// the replay window survives application restarts. Stale entries (older than 1h)
    /// are dropped during import.
    /// </summary>
    public void ImportSnapshot(IDictionary<string, long>? snapshot)
    {
        if (snapshot is null || snapshot.Count == 0) return;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var kv in snapshot)
            {
                if (now - kv.Value > NonceMaxAge) continue;
                if (_seenNonces.Count >= NonceMaxSize) break;
                _seenNonces[kv.Key] = kv.Value;
            }
        }
    }

    /// <summary>
    /// Export all seen-nonce entries that are still within the replay window so the
    /// caller can persist them. Evicts stale entries as a side-effect.
    /// </summary>
    public Dictionary<string, long> ExportSnapshot()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var stale = _seenNonces.Where(kv => now - kv.Value > NonceMaxAge).Select(kv => kv.Key).ToList();
            foreach (var k in stale) _seenNonces.Remove(k);
            return new Dictionary<string, long>(_seenNonces);
        }
    }

    /// <summary>
    /// Check if a nonce is fresh (not seen before). Returns true if fresh, false if replay.
    /// Mirrors: checkClientNonce(nonceB64)
    /// </summary>
    public bool Check(string nonceB64)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // H4: Always evict stale entries (not just when over max)
            // This prevents memory growth from accumulating expired nonces
            if (_seenNonces.Count > NonceMaxSize / 2)
            {
                var stale = _seenNonces.Where(kv => now - kv.Value > NonceMaxAge).Select(kv => kv.Key).ToList();
                foreach (var k in stale)
                    _seenNonces.Remove(k);
            }

            // H7: Hard cap — if still over limit after eviction, reject to prevent DoS
            if (_seenNonces.Count >= NonceMaxSize)
                return false;

            if (_seenNonces.ContainsKey(nonceB64))
                return false;

            _seenNonces[nonceB64] = now;
            return true;
        }
    }
}
