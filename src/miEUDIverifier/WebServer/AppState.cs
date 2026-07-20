using miEUDIverifier.Models;

namespace miEUDIverifier.WebServer;

/// <summary>
/// Shared mutable state between the background polling task and the web server.
/// </summary>
public class AppState
{
    /// <summary>waiting | complete | partial | error</summary>
    public string Status { get; set; } = "waiting";

    /// <summary>Backend key this session talks to (e.g. "eu" or "de").</summary>
    public string Backend { get; set; } = string.Empty;

    public string TransactionId { get; set; } = string.Empty;
    public string DeepLink      { get; set; } = string.Empty;
    public string QrBase64      { get; set; } = string.Empty;

    public IdentityData? Identity     { get; set; }
    public string?       ErrorMessage { get; set; }

    /// <summary>Last raw JSON response from the verifier backend (for debugging).</summary>
    public string? LastRawResponse { get; set; }

    /// <summary>CancellationTokenSource of the current polling task.</summary>
    public CancellationTokenSource? PollingCts { get; set; }

    /// <summary>Creation time (UTC) – used for TTL-based eviction of API sessions.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
