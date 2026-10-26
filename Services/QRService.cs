using QRCoder;

namespace MenuScan.Services
{
    public interface IQRService
    {
        string GenerateQrCodeDataUri(string text);
        byte[] GenerateQrCodePngBytes(string text);
    }

    public class QRService : IQRService
    {
        public string GenerateQrCodeDataUri(string text)
        {
            var bytes = GenerateQrCodePngBytes(text);
            return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        }

        public byte[] GenerateQrCodePngBytes(string text)
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            using var qrCode = new PngByteQRCode(qrCodeData);
            return qrCode.GetGraphic(20);
        }
    }
}
