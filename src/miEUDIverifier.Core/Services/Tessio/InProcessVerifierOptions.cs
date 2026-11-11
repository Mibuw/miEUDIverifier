using Tessio.Verifier.OpenId4Vp;

namespace miEUDIverifier.Services.Tessio;

/// <summary>
/// What a deployment must supply to verify presentations in process, with no verifier backend.
/// </summary>
/// <remarks>
/// Three of these are not optional and have no sensible default, because they are what makes this
/// application the verifier rather than a client of one: the access certificate it signs requests
/// with, the registration certificate that says what it may ask for, and the issuer's trust anchors
/// that decide whose credentials it believes.
/// </remarks>
public sealed class InProcessVerifierOptions
{
    /// <summary>
    /// The publicly reachable base URL, as the wallet sees it. Not localhost: the wallet fetches the
    /// request object and posts the response over the internet.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Where the wallet-facing endpoints are hosted, relative to the base URL.</summary>
    public string RoutePrefix { get; set; } = "/verify";

    /// <summary>
    /// PKCS#12 holding the relying-party access certificate, its private key and its issuing CA.
    /// The SHA-256 over the leaf's DER is the <c>x509_hash</c> client identifier the wallet pins the
    /// request to.
    /// </summary>
    public string AccessCertificatePath { get; set; } = string.Empty;

    /// <summary>The PKCS#12 password, or null when it has none.</summary>
    public string? AccessCertificatePassword { get; set; }

    /// <summary>
    /// The registration certificate as the issuing authority delivers it: a compact JWT whose
    /// <c>typ</c> header is <c>rc-wrp+jwt</c>. Sent verbatim, because the wallet verifies a
    /// signature over these exact bytes. Takes precedence over
    /// <see cref="RegistrationCertificatePath"/>.
    /// </summary>
    public string? RegistrationCertificate { get; set; }

    /// <summary>A file holding the registration certificate, when it is not supplied inline.</summary>
    public string? RegistrationCertificatePath { get; set; }

    /// <summary>
    /// A folder of the PID issuer's CA certificates (<c>.pem</c>, <c>.crt</c>, <c>.cer</c>,
    /// <c>.der</c>). Without them a presentation verifies cryptographically and is still untrusted.
    /// </summary>
    public string IssuerTrustAnchorDirectory { get; set; } = string.Empty;

    /// <summary>The mdoc doctype to request.</summary>
    public string DocType { get; set; } = "eu.europa.ec.eudi.pid.1";

    /// <summary>
    /// The namespace the requested elements live in. For the EUDI PID this equals the doctype.
    /// </summary>
    public string MdocNamespace { get; set; } = "eu.europa.ec.eudi.pid.1";

    /// <summary>
    /// The attributes to request, or null for <see cref="DefaultRequestedClaims"/>. Must stay within
    /// what the registration certificate grants: one attribute more and the German wallet drops the
    /// request with no consent screen, which looks like a certificate fault and is not one.
    /// </summary>
    /// <remarks>
    /// Null rather than pre-filled, and that is not a style choice. .NET's configuration binder
    /// ADDS to a collection property that already holds items instead of replacing it, so a
    /// pre-filled default plus three claims in appsettings.json produced a request for ten. In this
    /// domain that is not a cosmetic bug: asking for more than the registration certificate grants
    /// makes the wallet refuse silently. Null cannot be appended to, so what is configured is what
    /// is requested.
    /// </remarks>
    public IReadOnlyList<string>? RequestedClaims { get; set; }

    /// <summary>
    /// What <see cref="RequestedClaims"/> falls back to: the seven PID attributes this project's
    /// registration certificate covers.
    /// </summary>
    public static IReadOnlyList<string> DefaultRequestedClaims { get; } =
    [
        "family_name",
        "given_name",
        "birth_date",
        "place_of_birth",
        "nationality",
        "issuing_authority",
        "issuing_country",
    ];

    /// <summary>How the wallet answers. The German wallet uses the HAIP default, encrypted.</summary>
    public ResponseMode ResponseMode { get; set; } = ResponseMode.DirectPostJwt;

