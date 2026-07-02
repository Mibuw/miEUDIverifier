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
