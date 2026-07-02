using miEUDIverifier.Models;
using FluentAssertions;
using Xunit;

namespace miEUDIverifier.Tests.Models;

/// <summary>
/// Tests für <see cref="InitTransactionResponse.BuildWalletDeepLink"/>.
///
/// Der Deep-Link ist der Inhalt des QR-Codes: er wird vom EUDI-Wallet
/// gescannt und enthält alle Informationen um die Präsentation zu starten.
/// Format: openid4vp://?client_id=...&request_uri=...&request_uri_method=...
/// </summary>
public class InitTransactionResponseTests
{
    [Fact]
    public void BuildWalletDeepLink_ReturnsCorrectSchemeAndParameters()
    {
        // Arrange
        var response = new InitTransactionResponse
        {
            TransactionId    = "tx-123",
            ClientId         = "x509_san_dns:verifier-backend.eudiw.dev",
            RequestUri       = "https://verifier-backend.eudiw.dev/wallet/request.jwt/abc123",
            RequestUriMethod = "post",
        };

        // Act
        var deepLink = response.BuildWalletDeepLink();

        // Assert
        deepLink.Should().StartWith("openid4vp://",
            because: "das ist das Standard-URI-Schema für OpenID4VP");
        deepLink.Should().Contain("client_id=",
            because: "die Wallet muss den Client identifizieren können");
        deepLink.Should().Contain("request_uri=",
            because: "die Wallet lädt den Autorisierungsrequest von dieser URL");
        deepLink.Should().Contain("request_uri_method=post",
            because: "die Anfrage soll per POST gestellt werden");
    }

    [Fact]
    public void BuildWalletDeepLink_UrlEncodesSpecialCharacters_InClientId()
    {
        // Arrange: Client-ID enthält Doppelpunkt der URL-kodiert werden muss
        var response = new InitTransactionResponse
        {
            ClientId         = "x509_san_dns:verifier.example.com",
            RequestUri       = "https://verifier.example.com/wallet/request.jwt/xyz",
            RequestUriMethod = "get",
        };

        // Act
        var deepLink = response.BuildWalletDeepLink();

        // Assert: Doppelpunkt muss als %3A codiert sein
        deepLink.Should().Contain("%3A",
            because: "der Doppelpunkt in 'x509_san_dns:...' muss URL-kodiert werden");
    }

    [Fact]
    public void BuildWalletDeepLink_UsesCustomScheme_WhenProvided()
    {
        // Arrange
        var response = new InitTransactionResponse
        {
            ClientId         = "verifier",
            RequestUri       = "https://x.com/jwt/abc",
            RequestUriMethod = "post",
        };

        // Act
        var eudi   = response.BuildWalletDeepLink("eudi-openid4vp");
        var haip   = response.BuildWalletDeepLink("haip-vp");
        var openid = response.BuildWalletDeepLink("openid4vp");

        // Assert: Schema ist konfigurierbar (je nach Wallet-Implementierung)
        eudi.Should().StartWith("eudi-openid4vp://");
        haip.Should().StartWith("haip-vp://");
        openid.Should().StartWith("openid4vp://");
    }

    [Fact]
    public void BuildWalletDeepLink_EncodesRequestUri_ContainingSpecialChars()
    {
        // Arrange: Request-URI mit Sonderzeichen
        var response = new InitTransactionResponse
        {
            ClientId         = "client",
            RequestUri       = "https://verifier.example.com/wallet/request.jwt/abc+def==",
            RequestUriMethod = "post",
        };

        // Act
        var deepLink = response.BuildWalletDeepLink();
        var uri      = new Uri(deepLink.Replace("openid4vp://", "https://dummy.invalid/"));

        // Assert: request_uri-Parameter darf keine uncodierten Sonderzeichen enthalten
        var requestUriParam = System.Web.HttpUtility.ParseQueryString(uri.Query)["request_uri"];
        requestUriParam.Should().Be("https://verifier.example.com/wallet/request.jwt/abc+def==",
            because: "URL-Dekodierung muss den Original-Wert reproduzieren");
    }
}
