using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScoreStudio.Rendering;

/// <summary>
/// The original page pictures of a recognised score as WPF images (docs/OMR_COMPARE.md): as they are, for side by
/// side, or as coloured ink on a transparent layer, for laying over the score.
/// </summary>
public static class SourcePageImages
{
    /// <summary>Wider pictures are scaled down: enough for 2× zoom, without holding page-sized 300 dpi bitmaps.</summary>
    public const int MaxWidth = 1600;

    private static readonly ConditionalWeakTable<byte[], BitmapSource> PlainCache = new();
    private static readonly ConditionalWeakTable<byte[], Dictionary<Color, BitmapSource>> InkCache = new();

    /// <summary>The picture in grey, scaled down to <see cref="MaxWidth"/>.</summary>
    public static BitmapSource Plain(byte[] png)
    {
        lock (PlainCache)
        {
            if (PlainCache.TryGetValue(png, out var cached)) return cached;
            var image = Gray(png);
            PlainCache.AddOrUpdate(png, image);
            return image;
        }
    }

    /// <summary>The picture's dark parts in <paramref name="ink"/>, the light parts transparent (alpha = darkness).</summary>
    public static BitmapSource Ink(byte[] png, Color ink)
    {
        lock (InkCache)
        {
            var colours = InkCache.GetValue(png, _ => new Dictionary<Color, BitmapSource>());
            if (colours.TryGetValue(ink, out var cached)) return cached;

            var gray = Plain(png);
            var (width, height) = (gray.PixelWidth, gray.PixelHeight);
            var source = new byte[width * height];
            gray.CopyPixels(source, width, 0);
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < source.Length; i++)
            {
                pixels[i * 4] = ink.B;
                pixels[i * 4 + 1] = ink.G;
                pixels[i * 4 + 2] = ink.R;
                pixels[i * 4 + 3] = (byte)(255 - source[i]);
            }

            var image = BitmapSource.Create(width, height, gray.DpiX, gray.DpiY, PixelFormats.Bgra32, null, pixels, width * 4);
            image.Freeze();
            colours[ink] = image;
            return image;
        }
    }

    private static BitmapSource Gray(byte[] png)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.PixelWidth > MaxWidth)
        {
            var scale = (double)MaxWidth / frame.PixelWidth;
            frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }

        var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        // Copied into memory so the stream and the converters are not kept alive.
        var bytes = new byte[gray.PixelWidth * gray.PixelHeight];
        gray.CopyPixels(bytes, gray.PixelWidth, 0);
        var image = BitmapSource.Create(gray.PixelWidth, gray.PixelHeight, 96, 96, PixelFormats.Gray8, null, bytes, gray.PixelWidth);
        image.Freeze();
        return image;
    }
}
