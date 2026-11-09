// Minimal PID verification with the miEUDIverifier.Core NuGet package.
//
// This sample does not reference anything from the miEUDIverifier repository — it pulls
// `miEUDIverifier.Core` from NuGet like any other consumer would. It runs one verification
// end to end: start a transaction, show the deep link as a QR code, wait for the wallet,
// print the attributes.
//
// Three things worth knowing before reading on, because none of them lives in this process:
//
//   * The ENDPOINT is `VerifierSettings:BackendUrl` below — the REST API of an EUDI verifier
//     backend. This library is a client of that backend, not a verifier in its own right.
//   * The CERTIFICATES are not configured here at all. The access certificate that signs the
//     request object belongs to the backend; see ../README.md.
//   * The RESPONSE URL the wallet posts to is derived by the backend from its own
//     `VERIFIER_PUBLICURL`. Nothing in this process can set it.

using miEUDIverifier;
using miEUDIverifier.Configuration;
using miEUDIverifier.Models;
using miEUDIverifier.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ── 1. Configuration ──────────────────────────────────────────────────────────
// The library reads one section, "VerifierSettings". Where that section comes from is up to
// you; here it is appsettings.json, overridable by EUDI_-prefixed environment variables and
// by --Key=Value on the command line, e.g.
//     dotnet run -- --VerifierSettings:BackendUrl=http://localhost:8080

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables("EUDI_")
    .AddCommandLine(args)
    .Build();

// Read the same section ourselves for the few values the call sites need.
var settings = configuration
    .GetSection(VerifierSettings.SectionName)
    .Get<VerifierSettings>() ?? new VerifierSettings();

// ── 2. Dependency injection ───────────────────────────────────────────────────
// AddMiEUDIverifier registers VerifierApiService together with a typed HttpClient whose
// BaseAddress is taken from VerifierSettings:BackendUrl. Logging must be registered too:
// VerifierApiService takes an ILogger<VerifierApiService>.

var services = new ServiceCollection();
services.AddLogging(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(LogLevel.Warning)
    .AddFilter("miEUDIverifier", LogLevel.Information));
services.AddMiEUDIverifier(configuration);

await using var provider = services.BuildServiceProvider();
var verifier = provider.GetRequiredService<VerifierApiService>();

// Ctrl+C cancels the wait instead of killing the process mid-request.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── 3. What to ask the wallet for ─────────────────────────────────────────────
// TransactionOptions shapes the DCQL query per transaction. Leave a property null and the
// configured default applies. The rule that governs all of this: a backend fronting a
// registration certificate must request exactly what that certificate covers — asking for
// more makes the wallet abort before the consent screen.

var options = new TransactionOptions
{
    // IntendedUseId selects which registration certificate the backend attaches.
    IntendedUseId = settings.IntendedUseId,

    // High-assurance wallets require an encrypted response. Null keeps the configured default.
    ResponseMode = settings.ResponseMode,

    // Uncomment to restrict the request to one credential format:
    // SdJwtOnly = true,
    // MdocOnly  = true,

    // Uncomment to override the requested attributes (mso_mdoc spelling; the SD-JWT options
    // translate birth_date → birthdate and nationality → nationalities):
    // PidClaims = new List<string> { "family_name", "given_name", "birth_date" },
};

Console.WriteLine($"Backend : {settings.BackendUrl}");
Console.WriteLine($"Claims  : {string.Join(", ", options.PidClaims ?? settings.PidClaims)}");
Console.WriteLine();

// ── 4. Start the transaction ──────────────────────────────────────────────────
// The backend signs an authorization request object and hands back the reference the wallet
// will resolve. Note what comes back: request_uri points at the BACKEND's public URL, not at
// this process.

var tx = await verifier.InitializeTransactionAsync(options, cts.Token);

Console.WriteLine($"Transaction : {tx.TransactionId}");
Console.WriteLine($"client_id   : {tx.ClientId}");
Console.WriteLine($"request_uri : {tx.RequestUri}  ({tx.RequestUriMethod.ToUpperInvariant()})");
Console.WriteLine();

// ── 5. Present the deep link ──────────────────────────────────────────────────
// Cross-device: render the QR code and let the holder scan it. Same device: hand the deep
// link to the OS so the wallet app opens directly.
// The scheme is configurable because wallets differ (openid4vp, eudi-openid4vp, haip-vp).

var deepLink = tx.BuildWalletDeepLink(settings.AuthorizationRequestScheme);

var qrPath = Path.Combine(Environment.CurrentDirectory, "qr.png");
await File.WriteAllBytesAsync(qrPath, QrCodeService.GeneratePng(deepLink), cts.Token);

Console.WriteLine("Scan this with the wallet:");
Console.WriteLine($"  QR code  : {qrPath}");
Console.WriteLine($"  Deep link: {deepLink}");
Console.WriteLine();

// ── 6. Wait for the wallet ────────────────────────────────────────────────────
// There is no callback. The backend holds the response until it is fetched, and this call
// polls for it every VerifierSettings:PollIntervalSeconds until
// VerifierSettings:PollTimeoutSeconds. In a web application you would run this in the
// background and let the browser poll you, exactly as the demo app does.

Console.WriteLine($"Waiting up to {settings.PollTimeoutSeconds}s (Ctrl+C to stop) …");

// The method returns only once a presentation has actually arrived. Every other outcome is
// an exception, so these three catches are the complete set of failure paths.

WalletResponseEnvelope envelope;
try
{
    envelope = await verifier.WaitForWalletResponseAsync(
        tx.TransactionId,
        progress: new Progress<string>(m => Console.WriteLine($"  {m}")),
        ct: cts.Token);
}
catch (TimeoutException ex)
{
    // Either the local deadline passed, or the backend reported the request as expired.
    Console.Error.WriteLine($"Timed out: {ex.Message}");
    return 1;
}
catch (InvalidOperationException ex)
{
    // The wallet answered with an OpenID4VP error instead of a presentation.
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    return 2;
}

// ── 7. Read the attributes ────────────────────────────────────────────────────
// Pass the same options the transaction was started with. Every presentation is checked
// against the credential types that were actually requested; the backend's own verdict is
// not taken as sufficient. A response carrying nothing that was asked for yields a
// CredentialType of null — that is a rejection, not a partial result, and polling again
// will not change it.

var identity = await verifier.ExtractIdentityDataAsync(envelope, options, cts.Token);

if (identity.CredentialType is null)
{
    Console.Error.WriteLine("Rejected: the response carried no credential of the requested type.");
    return 1;
}

Console.WriteLine();
Console.WriteLine(identity.IsComplete ? "Complete presentation:" : "Partial presentation:");
Console.WriteLine($"  Family name       : {identity.FamilyName}");
Console.WriteLine($"  Given name        : {identity.GivenName}");
Console.WriteLine($"  Date of birth     : {identity.BirthDate}");
Console.WriteLine($"  Place of birth    : {identity.PlaceOfBirth}");
Console.WriteLine($"  Nationality       : {identity.Nationality}");
Console.WriteLine($"  Issuing authority : {identity.IssuingAuthority}");
Console.WriteLine($"  Issuing country   : {identity.IssuingCountry}");
Console.WriteLine($"  Format / type     : {identity.CredentialFormat} / {identity.CredentialType}");

return 0;
