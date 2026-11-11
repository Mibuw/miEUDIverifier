using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using miEUDIverifier.Models;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Tessio.Verifier.AspNetCore;
using Tessio.Verifier.Core.Mdoc;
using Tessio.Verifier.OpenId4Vp;

namespace miEUDIverifier.Services.Tessio;

/// <summary>
/// Runs a verification in this process: builds and serves the request, and verifies the wallet's
/// response, with no verifier backend behind it.
/// </summary>
/// <remarks>
/// <para>
/// What makes this possible is holding the relying-party access certificate ourselves. The EU
/// ecosystem path cannot work this way, because there the hosted backend is the verifier; see
/// <see cref="IVerifierEngine"/>.
/// </para>
/// <para>
/// The division of labour with the library: it owns the session, the request's claim set and
/// signature, the response's decryption and the credential's verification. This class owns the
/// per-request inputs (nonce, state, the DCQL query, the response URI) and the session's lifecycle
/// as the application sees it. Those inputs are assembled here rather than taken from the library
/// because the factory that builds them, and the store that serves request objects, are both
/// internal to the package -- see <see cref="HostedRequestObjects"/>.
/// </para>
/// <para>
/// mdoc only. The German wallet presents the PID as an mdoc, which is the format the registration
/// certificate used here grants and the one that has been verified end to end. The backend path
/// additionally offers SD-JWT VC as an alternative; this one does not, because
/// <c>VerifierOptions.CredentialFormat</c> names a single format.
/// </para>
/// </remarks>
public sealed class TessioVerifierEngine : IVerifierEngine
{
    private readonly ISessionStore _sessions;
    private readonly ResponseEncryptionKeyStore _responseKeys;
    private readonly HostedRequestObjects _requestObjects;
    private readonly RegistrationCertificateRequestBuilder _requestBuilder;
    private readonly TessioEngineOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TessioVerifierEngine> _logger;

