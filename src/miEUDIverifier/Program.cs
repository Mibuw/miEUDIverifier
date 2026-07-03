using System.Diagnostics;
using miEUDIverifier.Configuration;
using miEUDIverifier.Services;
using miEUDIverifier.WebServer;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ── 1. WebApplication aufbauen ────────────────────────────────────────────────
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

// ── Lokale Hilfsfunktionen ────────────────────────────────────────────────────

// Startet eine neue Transaction und aktualisiert den AppState
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

// Startet den Hintergrund-Polling-Task fuer eine Transaction
void StartPolling(AppState state, CancellationToken appStopping)
{
    // Alten Polling-Task abbrechen
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

// ── 2. Erste Transaction starten ──────────────────────────────────────────────
Console.WriteLine("\n  EUDI Wallet Verifier – Starte ...");

// Session-Store für die REST-API (parallele Verifikationen unabhängig von der Demo-Seite)
var sessions = new SessionStore(TimeSpan.FromMinutes(settings.SessionTtlMinutes));
using var sessionPurgeTimer = new Timer(
    _ => { try { sessions.PurgeExpired(); } catch { /* best effort */ } },
    null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

var appState = new AppState();
try
{
    await StartNewTransaction(appState, CancellationToken.None);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n  Fehler: {ex.Message}");
    Console.ResetColor();
    return;
}

StartPolling(appState, app.Lifetime.ApplicationStopping);

// ── 3. Web-Routen ─────────────────────────────────────────────────────────────

app.MapGet("/", (HttpRequest request) =>
    Results.Content(
        HtmlPage.Render(appState, request.Headers.AcceptLanguage.ToString()),
        "text/html; charset=utf-8"));

app.MapGet("/api/status", () => Results.Json(new
{
    status     = appState.Status,
    familyName = appState.Identity?.FamilyName,
    givenName  = appState.Identity?.GivenName,
    birthDate  = appState.Identity?.BirthDate,
    format     = appState.Identity?.CredentialFormat,
    error      = appState.ErrorMessage,
}));

// Neuer Request – initialisiert eine frische Transaction und liefert den neuen QR-Code
app.MapPost("/api/reset", async () =>
{
    try
    {
        await StartNewTransaction(appState, app.Lifetime.ApplicationStopping);
        StartPolling(appState, app.Lifetime.ApplicationStopping);
        return Results.Json(new { qrBase64 = appState.QrBase64 });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/debug", () => Results.Json(new
{
    status          = appState.Status,
    transactionId   = appState.TransactionId,
    lastRawResponse = appState.LastRawResponse,
    identity        = appState.Identity,
}));

// ── 3b. Session-basierte REST-API ──────────────────────────────────────────────
// Ermöglicht beliebig viele parallele Verifikationen unabhängig von der Demo-Seite.

// POST /api/verification – startet eine neue Verifikation, liefert Session-ID + Deep-Link
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

// GET /api/verification/{id}/qrcode – QR-Code als PNG-Bild
app.MapGet("/api/verification/{id}/qrcode", (string id) =>
{
    if (!sessions.TryGet(id, out var state) || string.IsNullOrEmpty(state.QrBase64))
        return Results.NotFound(new { error = "Unknown or empty verification session." });

    var png = Convert.FromBase64String(state.QrBase64);
    return Results.File(png, "image/png");
});

// GET /api/verification/{id}/status – aktueller Status der Verifikation
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

// GET /api/verification/{id}/data – extrahierte PID-Daten (wenn vorhanden)
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

    // Noch keine Daten (waiting) oder Fehler → 409, damit Clients weiterpollen können
    return Results.Json(new { id, status = state.Status, error = state.ErrorMessage },
        statusCode: 409);
});

// DELETE /api/verification/{id} – Session verwerfen und Polling abbrechen
app.MapDelete("/api/verification/{id}", (string id) =>
{
    if (!sessions.Remove(id, out var state))
        return Results.NotFound(new { error = "Unknown verification session." });

    state?.PollingCts?.Cancel();
    return Results.NoContent();
});

// ── 4. Starten ────────────────────────────────────────────────────────────────
// Kestrel liest Endpoints + Zertifikat direkt aus appsettings.json
// (Kestrel:Endpoints übersteuert app.Urls – kein app.Urls.Add() nötig)

// LAN-IP ermitteln
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

// Browser-URL: lokale HTTPS-Adresse wenn HTTPS aktiv, sonst lokaler HTTP-Fallback
var browserUrl = localHttps ?? localHttp;
try { Process.Start(new ProcessStartInfo(browserUrl) { UseShellExecute = true }); }
catch { Console.WriteLine($"  Bitte {browserUrl} manuell im Browser oeffnen."); }

await app.RunAsync();
