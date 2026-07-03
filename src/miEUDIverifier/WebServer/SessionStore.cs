using System.Collections.Concurrent;

namespace miEUDIverifier.WebServer;

/// <summary>
/// Thread-safe store of verification sessions, keyed by an opaque session id.
/// Lets external applications run any number of verifications in parallel, each
/// with its own <see cref="AppState"/> (transaction, QR code, polling, result).
/// Abandoned sessions are evicted after a configurable time-to-live.
/// </summary>
public class SessionStore
{
    private readonly ConcurrentDictionary<string, AppState> _sessions = new();
    private readonly TimeSpan _ttl;

    public SessionStore(TimeSpan ttl) => _ttl = ttl;

    /// <summary>Stores a session and returns its newly generated id.</summary>
    public string Add(AppState state)
    {
        var id = Guid.NewGuid().ToString("N");
        state.CreatedAt = DateTime.UtcNow;
        _sessions[id] = state;
        return id;
    }

    public bool TryGet(string id, out AppState state) => _sessions.TryGetValue(id, out state!);

    /// <summary>Removes a session (does not cancel its polling task – the caller decides).</summary>
    public bool Remove(string id, out AppState? state) => _sessions.TryRemove(id, out state);

    public int Count => _sessions.Count;

    /// <summary>Removes and cancels sessions older than the configured TTL. Returns the count removed.</summary>
    public int PurgeExpired()
    {
        var cutoff  = DateTime.UtcNow - _ttl;
        var removed = 0;
        foreach (var kv in _sessions)
        {
            if (kv.Value.CreatedAt >= cutoff) continue;
            if (_sessions.TryRemove(kv.Key, out var state))
            {
                state.PollingCts?.Cancel();
                removed++;
            }
        }
        return removed;
    }
}
