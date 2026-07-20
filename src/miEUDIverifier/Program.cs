using System.Diagnostics;
using miEUDIverifier.Configuration;
using miEUDIverifier.Services;
using miEUDIverifier.WebServer;
using Microsoft.Extensions.Options;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ── 1. Build the WebApplication ───────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("EUDI_");
builder.Logging
    .ClearProviders()
    .AddConsole()
    .SetMinimumLevel(LogLevel.Warning)
    .AddFilter("miEUDIverifier", LogLevel.Information);

var settings = builder.Configuration
    .GetSection(VerifierSettings.SectionName)
    .Get<VerifierSettings>() ?? new VerifierSettings();

builder.Services.Configure<VerifierSettings>(
    builder.Configuration.GetSection(VerifierSettings.SectionName));

// Named verifier backends (multi-ecosystem): e.g. "eu" = EUDI reference (eudiw.dev),
// "de" = German EUDI Wallet (own backend with a SPRIND-issued RP access certificate, added
// later). Falls back to a single "eu" backend from BackendUrl when Backends is not configured.
var backendUrls = (settings.Backends is { Count: > 0 }
        ? settings.Backends
        : new Dictionary<string, string> { ["eu"] = settings.BackendUrl })
    .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
    .ToDictionary(kv => kv.Key, kv => kv.Value.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);

if (backendUrls.Count == 0)
    backendUrls["eu"] = "https://verifier-backend.eudiw.dev";

var defaultBackend = backendUrls.ContainsKey(settings.DefaultBackend)
    ? settings.DefaultBackend
    : backendUrls.Keys.First();

