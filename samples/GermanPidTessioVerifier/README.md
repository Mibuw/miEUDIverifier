# German PID verifier without a backend

A verifier for the German EUDI Wallet that needs **no `eudi-srv-verifier-endpoint` container**.
One NuGet package hosts the request and response endpoints and verifies the presentation in
process.

Compare `../GermanPidVerifier`, which does the same job through `miEUDIverifier.Core` and the
Kotlin reference backend. That one is the tested production path. This one removes the backend.

## What it is made of

```
Tessio.Verifier.AspNetCore 0.30.2     the hosted endpoints and the verification pipeline
RegistrationCertificateRequestBuilder what this sample adds: the signed request object
```

`AddTessioVerifier` registers its services with `TryAdd`, so anything registered **before** it
wins. This sample replaces two defaults:

| Default | Replaced by | Why |
|---|---|---|
| `DemoPresentationRequestBuilder` | `RegistrationCertificateRequestBuilder` | 0.30.2 cannot put a registration certificate in `verifier_info`, and the German wallet refuses a request without one |
| `DevDefaultTrustListResolver` | `StaticTrustListResolver` over your anchor folder | the default trusts the library's own mock issuers and nothing else |

`MdocVerifier` is also re-registered, only to turn `CheckStatus` on so revocation is actually
checked.

### Why a request builder of our own

The registration certificate travels in the `verifier_info` claim, which is **inside** the
signature, so it cannot be added after the fact. Release 0.30.2 has no option for it. That is
[issue 185](https://github.com/tripledownab/tessio-verifier/issues/185) upstream, with
[pull request 187](https://github.com/tripledownab/tessio-verifier/pull/187) pending. **When that
lands, delete `RegistrationCertificateRequestBuilder.cs`** and register Tessio's own
`SignedPresentationRequestBuilder` with `VerifierInfoJson` instead.

The claim set it produces was compared against a request object the reference implementation
emitted for the same presentation. They match claim for claim; this sample additionally sets
`exp`.

## The three things you must supply

Nothing is checked in. All of it goes in configuration, environment variables, or your host's
secret store -- never in the repository.

```
GermanPid__AccessCertificatePath       PKCS#12 with the RP access certificate AND its private key
GermanPid__AccessCertificatePassword   its password
GermanPid__RegistrationCertificatePath file holding the registration certificate JWT
                                       (or GermanPid__RegistrationCertificate for the value itself)
GermanPid__IssuerTrustAnchorDirectory  folder of .pem/.crt/.cer/.der CA certificates of the PID issuer
GermanPid__PublicBaseUrl               the verifier's public base URL, e.g. https://verifier.example
```

1. **Access certificate.** Its SHA-256 over the DER *is* the `client_id`
   (`x509_hash:<base64url>`); the sample logs the value it computed at startup. The key must be
   EC P-256, because the request is signed ES256.
2. **Registration certificate.** A compact JWT whose `typ` header is `rc-wrp+jwt`, sent verbatim
   -- the wallet checks a signature over those exact bytes. It also fixes **what you may ask
   for**: request one attribute beyond its scope and the wallet drops the request with no consent
   screen, roughly a third of a second after it fetched the request object. That failure looks
   like a trust problem and is not one.
3. **Issuer trust anchors.** The CA certificates of the PID issuer, D-Trust for the German test
   ecosystem. Without them every presentation verifies cryptographically and is still refused as
   untrusted, so the sample refuses to start on an empty folder rather than fail later.

`PublicBaseUrl` must be the name the **wallet** can reach, not localhost: the wallet fetches
`request_uri` and posts the response over the internet. Behind a reverse proxy, that is the
outside name.

## Running it

```bash
dotnet run --project samples/GermanPidTessioVerifier
```

Then open the root path. The hosted pages render the QR code and poll the session themselves.

| Endpoint | Purpose |
|---|---|
| `GET /verify/start` | creates a session, renders the QR page |
| `GET /verify/request/{id}` | serves the signed JAR to the wallet |
| `POST /verify/callback` | receives the encrypted response (`direct_post.jwt`) |
| `GET /verify/{sessionId}` | session status as JSON |
| `GET /verify/{sessionId}/stream` | the same as server-sent events |

## What was verified, and what was not

Checked locally against a throwaway key and a real registration certificate:

- the request object matches the reference implementation's claim for claim, plus `exp`
- the registration certificate arrives byte-identical, `typ` `rc-wrp+jwt`, `format`
  `registration_cert`
- the hand-built ES256 signature verifies against the certificate
- the JAR is served as `application/oauth-authz-req+jwt`
- the DCQL asks for the seven PID attributes under doctype `eu.europa.ec.eudi.pid.1`

**Not yet verified: an end-to-end presentation from the real wallet through this sample.** That
needs the production access certificate, a publicly reachable host, and the issuer's trust
anchors. Until that scan has run, `../GermanPidVerifier` with the reference backend is the path
with a successful run behind it.

One thing this sample does better by accident: Tessio serves `request_uri` repeatedly until it
expires, while the reference backend's endpoint is single-use. A wallet that resolves
`request_uri` twice -- which one app release actually did -- breaks against the backend and works
here.

## Requesting different attributes

`options.RequestedClaims` in `Program.cs`. Keep it inside what the registration certificate
grants, and remember the certificate needs one claim entry per attribute, mdoc entries carrying
the namespace as their first path segment. See `../../docs/german-pid-presentation.md`.
