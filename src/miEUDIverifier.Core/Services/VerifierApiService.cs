using System.Net.Http.Json;
using System.Text.Encodings.Web;
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
        // Don't escape characters like '+' (e.g. the "dc+sd-jwt" format id) into \uXXXX;
        // this is a server-to-server JSON body, so relaxed escaping keeps it human-readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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

        // mso_mdoc: claim paths are ["namespace", "element_identifier"]
        var mdocClaims = new List<DcqlClaim>
        {
            new DcqlClaim { Path = new List<string> { PidNamespace, "family_name" } },
            new DcqlClaim { Path = new List<string> { PidNamespace, "given_name" } },
            new DcqlClaim { Path = new List<string> { PidNamespace, "birth_date" } },
        };

        // SD-JWT VC: claim paths are flat ["claim_name"]
        var sdJwtClaims = new List<DcqlClaim>
        {
            new DcqlClaim { Path = new List<string> { "family_name" } },
            new DcqlClaim { Path = new List<string> { "given_name" } },
            new DcqlClaim { Path = new List<string> { "birth_date" } },
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
            new DcqlCredential
            {
                Id     = credentialId + "-sdjwt",
                Format = _settings.SdJwtFormat,
                Meta   = new DcqlCredentialMeta { VctValues = _settings.SdJwtVctValues },
                Claims = sdJwtClaims,
            },
        };

        // Two alternative options → the wallet may satisfy the request with EITHER the
        // mso_mdoc PID OR the SD-JWT VC PID, whichever it holds.
        var credentialSets = new List<DcqlCredentialSet>
        {
            new DcqlCredentialSet
            {
                Options = new List<List<string>>
                {
                    new List<string> { credentialId + "-mdoc" },
                    new List<string> { credentialId + "-sdjwt" },
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

        // C) vp_token = SD-JWT VC presentation string
        if (vpToken.ValueKind == JsonValueKind.String)
        {
            var sdJwt = vpToken.GetString();
            if (!string.IsNullOrEmpty(sdJwt))
                ExtractFromSdJwtString(sdJwt, identity);
        }

        return identity;
    }

    // ── B) CBOR mDoc via Utility-Endpunkt dekodieren ──────────────────────────

    private async Task<IdentityData> DecodeVpTokenObjectAsync(
        JsonElement vpToken,
        CancellationToken ct)
    {
        var identity = new IdentityData();

        foreach (var cred in vpToken.EnumerateObject())
        {
            var value = cred.Value;

            // SD-JWT VC: the presentation is a single string "<jwt>~<disclosure>~…"
            if (value.ValueKind == JsonValueKind.String)
            {
                var sdJwt = value.GetString();
                if (!string.IsNullOrEmpty(sdJwt))
                    ExtractFromSdJwtString(sdJwt, identity);
                if (identity.IsComplete) break;
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array) continue;

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var presentation = item.GetString();
                if (string.IsNullOrEmpty(presentation)) continue;

                // Heuristic: an SD-JWT contains '.' (JWT) and/or '~' (disclosures);
                // an mso_mdoc DeviceResponse is base64url-encoded CBOR (neither).
                if (presentation.Contains('~') || presentation.Contains('.'))
                    ExtractFromSdJwtString(presentation, identity);
                else
                    await DecodeMdocDeviceResponseAsync(presentation, identity, ct);

                if (identity.IsComplete) break;
            }

            if (identity.IsComplete) break;
        }

        return identity;
    }

    // Decode a single base64url-CBOR mso_mdoc DeviceResponse via the backend utility endpoint.
    private async Task DecodeMdocDeviceResponseAsync(
        string deviceResponse, IdentityData identity, CancellationToken ct)
    {
        try
        {
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
                return;
            }

            // Response format: [{ "docType": "...", "attributes": { "ns": { "field": value } } }]
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            identity.CredentialFormat = "mso_mdoc";
            foreach (var docEl in doc.RootElement.EnumerateArray())
            {
                if (docEl.TryGetProperty("attributes", out var attrs))
                    ExtractFromMdoc(attrs, identity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode DeviceResponse.");
        }
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

    // SD-JWT (already decoded to a flat object): { "family_name": "...", "given_name": "...", ... }
    private static void ExtractFromSdJwt(JsonElement attributes, IdentityData identity)
    {
        foreach (var claim in attributes.EnumerateObject())
            ApplySdJwtClaim(claim.Name, claim.Value, identity);
    }

    // Maps a single SD-JWT claim (name + value) onto the identity result.
    private static void ApplySdJwtClaim(string? name, JsonElement value, IdentityData identity)
    {
        switch (name)
        {
            case "family_name": identity.FamilyName = GetStringValue(value); break;
            case "given_name":  identity.GivenName  = GetStringValue(value)?.Trim(); break;
            // EUDI PID uses "birth_date"; accept the OIDC-style "birthdate" as an alias too.
            case "birth_date":
            case "birthdate":   identity.BirthDate  = ParseDateValue(value); break;
            case null:          break;
            default:
                var v = GetStringValue(value);
                if (v != null)
                    identity.AdditionalClaims[name] = v;
                break;
        }
    }

    /// <summary>
    /// Parses an SD-JWT VC presentation string of the form
    /// <c>&lt;issuer-jwt&gt;~&lt;disclosure&gt;~…~[&lt;key-binding-jwt&gt;]</c>.
    /// Plaintext claims in the issuer JWT payload as well as the selectively disclosed
    /// values (carried in the base64url-encoded disclosures) are extracted.
    /// </summary>
    private void ExtractFromSdJwtString(string sdJwt, IdentityData identity)
    {
        identity.CredentialFormat ??= _settings.SdJwtFormat;
        var segments = sdJwt.Split('~');

        // 1) Plaintext claims directly in the issuer JWT payload (segment 0 = header.payload.sig).
        var jwtParts = segments[0].Split('.');
        if (jwtParts.Length >= 2)
        {
            try
            {
                using var doc = JsonDocument.Parse(Base64UrlDecode(jwtParts[1]));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    ExtractFromSdJwt(doc.RootElement, identity);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse SD-JWT issuer payload.");
            }
        }

        // 2) Disclosures: each is base64url(JSON [salt, claim_name, claim_value]).
        //    The trailing segment may be a key-binding JWT (contains '.') → skip it.
        for (var i = 1; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (string.IsNullOrEmpty(seg) || seg.Contains('.')) continue;

            try
            {
                using var doc = JsonDocument.Parse(Base64UrlDecode(seg));
                var arr = doc.RootElement;
                if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() == 3)
                    ApplySdJwtClaim(arr[1].GetString(), arr[2], identity);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse SD-JWT disclosure.");
            }
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
