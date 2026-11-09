// Reading a German PID from your own web application, using the miEUDIverifier.Core
// NuGet package.
//
// This sample targets the German EUDI Wallet (SPRIND sandbox, "d-you") and nothing else.
// It has no project reference into the miEUDIverifier repository — it takes the library
// from NuGet like any other consumer would.
//
// It will NOT work against the public EU reference backend. A verifier backend signs every
// request object with one access certificate for one trust ecosystem, and the German wallet
// does not trust the EU reference CA. Pointed at eudiw.dev, the wallet aborts late with
// DocumentStatusCheckFailed — which looks like a certificate fault and is really a wrong
// ecosystem. See ../README.md for what you need instead.
//
// Everything configurable lives in appsettings.json under "VerifierSettings", read here by
// AddMiEUDIverifier. What is deliberately NOT there: certificates. The access certificate
// that signs the request object and the registration certificate that decides which
// attributes you may ask for both belong to the verifier backend, and so does the response
// URL the wallet posts to. Your side names an intended use; that is all.

using System.Collections.Concurrent;
using miEUDIverifier;
using miEUDIverifier.Configuration;
using miEUDIverifier.Models;
using miEUDIverifier.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Environment variables with an EUDI_ prefix override appsettings.json, e.g.
//   EUDI_VerifierSettings__BackendUrl=http://localhost:8080
builder.Configuration.AddEnvironmentVariables("EUDI_");

// Registers VerifierApiService with a typed HttpClient pointed at
// VerifierSettings:BackendUrl. Logging is already configured by the web host.
builder.Services.AddMiEUDIverifier(builder.Configuration);

// Verifications in flight. In a real application this is your database or cache — it has to
// outlive the request that started it, because the wallet answers minutes later.
builder.Services.AddSingleton<ConcurrentDictionary<string, Verification>>();

var app = builder.Build();

var settings = app.Services.GetRequiredService<IOptions<VerifierSettings>>().Value;
var store = app.Services.GetRequiredService<ConcurrentDictionary<string, Verification>>();

// ── What to ask the wallet for ────────────────────────────────────────────────
// Every value exists to stay inside what the registration certificate grants: ask for more
// and the wallet aborts before the consent screen, posting an error response a fraction of
// a second after it fetched the request object — no consent screen, no PIN, and from here
// indistinguishable from a trust failure.
//
// These are set in code rather than appsettings.json on purpose: the .NET configuration
// binder APPENDS to a list that already carries defaults, so configuring them as JSON
// arrays would silently ask for the defaults as well.

TransactionOptions GermanPidRequest() => new()
{
    // The only certificate-related value this application sends — and it is a NAME, not a
    // certificate. The backend resolves it to the registration certificate it holds under
    // VERIFIER_INTENDEDUSES_<n>_ID.
    IntendedUseId = settings.IntendedUseId,

    // The German wallet runs the HAIP profile and refuses an unencrypted response. Without
    // this it rejects the request inside the wallet, before the consent screen, so nothing
    // reaches the verifier log at all.
    ResponseMode = "direct_post.jwt",

    // Drop the generic EU SD-JWT option; a German registration certificate does not cover it.
    SdJwtVctValues = new List<string>(),

    // Exactly the vct the certificate covers.
    GermanPidVctValues = new List<string> { "urn:eudi:pid:de:1" },

    // mso_mdoc spelling; the SD-JWT option translates birth_date → birthdate and
    // nationality → nationalities itself.
    PidClaims = new List<string>
    {
        "family_name", "given_name", "birth_date", "place_of_birth",
        "nationality", "issuing_authority", "issuing_country",
    },
};

// ── POST /verify ──────────────────────────────────────────────────────────────
// Starts a verification and answers immediately with the deep link. It must not wait for
// the wallet: that takes as long as a human takes.

app.MapPost("/verify", async (VerifierApiService verifier, IServiceScopeFactory scopes) =>
{
    var options = GermanPidRequest();

    InitTransactionResponse tx;
    try
    {
        tx = await verifier.InitializeTransactionAsync(options);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            $"Cannot reach the verifier backend at {settings.BackendUrl}: {ex.Message}",
            statusCode: StatusCodes.Status502BadGateway);
    }

    var id = Guid.NewGuid().ToString("N");
    var verification = new Verification
    {
        // With the x509_hash prefix the client_id IS the base64url SHA-256 of the backend's
        // access certificate — the only shape in which that certificate reaches this process.
        ClientId = tx.ClientId,
        DeepLink = tx.BuildWalletDeepLink(settings.AuthorizationRequestScheme),
    };
    verification.Qr = QrCodeService.GeneratePng(verification.DeepLink);
    store[id] = verification;

    // There is no callback. The backend holds the response until it is fetched, so wait for
    // it in the background and let your own frontend poll this application.
    _ = Task.Run(() => AwaitWallet(id, tx.TransactionId, options, scopes, app.Lifetime.ApplicationStopping));

    return Results.Created($"/verify/{id}", new
    {
        id,
        status = verification.Status,
        clientId = verification.ClientId,
        deepLink = verification.DeepLink,
        qrCode = $"/verify/{id}/qr",
    });
});

