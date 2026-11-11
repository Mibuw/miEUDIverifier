# Schritt für Schritt: einen deutschen PID auslesen, ohne Backend

Diese Anleitung führt von „nichts installiert" bis „die Daten stehen auf dem Bildschirm".
Jeder Befehl ist zum Kopieren gedacht. Nach jedem Schritt steht, **was du sehen musst** — wenn
dort etwas anderes steht, lies nicht weiter, sondern schau in [Wenn etwas schiefgeht](#wenn-etwas-schiefgeht).

Du klonst dafür **nicht** dieses Repository. Du legst ein eigenes, leeres Web-Projekt an und holst
ein NuGet-Paket hinein — genau so, wie du es später in deine richtige Anwendung einbaust. Dieses
Repository brauchst du nur, falls du zum Vergleich hineinsehen willst.

Am Ende hast du: eine Webseite mit QR-Code, die du mit der deutschen EUDI-Wallet scannst, und die
danach Name, Geburtsdatum und Geburtsort aus dem Ausweis anzeigt. Dazwischen läuft **kein**
Verifier-Backend, kein Java, kein zweiter Container.

Rechne mit 30–45 Minuten, wenn du die Zertifikate schon hast.

---

## Inhalt

1. [Was du brauchst](#1-was-du-brauchst)
2. [.NET installieren](#2-net-installieren)
3. [Projekt anlegen und das Paket holen](#3-projekt-anlegen-und-das-paket-holen)
4. [Die zwei Zertifikate besorgen](#4-die-zwei-zertifikate-besorgen)
   - [4b. Nur PEM-Dateien? Die `.p12` selbst bauen](#4b-du-hast-nur-pem-dateien-dann-baust-du-die-p12-selbst)
5. [Den Keystore prüfen — der Schritt, der einen Testlauf spart](#5-den-keystore-prüfen--der-schritt-der-einen-testlauf-spart)
6. [Trust Anchors holen](#6-trust-anchors-holen)
7. [Von außen erreichbar werden](#7-von-außen-erreichbar-werden)
8. [Konfigurieren](#8-konfigurieren)
9. [Starten](#9-starten)
10. [Scannen](#10-scannen)
11. [Was danach auf dem Handy passiert](#11-was-danach-auf-dem-handy-passiert)
12. [Wenn etwas schiefgeht](#wenn-etwas-schiefgeht)
13. [Einbau in eine bestehende Web-Anwendung](#einbau-in-eine-bestehende-web-anwendung)

---

## 1. Was du brauchst

| | Was | Woher |
|---|---|---|
| ☐ | Ein Rechner mit Windows, macOS oder Linux | — |
| ☐ | .NET SDK 8 oder neuer | [Schritt 2](#2-net-installieren) |
| ☐ | `openssl` und `curl` | bei macOS/Linux dabei; bei Windows in Git Bash enthalten |
| ☐ | Kein Docker, kein Git, kein Klon dieses Repositorys | — |
| ☐ | Ein Smartphone mit der **deutschen EUDI-Wallet** und einem **Test-PID** darin | SPRIND-Sandbox |
| ☐ | Das **Zugriffszertifikat** als `.p12` (plus Passwort, falls eines gesetzt ist) — oder Zertifikat und Schlüssel als PEM, dann [Schritt 4b](#4b-du-hast-nur-pem-dateien-dann-baust-du-die-p12-selbst) | [Schritt 4](#4-die-zwei-zertifikate-besorgen) |
| ☐ | Das **Registrierungszertifikat** (ein langer Text in einer Zeile) | [Schritt 4](#4-die-zwei-zertifikate-besorgen) |
| ☐ | Die **Trust Anchors** des Ausstellers | zwei Befehle, [Schritt 6](#6-trust-anchors-holen) |

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

## 3. Projekt anlegen und das Paket holen

Kein Klon, kein Build dieses Repositorys. Du legst ein leeres Web-Projekt an und holst ein Paket:

```bash
dotnet new web -o MeinPidVerifier
cd MeinPidVerifier
dotnet add package miEUDIverifier.Core
```

**Was du sehen musst:** `Wiederherstellung erfolgreich.` und in der `.csproj` eine Zeile
`<PackageReference Include="miEUDIverifier.Core" Version="2.1.0" />` (oder neuer).

Das ist **ein** Paket, nicht zwei. `Tessio.Verifier`, das die Protokollarbeit macht, kommt als
Abhängigkeit mit — du musst es nicht hinzufügen und nicht direkt benutzen.

> **Falls du eine Version vor 2.1.0 erwischst:** setze in der `.csproj`
> `<TargetFramework>net8.0</TargetFramework>`. Ältere Pakete liefern nur net8.0, und ein
> net10.0-Projekt baut damit fehlerfrei, stürzt aber beim Start ab:
> `Could not load file or assembly 'Microsoft.Bcl.Cryptography'`. Ab 2.1.0 liefert das Paket beide
> Zielframeworks und du kannst das Zielframework lassen, wie das Template es gesetzt hat.

### Den Code einfügen

Ersetze den **gesamten** Inhalt von `Program.cs` durch das Folgende. Mehr braucht es nicht — und
die Hälfte davon ist die Anzeige:

```csharp
using miEUDIverifier.Models;
using miEUDIverifier.Services;
using miEUDIverifier.Services.Tessio;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInProcessVerifier(new InProcessVerifierOptions
{
    // Die Adresse, die das HANDY erreicht. Nicht localhost.
    PublicBaseUrl = builder.Configuration["PUBLIC_BASE_URL"]!,

    // Deine .p12 mit privatem Schlüssel, deinem Zertifikat UND dessen CA.
    AccessCertificatePath = builder.Configuration["KEYSTORE"]!,
    AccessCertificatePassword = builder.Configuration["KEYSTORE_PASSWORD"],

    // Unverändert, Byte für Byte.
    RegistrationCertificate = builder.Configuration["REGISTRATION_CERTIFICATE"],

    // Der Ordner mit den CA-Zertifikaten des Ausstellers.
    IssuerTrustAnchorDirectory = builder.Configuration["TRUST_ANCHORS"]!,
});

var app = builder.Build();

app.UseForwardedHeaders();   // hinter Proxy oder Tunnel: sonst wird eine http-Adresse verschickt
app.MapInProcessVerifier();  // die Endpunkte, die die WALLET aufruft

// Eine Prüfung zur Zeit. Für einen Testlauf reicht das; in einer echten Anwendung gehört die
// Transaktions-ID in die Sitzung des Benutzers.
string? transactionId = null;
IdentityData? identity = null;
string? fehler = null;

app.MapGet("/", async (TessioVerifierEngine verifier) =>
{
    if (transactionId is null)
    {
        var tx = await verifier.InitializeTransactionAsync();
        transactionId = tx.TransactionId;

        var qr = Convert.ToBase64String(QrCodeService.GeneratePng(tx.BuildWalletDeepLink()));

        // WaitForWalletResponseAsync blockiert, bis die Wallet antwortet oder die Sitzung
        // abläuft — deshalb im Hintergrund und nicht im Anfrage-Thread.
        _ = Task.Run(async () =>
        {
            var antwort = await verifier.WaitForWalletResponseAsync(tx.TransactionId);
            identity = await verifier.ExtractIdentityDataAsync(antwort, options: null);

            if (antwort.Error is { Length: > 0 })
            {
                fehler = $"{antwort.Error}: {antwort.ErrorDescription}";
            }
            else if (identity.CredentialType is null)
            {
                // Das ist NICHT dasselbe wie "Daten unvollständig": die Antwort trug kein
                // Credential des angefragten Typs, es wurde nichts übernommen.
                fehler = "Die Antwort trug kein Credential des angefragten Typs.";
            }
        });

        return Results.Content($"""
            <!doctype html><meta charset="utf-8">
            <meta http-equiv="refresh" content="2">
            <p>QR-Code mit der Wallet scannen:</p>
            <img src="data:image/png;base64,{qr}" width="280">
            """, "text/html; charset=utf-8");
    }

    if (fehler is not null)
    {
        return Results.Content(
            $"<!doctype html><meta charset=\"utf-8\"><p>Nicht geprüft: {fehler}</p>",
            "text/html; charset=utf-8");
    }

    if (identity is null)
    {
        return Results.Content("""
            <!doctype html><meta charset="utf-8"><meta http-equiv="refresh" content="2">
            <p>Warte auf die Wallet…</p>
            """, "text/html; charset=utf-8");
    }

    return Results.Content($"""
        <!doctype html><meta charset="utf-8">
        <p><b>{identity.GivenName} {identity.FamilyName}</b><br>
        geboren am {identity.BirthDate} in {identity.PlaceOfBirth}<br>
        Staatsangehörigkeit {identity.Nationality}</p>
        """, "text/html; charset=utf-8");
});

app.Run();
```

Prüfe, dass es baut:

```bash
dotnet build
```

**Was du sehen musst:** `Der Buildvorgang wurde erfolgreich ausgeführt.` und `0 Fehler`. Starten
kannst du noch nicht — die vier Werte aus der Konfiguration fehlen, und darum gehen die nächsten
Schritte.

> Die Anzeige hier ist absichtlich primitiv: sie lädt sich alle zwei Sekunden selbst neu, statt per
> JavaScript abzufragen. Eine ansehnlichere Variante steht in [`Program.cs`](Program.cs) dieses
> Samples, eine mit Sitzungen pro Browser in `src/miEUDIverifier/` dieses Repositorys.

---

## 4. Die zwei Zertifikate besorgen

Beide kommen aus der **SPRIND-Sandbox**: <https://sandbox.eudi-wallet.org/>

Dort registrierst du dich als Relying Party (Prüfstelle) und bekommst:

**(1) Das Zugriffszertifikat** — es beweist der Wallet, *wer* fragt.
Du brauchst es als PKCS#12-Datei (Endung `.p12` oder `.pfx`) **mit privatem Schlüssel**, plus das
Passwort dazu. Lege einen Ordner dafür an, der **außerhalb** deines Projekts liegt — damit er
nicht versehentlich in einem Repository oder Container-Image landet:

```bash
mkdir -p ~/eudi-zertifikate        # Windows: %USERPROFILE%\eudi-zertifikate
# die .p12 dorthin kopieren, z.B. als ~/eudi-zertifikate/keystore.p12
```

**(2) Das Registrierungszertifikat** — es sagt, *welche Attribute* du fragen darfst.
Das ist ein JWT: ein langer Text aus drei Blöcken, getrennt durch Punkte, ungefähr so:

```
eyJ0eXAiOiJyYy13cnArand0IiwiYWxnIjoi…  .  eyJzdWIiOiJ…  .  MEUCIQDf…
```

Speichere es in eine Datei, **eine Zeile, kein Umbruch, keine Leerzeichen**:

Speichere es in `~/eudi-zertifikate/registration-certificate.txt` — unter Windows mit Notepad,
„Speichern unter", Kodierung UTF-8, **ohne** Zeilenumbruch am Ende.

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
[Schritt 8](#8-konfigurieren) die Variable `KEYSTORE_PASSWORD` **gar nicht**
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
openssl pkcs12 -in ~/eudi-zertifikate/keystore.p12 -nokeys -info 2>/dev/null \
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
openssl pkcs12 -in ~/eudi-zertifikate/keystore.p12 -nokeys 2>/dev/null \
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

## 6. Trust Anchors holen

Zwei öffentliche Zertifikate des deutschen PID-Test-Ausstellers (D-Trust). Zwei Befehle:

```bash
mkdir -p ~/eudi-zertifikate/trust-anchors
cd ~/eudi-zertifikate/trust-anchors

base="http://www.d-trust.net/cgi-bin"
curl -sSL --fail -o root.crt   "$base/Deutschland_PID-Provider_Root_Test_CA_1_2026.crt"
curl -sSL --fail -o signer.crt "$base/Deutschland_PID-Signer_Test_CA_1-26-1_2026.crt"
```

`-L` ist nötig, weil `www.d-trust.net` auf `www1.d-trust.net` weiterleitet. Plain HTTP ist für ein
CA-Verzeichnis normal und hier unbedenklich, weil du die Dateien im nächsten Schritt
kryptografisch prüfst.

**Was du sehen musst** — prüfe, dass es die richtigen sind, und dass die eine von der anderen
signiert wurde:

```bash
openssl x509 -inform DER -in root.crt   -noout -subject
openssl x509 -inform DER -in signer.crt -noout -subject

openssl x509 -inform DER -in root.crt   -out .r.pem
openssl x509 -inform DER -in signer.crt -out .s.pem
openssl verify -CAfile .r.pem .s.pem
rm -f .r.pem .s.pem
```

Erwartet:

```
subject=C=DE, O=D-Trust GmbH, CN=Deutschland PID-Provider Root Test CA 1 2026
subject=C=DE, O=D-Trust GmbH, CN=Deutschland PID-Signer Test CA 1-26-1 2026
.s.pem: OK
```

Die Adressen sind nicht geraten: jedes Zertifikat nennt das nächste darüber in seiner eigenen
Erweiterung `authorityInfoAccess`. Nachsehen kannst du mit
`openssl x509 -inform DER -in signer.crt -noout -text | grep -A2 "Authority Information Access"`.

> Es gibt noch ein drittes, die **PID-Status-List-Signer Test CA 1-26-2**. Für diesen Testlauf
> brauchst du es nicht, weil die Widerrufsprüfung aus ist — und es behebt den Fehler
> `status_invalid` auch **nicht**, obwohl er genau dazu verleitet. Warum, steht in der
> [README dieses Samples](README.md#revocation-is-not-checked).
>
> Fertig heruntergeladen, mit Fingerprints zum Vergleichen, liegen alle drei auch in
> [`trust-anchors/`](trust-anchors/) dieses Repositorys — samt
> [`LIESMICH.txt`](trust-anchors/LIESMICH.txt) und dem Script
> [`fetch-trust-anchors.sh`](fetch-trust-anchors.sh), das genau die Befehle von oben macht und die
> Ketten selbst prüft. Für den Weg in dieser Anleitung brauchst du das Repository aber nicht.

Das sind **Test**-Anchors. Ein Produktivbetrieb nimmt die Produktiv-Hierarchie und holt sie aus der
veröffentlichten Trust List, nicht über `authorityInfoAccess`.

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
$env:PUBLIC_BASE_URL          = "https://irgendwas-zufaelliges.trycloudflare.com"
$env:KEYSTORE                 = "$env:USERPROFILE\eudi-zertifikate\keystore.p12"
$env:KEYSTORE_PASSWORD        = "dein-p12-passwort"      # weglassen, wenn keines gesetzt ist
$env:REGISTRATION_CERTIFICATE = Get-Content -Raw "$env:USERPROFILE\eudi-zertifikate\registration-certificate.txt"
$env:TRUST_ANCHORS            = "$env:USERPROFILE\eudi-zertifikate\trust-anchors"
```

**macOS / Linux / Git Bash:**

```bash
export PUBLIC_BASE_URL="https://irgendwas-zufaelliges.trycloudflare.com"
export KEYSTORE="$HOME/eudi-zertifikate/keystore.p12"
export KEYSTORE_PASSWORD="dein-p12-passwort"             # weglassen, wenn keines gesetzt ist
export REGISTRATION_CERTIFICATE="$(cat "$HOME/eudi-zertifikate/registration-certificate.txt")"
export TRUST_ANCHORS="$HOME/eudi-zertifikate/trust-anchors"
```

Diese fünf Namen stehen genau so in dem `Program.cs`, das du in
[Schritt 3](#3-projekt-anlegen-und-das-paket-holen) eingefügt hast — ASP.NET Core liest
Umgebungsvariablen von sich aus in die Konfiguration. In einer richtigen Anwendung nimmst du
stattdessen `appsettings.json` für die Pfade und den Secret-Store deiner Umgebung für Passwort und
Registrierungszertifikat; wie das aussieht, steht unter
[Einbau in eine bestehende Web-Anwendung](#einbau-in-eine-bestehende-web-anwendung).

| Variable | Bedeutung |
|---|---|
| `PUBLIC_BASE_URL` | die Tunnel-Adresse aus Schritt 7, **ohne** Schrägstrich am Ende |
| `KEYSTORE` | die `.p12` aus Schritt 4 |
| `KEYSTORE_PASSWORD` | deren Passwort, oder weglassen, wenn die Datei keines hat |
| `REGISTRATION_CERTIFICATE` | der Inhalt der Textdatei aus Schritt 4, nicht ihr Pfad |
| `TRUST_ANCHORS` | der Ordner aus Schritt 6 |

---

## 9. Starten

```bash
dotnet run
```

**Was du sehen musst** — drei Zeilen, in dieser Reihenfolge:

```
info: In-process verifier hosted under /verify: request objects at /verify/jar/{id},
      wallet callback at https://…trycloudflare.com/verify/callback, revocation checking OFF.
warn: Revocation is NOT checked on the in-process path: a revoked PID is accepted.
info: Now listening on: http://localhost:5135
```

Prüfe daran zwei Dinge:

- **Die Adresse hinter `wallet callback at`** muss deine Tunnel-Adresse sein und mit `https`
  beginnen. Steht dort `http` oder `localhost`, stimmt `PUBLIC_BASE_URL` nicht — die Wallet würde
  die Antwort verweigern. Das ist der häufigste Fehler an dieser Stelle.
- **Die `warn`-Zeile zur Revocation gehört dort hin.** Sie ist kein Fehler, sondern der Hinweis auf
  die eine Lücke dieses Weges, siehe die [README dieses Samples](README.md#revocation-is-not-checked).

Die Anzahl der Zertifikate im `x5c` steht hier **nicht** — das leitet das Paket selbst ab. Deshalb
war [Schritt 5](#5-den-keystore-prüfen--der-schritt-der-einen-testlauf-spart) wichtig: dort hast du
vor dem Start geprüft, dass die CA in der `.p12` steckt. Hast du ihn übersprungen, hol ihn jetzt
nach, bevor du scannst.

Dann im Browser die Adresse aus der letzten Zeile öffnen — **`dotnet new web` legt einen zufälligen
Port in `Properties/launchSettings.json` ab**, bei dir steht dort also wahrscheinlich eine andere
Nummer als 5135. Das ist der Port, auf den der Tunnel aus
[Schritt 7](#7-von-außen-erreichbar-werden) zeigen muss; stimmen die beiden nicht überein, erreicht
die Wallet nichts. Entweder passt du den Tunnel an oder du trägst in `launchSettings.json`
`"applicationUrl": "http://localhost:5000"` ein.

**Was du sehen musst:** eine Seite mit einem QR-Code und „QR-Code mit der Wallet scannen:".

---

## 10. Scannen

1. **Am Computer** die Seite mit dem QR-Code offen lassen.
2. **Am Handy** die deutsche EUDI-Wallet öffnen und den QR-Code scannen.
3. Die Wallet zeigt, wer fragt und welche Daten — **bestätigen**.
4. Am Computer zeigt die Seite nach höchstens zwei Sekunden Name, Geburtsdatum und Geburtsort.

Im Terminal steht dann:

```
info: Session … completed: credential valid
      (issuer SERIALNUMBER=…, CN=pp_pid_auth_crt, O=Bundesdruckerei GmbH, C=DE, key via x5c)
```

`credential valid` und `key via x5c` sind die Wörter, auf die es ankommt: die Präsentation wurde
geprüft, und der Aussteller wurde über die mitgeschickte Zertifikatskette als vertrauenswürdig
erkannt.

Ging etwas schief, zeigt die Seite `Nicht geprüft:` mit **Code und Meldung**, zum Beispiel
`mdoc_digest_mismatch: …`. Das ist ein Unterschied zum gehosteten Statusendpunkt der Bibliothek,
der Meldungen absichtlich zurückhält: der eingefügte Code liest sie direkt aus
`envelope.ErrorDescription`.

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
| `'PublicBaseUrl' is required` o. ä. | eine Variable aus Schritt 8 fehlt oder ist leer | Namen und Groß-/Kleinschreibung prüfen; in derselben Shell starten, in der du sie gesetzt hast |
| `Could not load file or assembly 'Microsoft.Bcl.Cryptography'` beim Start | net10.0-Projekt mit einem Paket vor 2.1.0, das nur net8.0 liefert | auf `miEUDIverifier.Core` 2.1.0 aktualisieren, oder `<TargetFramework>net8.0</TargetFramework>` setzen |
| Wallet erreicht nichts, Seite bleibt beim QR-Code | Tunnel zeigt auf einen anderen Port als die Anwendung | `dotnet new web` vergibt einen zufälligen Port, siehe [Schritt 9](#9-starten) |
| `No issuer trust anchor found in '…'` | Ordner leer oder falscher Pfad | [Schritt 6](#6-trust-anchors-holen) |
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


## Einbau in eine bestehende Web-Anwendung

Hier geht es nicht mehr um das Sample, sondern um deine eigene Anwendung — eine, die es schon gibt,
mit eigenen Seiten, eigener Anmeldung und eigenem Hosting. Du brauchst **kein Docker**, keinen
zweiten Dienst und keinen eigenen Prozess. Es kommt ein Paket dazu und drei Zeilen Code.

### Ein Paket, nicht zwei

```bash
dotnet add package miEUDIverifier.Core
```

Das ist alles. `Tessio.Verifier` kommt als Abhängigkeit mit — du musst es **nicht** separat
hinzufügen und auch nicht direkt benutzen. Was du davon brauchst, ist hinter
`miEUDIverifier.Core` versteckt.

Eine Voraussetzung: das Paket verlangt ab 2.0.0 das **ASP.NET-Core-Framework**, weil es
HTTP-Endpunkte hostet. Eine Web-Anwendung erfüllt das von sich aus.

### Drei Zeilen in `Program.cs`

```csharp
using miEUDIverifier.Services.Tessio;

// 1. Registrieren — am besten aus der Konfiguration gebunden, siehe unten.
builder.Services.AddInProcessVerifier(
    builder.Configuration.GetSection("Pid").Get<InProcessVerifierOptions>()!);

var app = builder.Build();

// 2. Nur falls deine App hinter einem Reverse Proxy oder einer Lastverteilung steht.
//    Ohne das baut die Bibliothek eine http-Adresse, die die Wallet ablehnt.
app.UseForwardedHeaders();

// 3. Die Endpunkte, die die WALLET aufruft. Nicht deine Oberfläche.
app.MapInProcessVerifier();
```

Das fügt deiner Anwendung diese Routen hinzu, unter dem Präfix aus `RoutePrefix` (Standard
`/verify`):

| Route | Wer ruft das auf |
|---|---|
| `GET {prefix}/jar/{id}` | die Wallet, um die signierte Anfrage zu holen |
| `POST {prefix}/callback` | die Wallet, um die verschlüsselte Antwort abzuliefern |
| `GET {prefix}/{session}` und `/stream` | Statusabfrage |
| `GET {prefix}/{session}/return` | die Wallet schickt das Handy dorthin |
| `GET {prefix}/start` | wird auf `/` umgeleitet, siehe unten |

Wähle das Präfix so, dass es mit nichts in deiner Anwendung kollidiert.

### Die Konfiguration gehört in `appsettings.json`

```json
{
  "Pid": {
    "PublicBaseUrl": "https://deine-app.example.com",
    "RoutePrefix": "/verify",
    "AccessCertificatePath": "/certs/keystore.p12",
    "IssuerTrustAnchorDirectory": "/trust-anchors",
    "RequestedClaims": [ "family_name", "given_name", "birth_date" ]
  }
}
```

Passwort und Registrierungszertifikat **nicht** hierhin — die kommen aus Umgebungsvariablen oder
dem Secret-Store deiner Umgebung:

```csharp
var pid = builder.Configuration.GetSection("Pid").Get<InProcessVerifierOptions>()!;
pid.AccessCertificatePassword = builder.Configuration["KEYSTORE_PASSWORD"];
pid.RegistrationCertificate   = builder.Configuration["REGISTRATION_CERTIFICATE"];
builder.Services.AddInProcessVerifier(pid);
```

### Zwei Dinge, die eine bestehende Anwendung anders machen als ein Sample

Beide sind beim Einbau in eine App mit Anmeldung aufgefallen, nicht im Sample — und beide sind in
`miEUDIverifier.Core` **2.0.1** behoben. Mit einer älteren Version musst du sie selbst behandeln.

**Deine Anmeldung darf die Wallet nicht aussperren.** Eine Wallet kann sich nicht einloggen. Hat
deine Anwendung eine globale Regel „jeder Endpunkt braucht einen angemeldeten Benutzer" —
`options.FallbackPolicy = … RequireAuthenticatedUser()`, eine verbreitete und ansonsten sinnvolle
Einstellung — dann antwortet sie der Wallet mit `302` auf die Login-Seite, und es gibt keine
Präsentation. Seit 2.0.1 sind die Wallet-Endpunkte als `AllowAnonymous` markiert; deine eigenen
Seiten bleiben geschützt. Das ist keine Aufweichung: diese Endpunkte sind die
Protokolloberfläche zur Wallet und von Haus aus ohne Anmeldung gedacht.

**Die Attributliste aus der Konfiguration ersetzt den Standard, sie ergänzt ihn nicht.** Das klingt
selbstverständlich, war es aber nicht: der .NET-Konfigurationsbinder **hängt an** eine
Collection-Eigenschaft an, die schon Werte enthält. Drei Attribute in `appsettings.json` ergaben
deshalb eine Anfrage über zehn — und mehr zu fragen, als das Registrierungszertifikat erlaubt,
lässt die Wallet die Anfrage ohne Zustimmungsdialog verwerfen. Seit 2.0.1 ist die Eigenschaft
`null`-fähig ohne Vorbelegung, es gilt also genau, was du konfigurierst. Prüfe es beim ersten Start
im Log oder im Request-Objekt, wenn du von einer älteren Version kommst.

### Eine Prüfung starten, von deinem Code aus

```csharp
app.MapPost("/pruefung/start", async (TessioVerifierEngine verifier) =>
{
    var tx = await verifier.InitializeTransactionAsync();

    // Das kommt in den QR-Code, oder als Link auf demselben Gerät:
    var deepLink = tx.BuildWalletDeepLink();
    var qrPng    = QrCodeService.GeneratePng(deepLink);

    // tx.TransactionId merken — damit fragst du das Ergebnis ab.
    return Results.Ok(new { tx.TransactionId, deepLink });
});
```

Auf das Ergebnis warten. `WaitForWalletResponseAsync` blockiert bis die Wallet antwortet oder die
Sitzung abläuft, also **nicht** im Anfrage-Thread aufrufen, sondern im Hintergrund und das Ergebnis
dort ablegen, wo deine Oberfläche es abfragen kann:

```csharp
var envelope = await verifier.WaitForWalletResponseAsync(transactionId, ct: stoppingToken);
var identity = await verifier.ExtractIdentityDataAsync(envelope, options: null, ct: stoppingToken);

if (envelope.Error is { Length: > 0 })
{
    // Code UND Meldung — der gehostete Statusendpunkt hält die Meldung bewusst zurück,
    // hier bekommst du sie.
    logger.LogWarning("Abgelehnt: {Code} {Meldung}", envelope.Error, envelope.ErrorDescription);
}
else if (identity.CredentialType is null)
{
    // Wichtig: das ist NICHT dasselbe wie "Daten unvollständig". Die Antwort trug kein
    // Credential des angefragten Typs, es wurde nichts übernommen.
}
else
{
    // identity.FamilyName, .GivenName, .BirthDate, .PlaceOfBirth, .Nationality, …
}
```

In einer Anwendung mit mehreren Benutzern gehört die Transaktions-ID in die Sitzung des Benutzers,
nicht in ein Feld. Wie das aussieht, zeigt `src/miEUDIverifier/` in diesem Repository: dort hängt
sie an einem Cookie, und ein Hintergrundlauf pro Sitzung schreibt das Ergebnis hinein.

### Was du nicht vergessen darfst

- **Eine öffentlich erreichbare HTTPS-Adresse.** Hat deine Anwendung schon, sonst wäre sie nicht
  erreichbar — `PublicBaseUrl` ist genau diese Adresse. Nur zum lokalen Testen brauchst du den
  Tunnel aus [Schritt 7](#7-von-außen-erreichbar-werden).
- **Die zwei Zertifikate und die Trust Anchors** müssen zur Laufzeit erreichbar sein, also auf dem
  Server liegen oder eingehängt werden — nicht ins Repository und nicht ins Container-Image.
- **Der Widerrufsstatus wird nicht geprüft.** Bestätigt wird, dass der Ausweis *gültig ausgestellt*
  wurde, nicht, dass er *noch gültig* ist. Sag deinen Nutzern, was du prüfst. Die Begründung steht
  in `InProcessVerifierOptions.CheckRevocation`.
- **`{prefix}/start` wird auf `/` umgeleitet.** Das ist die Demo-Startseite der Bibliothek, die in
  einer fremden Anwendung nicht funktionieren kann. Wenn `/` bei dir auf das Präfix weiterleitet,
  hast du eine Endlosschleife — leite stattdessen auf deine eigene Prüfseite.
