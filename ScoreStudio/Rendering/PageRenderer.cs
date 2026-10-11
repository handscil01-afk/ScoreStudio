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

    /// <summary>Lyrics whose source font is a serif one; Times New Roman matches the widths the layout measures with.</summary>
    private static readonly Typeface SerifTextTypeface = new(new FontFamily("Times New Roman, Batang"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static void Render(DrawingContext dc, PageLayout page, double pixelsPerDip)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, page.Width * DipPerMm, page.Height * DipPerMm));
        DrawItems(dc, page.Items, pixelsPerDip, Brushes.Black);
    }

    /// <summary>Draws layout items in one colour (black for the page; grey for the note entry preview).</summary>
    public static void DrawItems(DrawingContext dc, IEnumerable<DrawItem> items, double pixelsPerDip, Brush brush)
    {
        var pens = new Dictionary<double, Pen>();
        var music = MusicFont.Typeface;

        foreach (var item in items)
        {
            switch (item)
            {
                case LineItem line:
                    if (!pens.TryGetValue(line.Thickness, out var pen))
                    {
                        pen = new Pen(brush, line.Thickness * DipPerMm) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
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
                    dc.DrawGeometry(brush, null, band);
                    break;

                case CurveItem curve:
                    var (innerX1, innerY1, innerX2, innerY2) = curve.InnerControls();
                    var crescent = new StreamGeometry();
                    using (var context = crescent.Open())
                    {
                        context.BeginFigure(Mm(curve.X1, curve.Y1), isFilled: true, isClosed: true);
                        context.BezierTo(Mm(curve.ControlX1, curve.ControlY1), Mm(curve.ControlX2, curve.ControlY2), Mm(curve.X2, curve.Y2),
                            isStroked: false, isSmoothJoin: false);
                        context.BezierTo(Mm(innerX2, innerY2), Mm(innerX1, innerY1), Mm(curve.X1, curve.Y1), isStroked: false, isSmoothJoin: false);
                    }

                    crescent.Freeze();
                    dc.DrawGeometry(brush, null, crescent);
                    break;

                case GlyphItem glyph when music is { } musicTypeface:
                    var symbol = Format(glyph.Glyph.ToString(), musicTypeface, glyph.Size, pixelsPerDip, brush);
                    dc.DrawText(symbol, new Point(glyph.X * DipPerMm, glyph.Y * DipPerMm - symbol.Baseline));
                    break;

                case TextItem text:
                    var typeface = text.Serif ? SerifTextTypeface : text.Bold ? BoldTextTypeface : TextTypeface;
                    var formatted = Format(text.Text, typeface, text.Size, pixelsPerDip, brush);
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

    private static Point Mm(double x, double y) => new(x * DipPerMm, y * DipPerMm);

    private static FormattedText Format(string text, Typeface typeface, double sizeMm, double pixelsPerDip, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, sizeMm * DipPerMm, brush, pixelsPerDip);
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

    // Frozen, so pages on any UI thread can share them.
    private static readonly Brush SelectionFill = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x2F, 0x7B, 0xE0)));
    private static readonly Pen SelectionOutline = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x7B, 0xE0))), 1));
    private static readonly Pen CursorPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x7B, 0xE0))), 2));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static readonly Brush PreviewBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0x55, 0x60, 0x70)));

    private ChordBox? _selection;
    private (double X, double Top, double Bottom)? _cursor;
    private IReadOnlyList<DrawItem>? _preview;

    /// <summary>Shows a grey note where a mouse click would enter one (null hides it).</summary>
    public void SetPreview(IReadOnlyList<DrawItem>? preview)
    {
        if (ReferenceEquals(_preview, preview) || (_preview is null && preview is null)) return;
        _preview = preview;
        InvalidateVisual();
    }

    /// <summary>Marks the selected chord (a box) and the input cursor (a bar) on this page, in mm; null hides them.</summary>
    public void SetMarks(ChordBox? selection, (double X, double Top, double Bottom)? cursor)
    {
        if (_selection == selection && _cursor == cursor) return;
        (_selection, _cursor) = (selection, cursor);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        PageRenderer.Render(drawingContext, Page, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        const double k = PageRenderer.DipPerMm;
        if (_selection is { } box)
        {
            var pad = 0.6;
            drawingContext.DrawRectangle(SelectionFill, SelectionOutline,
                new Rect((box.Left - pad) * k, (box.Top - pad) * k, (box.Right - box.Left + 2 * pad) * k, (box.Bottom - box.Top + 2 * pad) * k));
        }

        if (_cursor is { } c)
            drawingContext.DrawLine(CursorPen, new Point(c.X * k, c.Top * k), new Point(c.X * k, c.Bottom * k));

        if (_preview is { } preview)
            PageRenderer.DrawItems(drawingContext, preview, VisualTreeHelper.GetDpi(this).PixelsPerDip, PreviewBrush);
    }
}
