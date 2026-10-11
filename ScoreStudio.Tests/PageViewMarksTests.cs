using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScoreStudio.Editing;
using ScoreStudio.Layout;
using ScoreStudio.Models;
using ScoreStudio.Rendering;

namespace ScoreStudio.Tests;

/// <summary>The mouse entry preview (a grey note) is drawn where MouseEntry puts it, and nowhere once hidden.</summary>
public sealed class PageViewMarksTests
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

    /// <summary>Grey pixels (neither white paper nor black ink) in a box of the rendered page, in mm.</summary>
    private static int GreyPixels(PageView view, double left, double top, double right, double bottom)
    {
        const double scale = 2;
        view.Measure(new Size(view.Width, view.Height));
        view.Arrange(new Rect(0, 0, view.Width, view.Height));
        var (w, h) = ((int)(view.Width * scale), (int)(view.Height * scale));
        var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);

        var k = PageRenderer.DipPerMm * scale;
        var grey = 0;
        for (var y = (int)(top * k); y < (int)(bottom * k); y++)
            for (var x = (int)(left * k); x < (int)(right * k); x++)
            {
                var i = (y * w + x) * 4;
                var value = pixels[i]; // blue channel; the preview is a grey between paper and ink
                if (value is > 40 and < 220 && Math.Abs(pixels[i] - pixels[i + 2]) < 40) grey++;
            }

        return grey;
    }

    [Fact]
    public void MouseEntryPreview_IsDrawnInGrey_AndCanBeHidden()
    {
        RunSta(() =>
        {
            var project = ScoreProject.CreateNew();
            var layout = ScoreLayoutEngine.Layout(project);
            var page = layout.Pages[0];
            var sp = layout.StaffSpace;
            var measure = page.Systems[0].Measures[1];
            var staffTop = page.Systems[0].Staves[0].Top;

            // Middle C, below the staff: a notehead and one leger line.
            var target = MouseEntry.Find(layout, 0, project, NoteType.Quarter, (measure.Left + measure.Right) / 2, staffTop + 5 * sp);
            Assert.NotNull(target);
            Assert.Equal("C4", target.PitchName);
            var head = target.Preview.OfType<GlyphItem>().Single();

            // The box stays clear of the staff's bottom line (one space above the head), whose edges are grey too.
            var (left, top, right, bottom) = (head.X - sp, head.Y - 0.6 * sp, head.X + 2 * sp, head.Y + sp);
            var view = new PageView(page);
            Assert.True(GreyPixels(view, left, top, right, bottom) < 5);
            view.SetPreview(target.Preview);
            Assert.True(GreyPixels(view, left, top, right, bottom) > 30);
            view.SetPreview(null);
            Assert.True(GreyPixels(view, left, top, right, bottom) < 5);
        });
    }
}
