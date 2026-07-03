# miEUDIverifier.Core

Reusable **.NET 8** library to read **EUDI Wallet PID identity data** (family name, given name,
date of birth) via the **EUDI Verifier Backend REST API** (OpenID4VP). It builds the DCQL request,
polls for the wallet response and extracts the identity data — for **both** `mso_mdoc`
(ISO/IEC 18013-5) and **SD-JWT VC** (`dc+sd-jwt`) credential formats, including SD-JWT
disclosure parsing.

Part of the [miEUDIverifier](https://github.com/Mibuw/miEUDIverifier) project (Apache-2.0),
which also ships a ready-to-run web app with a browser UI, REST API and Docker image.

## Install

```bash
dotnet add package miEUDIverifier.Core
```

## Usage

```csharp
// Program.cs — register via DI (reads the "VerifierSettings" configuration section)
builder.Services.AddMiEUDIverifier(builder.Configuration);

// Resolve the service and run a verification
var verifier = app.Services.GetRequiredService<VerifierApiService>();

var tx       = await verifier.InitializeTransactionAsync();
var deepLink = tx.BuildWalletDeepLink();          // openid4vp:// deep link for the wallet
var qrPng    = QrCodeService.GeneratePng(deepLink); // QR code as PNG bytes

var envelope = await verifier.WaitForWalletResponseAsync(tx.TransactionId);
var identity = await verifier.ExtractIdentityDataAsync(envelope);
// identity.FamilyName, identity.GivenName, identity.BirthDate, identity.CredentialFormat
```

## Configuration (`VerifierSettings` section)

```json
{
  "VerifierSettings": {
    "BackendUrl": "https://verifier-backend.eudiw.dev"
  }
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `BackendUrl` | `https://verifier.eudiw.dev` | Base URL of the EUDI verifier backend |
| `PollIntervalSeconds` | `3` | Polling interval for the wallet response |
| `PollTimeoutSeconds` | `120` | Maximum wait time for the wallet response |
| `Profile` | `openid4vp` | OpenID4VP profile (`openid4vp` or `haip`) |
| `JarMode` | `by_reference` | How the authorization request JWT is passed |
| `RequestUriMethod` | `post` | HTTP method for `request_uri` |
| `ResponseMode` | `direct_post` | Wallet response mode |
| `AuthorizationRequestScheme` | `openid4vp` | URI scheme for the QR-code deep link |
| `IssuerChain` | *(none)* | PEM certificate chain of the trusted PID issuer |
| `SdJwtFormat` | `dc+sd-jwt` | DCQL format id for the SD-JWT VC option |
| `SdJwtVctValues` | `urn:eudi:pid:1`, `urn:eu.europa.ec.eudi:pid:1` | Accepted `vct` values for the SD-JWT VC PID |
| `SessionTtlMinutes` | `30` | Time-to-live for verification sessions in hosting apps that manage parallel sessions (e.g. the miEUDIverifier web app / REST API) |

All values can be overridden via environment variables (e.g.
`VerifierSettings__BackendUrl=…`) or CLI arguments, following standard .NET configuration
binding.

## What it requests

Only the **PID credential** with three attributes — `family_name`, `given_name`, `birth_date` —
offered to the wallet as *either* `mso_mdoc` *or* `dc+sd-jwt` (DCQL `credential_sets` options).
The DCQL query is built in `VerifierApiService.InitializeTransactionAsync` and can easily be
extended with more attributes or credential types.

## License

Apache-2.0 — Copyright © Wolfgang Mitterbucher.
