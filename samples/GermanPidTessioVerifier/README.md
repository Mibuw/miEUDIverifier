# German PID verifier without a backend

A verifier for the German EUDI Wallet that needs **no `eudi-srv-verifier-endpoint` container**.
One NuGet package hosts the request and response endpoints and verifies the presentation in
process.

Compare `../GermanPidVerifier`, which does the same job through `miEUDIverifier.Core` and the
Kotlin reference backend. Both have now completed a presentation from the real German wallet; this
one does it with one container instead of two and no JVM.

Two gaps separate it from a production path, both documented below and neither hidden:
revocation is not checked, and the registration certificate needs a request builder of our own
until the upstream change lands.

## What it is made of

```
Tessio.Verifier.AspNetCore 0.30.2     the hosted endpoints, the request object, the verification
RegistrationCertificateRequestBuilder what this sample adds: one claim, and a second signature
AccessCertificateChain                reads the access certificate and its issuers out of a PKCS#12
```

`AddTessioVerifier` registers its services with `TryAdd`, so anything registered **before** it
wins. This sample replaces two defaults:

| Default | Replaced by | Why |
|---|---|---|
| `DemoPresentationRequestBuilder` | `RegistrationCertificateRequestBuilder`, which **wraps** the library's own `SignedPresentationRequestBuilder` | 0.30.2 cannot put a registration certificate in `verifier_info`, and the German wallet refuses a request without one |
| `DevDefaultTrustListResolver` | `StaticTrustListResolver` over your anchor folder | the default trusts the library's own mock issuers and nothing else |