// ── GET /verify/{id} ──────────────────────────────────────────────────────────

app.MapGet("/verify/{id}", (string id) =>
{
    if (!store.TryGetValue(id, out var v))
        return Results.NotFound(new { error = "Unknown verification." });

    if (v.Identity is null)
        return Results.Ok(new { id, status = v.Status, error = v.Error });

    return Results.Ok(new
    {
        id,
        status = v.Status,
        familyName = v.Identity.FamilyName,
        givenName = v.Identity.GivenName,
        birthDate = v.Identity.BirthDate,
        placeOfBirth = v.Identity.PlaceOfBirth,
        nationality = v.Identity.Nationality,
        issuingAuthority = v.Identity.IssuingAuthority,
        issuingCountry = v.Identity.IssuingCountry,
        format = v.Identity.CredentialFormat,
        credentialType = v.Identity.CredentialType,
    });
});

// ── GET /verify/{id}/qr ───────────────────────────────────────────────────────

app.MapGet("/verify/{id}/qr", (string id) =>
    store.TryGetValue(id, out var v) && v.Qr is not null
        ? Results.File(v.Qr, "image/png")
        : Results.NotFound());

// ── GET / ─────────────────────────────────────────────────────────────────────
// A page small enough to read in one go: start a verification, show the QR code, poll until
// the attributes arrive. This is the shape of the real flow — the browser polls YOUR
// application, which polls the verifier backend.

app.MapGet("/", () => Results.Content("""
    <!doctype html><meta charset="utf-8"><title>German PID</title>
    <style>body{font:16px system-ui;margin:3rem auto;max-width:34rem}
    pre{background:#f4f4f5;padding:1rem;border-radius:.5rem;overflow:auto}</style>
    <h1>Read a German PID</h1>
    <button id="go">Start verification</button>
    <div id="out"></div>
    <script>
    document.getElementById('go').onclick = async () => {
      const out = document.getElementById('out');
      const r = await fetch('/verify', { method: 'POST' });
      if (!r.ok) { out.textContent = 'Backend unreachable — see the console output.'; return; }
      const v = await r.json();
      out.innerHTML = '<p>Scan with the German EUDI Wallet:</p>' +
                      '<img src="' + v.qrCode + '" alt="QR code">' +
                      '<p id="s">waiting…</p>';
      const poll = setInterval(async () => {
        const d = await (await fetch('/verify/' + v.id)).json();
        if (d.status === 'waiting') return;
        clearInterval(poll);
        document.getElementById('s').outerHTML =
          '<pre>' + JSON.stringify(d, null, 2) + '</pre>';
      }, 2000);
    };
    </script>
    """, "text/html"));

app.Run();

// ── Waiting for the wallet ────────────────────────────────────────────────────
// WaitForWalletResponseAsync returns only once a presentation has actually arrived; every
// other outcome is an exception, so these catches are the complete set of failure paths.

async Task AwaitWallet(
    string id,
    string transactionId,
    TransactionOptions options,
    IServiceScopeFactory scopes,
    CancellationToken ct)
{
    // The request that started this is long gone, so resolve the service in a fresh scope.
    using var scope = scopes.CreateScope();
    var verifier = scope.ServiceProvider.GetRequiredService<VerifierApiService>();
    var v = store[id];

    try
    {
        var envelope = await verifier.WaitForWalletResponseAsync(transactionId, ct: ct);

        // Pass the same options the transaction was started with. Every presentation is
        // checked against the credential types that were actually requested — the backend's
        // own verdict is not taken as sufficient.
        var identity = await verifier.ExtractIdentityDataAsync(envelope, options, ct);

        if (identity.CredentialType is null)
        {
            // Nothing of the requested type came back. That is a rejection, not a partial
            // result: polling again will not change it, and it is also what a request that
            // reached past the registration certificate looks like from here.
            v.Status = "rejected";
            v.Error = "The response carried no credential of the requested type.";
            return;
        }

        v.Identity = identity;
        v.Status = identity.IsComplete ? "complete" : "partial";
    }
    catch (TimeoutException ex)
    {
        v.Status = "timeout";
        v.Error = ex.Message;
    }
    catch (OperationCanceledException)
    {
        v.Status = "cancelled";
    }
    catch (Exception ex)
    {
        // Includes InvalidOperationException, which is how a wallet-side OpenID4VP error
        // surfaces.
        v.Status = "error";
        v.Error = ex.Message;
    }
}

/// <summary>One verification in flight.</summary>
sealed class Verification
{
    public string Status { get; set; } = "waiting";
    public string? Error { get; set; }
    public string ClientId { get; init; } = string.Empty;
    public string DeepLink { get; init; } = string.Empty;
    public byte[]? Qr { get; set; }
    public IdentityData? Identity { get; set; }
}
