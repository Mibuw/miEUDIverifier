using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;
using Tessio.Verifier.OpenId4Vp;

namespace GermanPidTessioVerifier;

/// <summary>
/// Adds the registration certificate the German EUDI Wallet insists on to the request object
/// Tessio builds, by re-signing it with one claim more.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: the registration certificate travels in <c>verifier_info</c>, which is inside
/// the signature, and release 0.30.2 has no option to put it there (upstream issue 185, pull
/// request 187). When that lands, delete this class and register
/// <see cref="SignedPresentationRequestBuilder"/> directly with <c>VerifierInfoJson</c> set.
/// </para>
/// <para>
/// Why it delegates rather than building the request itself. An earlier version assembled the
/// whole claim set and signed it, which meant owning a copy of everything the library does: the
/// claim set, the certificate path rules, the JOSE header. That copy was already wrong in one
/// way that only a wallet would have revealed -- it put a single certificate in <c>x5c</c>, while
/// HAIP 1.0 section 5 and ETSI TS 119 472-2 clause 6.4.2 require the access certificate <em>and
/// its path up to, but excluding, the trust anchor</em>, which is what the EUDI reference
/// implementation sends. Delegating means the header, the chain and every claim come from the
/// library, and a protocol member it gains in a later release arrives here for free. What is left
/// below is one claim and one signature.
/// </para>
/// </remarks>
public sealed class RegistrationCertificateRequestBuilder : IPresentationRequestBuilder, IDisposable
{
    private readonly SignedPresentationRequestBuilder _inner;
    private readonly ECDsa _signingKey;
    private readonly string _registrationCertificate;

    /// <param name="accessCertificateChain">
    /// The relying-party access certificate first, then each certificate that issued the one
    /// before it, stopping before the trust anchor. The first one must hold the private key; the
    /// SHA-256 over its DER is the <c>x509_hash</c> client identifier.
    /// </param>
    /// <param name="registrationCertificate">
    /// The registration certificate as the reference implementation delivers it: a compact JWT
    /// whose <c>typ</c> header is <c>rc-wrp+jwt</c>. Sent verbatim, because the wallet verifies a
    /// signature over these exact bytes.
    /// </param>
    /// <param name="requestUriBase">
    /// Where the host serves the JAR, publicly reachable. Tessio's hosted endpoints serve it under
    /// <c>{routePrefix}/request/{id}</c> and take the id from the last segment of the URI this
    /// produces.
    /// </param>
    /// <param name="lifetime">How long a request stays valid when the caller names no lifetime.</param>
    /// <param name="allowSelfSignedAccessCertificate">
    /// Only for a local smoke test with a throwaway key. HAIP 1.0 section 5 says the certificate
    /// signing the request must not be self-signed, and a real wallet enforces it.
    /// </param>
    public RegistrationCertificateRequestBuilder(
        IReadOnlyList<X509Certificate2> accessCertificateChain,
        string registrationCertificate,
        Uri requestUriBase,
        TimeSpan lifetime,
        bool allowSelfSignedAccessCertificate = false)
    {
        ArgumentNullException.ThrowIfNull(accessCertificateChain);
        ArgumentNullException.ThrowIfNull(requestUriBase);
        if (accessCertificateChain.Count == 0)
        {
            throw new ArgumentException("The access certificate chain is empty.", nameof(accessCertificateChain));
        }

        if (string.IsNullOrWhiteSpace(registrationCertificate))
        {
            throw new ArgumentException(
                "An empty registration certificate is not one. The German wallet drops a request without it.",
                nameof(registrationCertificate));
        }

        var leaf = accessCertificateChain[0];
        _signingKey = leaf.GetECDsaPrivateKey()
            ?? throw new ArgumentException(
                leaf.HasPrivateKey
                    ? "The access certificate's private key is not an EC key, so it cannot sign ES256."
                    : "The access certificate has no private key, so it cannot sign the request object.",
                nameof(accessCertificateChain));

        _registrationCertificate = registrationCertificate.Trim();

        // The library checks the chain's shape here, in the constructor: ordered, linked, the leaf
        // not a CA and not self-signed, the trust anchor left out. A misconfigured certificate
        // therefore fails at startup instead of silently at the wallet.
        _inner = new SignedPresentationRequestBuilder(new PresentationRequestBuilderOptions
        {
            SigningCredentials = new SigningCredentials(
                new ECDsaSecurityKey(_signingKey), SecurityAlgorithms.EcdsaSha256),
            SigningCertificateChain = accessCertificateChain,
            AllowSelfSignedSigningCertificate = allowSelfSignedAccessCertificate,
            RequestUriBase = requestUriBase,
            DefaultRequestLifetime = lifetime,
        });

        ClientId = ClientIdentifier.X509Hash(leaf);
    }

    /// <summary>The <c>x509_hash</c> client identifier this builder's access certificate produces.</summary>
    public string ClientId { get; }

    /// <inheritdoc />
    public async Task<PresentationRequest> BuildAsync(
        PresentationRequestOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var request = await _inner.BuildAsync(options with { ClientId = ClientId }, ct).ConfigureAwait(false);

        // By-reference only. Under by-value delivery the authorization URI carries the JAR itself,
        // so replacing the JAR would leave the URI pointing at the old one. The German wallet
        // fetches request_uri anyway, and the host is configured for it.
        if (request is not PresentationRequest.ByReference byReference)
        {
            throw new InvalidOperationException(
                "This builder delivers the request by reference. Configure the host's request URI base.");
        }

        return byReference with { SignedRequestObject = ReSignWithVerifierInfo(byReference.SignedRequestObject) };
    }

    /// <summary>
    /// Takes the library's own request object apart, adds <c>verifier_info</c> and signs it again.
    /// The JOSE header is reused byte for byte, so <c>typ</c> and the <c>x5c</c> chain stay exactly
    /// as the library produced them.
    /// </summary>
    private string ReSignWithVerifierInfo(string requestObject)
    {
        var parts = requestObject.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException(
                "The library produced a request object that is not a compact JWS; cannot add verifier_info.");
        }

        var payload = JsonNode.Parse(Base64UrlEncoder.DecodeBytes(parts[1]))!.AsObject();

        // SPEC: OpenID4VP 1.0 section 5.11 -- verifier_info is a non-empty array of attestations
        // about the verifier. The reference implementation sends one element for the registration
        // certificate, with this format identifier and the certificate as an opaque string.
        payload["verifier_info"] = new JsonArray(
            new JsonObject
            {
                ["format"] = "registration_cert",
                ["data"] = _registrationCertificate,
            });

        var signingInput = parts[0] + "." + Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload));

        // SignData returns the IEEE P1363 fixed-field concatenation (r||s), which is what JWS wants.
        var signature = _signingKey.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);

        return signingInput + "." + Base64UrlEncoder.Encode(signature);
    }

    /// <inheritdoc />
    public void Dispose() => _signingKey.Dispose();
}
