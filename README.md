# miEUDIverifier

[![NuGet](https://img.shields.io/nuget/v/miEUDIverifier.Core.svg)](https://www.nuget.org/packages/miEUDIverifier.Core/) [![NuGet downloads](https://img.shields.io/nuget/dt/miEUDIverifier.Core.svg)](https://www.nuget.org/packages/miEUDIverifier.Core/) [![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE) [![Docker on GHCR](https://img.shields.io/badge/GHCR-mibuw%2Fmieudiverifier-2496ED?logo=docker&logoColor=white)](https://github.com/Mibuw/miEUDIverifier/pkgs/container/mieudiverifier)

**Reusable core library on NuGet:** `dotnet add package miEUDIverifier.Core`

A C#/.NET 8 **ASP.NET Core web app** (Minimal API with a browser UI) that talks to the
**EUDI Verifier Backend REST API** (OpenID4VP) to read identity data from an EUDI Wallet.

This verifier reads **only the PID credential** (Personal Identification Data) and only the
following three attributes:

| Field | DCQL path (mso_mdoc) | DCQL path (sd-jwt) |
|-------|----------------------|--------------------|
| Family name | `eu.europa.ec.eudi.pid.1 / family_name` | `family_name` |
| Given name | `eu.europa.ec.eudi.pid.1 / given_name` | `given_name` |
| Date of birth | `eu.europa.ec.eudi.pid.1 / birth_date` | `birth_date` |

> **Note:** The application can of course be extended to request **more attributes** (or other
> credential types). The requested claims are defined in the DCQL query built in
> [`VerifierApiService`](src/miEUDIverifier.Core/Services/VerifierApiService.cs) – just add the
> desired paths there and map the returned values in `ExtractIdentityDataAsync`.

## Flow (Cross-Device)

```
┌─────────────┐     POST /ui/presentations      ┌──────────────────────┐
│  Web app    │ ──────────────────────────────► │  EUDI Verifier       │
│  (Verifier) │ ◄────────────────────────────── │  Backend             │
│             │  transaction_id + request_uri   │  (verifier.eudiw.dev │
│  Show QR    │                                 │   or local)          │
│  code in    │                                 └──────────────────────┘
│  browser    │                                          ▲
│             │                                          │ VP Token
└─────────────┘                                          │
      ▲                                       ┌──────────┴───────┐
      │ GET /ui/presentations/{id}            │  EUDI Wallet App │
      │ (auto-polling)                        │  (iOS / Android) │
      │                                       │  scans QR code   │
      └───────────────────────────────────────┴──────────────────┘
```

The app starts a local web server, automatically opens the browser and shows the QR code as an
image. After the scan, the page polls the wallet's response server-side and displays the identity
data – no manual reload needed.

## Try it (test endpoint)

A public test instance is available at **http://miEUDIverifier.mitterbucher.com:5050** —
just open it and click **New request** to get a fresh QR code, then scan it with your EUDI Wallet App.

> _No guarantee of availability_ — this endpoint may be offline at any time. To run your own
> instance, see the [Quick start](#quick-start) below.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- Internet access to `https://verifier.eudiw.dev` **or** Docker (for local operation)
- EUDI Wallet App (Android / iOS) holding an issued PID credential – see the next section

## Set up the EUDI Wallet App & a test PID

For the verifier to read anything, the wallet app must be installed and must hold a
**PID credential** (Personal Identification Data). In the public demo environment this works
entirely with test data – no real ID document required.

### 1. Install the wallet app

- **Android:** Pre-built APKs are available as release assets:
  → [eudi-app-android-wallet-ui / releases](https://github.com/eu-digital-identity-wallet/eudi-app-android-wallet-ui/releases)
  Download the APK of the latest `*-Demo` version and sideload it
  ("Install from unknown sources" must be allowed). When switching versions a clean reinstall may
  be required (uninstall the previous version first).
- **iOS:** Source code and build instructions are at
  [eudi-app-ios-wallet-ui](https://github.com/eu-digital-identity-wallet/eudi-app-ios-wallet-ui)
  (no public App Store build – build it yourself or use a provided TestFlight invitation).

### 2. Load a test PID into the wallet

The public test environment [issuer.eudiw.dev](https://issuer.eudiw.dev/) issues PID credentials
with test data:

1. Open the **Credential Offer** page in your browser:
   [issuer.eudiw.dev/credential_offer](https://issuer.eudiw.dev/credential_offer)
2. Select **PID** in the **MSO Mdoc** format and generate the offer
   → a **QR code / barcode** (or a deep link) appears.
3. In the EUDI Wallet App go to **Documents** → **Add document / scan QR** and scan the barcode.
4. Follow the test environment's **authentication and confirmation steps** and accept the offer
   with **Add**.

Afterwards the PID credential is stored in the wallet and can be read by this verifier.

> **Note:** This verifier requests the PID in **both** formats — **mso_mdoc** and **SD-JWT VC**
> (`dc+sd-jwt`) — as alternative options, so the wallet can present whichever it holds. On the test
> issuer you can therefore load **PID (MSO Mdoc)** or **PID (SD-JWT VC)**; both work.

## Quick start

```bash
# 1. Clone the repository / unpack the project
cd miEUDIverifier

# 2. Restore dependencies and run
dotnet run --project src/miEUDIverifier
#    → The browser opens automatically (http://localhost:5050)

# 3. Scan the QR code in the browser with the EUDI Wallet App
#    → The app asks for consent to share the data
#    → After confirmation the web page shows name and date of birth
```

## Configuration

All settings live in `src/miEUDIverifier/appsettings.json`:

| Key | Default | Description |
|-----|---------|-------------|
| `BackendUrl` | `https://verifier.eudiw.dev` | URL of the verifier backend |
| `PollIntervalSeconds` | `3` | Polling interval (seconds) |
| `PollTimeoutSeconds` | `120` | Maximum wait time |
| `Profile` | `openid4vp` | OpenID4VP profile (`openid4vp` or `haip`) |
| `AuthorizationRequestScheme` | `openid4vp` | URI scheme for the QR code |
| `IssuerChain` | EUDI demo CA | PEM certificate of the trusted PID issuer |

Override via environment variable (prefix `EUDI_`):
```bash
EUDI_VerifierSettings__BackendUrl=http://localhost:8080 dotnet run --project src/miEUDIverifier
```

Or via CLI argument:
```bash
dotnet run --project src/miEUDIverifier -- --VerifierSettings:BackendUrl=http://localhost:8080
```

## Hostname, IP address & HTTPS/SSL

The address and port the built-in web server (Kestrel) listens on are controlled via the
`Kestrel:Endpoints` section in `src/miEUDIverifier/appsettings.json`. By default only **HTTP** is
active:

```jsonc
{
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5050"
      }
    }
  }
}
```

### Hostname / IP address / port

The `Url` value determines the binding and port. On startup the detected local and LAN addresses
are printed to the console.

| Example `Url` | Meaning |
|---------------|---------|
| `http://0.0.0.0:5050` | All network interfaces (also reachable on the LAN) – default |
| `http://localhost:5050` | Local machine only |
| `http://192.168.1.42:5050` | Bind to a specific **IP address** |
| `http://my-hostname:5050` | Bind to a specific **hostname** |

### Enable HTTPS / SSL

Add an `Https` endpoint with a certificate. Kestrel supports two certificate formats:

**A) PFX/PKCS#12 (certificate + key in a single file):**
```jsonc
"Kestrel": {
  "Endpoints": {
    "Http":  { "Url": "http://0.0.0.0:5050" },
    "Https": {
      "Url": "https://0.0.0.0:5443",
      "Certificate": {
        "Path": "certs/my-certificate.pfx",
        "Password": "<do-not-put-it-here-see-below>"
      }
    }
  }
}
```

**B) PEM (separate certificate and key files):**
```jsonc
"Https": {
  "Url": "https://0.0.0.0:5443",
  "Certificate": {
    "Path": "certs/fullchain.pem",
    "KeyPath": "certs/privkey.pem"
  }
}
```

If an HTTPS endpoint is configured, the app automatically opens the local HTTPS address
(`https://localhost:<port>`) on startup.

### Do NOT store the certificate password in clear text

The password does **not** belong in the committed `appsettings.json`. Instead:

```bash
# Via environment variable (prefix EUDI_, __ separates the levels):
EUDI_Kestrel__Endpoints__Https__Certificate__Password=secret dotnet run --project src/miEUDIverifier

# Or via user secrets (local only, never written to the repo):
dotnet user-secrets --project src/miEUDIverifier set "Kestrel:Endpoints:Https:Certificate:Password" "secret"
```

> **Note:** `*.pfx`, `*.pem`, `*.key`, `*.crt` as well as the `Cert/` folder are already excluded
> in `.gitignore`/`.dockerignore` – so certificates won't accidentally end up in the repository or
> Docker image.

### Create a development certificate (optional)

For local HTTPS without your own certificate:
```bash
dotnet dev-certs https -ep certs/dev.pfx -p devpass
dotnet dev-certs https --trust          # mark the certificate as trusted in the OS
```

### Docker

Mount the certificate into the container as a volume, publish the port and pass the password as an
environment variable – never copy it into the image:
```bash
docker run --rm \
  -p 5443:5443 \
  -v "$PWD/certs:/app/certs:ro" \
  -e EUDI_Kestrel__Endpoints__Https__Url="https://0.0.0.0:5443" \
  -e EUDI_Kestrel__Endpoints__Https__Certificate__Path="certs/my-certificate.pfx" \
  -e EUDI_Kestrel__Endpoints__Https__Certificate__Password="secret" \
  mieudiverifier:latest
```

## Local verifier backend (Docker)

For development without internet access or for full control:

```bash
cd docker
docker compose up -d

# Then start the app pointing at the local URL:
EUDI_VerifierSettings__BackendUrl=http://localhost:8080 dotnet run --project src/miEUDIverifier
```

> **Note:** With a local backend the wallet app must be able to reach the backend.
> Replace `localhost` with your machine's LAN IP and set `VERIFIER_PUBLICURL` in
> `docker/docker-compose.yml` accordingly.

## Project structure

```
miEUDIverifier/
├── miEUDIverifier.sln
├── README.md
├── Dockerfile                              # Container build of the web app
├── docker/
│   └── docker-compose.yml                  # Local EUDI verifier backend stack + app
└── src/
    ├── miEUDIverifier/                      # ASP.NET Core web app (Minimal API)
    │   ├── miEUDIverifier.csproj
    │   ├── appsettings.json                # Config: backend, Kestrel/HTTPS, logging
    │   ├── Program.cs                       # Entry point, endpoints & flow control
    │   └── WebServer/
    │       ├── AppState.cs                  # Runtime state (transaction, polling)
    │       └── HtmlPage.cs                  # Single-page browser UI (HTML)
    ├── miEUDIverifier.Core/                 # Reusable library
    │   ├── miEUDIverifier.Core.csproj
    │   ├── MiEUDIverifierExtensions.cs      # AddMiEUDIverifier() DI extension
    │   ├── Configuration/
    │   │   └── VerifierSettings.cs          # Configuration model
    │   ├── Models/
    │   │   ├── InitTransactionRequest.cs    # DCQL request
    │   │   ├── InitTransactionResponse.cs   # Response with transaction_id
    │   │   └── WalletResponseModels.cs      # VP response & IdentityData
    │   └── Services/
    │       ├── VerifierApiService.cs        # REST API client (core logic)
    │       └── QrCodeService.cs             # QR code output (PNG)
    └── miEUDIverifier.Core.Tests/           # xUnit tests (offline)
```

## Standards & protocols used

| Standard | Description |
|----------|-------------|
| [OpenID4VP 1.0](https://openid.net/specs/openid-4-verifiable-presentations-1_0-final.html) | Presentation protocol |
| [DCQL](https://openid.net/specs/openid-4-verifiable-presentations-1_0.html#name-digital-credentials-query-l) | Digital Credentials Query Language |
| [ISO/IEC 18013-5](https://www.iso.org/standard/69084.html) | mDL / mDoc data format |
| [SD-JWT VC](https://datatracker.ietf.org/doc/draft-ietf-oauth-sd-jwt-vc/) | Selective Disclosure JWT Verifiable Credentials |
| [EUDI ARF](https://github.com/eu-digital-identity-wallet/eudi-doc-architecture-and-reference-framework) | EU Digital Identity Architecture |

## Referenced EUDI repositories

- [eudi-srv-verifier-endpoint](https://github.com/eu-digital-identity-wallet/eudi-srv-verifier-endpoint) – Verifier backend (Kotlin/Spring Boot)
- [eudi-web-verifier](https://github.com/eu-digital-identity-wallet/eudi-web-verifier) – Verifier frontend
- [eudi-app-android-wallet-ui](https://github.com/eu-digital-identity-wallet/eudi-app-android-wallet-ui) – Android wallet app
- [eudi-app-ios-wallet-ui](https://github.com/eu-digital-identity-wallet/eudi-app-ios-wallet-ui) – iOS wallet app
- [eudi-srv-pid-issuer](https://github.com/eu-digital-identity-wallet/eudi-srv-pid-issuer) – PID issuer

## License

Licensed under the **Apache License, Version 2.0** – in line with the EUDI Reference
Implementation. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

Copyright 2026 Wolfgang Mitterbucher.

All third-party dependencies are permissive (MIT / Apache 2.0) and compatible with this license.

---

## Author

**Wolfgang Mitterbucher** — Software Engineering & Digital Identity, Leonding (Austria)

🌐 [www.mitterbucher.com](https://www.mitterbucher.com) · 💼 [LinkedIn](https://at.linkedin.com/in/wolfgangmitterbucher) · ✉️ office@mitterbucher.com

**More open-source projects:** [miPDFconvert](https://github.com/Mibuw/miPDFconvert) · [miPDFvalidator](https://github.com/Mibuw/miPDFvalidator) · [miEUDIverifier](https://github.com/Mibuw/miEUDIverifier)
