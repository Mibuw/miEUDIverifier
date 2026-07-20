namespace miEUDIverifier.Configuration;

/// <summary>
/// Configuration settings for the EUDI Verifier Endpoint connection.
/// </summary>
public class VerifierSettings
{
    public const string SectionName = "VerifierSettings";

    /// <summary>
    /// Base URL of the EUDI Verifier Backend.
    /// Default: https://verifier.eudiw.dev (public demo instance)
    /// For local development: http://localhost:8080
    /// </summary>
    public string BackendUrl { get; set; } = "https://verifier.eudiw.dev";

    /// <summary>How often (in seconds) to poll for the wallet response.</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>Maximum time (in seconds) to wait for the wallet response.</summary>
    public int PollTimeoutSeconds { get; set; } = 120;

    /// <summary>OpenID4VP profile. "openid4vp" or "haip".</summary>
    public string Profile { get; set; } = "openid4vp";

    /// <summary>How the authorization request JWT is passed. "by_reference" or "by_value".</summary>
    public string JarMode { get; set; } = "by_reference";

    /// <summary>HTTP method for request_uri. "post" or "get".</summary>
    public string RequestUriMethod { get; set; } = "post";

    /// <summary>Wallet response mode. "direct_post" or "direct_post.jwt".</summary>
    public string ResponseMode { get; set; } = "direct_post";

    /// <summary>URI scheme for the QR code deep link (e.g. "openid4vp", "eudi-openid4vp", "haip-vp").</summary>
    public string AuthorizationRequestScheme { get; set; } = "openid4vp";

    /// <summary>
    /// PEM-encoded certificate chain of the trusted PID issuer.
    /// The demo certificate for the EUDI reference implementation test environment is pre-filled.
    /// </summary>
    public string? IssuerChain { get; set; }

    /// <summary>
    /// DCQL format identifier for the SD-JWT VC option. OpenID4VP 1.0 / HAIP use "dc+sd-jwt";
    /// older stacks used "vc+sd-jwt".
    /// </summary>
    public string SdJwtFormat { get; set; } = "dc+sd-jwt";

    /// <summary>
    /// Accepted verifiable-credential types (vct) for the SD-JWT VC PID. The wallet may present a
    /// credential whose vct matches any of these. Defaults cover the ARF value and the OpenID4VP
    /// specification example.
    /// </summary>
    public List<string> SdJwtVctValues { get; set; } = new()
    {
        "urn:eudi:pid:1",
        "urn:eu.europa.ec.eudi:pid:1",
    };

    /// <summary>
    /// Accepted vct values for the German EUDI Wallet (Bundesdruckerei prototype) PID,
    /// offered as an additional SD-JWT VC option. The German PID uses the OIDC-style
    /// claim name "birthdate" instead of "birth_date", so it needs its own DCQL entry.
    /// Set to an empty list to disable the option.
    /// </summary>
    public List<string> GermanPidVctValues { get; set; } = new()
    {
        "https://demo.pid-issuer.bundesdruckerei.de/credentials/pid/1.0",
    };

    /// <summary>
    /// Time-to-live (minutes) for REST-API verification sessions. Abandoned sessions are
    /// evicted after this period. Default: 30 minutes.
    /// </summary>
    public int SessionTtlMinutes { get; set; } = 30;
}
