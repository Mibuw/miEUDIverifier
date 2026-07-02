using System.Text.Json.Serialization;

namespace miEUDIverifier.Models;

// ─── Initialize Transaction Request ──────────────────────────────────────────

public class InitTransactionRequest
{
    [JsonPropertyName("dcql_query")]
    public DcqlQuery DcqlQuery { get; set; } = new();

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("jar_mode")]
    public string JarMode { get; set; } = "by_reference";

    [JsonPropertyName("request_uri_method")]
    public string RequestUriMethod { get; set; } = "post";

    [JsonPropertyName("response_mode")]
    public string ResponseMode { get; set; } = "direct_post";

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "openid4vp";

    [JsonPropertyName("authorization_request_scheme")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthorizationRequestScheme { get; set; }

    [JsonPropertyName("issuer_chain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IssuerChain { get; set; }
}

// ─── DCQL Query ───────────────────────────────────────────────────────────────

public class DcqlQuery
{
    [JsonPropertyName("credentials")]
    public List<DcqlCredential> Credentials { get; set; } = new();

    [JsonPropertyName("credential_sets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DcqlCredentialSet>? CredentialSets { get; set; }
}

public class DcqlCredential
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DcqlCredentialMeta? Meta { get; set; }

    [JsonPropertyName("claims")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DcqlClaim>? Claims { get; set; }
}

public class DcqlCredentialMeta
{
    /// <summary>For mso_mdoc: the document type, e.g. "eu.europa.ec.eudi.pid.1"</summary>
    [JsonPropertyName("doctype_value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DoctypeValue { get; set; }

    /// <summary>For vc+sd-jwt: the verifiable credential type</summary>
    [JsonPropertyName("vct_values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? VctValues { get; set; }
}

public class DcqlClaim
{
    /// <summary>
    /// Claim path array.
    /// For mso_mdoc: ["namespace", "element_identifier"]
    /// For sd-jwt: ["claim_name"]
    /// </summary>
    [JsonPropertyName("path")]
    public List<string> Path { get; set; } = new();
}

public class DcqlCredentialSet
{
    [JsonPropertyName("options")]
    public List<List<string>> Options { get; set; } = new();

    [JsonPropertyName("purpose")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Purpose { get; set; }
}
