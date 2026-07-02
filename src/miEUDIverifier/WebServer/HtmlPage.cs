using System.Globalization;
using System.Text.Json;

namespace miEUDIverifier.WebServer;

/// <summary>
/// Generates the single-page HTML UI. Dynamic values are injected via
/// simple string replacement — no Razor/Blazor dependency needed.
/// The UI is bilingual (German / English); the language is chosen from the
/// browser's Accept-Language header with English as the fallback.
/// </summary>
public static class HtmlPage
{
    public static string Render(AppState state, string? acceptLanguage)
    {
        var lang = PickLanguage(acceptLanguage);
        var t    = lang == "de" ? De : En;

        // Strings needed by the client-side script (dynamic status/buttons).
        var js = JsonSerializer.Serialize(new
        {
            waiting            = t["waiting"],
            received           = t["received"],
            errorPrefix        = t["errorPrefix"],
            unknown            = t["unknown"],
            loading            = t["loading"],
            networkErrorPrefix = t["networkErrorPrefix"],
            newRequest         = t["btnNewRequest"],
        });

        return Template
            .Replace("___LANG___",            lang)
            .Replace("___QR_BASE64___",       state.QrBase64)
            .Replace("___SUBTITLE___",        t["subtitle"])
            .Replace("___WAITING___",         t["waiting"])
            .Replace("___HINT___",            t["hint"])
            .Replace("___RESULT_TITLE___",    t["resultTitle"])
            .Replace("___LABEL_FAMILY___",    t["labelFamily"])
            .Replace("___LABEL_GIVEN___",     t["labelGiven"])
            .Replace("___LABEL_BIRTH___",     t["labelBirth"])
            .Replace("___BTN_SCAN_AGAIN___",  t["btnScanAgain"])
            .Replace("___BTN_NEW_REQUEST___", t["btnNewRequest"])
            .Replace("___T_JSON___",          js);
    }

    /// <summary>
    /// Picks "de" or "en" from an Accept-Language header, honouring q-values.
    /// English is the fallback when neither is requested or the header is empty.
    /// </summary>
    public static string PickLanguage(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
            return "en";

        var ranked = acceptLanguage
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry =>
            {
                var parts = entry.Split(';', StringSplitOptions.TrimEntries);
                var code  = parts[0].ToLowerInvariant();
                var q     = 1.0;
                var qPart = parts.Skip(1)
                    .FirstOrDefault(p => p.StartsWith("q=", StringComparison.OrdinalIgnoreCase));
                if (qPart is not null &&
                    double.TryParse(qPart.AsSpan(2), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var parsed))
                    q = parsed;
                return (code, q);
            })
            .OrderByDescending(x => x.q);

