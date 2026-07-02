using System.Net.Http.Json;
using System.Text.Json;
using miEUDIverifier.Configuration;
using miEUDIverifier.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace miEUDIverifier.Services;

public class VerifierApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private const string PidNamespace = "eu.europa.ec.eudi.pid.1";

    private readonly HttpClient _http;
    private readonly VerifierSettings _settings;
    private readonly ILogger<VerifierApiService> _logger;

    public VerifierApiService(
        HttpClient http,
        IOptions<VerifierSettings> settings,
        ILogger<VerifierApiService> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
    }

    // ── Step 1: Initialize Transaction ───────────────────────────────────────

    public async Task<InitTransactionResponse> InitializeTransactionAsync(
        CancellationToken ct = default)
    {
        var credentialId = Guid.NewGuid().ToString();

        var mdocClaims = new List<DcqlClaim>
        {
            new DcqlClaim { Path = new List<string> { PidNamespace, "family_name" } },
            new DcqlClaim { Path = new List<string> { PidNamespace, "given_name" } },
            new DcqlClaim { Path = new List<string> { PidNamespace, "birth_date" } },
        };

        var credentials = new List<DcqlCredential>
        {
            new DcqlCredential
            {
                Id     = credentialId + "-mdoc",
                Format = "mso_mdoc",
                Meta   = new DcqlCredentialMeta { DoctypeValue = PidNamespace },
                Claims = mdocClaims,
            },
        };

        var credentialSets = new List<DcqlCredentialSet>
        {
            new DcqlCredentialSet
            {
                Options = new List<List<string>>
                {
                    new List<string> { credentialId + "-mdoc" },
                },
                Purpose = "Identitaetsnachweis - Name und Geburtsdatum",
            },
        };

        var request = new InitTransactionRequest
        {
            JarMode                    = _settings.JarMode,
            RequestUriMethod           = _settings.RequestUriMethod,
            ResponseMode               = _settings.ResponseMode,
            Profile                    = _settings.Profile,
            AuthorizationRequestScheme = _settings.AuthorizationRequestScheme,
            IssuerChain = string.IsNullOrWhiteSpace(_settings.IssuerChain)
                ? null
                : _settings.IssuerChain,
            Nonce     = Guid.NewGuid().ToString("N"),
            DcqlQuery = new DcqlQuery
            {
                Credentials    = credentials,
                CredentialSets = credentialSets,
            },
        };

        _logger.LogInformation("Initializing transaction at {Url}",
            _settings.BackendUrl + "/ui/presentations");

        var response = await _http.PostAsJsonAsync(
            "/ui/presentations", request, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                "Failed to initialize transaction: " + response.StatusCode + "\n" + body);
        }

        var result = await response.Content
            .ReadFromJsonAsync<InitTransactionResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty response from verifier endpoint.");

        _logger.LogInformation("Transaction initialized. ID: {Id}", result.TransactionId);
        return result;
    }

    // ── Step 2: Poll for Wallet Response ─────────────────────────────────────

    public async Task<WalletResponseEnvelope> WaitForWalletResponseAsync(
        string transactionId,
        IProgress<string>? progress = null,
        Action<string>? onRawResponse = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_settings.PollTimeoutSeconds);
        var interval = TimeSpan.FromSeconds(_settings.PollIntervalSeconds);
        var url      = "/ui/presentations/" + Uri.EscapeDataString(transactionId);

        _logger.LogInformation("Polling for wallet response (timeout: {T}s)",
            _settings.PollTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var response = await _http.GetAsync(url, ct);

            if (response.IsSuccessStatusCode)
            {
                var rawJson = await response.Content.ReadAsStringAsync(ct);
                onRawResponse?.Invoke(rawJson);
                _logger.LogInformation("Poll raw: {Raw}", rawJson);

                var envelope = JsonSerializer.Deserialize<WalletResponseEnvelope>(
                    rawJson, JsonOptions);
                if (envelope is null) { await Task.Delay(interval, ct); continue; }

                if (envelope.IsSubmitted || envelope.HasVpToken)
                {
                    _logger.LogInformation("Wallet response received.");
                    return envelope;
                }

                if (envelope.IsTimedOut)
                    throw new TimeoutException("Presentation request expired.");

                if (envelope.HasError)
                    throw new InvalidOperationException(
                        "Wallet error: " + envelope.Error + " - " + envelope.ErrorDescription);

                progress?.Report(envelope.Status ?? "waiting...");
            }
            else if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Poll {Status}: {Body}", response.StatusCode, body);
            }

            await Task.Delay(interval, ct);
        }

        throw new TimeoutException(
            "No wallet response within " + _settings.PollTimeoutSeconds + "s.");
    }

    // ── Step 3: Extract Identity Data ─────────────────────────────────────────

    /// <summary>
    /// Extrahiert Identitätsdaten aus dem Wallet-Response.
    /// Unterstützt drei Formate:
    ///   A) Backend liefert bereits dekodierte Attribute (credentials[])
    ///   B) vp_token ist ein Objekt mit { credential-id: [base64-DeviceResponse] }
    ///      → Dekodierung über den Utility-Endpunkt des Backends
    ///   C) vp_token ist ein JWT-String (SD-JWT)
    /// </summary>
    public async Task<IdentityData> ExtractIdentityDataAsync(
        WalletResponseEnvelope envelope,
        CancellationToken ct = default)
    {
        var identity = new IdentityData();

        // A) Dekodierte Credentials vom Backend
        if (envelope.Credentials != null && envelope.Credentials.Count > 0)
        {
            foreach (var cred in envelope.Credentials)
            {
                identity.CredentialFormat = cred.Format;
                if (cred.Attributes is null) continue;

                if (cred.Format == "mso_mdoc")
                    ExtractFromMdoc(cred.Attributes.Value, identity);
                else
                    ExtractFromSdJwt(cred.Attributes.Value, identity);

                if (identity.IsComplete) break;
            }
            return identity;
        }

        if (!envelope.VpToken.HasValue) return identity;
        var vpToken = envelope.VpToken.Value;

        // B) vp_token = { "credential-id": ["base64url-CBOR-DeviceResponse", ...] }
        if (vpToken.ValueKind == JsonValueKind.Object)
        {
            identity = await DecodeVpTokenObjectAsync(vpToken, ct);
            return identity;
        }

        // C) vp_token = JWT-String (SD-JWT)
        if (vpToken.ValueKind == JsonValueKind.String)
            TryExtractFromJwt(vpToken, identity);

        return identity;
    }

    // ── B) CBOR mDoc via Utility-Endpunkt dekodieren ──────────────────────────

    private async Task<IdentityData> DecodeVpTokenObjectAsync(
        JsonElement vpToken,
        CancellationToken ct)
    {
        var identity = new IdentityData { CredentialFormat = "mso_mdoc" };

        foreach (var cred in vpToken.EnumerateObject())
        {
            if (cred.Value.ValueKind != JsonValueKind.Array) continue;

            foreach (var item in cred.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var deviceResponse = item.GetString();
                if (string.IsNullOrEmpty(deviceResponse)) continue;

                try
                {
                    // POST an den Utility-Endpunkt des Verifier-Backends
                    var content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("device_response", deviceResponse),
                    });

                    var httpResp = await _http.PostAsync(
                        "/utilities/validations/msoMdoc/deviceResponse", content, ct);

                    var json = await httpResp.Content.ReadAsStringAsync(ct);
                    _logger.LogInformation("Utility endpoint response ({Status}): {Json}",
                        httpResp.StatusCode, json);

                    if (!httpResp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Utility endpoint failed: {Body}", json);
                        continue;
                    }

                    // Antwort-Format: [{ "docType": "...", "attributes": { "ns": { "field": value } } }]
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                    foreach (var docEl in doc.RootElement.EnumerateArray())
                    {
                        if (docEl.TryGetProperty("attributes", out var attrs))
                            ExtractFromMdoc(attrs, identity);
                    }

                    if (identity.IsComplete) return identity;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decode DeviceResponse.");
                }
            }

            if (identity.IsComplete) break;
        }

        return identity;
    }

    // ── Attribute-Extraktion ──────────────────────────────────────────────────

    // mso_mdoc: { "eu.europa.ec.eudi.pid.1": { "family_name": "...", ... } }
    private static void ExtractFromMdoc(JsonElement attributes, IdentityData identity)
    {
        foreach (var ns in attributes.EnumerateObject())
        {
            if (ns.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var claim in ns.Value.EnumerateObject())
            {
                switch (claim.Name)
                {
                    case "family_name": identity.FamilyName = GetStringValue(claim.Value); break;
                    case "given_name":  identity.GivenName  = GetStringValue(claim.Value)?.Trim(); break;
                    case "birth_date":  identity.BirthDate  = ParseDateValue(claim.Value); break;
                    default:
                        var v = GetStringValue(claim.Value);
                        if (v != null)
                            identity.AdditionalClaims[ns.Name + "/" + claim.Name] = v;
                        break;
                }
            }
        }
    }

    // SD-JWT: { "family_name": "...", "given_name": "...", "birth_date": "..." }
    private static void ExtractFromSdJwt(JsonElement attributes, IdentityData identity)
    {
        foreach (var claim in attributes.EnumerateObject())
        {
            switch (claim.Name)
            {
                case "family_name": identity.FamilyName = GetStringValue(claim.Value); break;
                case "given_name":  identity.GivenName  = GetStringValue(claim.Value)?.Trim(); break;
                case "birth_date":  identity.BirthDate  = ParseDateValue(claim.Value); break;
                default:
                    var v = GetStringValue(claim.Value);
                    if (v != null)
                        identity.AdditionalClaims[claim.Name] = v;
                    break;
            }
        }
    }

    private void TryExtractFromJwt(JsonElement vpToken, IdentityData identity)
    {
        try
        {
            var raw   = vpToken.GetString() ?? string.Empty;
            var parts = raw.Split('.');
            if (parts.Length < 2) return;
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            ExtractFromSdJwt(doc.RootElement, identity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract from JWT vp_token.");
        }
    }

    private static string? GetStringValue(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String: return el.GetString();
            case JsonValueKind.Number: return el.GetRawText();
            case JsonValueKind.True:   return "true";
            case JsonValueKind.False:  return "false";
            default:                   return null;
        }
    }

    /// <summary>
    /// Konvertiert ein Datums-Feld aus dem CBOR-Decoder.
    /// Der Utility-Endpunkt liefert birth_date manchmal als Unix-Timestamp
    /// in Millisekunden (z.B. 212371200000 = 1976-09-24) statt als ISO-String.
    /// </summary>
    private static string? ParseDateValue(JsonElement el)
    {
        // Bereits ein ISO-Datum-String → direkt zurückgeben
        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var raw))
        {
            // Heuristik: Betrag > 1e10 → vermutlich Millisekunden (> Jahr 2286 in Sekunden),
            // kleinere Beträge → Sekunden seit Epoch. Der Betrag ist entscheidend, damit
            // negative Timestamps (Geburtsdatum vor 1970) korrekt als ms erkannt werden.
            try
            {
                var dt = Math.Abs(raw) > 10_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
                    : DateTimeOffset.FromUnixTimeSeconds(raw);

                // Plausibilitätsprüfung: Geburtsdatum zwischen 1900 und heute
                if (dt.Year >= 1900 && dt.Year <= DateTimeOffset.UtcNow.Year)
                    return dt.ToString("yyyy-MM-dd");
            }
            catch { /* Fallback auf Rohwert */ }

            return raw.ToString();
        }

        return el.GetRawText();
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "=";  break;
        }
        return Convert.FromBase64String(padded);
    }
}
