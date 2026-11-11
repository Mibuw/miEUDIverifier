# Reading a German PID with `miEUDIverifier.Core`

[`GermanPidVerifier/`](GermanPidVerifier) is a small ASP.NET Core application that reads a PID from
the **German EUDI Wallet** (SPRIND sandbox, "d-you") — the shape a real integration takes, with the
settings in the web server's own configuration. It has **no project reference into this
repository** — it pulls the library from NuGet exactly as any other consumer would:

```xml
<PackageReference Include="miEUDIverifier.Core" Version="2.0.1" />
```

It is deliberately **not** part of `miEUDIverifier.sln`, so building the repository never depends on
a published package and the sample never silently picks up unreleased source changes.

## There is a second sample, without a backend

[`GermanPidTessioVerifier/`](GermanPidTessioVerifier) reads the same German PID with **no verifier
backend at all** — no `eudi-srv-verifier-endpoint` container. Same package, different call:
`AddInProcessVerifier()` and `MapInProcessVerifier()` instead of a backend URL. The package hosts
the endpoints the wallet calls and verifies the presentation in process, using
[`Tessio.Verifier`](https://github.com/tripledownab/tessio-verifier) for the protocol work.

Which to use:

| | `GermanPidVerifier` | `GermanPidTessioVerifier` |
|---|---|---|
| NuGet package | `miEUDIverifier.Core` 2.0.1 | `miEUDIverifier.Core` 2.0.1 |
| Verifier backend | the Kotlin reference container | none |
| Access and registration certificate | in the backend's configuration | in the application's configuration |
| Credential format | `mso_mdoc` or `dc+sd-jwt`, either/or | `mso_mdoc` |
| Revocation checked | yes, by the backend | **no** — see that sample's README |
| Real wallet presentation | verified end to end | verified end to end (2026-11-11) |

If you have never done this before, start with
[`GermanPidTessioVerifier/SCHRITT-FUER-SCHRITT.md`](GermanPidTessioVerifier/SCHRITT-FUER-SCHRITT.md):
a German, command-by-command walkthrough of a test run that says after each step what you must see.
Its trust anchors are committed ready to use in
[`GermanPidTessioVerifier/trust-anchors/`](GermanPidTessioVerifier/trust-anchors/).

Both have now completed a presentation from the real German wallet, so the choice is no longer
between a proven and an unproven path. It is between two containers and one, against one gap: the
backend-free path does not check revocation, for a reason that is documented rather than hidden.
Both need the same two certificates, which is the part no library removes.

Since 2026-11-11 this repository's own web app takes the backend-free path for the German
ecosystem, so what the second sample demonstrates is also what runs at
[mieudiverifier.mitterbucher.com](https://mieudiverifier.mitterbucher.com/?backend=de). The
code behind it lives in
[`src/miEUDIverifier.Core/Services/Tessio/`](../src/miEUDIverifier.Core/Services/Tessio/) and the
sample consumes it from NuGet like any other caller — no project reference, which is why neither
sample is part of `miEUDIverifier.sln`.

Both samples therefore show the same package from its two sides. The difference in their
`Program.cs` is one registration call and the five inputs the in-process path needs.

## What you need first

This is the hard part, and no amount of client configuration substitutes for it.

A verifier backend signs every authorization request object with **one** access certificate for
**one** trust ecosystem. The German wallet trusts the German Relying-Party Access CA, so you need:

1. **A backend of your own** — the EUDI reference `eudi-srv-verifier-endpoint` container — holding
2. **an access certificate** issued by the [SPRIND registrar](https://sandbox.eudi-wallet.org/), and
3. **a registration certificate**, which decides which attributes you may ask for.

[`docker/docker-compose.de-backend.yml`](../docker/docker-compose.de-backend.yml) is a commented
template, and [`docs/trust-and-multi-backend.md`](../docs/trust-and-multi-backend.md) walks through
the onboarding.

**Pointing this sample at the public EU reference backend does not work.** The German wallet does
not trust that ecosystem's CA. It fails late, with `DocumentStatusCheckFailed`, which reads like a
certificate fault and is really a wrong ecosystem.

## Run it

```bash
cd samples/GermanPidVerifier
dotnet run
```

Then open <http://localhost:5080/> and press **Start verification**. The page shows a QR code and
polls until the attributes arrive — which is the shape of the real flow: the browser polls *your*
application, and your application polls the verifier backend.

| Endpoint | |
|---|---|
| `POST /verify` | Starts a verification. Answers **immediately** with `{ id, status, clientId, deepLink, qrCode }`. |
| `GET /verify/{id}` | `waiting` while the wallet is out; then the attributes, or `rejected` / `timeout` / `error`. |
| `GET /verify/{id}/qr` | The deep link as a PNG. |

`appsettings.json` expects the backend at `http://localhost:8080`. Everything is overridable by
environment variable or command line:

```bash
EUDI_VerifierSettings__BackendUrl=http://localhost:8080 dotnet run
dotnet run -- --VerifierSettings:IntendedUseId=my-intended-use
```

If your backend runs on a server and is not published to the internet — which is the right default,
see below — reach its REST API through a tunnel:

```bash
ssh -N -L 8080:<backend-container-ip>:8080 user@your-server
```

The tunnel carries only the REST API your application calls. **The wallet never uses it:** it
resolves the URLs the backend put in the signed request object, which are public. A run against a
tunnelled backend therefore produces a QR code the phone can scan straight away, and a completed
verification looks like this:

```json
{
  "id": "b5b1ef21a53f4b07a43c86a8a97e6458",
  "status": "complete",
  "familyName": "MUSTERMANN",
  "givenName": "ERIKA",
  "birthDate": "1964-08-12",
  "placeOfBirth": "BERLIN",
  "nationality": "DE",
  "issuingAuthority": "DE",
  "issuingCountry": "DE",
  "format": "mso_mdoc",
  "credentialType": "eu.europa.ec.eudi.pid.1"
}
```

The request offers `mso_mdoc` and `dc+sd-jwt` as alternatives and the wallet picks one, so which
format comes back is its choice, not yours. Both have been verified against the d-you wallet.

## What the integration actually looks like

Two lines of wiring, and the settings in your web server's own configuration:

```csharp
builder.Configuration.AddEnvironmentVariables("EUDI_");
builder.Services.AddMiEUDIverifier(builder.Configuration);
```

```json
{
  "Kestrel":  { "Endpoints": { "Http": { "Url": "http://localhost:5080" } } },
  "Logging":  { "LogLevel": { "Default": "Warning", "miEUDIverifier": "Information" } },
  "VerifierSettings": {
    "BackendUrl": "http://localhost:8080",
    "IntendedUseId": "pos-pid",
    "ResponseMode": "direct_post.jwt",
    "RequestUriMethod": "get",
    "PollIntervalSeconds": 3,
    "PollTimeoutSeconds": 120
  }
}
```

`miEUDIverifier.Core` is the **only** package reference — configuration, dependency injection and
logging come with the web SDK.

**`POST /verify` must not wait for the wallet.** There is no callback: the backend holds the
response until it is fetched, so the handler starts the transaction, answers with the deep link and
leaves a background task polling. That task outlives the request, so it resolves
`VerifierApiService` from its own scope, and the verification state has to outlive the request too —
a `ConcurrentDictionary` in the sample, your database in reality.

## Three moving parts, and where each one lives

The package is a **client of a verifier backend**, not a verifier in its own right. The
cryptography belongs to the backend.

| | Set where | Set how |
|---|---|---|
| **Endpoint** | in your process | `VerifierSettings:BackendUrl` |
| **Certificates** | in the verifier backend | `VERIFIER_ACCESS_CERTIFICATE_*`, `VERIFIER_INTENDEDUSES_*` |
| **Response URL** | in the verifier backend | `VERIFIER_PUBLICURL` |

### 1. The endpoint

`VerifierSettings:BackendUrl` is the base URL of the backend's **REST API** — the one the sample
calls to start a transaction and poll for the answer.

`AddMiEUDIverifier(configuration)` registers `VerifierApiService` with a typed `HttpClient` whose
`BaseAddress` comes from that setting. A web host already provides logging; outside one, register a
logging provider too — the service takes an `ILogger<VerifierApiService>` and resolution fails
without it.

> Do not publish this API to the internet. Anyone who can reach `/ui/*` can start transactions under
> your verifier identity and read wallet responses, without authenticating. Expose only `/wallet/*`,
> which is what the wallet needs, and keep `/ui/*` inside your network.

### 2. The certificates

**Nothing in your process holds a key** — and nothing in `appsettings.json` is a placeholder
waiting for one. There is no keystore path, no password and no certificate JWT on this side. The
client touches certificates in exactly two indirect places:

| Direction | In the sample | On the wire |
|---|---|---|
| out | `IntendedUseId = "pos-pid"` | `intended_use_id` — a **name**, not a certificate |
| back | `tx.ClientId`, printed | `client_id` — the SHA-256 of the access certificate |

The backend signs every request object, so the access certificate lives there as a PKCS#12 mounted
into the container:

| Backend variable | Purpose |
|---|---|
| `VERIFIER_ACCESS_CERTIFICATE_KEYSTORE` *(+ `_TYPE`, `_PASSWORD`, `_ALIAS`, `_SIGNING_ALGORITHM`)* | The PKCS#12 holding your private key and RP access certificate |
| `VERIFIER_CLIENTIDPREFIX` / `VERIFIER_ORIGINALCLIENTID` | Client-id scheme (`x509_hash` for the German path) and its value |
| `VERIFIER_INTENDEDUSES_<n>_ID` / `_REGISTRATIONCERTIFICATE` | Registration certificates, selected per transaction by id |

Your side of this is one line: `TransactionOptions.IntendedUseId`, which tells the backend *which*
registration certificate to attach.

With the `x509_hash` prefix the `client_id` **is** the base64url SHA-256 of the access certificate,
so a reissued certificate always needs `VERIFIER_ORIGINALCLIENTID` updated too. A stale value fails
with the same `Could not trust certificate chain` message as a genuinely untrusted chain.

### 3. The response URL

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
  application shares the hostname, route `/wallet/*` there and everything else to your app.

**There is no callback.** The backend holds the response until it is fetched, and
`WaitForWalletResponseAsync` polls `GET /ui/presentations/{id}` every
`VerifierSettings:PollIntervalSeconds` until `PollTimeoutSeconds`. In a web application, run it in
the background and let your own frontend poll you.

## Staying inside the registration certificate

Everything the sample puts into `TransactionOptions` exists for one reason: **ask for more than the
registration certificate grants and the wallet aborts before the consent screen.** It posts an error
response a fraction of a second after fetching the request object — no consent screen, no PIN — and
from the verifier side that is indistinguishable from a trust failure.

```csharp
var options = new TransactionOptions
{
    IntendedUseId      = "pos-pid",                                // selects the certificate
    ResponseMode       = "direct_post.jwt",                        // HAIP wallets refuse plaintext
    SdJwtVctValues     = new List<string>(),                       // drop the generic EU option
    GermanPidVctValues = new List<string> { "urn:eudi:pid:de:1" }, // exactly what is covered
    PidClaims          = new List<string>
    {
        "family_name", "given_name", "birth_date", "place_of_birth",
        "nationality", "issuing_authority", "issuing_country",
    },
};
```

Attribute names are given in **mso_mdoc spelling**; the SD-JWT option translates `birth_date` →
`birthdate` and `nationality` → `nationalities` itself. What the certificate must look like on the
other side — one claim entry per attribute, mdoc paths carrying the namespace — is in
[`docs/german-pid-presentation.md`](../docs/german-pid-presentation.md).

> **Do not move these into `appsettings.json`.** The .NET configuration binder *appends* to a list
> that already carries defaults, so a JSON array asks for the defaults as well — restating the three
> default claims requests six, each name twice. `TransactionOptions` replaces. Given what asking for
> too much costs you, this one bites hard.

## Failure paths

`WaitForWalletResponseAsync` returns only once a presentation has actually arrived. Everything else
is an exception, so these are the complete set:

| Outcome | How it surfaces | `status` in the sample |
|---|---|---|
| No answer in time, or the request expired | `TimeoutException` | `timeout` |
| The wallet answered with an OpenID4VP error | `InvalidOperationException` | `error` |
| Cancelled, e.g. the host shutting down | `OperationCanceledException` | `cancelled` |

One more case is not an exception. `ExtractIdentityDataAsync` checks each presentation against the
credential types that were actually requested rather than trusting the backend's verdict, and
returns `CredentialType == null` when nothing matched. That is a **rejection**, not a partial
result — polling again will not change it, and it is also what a request that reached past the
registration certificate looks like from here.

```csharp
if (identity.CredentialType is null)   // "rejected": nothing of the requested type
else if (identity.IsComplete)          // "complete": every requested attribute present
else                                   // "partial": credential accepted, fewer attributes
```
