# German PID verifier without a backend

A verifier for the German EUDI Wallet that needs **no `eudi-srv-verifier-endpoint` container**.
One NuGet package hosts the request and response endpoints and verifies the presentation in
process.

Compare `../GermanPidVerifier`, which does the same job through `miEUDIverifier.Core` and the
Kotlin reference backend. Both have now completed a presentation from the real German wallet; this
one does it with one container instead of two and no JVM.

One gap separates it from a production path, documented below and not hidden: revocation is not
checked.

> **New to EUDI wallets?** [`SCHRITT-FUER-SCHRITT.md`](SCHRITT-FUER-SCHRITT.md) walks through a
> test run command by command, in German, and says after every step what you must see — including
> the one check that saves a wasted scan. The trust anchors it needs are already in
> [`trust-anchors/`](trust-anchors/); what is in that folder, and what you must bring yourself, is
> in [`trust-anchors/LIESMICH.txt`](trust-anchors/LIESMICH.txt). This README is the shorter path
> if you know the protocols.

## What it is made of

One package from NuGet, and no project reference into this repository — the sample consumes the
published package exactly as any other consumer would, which is also why it is not part of
`miEUDIverifier.sln`:

```xml
<PackageReference Include="miEUDIverifier.Core" Version="2.0.0" />
```

Two calls are the whole integration:

```csharp
builder.Services.AddInProcessVerifier(new InProcessVerifierOptions { /* five inputs */ });

app.UseForwardedHeaders();   // behind a proxy or tunnel
app.MapInProcessVerifier();  // the endpoints the wallet itself calls
```

Everything specific to the German ecosystem lives in the package: the registration certificate in
`verifier_info`, the certificate path in `x5c`, the encrypted response, the DCQL query, the trust
resolution and the verification itself. It pulls in `Tessio.Verifier.AspNetCore`, which does the
protocol work.

What is left in `Program.cs` is the five inputs only you can supply, and a page to show the result
on — about a hundred lines, most of it the page. That page is deliberately the sample's own rather
than the library's hosted demo page, because it shows what an integration actually looks like:

```csharp
var transaction = await verifier.InitializeTransactionAsync();
var qr          = QrCodeService.GeneratePng(transaction.BuildWalletDeepLink());
var envelope    = await verifier.WaitForWalletResponseAsync(transaction.TransactionId);
var identity    = await verifier.ExtractIdentityDataAsync(envelope, options: null);
```

### What used to be here

