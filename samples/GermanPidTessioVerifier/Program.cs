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

// Revocation is checked when the MSO carries a status element; it costs a network call per
// presentation and is the difference between "was valid once" and "is valid now".
builder.Services.AddSingleton(sp => new MdocVerifier(
    sp.GetRequiredService<ITrustListResolver>(),
    new MdocVerifierOptions { CheckStatus = true },
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
