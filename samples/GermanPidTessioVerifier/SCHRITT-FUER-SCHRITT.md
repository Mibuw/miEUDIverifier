# Schritt für Schritt: einen deutschen PID auslesen, ohne Backend

Diese Anleitung führt von „nichts installiert" bis „die Daten stehen auf dem Bildschirm".
Jeder Befehl ist zum Kopieren gedacht. Nach jedem Schritt steht, **was du sehen musst** — wenn
dort etwas anderes steht, lies nicht weiter, sondern schau in [Wenn etwas schiefgeht](#wenn-etwas-schiefgeht).

Am Ende hast du: eine Webseite mit QR-Code, die du mit der deutschen EUDI-Wallet scannst, und die
danach Name, Geburtsdatum und Geburtsort aus dem Ausweis anzeigt. Dazwischen läuft **kein**
Verifier-Backend, kein Java, kein zweiter Container.

Rechne mit 30–45 Minuten, wenn du die Zertifikate schon hast.

---

## Inhalt

1. [Was du brauchst](#1-was-du-brauchst)
2. [.NET installieren](#2-net-installieren)
3. [Das Projekt holen](#3-das-projekt-holen)
4. [Die zwei Zertifikate besorgen](#4-die-zwei-zertifikate-besorgen)
   - [4b. Nur PEM-Dateien? Die `.p12` selbst bauen](#4b-du-hast-nur-pem-dateien-dann-baust-du-die-p12-selbst)
5. [Den Keystore prüfen — der Schritt, der einen Testlauf spart](#5-den-keystore-prüfen--der-schritt-der-einen-testlauf-spart)
6. [Trust Anchors — schon fertig](#6-trust-anchors--schon-fertig)
7. [Von außen erreichbar werden](#7-von-außen-erreichbar-werden)
8. [Konfigurieren](#8-konfigurieren)
9. [Starten](#9-starten)
10. [Scannen](#10-scannen)
11. [Was danach auf dem Handy passiert](#11-was-danach-auf-dem-handy-passiert)
12. [Wenn etwas schiefgeht](#wenn-etwas-schiefgeht)
13. [Variante: mit Docker statt .NET](#variante-mit-docker-statt-net)
14. [In die eigene Anwendung einbauen](#in-die-eigene-anwendung-einbauen)

---

## 1. Was du brauchst

| | Was | Woher |
|---|---|---|
| ☐ | Ein Rechner mit Windows, macOS oder Linux | — |
| ☐ | .NET SDK 8 oder neuer | [Schritt 2](#2-net-installieren) |
| ☐ | `openssl` und `curl` | bei macOS/Linux dabei; bei Windows in Git Bash enthalten |
| ☐ | Ein Smartphone mit der **deutschen EUDI-Wallet** und einem **Test-PID** darin | SPRIND-Sandbox |
| ☐ | Das **Zugriffszertifikat** als `.p12` (plus Passwort, falls eines gesetzt ist) — oder Zertifikat und Schlüssel als PEM, dann [Schritt 4b](#4b-du-hast-nur-pem-dateien-dann-baust-du-die-p12-selbst) | [Schritt 4](#4-die-zwei-zertifikate-besorgen) |
| ☐ | Das **Registrierungszertifikat** (ein langer Text in einer Zeile) | [Schritt 4](#4-die-zwei-zertifikate-besorgen) |
| ☐ | Die **Trust Anchors** des Ausstellers | liegen schon bei, siehe [Schritt 6](#6-trust-anchors--schon-fertig) |

> **Zum Passwort:** Was du zwingend brauchst, ist der **private Schlüssel** — er signiert die
> Anfrage, und ohne Signatur öffnet die Wallet sie nicht einmal. Ein Passwort brauchst du nur
> dann, wenn die `.p12` eines hat: es verschlüsselt den Schlüssel *innerhalb* der Datei. Eine
> `.p12` ohne Passwort ist erlaubt und funktioniert hier — siehe
> [Schritt 4b](#4b-du-hast-nur-pem-dateien-dann-baust-du-die-p12-selbst). Was nicht geht, ist eine
> Datei ohne Schlüssel.

---

## 2. .NET installieren

Prüfe zuerst, ob schon etwas da ist:

```bash
dotnet --list-sdks
```

**Was du sehen musst:** mindestens eine Zeile, die mit `8.` oder höher beginnt, zum Beispiel
`8.0.404 [C:\Program Files\dotnet\sdk]`. Steht dort nichts oder „command not found", installiere:

- **Windows:** `winget install --id Microsoft.DotNet.SDK.8`
- **macOS:** `brew install --cask dotnet-sdk`
- **Linux / anderes:** <https://dotnet.microsoft.com/download/dotnet/8.0>

Danach **Terminal neu öffnen** und `dotnet --list-sdks` erneut ausführen.

---

## 3. Das Projekt holen

```bash
git clone https://github.com/Mibuw/miEUDIverifier.git
cd miEUDIverifier/samples/GermanPidTessioVerifier
```

Prüfe, dass es baut:

```bash
dotnet build
```

**Was du sehen musst:** `Der Buildvorgang wurde erfolgreich ausgeführt.` bzw.
`Build succeeded.` — und `0 Fehler`. Das lädt beim ersten Mal `miEUDIverifier.Core` aus dem
Internet (und damit `Tessio.Verifier.AspNetCore`), das kann eine Minute dauern.

Das Sample hat **keinen** Projektverweis in dieses Repository: es benutzt das veröffentlichte
NuGet-Paket, genau wie jeder andere Konsument. Deswegen gehört es auch nicht zu
`miEUDIverifier.sln`.

---

## 4. Die zwei Zertifikate besorgen

Beide kommen aus der **SPRIND-Sandbox**: <https://sandbox.eudi-wallet.org/>

Dort registrierst du dich als Relying Party (Prüfstelle) und bekommst:

**(1) Das Zugriffszertifikat** — es beweist der Wallet, *wer* fragt.
Du brauchst es als PKCS#12-Datei (Endung `.p12` oder `.pfx`) **mit privatem Schlüssel**, plus das
Passwort dazu. Lege es irgendwo ab, wo es **nicht** im Git-Repository liegt. Im Projekt ist der
Ordner `cert/` genau dafür von Git ausgenommen:

```bash
mkdir -p ../../cert
# Datei dorthin kopieren, z.B. als ../../cert/keystore.p12
```

**(2) Das Registrierungszertifikat** — es sagt, *welche Attribute* du fragen darfst.
Das ist ein JWT: ein langer Text aus drei Blöcken, getrennt durch Punkte, ungefähr so:

```
eyJ0eXAiOiJyYy13cnArand0IiwiYWxnIjoi…  .  eyJzdWIiOiJ…  .  MEUCIQDf…
```

Speichere es in eine Datei, **eine Zeile, kein Umbruch, keine Leerzeichen**:

```bash
# Inhalt genau so einfügen, wie du ihn bekommen hast
# (Windows: Notepad, "Speichern unter", Kodierung UTF-8, keine Zeilenumbrüche)
../../cert/registration-certificate.txt
```

> **Warum so pedantisch:** Die Wallet prüft eine Signatur über **genau diese Zeichen**. Ein
> zusätzlicher Zeilenumbruch, ein Leerzeichen am Ende oder ein „aufgeräumtes" Format macht die
> Signatur ungültig, und die Wallet bricht ab, bevor sie dich überhaupt fragt.

### 4b. Du hast nur PEM-Dateien? Dann baust du die `.p12` selbst

Das ist der häufigere Fall: du bekommst **Zertifikat** und **privaten Schlüssel** als zwei
PEM-Dateien (`.pem`, `.crt`, `.key`) und keine `.p12`. Kein Problem — eine `.p12` ist nur ein
Behälter, und openssl packt ihn in einem Befehl.

**Was du dafür brauchst, sind drei Teile:**

| Teil | Typische Datei | Hast du den? |
|---|---|---|
| dein privater Schlüssel | `access.key`, `privkey.pem` | ja, sagst du |
| dein Zertifikat | `access.crt`, `cert.pem` | ja, sagst du |
| das Zertifikat der **ausstellenden CA** | `ca.crt`, `chain.pem` | **das fehlt oft** — siehe unten |

**Fehlt die CA, frag dein eigenes Zertifikat, wo sie liegt.** Jedes ordentlich ausgestellte
Zertifikat nennt seinen Ausstellerpfad in der Erweiterung `authorityInfoAccess`:

```bash
openssl x509 -in access.crt -noout -text | grep -A2 "Authority Information Access"
```

Das gibt eine Zeile `CA Issuers - URI:http://…` aus. Die lädst du herunter (`-L` ist wichtig,
solche Server leiten gern um) und wandelst sie nach PEM, falls sie als DER kommt:

```bash
curl -sSL -o ca.der "http://…die-adresse-aus-der-zeile…"
openssl x509 -inform DER -in ca.der -out ca.crt      # falls DER
# schon PEM? dann einfach als ca.crt verwenden
```

Prüfe, dass es die richtige ist — der `subject` der CA muss dem `issuer` deines Zertifikats
entsprechen:

```bash
openssl x509 -in access.crt -noout -issuer
openssl x509 -in ca.crt     -noout -subject
```

**Dann der eine Befehl, der alles zusammenpackt:**

```bash
openssl pkcs12 -export \
  -inkey access.key \
  -in access.crt \
  -certfile ca.crt \
  -passout pass: \
  -out keystore.p12
```

`-certfile ca.crt` ist der Teil, auf den es ankommt — ohne ihn landet nur dein eigenes Zertifikat
in der Datei, und du läufst in genau den Fehler aus [Schritt 5](#5-den-keystore-prüfen--der-schritt-der-einen-testlauf-spart).
Liegt die Kette als eine Datei mit mehreren Zertifikaten vor (`chain.pem`), gib einfach die an.

**`-passout pass:` bedeutet: kein Passwort.** Das ist erlaubt und funktioniert. Setze dann in
[Schritt 8](#8-konfigurieren) die Variable `GermanPid__AccessCertificatePassword` **gar nicht**
(oder auf einen leeren Wert). Der Verifier startet trotzdem.

Wenn du lieber eines setzt, nimm `-passout pass:deinPasswort` und trage es in Schritt 8 ein. Für
einen Testlauf auf deinem eigenen Rechner bringt es nichts: der private Schlüssel liegt vorher
schon unverschlüsselt in `access.key`, ein Passwort auf der `.p12` schützt also nichts, was nicht
daneben offen liegt. Für den Transport auf einen Server ist es sinnvoll — dann aber die
unverschlüsselte PEM-Datei danach löschen.

> **Ist dein `access.key` selbst mit einer Passphrase geschützt?** Dann fragt openssl beim Export
> danach. Das ist nicht dasselbe wie das `.p12`-Passwort: das eine schützt die PEM-Datei, das
> andere den fertigen Behälter.

Zum Abschluss prüfen, ob Schlüssel und Zertifikat zueinander passen und zwei Zertifikate drin sind
— beides steht in [Schritt 5](#5-den-keystore-prüfen--der-schritt-der-einen-testlauf-spart).

---

## 5. Den Keystore prüfen — der Schritt, der einen Testlauf spart

Hier liegt der Fehler, der sich am teuersten rächt: er fällt erst in der Wallet auf, nachdem du
schon gescannt hast.

In der `.p12`-Datei müssen **zwei Zertifikate** stecken: dein eigenes **und** das der CA, die es
ausgestellt hat. Nur das eigene reicht nicht.

```bash
# Fragt nach dem Passwort (Eingabe bleibt unsichtbar). Absichtlich so,
# damit das Passwort nicht in der Terminal-History landet.
openssl pkcs12 -in ../../cert/keystore.p12 -nokeys -info 2>/dev/null \
  | grep -c "BEGIN CERTIFICATE"
```

**Was du sehen musst:** `2` (oder mehr). Steht dort `1`, exportiere die Datei bei der Sandbox noch
einmal **mit Kette** („include certificate chain", „full chain") oder baue sie zusammen:

```bash
openssl pkcs12 -export -inkey mein.key -in mein.crt -certfile ca.crt -out keystore.p12
```

> **Warum:** HAIP 1.0 Abschnitt 5 und ETSI TS 119 472-2 Abschnitt 6.4.2 verlangen, dass die Anfrage
> das Zertifikat **und seinen Weg nach oben bis vor den Vertrauensanker** mitschickt (das Feld
> `x5c`). Eine Wallet, die nur das eine Zertifikat bekommt, kann unter Umständen keinen Pfad zu
> ihrer Vertrauensliste bauen und lehnt ab — mit der Meldung
> `Could not trust certificate chain`, die nach einem kaputten Zertifikat klingt und keines ist.

Noch aussagekräftiger — zeigt, wer was ausgestellt hat:

```bash
openssl pkcs12 -in ../../cert/keystore.p12 -nokeys 2>/dev/null \
  | grep -E "^subject=|^issuer="
```

**Was du sehen musst:** zwei Paare, und das erste muss ins zweite zeigen. Also der `issuer=` des
ersten Zertifikats ist der `subject=` des zweiten:

```
subject=…, CN=dein-verifier, …          <- dein Zertifikat
issuer=…,  CN=die-ausstellende-CA, …       |
subject=…, CN=die-ausstellende-CA, …    <--+  dieselbe CA, jetzt als Zertifikat
issuer=…,  CN=die-Wurzel, …
```

Kommt nur **ein** Paar, fehlt die CA. Zeigt der `issuer=` des ersten auf etwas, das im zweiten
Paar nicht als `subject=` auftaucht, ist die falsche CA beigepackt.

---

## 6. Trust Anchors — schon fertig

Sie liegen im Ordner [`trust-anchors/`](trust-anchors/) neben dieser Datei: drei öffentliche
Zertifikate des deutschen PID-Test-Ausstellers (D-Trust). **Du musst hier nichts tun.**

Was sie sind, wie du ihre Echtheit selbst nachprüfst und welches davon wofür da ist, steht in
[`trust-anchors/LIESMICH.txt`](trust-anchors/LIESMICH.txt).

Falls du sie frisch holen willst (das Script prüft die Ketten selbst und bricht ab, wenn etwas
nicht aufgeht):

```bash
./fetch-trust-anchors.sh ./trust-anchors
```

**Was du sehen musst:** drei Zeilen mit `Deutschland PID-…` und zweimal
`Chain check: … verifies against the root.`

> **Warum das nötig ist:** Die Antwort der Wallet schickt nur das Zertifikat des PID-Signierers mit,
> nicht den Weg nach oben. Ohne diese Dateien wird jede Präsentation kryptografisch korrekt geprüft
> und trotzdem als „nicht vertrauenswürdig" abgelehnt.

---

## 7. Von außen erreichbar werden

Das ist der Schritt, den man am leichtesten unterschätzt: **die Wallet ist nicht dein Browser.**

Das Handy holt die Anfrage über das Internet ab und schickt die Antwort über das Internet zurück.
`localhost` kann es nicht erreichen — das Handy meint damit sich selbst. Und es muss `https` sein.

Für einen Test genügt ein Wegwerf-Tunnel von Cloudflare. Kein Konto, keine DNS-Änderung:

```bash
# Installieren
#   Windows: winget install --id Cloudflare.cloudflared
#   macOS:   brew install cloudflared
#   Linux:   https://github.com/cloudflare/cloudflared/releases

cloudflared tunnel --url http://localhost:5000
```

**Was du sehen musst:** im Kasten eine Adresse der Form

```
https://irgendwas-zufaelliges.trycloudflare.com
```

**Diese Adresse brauchst du im nächsten Schritt.** Lass das Fenster offen — wenn du es schließt, ist
der Tunnel weg. Beim nächsten Start bekommst du eine **neue** Adresse, und dann musst du Schritt 8
und 9 wiederholen.

---

## 8. Konfigurieren

Fünf Werte. Setze sie als Umgebungsvariablen in dem Terminal, in dem du gleich startest.

**Windows (PowerShell):**

```powershell
$env:GermanPid__AccessCertificatePath       = "C:\Pfad\zu\cert\keystore.p12"
$env:GermanPid__AccessCertificatePassword   = "dein-p12-passwort"
$env:GermanPid__RegistrationCertificatePath = "C:\Pfad\zu\cert\registration-certificate.txt"
$env:GermanPid__IssuerTrustAnchorDirectory  = "C:\Pfad\zu\samples\GermanPidTessioVerifier\trust-anchors"
$env:GermanPid__PublicBaseUrl               = "https://irgendwas-zufaelliges.trycloudflare.com"
```

**macOS / Linux / Git Bash:**

```bash
export GermanPid__AccessCertificatePath="$HOME/cert/keystore.p12"
export GermanPid__AccessCertificatePassword="dein-p12-passwort"
export GermanPid__RegistrationCertificatePath="$HOME/cert/registration-certificate.txt"
export GermanPid__IssuerTrustAnchorDirectory="$PWD/trust-anchors"
export GermanPid__PublicBaseUrl="https://irgendwas-zufaelliges.trycloudflare.com"
```

Die zwei Unterstriche sind kein Tippfehler: `__` trennt in .NET die Konfigurationsebenen.

| Variable | Bedeutung |
|---|---|
| `AccessCertificatePath` | die `.p12` aus Schritt 4 |
| `AccessCertificatePassword` | deren Passwort |
| `RegistrationCertificatePath` | die Textdatei aus Schritt 4 (alternativ `GermanPid__RegistrationCertificate` mit dem Wert direkt) |
| `IssuerTrustAnchorDirectory` | der Ordner aus Schritt 6 |
| `PublicBaseUrl` | die Tunnel-Adresse aus Schritt 7, **ohne** Schrägstrich am Ende |

---

## 9. Starten

```bash
dotnet run
```

**Was du sehen musst** — vier Zeilen, in dieser Reihenfolge:

```
info: In-process verifier hosted under /verify: request objects at /verify/jar/{id},
      wallet callback at https://…trycloudflare.com/verify/callback, revocation checking OFF.
warn: Revocation is NOT checked on the in-process path: a revoked PID is accepted.
info: Access certificate in use: client_id x509_hash:…
info: Now listening on: http://localhost:5000
```

Prüfe daran drei Dinge:

- **die Adresse hinter `wallet callback at`** muss deine Tunnel-Adresse sein und mit `https`
  beginnen. Steht dort `http` oder `localhost`, stimmt `PublicBaseUrl` nicht — die Wallet würde die
  Antwort verweigern.
- **`client_id x509_hash:…`** kommt aus deinem Zertifikat. Bleibt gleich, solange das Zertifikat
  gleich bleibt; genau diesen Wert will ein Registrar von dir.
- **die `warn`-Zeile zur Revocation gehört dort hin.** Sie ist kein Fehler, sondern der Hinweis auf
  die eine Lücke dieses Weges, siehe die README des Samples.

Die Anzahl der Zertifikate im `x5c` steht hier **nicht** — das Paket leitet sie selbst ab. Deshalb
war [Schritt 5](#5-den-keystore-prüfen--der-schritt-der-einen-testlauf-spart) wichtig: dort hast du
vor dem Start geprüft, dass die CA in der `.p12` steckt. Hast du ihn übersprungen, hol ihn jetzt
nach, bevor du scannst.

Dann im Browser öffnen:

```
http://localhost:5000
```

**Was du sehen musst:** eine Seite mit einem QR-Code und „Waiting for wallet…".

---

## 10. Scannen

1. **Am Computer** die Seite mit dem QR-Code offen lassen.
2. **Am Handy** die deutsche EUDI-Wallet öffnen und den QR-Code scannen.
3. Die Wallet zeigt, wer fragt und welche Daten — **bestätigen**.
4. Am Computer wechselt die Seite auf `Verified ✓` und zeigt die Attribute.

Im Terminal stehen dann zwei Zeilen — eine von der Bibliothek, eine vom Sample:

```
info: Session … completed: credential valid
      (issuer SERIALNUMBER=…, CN=pp_pid_auth_crt, O=Bundesdruckerei GmbH, C=DE, key via x5c)
info: Identity received: Erika Mustermann, born 1964-08-12
```

In der ersten sind `credential valid` und `key via x5c` die Wörter, auf die es ankommt: die
Präsentation wurde geprüft, und der Aussteller wurde über die mitgeschickte Zertifikatskette als
vertrauenswürdig erkannt. Die zweite ist der Beweis, dass die Attribute auch bei dir angekommen
und zugeordnet sind.

Ging etwas schief, steht statt der zweiten Zeile eine `warn`-Zeile mit **Code und Meldung**, zum
Beispiel `Presentation refused: mdoc_digest_mismatch: …`. Das ist ein Unterschied zum gehosteten
Statusendpunkt der Bibliothek, der Meldungen absichtlich zurückhält: hier bekommst du sie, weil das
Sample sie direkt aus der Antwort liest.

---

## 11. Was danach auf dem Handy passiert

**Auf dem Handy öffnet sich ein Browser** und zeigt eine Seite mit den Attributen. Das ist richtig
so und kein Fehler — es ist nur ungewohnt, weil das Referenz-Backend das nicht macht.

Kurz: die Bibliothek antwortet auf **jede** verarbeitete Antwort mit einer Rücksprungadresse, damit
eine Wallet nicht am Antwortformat erkennen kann, ob sie auf einem oder auf zwei Geräten arbeitet.

Für eine Sekunde steht dort vielleicht „No presentation is waiting to be confirmed" — das liest sich
wie ein Fehler, ist aber erwartet, und die Statuszeile korrigiert sich sofort.

Was genau dort angezeigt wird, was diese Oberfläche preisgibt und wie du stattdessen deine eigene
Seite ausliefern kannst, steht in der
[README des Samples](README.md#a-page-opens-on-the-phone-afterwards).

---

## Wenn etwas schiefgeht

| Was du siehst | Was es bedeutet | Was zu tun ist |
|---|---|---|
| `Configuration 'GermanPid:…' is missing` | eine Variable aus Schritt 8 fehlt | Name prüfen — zwei Unterstriche, Groß-/Kleinschreibung |
| `No issuer trust anchor found in '…'` | Ordner leer oder falscher Pfad | [Schritt 6](#6-trust-anchors--schon-fertig) |
| Start bricht mit `has no private key` ab | die `.p12` enthält kein Schlüsselmaterial | neu exportieren, **mit** privatem Schlüssel |
| Start bricht mit `password` / `MAC verify failure` ab | Passwort falsch | Passwort prüfen |
| `1 certificate(s) in x5c` im Log | CA fehlt in der `.p12` | [Schritt 5](#5-den-keystore-prüfen--der-schritt-der-einen-testlauf-spart) |
| Wallet: `Could not trust certificate chain` | drei Ursachen sehen gleich aus: `x5c` unvollständig, Zertifikat abgelaufen, oder AIA/CRL im Zertifikat falsch | erst [Schritt 5](#5-den-keystore-prüfen--der-schritt-der-einen-testlauf-spart), dann `openssl x509 -noout -dates` |
| Wallet lehnt die **Signatur** ab, obwohl Start und Kette gut aussehen | selten: der private Schlüssel in der `.p12` passt nicht zum Zertifikat darin | siehe Kasten unter dieser Tabelle |
| Wallet zeigt **keinen** Zustimmungsdialog, bricht gleich ab | du fragst mehr Attribute, als das Registrierungszertifikat erlaubt | Attributliste prüfen, siehe README „Requesting different attributes" |
| Wallet: nichts passiert, Timeout | sie erreicht deine Adresse nicht | Tunnel noch offen? `PublicBaseUrl` genau die Tunnel-Adresse, mit `https`, ohne `/` am Ende? |
| Wallet beschwert sich über `http` | hinter einem Proxy/Tunnel sieht die Anwendung nur `http` | im Sample ist `UseForwardedHeaders` schon aktiv; prüfe, dass `PublicBaseUrl` mit `https://` beginnt |
| `status_invalid` | der Widerrufsstatus konnte nicht geprüft werden | im Sample ist die Prüfung aus, du solltest das nicht sehen. Falls doch: README „Revocation is not checked" — und **nicht** versuchen, es mit weiteren Trust Anchors zu beheben, das hilft nachweislich nicht |
| Seite zeigt nach dem Scan weiter „Waiting" | die Antwort kam nicht an oder wurde abgelehnt | Terminal lesen: dort steht der Grund |

> **Passen Schlüssel und Zertifikat zusammen?** Die `.p12` ist ein Behälter mit drei Dingen: dem
> privaten Schlüssel (verschlüsselt), deinem Zertifikat und dem der CA. Normalerweise passen
> Schlüssel und Zertifikat zueinander — wenn nicht, startet alles sauber und erst die Wallet
> verwirft die Signatur. Prüfen, ohne den Schlüssel anzuschauen: beide Zeilen müssen denselben
> Hash ausgeben.
>
> ```bash
> openssl pkcs12 -in keystore.p12 -clcerts -nokeys \
>   | openssl x509 -noout -pubkey | openssl sha256
>
> openssl pkcs12 -in keystore.p12 -nocerts -nodes \
>   | openssl pkey -pubout | openssl sha256
> ```
>
> Der zweite Befehl entschlüsselt den privaten Schlüssel — er wird nur durch die Pipe geschickt und
> nirgends gespeichert. Leite ihn **nicht** mit `>` in eine Datei um, sonst liegt der Schlüssel
> unverschlüsselt auf der Platte.

Mehr Details im Log:

```bash
# Linux/macOS/Git Bash
export Logging__LogLevel__Tessio=Debug
# PowerShell
$env:Logging__LogLevel__Tessio = "Debug"
```

---

## Variante: mit Docker statt .NET

Wenn du kein .NET installieren willst. Im Ordner liegen `Dockerfile` und `docker-compose.yml`; die
Compose-Datei startet den Tunnel gleich mit.

```bash
# 1. Bauen
docker build -t germanpid-tessio-verifier:test .

# 2. Tunnel starten und die Adresse aus seinem Log ablesen
docker compose -p pidtest up -d tunnel
docker compose -p pidtest logs tunnel | grep trycloudflare

# 3. Verifier mit dieser Adresse starten
KEYSTORE_PATH=/pfad/zu/keystore.p12 \
TRUST_ANCHOR_DIR=$PWD/trust-anchors \
PUBLIC_BASE_URL=https://…trycloudflare.com \
DE_KEYSTORE_PASSWORD=dein-passwort \
DE_REGISTRATION_CERTIFICATE="eyJ0…" \
docker compose -p pidtest up -d verifier

# 4. Log ansehen
docker compose -p pidtest logs -f verifier
```

Zwei Schritte, weil die Tunnel-Adresse zufällig ist und der Verifier sie kennen muss, **bevor** die
Wallet sie zu sehen bekommt.

Besser als Passwörter in die Kommandozeile zu tippen: eine Datei `.env` daneben legen (mit
`DE_KEYSTORE_PASSWORD=…` und `DE_REGISTRATION_CERTIFICATE=…`) und `--env-file .env` übergeben. Dann
landet nichts in der Shell-History.

---

## In die eigene Anwendung einbauen

Das Sample ist absichtlich eigenständig und hängt an genau einem Paket. Für eine eigene Anwendung
nimm stattdessen `miEUDIverifier.Core` ab Version 2.0.0 — dort steckt derselbe Weg, aber als
Bibliothek, mit der Attributzuordnung auf ein `IdentityData`-Objekt.

```bash
dotnet add package miEUDIverifier.Core
```

```csharp
using miEUDIverifier.Services;
using miEUDIverifier.Services.Tessio;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInProcessVerifier(new InProcessVerifierOptions
{
    // Die Adresse, die das HANDY erreicht — nicht localhost.
    PublicBaseUrl = "https://verifier.example.com",

    // Die .p12 mit privatem Schlüssel, deinem Zertifikat UND dessen CA.
    AccessCertificatePath = "/certs/keystore.p12",
    AccessCertificatePassword = builder.Configuration["KeystorePassword"],

    // Unverändert, Byte für Byte.
    RegistrationCertificate = builder.Configuration["RegistrationCertificate"],

    // Der Ordner aus Schritt 6.
    IssuerTrustAnchorDirectory = "/trust-anchors",
});

var app = builder.Build();

app.UseForwardedHeaders();    // hinter Proxy/Tunnel: sonst wird eine http-Adresse verschickt
app.MapInProcessVerifier();   // die Endpunkte, die die Wallet aufruft

// Eine Prüfung starten und auf das Ergebnis warten:
app.MapGet("/pruefen", async (TessioVerifierEngine verifier) =>
{
    var tx = await verifier.InitializeTransactionAsync();

    // Das kommt in den QR-Code:
    var deepLink = tx.BuildWalletDeepLink();
    var qrPng    = QrCodeService.GeneratePng(deepLink);

    // Blockiert, bis die Wallet geantwortet hat oder die Sitzung abläuft.
    var antwort  = await verifier.WaitForWalletResponseAsync(tx.TransactionId);
    var identitaet = await verifier.ExtractIdentityDataAsync(antwort, options: null);

    // ACHTUNG: null bedeutet "kein Credential des angefragten Typs" — das ist etwas
    // anderes als "Daten unvollständig". Beides zu unterscheiden ist der Sinn des Feldes.
    if (identitaet.CredentialType is null)
        return Results.Conflict("Die Antwort trug kein Credential des angefragten Typs.");

    return Results.Ok(new { identitaet.GivenName, identitaet.FamilyName, identitaet.BirthDate });
});

app.Run();
```

Zwei Dinge, die du dabei wissen solltest:

- **Das Paket verlangt ab 2.0.0 das ASP.NET-Core-Framework**, weil es HTTP-Endpunkte hostet. Eine
  Web-Anwendung merkt davon nichts; eine Konsolenanwendung braucht die ASP.NET-Core-Runtime.
- **Der Widerrufsstatus wird auf diesem Weg nicht geprüft.** Bestätigt wird, dass der Ausweis
  *gültig ausgestellt* wurde, nicht, dass er *noch gültig* ist. Warum das so ist und was die
  Alternative ist, steht in `InProcessVerifierOptions.CheckRevocation`. Sag deinen Nutzern, was du
  prüfst und was nicht.

---

**Geschrieben für:** jemanden, der das erste Mal mit EUDI-Wallets arbeitet. Wenn du die Protokolle
kennst, ist die [README des Samples](README.md) der kürzere Weg.
