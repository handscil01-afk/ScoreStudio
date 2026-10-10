using ScoreStudio.Models;

namespace ScoreStudio.Layout;

/// <summary>
/// SMuFL code points and metrics for the glyphs the layout engine uses.
/// Widths are in staff spaces, taken from Bravura 1.392's metadata (glyphAdvanceWidths and engravingDefaults).
/// </summary>
public static class Smufl
{
    public const char GClef = '\uE050';
    public const char GClef8vb = '\uE052';
    public const char CClef = '\uE05C';
    public const char FClef = '\uE062';
    public const char Brace = '\uE000';
    public const char TimeSig0 = '\uE080';

    public const char NoteheadDoubleWhole = '\uE0A0';
    public const char NoteheadWhole = '\uE0A2';
    public const char NoteheadHalf = '\uE0A3';
    public const char NoteheadBlack = '\uE0A4';
    public const char AugmentationDot = '\uE1E7';

    public const char AccidentalFlat = '\uE260';
    public const char AccidentalNatural = '\uE261';
    public const char AccidentalSharp = '\uE262';
    public const char AccidentalDoubleSharp = '\uE263';
    public const char AccidentalDoubleFlat = '\uE264';

    /// <summary>Rests from breve (E4E2) to 64th (E4E9) are consecutive code points.</summary>
    public const char RestDoubleWhole = '\uE4E2';

    /// <summary>Flags for 8th to 64th notes: up flags at E240, E242, …; each down flag follows its up flag.</summary>
    public const char Flag8thUp = '\uE240';

    public const char OttavaAlta = '\uE511';        // 8va
    public const char OttavaBassa = '\uE512';       // 8vb
    public const char QuindicesimaAlta = '\uE515';  // 15ma
    public const char QuindicesimaBassa = '\uE516'; // 15mb

    /// <summary>Clef changes inside a system are drawn smaller than the clefs at the start of a system.</summary>
    public const double ClefChangeScale = 0.75;

    public const double StemThickness = 0.12;
    public const double LegerLineThickness = 0.16;
    public const double LegerLineExtension = 0.4;
    public const double AugmentationDotWidth = 0.4;

    /// <summary>Where stems attach to black and half noteheads (stemUpSE / stemDownNW), in staff spaces.</summary>
    public const double StemAttachY = 0.168;

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

    public static char Notehead(NoteType type) => type switch
    {
        NoteType.Breve => NoteheadDoubleWhole,
        NoteType.Whole => NoteheadWhole,
        NoteType.Half => NoteheadHalf,
        _ => NoteheadBlack,
    };

    public static double NoteheadWidth(NoteType type) => type switch
    {
        NoteType.Breve => 2.396,
        NoteType.Whole => 1.688,
        _ => 1.18,
    };

    public static char Rest(NoteType type) => (char)(RestDoubleWhole + (int)type);

    public static double RestWidth(NoteType type) => type switch
    {
        NoteType.Breve => 0.504,
        NoteType.Whole or NoteType.Half => 1.132,
        NoteType.Quarter => 1.08,
        NoteType.Eighth => 1.0,
        NoteType.Sixteenth => 1.28,
        NoteType.ThirtySecond => 1.452,
        _ => 1.696,
    };

    /// <summary>Flag glyph for an 8th (one flag) to 64th (four flags) note, or null for longer notes.</summary>
    public static char? Flag(NoteType type, bool stemUp) =>
        type >= NoteType.Eighth ? (char)(Flag8thUp + 2 * (type - NoteType.Eighth) + (stemUp ? 0 : 1)) : null;

    /// <summary>
    /// Where the flag's stem anchor (stemUpNW / stemDownSW) sits relative to the flag origin, in staff spaces with y up.
    /// </summary>
    public static double FlagAnchorY(NoteType type, bool stemUp) => (type, stemUp) switch
    {
        (NoteType.Eighth, true) => -0.04,
        (NoteType.Eighth, false) => 0.132,
        (NoteType.Sixteenth, true) => -0.088,
        (NoteType.Sixteenth, false) => 0.128,
        (NoteType.ThirtySecond, true) => 0.376,
        (NoteType.ThirtySecond, false) => -0.448,
        (_, true) => 1.172,
        (_, false) => -1.244,
    };

    public static char Accidental(int alter) => alter switch
    {
        -2 => AccidentalDoubleFlat,
        -1 => AccidentalFlat,
        1 => AccidentalSharp,
        2 => AccidentalDoubleSharp,
        _ => AccidentalNatural,
    };

    public static double AccidentalWidth(int alter) => alter switch
    {
        -2 => 1.652,
        -1 => 0.904,
        1 => 0.996,
        2 => 1.0,
        _ => 0.672,
    };

    /// <summary>Octave-line label for a shift of 1 (8va), 2 (15ma), −1 (8vb) or −2 (15mb).</summary>
    public static char OctaveLineLabel(int shift) => shift switch
    {
        >= 2 => QuindicesimaAlta,
        1 => OttavaAlta,
        -1 => OttavaBassa,
        _ => QuindicesimaBassa,
    };

    public static double OctaveLineLabelWidth(int shift) => shift switch
    {
        >= 2 => 5.26,
        1 => 3.54,
        -1 => 3.356,
        _ => 5.14,
    };
}
