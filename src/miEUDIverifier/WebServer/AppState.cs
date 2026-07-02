using miEUDIverifier.Models;

namespace miEUDIverifier.WebServer;

/// <summary>
/// Shared mutable state between the background polling task and the web server.
/// </summary>
public class AppState
{
    /// <summary>waiting | complete | partial | error</summary>
    public string Status { get; set; } = "waiting";

    public string TransactionId { get; set; } = string.Empty;
    public string DeepLink      { get; set; } = string.Empty;
    public string QrBase64      { get; set; } = string.Empty;

    public IdentityData? Identity     { get; set; }
    public string?       ErrorMessage { get; set; }

    /// <summary>Letzter roher JSON-Response vom Verifier-Backend (für Debugging).</summary>
    public string? LastRawResponse { get; set; }

    /// <summary>CancellationTokenSource des aktuellen Polling-Tasks.</summary>
    public CancellationTokenSource? PollingCts { get; set; }
}