    /// <summary>Wires the engine to the library's session store and this host's own pieces.</summary>
    public TessioVerifierEngine(
        ISessionStore sessions,
        ResponseEncryptionKeyStore responseKeys,
        HostedRequestObjects requestObjects,
        RegistrationCertificateRequestBuilder requestBuilder,
        TessioEngineOptions options,
        TimeProvider clock,
        ILogger<TessioVerifierEngine> logger)
    {
        _sessions = sessions;
        _responseKeys = responseKeys;
        _requestObjects = requestObjects;
        _requestBuilder = requestBuilder;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<InitTransactionResponse> InitializeTransactionAsync(
        TransactionOptions? options = null,
        CancellationToken ct = default)
    {
        var claims = options?.PidClaims is { Count: > 0 } requested
            ? requested.ToArray()
            : _options.RequestedClaims.ToArray();

        var expiresAt = _clock.GetUtcNow() + _options.SessionLifetime;

        // SPEC: HAIP 1.0 section 5 -- a FRESH response encryption key per request. Reusing one would
        // be a correlation handle across presentations and a single point of retrospective
        // compromise. The library holds it and finds it again from the kid the wallet echoes back.
        JsonObject? responseEncryptionJwk = _options.ResponseMode == ResponseMode.DirectPostJwt
            ? _responseKeys.CreateForRequest(expiresAt).PublicJwk
            : null;

        var requestOptions = new PresentationRequestOptions
        {
            // The builder replaces this with the x509_hash over its own access certificate. Passed
            // anyway so the value is never empty if the builder is ever swapped for one that
            // honours what it is given.
            ClientId = _requestBuilder.ClientId,
            Nonce = NewNonce(),
            State = NewNonce(),
            DcqlQueryJson = Dcql.Mdoc(_options.DocType, _options.MdocNamespace, claims),
            ResponseUri = _options.ResponseUri,
            ResponseMode = _options.ResponseMode,
            RequestLifetime = _options.SessionLifetime,
            ClientMetadataJson = ClientMetadata.Build(MdocVerifier.Format, responseEncryptionJwk),
        };

        // Creating the session runs the request builder, so the signed request object exists from
        // here on. The session also records the state the wallet's response is correlated by.
        var session = await _sessions.CreateAsync(requestOptions, ct).ConfigureAwait(false);

        if (session.Request is not PresentationRequest.ByReference byReference)
        {
            throw new InvalidOperationException(
                "The request builder returned the request by value. This engine serves the request "
                + "object from its own endpoint, so it must be delivered by reference.");
        }

        // The last segment of the URI the builder produced is the id the endpoint will look up.
        var requestId = byReference.RequestUri.Segments[^1].TrimEnd('/');
        _requestObjects.Put(requestId, byReference.SignedRequestObject, byReference.ExpiresAt);

        // The request id is logged alongside the session: it is what appears in the wallet's fetch of
        // the request object, and without it a 404 in an access log cannot be tied to a session.
        _logger.LogInformation(
            "Verification session {SessionId} started in process, {ClaimCount} attribute(s) of "
            + "{DocType}, request object {RequestId}, expires {ExpiresAt:O}",
            session.SessionId, claims.Length, _options.DocType, requestId, session.ExpiresAt);

        return new InitTransactionResponse
        {
            TransactionId = session.SessionId,
            ClientId = _requestBuilder.ClientId,
            RequestUri = byReference.RequestUri.ToString(),
            // By reference and fetched with GET, which is what the builder produces and what the
            // deep link must therefore announce.
            RequestUriMethod = "get",
        };
    }

    /// <inheritdoc />
    public async Task<WalletResponseEnvelope> WaitForWalletResponseAsync(
        string transactionId,
        IProgress<string>? progress = null,
        Action<string>? onRawResponse = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        var interval = _options.PollInterval;
        var deadline = _clock.GetUtcNow() + _options.SessionLifetime + interval;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var session = await _sessions.GetAsync(transactionId, ct).ConfigureAwait(false);
            if (session is null)
            {
                // Gone rather than pending: the store drops a session once it has expired, and
                // waiting longer cannot bring it back.
                return TimedOut(transactionId);
            }

            switch (session.Status)
            {
                // Completed means verification RAN, not that it passed -- the library's own remark on
                // the enum says to read IsValid. This deployment sets CompleteOnlyOnValidResponse, so
                // a rejected presentation leaves the session pending for another try and this should
                // not happen; it is still read rather than assumed, because a configuration change
                // elsewhere would otherwise turn a refusal into a reported success.
                case VerificationSessionStatus.Completed when session.Result?.IsValid == true:
                    return Submitted(session, onRawResponse);

                case VerificationSessionStatus.Completed:
                    return Refused(session, onRawResponse);

                case VerificationSessionStatus.Expired:
                    return Refused(session, onRawResponse);

                default:
                    progress?.Report($"waiting for the wallet ({session.Status})");
                    break;
            }

            if (_clock.GetUtcNow() >= deadline)
            {
                return TimedOut(transactionId);
            }

            await Task.Delay(interval, _clock, ct).ConfigureAwait(false);
        }
    }

    private static WalletResponseEnvelope TimedOut(string transactionId) => new()
    {
        TransactionId = transactionId,
        Status = "timed_out",
    };

    /// <summary>
    /// A verified presentation, shaped as the application already reads it.
    /// </summary>
    /// <remarks>
    /// The claims come across as namespace to element to value, which is exactly the shape the
    /// reference backend puts in a credential's <c>attributes</c> for an mdoc. So the reading of
    /// them is shared with the backend path rather than written twice -- see
    /// <see cref="ExtractIdentityDataAsync"/>.
    /// </remarks>
    private static WalletResponseEnvelope Submitted(VerificationSession session, Action<string>? onRawResponse)
    {
        var result = session.Result;
        var attributes = result?.DisclosedClaims is { Count: > 0 } claims
            ? JsonSerializer.SerializeToElement(claims)
            : (JsonElement?)null;

        onRawResponse?.Invoke(Describe(session));

        return new WalletResponseEnvelope
        {
            TransactionId = session.SessionId,
            Status = "submitted",
            Credentials =
            [
                new VerifiedCredential
                {
                    CredentialId = result?.CredentialQueryId ?? "credential",
                    Format = MdocVerifier.Format,
                    DocType = result?.CredentialType,
                    Attributes = attributes,
                },
            ],
        };
    }

