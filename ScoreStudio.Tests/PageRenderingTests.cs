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

    private static readonly char[] Clefs = { Smufl.GClef, Smufl.GClef8vb, Smufl.FClef, Smufl.CClef };
    private static readonly char[] Noteheads = { Smufl.NoteheadDoubleWhole, Smufl.NoteheadWhole, Smufl.NoteheadHalf, Smufl.NoteheadBlack };

    private static PageLayout Without(PageLayout page, Func<DrawItem, bool> remove)
    {
        var copy = new PageLayout { Number = page.Number, Width = page.Width, Height = page.Height };
        copy.Items.AddRange(page.Items.Where(item => !remove(item)));
        return copy;
    }

    /// <summary>Pixels inside a rectangle (in mm) that are dark in <paramref name="with"/> but light in <paramref name="without"/>.</summary>
    private static int InkAdded(byte[] with, byte[] without, int width, int height, double x1, double y1, double x2, double y2)
    {
        var scale = Dpi / 25.4;
        var count = 0;
        for (var y = Math.Max(0, (int)(y1 * scale)); y < Math.Min(height, (int)(y2 * scale)); y++)
        {
            for (var x = Math.Max(0, (int)(x1 * scale)); x < Math.Min(width, (int)(x2 * scale)); x++)
            {
                var i = (y * width + x) * 4;
                if (with[i] < 128 && without[i] >= 128) count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Renders the page with and without the given glyphs and checks that each glyph really puts ink where the layout
    /// placed it. This catches a missing font, wrong baselines and glyphs drawn off position.
    /// </summary>
    private static void AssertGlyphsDrawn(PageLayout page, char[] glyphs, double left, double up, double right, double down, int minimumInk, string what)
    {
        var (with, width, height, _) = Render(page);
        var (without, _, _, _) = Render(Without(page, item => item is GlyphItem g && glyphs.Contains(g.Glyph)));

        var placed = page.Items.OfType<GlyphItem>().Where(g => glyphs.Contains(g.Glyph)).ToList();
        Assert.NotEmpty(placed);
        var fullSize = placed.Max(g => g.Size);
        foreach (var glyph in placed)
        {
            // Smaller glyphs (clef changes inside a system) put down ink in proportion to their area.
            var ink = InkAdded(with, without, width, height, glyph.X - left, glyph.Y - up, glyph.X + right, glyph.Y + down);
            Assert.True(ink >= minimumInk * Math.Pow(glyph.Size / fullSize, 2), $"page {page.Number}: {what} at ({glyph.X:F1}, {glyph.Y:F1}) mm added only {ink} dark pixels");
        }
    }

    /// <summary>Each tie puts down ink along its length: on average at least half a pixel per pixel of width.</summary>
    private static void AssertCurvesDrawn(PageLayout page)
    {
        var curves = page.Items.OfType<CurveItem>().ToList();
        if (curves.Count == 0) return;

        var (with, width, height, _) = Render(page);
        var (without, _, _, _) = Render(Without(page, item => item is CurveItem));
        var pixelsPerMm = Dpi / 25.4;
        foreach (var curve in curves)
        {
            var ys = new[] { curve.Y1, curve.Y2, curve.ControlY1, curve.ControlY2 };
            var ink = InkAdded(with, without, width, height, curve.X1, ys.Min() - 0.5, curve.X2, ys.Max() + 0.5);
            var length = (curve.X2 - curve.X1) * pixelsPerMm;
            Assert.True(ink >= length / 2, $"page {page.Number}: tie at ({curve.X1:F1}, {curve.Y1:F1}) mm added only {ink} dark pixels over {length:F0}");
        }
    }

    /// <summary>Each beam band fills at least a quarter of its bounding box (a band rising one space fills a third).</summary>
    private static void AssertBeamsDrawn(PageLayout page)
    {
        var beams = page.Items.OfType<BeamItem>().ToList();
        if (beams.Count == 0) return;

        var (with, width, height, _) = Render(page);
        var (without, _, _, _) = Render(Without(page, item => item is BeamItem));
        var pixelsPerMm = Dpi / 25.4;
        foreach (var beam in beams)
        {
            var top = Math.Min(beam.Y1, beam.Y2);
            var bottom = Math.Max(beam.Y1, beam.Y2) + beam.Thickness;
            var ink = InkAdded(with, without, width, height, beam.X1, top, beam.X2, bottom);
            var area = (beam.X2 - beam.X1) * (bottom - top) * pixelsPerMm * pixelsPerMm;
            Assert.True(ink >= area / 4, $"page {page.Number}: beam at ({beam.X1:F1}, {beam.Y1:F1}) mm added only {ink} dark pixels of {area:F0}");
        }
    }

    [Theory]
    [InlineData("simple-melody")]
    [InlineData("piano-grand-staff")]
    [InlineData("unsupported-features")]
    [InlineData("hymn-repeats-chords")]
    [InlineData("layout-features")]
    [InlineData("beams")]
    [InlineData("part-groups")]
    [InlineData("page-layout")]
    [InlineData("lyrics-ties")]
    public void ImportedMusicXml_DrawsClefsAndNoteheadsWhereTheLayoutPutsThem(string sample)
    {
        RunSta(() =>
        {
            var project = MusicXmlImporter.ImportFile(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml", sample + ".musicxml")).Project;
            var page = ScoreLayoutEngine.Layout(project).Pages[0];

            AssertGlyphsDrawn(page, Clefs, 0.5, 8, 5, 6, 100, "clef");
            AssertGlyphsDrawn(page, Noteheads, 0.5, 1.2, 3, 1.2, 15, "notehead");
            AssertBeamsDrawn(page);
            AssertCurvesDrawn(page);
            if (page.Items.OfType<GlyphItem>().Any(g => g.Glyph == Smufl.BracketTop))
            {
                AssertGlyphsDrawn(page, new[] { Smufl.BracketTop }, 0, 2.5, 3.5, 0.3, 10, "bracket top");
                AssertGlyphsDrawn(page, new[] { Smufl.BracketBottom }, 0, 0.3, 3.5, 2.5, 10, "bracket bottom");
            }

            SavePng(Render(page).Bitmap, $"preview-musicxml-{sample}.png");
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
            foreach (var page in SampleLayout().Pages)
            {
                AssertGlyphsDrawn(page, Clefs, 0.5, 8, 5, 6, 100, "clef");
                SavePng(Render(page).Bitmap, $"preview-page-{page.Number}.png");
            }
        });
    }
}