    /// <summary>How long a request and its session stay valid.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether to check the credential's revocation status. <b>Off by default, and that is a real
    /// gap rather than a preference:</b> with it off this path answers "was validly issued", not
    /// "is valid now", so a revoked PID is accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Turning it on makes every German PID presentation fail with <c>status_invalid</c>: "The top
    /// of the MSO revocation list's chain was not issued by the credential's Document Signer's
    /// issuer, by name and authority key identifier."
    /// </para>
    /// <para>
    /// That is not a library defect. EAA-6.2.10.1-06.2 (Implementing Regulation (EU) 2024/2979,
    /// Annex II as replaced by (EU) 2026/1731) requires the top of the status list's chain to be
    /// signed by whatever signed the Document Signer. The German PID test issuer signs the two under
    /// sibling CAs under one root -- PID-Signer Test CA 1-26-1 and PID-Status-List-Signer Test CA
    /// 1-26-2 -- so the rule cannot hold however the trust anchors are configured, because it
    /// compares issuer names and signatures rather than reachability within a trust list. Adding the
    /// status list CA to the trust store, which is what the error makes one try first, changes
    /// nothing.
    /// </para>
    /// <para>
    /// The regulation leaves an issuer one way to use a separate status list CA: carry the
    /// <c>certificate</c> element in the MSO's revocation reference (-06.1), which anchors the list
    /// on its own. This PID does not carry it. Asked upstream in case there is a route neither side
    /// sees; until then, the backend path is the one that checks revocation.
    /// </para>
    /// </remarks>
    public bool CheckRevocation { get; set; }

    /// <summary>
    /// Only for a local smoke test with a throwaway key. HAIP 1.0 section 5 says the certificate
    /// signing the request must not be self-signed, and a real wallet enforces it.
    /// </summary>
    public bool AllowSelfSignedAccessCertificate { get; set; }

    /// <summary>
    /// Throws when something required is missing, naming the setting. Called at startup so a
    /// misconfiguration stops the process instead of the first wallet to scan a code.
    /// </summary>
    public void Validate()
    {
        Require(PublicBaseUrl, nameof(PublicBaseUrl));
        Require(AccessCertificatePath, nameof(AccessCertificatePath));
        Require(IssuerTrustAnchorDirectory, nameof(IssuerTrustAnchorDirectory));

        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var baseUrl))
        {
            throw new InvalidOperationException(
                $"'{nameof(PublicBaseUrl)}' must be an absolute URL, and '{PublicBaseUrl}' is not.");
        }

        if (baseUrl.IsLoopback)
        {
            throw new InvalidOperationException(
                $"'{nameof(PublicBaseUrl)}' is a loopback address ('{PublicBaseUrl}'). The wallet "
                + "fetches the request object and posts the response over the internet, so it must "
                + "be an address the phone can reach.");
        }

        if (string.IsNullOrWhiteSpace(RegistrationCertificate)
            && string.IsNullOrWhiteSpace(RegistrationCertificatePath))
        {
            throw new InvalidOperationException(
                $"Either '{nameof(RegistrationCertificate)}' or '{nameof(RegistrationCertificatePath)}' "
                + "must be set. The German wallet drops a request that carries none.");
        }

        // Empty is refused, null is not: null means "use the default", an empty list means a caller
        // built one and lost its contents on the way, which would ask the wallet for nothing.
        if (RequestedClaims is { Count: 0 })
        {
            throw new InvalidOperationException(
                $"'{nameof(RequestedClaims)}' is empty, so the wallet would be asked for nothing. "
                + $"Leave it unset for the default ({nameof(DefaultRequestedClaims)}).");
        }

        if (!Directory.Exists(IssuerTrustAnchorDirectory))
        {
            throw new InvalidOperationException(
                $"The trust anchor directory '{IssuerTrustAnchorDirectory}' does not exist.");
        }

        if (!File.Exists(AccessCertificatePath))
        {
            throw new InvalidOperationException(
                $"The access certificate '{AccessCertificatePath}' does not exist.");
        }

        if (RegistrationCertificatePath is { Length: > 0 } path
            && string.IsNullOrWhiteSpace(RegistrationCertificate)
            && !File.Exists(path))
        {
            throw new InvalidOperationException($"The registration certificate '{path}' does not exist.");
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"'{name}' is required for the in-process verifier.");
        }
    }
}
