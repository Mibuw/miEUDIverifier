# Using `miEUDIverifier.Core` from your own application

[`ConsoleVerifier/`](ConsoleVerifier) is a standalone console application that verifies a PID end to
end. It has **no project reference into this repository** — it pulls `miEUDIverifier.Core` from
NuGet exactly as any other consumer would:

```xml
<PackageReference Include="miEUDIverifier.Core" Version="1.4.2" />
```

It is deliberately **not** part of `miEUDIverifier.sln`, so building the repository never depends on
a published package, and the sample never silently picks up unreleased source changes.

```bash
cd samples/ConsoleVerifier
dotnet run

# point it somewhere else, override any setting on the command line
dotnet run -- --VerifierSettings:BackendUrl=http://localhost:8080
dotnet run -- --VerifierSettings:PollTimeoutSeconds=30
```

The run prints the transaction, writes `qr.png`, shows the deep link and then waits for the wallet:

```
Backend : https://verifier-backend.eudiw.dev
Claims  : family_name, given_name, birth_date

Transaction : CGsQFhfsDVrRmT-8xFcr_jE8i7YZiFIrPDNoJdF1fzD7…
client_id   : x509_hash:IwslPFHbKBzkIyXhdqEkIao40jEDjzd_sDNUB-ZhTyE
request_uri : https://verifier-backend.eudiw.dev/wallet/request.jwt/ajetMj__Xiv…  (GET)

Scan this with the wallet:
  QR code  : …/samples/ConsoleVerifier/qr.png
  Deep link: openid4vp://?client_id=…&request_uri=…&request_uri_method=get

Waiting up to 120s (Ctrl+C to stop) …
```

## Three moving parts, and where each one lives

This is the thing to get straight before anything else. The package is a **client of an EUDI
verifier backend**, not a verifier in its own right. The cryptography belongs to the backend.

| | Set where | Set how |
|---|---|---|
| **Endpoint** | in your process | `VerifierSettings:BackendUrl` |
| **Certificates** | in the verifier backend | `VERIFIER_ACCESS_CERTIFICATE_*` environment variables |
| **Response URL** | in the verifier backend | `VERIFIER_PUBLICURL` |

## 1. The endpoint

`VerifierSettings:BackendUrl` is the base URL of the backend's **REST API**:

```json
{
  "VerifierSettings": {
    "BackendUrl": "https://verifier-backend.eudiw.dev"
  }
}
```

A common mistake is pointing it at `https://verifier.eudiw.dev` — that host serves the Angular demo
UI and answers the API paths with `405`. For a backend of your own,
[`docker/docker-compose.de-backend.yml`](../docker/docker-compose.de-backend.yml) is a commented
template; locally that is usually `http://localhost:8080`.

`AddMiEUDIverifier(configuration)` registers `VerifierApiService` with a typed `HttpClient` whose
`BaseAddress` comes from that setting. Register a logging provider as well — the service takes an
`ILogger<VerifierApiService>` and resolution fails without one.

**Several ecosystems at once.** One backend signs with one access certificate for one trust
ecosystem, so supporting both the EU reference wallet and the German one means two backends. The DI
extension wires a single `BackendUrl`; for more, build the clients yourself and keep one service per
backend:

```csharp
foreach (var (key, url) in backendUrls)
    services.AddHttpClient($"verifier-{key}", c =>
    {
        c.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        c.DefaultRequestHeaders.Add("Accept", "application/json");
        c.Timeout = TimeSpan.FromSeconds(30);
    });

var verifiers = backendUrls.Keys.ToDictionary(
    key => key,
    key => new VerifierApiService(
        httpFactory.CreateClient($"verifier-{key}"),
        provider.GetRequiredService<IOptions<VerifierSettings>>(),
        provider.GetRequiredService<ILogger<VerifierApiService>>()),
    StringComparer.OrdinalIgnoreCase);
```

## 2. The certificates

**Nothing in your process holds a key.** The backend signs every authorization request object, so
the access certificate lives there, as a PKCS#12 mounted into the container:

