# Trust model, wallet interoperability & multi-backend architecture

This document captures **why** the verifier works with some wallets but not others, what it takes to
support the **German EUDI Wallet (EUDIWalletDE)**, and **how** the app is structured to serve
multiple trust ecosystems. It is the reference for the design decisions behind
`VerifierSettings.Backends` and [`docker/docker-compose.de-backend.yml`](../docker/docker-compose.de-backend.yml).

> TL;DR — A verifier is only accepted by a wallet if the verifier's signing certificate chains to a
> Certificate Authority that the wallet **already trusts** (via its trusted list). One signing
> certificate serves **one** ecosystem. The EUDI reference wallet trusts the `eudiw.dev` reference
> CA; EUDIWalletDE trusts the German Relying-Party Access CA (obtained via the SPRIND sandbox).
> Therefore multiple wallets require multiple verifier identities — which the app supports through
> named backends.

> **Update, 2026-11-11.** This document's analysis of the trust model is unchanged and still the
> reference for it. One of its conclusions is not: it says throughout that each ecosystem needs its
> own `eudi-srv-verifier-endpoint` **instance**. What each ecosystem actually needs is its own
> **signing certificate** — and where you hold that certificate yourself, no backend is involved at
> all. The German path has run that way since 2026-11-11, verifying in process. The backend
> instance remains necessary for an ecosystem whose verifier identity you do not hold, which is why
> the EU path still uses one. Read "backend instance" below as "verifier identity", and see the
> [README](../README.md#why-one-path-needs-no-backend-and-the-other-does) for which is which.

Trust is only the first gate. Once a wallet accepts the verifier, a second gate decides which
**attributes** it will actually release — that is the registration certificate, covered in
[`german-pid-presentation.md`](german-pid-presentation.md).

## 0. Status — both ecosystems verified working (2026-07-28)

Since **28 July 2026** the public demo completes a presentation with **both** wallets: the EUDI
reference wallet against the `eu` backend, and the German EUDI Wallet (SPRIND sandbox) against our
own `de` backend. Getting there took three fixes that are worth recording, because none of them is
obvious from the error message the wallet shows.

**1. The registrar's AIA endpoint was broken (SPRIND-side).** Our access certificate pointed its
`authorityInfoAccess` CA-Issuers URL at `…/api/status-management/ca.der`, which returned **404**. The
wallet could fetch the CRL (that URL worked) but never the issuing CA, so it could not build the
chain and aborted with `Could not trust certificate chain`. SPRIND separated the endpoints on
27 July 2026: the CA now lives at `https://sandbox.eudi-wallet.org/api/ca.der`, the CRL stays at
`…/api/status-management/crl`. **AIA and CRL distribution points are signed extensions**, so an
already-issued certificate cannot be repaired — the certificate had to be reissued. The registrar
CA also changed its subject DN from `C=DE, O=German Registrar, CN=German Registrar` to
`C=DE, CN=German Registrar` while keeping the same key pair, which alone breaks strict RFC 5280 path
building (`openssl verify` → error 20) even though the signature still matches.

**2. Do not trust the Subject Key Identifier to tell certificates apart.** The SKI is derived from
the public key. We reused our existing key for the reissued certificate, so old and new certificate
carry an **identical** SKI — the wallet even logs `Subject Key Identifier equal to public key SHA1`.
Compare the **serial number** or the base64url SHA-256 of the DER instead. Related trap: with the
`x509_hash` client-id prefix the `client_id` *is* that hash, so a reissued certificate always needs
`VERIFIER_ORIGINALCLIENTID` updated too. A new keystore with a stale hash fails with the same
`Could not trust certificate chain` message as a genuinely untrusted chain.

**3. HAIP wallets require an encrypted response.** A wallet running the HAIP profile rejects an
unencrypted `direct_post` request with *"HAIP profile requires an encrypted response mode
(direct_post.jwt or dc_api.jwt)"*. That check runs **inside the wallet, before the consent screen**,
so nothing appears in the verifier logs. Both backends now use `direct_post.jwt`
(`VerifierSettings.ResponseModes`); the backend then advertises an ephemeral ECDH-ES P-256 key in
`client_metadata` and decrypts the JWE response.

**Verifying a deployment actually landed.** Reading the wallet log is not enough — check what is
served. Start a transaction, fetch the request object, and confirm that
`SHA-256(x5c leaf) == client_id` suffix:

```bash
BASE=https://your-verifier.example
RU=$(curl -s -X POST "$BASE/api/verification?backend=de" \
     | python -c "import sys,json,urllib.parse as u; d=json.load(sys.stdin); \
       print(u.parse_qs(u.urlparse(d['deepLink']).query)['request_uri'][0])")
# request_uri_method=get (the default since 2026-11-04); use -X POST for "post"
curl -s "$RU"
```

A separate, unrelated failure mode seen the same day: the EUDI reference backend rejected a
presentation with `X5CNotTrusted (Issuer X5C not trusted)`. That was **not** a verifier problem — the
PID in the wallet had been signed by a Document Signer certificate that had expired meanwhile
(`PID DS - 01`, valid until 4 July 2026), while the credential's own MSO still looked valid. The fix
is to re-issue the PID in the wallet.

### Worked example — the certificate behind the public demo

This is the real RP Access Certificate of the public test instance, so the values above can be
reproduced with nothing but `openssl`. It is a **sandbox** certificate and public by nature: the
verifier sends it to every wallet in the request object's `x5c` header. The matching
`private_key.pem`, `keystore.p12` and `keystore.pass` are **not** in this repository and must never
be — with the private key anyone could impersonate this verifier. `cert/` is gitignored.

```
-----BEGIN CERTIFICATE-----
MIIDJTCCAsugAwIBAgIRAMR/qQaWM1BCMh/ooj8zvZMwCgYIKoZIzj0EAwIwKDEL
MAkGA1UEBhMCREUxGTAXBgNVBAMMEEdlcm1hbiBSZWdpc3RyYXIwHhcNMjYwNzI4
MTY0MjUyWhcNMjcwNzI4MTY0MjUyWjBQMQswCQYDVQQGEwJERTEMMAoGA1UECgwD
UE9TMSUwIwYDVQRhDBxOVFJERS1FVUlERS0zM0VBQ0QzODVERDYyNzQxMQwwCgYD
VQQDDANQT1MwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAAQjvGL3icQnbrrZUAFF
316jCEbfJqOaK7KZOHM6W62OctgUGWaY3UybSR12aODzI9T5jHFqOGiBjJduTzRb
oJ68o4IBrDCCAagwDAYDVR0TAQH/BAIwADAdBgNVHQ4EFgQU27c/NKN5nGgD4ICg
37p7JDDZRk0wHwYDVR0jBBgwFoAUqcKj2i9trFTuzrlO6CzJLACDgDMwDgYDVR0P
AQH/BAQDAgeAMBIGA1UdJQQLMAkGByiBjF0FAQYwUwYDVR0RBEwwSoYnaHR0cHM6
Ly9taWV1ZGl2ZXJpZmllci5taXR0ZXJidWNoZXIuY29tgh9taWV1ZGl2ZXJpZmll
ci5taXR0ZXJidWNoZXIuY29tMEsGA1UdIAREMEIwQAYHBACL7EYBAjA1MDMGCCsG
AQUFBwIBFidodHRwczovL3NhbmRib3guZXVkaS13YWxsZXQub3JnL2FwaS9jcHMw
RgYIKwYBBQUHAQEEOjA4MDYGCCsGAQUFBzAChipodHRwczovL3NhbmRib3guZXVk
aS13YWxsZXQub3JnL2FwaS9jYS5kZXIwSgYDVR0fBEMwQTA/oD2gO4Y5aHR0cHM6
Ly9zYW5kYm94LmV1ZGktd2FsbGV0Lm9yZy9hcGkvc3RhdHVzLW1hbmFnZW1lbnQv
Y3JsMAoGCCqGSM49BAMCA0gAMEUCID9WMeVIVwCToq4Wh3PLb4a33vmQPfXn76+N
AUEYGStoAiEA7JYFAOxqjqFfNlyAjmJDWM20+2u5LTkiNLgVcry9kQg=
-----END CERTIFICATE-----
```

| Property | Value |
|----------|-------|
| Subject | `C=DE, O=POS, organizationIdentifier=NTRDE-EUIDE-33EACD385DD62741, CN=POS` |
| Issuer | `C=DE, CN=German Registrar` |
| Serial | `C47FA90696335042321FE8A23F33BD93` |
| Validity | 2026-07-28 → 2027-07-28 |
| Key | EC P-256, signed with `ecdsa-with-SHA256` |
| Extended Key Usage | `1.0.18013.5.1.6` (ISO 18013-5 mdlReaderAuth) |
| SAN | `URI:https://mieudiverifier.mitterbucher.com`, `DNS:mieudiverifier.mitterbucher.com` |
| AIA CA-Issuers | `https://sandbox.eudi-wallet.org/api/ca.der` |
| CRL | `https://sandbox.eudi-wallet.org/api/status-management/crl` |

> ### ⚠️ The interim sandbox loses support on 19 November 2026
>
> From <https://eudi-hub.gov.de/de/start/sandbox-migration> (read 2026-11-12): support for
> `https://sandbox.eudi-wallet.org/` **ends on 2026-11-19**, and it is not a URL change. The EUDI
> Wallet app will begin **enforcing revocation lists** for access *and* registration certificates,
> and certificates issued by the interim sandbox "do not support this mechanism", so they stop
> working in current and future app versions. Everything in this section describes certificates
> from that sandbox.
>
> The replacement is the **EUDI Hub**, <https://portal.eudi-hub.gov.de/>, in beta and open for
> registration: create an account, register the organisation, configure which credentials you issue
> or request, then mint certificates from that configuration — configuration and issuance are now
> separate steps. ELSTER verification follows in early 2027; until then an organisation name carries
> an `unverified` prefix. Migration contact: `partner@d-you.help`.
>
> **No new technical endpoints are published publicly.** The BMI developer guide still documents the
> old sandbox and does not mention the hub or revocation enforcement; `api.eudi-hub.gov.de` and
> `account.eudi-hub.gov.de` — the two backends the portal's own CSP names — expose no OIDC discovery
> or OpenAPI document. So the new PKI's endpoints can only be read off a certificate the hub issues,
> which is what the reissue checklist below now does.
>
> One thing worth asking rather than assuming: the certificate reissued on 2026-11-04 already has a
> CRL distribution point and the two-level hierarchy described below, and that CRL is refreshed
> daily — so it may already satisfy the mechanism. It is still hosted on the sandbox that loses
> support, and public information does not say whether such certificates are covered.
>
> #### A hub certificate has read a real PID (2026-11-12)
>
> Verified end to end with an access certificate from the new hub
> (`Common Codes GmbH`, `OU=EUDI Hub`, `CN=Common Codes EUDI Hub Sandbox Access Certificates CA 01`):
> `credential valid … key via x5c`. The migration is therefore not a leap of faith. Three findings
> from that run:
>
> - **A single certificate in `x5c` was accepted.** The hub's intermediate CA cannot be fetched —
>   its AIA, `https://eudi-hub.gov.de/trust/sandbox-access-certificates.pem`, returns the portal's
>   error page with **HTTP 200**, and `/trust` is an empty shell. So the request carried the leaf
>   alone and the wallet took it, because it ships the hub's trust list. Read that as an observation
>   about this wallet build, not as licence to ship leaf-only: HAIP 1.0 §5 still asks for the path,
>   and a `200` serving HTML is worth reporting to `partner@d-you.help` — a 404 would at least be
>   detectable.
> - **The issuer side is untouched.** The D-Trust anchors still resolve the credential; the hub
>   governs the relying party's certificate, not the PID issuer's.
> - **Check every registration certificate the portal issues**, by decoding its `credentials`
>   array, before deploying it. Two different faults appeared in one afternoon: the first granted
>   `familiy_name` (a typo, so a request for `family_name` is dropped with no consent screen), and
>   its replacement fixed the spelling but set the mdoc entry to `format: "dc+sd-jwt"` with
>   `meta.vct_values`, where it should be `format: "mso_mdoc"` with `meta.doctype_value`.
>
>   Of those two, **only the spelling actually stopped a presentation.** The mislabelled format did
>   not: on 2026-11-12 an `mso_mdoc` request for all seven attributes succeeded against that
>   certificate, so this wallet build matches the claim paths and the type value and does not
>   enforce the `format` field. That was tested because the opposite was expected, and the
>   expectation was wrong — worth recording, because the reasoning that produced it looked sound.
>
>   Do not build on it. A certificate whose format is wrong is still wrong, the leniency is one
>   wallet build's behaviour rather than a documented rule, and the claim-name check is strict, so
>   there is no reason to think this one stays loose. Have the portal fix it; just do not spend a
>   day hunting it when something else is broken.
>
> The certificate also differs from the old one in ways that did not matter here but might
> elsewhere: no Extended Key Usage (the old one carried `1.0.18013.5.1.6`), a Key Usage of
> `Non Repudiation` alone, and a Subject Alternative Name pointing at the organisation's
> information page rather than the verifier's address — harmless under the `x509_hash` client
> identifier, which is a hash rather than a name, but a blocker under `x509_san_dns`.
>
> **The registrar's PKI has changed, and those last two rows describe the old one.** Verified
> 2026-11-12. The certificate reissued on 2026-11-04 comes from a two-level hierarchy and names
> different endpoints; old and new are served side by side:
>
> ```
> German Registrar Root CA                     self-signed, to 2036-10-12   /api/pki/root.der
> └── German Registrar Access Certificate CA 1 to 2029-10-14                /api/pki/ac/1.der
>     └── C=DE, O=POS, CN=POS                  to 2027-11-04                (ours)
> ```
>
> | | old certificate (2026-07-28) | current certificate (2026-11-04) |
> |---|---|---|
> | Issuer | `C=DE, CN=German Registrar` | `C=DE, O=German Registrar, CN=German Registrar Access Certificate CA 1` |
> | AIA CA-Issuers | `…/api/ca.der` | `…/api/pki/ac/1.der` |
> | CRL | `…/api/status-management/crl` | `…/api/pki/ac/1.crl` |
> | Root | the issuer itself, flat | `…/api/pki/root.der` |
>
> All five URLs answered 200 on 2026-11-12 and both CRLs had been regenerated that morning, so the
> old pair is still served for certificates issued before the change rather than abandoned.
>
> Two consequences. **Read the AIA and CRL off a reissued certificate rather than assuming them** —
> they are signed extensions, so a new hierarchy only reaches you with a new certificate, and a
> wallet follows what the certificate says, not what a document says. And this hierarchy is why the
> request object carries **two** certificates in `x5c`: the leaf and Access Certificate CA 1, with
> the root left out, which is what HAIP 1.0 section 5 asks for.
>
> Separately, a portal at `portal.eudi-hub.gov.de` ("Organisationsverwaltungsportal — manage your
> organization in the EUDI Hub") has appeared alongside `sandbox.eudi-wallet.org`. It is a Next.js
> application whose own CSP header names exactly two backends, `account.eudi-hub.gov.de` and
> `api.eudi-hub.gov.de`; neither exposes OIDC discovery or an OpenAPI document publicly, and the old
> sandbox does not redirect to it. Whether certificates issued through it carry yet other endpoints
> can only be read off such a certificate.

Checks worth running after any reissue:

```bash
# Extensions, issuer DN, validity
openssl x509 -in access.crt -noout -subject -issuer -dates \
        -ext authorityInfoAccess,crlDistributionPoints,subjectAltName,extendedKeyUsage

# Chain against what the certificate's OWN AIA serves, followed up to the root. Taking the URLs
# from the certificate instead of from here is the point: they changed once already.
aia=$(openssl x509 -in access.crt -noout -ext authorityInfoAccess | grep -oE 'https?://[^ ]+')
curl -s "$aia" | openssl x509 -inform der -out issuer.pem
root=$(openssl x509 -in issuer.pem -noout -ext authorityInfoAccess | grep -oE 'https?://[^ ]+')
if [ -n "$root" ]; then                       # two-level hierarchy (since 2026-11)
  curl -s "$root" | openssl x509 -inform der -out root.pem
  openssl verify -CAfile root.pem -untrusted issuer.pem access.crt
else                                          # flat hierarchy (before that)
  openssl verify -CAfile issuer.pem access.crt
fi

# Not revoked? Again from the certificate's own distribution point.
crl=$(openssl x509 -in access.crt -noout -ext crlDistributionPoints | grep -oE 'https?://[^ ]+')
curl -s "$crl" | openssl crl -inform der -noout -text \
  | grep "$(openssl x509 -in access.crt -noout -serial | cut -d= -f2)" || echo "not revoked"

# The client_id for VERIFIER_ORIGINALCLIENTID (x509_hash prefix)
openssl x509 -in access.crt -outform DER | openssl dgst -sha256 -binary \
  | openssl base64 -A | tr '+/' '-_' | tr -d '='
```

For this certificate the last command yields `t96yaT8i5o1oL9OXznzyzjETzSjDyhKuUHft7RVkik4`, which is
exactly what the wallet compares against the `client_id` it received.

## 1. How verifier trust works in OpenID4VP

1. The app asks a **verifier backend** to start a presentation (`POST /ui/presentations` with a
   DCQL query). The backend returns a `transaction_id` and a `request_uri`.
2. The app renders a QR code / deep link (`openid4vp://…?client_id=…&request_uri=…`).
3. The wallet scans it and **fetches the request object** (a signed JWT, `oauth-authz-req+jwt`) from
   `request_uri` (POST, `application/x-www-form-urlencoded`).
4. The request object is **signed with the verifier's access certificate**; the certificate chain
   travels in the JWT header (`x5c`). The `client_id` carries the scheme
   (`x509_san_dns:…`, `x509_hash:…`, or a pre-registered id).
5. The wallet **validates that `x5c` chain against its trusted list** (ETSI TS 119 602 *List of
   Trusted Entities*, LoTE — downloaded at runtime). Only if the chain is trusted does the wallet
   show the consent dialog and, after user confirmation, **post the presentation** back to the
   verifier's `response_uri`.

The decisive check is step 5: **trust of the verifier's certificate**, not the software that runs
the backend.

## 2. Why the EUDI reference wallet works with `eudiw.dev`

Inspecting a live request object from `verifier-backend.eudiw.dev`:

```
alg: ES256, typ: oauth-authz-req+jwt, client_id scheme: x509_hash
x5c leaf:
  Subject: CN=Verifier Signer, O=Niscy, organizationIdentifier=LEIEU-987654321
  Issuer:  CN=PID Issuer CA 02, O=EUDI Wallet Reference Implementation, C=EU   ← CA-signed
  SAN:     URI:https://verifier-backend.eudiw.dev/
```

The verifier certificate is issued by the **EUDI Wallet Reference Implementation CA**, and that CA
is a trust anchor in the reference wallet's LoTE (both `dev` and `demo` builds). The public CA
certificate even ships in the wallet repo (`resources-logic/src/main/res/raw/pidissuerca02_eu.pem`)
— but that is only useful for *verifying*, not for *issuing*: the CA **private key is not published**
and there is no public enrollment service for third-party verifier certificates.

So: same reference-implementation backend software, but `eudiw.dev` signs with a cert the wallet
trusts. **We cannot mint an equivalent cert ourselves.**

## 3. Why a self-hosted verifier is rejected

Running our own `eudi-srv-verifier-endpoint` and signing with a self-made test CA, we observed the
wallet **fetch the request object successfully but never submit** a response — the classic symptom
of the wallet refusing an **untrusted verifier chain**. Reference wallet reader policy:
`ReaderAuthPolicy.EnforceIfPresent` — *"admit readers that send no reader auth, but refuse a reader
that presents an untrusted chain."*

Operational lessons from that attempt (all backend configuration, not app code):

- The reference backend **refuses a directly self-signed** access certificate
  (`"access certificate must not be self-signed"`) → a minimal **test CA** signing a leaf with the
  correct SAN is required.
- The `self-signed` Spring profile defaults `client_id` to the **pre-registered** `Verifier`, which
  wallets reject. Use `VERIFIER_CLIENTIDPREFIX=x509_san_dns` +
  `VERIFIER_ORIGINALCLIENTID=<host matching a SAN>`.
- The self-signed profile defaults the JAR signing algorithm to **ES512**; a P-256 key needs
  `VERIFIER_ACCESS_CERTIFICATE_SIGNING_ALGORITHM=ES256`, otherwise the request object fails with
  `The ES512 algorithm is not allowed or supported`.
- The backend image is a Cloud-Native-Buildpack image with **no shell/wget/curl** → a Docker
  `healthcheck` using `wget` can never become healthy; omit it.

Even with a perfectly formed, correctly signed request object, the self-hosted cert is **not on any
wallet's trusted list**, so it is refused. This is unavoidable without a trust-registered CA.

## 4. Why EUDIWalletDE needs even more

The German wallet fails in **two** independent places, depending on which backend is used:

| Backend | EUDI reference wallet | EUDIWalletDE |
|---------|-----------------------|--------------|
| `eudiw.dev` (public, trust-registered) | ✅ works | ⚠️ reaches consent, but the **backend rejects the German PID** — `eudiw.dev` does not trust the Bundesdruckerei PID **issuer** |
| Own backend (self-made CA) | ❌ wallet rejects our **verifier** cert | ❌ same |

Two distinct trust checks are involved:

1. **Verifier trust** — does the wallet trust *us* (the relying party)? EUDIWalletDE uses the German
   RP Access CA as trust anchor, not the EU reference CA.
2. **Issuer trust** — does the backend trust the *issuer* of the presented PID? The German PID is
   issued by the Bundesdruckerei prototype issuer, which `eudiw.dev` does not trust; a self-hosted
   backend can be configured to accept it.

Additionally the German PID differs at the **data layer**: it uses the credential type
`https://demo.pid-issuer.bundesdruckerei.de/credentials/pid/1.0` and the OIDC-style claim name
**`birthdate`** (not `birth_date`). This is already handled — see `GermanPidVctValues` in
`VerifierSettings` and the extra DCQL alternative built in `VerifierApiService`; the response parser
accepts `birthdate` as an alias.

## 5. The SPRIND path (to a trusted German verifier)

To make EUDIWalletDE accept us, we need a Relying-Party Access Certificate from the **German RP
Access CA**, issued via the **SPRIND sandbox**. Per SPRIND: *"everyone in Germany that wants to
participate has to pass this sandbox."*

Steps:

1. Define the use case per the German PID Rulebook. Note which attributes the German PID actually
   carries — the portrait, document number, sex, e-mail, phone number and expiry date are **not**
   included, so a use case built around them cannot be fulfilled.
2. Submit the **intent form** (linked from the BMI developer guide).
3. Attend the monthly **kick-off call** → receive Closed-Beta wallet access + **Registrar portal**
   access (`https://sandbox.eudi-wallet.org/`).
4. Configure Access/Registration Certificates in the registrar → download a **PKCS#12 (`.p12`)**
   with the private key + RP access certificate.

   **Put both PID formats into one Registration Certificate.** Its `credentials` array takes
   several entries, and a certificate listing `mso_mdoc` (doctype `eu.europa.ec.eudi.pid.1`)
   *and* `dc+sd-jwt` (vct `urn:eudi:pid:de:1`) lets one request offer them as either/or via DCQL
   `credential_sets` — the pattern the German developer guide documents for PID presentations.
   The wallet then answers with whichever PID it holds, which matters because it may carry only
   one of the two.

   Two separately issued certificates do **not** add up to the same thing: a transaction carries
   exactly one Registration Certificate (`IntendedUse` holds a single `registrationCertificate`,
   and the JAR is built as `listOf(verifierInfo)`), and a request may not exceed what that one
   certificate covers. Asking for a second certificate afterwards means a second round trip
   through the registrar — cheaper to get it right in one go.

   Mind the per-format spelling: `birth_date`/`nationality` in mso_mdoc are `birthdate`/
   `nationalities` in SD-JWT VC.
5. Load that `.p12` into the verifier backend's JAR-signing keystore. Its `x5c` is then trusted by
   EUDIWalletDE.

Production RP registration additionally requires a German legal entity + official registration
number; the sandbox is the test track before that. Timeline: sandbox since Dec 2025 for selected
RPs, expanding through 2026, full ecosystem targeted ~end 2026.

## 6. One instance = one ecosystem → multi-backend architecture

Because a verifier backend signs **every** request object with a **single** access certificate for a
**single** `client_id` scheme, it can only be trusted by **one** ecosystem at a time. Serving both
the EUDI reference wallet and EUDIWalletDE therefore requires **two backend instances**.

The app makes this a single product via **named backends**:

- `VerifierSettings.Backends` maps a key → backend base URL (e.g. `eu`, `de`); `DefaultBackend`
  picks the default. When unset, the single `BackendUrl` is used as `eu` (unchanged default).
- The app builds **one `VerifierApiService` per backend** (one named `HttpClient` each) and routes
  per request:
  - `POST /api/verification?backend=<key>` (unknown/unconfigured → `400`)
  - `POST /api/reset?backend=<key>` (re-target a demo session)
  - `GET /?backend=<key>` (demo page)
  - `GET /api/backends` (list configured keys + default)
  - The chosen backend is echoed as `backend` in status/data responses.

Everything **above** the backend — the UI, the REST API, the session store, and the DCQL query
(which already requests mdoc + both SD-JWT variants + the German PID in one go) — is shared. Only
the **verifier identity** (signing cert + `client_id` scheme + issuer trust) is per-ecosystem, and
that lives in the backend instance, not the app.

Future note: in the mature eIDAS 2.0 ecosystem, national trusted lists aggregate into the EU List of
Trusted Lists (LOTL); a single German RP certificate would then be trusted by any conformant EU
wallet, collapsing this back to one instance. Today's prototype sandboxes are isolated, so this does
not yet apply.

## 7. Deployment model

- The app image (`ghcr.io/mibuw/mieudiverifier:latest`) publishes no host ports; a **reverse proxy**
  (Caddy) terminates TLS and forwards to the container on port 5050. The public demo runs at
  `https://mieudiverifier.mitterbucher.com`.
- The German backend instance is a **separate** container, running from
  [`docker/docker-compose.de-backend.yml`](../docker/docker-compose.de-backend.yml): the
  SPRIND-issued `.p12` is mounted as its keystore, a reverse-proxy route points its public host at
  it, and `EUDI_VerifierSettings__Backends__de` on the app selects it. Without that entry the app
  simply serves `eu` only, and the demo page hides the ecosystem switcher.
- **Single domain:** the access certificate's SAN is the app's own host, so the wallet-facing
  endpoints must live under it. Caddy routes `/wallet/*` on `mieudiverifier.mitterbucher.com` to the
  German backend and everything else to the app.

## Sources

- [OpenID4VP 1.0 (final)](https://openid.net/specs/openid-4-verifiable-presentations-1_0-final.html)
- [SD-JWT VC (IETF draft)](https://datatracker.ietf.org/doc/draft-ietf-oauth-sd-jwt-vc/)
- [eudi-srv-verifier-endpoint](https://github.com/eu-digital-identity-wallet/eudi-srv-verifier-endpoint)
- [eudi-app-android-wallet-ui](https://github.com/eu-digital-identity-wallet/eudi-app-android-wallet-ui) (trust config, `res/raw` trust anchors)
- [Bundesdruckerei prototype PID issuer — SD-JWT](https://demo.pid-issuer.bundesdruckerei.de/sdjwt)
- [BMI developer guide — RP onboarding](https://bmi.usercontent.opencode.de/eudi-wallet/developer-guide/rp/onboarding/rp_highlevel_onboarding/)
- [SPRIND EUDI Wallet](https://www.sprind.org/en/actions/strategic-projects/eudi-wallet)
