using Microsoft.AspNetCore.HttpOverrides;
using miEUDIverifier.Models;
using miEUDIverifier.Services;
using miEUDIverifier.Services.Tessio;

// A German PID verifier in about a hundred lines, with no verifier backend behind it: one NuGet
// package hosts the endpoints the wallet calls and verifies the presentation in process.
//
// Everything specific to the German ecosystem -- the registration certificate in verifier_info,
// the certificate path in x5c, the encrypted response -- is inside the package. What is left here
// is the five things only you can supply, and a page to show the result on.

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("GermanPid");

// ── The five inputs ──────────────────────────────────────────────────────────────────────
builder.Services.AddInProcessVerifier(new InProcessVerifierOptions
{
    // The address the WALLET reaches, not localhost: it fetches the request object and posts the
    // response over the internet. Behind a reverse proxy or tunnel, the outside name.
    PublicBaseUrl = Required("PublicBaseUrl"),

    // The PKCS#12 with the private key, your access certificate AND its issuing CA. The chain
    // matters: HAIP 1.0 section 5 wants the path up to, but excluding, the trust anchor in x5c.
    // A password only if the file has one -- a container without one is fine.
    AccessCertificatePath = Required("AccessCertificatePath"),
    AccessCertificatePassword = settings["AccessCertificatePassword"],

    // Verbatim, as your registrar issued it: the wallet verifies a signature over those bytes.
    RegistrationCertificate = settings["RegistrationCertificate"],
    RegistrationCertificatePath = settings["RegistrationCertificatePath"],

    // The PID issuer's CA certificates. Without them a presentation verifies cryptographically
    // and is still refused as untrusted. The folder next to this file is ready to use.
    IssuerTrustAnchorDirectory = Required("IssuerTrustAnchorDirectory"),

    // Only ever true for a local smoke test. A real wallet refuses a self-signed signer.
    AllowSelfSignedAccessCertificate = settings.GetValue("AllowSelfSignedAccessCertificate", false),
});

// Behind a proxy or a tunnel the application sees a plain HTTP connection, and the response URI is
// built from what it sees. Without this the wallet is told to post to an http:// address, which it
// refuses -- and should. Safe without naming the proxy only because nothing but the proxy can
// reach this process; an instance exposed directly must name its proxies, or headers can be forged.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders =
        ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedFor;
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();   // before anything that reads the scheme or host
app.MapInProcessVerifier();  // the endpoints the wallet itself calls

// The identifier the wallet pins this request to: SHA-256 over the access certificate, which is
// also the value a registrar wants when you enrol. Logged once at startup because it is otherwise
// only visible by decoding a request object.
//
// Not logged here: how many certificates end up in x5c. The package derives that itself and keeps
// the loader internal, and the check that matters happens before anything starts -- count the
// certificates in the PKCS#12 with openssl, see SCHRITT-FUER-SCHRITT.md step 5. A chain with only
// the leaf in it is the one fault no local test reveals.
app.Logger.LogInformation(
    "Access certificate in use: client_id {ClientId}",
    app.Services.GetRequiredService<RegistrationCertificateRequestBuilder>().ClientId);

// ── One verification at a time ───────────────────────────────────────────────────────────
// A sample, not a server: a single verification in a field, so the interesting part stays
// readable. A real application keys this by browser session -- see src/miEUDIverifier for one.
Verification current = new();

app.MapGet("/", async (TessioVerifierEngine verifier) =>
{
    if (current.QrBase64 is null)
    {
        await StartAsync(verifier, app.Logger, app.Lifetime.ApplicationStopping);
    }

    return Results.Content(Page(current), "text/html; charset=utf-8");
});

app.MapPost("/new", async (TessioVerifierEngine verifier) =>
{
    await StartAsync(verifier, app.Logger, app.Lifetime.ApplicationStopping);
    return Results.Ok();
});

// What the page polls. Claim values are returned because this page is the one that shows them;
// note that anyone who can reach this endpoint sees them, so do not expose it as it stands.
app.MapGet("/status", () => Results.Json(new
{
    status = current.Status,
    current.Identity?.FamilyName,
    current.Identity?.GivenName,
    current.Identity?.BirthDate,
    current.Identity?.PlaceOfBirth,
    current.Identity?.Nationality,
    current.Identity?.IssuingAuthority,
    current.Identity?.IssuingCountry,
    current.Identity?.CredentialType,
    current.Error,
}));

app.Run();

