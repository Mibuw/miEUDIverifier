# Presenting a German PID end-to-end (SPRIND sandbox / d-you wallet)

This document covers what it takes to get a **complete** German PID presentation — not just a
trusted connection, but the wallet actually releasing attributes. It picks up where
[`trust-and-multi-backend.md`](trust-and-multi-backend.md) leaves off: that document explains how to
become *trusted* by the German EUDI Wallet; this one explains why a trusted verifier can still come
back empty-handed.

> TL;DR — Trust and authorization are two separate gates. The **access certificate** decides whether
> the wallet talks to you at all. The **registration certificate** decides which attributes you may
> ask for, and it only grants what it spells out *one claim entry at a time*. Get that spelling
> wrong and the wallet aborts before the consent screen with an error that looks exactly like a
> broken certificate chain.

> **Update, 2026-11-11.** Everything below still holds, and it is still what makes a presentation
> succeed. What has changed is only *who* puts the registration certificate into the request: the
> German path no longer runs through a verifier backend, so there is no
> `VERIFIER_INTENDEDUSES_<n>_REGISTRATIONCERTIFICATE` and no intended-use id to reference it by. The
> application holds the certificate and places it in the request object's `verifier_info` itself,
> as one element with `format: "registration_cert"`. The spelling rules for the granted claims are
> unchanged — and so is the failure mode when they are wrong.

## 0. Status — full PID attribute set verified (2026-11-04)

Since **4 November 2026** the `de` backend completes a presentation of all seven PID attributes
against the d-you wallet (SPRIND sandbox). The request offers both credential formats as
alternatives and the wallet picks one per presentation; both have been exercised — `dc+sd-jwt` at
14:19 UTC, `mso_mdoc` at 14:28.

| | |
|---|---|
| Formats | `mso_mdoc` (`eu.europa.ec.eudi.pid.1`) and `dc+sd-jwt` (`urn:eudi:pid:de:1`), as `credential_sets` alternatives — both verified |
| Attributes | family name, given name, date of birth, place of birth, nationality, issuing authority, issuing country |
| JAR delivery | `ByReference`, `request_uri_method=get` |
| Response mode | `direct_post.jwt` |

Which format a run used is visible in the VP token key, suffixed `-mdoc` or `-sdjwt-de`. The
verifier log of the `dc+sd-jwt` run, with the disclosed values:

```
14:18:53  Wallet retrieved Request Object - tx: QONqjV3R…
14:19:01  Wallet posted response. VP Token: '{"…-sdjwt-de":["eyJ4NWMiOi…
          family_name MUSTERMANN · given_name ERIKA · birthdate 1964-08-12
          place_of_birth→locality BERLIN · nationalities [DE]
          issuing_authority DE · issuing_country DE
```

Getting there took three fixes. As with the trust problems before them, none is obvious from the
error the wallet shows.

## 1. The registration certificate decides which attributes you may ask for

The registration certificate (RC) travels to the wallet as `verifier_info` inside every request
object. The wallet's policy engine compares the DCQL query against it, attribute by attribute.

**Every attribute needs its own `claim` entry.** `path` addresses *one* claim and its nested
members — it is not a list. Putting seven names into one entry:

```json
"claim": [ { "path": ["family_name","given_name","birthdate","place_of_birth",
                      "nationalities","issuing_authority","issuing_country"] } ]
```

does not grant seven attributes. It grants `family_name` with six levels nested underneath it, and
only the first segment ends up in scope. The correct form is one entry per attribute:

```json
"claim": [ { "path": ["family_name"] },
           { "path": ["given_name"]  },
           { "path": ["birthdate"]   } ]
```

Per format:

- **`dc+sd-jwt`** — one segment per entry. Granting a parent covers its nested members: an entry for
  `place_of_birth` also satisfies a request for `place_of_birth.locality`.
- **`mso_mdoc`** — **two** segments per entry, namespace first:
  `{"path":["eu.europa.ec.eudi.pid.1","family_name"]}`. An mdoc entry without its namespace grants
  nothing usable.

**The registrar UI shows you this, if you know what to look for.** It renders each claim entry as a
chain with arrows. `family_name → given_name → birthdate → …` is a single malformed entry. For
`dc+sd-jwt` there must be no arrows at all; for `mso_mdoc` exactly one per entry, between namespace
and attribute.

**The symptom, if you get it wrong.** The wallet logs

```
Policy warnings: [… "Credential 'urn:eudi:pid:de:1' requests claims beyond policy scope.
                     Extra fields: [given_name, birthdate, place_of_birth, …]"]
```

and then posts an error response a fraction of a second after fetching the request object:

```
14:06:03.352  Handling GetRequestObject …
14:06:03.683  Handling PostWalletResponse …          (0.33 s later)
14:06:03.692  Wallet posted response. VP Token: 'null'
```

No consent screen, no PIN prompt. On the verifier side this is indistinguishable from a trust
failure, and the access certificate is not at fault. One attribute over the line is enough — adding
only `given_name` to a `family_name`-only request reproduced it exactly.

This is self-service: reissue the RC in the registrar with one entry per attribute. The access
certificate and its key stay valid, and `VERIFIER_ORIGINALCLIENTID` does not change — only
`VERIFIER_INTENDEDUSES_0_REGISTRATIONCERTIFICATE`.

