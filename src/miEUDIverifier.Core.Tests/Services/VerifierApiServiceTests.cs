using System.Net;
using System.Text.Json;
using miEUDIverifier.Configuration;
using miEUDIverifier.Models;
using miEUDIverifier.Services;
using miEUDIverifier.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace miEUDIverifier.Tests.Services;

/// <summary>
/// Tests für <see cref="VerifierApiService"/>.
///
/// Der VerifierApiService ist das Herzstück der Library – er:
///   1. Initialisiert eine OpenID4VP-Transaction beim EUDI-Verifier-Backend
///   2. Pollt auf die Wallet-Antwort
///   3. Extrahiert Identitätsdaten (Name, Vorname, Geburtsdatum) aus dem VP-Token
/// </summary>
public class VerifierApiServiceTests
{
    // ── Hilfsmethoden ─────────────────────────────────────────────────────────

    /// <summary>
    /// Erstellt einen VerifierApiService mit einem kontrollierten HTTP-Handler.
    /// Alle echten Netzwerkaufrufe werden abgefangen.
    /// </summary>
    private static VerifierApiService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        VerifierSettings? settings = null)
    {
        var mockHandler = new MockHttpMessageHandler(handler);
        var httpClient  = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://verifier-backend.eudiw.dev/")
        };
        var options = Options.Create(settings ?? new VerifierSettings
        {
            BackendUrl         = "https://verifier-backend.eudiw.dev",
            PollIntervalSeconds = 0,   // Kein Warten zwischen Polls in Tests
            PollTimeoutSeconds  = 2,   // Kurzer Timeout für Timeout-Tests
        });
        return new VerifierApiService(httpClient, options, NullLogger<VerifierApiService>.Instance);
    }

    // ── InitializeTransactionAsync ────────────────────────────────────────────

    [Fact]
    public async Task InitializeTransactionAsync_ReturnsTransactionId_WhenBackendRespondsOk()
    {
        // Arrange: Backend antwortet mit einer gültigen Transaction
        var service = CreateService(_ => HttpResponseFactory.Ok("""
            {
              "transaction_id": "abc-123-xyz",
              "client_id":      "x509_san_dns:verifier-backend.eudiw.dev",
              "request_uri":    "https://verifier-backend.eudiw.dev/wallet/request.jwt/abc",
              "request_uri_method": "post"
            }
            """));

        // Act
        var result = await service.InitializeTransactionAsync();

        // Assert
        result.TransactionId.Should().Be("abc-123-xyz");
        result.ClientId.Should().Contain("verifier-backend");
        result.RequestUri.Should().StartWith("https://");
    }

    [Fact]
    public async Task InitializeTransactionAsync_SendsPidDcqlQuery_WithRequiredClaims()
    {
        // Arrange: HTTP-Request abfangen und Body prüfen
        string? capturedBody = null;
        var service = CreateService(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return HttpResponseFactory.Ok("""
                { "transaction_id": "t1", "client_id": "c1",
                  "request_uri": "https://x.com/r", "request_uri_method": "post" }
                """);
        });

        // Act
        await service.InitializeTransactionAsync();

        // Assert: Der gesendete DCQL-Query muss alle drei PID-Felder enthalten
        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("mso_mdoc",            because: "PID wird als mDoc übertragen");
        capturedBody.Should().Contain("eu.europa.ec.eudi.pid.1", because: "das ist der PID-Namespace");
        capturedBody.Should().Contain("family_name",         because: "Familienname wird angefragt");
        capturedBody.Should().Contain("given_name",          because: "Vorname wird angefragt");
        capturedBody.Should().Contain("birth_date",          because: "Geburtsdatum wird angefragt");
    }

    [Fact]
    public async Task InitializeTransactionAsync_Throws_WhenBackendReturnsBadRequest()
    {
        // Arrange
        var service = CreateService(_ => HttpResponseFactory.BadRequest(
            """{"error":"UnsupportedFormat"}"""));

        // Act & Assert
        await service.Invoking(s => s.InitializeTransactionAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*BadRequest*");
    }

    [Fact]
    public async Task InitializeTransactionAsync_DoesNotSendIssuerChain_WhenNotConfigured()
    {
        // Arrange: kein IssuerChain konfiguriert → darf nicht im Request erscheinen
        string? capturedBody = null;
        var service = CreateService(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return HttpResponseFactory.Ok("""
                { "transaction_id": "t1", "client_id": "c1",
                  "request_uri": "https://x.com/r", "request_uri_method": "post" }
                """);
        }, new VerifierSettings { IssuerChain = null });

        await service.InitializeTransactionAsync();

        // issuer_chain: null oder fehlendes Feld verursachen InvalidIssuerChain-Fehler
        // → darf nicht serialisiert werden
        capturedBody.Should().NotContain("issuer_chain",
            because: "leerer Chain wird weggelassen (JsonIgnore WhenWritingNull)");
    }

    // ── ExtractIdentityDataAsync ──────────────────────────────────────────────

    [Fact]
    public async Task ExtractIdentityDataAsync_ExtractsAllThreeFields_FromDecodedCredentials()
    {
        // Arrange: Backend gibt bereits dekodierte Credentials zurück (Format A)
        var service = CreateService(_ => HttpResponseFactory.NotFound());

        var attributes = JsonDocument.Parse("""
            {
              "eu.europa.ec.eudi.pid.1": {
                "family_name": "Mitterbucher",
                "given_name":  "Wolfgang",
                "birth_date":  "1976-09-24"
              }
            }
            """).RootElement;

        var envelope = new WalletResponseEnvelope
        {
            Status = "submitted",
            Credentials = new List<VerifiedCredential>
            {
                new() { Format = "mso_mdoc", Attributes = attributes }
            }
        };

        // Act
        var identity = await service.ExtractIdentityDataAsync(envelope);

        // Assert
        identity.FamilyName.Should().Be("Mitterbucher");
        identity.GivenName.Should().Be("Wolfgang");
        identity.BirthDate.Should().Be("1976-09-24");
        identity.IsComplete.Should().BeTrue();
        identity.CredentialFormat.Should().Be("mso_mdoc");
    }

    [Fact]
    public async Task ExtractIdentityDataAsync_CallsUtilityEndpoint_ForVpTokenObject()
    {
        // Arrange: vp_token ist ein Objekt mit base64-codierten CBOR-Daten (Format B)
        // Der Utility-Endpunkt /utilities/validations/msoMdoc/deviceResponse wird aufgerufen
        bool utilityEndpointCalled = false;

        var service = CreateService(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("deviceResponse"))
            {
                utilityEndpointCalled = true;
                return HttpResponseFactory.Ok("""
                    [{
                      "docType": "eu.europa.ec.eudi.pid.1",
                      "attributes": {
                        "eu.europa.ec.eudi.pid.1": {
                          "family_name": "Musterfrau",
                          "given_name":  "Maria",
                          "birth_date":  "1985-03-15"
                        }
                      }
                    }]
                    """);
            }
            return HttpResponseFactory.NotFound();
        });

        // vp_token als Objekt: { "credential-id": ["base64-DeviceResponse"] }
        using var doc = JsonDocument.Parse("""{"cred-id-mdoc": ["dGVzdA=="]}""");
        var envelope = new WalletResponseEnvelope { VpToken = doc.RootElement };

        // Act
        var identity = await service.ExtractIdentityDataAsync(envelope);

        // Assert
        utilityEndpointCalled.Should().BeTrue(
            because: "mDoc-CBOR muss über den Utility-Endpunkt dekodiert werden");
        identity.FamilyName.Should().Be("Musterfrau");
        identity.GivenName.Should().Be("Maria");
        identity.BirthDate.Should().Be("1985-03-15");
        identity.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task ExtractIdentityDataAsync_ConvertsUnixTimestampMillis_ToBirthDateString()
    {
        // Arrange: Geburtsdatum kommt als Unix-Timestamp in Millisekunden
        // 212371200000 ms = 1976-09-24
        var service = CreateService(req =>
            HttpResponseFactory.Ok("""
                [{
                  "docType": "eu.europa.ec.eudi.pid.1",
                  "attributes": {
                    "eu.europa.ec.eudi.pid.1": {
                      "family_name": "Test",
                      "given_name":  "User",
                      "birth_date":  212371200000
                    }
                  }
                }]
                """));

        using var doc = JsonDocument.Parse("""{"cred": ["dGVzdA=="]}""");
        var envelope = new WalletResponseEnvelope { VpToken = doc.RootElement };

        // Act
        var identity = await service.ExtractIdentityDataAsync(envelope);

        // Assert: Timestamp muss in lesbares ISO-Datum umgewandelt werden
        identity.BirthDate.Should().Be("1976-09-24",
            because: "212371200000ms entspricht dem 24. September 1976");
    }

    [Fact]
    public async Task ExtractIdentityDataAsync_ConvertsNegativeUnixTimestampMillis_ToBirthDateBefore1970()
    {
        // Arrange: Geburtsdatum vor 1970 → negativer Unix-Timestamp in Millisekunden
        // -80870400000 ms = 1967-06-10 (Regressionstest: Betrag entscheidet ms vs. s)
        var service = CreateService(req =>
            HttpResponseFactory.Ok("""
                [{
                  "docType": "eu.europa.ec.eudi.pid.1",
                  "attributes": {
                    "eu.europa.ec.eudi.pid.1": {
                      "family_name": "Test",
                      "given_name":  "User",
                      "birth_date":  -80870400000
                    }
                  }
                }]
                """));

        using var doc = JsonDocument.Parse("""{"cred": ["dGVzdA=="]}""");
        var envelope = new WalletResponseEnvelope { VpToken = doc.RootElement };

        // Act
        var identity = await service.ExtractIdentityDataAsync(envelope);

        // Assert: negativer Timestamp darf nicht als Rohwert durchfallen
        identity.BirthDate.Should().Be("1967-06-10",
            because: "-80870400000ms entspricht dem 10. Juni 1967");
    }

    [Fact]
    public async Task ExtractIdentityDataAsync_ReturnsIncompleteIdentity_WhenVpTokenIsEmpty()
    {
        // Arrange
        var service = CreateService(_ => HttpResponseFactory.NotFound());
        var envelope = new WalletResponseEnvelope();  // kein VpToken, keine Credentials

        // Act
        var identity = await service.ExtractIdentityDataAsync(envelope);

        // Assert
        identity.IsComplete.Should().BeFalse();
        identity.FamilyName.Should().BeNull();
        identity.GivenName.Should().BeNull();
        identity.BirthDate.Should().BeNull();
    }

    // ── WaitForWalletResponseAsync ────────────────────────────────────────────

    [Fact]
    public async Task WaitForWalletResponseAsync_ReturnsEnvelope_WhenStatusIsSubmitted()
    {
        // Arrange: erster Poll gibt "requested", zweiter "submitted" zurück
        var callCount = 0;
        var service   = CreateService(_ =>
        {
            callCount++;
            var status = callCount == 1 ? "requested" : "submitted";
            return HttpResponseFactory.Ok($$"""{"status":"{{status}}"}""");
        });

        // Act
        var envelope = await service.WaitForWalletResponseAsync("tx-001");

        // Assert
        envelope.IsSubmitted.Should().BeTrue();
        callCount.Should().BeGreaterThanOrEqualTo(2,
            because: "mindestens zwei Polls nötig (erst 'requested', dann 'submitted')");
    }

    [Fact]
    public async Task WaitForWalletResponseAsync_DetectsResponse_WhenVpTokenPresent()
    {
        // Arrange: Backend gibt kein "status"-Feld, aber einen vp_token
        // (manche Backend-Versionen nutzen dieses Format)
        var service = CreateService(_ => HttpResponseFactory.Ok("""
            {"vp_token": {"cred-id": ["dGVzdA=="]}}
            """));

        // Act
        var envelope = await service.WaitForWalletResponseAsync("tx-002");

        // Assert: Response wird erkannt, auch ohne explizites "status"-Feld
        envelope.HasVpToken.Should().BeTrue(
            because: "vp_token-Präsenz signalisiert eine fertige Antwort");
    }

    [Fact]
    public async Task WaitForWalletResponseAsync_ThrowsTimeout_WhenNoResponseWithinLimit()
    {
        // Arrange: Backend antwortet immer mit 404 (Wallet hat noch nicht reagiert)
        var service = CreateService(_ => HttpResponseFactory.NotFound(),
            new VerifierSettings
            {
                BackendUrl          = "https://x.com",
                PollIntervalSeconds = 0,
                PollTimeoutSeconds  = 1,  // sehr kurzer Timeout für den Test
            });

        // Act & Assert
        await service.Invoking(s => s.WaitForWalletResponseAsync("tx-timeout"))
            .Should().ThrowAsync<TimeoutException>();
    }
}
