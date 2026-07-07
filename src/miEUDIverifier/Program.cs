using System.Diagnostics;
using miEUDIverifier.Configuration;
using miEUDIverifier.Services;
using miEUDIverifier.WebServer;

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

builder.Services.AddHttpClient<VerifierApiService>(client =>
{
    client.BaseAddress = new Uri(settings.BackendUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();
var verifier = app.Services.GetRequiredService<VerifierApiService>();

// ── Local helper functions ────────────────────────────────────────────────────

// Starts a new transaction and updates the AppState
async Task StartNewTransaction(AppState state, CancellationToken ct)
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

// Starts the background polling task for a transaction
void StartPolling(AppState state, CancellationToken appStopping)
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

    var state = new AppState();
    await StartNewTransaction(state, app.Lifetime.ApplicationStopping);
    StartPolling(state, app.Lifetime.ApplicationStopping);

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
        familyName = state.Identity?.FamilyName,
        givenName  = state.Identity?.GivenName,
        birthDate  = state.Identity?.BirthDate,
        format     = state.Identity?.CredentialFormat,
        error      = state.ErrorMessage,
    });
});

// New request – fresh transaction for the current browser session
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
            await StartNewTransaction(state, app.Lifetime.ApplicationStopping);
            StartPolling(state, app.Lifetime.ApplicationStopping);
        }
        return Results.Json(new { qrBase64 = state.QrBase64 });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

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

// POST /api/verification – starts a new verification, returns session id + deep link
app.MapPost("/api/verification", async () =>
{
    try
    {
        var state = new AppState();
        await StartNewTransaction(state, app.Lifetime.ApplicationStopping);
        StartPolling(state, app.Lifetime.ApplicationStopping);

        var id = sessions.Add(state);
        return Results.Created($"/api/verification/{id}", new
        {
            id,
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
        status = state.Status,          // waiting | complete | partial | error
        error  = state.ErrorMessage,
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
            status     = state.Status,
            familyName = state.Identity.FamilyName,
            givenName  = state.Identity.GivenName,
            birthDate  = state.Identity.BirthDate,
            format     = state.Identity.CredentialFormat,
        });
    }

    // No data yet (waiting) or error → 409 so clients can keep polling
    return Results.Json(new { id, status = state.Status, error = state.ErrorMessage },
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
