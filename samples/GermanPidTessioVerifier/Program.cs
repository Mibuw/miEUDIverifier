using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Cryptography.X509Certificates;
using GermanPidTessioVerifier;
using Tessio.Verifier.AspNetCore;
using Tessio.Verifier.Core;
using Tessio.Verifier.Core.Mdoc;
using Tessio.Verifier.OpenId4Vp;
using Tessio.Verifier.Trust;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("GermanPid");

// ── The three things a verifier must bring itself ────────────────────────────────────────
// 1. the access certificate, whose SHA-256 is the client_id the wallet pins the request to
// 2. the registration certificate, which says what this verifier is allowed to ask for
// 3. the trust anchors of the PID issuer, without which a valid credential is still untrusted

// The whole chain, not just the leaf. HAIP 1.0 section 5 and ETSI TS 119 472-2 clause 6.4.2 want
// the access certificate AND its path up to, but excluding, the trust anchor in the JAR's x5c,
// and a wallet that is handed only the leaf may not be able to build a path to its trust list.
var accessCertificateChain = AccessCertificateChain.LoadFromPkcs12(
    Required(settings, "AccessCertificatePath"),
    settings["AccessCertificatePassword"]);

var registrationCertificate = settings["RegistrationCertificate"] is { Length: > 0 } inline
    ? inline
    : File.ReadAllText(Required(settings, "RegistrationCertificatePath")).Trim();

var anchorDirectory = Required(settings, "IssuerTrustAnchorDirectory");
var anchors = Directory.EnumerateFiles(anchorDirectory)
    .Where(f => Path.GetExtension(f) is ".pem" or ".crt" or ".cer" or ".der")
    .Select(X509CertificateLoader.LoadCertificateFromFile)
    .ToList();

if (anchors.Count == 0)
{
    throw new InvalidOperationException(
        $"No issuer trust anchor found in '{anchorDirectory}'. Without the PID issuer's CA certificate " +
        "every presentation verifies cryptographically and is still refused as untrusted.");
}

// The public base URL matters: the wallet fetches request_uri and posts to response_uri over the
// internet, so neither may be a localhost address. Behind a reverse proxy this is the outside name.
var publicBaseUrl = Required(settings, "PublicBaseUrl").TrimEnd('/');
const string RoutePrefix = "/verify";

// ── Replace two of Tessio's defaults, BEFORE AddTessioVerifier ───────────────────────────
// Both are registered with TryAdd, so whatever is in the container first wins.

// The request builder, because release 0.30.2 cannot put a registration certificate in
// verifier_info yet (upstream issue 185, pull request 187).
builder.Services.AddSingleton<IPresentationRequestBuilder>(_ => new RegistrationCertificateRequestBuilder(
    accessCertificateChain,
    registrationCertificate,
    new Uri($"{publicBaseUrl}{RoutePrefix}/request"),
    TimeSpan.FromMinutes(5),
    // Only ever true for a local smoke test. A real wallet refuses a self-signed signer.
    allowSelfSignedAccessCertificate: settings.GetValue("AllowSelfSignedAccessCertificate", false)));

// The trust resolver, because the default one trusts the library's own mock issuers and nothing else.
builder.Services.AddSingleton<ITrustListResolver>(_ => new StaticTrustListResolver(
    trustedIssuers: [],
    source: anchorDirectory,
    trustAnchors: anchors,
    clock: TimeProvider.System));

// Revocation checking is OFF, and that is a real gap, not a preference. It is the difference
// between "was valid once" and "is valid now", so read this before copying the setting.
//
// Turning it on makes every German PID presentation fail with status_invalid:
//
//   The top of the MSO revocation list's chain was not issued by the credential's Document
//   Signer's issuer, by name and authority key identifier.
//
// That is not a library defect, and it took reading the library's source to see why. The rule
// comes from EAA-6.2.10.1-06.2 (Implementing Regulation (EU) 2024/2979, Annex II as replaced by
// (EU) 2026/1731): the top certificate of the status list's chain "shall be signed or sealed by
// the certificate used to sign the certificate in the x5chain element of the MSO" -- that is, by
// whatever signed the Document Signer. The German PID test issuer signs the two under sibling CAs
// under one root:
//
//   Deutschland PID-Provider Root Test CA 1 2026                 the root
//   +- Deutschland PID-Signer Test CA 1-26-1 2026                signs the Document Signer
//   +- Deutschland PID-Status-List-Signer Test CA 1-26-2 2026     signs the status list
//
// 1-26-2 was signed by the root, not by 1-26-1, so the rule cannot hold however the trust anchors
// are configured: it compares issuer names and signatures, not reachability within a trust list.
// Adding 1-26-2 to the trust store -- the obvious reading of the error -- changes nothing.
//
// The regulation leaves an issuer exactly one way to use a separate status list CA: carry the
// certificate element in the MSO's revocation reference (-06.1), which then anchors the list on
// its own. This PID does not carry it. On that reading it is the issuer's side that is out of
// line, which is worth knowing before anyone spends a day on trust anchors. Asked upstream in
// case there is a route neither of us sees.
//
// Until then the choice is this flag or no presentations at all, and a test verifier can live
// with the first. A production verifier cannot: a revoked PID would be accepted here.
builder.Services.AddSingleton(sp => new MdocVerifier(
    sp.GetRequiredService<ITrustListResolver>(),
    new MdocVerifierOptions { CheckStatus = false },
    TimeProvider.System));

