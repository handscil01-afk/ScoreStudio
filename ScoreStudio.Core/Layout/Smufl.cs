using ScoreStudio.Models;

namespace ScoreStudio.Layout;

/// <summary>
/// SMuFL code points and metrics for the glyphs the layout engine uses.
/// Widths are in staff spaces, taken from Bravura 1.392's metadata (glyphAdvanceWidths and engravingDefaults).
/// </summary>
public static class Smufl
{
    public const char GClef = '';
    public const char GClef8vb = '';
    public const char CClef = '';
    public const char FClef = '';
    public const char Brace = '';
    public const char TimeSig0 = '';

    public const double StaffLineThickness = 0.13;
    public const double ThinBarlineThickness = 0.16;
    public const double ThickBarlineThickness = 0.5;
    public const double BarlineSeparation = 0.4;

    /// <summary>Width of the brace glyph at its natural size (one em tall).</summary>
    public const double BraceAdvanceWidth = 0.336;

    private static readonly double[] TimeSigDigitWidths =
        { 1.88, 1.336, 1.784, 1.684, 1.88, 1.612, 1.736, 1.764, 1.744, 1.736 };

    public static char ClefGlyph(ClefType clef) => clef switch
    {
        ClefType.Treble => GClef,
        ClefType.Treble8vb => GClef8vb,
        ClefType.Bass => FClef,
        ClefType.Alto or ClefType.Tenor => CClef,
        _ => throw new ArgumentOutOfRangeException(nameof(clef), clef, null),
    };

    public static double ClefAdvanceWidth(ClefType clef) => clef switch
    {
        ClefType.Treble => 2.684,
        ClefType.Treble8vb => 2.656,
        ClefType.Bass => 2.736,
        ClefType.Alto or ClefType.Tenor => 2.796,
        _ => throw new ArgumentOutOfRangeException(nameof(clef), clef, null),
    };

    /// <summary>Staff line the clef sits on, counted in staff spaces down from the top line.</summary>
    public static double ClefLine(ClefType clef) => clef switch
    {
        ClefType.Treble or ClefType.Treble8vb => 3, // G line: second from the bottom
        ClefType.Bass => 1,                         // F line: second from the top
        ClefType.Alto => 2,                         // middle line
        ClefType.Tenor => 1,                        // fourth line from the bottom
        _ => throw new ArgumentOutOfRangeException(nameof(clef), clef, null),
    };

    public static string TimeSigDigits(int value) =>
        new(value.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(c => (char)(TimeSig0 + (c - '0'))).ToArray());

    public static double TimeSigWidth(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture).Sum(c => TimeSigDigitWidths[c - '0']);
}