    private static WalletResponseEnvelope Refused(VerificationSession session, Action<string>? onRawResponse)
    {
        onRawResponse?.Invoke(Describe(session));

        var failure = session.Result ?? session.LastFailure;
        var errors = failure?.Errors ?? [];

        return new WalletResponseEnvelope
        {
            TransactionId = session.SessionId,
            Status = session.Status == VerificationSessionStatus.Expired ? "timed_out" : "failed",
            // The codes, which name what went wrong. Joined because this field is one line in a UI.
            Error = errors.Count > 0
                ? string.Join(", ", errors.Select(e => e.Code))
                : "verification_failed",
            ErrorDescription = errors.Count > 0
                ? string.Join("; ", errors.Select(e => e.Message))
                : null,
        };
    }

    /// <summary>
    /// What <c>onRawResponse</c> is handed: the verdict and the issuer, never a claim value. The
    /// backend path passes the backend's raw JSON there, which does include the attributes; this
    /// engine deliberately does not, because the caller shows it in a diagnostics pane.
    /// </summary>
    private static string Describe(VerificationSession session)
    {
        var result = session.Result ?? session.LastFailure;
        var payload = new JsonObject
        {
            ["session_id"] = session.SessionId,
            ["status"] = session.Status.ToString(),
            ["engine"] = "tessio-in-process",
            ["is_valid"] = result?.IsValid,
            ["credential_type"] = result?.CredentialType,
            ["issuer"] = result?.Issuer?.Identifier,
            ["issuer_trusted"] = result?.Issuer?.Trusted,
            ["trust_anchor"] = result?.Issuer?.TrustAnchorSubject,
            ["disclosed_claim_count"] = result?.DisclosedClaims?.Count,
            ["errors"] = new JsonArray(
                (result?.Errors ?? []).Select(e => (JsonNode)new JsonObject
                {
                    ["code"] = e.Code,
                    ["message"] = e.Message,
                }).ToArray()),
        };

        return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <inheritdoc />
    public Task<IdentityData> ExtractIdentityDataAsync(
        WalletResponseEnvelope envelope,
        TransactionOptions? options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        var identity = new IdentityData();

        var credential = envelope.Credentials?.FirstOrDefault();
        if (credential?.Attributes is not { } attributes)
        {
            // Nothing taken. A null CredentialType is how the caller tells "no credential of the
            // requested type" from "the data was incomplete", so it is left unset.
            return Task.FromResult(identity);
        }

        // Checked here too, rather than trusted because the library verified something. The library
        // was told which doctype to expect, so a mismatch means the engine and the transaction were
        // configured apart -- which is exactly the case worth catching.
        if (!string.Equals(credential.DocType, _options.DocType, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Presentation carried docType {Actual}, which is not the requested {Expected}; nothing taken from it",
                credential.DocType ?? "(none)", _options.DocType);
            return Task.FromResult(identity);
        }

        identity.CredentialFormat = credential.Format;

        // Shared with the backend path on purpose: Tessio reports an mdoc's claims in the same
        // namespace-to-element shape, so one reader serves both, and the German PID's quirks -- a
        // structured place of birth, nationality as a list -- are handled in one place.
        VerifierApiService.ExtractFromMdoc(attributes, identity);

        // A presentation that disclosed something but mapped to nothing means the library changed
        // how it reports an mdoc's claims, which is the one assumption this engine makes about it.
        // Silent here would surface as a blank result page with a valid credential behind it, so it
        // is said out loud, with the shape that was actually received.
        if (identity is { FamilyName: null, GivenName: null, BirthDate: null }
            && attributes.ValueKind == JsonValueKind.Object
            && attributes.EnumerateObject().Any())
        {
            _logger.LogWarning(
                "A valid {DocType} presentation disclosed claims that mapped to no identity data. "
                + "Expected namespace-to-element nesting; the top-level names were: {Names}",
                credential.DocType,
                string.Join(", ", attributes.EnumerateObject().Select(p => p.Name)));
        }

        identity.CredentialType = credential.DocType;
        return Task.FromResult(identity);
    }

    /// <summary>
    /// A one-time random value for the request's nonce and state.
    /// </summary>
    /// <remarks>
    /// Minted here because the library's own <c>Tokens</c> helper is internal to the package. 256
    /// bits from the system CSPRNG, base64url so it survives a query string.
    /// </remarks>
    private static string NewNonce()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncoder.Encode(bytes);
    }
}