`MdocVerifier` is also re-registered, to turn `CheckStatus` **off**. That is a real gap, not a
preference — a revoked PID is accepted here. [Revocation](#revocation-is-not-checked) says why it
cannot currently be on, and it is not a reason to copy the setting without reading that.

### Why a request builder of our own

The registration certificate travels in the `verifier_info` claim, which is **inside** the
signature, so it cannot be bolted on afterwards. Release 0.30.2 has no option for it. That is
[issue 185](https://github.com/tripledownab/tessio-verifier/issues/185) upstream, with
[pull request 187](https://github.com/tripledownab/tessio-verifier/pull/187) pending. **When that
lands, delete `RegistrationCertificateRequestBuilder.cs`** and register Tessio's own
`SignedPresentationRequestBuilder` with `VerifierInfoJson` instead.

Until then, the builder does the least it can get away with: it lets the library build and sign
the request, then takes the result apart, adds the one claim, and signs the payload again under
**the library's own JOSE header, reused byte for byte**. The claim set, the `typ`, the `x5c` chain
and the certificate path rules all stay the library's. Roughly thirty lines are ours.

That is worth saying because the first version of this file did the opposite -- assembled the
whole claim set and the header itself -- and was already wrong in a way only a wallet would have
shown: it put a single certificate in `x5c`, while HAIP 1.0 section 5 and ETSI TS 119 472-2
clause 6.4.2 require the access certificate *and its path up to, but excluding, the trust anchor*.
The reference implementation sends two. A wallet handed only the leaf may be unable to reach its
own trust list.

The claim set was compared against a request object the reference implementation emitted for the
same presentation. They match claim for claim; this sample additionally sets `exp`.

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

There is one more switch, `GermanPid__AllowSelfSignedAccessCertificate`, default false. It exists
only so a local smoke test can run against a throwaway key. HAIP forbids a self-signed request
signer and a real wallet enforces that, so leaving it false is what keeps a misconfiguration from
reaching the wallet.

1. **Access certificate.** Export it as a PKCS#12 holding the certificate, its private key **and
   its issuing CA certificates**: the JAR must carry the whole path except the trust anchor, and
   the sample logs how many certificates it put in `x5c` at startup. Its SHA-256 over the DER *is*
   the `client_id` (`x509_hash:<base64url>`), also logged. The key must be EC P-256, because the
   request is signed ES256.
2. **Registration certificate.** A compact JWT whose `typ` header is `rc-wrp+jwt`, sent verbatim
   -- the wallet checks a signature over those exact bytes. It also fixes **what you may ask
   for**: request one attribute beyond its scope and the wallet drops the request with no consent
   screen, roughly a third of a second after it fetched the request object. That failure looks
   like a trust problem and is not one.
3. **Issuer trust anchors.** The CA certificates of the PID issuer, D-Trust for the German test
   ecosystem. Without them every presentation verifies cryptographically and is still refused as
   untrusted, so the sample refuses to start on an empty folder rather than fail later.

   `./fetch-trust-anchors.sh ./trust-anchors` downloads all three and checks both chains. You do
   not have to take the URLs on trust: each certificate names the next one up in its own
   `authorityInfoAccess` extension, so they came out of a real presentation —

   ```
   Deutschland PID-Provider Root Test CA 1 2026            to 2032-05-19  self-signed, the anchor
   Deutschland PID-Signer Test CA 1-26-1 2026              to 2031-05-19  signs the PID
   Deutschland PID-Status-List-Signer Test CA 1-26-2 2026  to 2031-05-19  signs the status list
   ```

   The first two are what verification needs: the root is the anchor and the PID signer CA builds
   the path, because the wallet's response carries only the signer's own certificate in its
   `x5chain`. The third is fetched so the folder describes the issuer completely; with revocation
   checking off it is never used, and it does **not** fix `status_invalid` — see
   [Revocation](#revocation-is-not-checked).

   Verified against a real captured presentation, which resolves as
   `trusted=True via=x5c anchor=CN=Deutschland PID-Provider Root Test CA 1 2026`.

   These are **test** anchors. A production verifier uses the production hierarchy, and should
   take it from the published trust list rather than from `authorityInfoAccess`.

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
- `x5c` carries the access certificate and its issuing CA, and leaves the self-signed root out,
  which is what the reference implementation does and what HAIP requires

Also verified, against a presentation captured from a real wallet: with the two anchors above,
the PID signer resolves as trusted through its `x5c`, and a single altered byte in an
issuer-signed item is caught as `mdoc_digest_mismatch`, naming the element and its `digestID`.

**Verified end to end against the real German EUDI Wallet** on 2026-11-11, with the production
access certificate and the wallet posting to this host through a temporary tunnel:

```
Verification session … created (Live mode)
Session … completed: credential valid
  (issuer SERIALNUMBER=…, CN=pp_pid_auth_crt, O=Bundesdruckerei GmbH, C=DE, key via x5c)
```

The wallet showed the consent screen with no policy warnings, which means it accepted the
registration certificate and found the seven requested attributes within what that certificate
grants. So the request side, the response side and the wallet's own policy engine all agree, with
no verifier backend in the picture.

Getting there cost five failed scans, every one of them a configuration fault rather than a
protocol one: an `x5c` with only the leaf in it, an `http` `response_uri` behind the tunnel (fixed
by `UseForwardedHeaders`), and two attempts to decorate `ISessionStore`, which breaks the wallet
callback because the store must also implement `IStateCorrelatingSessionStore` and
`ISameDeviceSessionStore`. The `/diagnostics/session/{id}` endpoint exists because of that: the
hosted status endpoint withholds failure messages by design, and guessing costs a scan each time.

For a one-off test without touching a production deployment, a temporary tunnel is enough:
`cloudflared tunnel --url http://localhost:5199`, then set `GermanPid__PublicBaseUrl` to the URL
it prints — or use the `docker-compose.yml` next to this file, which wires both up. The wallet has
to reach `request_uri` and `response_uri` over the internet, so localhost will not do.

### A page opens on the phone afterwards

Expected, and not something this sample chose. Tessio answers every processed response with a
`redirect_uri` pointing at its own return page, in both flows, deliberately — so a wallet cannot
tell a cross-device session from a same-device one by the shape of the answer. HAIP 1.0 section
5.1 requires the member for the same-device flow; OpenID4VP 1.0 section 8.2 leaves it optional.
The reference backend sends nothing there, which is why this is new.

There is no option for it in 0.30.2, and the only local workaround would be middleware rewriting
the callback's JSON. Asked upstream instead.

## Revocation is not checked

`CheckStatus` is off, so this sample answers "this PID was validly issued", not "this PID is still
valid". A revoked one passes. Do not carry the setting into anything real without reading why it
is there.

Turning it on fails every presentation with `status_invalid`:

> The top of the MSO revocation list's chain was not issued by the credential's Document Signer's
> issuer, by name and authority key identifier.

That is not a library defect. EAA-6.2.10.1-06.2 (Implementing Regulation (EU) 2024/2979, Annex II
as replaced by (EU) 2026/1731) requires the top of the status list's chain to be "signed or sealed
by the certificate used to sign the certificate in the x5chain element of the MSO" — whatever
signed the Document Signer. The German PID test issuer uses two sibling CAs under one root:

```
Deutschland PID-Provider Root Test CA 1 2026                 the root
├── Deutschland PID-Signer Test CA 1-26-1 2026               signs the Document Signer
└── Deutschland PID-Status-List-Signer Test CA 1-26-2 2026   signs the status list
```

CA 1-26-2 was signed by the root, not by 1-26-1, so the rule cannot hold — it compares issuer
names and signatures, not reachability within a trust list. **Adding 1-26-2 to the trust store
does not help**, which is the first thing the error makes you try.

The regulation leaves an issuer one way to use a separate status list CA: carry the `certificate`
element in the MSO's revocation reference (-06.1), which anchors the list on its own. This PID
does not carry it, which on that reading puts the issuer's side out of line rather than the
verifier's. Asked upstream in case there is a route neither side sees.

Ruled out along the way, so nobody repeats it: actual revocation (that is `credential_revoked`),
an index past the end of the list (16000 bytes, so 128000 indices, and every relevant one reads
0), the container not reaching the status endpoint (HTTP 200 from its own network namespace),
net8.0 versus net10.0, and the missing status list CA above.

One thing this sample does better by accident: Tessio serves `request_uri` repeatedly until it
expires, while the reference backend's endpoint is single-use. A wallet that resolves
`request_uri` twice -- which one app release actually did -- breaks against the backend and works
here.

## Requesting different attributes

`options.RequestedClaims` in `Program.cs`. Keep it inside what the registration certificate
grants, and remember the certificate needs one claim entry per attribute, mdoc entries carrying
the namespace as their first path segment. See `../../docs/german-pid-presentation.md`.