| Backend variable | Purpose |
|---|---|
| `VERIFIER_ACCESS_CERTIFICATE_KEYSTORE` *(+ `_TYPE`, `_PASSWORD`, `_ALIAS`, `_SIGNING_ALGORITHM`)* | The PKCS#12 holding your private key and RP access certificate |
| `VERIFIER_CLIENTIDPREFIX` / `VERIFIER_ORIGINALCLIENTID` | Client-id scheme (`x509_san_dns`, `x509_hash`, …) and its value |
| `VERIFIER_INTENDEDUSES_<n>_ID` / `_REGISTRATIONCERTIFICATE` | Registration certificates, selected per transaction by id |

Two traps worth knowing in advance:

- With the `x509_hash` prefix the `client_id` **is** the base64url SHA-256 of the certificate, so a
  reissued certificate always needs `VERIFIER_ORIGINALCLIENTID` updated too. A stale value fails
  with the same `Could not trust certificate chain` message as a genuinely untrusted chain.
- The registration certificate decides which **attributes** you may ask for, and it grants only what
  it spells out one claim entry at a time. Asking for more makes the wallet abort before the consent
  screen. See [`docs/german-pid-presentation.md`](../docs/german-pid-presentation.md).

Your side of this is only `TransactionOptions.IntendedUseId`, which tells the backend *which*
registration certificate to attach.

## 3. The response URL

The wallet never talks to your process. It fetches the request object and posts its response to the
**backend**, at URLs the backend derives from a single setting:

```yaml
VERIFIER_PUBLICURL: "https://verifier.example.com"
```

From it come both `request_uri` (`…/wallet/request.jwt/<id>`) and the `response_uri` inside the
signed request object (`…/wallet/direct_post/<id>`). Three consequences:

- It must be **reachable by the phone**, so `localhost` only works for an emulator on the same host.
- It must match the access certificate's SAN, or the wallet rejects the request.
- If a reverse proxy terminates TLS, it has to route `/wallet/*` to the backend. When your own
  application shares the hostname, route `/wallet/*` to the backend and everything else to your app.

**There is no callback.** The backend holds the response until it is fetched, and
`WaitForWalletResponseAsync` polls `GET /ui/presentations/{id}` every
`VerifierSettings:PollIntervalSeconds` until `PollTimeoutSeconds`. In a web application, run it in
the background and let your own frontend poll you.

## Choosing the attributes

`TransactionOptions` shapes the DCQL query per transaction; leave a property `null` and the
configured default applies. Attribute names are given in **mso_mdoc spelling** — the SD-JWT options
translate `birth_date` → `birthdate` and `nationality` → `nationalities` themselves.

```csharp
var options = new TransactionOptions
{
    IntendedUseId = "TEST-01",
    ResponseMode  = "direct_post.jwt",   // high-assurance wallets require an encrypted response
    SdJwtOnly     = true,                // stay inside a dc+sd-jwt registration certificate
    PidClaims     = new List<string> { "family_name", "given_name", "birth_date" },
};
```

> **Do not put `PidClaims` in `appsettings.json`.** The .NET configuration binder *appends* to a
> list that already carries defaults, so restating the three default claims requests six, with each
> name duplicated. Override the attribute set through `TransactionOptions` instead. Since asking for
> more than the registration certificate covers makes the wallet abort, this one bites hard.

## Failure paths

`WaitForWalletResponseAsync` returns only once a presentation has actually arrived. Everything else
is an exception, so these are the complete set:

| Outcome | How it surfaces |
|---|---|
| No answer in time, or the request expired | `TimeoutException` |
| The wallet answered with an OpenID4VP error | `InvalidOperationException` |
| Cancelled | `OperationCanceledException` |

One more case is not an exception. `ExtractIdentityDataAsync` checks each presentation against the
credential types that were actually requested rather than trusting the backend's verdict, and
returns `CredentialType == null` when nothing in the response matched. That is a **rejection**, not
a partial result — polling again will not change it.

```csharp
if (identity.CredentialType is null)   // rejected: nothing of the requested type
else if (identity.IsComplete)          // every requested attribute present
else                                   // partial: credential accepted, fewer attributes
```
