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

        // Alternative options → the wallet may satisfy the request with ANY of these,
        // whichever PID it holds.
        var options = new List<List<string>>
        {
            new List<string> { credentialId + "-mdoc" },
            new List<string> { credentialId + "-sdjwt" },
        };

        // German EUDI Wallet (Bundesdruckerei prototype PID): own vct and the OIDC-style
        // claim name "birthdate" instead of "birth_date" → needs a separate DCQL entry.
        if (_settings.GermanPidVctValues is { Count: > 0 })
        {
            credentials.Add(new DcqlCredential
            {
                Id     = credentialId + "-sdjwt-de",
                Format = _settings.SdJwtFormat,
                Meta   = new DcqlCredentialMeta { VctValues = _settings.GermanPidVctValues },
                Claims = new List<DcqlClaim>
                {
                    new DcqlClaim { Path = new List<string> { "family_name" } },
                    new DcqlClaim { Path = new List<string> { "given_name" } },
                    new DcqlClaim { Path = new List<string> { "birthdate" } },
                },
            });
            options.Add(new List<string> { credentialId + "-sdjwt-de" });
        }

        var credentialSets = new List<DcqlCredentialSet>
        {
            new DcqlCredentialSet
            {
                Options = options,
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
    /// Extracts identity data from the wallet response.
    /// Supports three formats:
    ///   A) The backend already returns decoded attributes (credentials[])
    ///   B) vp_token is an object of { credential-id: [base64-DeviceResponse] }
    ///      → decoded via the backend's utility endpoint
    ///   C) vp_token is a JWT string (SD-JWT)
    /// </summary>
    public async Task<IdentityData> ExtractIdentityDataAsync(
        WalletResponseEnvelope envelope,
        CancellationToken ct = default)
    {
        var identity = new IdentityData();

        // A) Credentials already decoded by the backend
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

    // ── B) Decode CBOR mDoc via the utility endpoint ──────────────────────────

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

    // ── Attribute extraction ──────────────────────────────────────────────────

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
    /// Converts a date field coming from the CBOR decoder.
    /// The utility endpoint sometimes returns birth_date as a Unix timestamp
    /// in milliseconds (e.g. 212371200000 = 1976-09-24) instead of an ISO string.
    /// </summary>
    private static string? ParseDateValue(JsonElement el)
    {
        // Already an ISO date string → return as is
        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var raw))
        {
            // Heuristic: magnitude > 1e10 → probably milliseconds (> year 2286 in seconds),
            // smaller magnitudes → seconds since epoch. The magnitude matters so that
            // negative timestamps (birth dates before 1970) are correctly detected as ms.
            try
            {
                var dt = Math.Abs(raw) > 10_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
                    : DateTimeOffset.FromUnixTimeSeconds(raw);

                // Plausibility check: birth date between 1900 and today
                if (dt.Year >= 1900 && dt.Year <= DateTimeOffset.UtcNow.Year)
                    return dt.ToString("yyyy-MM-dd");
            }
            catch { /* fall back to the raw value */ }

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
