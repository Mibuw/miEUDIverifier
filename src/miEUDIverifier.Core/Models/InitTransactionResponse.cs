using System.Text.Json.Serialization;

namespace miEUDIverifier.Models;

/// <summary>
/// Response from POST /ui/presentations — contains the transaction ID
/// and the URI that must be passed to the wallet (via QR code or deep link).
/// </summary>
public class InitTransactionResponse
{
    [JsonPropertyName("transaction_id")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// URL pointing to the signed authorization request JWT.
    /// Must be embedded in the wallet deep link as ?request_uri=...
    /// </summary>
    [JsonPropertyName("request_uri")]
    public string RequestUri { get; set; } = string.Empty;

    [JsonPropertyName("request_uri_method")]
    public string RequestUriMethod { get; set; } = "get";

    /// <summary>
    /// Builds the wallet deep link URI, e.g.
    ///   openid4vp://?client_id=...&amp;request_uri=...&amp;request_uri_method=post
    /// </summary>
    public string BuildWalletDeepLink(string scheme = "openid4vp")
    {
        var encodedRequestUri = Uri.EscapeDataString(RequestUri);
        var encodedClientId = Uri.EscapeDataString(ClientId);
        return $"{scheme}://?client_id={encodedClientId}&request_uri={encodedRequestUri}&request_uri_method={RequestUriMethod}";
    }
}
