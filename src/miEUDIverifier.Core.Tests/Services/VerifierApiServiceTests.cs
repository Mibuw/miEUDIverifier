using System.Net;
using System.Text;
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
/// Tests for <see cref="VerifierApiService"/>.
///
/// The VerifierApiService is the heart of the library – it:
///   1. Initializes an OpenID4VP transaction at the EUDI verifier backend
///   2. Polls for the wallet response
///   3. Extracts identity data (family name, given name, birth date) from the VP token
/// </summary>
public class VerifierApiServiceTests
{
    // ── Helper methods ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a VerifierApiService with a controlled HTTP handler.
    /// All real network calls are intercepted.
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
            PollIntervalSeconds = 0,   // No waiting between polls in tests
            PollTimeoutSeconds  = 2,   // Short timeout for the timeout tests
        });
        return new VerifierApiService(httpClient, options, NullLogger<VerifierApiService>.Instance);
    }

    // ── InitializeTransactionAsync ────────────────────────────────────────────

    [Fact]
    public async Task InitializeTransactionAsync_ReturnsTransactionId_WhenBackendRespondsOk()
    {
        // Arrange: backend responds with a valid transaction
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
        // Arrange: intercept the HTTP request and inspect the body
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

        // Assert: the DCQL query that was sent must contain all three PID fields
        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("mso_mdoc",            because: "PID wird als mDoc übertragen");
        capturedBody.Should().Contain("eu.europa.ec.eudi.pid.1", because: "das ist der PID-Namespace");
        capturedBody.Should().Contain("family_name",         because: "Familienname wird angefragt");
        capturedBody.Should().Contain("given_name",          because: "Vorname wird angefragt");
        capturedBody.Should().Contain("birth_date",          because: "Geburtsdatum wird angefragt");
        capturedBody.Should().Contain("dc+sd-jwt",           because: "SD-JWT VC wird als zweite Option angefragt");
        capturedBody.Should().Contain("urn:eudi:pid:1",      because: "vct der SD-JWT VC PID");
        capturedBody.Should().Contain("demo.pid-issuer.bundesdruckerei.de",
            because: "die deutsche EUDI Wallet (bdr-PID) wird als dritte Option angefragt");
        capturedBody.Should().Contain("birthdate",
            because: "die bdr-PID nutzt den OIDC-Claim-Namen 'birthdate'");
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
        // Arrange: no IssuerChain configured → must not appear in the request
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

        // issuer_chain: null or a missing field causes an InvalidIssuerChain error
        // → must not be serialized
        capturedBody.Should().NotContain("issuer_chain",
            because: "leerer Chain wird weggelassen (JsonIgnore WhenWritingNull)");
    }

    // ── ExtractIdentityDataAsync ──────────────────────────────────────────────

    [Fact]
    public async Task ExtractIdentityDataAsync_ExtractsAllThreeFields_FromDecodedCredentials()
    {
        // Arrange: backend returns already decoded credentials (format A)
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
        // Arrange: vp_token is an object with base64-encoded CBOR data (format B)
        // The utility endpoint /utilities/validations/msoMdoc/deviceResponse gets called
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

        // vp_token as an object: { "credential-id": ["base64-DeviceResponse"] }
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
        // Arrange: birth date arrives as a Unix timestamp in milliseconds
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

        // Assert: the timestamp must be converted into a readable ISO date
        identity.BirthDate.Should().Be("1976-09-24",
            because: "212371200000ms entspricht dem 24. September 1976");
    }

    [Fact]
    public async Task ExtractIdentityDataAsync_ConvertsNegativeUnixTimestampMillis_ToBirthDateBefore1970()
    {
        // Arrange: birth date before 1970 → negative Unix timestamp in milliseconds
        // -80870400000 ms = 1967-06-10 (regression test: magnitude decides ms vs. s)
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

        // Assert: a negative timestamp must not fall through as the raw value
        identity.BirthDate.Should().Be("1967-06-10",
            because: "-80870400000ms entspricht dem 10. Juni 1967");
    }

    [Fact]
    public async Task ExtractIdentityDataAsync_ParsesSdJwtVc_FromVpTokenString()
    {
        // Arrange: build an SD-JWT VC presentation "<jwt>~<disclosure>~<disclosure>~<disclosure>~"
        static string B64Url(byte[] b) =>
            Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        static string Disclosure(string name, string value) =>
            B64Url(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new object[] { "salt", name, value })));

        var header  = B64Url(Encoding.UTF8.GetBytes("""{"alg":"ES256","typ":"dc+sd-jwt"}"""));
        var payload = B64Url(Encoding.UTF8.GetBytes("""{"vct":"urn:eudi:pid:1"}"""));
        var sdJwt   = $"{header}.{payload}.sig"
                    + "~" + Disclosure("family_name", "Mitterbucher")
                    + "~" + Disclosure("given_name",  "Wolfgang")
                    + "~" + Disclosure("birth_date",  "1967-06-10")
                    + "~";

        // vp_token is an object keyed by the DCQL credential id → SD-JWT presentation string
        using var doc = JsonDocument.Parse(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["pid-sdjwt"] = sdJwt }));
        var envelope = new WalletResponseEnvelope { VpToken = doc.RootElement };

        // The mso_mdoc utility endpoint must NOT be called for an SD-JWT presentation.
        var service = CreateService(_ =>
            throw new InvalidOperationException("No HTTP call expected for SD-JWT parsing"));

        // Act
        var identity = await service.ExtractIdentityDataAsync(envelope);

        // Assert: selectively disclosed values are read from the disclosures
        identity.FamilyName.Should().Be("Mitterbucher");
        identity.GivenName.Should().Be("Wolfgang");
        identity.BirthDate.Should().Be("1967-06-10");
        identity.CredentialFormat.Should().Be("dc+sd-jwt");
        identity.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task ExtractIdentityDataAsync_ReturnsIncompleteIdentity_WhenVpTokenIsEmpty()
    {
        // Arrange
        var service = CreateService(_ => HttpResponseFactory.NotFound());
        var envelope = new WalletResponseEnvelope();  // no VpToken, no credentials

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
        // Arrange: first poll returns "requested", second returns "submitted"
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
        // Arrange: backend returns no "status" field but a vp_token
        // (some backend versions use this format)
        var service = CreateService(_ => HttpResponseFactory.Ok("""
            {"vp_token": {"cred-id": ["dGVzdA=="]}}
            """));

        // Act
        var envelope = await service.WaitForWalletResponseAsync("tx-002");

        // Assert: the response is detected even without an explicit "status" field
        envelope.HasVpToken.Should().BeTrue(
            because: "vp_token-Präsenz signalisiert eine fertige Antwort");
    }

    [Fact]
    public async Task WaitForWalletResponseAsync_ThrowsTimeout_WhenNoResponseWithinLimit()
    {
        // Arrange: backend always responds with 404 (wallet has not reacted yet)
        var service = CreateService(_ => HttpResponseFactory.NotFound(),
            new VerifierSettings
            {
                BackendUrl          = "https://x.com",
                PollIntervalSeconds = 0,
                PollTimeoutSeconds  = 1,  // very short timeout for this test
            });

        // Act & Assert
        await service.Invoking(s => s.WaitForWalletResponseAsync("tx-timeout"))
            .Should().ThrowAsync<TimeoutException>();
    }
}
