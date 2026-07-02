using QRCoder;

namespace miEUDIverifier.Services;

/// <summary>
/// Generates QR codes from a URL string.
/// </summary>
public static class QrCodeService
{
    /// <summary>Returns a QR code as raw PNG bytes (for embedding as base64).</summary>
    public static byte[] GeneratePng(string content, int pixelsPerModule = 5)
    {
        var generator = new QRCodeGenerator();
        var data      = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var qrCode    = new PngByteQRCode(data);
        return qrCode.GetGraphic(pixelsPerModule);
    }
}
