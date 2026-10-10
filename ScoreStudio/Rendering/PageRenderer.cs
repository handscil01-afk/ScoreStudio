using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ScoreStudio.Layout;

namespace ScoreStudio.Rendering;

/// <summary>Draws the layout engine's draw items with WPF. All layout decisions are made in ScoreStudio.Core.</summary>
public static class PageRenderer
{
    /// <summary>WPF device-independent pixels per millimetre (96 per inch).</summary>
    public const double DipPerMm = 96 / 25.4;

    private static readonly Typeface TextTypeface = new(new FontFamily("Segoe UI, Malgun Gothic"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Typeface BoldTextTypeface = new(new FontFamily("Segoe UI, Malgun Gothic"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static void Render(DrawingContext dc, PageLayout page, double pixelsPerDip)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, page.Width * DipPerMm, page.Height * DipPerMm));

        var pens = new Dictionary<double, Pen>();
        var music = MusicFont.Typeface;

        foreach (var item in page.Items)
        {
            switch (item)
            {
                case LineItem line:
                    if (!pens.TryGetValue(line.Thickness, out var pen))
                    {
                        pen = new Pen(Brushes.Black, line.Thickness * DipPerMm) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                        pen.Freeze();
                        pens[line.Thickness] = pen;
                    }

                    dc.DrawLine(pen, new Point(line.X1 * DipPerMm, line.Y1 * DipPerMm), new Point(line.X2 * DipPerMm, line.Y2 * DipPerMm));
                    break;

                case BeamItem beam:
                    var band = new StreamGeometry();
                    using (var context = band.Open())
                    {
                        context.BeginFigure(new Point(beam.X1 * DipPerMm, beam.Y1 * DipPerMm), isFilled: true, isClosed: true);
                        context.PolyLineTo(new[]
                        {
                            new Point(beam.X2 * DipPerMm, beam.Y2 * DipPerMm),
                            new Point(beam.X2 * DipPerMm, (beam.Y2 + beam.Thickness) * DipPerMm),
                            new Point(beam.X1 * DipPerMm, (beam.Y1 + beam.Thickness) * DipPerMm),
                        }, isStroked: false, isSmoothJoin: false);
                    }

                    band.Freeze();
                    dc.DrawGeometry(Brushes.Black, null, band);
                    break;

                case GlyphItem glyph when music is { } musicTypeface:
                    var symbol = Format(glyph.Glyph.ToString(), musicTypeface, glyph.Size, pixelsPerDip);
                    dc.DrawText(symbol, new Point(glyph.X * DipPerMm, glyph.Y * DipPerMm - symbol.Baseline));
                    break;

                case TextItem text:
                    var formatted = Format(text.Text, text.Bold ? BoldTextTypeface : TextTypeface, text.Size, pixelsPerDip);
                    var x = text.X * DipPerMm - text.Anchor switch
                    {
                        TextAnchor.Middle => formatted.WidthIncludingTrailingWhitespace / 2,
                        TextAnchor.End => formatted.WidthIncludingTrailingWhitespace,
                        _ => 0,
                    };
                    dc.DrawText(formatted, new Point(x, text.Y * DipPerMm - formatted.Baseline));
                    break;
            }
        }
    }

    private static FormattedText Format(string text, Typeface typeface, double sizeMm, double pixelsPerDip) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, sizeMm * DipPerMm, Brushes.Black, pixelsPerDip);
}

/// <summary>One page of the score on screen.</summary>
public sealed class PageView : FrameworkElement
{
    public PageView(PageLayout page)
    {
        Page = page;
        Width = page.Width * PageRenderer.DipPerMm;
        Height = page.Height * PageRenderer.DipPerMm;
    }

    public PageLayout Page { get; }

    protected override void OnRender(DrawingContext drawingContext) =>
        PageRenderer.Render(drawingContext, Page, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
