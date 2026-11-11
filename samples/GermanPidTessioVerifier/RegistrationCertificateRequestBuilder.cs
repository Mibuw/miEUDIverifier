using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tessio.Verifier.OpenId4Vp;

namespace GermanPidTessioVerifier;

/// <summary>
/// Builds the signed request object for the German EUDI Wallet, which will not present against a
/// request that carries no registration certificate.
/// </summary>
/// <remarks>
/// <para>
/// Why this class exists rather than Tessio's own <c>SignedPresentationRequestBuilder</c>: the
/// registration certificate travels in the <c>verifier_info</c> claim, inside the signature, and
/// release 0.30.2 has no option to put it there (upstream issue 185, pull request 187). Once that
/// lands, this file can be deleted and the built-in builder configured instead.
/// </para>
/// <para>
/// The claim set below was taken from a request object the EUDI reference implementation produced
/// for a German PID presentation and compared claim by claim, so the wallet sees the same request it
/// would get from that backend.
/// </para>
/// </remarks>
public sealed class RegistrationCertificateRequestBuilder : IPresentationRequestBuilder
{
    private readonly X509Certificate2 _accessCertificate;
    private readonly string _registrationCertificate;
    private readonly Uri _requestUriBase;
    private readonly TimeSpan _lifetime;

    /// <param name="accessCertificate">
    /// The relying-party access certificate with its private key. The SHA-256 of its DER is the
    /// <c>x509_hash</c> client identifier, so this is the certificate the wallet pins the request to.
    /// </param>
    /// <param name="registrationCertificate">
    /// The registration certificate as the reference implementation delivers it: a compact JWT whose
    /// <c>typ</c> header is <c>rc-wrp+jwt</c>. Sent verbatim, because the wallet verifies a signature
    /// over these exact bytes.
    /// </param>
    /// <param name="requestUriBase">
    /// Where the host serves the JAR, publicly reachable. Tessio's hosted endpoints serve it under
    /// <c>{routePrefix}/request/{id}</c> and take the id from this URI's last segment.
    /// </param>
    /// <param name="lifetime">How long the request stays valid.</param>
    public RegistrationCertificateRequestBuilder(
        X509Certificate2 accessCertificate,
        string registrationCertificate,
        Uri requestUriBase,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(accessCertificate);
        ArgumentNullException.ThrowIfNull(requestUriBase);
        if (string.IsNullOrWhiteSpace(registrationCertificate))
        {
            throw new ArgumentException(
                "An empty registration certificate is not one. The German wallet drops a request without it.",
                nameof(registrationCertificate));
        }

        if (!accessCertificate.HasPrivateKey)
        {
            throw new ArgumentException(
                "The access certificate has no private key, so it cannot sign the request object.",
                nameof(accessCertificate));
        }

        _accessCertificate = accessCertificate;
        _registrationCertificate = registrationCertificate.Trim();
        _requestUriBase = requestUriBase;
        _lifetime = lifetime;
    }

    /// <summary>The <c>x509_hash</c> client identifier this builder's certificate produces.</summary>
    public string ClientId => ClientIdentifier.X509Hash(_accessCertificate);

    /// <inheritdoc />
    public Task<PresentationRequest> BuildAsync(
        PresentationRequestOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = issuedAt + (options.RequestLifetime ?? _lifetime);

        var payload = new JsonObject
        {
            ["client_id"] = ClientId,
            ["response_type"] = "vp_token",
            ["response_mode"] = options.ResponseMode == ResponseMode.DirectPostJwt
                ? "direct_post.jwt"
                : "direct_post",
            ["response_uri"] = options.ResponseUri.ToString(),
            ["nonce"] = options.Nonce,
            ["dcql_query"] = JsonNode.Parse(options.DcqlQueryJson),
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),

            // SPEC: OpenID4VP 1.0 section 5.8 -- under static wallet discovery the audience is this
            // fixed string, not the wallet's own identifier.
            ["aud"] = "https://self-issued.me/v2",
        };

        if (options.State is not null)
        {
            payload["state"] = options.State;
        }

        if (options.ClientMetadataJson is not null)
        {
            payload["client_metadata"] = JsonNode.Parse(options.ClientMetadataJson);
        }

        // SPEC: OpenID4VP 1.0 section 5.11 -- verifier_info is a non-empty array of attestations about
        // the verifier. The reference implementation sends exactly one element for the registration
        // certificate, with this format identifier.
        payload["verifier_info"] = new JsonArray(
            new JsonObject
            {
                ["format"] = "registration_cert",
                ["data"] = _registrationCertificate,
            });

        var requestObject = Sign(payload);
        var requestUri = new Uri(
            $"{_requestUriBase.ToString().TrimEnd('/')}/{Base64Url(RandomNumberGenerator.GetBytes(16))}");

        // SPEC: OpenID4VP 1.0 section 5 / RFC 9101 -- by-reference delivery points the wallet at
        // request_uri. request_uri_method is left out, so the wallet uses GET, which is what the host
        // serves and what the German wallet was verified against.
        PresentationRequest request = new PresentationRequest.ByReference
        {
            ClientId = ClientId,
            Nonce = options.Nonce,
            State = options.State,
            AuthorizationRequestUri = new Uri(
                "openid4vp://?client_id=" + Uri.EscapeDataString(ClientId) +
                "&request_uri=" + Uri.EscapeDataString(requestUri.ToString())),
            SignedRequestObject = requestObject,
            RequestUri = requestUri,
            ExpiresAt = expiresAt,
        };

        return Task.FromResult(request);
    }

    private string Sign(JsonObject payload)
    {
        // SPEC: OpenID4VP 1.0 section 5 / RFC 9101 -- "Verifiers MUST include the typ Header Parameter
        // in Request Objects with the value oauth-authz-req+jwt".
        // SPEC: RFC 7515 section 4.1.6 -- x5c is base64 (not base64url) DER, the leaf first.
        var header = new JsonObject
        {
            ["alg"] = "ES256",
            ["typ"] = "oauth-authz-req+jwt",
            ["x5c"] = new JsonArray(Convert.ToBase64String(_accessCertificate.RawData)),
        };

        var signingInput =
            Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." +
            Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));

        using var key = _accessCertificate.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException(
                "The access certificate's private key is not an EC key, so it cannot sign ES256.");

        // SignData returns the IEEE P1363 fixed-field concatenation (r||s), which is what JWS wants.
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);

        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