## 2. `request_uri` is single-use — and some wallet builds resolve it twice

The EUDI reference verifier serves a request object exactly once. `RetrieveRequestObject` requires
the presentation to be in state `Requested` and moves it to `RequestObjectRetrieved`; a second fetch
of the same `request_uri` returns **400**. The guard is a hard `ensure` in the source with no
configuration flag — by design, "so that only once the Request Object can be retrieved by the
wallet".

Wallet builds that resolve the request twice — once to render the relying-party screen, once on
consent — therefore hit a 400 on the second attempt. Whether that is fatal is up to the wallet:

- d-you, October 2026: logged the failed second resolution and carried on; presentation succeeded.
- d-you, early November 2026: surfaced it as `invalidQueryResolution.other` and aborted right after
  the user tapped *Continue*.
- d-you, after the November 2026 app update: resolves once. Fixed on the wallet side.

**`request_uri_method` matters here.** With `post` the wallet sends `wallet_metadata` and a fresh
`wallet_nonce` per call, and the backend binds that nonce into the signed JAR — so a cached or
replayed request object would be invalid. With `get` the JAR carries no `wallet_nonce` at all
(claims are `aud, client_id, client_metadata, dcql_query, iat, nonce, response_mode, response_type,
response_uri, state, verifier_info`), which makes it safely replayable should a caching workaround
ever be needed. `get` also produces a smaller JAR. This repo defaults to `get`.

## 3. Verified working configuration

App (`mieudiverifier`), `de` backend:

```yaml
EUDI_VerifierSettings__Backends__de: "http://eudi-verifier-backend-de:8080"
EUDI_VerifierSettings__RequestUriMethod: "get"
EUDI_VerifierSettings__IntendedUseIds__de: "pos-pid"
EUDI_VerifierSettings__SdJwtVctValuesByBackend__de__0: ""          # generic EU SD-JWT: not covered by the RC
EUDI_VerifierSettings__GermanPidVctValuesByBackend__de__0: "urn:eudi:pid:de:1"
EUDI_VerifierSettings__PidClaimsByBackend__de__0: "family_name"    # mso_mdoc spelling;
EUDI_VerifierSettings__PidClaimsByBackend__de__1: "given_name"     # SD-JWT gets birthdate /
EUDI_VerifierSettings__PidClaimsByBackend__de__2: "birth_date"     # nationalities automatically
EUDI_VerifierSettings__PidClaimsByBackend__de__3: "place_of_birth"
EUDI_VerifierSettings__PidClaimsByBackend__de__4: "nationality"
EUDI_VerifierSettings__PidClaimsByBackend__de__5: "issuing_authority"
EUDI_VerifierSettings__PidClaimsByBackend__de__6: "issuing_country"
EUDI_VerifierSettings__ResponseModes__de: "direct_post.jwt"
```

Reference backend (`eudi-verifier-backend-de`):

```yaml
VERIFIER_CLIENTIDPREFIX: "x509_hash"
VERIFIER_ORIGINALCLIENTID: "<base64url SHA-256 of the access certificate DER>"
VERIFIER_INTENDEDUSES_0_ID: "pos-pid"
VERIFIER_INTENDEDUSES_0_REGISTRATIONCERTIFICATE: "<RC as JWT>"
VERIFIER_REQUESTJWT_EMBED: "ByReference"
VERIFIER_REQUESTJWT_REQUESTURIMETHOD: "Get"
```

Note that `VERIFIER_REQUESTJWT_REQUESTURIMETHOD` is only the backend's fallback. The app sends
`request_uri_method` per transaction in the init-transaction body, and that value wins — changing
the backend variable alone has no effect on the deep link.

## 4. Verifying a deployment

Reading the wallet log is the slow path. Check what is actually served: the RC and the DCQL must
match one-to-one, allowing for the SD-JWT translation (`birth_date`→`birthdate`,
`nationality`→`nationalities`).

```bash
BASE=https://your-verifier.example

RU=$(curl -s -X POST "$BASE/api/verification?backend=de" \
     | python -c "import sys,json,urllib.parse as u; d=json.load(sys.stdin); \
       print(u.parse_qs(u.urlparse(d['deepLink']).query)['request_uri'][0])")

curl -s "$RU" > /tmp/jar.txt
python - /tmp/jar.txt <<'PY'
import sys, base64, json
pad = lambda s: s + "=" * (-len(s) % 4)
payload = open(sys.argv[1]).read().strip().split(".")[1]
pl = json.loads(base64.urlsafe_b64decode(pad(payload)))
rc = json.loads(base64.urlsafe_b64decode(pad(pl["verifier_info"][0]["data"].split(".")[1])))
print("RC jti:", rc["jti"])
for c in rc["credentials"]:
    print("  RC  ", c["format"], [cl["path"] for cl in c["claim"]])
for c in pl["dcql_query"]["credentials"]:
    print("  DCQL", c["format"], [cl["path"] for cl in c["claims"]])
PY
```

Use `curl -s "$RU"` for `request_uri_method=get` and `curl -s -X POST "$RU"` for `post`. Either way
the request object is consumed by this call — the transaction it belongs to cannot be scanned
afterwards.
