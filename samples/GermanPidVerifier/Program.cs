// Reading a German PID with the miEUDIverifier.Core NuGet package.
//
// This sample targets the German EUDI Wallet (SPRIND sandbox, "d-you") and nothing else. It
// does not reference anything from the miEUDIverifier repository — it pulls
// `miEUDIverifier.Core` from NuGet like any other consumer would.
//
// It will NOT work against the public EU reference backend. A verifier backend signs every
// request object with one access certificate for one trust ecosystem, and the German wallet
// does not trust the EU reference CA. Pointed at eudiw.dev, the wallet aborts late with
// DocumentStatusCheckFailed — which looks like a certificate fault and is really a wrong
// ecosystem. You need a backend of your own holding a SPRIND-issued access certificate; see
// ../README.md for what that involves and how to reach it.
//
// Two things this process never controls:
//   * The CERTIFICATES. The access certificate that signs the request object, and the
//     registration certificate that decides which attributes you may ask for, both live in
//     the backend.
//   * The RESPONSE URL the wallet posts to. The backend derives it from VERIFIER_PUBLICURL.

using miEUDIverifier;
using miEUDIverifier.Configuration;
using miEUDIverifier.Models;
using miEUDIverifier.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ── 1. Configuration ──────────────────────────────────────────────────────────
// The library reads one section, "VerifierSettings". Where it comes from is up to you; here
// it is appsettings.json, overridable by EUDI_-prefixed environment variables and by
// --Key=Value on the command line, e.g.
//     dotnet run -- --VerifierSettings:BackendUrl=http://localhost:8080

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables("EUDI_")
    .AddCommandLine(args)
    .Build();

var settings = configuration
    .GetSection(VerifierSettings.SectionName)
    .Get<VerifierSettings>() ?? new VerifierSettings();

// ── 2. Dependency injection ───────────────────────────────────────────────────
// AddMiEUDIverifier registers VerifierApiService with a typed HttpClient whose BaseAddress
// is VerifierSettings:BackendUrl. Logging has to be registered too — the service takes an
// ILogger<VerifierApiService> and resolution fails without a provider.

var services = new ServiceCollection();
services.AddLogging(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(LogLevel.Warning)
    .AddFilter("miEUDIverifier", LogLevel.Information));
services.AddMiEUDIverifier(configuration);

await using var provider = services.BuildServiceProvider();
var verifier = provider.GetRequiredService<VerifierApiService>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── 3. The German request ─────────────────────────────────────────────────────
// Every value here exists to stay inside what the registration certificate grants. The rule
// that governs all of it: ask for more than the certificate covers and the wallet aborts
// before the consent screen, posting an error response a fraction of a second after it
// fetched the request object — no consent screen, no PIN, and on this side it is
// indistinguishable from a trust failure.
//
// These are set through TransactionOptions rather than appsettings.json on purpose: the
// .NET configuration binder APPENDS to a list that already carries defaults, so configuring
// them as JSON arrays would silently ask for the defaults as well.

var options = new TransactionOptions
{
    // The only certificate-related value this process ever sends — and note that it is a
    // NAME, not a certificate. It goes out as `intended_use_id` and the backend resolves it
    // to the registration certificate it holds under VERIFIER_INTENDEDUSES_<n>_ID. No
    // keystore, key, password or certificate JWT is configured on this side at all.
    IntendedUseId = settings.IntendedUseId,

    // The German wallet runs the HAIP profile and refuses an unencrypted response. Without
    // this it rejects the request inside the wallet, before the consent screen, so nothing
    // appears in the verifier log at all.
    ResponseMode = "direct_post.jwt",

    // Drop the generic EU SD-JWT option — a German registration certificate does not cover
    // it, and offering it would put the request outside the certificate's scope.
    SdJwtVctValues = new List<string>(),

    // Exactly the vct the certificate covers.
    GermanPidVctValues = new List<string> { "urn:eudi:pid:de:1" },

    // Attributes in mso_mdoc spelling; the SD-JWT option translates birth_date → birthdate
    // and nationality → nationalities by itself. This list has to match what the
    // registration certificate grants, attribute for attribute.
    PidClaims = new List<string>
    {
        "family_name",
        "given_name",
        "birth_date",
        "place_of_birth",
        "nationality",
        "issuing_authority",
        "issuing_country",
    },
};

Console.WriteLine($"Backend        : {settings.BackendUrl}");
Console.WriteLine($"Intended use   : {options.IntendedUseId}");
Console.WriteLine($"Claims         : {string.Join(", ", options.PidClaims!)}");
Console.WriteLine();

// ── 4. Start the transaction ──────────────────────────────────────────────────
// Note where request_uri points: at the BACKEND's public URL, never at this process.

InitTransactionResponse tx;
try
{
    tx = await verifier.InitializeTransactionAsync(options, cts.Token);
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Cannot reach the verifier backend at {settings.BackendUrl}: {ex.Message}");
    Console.Error.WriteLine("This sample needs a backend holding a SPRIND access certificate — see ../README.md.");
    return 1;
}

Console.WriteLine($"Transaction : {tx.TransactionId}");

// The access certificate, as seen from here. With the x509_hash prefix the client_id IS the
// base64url SHA-256 of the backend's certificate — this is the only form in which that
// certificate reaches this process. Reproduce it with:
//   openssl x509 -in access-certificate.pem -outform DER \
//     | openssl dgst -sha256 -binary | basenc --base64url | tr -d '='
// If the two ever disagree, VERIFIER_ORIGINALCLIENTID was not updated after the certificate
// was reissued, and the wallet will refuse the request as an untrusted chain.
Console.WriteLine($"client_id   : {tx.ClientId}");
Console.WriteLine($"request_uri : {tx.RequestUri}  ({tx.RequestUriMethod.ToUpperInvariant()})");
Console.WriteLine();

// ── 5. Present the deep link ──────────────────────────────────────────────────
// Cross-device: render the QR code and let the holder scan it. Same device: hand the deep
// link to the OS so the wallet opens directly.

var deepLink = tx.BuildWalletDeepLink(settings.AuthorizationRequestScheme);

var qrPath = Path.Combine(Environment.CurrentDirectory, "qr.png");
await File.WriteAllBytesAsync(qrPath, QrCodeService.GeneratePng(deepLink), cts.Token);

Console.WriteLine("Scan this with the German EUDI Wallet:");
Console.WriteLine($"  QR code  : {qrPath}");
Console.WriteLine($"  Deep link: {deepLink}");
Console.WriteLine();

// ── 6. Wait for the wallet ────────────────────────────────────────────────────
// There is no callback. The backend holds the response until it is fetched, and this call
// polls for it. In a web application you would run it in the background and let your own
// frontend poll you.
//
// The method returns only once a presentation has actually arrived; every other outcome is
// an exception, so these three catches are the complete set of failure paths.

Console.WriteLine($"Waiting up to {settings.PollTimeoutSeconds}s (Ctrl+C to stop) …");

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
// against the credential types that were actually requested — the backend's own verdict is
// not taken as sufficient. A response carrying nothing that was asked for yields a
// CredentialType of null: that is a rejection, not a partial result, and polling again will
// not change it. It is also the signature of a request that went outside the registration
// certificate's scope.

var identity = await verifier.ExtractIdentityDataAsync(envelope, options, cts.Token);

if (identity.CredentialType is null)
{
    Console.Error.WriteLine("Rejected: the response carried no credential of the requested type.");
    Console.Error.WriteLine("If the wallet showed no consent screen, the request asked for more than");
    Console.Error.WriteLine("the registration certificate grants — see ../README.md.");
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