foreach (var (key, url) in backendUrls)
{
    var baseUri = new Uri(url + "/");
    builder.Services.AddHttpClient($"verifier-{key}", client =>
    {
        client.BaseAddress = baseUri;
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
}

var app = builder.Build();

// One VerifierApiService per configured backend, keyed by backend name.
var httpFactory     = app.Services.GetRequiredService<IHttpClientFactory>();
var verifierOptions = app.Services.GetRequiredService<IOptions<VerifierSettings>>();
var verifierLogger  = app.Services.GetRequiredService<ILogger<VerifierApiService>>();
var services = backendUrls.Keys.ToDictionary(
    key => key,
    key => new VerifierApiService(httpFactory.CreateClient($"verifier-{key}"), verifierOptions, verifierLogger),
    StringComparer.OrdinalIgnoreCase);

// Resolves the service for a backend key, falling back to the default backend.
VerifierApiService ServiceFor(string? backend) =>
    backend is not null && services.TryGetValue(backend, out var s) ? s : services[defaultBackend];

// ── Local helper functions ────────────────────────────────────────────────────

// Starts a new transaction (against the given backend) and updates the AppState
async Task StartNewTransaction(AppState state, VerifierApiService verifier, CancellationToken ct)
{
    var transaction = await verifier.InitializeTransactionAsync(ct);
    var deepLink    = transaction.BuildWalletDeepLink(settings.AuthorizationRequestScheme);
    var qrBytes     = QrCodeService.GeneratePng(deepLink);

    state.Status          = "waiting";
    state.TransactionId   = transaction.TransactionId;
    state.DeepLink        = deepLink;
    state.QrBase64        = Convert.ToBase64String(qrBytes);
    state.Identity        = null;
    state.ErrorMessage    = null;
    state.LastRawResponse = null;

    Console.WriteLine($"  Transaction-ID : {transaction.TransactionId}");
}

// Starts the background polling task for a transaction (against the given backend)
void StartPolling(AppState state, VerifierApiService verifier, CancellationToken appStopping)
{
    // Cancel any previous polling task
    state.PollingCts?.Cancel();
    var cts = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
    state.PollingCts = cts;

    _ = Task.Run(async () =>
    {
        try
        {
            var envelope = await verifier.WaitForWalletResponseAsync(
                state.TransactionId,
                onRawResponse: raw => state.LastRawResponse = raw,
                ct: cts.Token);

            var identity = await verifier.ExtractIdentityDataAsync(envelope, cts.Token);

            state.Identity = identity;
            state.Status   = identity.IsComplete ? "complete" : "partial";

            Console.WriteLine($"\n  Identitaet empfangen: {identity.GivenName} {identity.FamilyName}, *{identity.BirthDate}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            state.Status       = "error";
            state.ErrorMessage = ex.Message;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  Fehler beim Warten: {ex.Message}");
            Console.ResetColor();
        }
    }, cts.Token);
}

// ── 2. Session store & browser sessions ───────────────────────────────────────
Console.WriteLine("\n  EUDI Wallet Verifier – Starte ...");
Console.WriteLine($"  Backends   : {string.Join(", ", backendUrls.Select(b => $"{b.Key}={b.Value}"))} (default: {defaultBackend})");

// Central session store: both the REST API and the demo page (via cookie) keep their
// sessions here → every browser and API client gets its own session.
var sessions = new SessionStore(TimeSpan.FromMinutes(settings.SessionTtlMinutes));
using var sessionPurgeTimer = new Timer(
    _ => { try { sessions.PurgeExpired(); } catch { /* best effort */ } },
    null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

const string BrowserSessionCookie = "mieudi_sid";

// Returns the session belonging to the browser cookie – or null if none exists (anymore).
AppState? GetBrowserSession(HttpContext ctx) =>
    ctx.Request.Cookies.TryGetValue(BrowserSessionCookie, out var sid)
    && sessions.TryGet(sid, out var s) ? s : null;

// Gets the browser session or creates a new one (transaction + polling + cookie).
async Task<AppState> GetOrCreateBrowserSessionAsync(HttpContext ctx)
{
    var existing = GetBrowserSession(ctx);
    if (existing is not null) return existing;

    // Optional ?backend=eu|de selects the trust ecosystem for this browser session.
    var requested = ctx.Request.Query["backend"].FirstOrDefault();
    var backend   = requested is not null && services.ContainsKey(requested) ? requested : defaultBackend;

    var state = new AppState { Backend = backend };
    await StartNewTransaction(state, services[backend], app.Lifetime.ApplicationStopping);
    StartPolling(state, services[backend], app.Lifetime.ApplicationStopping);

    var id = sessions.Add(state);
    ctx.Response.Cookies.Append(BrowserSessionCookie, id, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path     = "/",
        MaxAge   = TimeSpan.FromMinutes(settings.SessionTtlMinutes),
    });
    return state;
}

// ── 3. Web routes (demo page, per browser session via cookie) ──────────────────

app.MapGet("/", async (HttpContext ctx) =>
{
    var state = await GetOrCreateBrowserSessionAsync(ctx);
    return Results.Content(
        HtmlPage.Render(state, ctx.Request.Headers.AcceptLanguage.ToString()),
        "text/html; charset=utf-8");
});

app.MapGet("/api/status", (HttpContext ctx) =>
{
    var state = GetBrowserSession(ctx);
    if (state is null) return Results.Json(new { status = "expired" });

    return Results.Json(new
    {
        status     = state.Status,
        backend    = state.Backend,
        familyName = state.Identity?.FamilyName,
        givenName  = state.Identity?.GivenName,
        birthDate  = state.Identity?.BirthDate,
        format     = state.Identity?.CredentialFormat,
        error      = state.ErrorMessage,
    });
});

// New request – fresh transaction for the current browser session.
// Optional ?backend=eu|de re-targets the session to a different trust ecosystem.
app.MapPost("/api/reset", async (HttpContext ctx) =>
{
    try
    {
        var state = GetBrowserSession(ctx);
        if (state is null)
        {
            // Session expired/missing → create a new one (also sets the cookie)
            state = await GetOrCreateBrowserSessionAsync(ctx);
        }
        else
        {
            var requested = ctx.Request.Query["backend"].FirstOrDefault();
            if (requested is not null && services.ContainsKey(requested))
                state.Backend = requested;

            var svc = ServiceFor(state.Backend);
            await StartNewTransaction(state, svc, app.Lifetime.ApplicationStopping);
            StartPolling(state, svc, app.Lifetime.ApplicationStopping);
        }
        return Results.Json(new { qrBase64 = state.QrBase64, backend = state.Backend });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

// Lists the configured verifier backends (trust ecosystems) and the default.
app.MapGet("/api/backends", () => Results.Json(new
{
    defaultBackend,
    backends = services.Keys.ToArray(),
}));

app.MapGet("/api/debug", (HttpContext ctx) =>
{
    var state = GetBrowserSession(ctx);
    if (state is null) return Results.Json(new { status = "expired" });

    return Results.Json(new
    {
        status          = state.Status,
        transactionId   = state.TransactionId,
        lastRawResponse = state.LastRawResponse,
        identity        = state.Identity,
    });
});

// ── 3b. Session-based REST API ─────────────────────────────────────────────────
// Lets external apps run any number of verifications in parallel, independent of the demo page.

// POST /api/verification[?backend=eu|de] – starts a new verification against the chosen
// trust ecosystem, returns session id + deep link. Unknown/unconfigured backend → 400.
app.MapPost("/api/verification", async (HttpContext ctx) =>
{
    var requested = ctx.Request.Query["backend"].FirstOrDefault() ?? defaultBackend;
    if (!services.TryGetValue(requested, out var svc))
    {
        return Results.Json(
            new { error = $"Unknown or unconfigured backend '{requested}'.", availableBackends = services.Keys.ToArray() },
            statusCode: 400);
    }

    try
    {
        var state = new AppState { Backend = requested };
        await StartNewTransaction(state, svc, app.Lifetime.ApplicationStopping);
        StartPolling(state, svc, app.Lifetime.ApplicationStopping);

        var id = sessions.Add(state);
        return Results.Created($"/api/verification/{id}", new
        {
            id,
            backend  = requested,
            status   = state.Status,   // waiting
            deepLink = state.DeepLink,
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

// GET /api/verification/{id}/qrcode – QR code as a PNG image
app.MapGet("/api/verification/{id}/qrcode", (string id) =>
{
    if (!sessions.TryGet(id, out var state) || string.IsNullOrEmpty(state.QrBase64))
        return Results.NotFound(new { error = "Unknown or empty verification session." });

    var png = Convert.FromBase64String(state.QrBase64);
    return Results.File(png, "image/png");
});

// GET /api/verification/{id}/status – current status of the verification
app.MapGet("/api/verification/{id}/status", (string id) =>
{
    if (!sessions.TryGet(id, out var state))
        return Results.NotFound(new { error = "Unknown verification session." });

    return Results.Json(new
    {
        id,
        backend = state.Backend,
        status  = state.Status,         // waiting | complete | partial | error
        error   = state.ErrorMessage,
    });
});

// GET /api/verification/{id}/data – extracted PID data (once available)
app.MapGet("/api/verification/{id}/data", (string id) =>
{
    if (!sessions.TryGet(id, out var state))
        return Results.NotFound(new { error = "Unknown verification session." });

    if (state.Identity is not null && (state.Status == "complete" || state.Status == "partial"))
    {
        return Results.Json(new
        {
            id,
            backend    = state.Backend,
            status     = state.Status,
            familyName = state.Identity.FamilyName,
            givenName  = state.Identity.GivenName,
            birthDate  = state.Identity.BirthDate,
            format     = state.Identity.CredentialFormat,
        });
    }

    // No data yet (waiting) or error → 409 so clients can keep polling
    return Results.Json(new { id, backend = state.Backend, status = state.Status, error = state.ErrorMessage },
        statusCode: 409);
});

// DELETE /api/verification/{id} – discard the session and cancel its polling
app.MapDelete("/api/verification/{id}", (string id) =>
{
    if (!sessions.Remove(id, out var state))
        return Results.NotFound(new { error = "Unknown verification session." });

    state?.PollingCts?.Cancel();
    return Results.NoContent();
});

// ── 4. Start ──────────────────────────────────────────────────────────────────
// Kestrel reads endpoints + certificate directly from appsettings.json
// (Kestrel:Endpoints overrides app.Urls – no app.Urls.Add() needed)

// Determine the LAN IP
var lanIp = System.Net.NetworkInformation.NetworkInterface
    .GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
             && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    .Select(a => a.Address.ToString())
    .FirstOrDefault();

var httpUrl  = builder.Configuration["Kestrel:Endpoints:Http:Url"]  ?? "http://0.0.0.0:5050";
var httpsUrl = builder.Configuration["Kestrel:Endpoints:Https:Url"];

var localHttp  = httpUrl.Replace("0.0.0.0",  "localhost");
var localHttps = httpsUrl?.Replace("0.0.0.0", "localhost");

Console.WriteLine($"  HTTP       : {localHttp}");
if (localHttps != null) Console.WriteLine($"  HTTPS      : {localHttps}");
if (lanIp != null)
{
    Console.WriteLine($"  LAN HTTP   : {httpUrl.Replace("0.0.0.0",  lanIp)}");
    if (httpsUrl != null)
        Console.WriteLine($"  LAN HTTPS  : {httpsUrl.Replace("0.0.0.0", lanIp)}");
}
Console.WriteLine($"  Beenden    : Strg+C\n");

// Browser URL: local HTTPS address when HTTPS is active, otherwise local HTTP fallback
var browserUrl = localHttps ?? localHttp;
try { Process.Start(new ProcessStartInfo(browserUrl) { UseShellExecute = true }); }
catch { Console.WriteLine($"  Bitte {browserUrl} manuell im Browser oeffnen."); }

await app.RunAsync();
