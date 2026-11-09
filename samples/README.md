# Reading a German PID with `miEUDIverifier.Core`

[`GermanPidVerifier/`](GermanPidVerifier) is a standalone console application that reads a PID from
the **German EUDI Wallet** (SPRIND sandbox, "d-you"). It has **no project reference into this
repository** — it pulls the library from NuGet exactly as any other consumer would:

```xml
<PackageReference Include="miEUDIverifier.Core" Version="1.4.2" />
```

It is deliberately **not** part of `miEUDIverifier.sln`, so building the repository never depends on
a published package and the sample never silently picks up unreleased source changes.

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

`appsettings.json` expects the backend at `http://localhost:8080`. Override anything on the command
line:

```bash
dotnet run -- --VerifierSettings:BackendUrl=http://localhost:8080
dotnet run -- --VerifierSettings:IntendedUseId=my-intended-use
dotnet run -- --VerifierSettings:PollTimeoutSeconds=30
```

If your backend runs on a server and is not published to the internet — which is the right default,
see below — reach its REST API through a tunnel:

```bash
ssh -N -L 8080:<backend-container-ip>:8080 user@your-server
```

The tunnel carries only the REST API you call. **The wallet never uses it:** it resolves the URLs
the backend put in the signed request object, which are public. A run against a tunnelled backend
therefore produces a QR code the phone can scan straight away:

```
Backend        : http://localhost:8080
Intended use   : pos-pid
Claims         : family_name, given_name, birth_date, place_of_birth, nationality, issuing_authority, issuing_country

Transaction : JQ7Sz0ZpTS3Afb4vL60rXtjcBwLxRw716THWadbIgMtCcBST8eyhvLQPiPaid…
client_id   : x509_hash:EwIYRjj23v-4q6yRqcz3QH1HuumzFXKhZsDSo7OJN0g
request_uri : https://verifier.example.com/wallet/request.jwt/oYJb0_5iKQqo8F1…  (GET)

Scan this with the German EUDI Wallet:
  QR code  : …/samples/GermanPidVerifier/qr.png
  Deep link: openid4vp://?client_id=…&request_uri=…&request_uri_method=get

Waiting up to 120s (Ctrl+C to stop) …

Complete presentation:
  Family name       : MUSTERMANN
  Given name        : ERIKA
  Date of birth     : 1964-08-12
  Place of birth    : BERLIN
  Nationality       : DE
  Issuing authority : DE
  Issuing country   : DE
  Format / type     : mso_mdoc / eu.europa.ec.eudi.pid.1
```

The request offers `mso_mdoc` and `dc+sd-jwt` as alternatives and the wallet picks one, so which
format comes back is its choice, not yours. Both have been verified against the d-you wallet.

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
`BaseAddress` comes from that setting. Register a logging provider as well; the service takes an
`ILogger<VerifierApiService>` and resolution fails without one.

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

| Outcome | How it surfaces |
|---|---|
| No answer in time, or the request expired | `TimeoutException` |
| The wallet answered with an OpenID4VP error | `InvalidOperationException` |
| Cancelled | `OperationCanceledException` |

One more case is not an exception. `ExtractIdentityDataAsync` checks each presentation against the
credential types that were actually requested rather than trusting the backend's verdict, and
returns `CredentialType == null` when nothing matched. That is a **rejection**, not a partial
result — polling again will not change it, and it is also what a request that reached past the
registration certificate looks like from here.

```csharp
if (identity.CredentialType is null)   // rejected: nothing of the requested type
else if (identity.IsComplete)          // every requested attribute present
else                                   // partial: credential accepted, fewer attributes
```
