using miEUDIverifier.Services;
using FluentAssertions;
using Xunit;

namespace miEUDIverifier.Tests.Services;

/// <summary>
/// Tests für <see cref="QrCodeService"/>.
/// Der QR-Code-Service wandelt den Wallet-Deep-Link in ein PNG-Bild um,
/// das auf der Webseite angezeigt und vom Nutzer mit der EUDI Wallet gescannt wird.
/// </summary>
public class QrCodeServiceTests
{
    // Ein typischer OpenID4VP Deep-Link wie er im QR-Code landet
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

        // Assert: PNG-Dateien beginnen immer mit der PNG-Signatur (8 Bytes)
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
        // Ein höherer pixelsPerModule-Wert ergibt ein größeres Bild
        var small = QrCodeService.GeneratePng(SampleDeepLink, pixelsPerModule: 2);
        var large = QrCodeService.GeneratePng(SampleDeepLink, pixelsPerModule: 8);

        large.Length.Should().BeGreaterThan(small.Length,
            because: "mehr Pixel pro Modul ergibt ein größeres Bild");
    }

    [Fact]
    public void GeneratePng_ProducesDeterministicOutput_ForSameInput()
    {
        // Zwei Aufrufe mit demselben Input sollen dasselbe Bild erzeugen
        var first  = QrCodeService.GeneratePng(SampleDeepLink);
        var second = QrCodeService.GeneratePng(SampleDeepLink);

        first.Should().BeEquivalentTo(second,
            because: "der QR-Code ist für denselben Inhalt immer gleich");
    }
}
