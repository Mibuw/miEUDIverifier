using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // The German and EU SD-JWT VC PIDs spell two attributes differently from mso_mdoc.
    // Everything else is identical, so only these two need translating.
    private static string ToSdJwtClaimName(string mdocClaimName) => mdocClaimName switch
    {
        "birth_date"  => "birthdate",
        "nationality" => "nationalities",
        _             => mdocClaimName,
    };

    // Blank entries are dropped so a vct list can be emptied from the environment: .NET
    // configuration cannot express an empty collection, but "__0=" yields a single blank entry,
    // which is how a backend scoped to a Registration Certificate switches an option off.
    private static List<string> UsableVctValues(IEnumerable<string>? values) =>
        values?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? new List<string>();

    public async Task<InitTransactionResponse> InitializeTransactionAsync(
        TransactionOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new TransactionOptions();

        // The verifier backend attaches exactly one Registration Certificate per transaction, so a
        // request must stay inside the format that certificate covers. The two scoping flags are
        // therefore mutually exclusive — together they would leave no credential to ask for.
        if (options.MdocOnly && options.SdJwtOnly)
        {
            throw new InvalidOperationException(
                "TransactionOptions.MdocOnly and SdJwtOnly are mutually exclusive.");
        }

        var credentialId = Guid.NewGuid().ToString();
        var credentials = new List<DcqlCredential>();
        var credentialSetOptions = new List<List<string>>();

        // PID attributes in mso_mdoc spelling; the SD-JWT options translate the differing names.
        var pidClaims = UsableVctValues(options.PidClaims ?? _settings.PidClaims);
        if (pidClaims.Count == 0)
        {
            throw new InvalidOperationException("No PID attributes configured to request.");
        }

        // mso_mdoc PID — omitted only for backends scoped to an SD-JWT VC Registration Certificate.
        if (!options.SdJwtOnly)
        {
            credentials.Add(new DcqlCredential
            {
                Id     = credentialId + "-mdoc",
                Format = "mso_mdoc",
                Meta   = new DcqlCredentialMeta { DoctypeValue = PidNamespace },
                // mso_mdoc: claim paths are ["namespace", "element_identifier"]
                Claims = pidClaims
                    .Select(c => new DcqlClaim { Path = new List<string> { PidNamespace, c } })
                    .ToList(),
            });
            credentialSetOptions.Add(new List<string> { credentialId + "-mdoc" });
        }

        // SD-JWT VC alternatives — omitted for backends scoped to an mso_mdoc Registration
        // Certificate (e.g. the German sandbox): requesting more than registered makes the wallet
        // abort. Each option is driven by its own vct list; an empty list disables it, which is how
        // a registered backend narrows the request to exactly what its certificate covers.
        if (!options.MdocOnly)
        {
            // SD-JWT VC: claim paths are flat ["claim_name"]
            var sdJwtVctValues = UsableVctValues(options.SdJwtVctValues ?? _settings.SdJwtVctValues);
            if (sdJwtVctValues is { Count: > 0 })
            {
                credentials.Add(new DcqlCredential
                {
                    Id     = credentialId + "-sdjwt",
                    Format = _settings.SdJwtFormat,
                    Meta   = new DcqlCredentialMeta { VctValues = sdJwtVctValues },
                    Claims = new List<DcqlClaim>
                    {
                        new DcqlClaim { Path = new List<string> { "family_name" } },
                        new DcqlClaim { Path = new List<string> { "given_name" } },
                        new DcqlClaim { Path = new List<string> { "birth_date" } },
                    },
                });
                credentialSetOptions.Add(new List<string> { credentialId + "-sdjwt" });
            }

            // German EUDI Wallet PID: own vct (urn:eudi:pid:de:1) and the OIDC-style claim name
            // "birthdate" instead of "birth_date" → needs a separate DCQL entry.
            var germanPidVctValues = UsableVctValues(options.GermanPidVctValues ?? _settings.GermanPidVctValues);
            if (germanPidVctValues is { Count: > 0 })
            {
                credentials.Add(new DcqlCredential
                {
                    Id     = credentialId + "-sdjwt-de",
                    Format = _settings.SdJwtFormat,
                    Meta   = new DcqlCredentialMeta { VctValues = germanPidVctValues },
                    Claims = pidClaims
                        .Select(c => new DcqlClaim { Path = new List<string> { ToSdJwtClaimName(c) } })
                        .ToList(),
                });
                credentialSetOptions.Add(new List<string> { credentialId + "-sdjwt-de" });
            }
        }

        if (credentials.Count == 0)
        {
            throw new InvalidOperationException(
                "No credential options left to request — check SdJwtOnly against the configured vct values.");
        }

        var credentialSets = new List<DcqlCredentialSet>
        {
            new DcqlCredentialSet
            {
                Options = credentialSetOptions,
                Purpose = "Identitaetsnachweis - Name und Geburtsdatum",
            },
        };

        var request = new InitTransactionRequest
        {
            JarMode                    = _settings.JarMode,
            RequestUriMethod           = _settings.RequestUriMethod,
            ResponseMode               = string.IsNullOrWhiteSpace(options.ResponseMode)
                ? _settings.ResponseMode
                : options.ResponseMode,
            Profile                    = _settings.Profile,
            AuthorizationRequestScheme = _settings.AuthorizationRequestScheme,
            IssuerChain = string.IsNullOrWhiteSpace(_settings.IssuerChain)
                ? null
                : _settings.IssuerChain,
            IntendedUseId = string.IsNullOrWhiteSpace(options.IntendedUseId)
                ? (string.IsNullOrWhiteSpace(_settings.IntendedUseId) ? null : _settings.IntendedUseId)
                : options.IntendedUseId,
            Nonce     = Guid.NewGuid().ToString("N"),
            DcqlQuery = new DcqlQuery
            {
                Credentials    = credentials,
                CredentialSets = credentialSets,
            },
        };

        // Log the actual target (the HttpClient base address), which may differ from
        // _settings.BackendUrl when the service is bound to a named backend (eu/de).
        _logger.LogInformation("Initializing transaction at {Url}",
            new Uri(_http.BaseAddress!, "ui/presentations"));

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
                    case "place_of_birth":    identity.PlaceOfBirth     = GetPlaceValue(claim.Value); break;
                    case "nationality":       identity.Nationality      = GetStringValue(claim.Value); break;
                    case "issuing_authority": identity.IssuingAuthority = GetStringValue(claim.Value); break;
                    case "issuing_country":   identity.IssuingCountry   = GetStringValue(claim.Value); break;
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
            case "place_of_birth":    identity.PlaceOfBirth     = GetPlaceValue(value); break;
            // mso_mdoc spells this "nationality", SD-JWT VC "nationalities" — accept both.
            case "nationality":
            case "nationalities":     identity.Nationality      = GetStringValue(value); break;
            case "issuing_authority": identity.IssuingAuthority = GetStringValue(value); break;
            case "issuing_country":   identity.IssuingCountry   = GetStringValue(value); break;
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

        // Disclosures are the segments after the issuer JWT. The trailing one may be a
        // key-binding JWT, which is recognisable by its dots.
        var disclosures = new List<string>();
        for (var i = 1; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (!string.IsNullOrEmpty(seg) && !seg.Contains('.')) disclosures.Add(seg);
        }

        var jwtParts = segments[0].Split('.');
        if (jwtParts.Length < 2) return;

        JsonNode? payload;
        try
        {
            payload = JsonNode.Parse(Base64UrlDecode(jwtParts[1]));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse SD-JWT issuer payload.");
            return;
        }
        if (payload is not JsonObject payloadObject) return;

        // Map each disclosure by its digest, so _sd entries and array elements can be resolved.
        var hashAlgorithm = payloadObject["_sd_alg"]?.GetValue<string>() ?? "sha-256";
        var byDigest = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var seg in disclosures)
        {
            try
            {
                if (JsonNode.Parse(Base64UrlDecode(seg)) is JsonArray arr && arr.Count is 2 or 3)
                    byDigest[ComputeDisclosureDigest(seg, hashAlgorithm)] = arr;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse SD-JWT disclosure.");
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        ResolveDisclosures(payloadObject, byDigest, used, depth: 0);

        try
        {
            using var doc = JsonDocument.Parse(payloadObject.ToJsonString());
            ExtractFromSdJwt(doc.RootElement, identity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read resolved SD-JWT payload.");
        }

        // Any disclosure the payload never referenced is still applied by name. Earlier versions
        // applied every disclosure blindly; keeping that as a fallback avoids losing data from a
        // wallet that omits the matching _sd entry.
        foreach (var (digest, arr) in byDigest)
        {
            if (used.Contains(digest) || arr.Count != 3) continue;
            using var doc = JsonDocument.Parse(arr.ToJsonString());
            ApplySdJwtClaim(doc.RootElement[1].GetString(), doc.RootElement[2], identity);
        }
    }

    /// <summary>
    /// Replaces SD-JWT selective-disclosure placeholders with their disclosed values, in place.
    /// Objects carry an <c>_sd</c> array of digests standing for hidden properties; array elements
    /// are hidden as <c>{"...": "&lt;digest&gt;"}</c>. Both forms nest, so this recurses — which is
    /// what a flat pass misses for place of birth (a nested object) and nationalities (an array).
    /// </summary>
    private static void ResolveDisclosures(
        JsonNode? node,
        IReadOnlyDictionary<string, JsonArray> byDigest,
        HashSet<string> used,
        int depth)
    {
        const int maxDepth = 32;
        if (node is null || depth > maxDepth) return;

        if (node is JsonObject obj)
        {
            // Hidden properties: _sd holds their digests, each disclosure being [salt, name, value].
            if (obj["_sd"] is JsonArray sd)
            {
                foreach (var digestNode in sd)
                {
                    var digest = digestNode?.GetValue<string>();
                    if (digest is null || !byDigest.TryGetValue(digest, out var disclosure)) continue;
                    if (disclosure.Count != 3) continue;

                    var name = disclosure[1]?.GetValue<string>();
                    if (name is null || obj.ContainsKey(name)) continue;

                    used.Add(digest);
                    obj[name] = disclosure[2]?.DeepClone();
                }
            }
            obj.Remove("_sd");
            obj.Remove("_sd_alg");

            foreach (var key in obj.Select(kv => kv.Key).ToList())
                ResolveDisclosures(obj[key], byDigest, used, depth + 1);
            return;
        }

        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                // A hidden element is {"...": "<digest>"}; its disclosure is [salt, value].
                if (array[i] is JsonObject element
                    && element.Count == 1
                    && element["..."]?.GetValue<string>() is { } digest)
                {
                    if (byDigest.TryGetValue(digest, out var disclosure) && disclosure.Count == 2)
                    {
                        used.Add(digest);
                        array[i] = disclosure[1]?.DeepClone();
                    }
                    else
                    {
                        // Not disclosed by the holder — drop it rather than render the placeholder.
                        array[i] = null;
                    }
                    continue;
                }
                ResolveDisclosures(array[i], byDigest, used, depth + 1);
            }

            for (var i = array.Count - 1; i >= 0; i--)
                if (array[i] is null) array.RemoveAt(i);
        }
    }

    /// <summary>
    /// Digest of a disclosure: the hash of its base64url string exactly as it appears in the
    /// token, base64url encoded. Driven by the payload's <c>_sd_alg</c> (default sha-256).
    /// </summary>
    private static string ComputeDisclosureDigest(string disclosureSegment, string hashAlgorithm)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(disclosureSegment);
        byte[] hash = hashAlgorithm.ToLowerInvariant() switch
        {
            "sha-384" => SHA384.HashData(bytes),
            "sha-512" => SHA512.HashData(bytes),
            _         => SHA256.HashData(bytes),
        };
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string? GetStringValue(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String: return el.GetString();
            case JsonValueKind.Number: return el.GetRawText();
            case JsonValueKind.True:   return "true";
            case JsonValueKind.False:  return "false";
            // PID attributes like nationality are arrays; without this they would be dropped
            // silently, here and in AdditionalClaims.
            case JsonValueKind.Array:
                var items = el.EnumerateArray()
                    .Select(GetStringValue)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList();
                return items.Count > 0 ? string.Join(", ", items) : null;
            case JsonValueKind.Object: return el.GetRawText();
            default:                   return null;
        }
    }

    /// <summary>
    /// Extracts a place of birth. Both PID formats carry a structured value; the German PID
    /// populates only "locality", so that is preferred, with the raw value as a fallback.
    /// </summary>
    private static string? GetPlaceValue(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "locality", "country", "region" })
            {
                if (el.TryGetProperty(name, out var v))
                {
                    var s = GetStringValue(v);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
        }
        return GetStringValue(el);
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
