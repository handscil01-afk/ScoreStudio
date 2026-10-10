namespace ScoreStudio.Layout;

/// <summary>
/// Output of the layout engine: what to draw on a page, in millimetres from the page's top-left corner.
/// Renderers (WPF screen, later PDF) only draw these items and make no layout decisions.
/// </summary>
public abstract record DrawItem;

/// <summary>A straight line. <paramref name="Thickness"/> is in millimetres.</summary>
public sealed record LineItem(double X1, double Y1, double X2, double Y2, double Thickness) : DrawItem;

/// <summary>
/// A beam: a band with vertical ends from (<paramref name="X1"/>, <paramref name="Y1"/>) to (<paramref name="X2"/>,
/// <paramref name="Y2"/>) along its top edge, <paramref name="Thickness"/> millimetres tall. Unlike a thick line, its ends
/// stay vertical when it slopes, so they line up with the stems.
/// </summary>
public sealed record BeamItem(double X1, double Y1, double X2, double Y2, double Thickness) : DrawItem;

/// <summary>
/// A SMuFL glyph from the music font. (<paramref name="X"/>, <paramref name="Y"/>) is the glyph origin on its
/// baseline; <paramref name="Size"/> is the font size (one em) in millimetres, which SMuFL defines as four staff spaces.
/// </summary>
public sealed record GlyphItem(char Glyph, double X, double Y, double Size) : DrawItem;

/// <summary>Plain text such as titles and part names. <paramref name="Y"/> is the text baseline.</summary>
public sealed record TextItem(string Text, double X, double Y, double Size, TextAnchor Anchor, bool Bold = false) : DrawItem;

/// <summary>Which point of the text <see cref="TextItem.X"/> refers to.</summary>
public enum TextAnchor
{
    Start,
    Middle,
    End,
}
