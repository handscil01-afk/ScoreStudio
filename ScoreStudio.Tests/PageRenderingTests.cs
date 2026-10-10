using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Rendering;
using ScoreStudio.Services;

namespace ScoreStudio.Tests;

public sealed class PageRenderingTests
{
    private const double Dpi = 150;

    /// <summary>WPF needs a single-threaded apartment; xUnit test threads are not STA.</summary>
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

    private static ScoreLayout SampleLayout() =>
        ScoreLayoutEngine.Layout(ProjectFileService.Load(Path.Combine(AppContext.BaseDirectory, "samples", "sample-choir.scoreproj")));

    private static (byte[] Pixels, int Width, int Height, RenderTargetBitmap Bitmap) Render(PageLayout page)
    {
        var view = new PageView(page);
        view.Measure(new Size(view.Width, view.Height));
        view.Arrange(new Rect(0, 0, view.Width, view.Height));

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(view.Width * Dpi / 96), (int)Math.Ceiling(view.Height * Dpi / 96), Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(view);

        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return (pixels, bitmap.PixelWidth, bitmap.PixelHeight, bitmap);
    }

    /// <summary>Counts dark pixels inside a rectangle given in millimetres.</summary>
    private static int DarkPixels(byte[] pixels, int width, int height, double x1, double y1, double x2, double y2)
    {
        var scale = Dpi / 25.4;
        var count = 0;
        for (var y = Math.Max(0, (int)(y1 * scale)); y < Math.Min(height, (int)(y2 * scale)); y++)
        {
            for (var x = Math.Max(0, (int)(x1 * scale)); x < Math.Min(width, (int)(x2 * scale)); x++)
            {
                if (pixels[(y * width + x) * 4] < 128) count++;
            }
        }

        return count;
    }

    private static string PreviewDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("SCORESTUDIO_PREVIEW_DIR") ?? Path.Combine(AppContext.BaseDirectory, "preview");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void SavePng(BitmapSource bitmap, string fileName)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(PreviewDirectory(), fileName));
        encoder.Save(stream);
    }

    [Theory]
    [InlineData("simple-melody")]
    [InlineData("piano-grand-staff")]
    [InlineData("unsupported-features")]
    [InlineData("hymn-repeats-chords")]
    public void ImportedMusicXml_RendersWithItsClefs(string sample)
    {
        RunSta(() =>
        {
            var project = MusicXmlImporter.ImportFile(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml", sample + ".musicxml")).Project;
            var page = ScoreLayoutEngine.Layout(project).Pages[0];
            var (pixels, width, height, bitmap) = Render(page);

            // Same check as for the sample project: the clef area must hold far more ink than staff lines alone.
            var system = page.Systems[0];
            foreach (var staff in system.Staves)
            {
                var clefLeft = system.Left + 1.75;
                var emptyLeft = system.Measures[^1].Left + 5;
                var clefInk = DarkPixels(pixels, width, height, clefLeft, staff.Top - 3, clefLeft + 4.5, staff.Top + 10);
                var emptyInk = DarkPixels(pixels, width, height, emptyLeft, staff.Top - 3, emptyLeft + 4.5, staff.Top + 10);
                Assert.True(clefInk > emptyInk * 2, $"{sample}, staff {staff.StaffIndex + 1}: clef ink {clefInk}, empty staff ink {emptyInk}");
            }

            SavePng(bitmap, $"preview-musicxml-{sample}.png");
        });
    }

    [Fact]
    public void MusicFont_IsBravuraFromTheFontsFolder()
    {
        RunSta(() =>
        {
            Assert.True(File.Exists(Path.Combine(MusicFont.FontDirectory, MusicFont.FileName)));
            Assert.NotNull(MusicFont.Typeface);
        });
    }

    [Fact]
    public void SamplePages_DrawClefsWhereTheLayoutPutsThem_AndArePreviewedAsPng()
    {
        RunSta(() =>
        {
            var layout = SampleLayout();

            foreach (var page in layout.Pages)
            {
                var (pixels, width, height, bitmap) = Render(page);

                // The clef area holds staff lines plus the clef glyph; an equally sized area in an empty
                // measure holds only staff lines. Much more ink in the clef area means the glyph was drawn there.
                foreach (var system in page.Systems)
                {
                    var staff = system.Staves[0];
                    var clefLeft = system.Left + 1.75;
                    var emptyLeft = system.Measures[^1].Left + 5;
                    var clefInk = DarkPixels(pixels, width, height, clefLeft, staff.Top - 3, clefLeft + 4.5, staff.Top + 10);
                    var emptyInk = DarkPixels(pixels, width, height, emptyLeft, staff.Top - 3, emptyLeft + 4.5, staff.Top + 10);
                    Assert.True(clefInk > emptyInk * 2, $"page {page.Number}: clef ink {clefInk}, empty staff ink {emptyInk}");
                }

                SavePng(bitmap, $"preview-page-{page.Number}.png");
            }
        });
    }
}
