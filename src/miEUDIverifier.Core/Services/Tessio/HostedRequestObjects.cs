using System.Collections.Concurrent;

namespace miEUDIverifier.Services.Tessio;

/// <summary>
/// The signed request objects (JARs) this application serves to wallets, by the id in their
/// <c>request_uri</c>.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists rather than Tessio's own store: the library keeps request objects in
/// <c>RequestObjectStore</c> and serves them from <c>{prefix}/request/{id}</c>, and both are
/// internal to the package. A host that creates its own sessions -- which this one must, because it
/// renders its own page instead of the library's hosted one -- therefore cannot put a request where
/// that endpoint would find it. So the request URI points here instead, and
/// <see cref="TessioVerifierExtensions"/> maps the endpoint that reads this.
/// </para>
/// <para>
/// In memory, which is the same choice the library makes and has the same consequence: a restart
/// loses requests in flight, and a second instance does not see the first one's. Both are fine for
/// one instance and wrong behind a load balancer, which would want this in Redis -- as would the
/// session store, so the two belong together.
/// </para>
/// </remarks>
public sealed class HostedRequestObjects
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <param name="clock">The time source, so tests do not have to wait for an expiry.</param>
    public HostedRequestObjects(TimeProvider clock) => _clock = clock;

    private readonly record struct Entry(string Jar, DateTimeOffset ExpiresAt);

    /// <summary>Holds <paramref name="jar"/> under <paramref name="requestId"/> until it expires.</summary>
    public void Put(string requestId, string jar, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jar);

        // Opportunistic, so a long-running process does not accumulate expired requests. Cheap
        // because a verifier creates one request per scan, not per web request.
        var now = _clock.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }

        _entries[requestId] = new Entry(jar, expiresAt);
    }

    /// <summary>
    /// The request object under <paramref name="requestId"/>, or null when there is none or it has
    /// expired.
    /// </summary>
    /// <remarks>
    /// Served repeatedly while it is valid, deliberately. The EUDI reference backend's endpoint is
    /// single use, and a wallet release that resolved <c>request_uri</c> twice broke against it; the
    /// JAR is signed and carries its own <c>exp</c>, so serving it again gives a wallet nothing it
    /// could not already see.
    /// </remarks>
    public string? Get(string requestId)
    {
        if (!_entries.TryGetValue(requestId, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAt <= _clock.GetUtcNow())
        {
            _entries.TryRemove(requestId, out _);
            return null;
        }

        return entry.Jar;
    }
}
