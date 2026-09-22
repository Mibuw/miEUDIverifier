namespace miEUDIverifier.Configuration;

/// <summary>
/// Configuration settings for the EUDI Verifier Endpoint connection.
/// </summary>
public class VerifierSettings
{
    public const string SectionName = "VerifierSettings";

    /// <summary>
    /// Base URL of the EUDI Verifier Backend.
    /// Default: https://verifier-backend.eudiw.dev (the reference backend's REST API).
    /// Note this is NOT https://verifier.eudiw.dev — that host serves the Angular demo UI and
    /// answers the API paths with 405.
    /// For local development: http://localhost:8080
    /// </summary>
    public string BackendUrl { get; set; } = "https://verifier-backend.eudiw.dev";

    /// <summary>
    /// Named verifier backends selectable per verification via <c>?backend=&lt;key&gt;</c>.
    /// Key → base URL. Enables serving multiple trust ecosystems from one app, e.g.
    /// <c>eu</c> = EUDI reference (eudiw.dev) and <c>de</c> = German EUDI Wallet (own backend
    /// with a SPRIND-issued RP access certificate). When empty, <see cref="BackendUrl"/> is used
    /// as the single <c>eu</c> backend. Configure via env, e.g.
    /// <c>EUDI_VerifierSettings__Backends__eu=…</c> / <c>EUDI_VerifierSettings__Backends__de=…</c>.
    /// Leave the <c>de</c> entry unset if no German backend instance is deployed — the app then
    /// serves <c>eu</c> only and the demo page hides the ecosystem switcher.
    /// </summary>
    public Dictionary<string, string> Backends { get; set; } = new();

    /// <summary>Key of the backend used when no <c>?backend=</c> is given. Default: <c>eu</c>.</summary>
    public string DefaultBackend { get; set; } = "eu";

    /// <summary>
    /// Backend keys that request only the <c>mso_mdoc</c> PID (no SD-JWT alternatives), e.g.
    /// <c>de</c> to stay within a Registration Certificate scoped to mso_mdoc.
    /// </summary>
    public List<string> MdocOnlyBackends { get; set; } = new();

    /// <summary>
    /// Backend keys that request only the SD-JWT VC PID (no mso_mdoc alternative), e.g. a German
    /// backend pointed at a Registration Certificate scoped to <c>dc+sd-jwt</c>. The mirror image
    /// of <see cref="MdocOnlyBackends"/>: the verifier backend attaches exactly one Registration
    /// Certificate per transaction, so a request must stay within the format that certificate
    /// covers. Setting a backend in both lists is a configuration error and throws.
    /// </summary>
    public List<string> SdJwtOnlyBackends { get; set; } = new();

    /// <summary>
    /// Per-backend Wallet Relying Party Intended Use id (key → id). Sent as <c>intended_use_id</c>
    /// so the backend attaches the matching Registration Certificate to the authorization request.
    /// Falls back to <see cref="IntendedUseId"/> for backends with no entry here.
    /// </summary>
    public Dictionary<string, string> IntendedUseIds { get; set; } = new();

    /// <summary>
    /// Fallback Wallet Relying Party Intended Use id, used whenever the transaction does not carry
    /// one of its own (mirrors how <see cref="ResponseMode"/> backs <see cref="ResponseModes"/>).
    /// Defaults to <c>TEST-01</c>, the public test intended use eudiw.dev publishes at
    /// <c>GET /ui/intended-uses</c> — since August 2026 it rejects a request without an
    /// <c>intended_use_id</c> (<c>MissingRegistrationCertificate</c>). Registration certificates
    /// are issued by a registrar, so a self-made one would not be trusted; this is the operator's
    /// own test certificate. Set to an empty string when pointing at a backend of your own.
    /// </summary>
    public string IntendedUseId { get; set; } = "TEST-01";

    /// <summary>
    /// Per-backend OpenID4VP <c>response_mode</c> override (key → <c>direct_post</c> or
    /// <c>direct_post.jwt</c>). Use <c>direct_post.jwt</c> for high-assurance wallets that require
    /// an encrypted response (e.g. the German EUDI Wallet). Defaults to <see cref="ResponseMode"/>.
    /// </summary>
    public Dictionary<string, string> ResponseModes { get; set; } = new();

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
    /// PID attributes to request, in mso_mdoc spelling — the canonical form. The SD-JWT VC options
    /// translate the two names that differ there: <c>birth_date</c> → <c>birthdate</c> and
    /// <c>nationality</c> → <c>nationalities</c>.
    /// <para>
    /// A backend fronting a Registration Certificate must request exactly the attributes that
    /// certificate covers, so this is normally set per backend via
    /// <see cref="PidClaimsByBackend"/>; the default stays at the three basic attributes.
    /// </para>
    /// </summary>
    public List<string> PidClaims { get; set; } = new()
    {
        "family_name",
        "given_name",
        "birth_date",
    };

    /// <summary>
    /// Per-backend PID attributes (key → attribute names in mso_mdoc spelling). Falls back to
    /// <see cref="PidClaims"/> for backends with no entry here.
    /// </summary>
    public Dictionary<string, List<string>> PidClaimsByBackend { get; set; } = new();

    /// <summary>
    /// Per-backend accepted vct values for the generic SD-JWT VC PID (key → vct values). Falls back
    /// to <see cref="SdJwtVctValues"/> for backends with no entry here; map a backend to an empty
    /// list to drop the generic option, which is how a backend scoped to the German Registration
    /// Certificate stays inside what that certificate covers.
    /// </summary>
    public Dictionary<string, List<string>> SdJwtVctValuesByBackend { get; set; } = new();

    /// <summary>
    /// Accepted vct values for the German EUDI Wallet PID, offered as an additional SD-JWT VC
    /// option. The German PID uses the OIDC-style claim name "birthdate" instead of "birth_date",
    /// so it needs its own DCQL entry. Set to an empty list to disable the option.
    /// <para>
    /// <c>urn:eudi:pid:de:1</c> is the identifier the BMI developer guide documents for the German
    /// PID; the Bundesdruckerei URL is the older prototype value, kept so wallets still carrying it
    /// keep matching. Used as the fallback for backends with no
    /// <see cref="GermanPidVctValuesByBackend"/> entry.
    /// </para>
    /// </summary>
    public List<string> GermanPidVctValues { get; set; } = new()
    {
        "urn:eudi:pid:de:1",
        "https://demo.pid-issuer.bundesdruckerei.de/credentials/pid/1.0",
    };

    /// <summary>
    /// Per-backend accepted vct values for the German PID (key → vct values), mirroring how
    /// <see cref="IntendedUseIds"/> backs <see cref="IntendedUseId"/>. Falls back to
    /// <see cref="GermanPidVctValues"/> for backends with no entry here.
    /// <para>
    /// A backend fronting a Registration Certificate must request exactly the vct values that
    /// certificate covers — asking for more than is registered makes the wallet abort. The tolerant
    /// multi-value default is therefore right for an unregistered backend but wrong for a
    /// registered one, e.g. <c>de</c> → <c>["urn:eudi:pid:de:1"]</c>.
    /// </para>
    /// </summary>
    public Dictionary<string, List<string>> GermanPidVctValuesByBackend { get; set; } = new();

    /// <summary>
    /// Time-to-live (minutes) for REST-API verification sessions. Abandoned sessions are
    /// evicted after this period. Default: 30 minutes.
    /// </summary>
    public int SessionTtlMinutes { get; set; } = 30;
}
