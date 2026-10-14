using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScoreStudio.Recognition;

namespace ScoreStudio.RecognitionUi;

/// <summary>Reads any picture Windows can decode (PNG, JPEG, BMP, GIF, TIFF) as grayscale, transparency over white.</summary>
public sealed class WpfImageReader : IImageReader
{
    public GrayImage Read(string path)
    {
        BitmapFrame frame;
        using (var stream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }

        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var (width, height) = (bgra.PixelWidth, bgra.PixelHeight);
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var (b, g, r, a) = (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3] / 255.0);
            var luma = 0.299 * r + 0.587 * g + 0.114 * b;
            gray[i] = (byte)Math.Clamp(Math.Round(luma * a + 255 * (1 - a)), 0, 255);
        }

        return new GrayImage(width, height, gray);
    }
}
