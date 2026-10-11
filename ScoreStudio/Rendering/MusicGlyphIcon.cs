using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ScoreStudio.Rendering;

/// <summary>
/// One music symbol from Bravura (e.g. a quarter note) drawn centred in a fixed box, for toolbar buttons. Without the
/// font it shows <see cref="Fallback"/> text instead.
/// </summary>
public sealed class MusicGlyphIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(MusicGlyphIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineProperty = DependencyProperty.Register(
        nameof(Baseline), typeof(double), typeof(MusicGlyphIcon), new FrameworkPropertyMetadata(0.85, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(MusicGlyphIcon), new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FallbackProperty = DependencyProperty.Register(
        nameof(Fallback), typeof(string), typeof(MusicGlyphIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface FallbackTypeface = new(new FontFamily("Segoe UI, Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public MusicGlyphIcon()
    {
        Width = 20;
        Height = 26;
    }

    /// <summary>The SMuFL character to draw.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Where the glyph's baseline sits, as a fraction of the height: notes stand on it, rests and dots are centred on it.</summary>
    public double Baseline
    {
        get => (double)GetValue(BaselineProperty);
        set => SetValue(BaselineProperty, value);
    }

    /// <summary>Font size in device-independent pixels (one em is four staff spaces).</summary>
    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    /// <summary>Text shown when Bravura is not available.</summary>
    public string Fallback
    {
        get => (string)GetValue(FallbackProperty);
        set => SetValue(FallbackProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var music = MusicFont.Typeface;
        var text = music is null
            ? new FormattedText(Fallback, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, FallbackTypeface, 12, Brushes.Black, pixelsPerDip)
            : new FormattedText(Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, music, GlyphSize, Brushes.Black, pixelsPerDip);
        var x = (ActualWidth - text.WidthIncludingTrailingWhitespace) / 2;
        var y = music is null ? (ActualHeight - text.Height) / 2 : ActualHeight * Baseline - text.Baseline;
        drawingContext.DrawText(text, new Point(x, y));
    }
}
