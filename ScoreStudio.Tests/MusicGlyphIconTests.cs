using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScoreStudio.Rendering;

namespace ScoreStudio.Tests;

/// <summary>The note-length toolbar icons draw their Bravura symbol inside their box, with nothing cut off.</summary>
public sealed class MusicGlyphIconTests
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

    /// <summary>The icons as MainWindow.xaml sets them up: glyph, baseline, size, width.</summary>
    public static TheoryData<string, string, double, double, double> Icons() => new()
    {
        { "whole", "\uE1D2", 0.5, 20, 20 },
        { "half", "\uE1D3", 0.85, 20, 20 },
        { "quarter", "\uE1D5", 0.85, 20, 20 },
        { "eighth", "\uE1D7", 0.85, 20, 20 },
        { "16th", "\uE1D9", 0.85, 20, 20 },
        { "32nd", "\uE1DB", 0.85, 20, 20 },
        { "dot", "\uE1E7", 0.5, 44, 12 },
        { "quarter rest", "\uE4E5", 0.5, 20, 20 },
    };

    [Theory]
    [MemberData(nameof(Icons))]
    public void Icon_IsDrawnInsideItsBox(string name, string glyph, double baseline, double size, double width)
    {
        RunSta(() =>
        {
            Assert.NotNull(MusicFont.Typeface);
            var icon = new MusicGlyphIcon { Glyph = glyph, Baseline = baseline, GlyphSize = size, Width = width };
            var (pixels, w, h) = Render(icon, 4);

            bool Dark(int x, int y) => pixels[(y * w + x) * 4 + 3] > 64;
            var ink = 0;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    if (Dark(x, y)) ink++;
            Assert.True(ink > 20, $"{name}: only {ink} dark pixels");

            // A clipped symbol would touch the edge of the box.
            for (var x = 0; x < w; x++) Assert.False(Dark(x, 0) || Dark(x, h - 1), $"{name}: touches the top or bottom");
            for (var y = 0; y < h; y++) Assert.False(Dark(0, y) || Dark(w - 1, y), $"{name}: touches the left or right");
        });
    }

    [Fact]
    public void Toolbar_IconsArePreviewedAsPng()
    {
        RunSta(() =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White };
            foreach (var row in Icons())
            {
                panel.Children.Add(new Border
                {
                    BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Margin = new Thickness(2),
                    Child = new MusicGlyphIcon { Glyph = (string)row[1], Baseline = (double)row[2], GlyphSize = (double)row[3], Width = (double)row[4] },
                });
            }

            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth * 4), (int)Math.Ceiling(panel.ActualHeight * 4), 384, 384, PixelFormats.Pbgra32);
            bitmap.Render(panel);

            var directory = Environment.GetEnvironmentVariable("SCORESTUDIO_PREVIEW_DIR") ?? Path.Combine(AppContext.BaseDirectory, "preview");
            Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, "preview-input-toolbar.png"));
            encoder.Save(stream);
        });
    }

    /// <summary>Renders the element at <paramref name="scale"/> times 96 dpi; returns BGRA pixels (alpha shows ink on a clear background).</summary>
    private static (byte[] Pixels, int Width, int Height) Render(FrameworkElement element, int scale)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        var (w, h) = ((int)Math.Ceiling(element.Width * scale), (int)Math.Ceiling(element.Height * scale));
        var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        return (pixels, w, h);
    }
}