        foreach (var (code, _) in ranked)
        {
            if (code.StartsWith("de")) return "de";
            if (code.StartsWith("en")) return "en";
        }
        return "en";
    }

    private static readonly Dictionary<string, string> De = new()
    {
        ["subtitle"]           = "Scanne den QR-Code mit der EUDI Wallet App",
        ["waiting"]            = "Warte auf Wallet-Antwort …",
        ["hint"]               = "Öffne die EUDI Wallet App, scanne den QR-Code<br>und bestätige die Datenweitergabe.",
        ["resultTitle"]        = "Identität verifiziert",
        ["labelFamily"]        = "Familienname",
        ["labelGiven"]         = "Vorname",
        ["labelBirth"]         = "Geburtsdatum",
        ["btnScanAgain"]       = "Neuen Scan starten",
        ["btnNewRequest"]      = "Neuer Request",
        ["received"]           = "Identität empfangen",
        ["errorPrefix"]        = "Fehler: ",
        ["unknown"]            = "Unbekannt",
        ["loading"]            = "Lade…",
        ["networkErrorPrefix"] = "Netzwerkfehler: ",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["subtitle"]           = "Scan the QR code with the EUDI Wallet App",
        ["waiting"]            = "Waiting for wallet response …",
        ["hint"]               = "Open the EUDI Wallet App, scan the QR code<br>and confirm the data sharing.",
        ["resultTitle"]        = "Identity verified",
        ["labelFamily"]        = "Family name",
        ["labelGiven"]         = "Given name",
        ["labelBirth"]         = "Date of birth",
        ["btnScanAgain"]       = "Start new scan",
        ["btnNewRequest"]      = "New request",
        ["received"]           = "Identity received",
        ["errorPrefix"]        = "Error: ",
        ["unknown"]            = "Unknown",
        ["loading"]            = "Loading…",
        ["networkErrorPrefix"] = "Network error: ",
    };

    // -------------------------------------------------------------------------
    // The template is a plain (non-interpolated) string, so CSS curly braces
    // do NOT need to be escaped. Dynamic values use ___PLACEHOLDER___ tokens.
    // -------------------------------------------------------------------------
    private static readonly string Template = """
        <!DOCTYPE html>
        <html lang="___LANG___">
        <head>
          <meta charset="UTF-8">
          <meta name="viewport" content="width=device-width, initial-scale=1.0">
          <title>miEUDIverifier</title>
          <style>
            *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

            body {
              font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto,
                           Helvetica, Arial, sans-serif;
              background: linear-gradient(135deg, #f0f4ff 0%, #e8f0fe 100%);
              min-height: 100vh;
              display: flex;
              align-items: center;
              justify-content: center;
              padding: 24px;
            }

            .card {
              background: #ffffff;
              border-radius: 20px;
              box-shadow: 0 8px 40px rgba(0, 51, 153, 0.12);
              padding: 44px 40px 36px;
              max-width: 460px;
              width: 100%;
              text-align: center;
              transition: all 0.3s ease;
            }

            /* ── Header ── */
            .eu-flag { font-size: 32px; margin-bottom: 8px; }

            .brand {
              font-size: 11px;
              font-weight: 700;
              letter-spacing: 3px;
              text-transform: uppercase;
              color: #003399;
              margin-bottom: 10px;
            }

            h1 {
              font-size: 24px;
              font-weight: 700;
              color: #0d1b4b;
              margin-bottom: 6px;
            }

            .subtitle {
              font-size: 14px;
              color: #6b7280;
              margin-bottom: 28px;
            }

            /* ── QR code ── */
            .qr-wrap {
              display: inline-flex;
              align-items: center;
              justify-content: center;
              background: #fafafa;
              border: 2px solid #e5e7eb;
              border-radius: 16px;
              padding: 16px;
              margin-bottom: 22px;
            }

            .qr-wrap img {
              display: block;
              width: 220px;
              height: 220px;
              image-rendering: pixelated;
            }

            /* ── Status badge ── */
            .status {
              display: inline-flex;
              align-items: center;
              gap: 9px;
              padding: 8px 20px;
              border-radius: 999px;
              font-size: 14px;
              font-weight: 500;
              margin-bottom: 14px;
              transition: all 0.4s ease;
            }

            .status.waiting {
              background: #fffbeb;
              color: #d97706;
              border: 1px solid #fcd34d;
            }

            .status.done {
              background: #f0fdf4;
              color: #16a34a;
              border: 1px solid #86efac;
            }

            .status.error {
              background: #fef2f2;
              color: #dc2626;
              border: 1px solid #fca5a5;
            }

            .spinner {
              width: 14px;
              height: 14px;
              border: 2px solid #fcd34d;
              border-top-color: #d97706;
              border-radius: 50%;
              animation: spin 0.75s linear infinite;
              flex-shrink: 0;
            }

            @keyframes spin { to { transform: rotate(360deg); } }

            .hint {
              font-size: 13px;
              color: #9ca3af;
              line-height: 1.6;
            }

            /* ── Result card ── */
            .result {
              background: #f0fdf4;
              border: 1px solid #bbf7d0;
              border-radius: 14px;
              padding: 24px;
              text-align: left;
              margin-top: 18px;
              display: none;
            }

            .result h2 {
              font-size: 15px;
              font-weight: 700;
              color: #15803d;
              margin-bottom: 18px;
              display: flex;
              align-items: center;
              gap: 8px;
            }

            .field {
              display: flex;
              justify-content: space-between;
              align-items: baseline;
              padding: 9px 0;
              border-bottom: 1px solid #d1fae5;
            }

            .field:last-of-type { border-bottom: none; }

            .field-label {
              font-size: 12px;
              font-weight: 600;
              text-transform: uppercase;
              letter-spacing: 0.5px;
              color: #6b7280;
            }

            .field-value {
              font-size: 15px;
              font-weight: 600;
              color: #0d1b4b;
              text-align: right;
            }

            .format-tag {
              display: inline-block;
              margin-top: 14px;
              font-size: 11px;
              background: #ede9fe;
              color: #5b21b6;
              padding: 3px 10px;
              border-radius: 999px;
              font-weight: 500;
            }

            .reset-btn {
              display: block;
              width: 100%;
              margin-top: 24px;
              padding: 12px 24px;
              background: #003399;
              color: #fff;
              border: none;
              border-radius: 10px;
              font-size: 15px;
              font-weight: 600;
              cursor: pointer;
              transition: background 0.2s, transform 0.1s;
              letter-spacing: 0.3px;
            }
            .reset-btn:hover:not(:disabled) { background: #002277; }
            .reset-btn:active:not(:disabled) { transform: scale(0.98); }
            .reset-btn:disabled { background: #9ca3af; cursor: not-allowed; }

            .scan-again-btn {
              display: block;
              width: 100%;
              margin-top: 20px;
              padding: 11px 24px;
              background: #16a34a;
              color: #fff;
              border: none;
              border-radius: 10px;
              font-size: 15px;
              font-weight: 600;
              cursor: pointer;
              transition: background 0.2s, transform 0.1s;
            }
            .scan-again-btn:hover:not(:disabled) { background: #15803d; }
            .scan-again-btn:active:not(:disabled) { transform: scale(0.98); }
            .scan-again-btn:disabled { background: #9ca3af; cursor: not-allowed; }
          </style>
        </head>
        <body>

          <div class="card">

            <div class="eu-flag">🇪🇺</div>
            <div class="brand">European Digital Identity</div>
            <h1>miEUDIverifier</h1>
            <p class="subtitle">___SUBTITLE___</p>

            <!-- QR Code -->
            <div id="qr-section">
              <div class="qr-wrap">
                <img src="data:image/png;base64,___QR_BASE64___"
                     alt="OpenID4VP QR code" />
              </div>
            </div>

            <!-- Status -->
            <div class="status waiting" id="status-badge">
              <div class="spinner" id="spinner"></div>
              <span id="status-text">___WAITING___</span>
            </div>

            <p class="hint" id="hint">___HINT___</p>

            <!-- Identity result -->
            <div class="result" id="result">
              <h2>&#10003;&ensp;___RESULT_TITLE___</h2>
              <div class="field">
                <span class="field-label">___LABEL_FAMILY___</span>
                <span class="field-value" id="r-family">&mdash;</span>
              </div>
              <div class="field">
                <span class="field-label">___LABEL_GIVEN___</span>
                <span class="field-value" id="r-given">&mdash;</span>
              </div>
              <div class="field">
                <span class="field-label">___LABEL_BIRTH___</span>
                <span class="field-value" id="r-birth">&mdash;</span>
              </div>
              <div class="format-tag" id="r-format"></div>
              <button class="scan-again-btn" onclick="resetRequest()">
                &#8635;&ensp;___BTN_SCAN_AGAIN___
              </button>
            </div>

            <button class="reset-btn" id="reset-btn" onclick="resetRequest()">
              &#8635;&ensp;___BTN_NEW_REQUEST___
            </button>

          </div>

          <script>
            const T = ___T_JSON___;
            const NEW_REQUEST_HTML = '&#8635;&ensp;' + T.newRequest;
            let finished = false;

            async function poll() {
              if (finished) return;
              try {
                const resp = await fetch('/api/status');
                const d    = await resp.json();

                if (d.status === 'complete' || d.status === 'partial') {
                  finished = true;

                  // Hide QR + spinner
                  document.getElementById('qr-section').style.display = 'none';
                  document.getElementById('spinner').style.display    = 'none';
                  document.getElementById('hint').style.display        = 'none';

                  // Update status badge
                  const badge = document.getElementById('status-badge');
                  badge.className = 'status done';
                  document.getElementById('status-text').textContent = T.received;

                  // Fill result card
                  document.getElementById('r-family').textContent = d.familyName || '—';
                  document.getElementById('r-given').textContent  = d.givenName  || '—';
                  document.getElementById('r-birth').textContent  = d.birthDate  || '—';
                  if (d.format) {
                    document.getElementById('r-format').textContent = 'Format: ' + d.format;
                  }
                  document.getElementById('result').style.display = 'block';
                  document.getElementById('reset-btn').style.display = 'none';

                } else if (d.status === 'error') {
                  finished = true;
                  document.getElementById('spinner').style.display = 'none';
                  const badge = document.getElementById('status-badge');
                  badge.className = 'status error';
                  document.getElementById('status-text').textContent =
                    T.errorPrefix + (d.error || T.unknown);
                }
              } catch (_) { /* server may be shutting down – ignore */ }
            }

            // Poll every 2 seconds
            setInterval(poll, 2000);
            async function resetRequest() {
              const btn = document.getElementById('reset-btn');
              btn.disabled = true;
              btn.textContent = T.loading;

              try {
                const resp = await fetch('/api/reset', { method: 'POST' });
                const d    = await resp.json();

                if (d.error) {
                  alert(T.errorPrefix + d.error);
                  return;
                }

                // Reset UI
                finished = false;
                document.getElementById('reset-btn').style.display = '';
                document.getElementById('qr-section').style.display = '';
                document.querySelector('.qr-wrap img').src =
                  'data:image/png;base64,' + d.qrBase64;
                const badge = document.getElementById('status-badge');
                badge.className = 'status waiting';
                document.getElementById('spinner').style.display = '';
                document.getElementById('status-text').textContent = T.waiting;
                document.getElementById('hint').style.display = '';
                document.getElementById('result').style.display = 'none';

              } catch (e) {
                alert(T.networkErrorPrefix + e.message);
              } finally {
                btn.disabled = false;
                btn.innerHTML = NEW_REQUEST_HTML;
              }
            }
          </script>
        </body>
        </html>
        """;
}