Earlier versions of this sample carried two files of their own,
`RegistrationCertificateRequestBuilder.cs` and `AccessCertificateChain.cs`, because the
registration certificate travels in the `verifier_info` claim — inside the signature, so it cannot
be bolted on afterwards — and Tessio 0.30.2 has no option for it
([issue 185](https://github.com/tripledownab/tessio-verifier/issues/185), with
[pull request 187](https://github.com/tripledownab/tessio-verifier/pull/187) pending).

Both now live in the package, under
[`src/miEUDIverifier.Core/Services/Tessio/`](../../src/miEUDIverifier.Core/Services/Tessio/). One
lesson from them is worth keeping where a reader will meet it: the builder deliberately lets the
library build and sign the request and then re-signs it with one claim added, reusing the library's
own JOSE header byte for byte. The first version did the opposite — assembled the claim set and
header itself — and was already wrong in a way only a wallet would have shown: it put a single
certificate in `x5c`, while HAIP 1.0 section 5 and ETSI TS 119 472-2 clause 6.4.2 require the
access certificate *and its path up to, but excluding, the trust anchor*. The reference
implementation sends two; a wallet handed only the leaf may be unable to reach its own trust list.

Revocation is off in the package's default, and that is a real gap rather than a preference — a
revoked PID is accepted. [Revocation](#revocation-is-not-checked) says why it cannot currently be
on, and it is not a reason to copy the setting without reading that.

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

It listens on `http://localhost:5000` — there is no `launchSettings.json`, so that is the Kestrel
default. Open the root path; the page renders the QR code and polls for the result.

The sample's own routes:

| Endpoint | Purpose |
|---|---|
| `GET /` | starts a verification if none is running, shows the QR code |
| `GET /status` | the result as JSON, which the page polls |
| `POST /new` | discards the current verification and starts another |

What `MapInProcessVerifier()` adds, for the wallet rather than for you:

| Endpoint | Purpose |
|---|---|
| `GET /verify/jar/{id}` | serves the signed request object to the wallet |
| `POST /verify/callback` | receives the encrypted response (`direct_post.jwt`) |
| `GET /verify/{sessionId}` | session status as JSON, anonymous surface |
| `GET /verify/{sessionId}/stream` | the same as server-sent events |
| `GET /verify/{sessionId}/return` | where the wallet sends the phone afterwards |
| `GET /verify/start` | redirected to `/` — see below |

The request object is served from `/verify/jar/{id}` rather than the library's own
`/verify/request/{id}`, because Tessio keeps the store behind that route internal to its package; a
host that creates its own sessions therefore serves the JAR itself.

`/verify/start` is the library's hosted demo page. It would create a session whose request object
it files where this host does not look, so `MapInProcessVerifier()` shadows it and redirects to
`/`. That is why this sample has a page of its own.

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
`cloudflared tunnel --url http://localhost:5000`, then set `GermanPid__PublicBaseUrl` to the URL
it prints — or use the `docker-compose.yml` next to this file, which wires both up. The wallet has
to reach `request_uri` and `response_uri` over the internet, so localhost will not do.

## A page opens on the phone afterwards

After the wallet finishes, a browser opens on the phone and lands on the verifier's **return page**.
This is expected, it is not something this sample chose, and it does not happen with the reference
backend — so it surprises anyone switching over. Here is exactly what happens and what is on it.

### Why

Tessio answers every processed wallet response with a `redirect_uri`, in both flows, and the wallet
follows it:

```csharp
// WalletCallbackEndpoint.cs
error is null ? new { redirect_uri = RedirectUri(origin, prefix, result) } : (object)new { error }

private static string RedirectUri(string origin, string prefix, CallbackResult result) =>
    $"{origin}{prefix}/{Uri.EscapeDataString(result.SessionId!)}/return"
    + $"#response_code={result.ResponseCode ?? SameDeviceSecrets.NewSecret()}";
```

It is deliberate, and the reason is good: a wallet must not be able to tell a cross-device session
from a same-device one by the shape of the answer, so **every** processed response gets a
`redirect_uri` with a fresh random code, whether or not anything is waiting to be redeemed.

SPEC: HAIP 1.0 §5.1 requires `redirect_uri` for the same-device flow. OpenID4VP 1.0 §8.2 leaves it
optional and asks that the URL carry "a fresh, cryptographically random value", which may be "a
path component, a fragment or a parameter" — Tessio uses a fragment, so the code never reaches a
server log or a `Referer` header. The EUDI reference backend sends no redirect for a cross-device
presentation at all (its wallet log line reads `Dispatch accepted, return url: ` with nothing
after it), which is why this is new rather than wrong.

### What happens, in order

1. The wallet POSTs the encrypted presentation to `{prefix}/callback`.
2. The callback verifies it, completes the session, and answers `200` with
   `{"redirect_uri": "https://…/verify/{sessionId}/return#response_code=…"}`.
3. The wallet opens that URL in the phone's browser.
4. `return.js` reads `response_code` out of the fragment and immediately removes it from the
   address bar, so it does not stay in history.
5. It POSTs the code to `{prefix}/{sessionId}/redeem`. **In a cross-device flow nothing is held**,
   so the answer is `not_held` and the page says "No presentation is waiting to be confirmed. Open
   your wallet again to retry." — then follows the session anyway.
6. It opens an SSE stream on `{prefix}/{sessionId}/stream` and renders whatever the session says.

Step 5 is worth knowing about: the "nothing waiting" wording is correct for same-device and reads
like a problem in cross-device, where the presentation has in fact just succeeded. The status line
from step 6 arrives immediately afterwards and overwrites it.

### What the page shows

A status line, a table of the disclosed attributes, and one line about the issuer:

| Element | Source | Content |
|---|---|---|
| Status line | the SSE stream's event | `Verified ✓` · `Verification failed ✗` · `Session expired` · `The wallet's answer was refused. Waiting for another…` · `Lost the connection to the verifier.` |
| Attribute table | `result.disclosedClaims` | **the claim values themselves** — one row per claim, name and value |
| Issuer line | `result.issuer` | `Issuer: <identifier> · trusted: <true/false> · key: <keyResolutionMethod>` |

So the holder sees their own PID attributes on their own phone. Everything is written with
`textContent` and never as markup, so an issuer identifier — which is the presenter's own string —
cannot become HTML.

### What that surface discloses, and what it withholds

The status endpoint and its stream ask for **nothing but a session id**, so they are an anonymous
surface, and the library treats them as one. `SessionView` narrows the verification result rather
than passing it through:

- **withheld:** every free-text failure message (a code remains), and the trust list identifier
  unless the deployment names it in `VerifierOptions.PublicTrustListSources`
- **never there at all:** the authorization request in any form, and `state` — because a wallet's
  callback is matched to its session by `state`, so it must not be reachable from something keyed
  only by a session id
- **disclosed:** the claim values, as above

Two consequences for a deployment to weigh. The claim values reach a second surface, the phone's
browser, which the verifier does not control; and the session id stays in that browser's URL and
history, so whoever later holds it can read the result again for as long as the session lives.
For the holder these are their own attributes, so this is not a disclosure to a third party — but
it is a copy in a place you did not choose.

### What you can do about it

**Leave it as it is.** For a self-service or same-device flow this page *is* the user's
confirmation, and showing it is the point. This sample leaves it on.

**Publish the trust list source,** if the anchor should be visible rather than redacted:
`VerifierOptions.PublicTrustListSources`. It is an allowlist by design, not a rule guessing which
strings are safe.

**Serve your own return page.** `MapTessioVerifier` maps the route and 0.30.2 has no option to turn
it off, but a host can shadow it the same way this sample shadows the hosted start page — with
middleware ahead of routing:

```csharp
app.Use(async (context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method)
        && context.Request.Path.StartsWithSegments(prefix, out var rest)
        && rest.Value?.EndsWith("/return", StringComparison.OrdinalIgnoreCase) == true)
    {
        // Your own page: a thank-you without the attributes, a redirect back to your site, …
        context.Response.Redirect("/done");
        return;
    }

    await next(context).ConfigureAwait(false);
});
```

That keeps the `redirect_uri` the spec wants and changes only what the holder is shown. Do not
instead strip `redirect_uri` out of the callback's JSON: it would remove a member HAIP requires in
the same-device flow, where it is what carries the flow forward.

**Wait for an option.** Asked upstream: an option on `VerifierOptions` to send the redirect only
where a response code is actually held, or to point it somewhere of the host's choosing. Note the
trade-off named above — sending it only for same-device tells a wallet which flow it is in, which
is exactly what today's unconditional answer avoids.

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

The package asks for the seven PID attributes by default. To change that, set `RequestedClaims` on
the options in `Program.cs`:

```csharp
builder.Services.AddInProcessVerifier(new InProcessVerifierOptions
{
    // …
    RequestedClaims = ["family_name", "given_name", "birth_date"],
});
```

Keep the list inside what the registration certificate grants. **One attribute more and the German
wallet drops the request with no consent screen** — which looks like a certificate fault and is
not one. The certificate itself needs one claim entry per attribute, mdoc entries carrying the
namespace as their first path segment; see
[`../../docs/german-pid-presentation.md`](../../docs/german-pid-presentation.md).

A different credential type needs `DocType` and `MdocNamespace` too. For the EUDI PID the two are
the same string, which is a coincidence of that credential rather than a rule.