// ── Starting a verification, and waiting for the answer in the background ────────────────
async Task StartAsync(TessioVerifierEngine verifier, ILogger logger, CancellationToken stopping)
{
    current = new Verification();

    var transaction = await verifier.InitializeTransactionAsync(ct: stopping);
    var deepLink = transaction.BuildWalletDeepLink();

    current.Status = "waiting";
    current.QrBase64 = Convert.ToBase64String(QrCodeService.GeneratePng(deepLink));
    var mine = current;

    // WaitForWalletResponseAsync blocks until the wallet answers or the session expires, so it
    // runs off the request thread. The captured 'mine' is what keeps a late answer from
    // overwriting a verification started in the meantime.
    _ = Task.Run(async () =>
    {
        try
        {
            var envelope = await verifier.WaitForWalletResponseAsync(transaction.TransactionId, ct: stopping);
            var identity = await verifier.ExtractIdentityDataAsync(envelope, options: null, ct: stopping);

            if (!ReferenceEquals(mine, current))
            {
                return;
            }

            if (envelope.Error is { Length: > 0 })
            {
                // The engine reports codes AND messages here, which the hosted status endpoint
                // withholds by design. That is why this sample needs no diagnostics endpoint.
                mine.Status = "rejected";
                mine.Error = $"{envelope.Error}: {envelope.ErrorDescription}";
                logger.LogWarning("Presentation refused: {Error} {Description}",
                    envelope.Error, envelope.ErrorDescription);
                return;
            }

            // Null means the response carried no credential of the requested type, which is a
            // different outcome from incomplete data. Telling them apart is the point of the field.
            if (identity.CredentialType is null)
            {
                mine.Status = "rejected";
                mine.Error = "The wallet response carried no credential of the requested type.";
                return;
            }

            mine.Identity = identity;
            mine.Status = identity.IsComplete ? "complete" : "partial";
            logger.LogInformation("Identity received: {Given} {Family}, born {Birth}",
                identity.GivenName, identity.FamilyName, identity.BirthDate);
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down.
        }
        catch (Exception ex)
        {
            mine.Status = "error";
            mine.Error = ex.Message;
            logger.LogError(ex, "Waiting for the wallet failed");
        }
    }, stopping);
}

string Required(string key) =>
    settings[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"Configuration 'GermanPid:{key}' is missing. See SCHRITT-FUER-SCHRITT.md, step 8.");

static string Page(Verification v) => $$"""
    <!doctype html>
    <html lang="en"><head><meta charset="utf-8">
    <meta name="viewport" content="width=device-width,initial-scale=1">
    <title>German PID verifier</title>
    <style>
      :root { color-scheme: light dark; --fg:#111; --bg:#fff; --muted:#666; --line:#ddd; }
      @media (prefers-color-scheme: dark) {
        :root { --fg:#eee; --bg:#161616; --muted:#aaa; --line:#333; }
      }
      body { font: 15px/1.6 system-ui, sans-serif; color: var(--fg); background: var(--bg);
             margin: 0; padding: 24px 16px; display: flex; justify-content: center; }
      main { max-width: 420px; width: 100%; }
      h1 { font-size: 19px; margin: 0 0 4px; }
      p.sub { color: var(--muted); margin: 0 0 20px; }
      img { width: 260px; height: 260px; display: block; margin: 0 auto 16px;
            image-rendering: pixelated; background: #fff; padding: 8px; border-radius: 8px; }
      #status { text-align: center; font-weight: 600; margin-bottom: 16px; }
      table { width: 100%; border-collapse: collapse; }
      td { padding: 7px 0; border-bottom: 1px solid var(--line); }
      td:first-child { color: var(--muted); width: 45%; }
      button { width: 100%; padding: 11px; margin-top: 18px; font: inherit; cursor: pointer;
               border: 1px solid var(--line); border-radius: 8px;
               background: transparent; color: var(--fg); }
      .err { color: #b00020; text-align: center; }
      @media (prefers-color-scheme: dark) { .err { color: #ff8a8a; } }
    </style></head>
    <body><main>
      <h1>German PID verifier</h1>
      <p class="sub">No verifier backend &mdash; verified in this process.</p>
      <img id="qr" src="data:image/png;base64,{{v.QrBase64}}" alt="QR code for the wallet">
      <div id="status">Scan the QR code with your wallet&hellip;</div>
      <div id="out"></div>
      <button onclick="fetch('/new',{method:'POST'}).then(()=>location.reload())">New request</button>
    <script>
      const LABELS = {
        givenName:'Given name', familyName:'Family name', birthDate:'Date of birth',
        placeOfBirth:'Place of birth', nationality:'Nationality',
        issuingAuthority:'Issuing authority', issuingCountry:'Issuing country',
        credentialType:'Credential type',
      };
      async function poll() {
        let s;
        try { s = await (await fetch('/status')).json(); } catch { return; }
        const status = document.getElementById('status');
        const out = document.getElementById('out');
        if (s.status === 'waiting') { setTimeout(poll, 1500); return; }

        document.getElementById('qr').style.display = 'none';
        if (s.status === 'complete' || s.status === 'partial') {
          status.textContent = s.status === 'complete' ? 'Verified ✓' : 'Verified, incomplete';
          const rows = Object.entries(LABELS)
            .filter(([k]) => s[k])
            .map(([k, label]) => `<tr><td>${label}</td><td>${s[k]}</td></tr>`).join('');
          out.innerHTML = `<table>${rows}</table>`;
        } else {
          status.textContent = 'Not verified';
          // textContent, never markup: the message carries verifier output.
          const p = document.createElement('p');
          p.className = 'err';
          p.textContent = s.error ?? 'unknown';
          out.replaceChildren(p);
        }
      }
      poll();
    </script>
    </main></body></html>
    """;

/// <summary>One verification, as this sample tracks it.</summary>
internal sealed class Verification
{
    public string Status { get; set; } = "waiting";
    public string? QrBase64 { get; set; }
    public IdentityData? Identity { get; set; }
    public string? Error { get; set; }
}
