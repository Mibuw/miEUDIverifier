using System.Text.Json;
using System.Text.Json.Serialization;

namespace miEUDIverifier.Models;

// ─── GET /ui/presentations/{transactionId} ───────────────────────────────────

/// <summary>
/// Response returned by GET /ui/presentations/{transactionId}.
/// The verifier backend decodes and validates the wallet's VP token
/// and returns the structured credential attributes here.
/// </summary>
public class WalletResponseEnvelope
{
    [JsonPropertyName("transaction_id")]
    public string? TransactionId { get; set; }

    /// <summary>
    /// Presentation status.
    /// Typical values: "pending" | "requested" | "request_object_retrieved" | "submitted" | "timed_out"
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    // ── Successful presentation payload ──────────────────────────────────────

    /// <summary>
    /// Verified credential presentations, keyed by credential ID from the DCQL query.
    /// Available when Status == "submitted".
    /// </summary>
    [JsonPropertyName("credentials")]
    public List<VerifiedCredential>? Credentials { get; set; }

    /// <summary>Raw vp_token string (may be a JWT or CBOR-encoded mDoc).</summary>
    [JsonPropertyName("vp_token")]
    public JsonElement? VpToken { get; set; }

    // ── Error case ───────────────────────────────────────────────────────────

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Status kann je nach Backend-Version "submitted", "complete" o.ä. sein
    public bool IsSubmitted =>
        string.Equals(Status, "submitted", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "complete",  StringComparison.OrdinalIgnoreCase);

    // Fallback: vp_token vorhanden → Antwort liegt vor, auch ohne Status-Feld
    public bool HasVpToken =>
        VpToken.HasValue && VpToken.Value.ValueKind != System.Text.Json.JsonValueKind.Null;

    public bool IsTimedOut =>
        string.Equals(Status, "timed_out", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "timedout",  StringComparison.OrdinalIgnoreCase);

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

/// <summary>
/// A single verified verifiable credential returned by the verifier backend.
/// </summary>
public class VerifiedCredential
{
    [JsonPropertyName("credential_id")]
    public string? CredentialId { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>mso_mdoc document type, e.g. "eu.europa.ec.eudi.pid.1"</summary>
    [JsonPropertyName("doctype")]
    public string? DocType { get; set; }

    /// <summary>
    /// Decoded attributes, structured as namespace → element_identifier → value
    /// for mso_mdoc, or flat claim → value for sd-jwt.
    /// </summary>
    [JsonPropertyName("attributes")]
    public JsonElement? Attributes { get; set; }
}

// ─── Extracted identity result ────────────────────────────────────────────────

/// <summary>The identity data extracted from the wallet presentation.</summary>
public class IdentityData
{
    public string? FamilyName { get; set; }
    public string? GivenName { get; set; }
    public string? BirthDate { get; set; }

    /// <summary>Source credential format ("mso_mdoc" or "vc+sd-jwt").</summary>
    public string? CredentialFormat { get; set; }

    /// <summary>Additional claims that were returned beyond the three requested.</summary>
    public Dictionary<string, string> AdditionalClaims { get; set; } = new();

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(FamilyName) &&
        !string.IsNullOrWhiteSpace(GivenName) &&
        !string.IsNullOrWhiteSpace(BirthDate);
}
