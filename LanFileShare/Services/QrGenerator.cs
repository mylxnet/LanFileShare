using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using QRCoder;

namespace LanFileShare.Services;

/// <summary>
/// 用 QRCoder 生成二维码位图。
/// 输出 PNG 字节数组，WPF 通过 BitmapImage 显示。
/// </summary>
public static class QrGenerator
{
    public static byte[] GeneratePng(string content, int size = 240)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new QRCode(data);

        // 只用 1 参数版本（默认黑色 / 白色 / 自动留白边距），
        // 避免与 GetGraphic(int, Color, Color, bool) 和带 icon 的 8 参数版本冲突。
        using var bmp = qrCode.GetGraphic(pixelsPerModule: Math.Max(2, size / 50));

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
