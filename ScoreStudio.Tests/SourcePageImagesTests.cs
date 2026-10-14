using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScoreStudio.Rendering;

namespace ScoreStudio.Tests;

/// <summary>The original page pictures shown next to or over a recognised score (docs/OMR_COMPARE.md).</summary>
public sealed class SourcePageImagesTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>A grey PNG like Audiveris' sheet pictures.</summary>
    private static byte[] GrayPng(int width, int height, Func<int, int, byte> gray)
    {
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) pixels[y * width + x] = gray(x, y);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 300, 300, PixelFormats.Gray8, null, pixels, width)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void Ink_ColoursTheDarkParts_AndLeavesTheLightPartsTransparent()
    {
        RunSta(() =>
        {
            var png = GrayPng(3, 1, (x, _) => x switch { 0 => 0, 1 => 128, _ => 255 });
            var ink = SourcePageImages.Ink(png, Color.FromRgb(200, 20, 30));
            Assert.Equal(PixelFormats.Bgra32, ink.Format);
            var pixels = new byte[3 * 4];
            ink.CopyPixels(pixels, 3 * 4, 0);
            Assert.Equal(new byte[] { 30, 20, 200, 255 }, pixels[..4]); // black: the ink, solid
            Assert.Equal(new byte[] { 30, 20, 200, 127 }, pixels[4..8]); // grey: the ink, half see-through
            Assert.Equal(0, pixels[11]); // white: transparent
            Assert.True(ink.IsFrozen);
            Assert.Same(ink, SourcePageImages.Ink(png, Color.FromRgb(200, 20, 30)));
        });
    }

    [Fact]
    public void WidePictures_AreScaledDown_AndKeepTheirShape()
    {
        RunSta(() =>
        {
            var png = GrayPng(SourcePageImages.MaxWidth * 2, 40, (x, _) => (byte)(x < SourcePageImages.MaxWidth ? 0 : 255));
            var plain = SourcePageImages.Plain(png);
            Assert.Equal(SourcePageImages.MaxWidth, plain.PixelWidth);
            Assert.Equal(20, plain.PixelHeight);
            var pixels = new byte[plain.PixelWidth * plain.PixelHeight];
            plain.CopyPixels(pixels, plain.PixelWidth, 0);
            Assert.True(pixels[10 * plain.PixelWidth + 100] < 50);
            Assert.True(pixels[10 * plain.PixelWidth + plain.PixelWidth - 100] > 200);
        });
    }
}
