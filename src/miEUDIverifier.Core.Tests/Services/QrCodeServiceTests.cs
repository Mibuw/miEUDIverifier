using miEUDIverifier.Services;
using FluentAssertions;
using Xunit;

namespace miEUDIverifier.Tests.Services;

/// <summary>
/// Tests for <see cref="QrCodeService"/>.
/// The QR code service converts the wallet deep link into a PNG image
/// that is shown on the web page and scanned by the user with the EUDI Wallet.
/// </summary>
public class QrCodeServiceTests
{
    // A typical OpenID4VP deep link as it ends up in the QR code
    private const string SampleDeepLink =
        "openid4vp://?client_id=x509_san_dns%3Averifier-backend.eudiw.dev" +
        "&request_uri=https%3A%2F%2Fverifier-backend.eudiw.dev%2Fwallet%2Frequest.jwt%2Fabc123" +
        "&request_uri_method=post";

    [Fact]
    public void GeneratePng_ReturnsNonEmptyByteArray_ForValidInput()
    {
        // Act
        var png = QrCodeService.GeneratePng(SampleDeepLink);

        // Assert
        png.Should().NotBeNull();
        png.Should().NotBeEmpty(because: "ein gültiger QR-Code muss Bilddaten erzeugen");
    }

    [Fact]
    public void GeneratePng_ReturnsPngFile_WithCorrectMagicBytes()
    {
        // Act
        var png = QrCodeService.GeneratePng(SampleDeepLink);

        // Assert: PNG files always start with the PNG signature (8 bytes)
        // 0x89 0x50 0x4E 0x47 0x0D 0x0A 0x1A 0x0A
        png.Should().HaveCountGreaterThan(8);
        png[0].Should().Be(0x89, because: "PNG-Signatur Byte 1");
        png[1].Should().Be(0x50, because: "PNG-Signatur Byte 2 ('P')");
        png[2].Should().Be(0x4E, because: "PNG-Signatur Byte 3 ('N')");
        png[3].Should().Be(0x47, because: "PNG-Signatur Byte 4 ('G')");
    }

    [Fact]
    public void GeneratePng_ReturnsDifferentSizes_ForDifferentPixelsPerModule()
    {
        // A higher pixelsPerModule value produces a larger image
        var small = QrCodeService.GeneratePng(SampleDeepLink, pixelsPerModule: 2);
        var large = QrCodeService.GeneratePng(SampleDeepLink, pixelsPerModule: 8);

        large.Length.Should().BeGreaterThan(small.Length,
            because: "mehr Pixel pro Modul ergibt ein größeres Bild");
    }

    [Fact]
    public void GeneratePng_ProducesDeterministicOutput_ForSameInput()
    {
        // Two calls with the same input should produce the same image
        var first  = QrCodeService.GeneratePng(SampleDeepLink);
        var second = QrCodeService.GeneratePng(SampleDeepLink);

        first.Should().BeEquivalentTo(second,
            because: "der QR-Code ist für denselben Inhalt immer gleich");
    }
}