// Behind a reverse proxy or a tunnel the application sees a plain HTTP connection, and the host
// builds response_uri from what it sees. Without this the wallet is told to post the presentation
// to an http:// address, which it refuses -- and should.
//
// The proxy is not named here because a tunnel's address is not fixed. That is only safe because
// the container publishes on 127.0.0.1 and nothing but the proxy can reach it; a verifier exposed
// directly must name its proxies instead, or a caller can forge these headers.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders =
        ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedFor;
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

builder.Services.AddTessioVerifier(options =>
{
    options.Mode = VerifierMode.Live;
    options.RoutePrefix = RoutePrefix;

    // The German PID is an mdoc, and its namespace equals its doctype.
    options.CredentialFormat = MdocVerifier.Format;
    options.ExpectedDocType = "eu.europa.ec.eudi.pid.1";
    options.MdocNamespace = "eu.europa.ec.eudi.pid.1";

    // Ask for exactly what the registration certificate grants. One attribute more and the wallet's
    // policy engine drops the request with no consent screen, which looks like a certificate fault.
    options.RequestedClaims = new List<string>
    {
        "family_name",
        "given_name",
        "birth_date",
        "place_of_birth",
        "nationality",
        "issuing_authority",
        "issuing_country",
    };

    // The German wallet answers encrypted, and the key is per request.
    options.ResponseMode = ResponseMode.DirectPostJwt;

    // Only a presentation that actually verified completes the session.
    options.CompleteOnlyOnValidResponse = true;
});

var app = builder.Build();

// Before anything that reads the request's scheme or host.
app.UseForwardedHeaders();

app.MapTessioVerifier();

// A plain starting point: the hosted pages render the QR code and poll the session themselves.
app.MapGet("/", () => Results.Redirect($"{RoutePrefix}/start"));

// Optional, off by default. The hosted session endpoint withholds failure messages ("the failure
// message is not disclosed on this surface") because whoever holds a session id is not
// necessarily the operator, and the callback's log line carries the error codes only. That is the
// right default, and it is not enough to debug with: status_invalid alone stands for a status
// list signer the verifier does not trust, an index outside the published list, a token whose
// signature or typ is wrong, and more. Finding out which otherwise costs one live scan per guess.
//
// This reads the recorded result and discloses the messages. Claim values are never returned.
// Do not enable it on anything reachable from outside, and prefer decorating nothing: the store
// also implements IStateCorrelatingSessionStore and ISameDeviceSessionStore, so a decorator that
// only implements ISessionStore breaks the wallet callback outright.
if (builder.Configuration.GetValue("GermanPid:DiagnosticsEndpoint", false))
{
    app.MapGet("/diagnostics/session/{sessionId}", async (string sessionId, ISessionStore store) =>
    {
        var session = await store.GetAsync(sessionId);
        if (session is null) return Results.NotFound();

        static object? Describe(VerificationResult? r) => r is null ? null : new
        {
            r.IsValid,
            r.CredentialType,
            issuer = r.Issuer?.Identifier,
            trusted = r.Issuer?.Trusted,
            anchor = r.Issuer?.TrustAnchorSubject,
            // Codes AND messages. The claim VALUES stay out: they are the holder's personal data.
            claimNames = r.DisclosedClaims?.Keys.Order().ToArray() ?? [],
            errors = r.Errors?.Select(e => new { e.Code, e.Message }).ToArray() ?? [],
        };

        return Results.Json(new
        {
            session.SessionId,
            status = session.Status.ToString(),
            session.FailedAttempts,
            result = Describe(session.Result),
            lastFailure = Describe(session.LastFailure),
        });
    });

    app.Logger.LogWarning(
        "Diagnostics endpoint is ENABLED under /diagnostics/session. It discloses verification "
        + "failure messages. Turn GermanPid:DiagnosticsEndpoint off when done.");
}

app.Logger.LogInformation(
    "German PID verifier ready. client_id {ClientId}, {ChainLength} certificate(s) in x5c, " +
    "{AnchorCount} issuer trust anchor(s) from {Directory}.",
    ClientIdentifier.X509Hash(accessCertificateChain[0]), accessCertificateChain.Count,
    anchors.Count, anchorDirectory);

app.Run();

static string Required(IConfiguration settings, string key) =>
    settings[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"Configuration 'GermanPid:{key}' is missing. See samples/GermanPidTessioVerifier/README.md.");
