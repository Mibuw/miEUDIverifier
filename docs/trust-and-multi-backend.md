# Trust model, wallet interoperability & multi-backend architecture

This document captures **why** the verifier works with some wallets but not others, what it takes to
support the **German EUDI Wallet (EUDIWalletDE)**, and **how** the app is structured to serve
multiple trust ecosystems. It is the reference for the design decisions behind
`VerifierSettings.Backends` and [`docker/docker-compose.de-backend.yml`](../docker/docker-compose.de-backend.yml).

> TL;DR — A verifier is only accepted by a wallet if the verifier's signing certificate chains to a
> Certificate Authority that the wallet **already trusts** (via its trusted list). One verifier
> backend signs with **one** certificate for **one** ecosystem. The EUDI reference wallet trusts the
> `eudiw.dev` reference CA; EUDIWalletDE trusts the German Relying-Party Access CA (obtained via the
> SPRIND sandbox). Therefore multiple wallets require multiple backend instances — which the app
> now supports through named backends.

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

1. Define the use case (PID → family name, given name, date of birth) per the German PID Rulebook.
2. Submit the **intent form** (linked from the BMI developer guide).
3. Attend the monthly **kick-off call** → receive Closed-Beta wallet access + **Registrar portal**
   access (`https://sandbox.eudi-wallet.org/`).
4. Configure Access/Registration Certificates in the registrar → download a **PKCS#12 (`.p12`)**
   with the private key + RP access certificate.
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
- The German backend instance is a **separate** container. A ready-to-fill template is in
  [`docker/docker-compose.de-backend.yml`](../docker/docker-compose.de-backend.yml): drop in the
  SPRIND-issued `.p12`, add a reverse-proxy route for its public host, and set
  `EUDI_VerifierSettings__Backends__de` on the app. Until then only `eu` is active.

## Sources

- [OpenID4VP 1.0 (final)](https://openid.net/specs/openid-4-verifiable-presentations-1_0-final.html)
- [SD-JWT VC (IETF draft)](https://datatracker.ietf.org/doc/draft-ietf-oauth-sd-jwt-vc/)
- [eudi-srv-verifier-endpoint](https://github.com/eu-digital-identity-wallet/eudi-srv-verifier-endpoint)
- [eudi-app-android-wallet-ui](https://github.com/eu-digital-identity-wallet/eudi-app-android-wallet-ui) (trust config, `res/raw` trust anchors)
- [Bundesdruckerei prototype PID issuer — SD-JWT](https://demo.pid-issuer.bundesdruckerei.de/sdjwt)
- [BMI developer guide — RP onboarding](https://bmi.usercontent.opencode.de/eudi-wallet/developer-guide/rp/onboarding/rp_highlevel_onboarding/)
- [SPRIND EUDI Wallet](https://www.sprind.org/en/actions/strategic-projects/eudi-wallet)
