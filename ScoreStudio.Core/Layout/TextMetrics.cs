namespace ScoreStudio.Layout;

/// <summary>Rough text widths without font metrics, so layout stays independent of the renderer.</summary>
public static class TextMetrics
{
    /// <summary>
    /// Width of <paramref name="text"/> in the units of <paramref name="emSize"/>: in the sans-serif text font, or in the
    /// serif lyric font when <paramref name="serif"/> is set.
    /// </summary>
    public static double Width(string? text, double emSize, bool serif = false) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Trim().Sum(c => emSize * (serif ? SerifWidth(c) : SansWidth(c)));

    /// <summary>Hangul syllables and jamo.</summary>
    public static bool IsHangul(char c) => c is >= '\uAC00' and <= '\uD7A3' or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F';

    /// <summary>Families known to be serif (by part of their name); any other family counts as sans-serif.</summary>
    private static readonly string[] SerifFamilies =
    {
        "times", "garamond", "plantin", "bodoni", "georgia", "palatino", "antiqua", "cambria", "century", "minion",
        "baskerville", "caslon", "bookman", "sabon", "bembo", "didot", "goudy", "janson", "constantia", "finale lyrics",
        "edwin", "batang", "바탕", "명조", "myeongjo", "mincho",
    };

    /// <summary>Whether a font family is drawn with the serif lyric font: a known serif family, or one named "serif".</summary>
    public static bool IsSerifFamily(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return false;
        var name = family.ToLowerInvariant();
        return SerifFamilies.Any(name.Contains) || (name.Contains("serif") && !name.Contains("sans"));
    }

    /// <summary>
    /// Hangul and CJK characters are about one em wide; Latin letters and digits are measured roughly as in common
    /// sans-serif fonts (Segoe UI): wide m and w, narrow i, l and punctuation, capitals two thirds, the rest a little over half.
    /// </summary>
    private static double SansWidth(char c) => c switch
    {
        >= '\u1100' and not ('♭' or '♯') => 1.0,
        'M' or 'W' or 'm' or 'w' => 0.88,
        'i' or 'l' or 'j' or 'I' or '.' or ',' or ':' or ';' or '\'' or '|' or '!' => 0.3,
        'f' or 't' or 'r' or '/' or '(' or ')' or '-' or ' ' or '1' => 0.4,
        >= 'A' and <= 'Z' => 0.67,
        _ => 0.58,
    };

    /// <summary>
    /// Advance widths of Times New Roman (checked against the metric-compatible Liberation Serif) for printable ASCII, in thousandths of an em,
    /// starting at the space.
    /// </summary>
    private static readonly int[] TimesAscii =
    {
        250, 333, 408, 500, 500, 833, 778, 180, 333, 333, 500, 564, 250, 333, 250, 278, // space ! " # $ % & ' ( ) * + , - . /
        500, 500, 500, 500, 500, 500, 500, 500, 500, 500, 278, 278, 564, 564, 564, 444, // 0-9 : ; < = > ?
        921, 722, 667, 667, 722, 611, 556, 722, 722, 333, 389, 722, 611, 889, 722, 722, // @ A-O
        556, 722, 667, 556, 611, 722, 722, 944, 722, 722, 611, 333, 278, 333, 469, 500, // P-Z [ \ ] ^ _
        333, 444, 500, 444, 500, 444, 333, 500, 500, 278, 278, 500, 278, 778, 500, 500, // ` a-o
        500, 500, 333, 389, 278, 500, 500, 722, 500, 500, 444, 480, 200, 480, 541,      // p-z { | } ~
    };

    /// <summary>
    /// Serif lyric font: Times widths for ASCII; accented Latin letters as their base letter; Hangul and CJK one em;
    /// anything else a little wider than an average lowercase letter, so unknown characters are not underestimated.
    /// </summary>
    private static double SerifWidth(char c)
    {
        if (c >= ' ' && c <= '~') return TimesAscii[c - ' '] / 1000.0;
        if (c >= '\u1100' && c is not ('♭' or '♯')) return 1.0;
        if (c is '’' or '‘') return 0.333;
        if (c is '“' or '”') return 0.444;
        if (c is 'ß') return 0.5;
        if (c is 'Æ' or 'Œ') return 0.889;
        if (c is 'æ') return 0.667;
        if (c is 'œ') return 0.722;
        if (c is '\u00AD') return 0;
        var stripped = c.ToString().Normalize(System.Text.NormalizationForm.FormD)[0];
        return stripped != c && stripped >= ' ' && stripped <= '~' ? TimesAscii[stripped - ' '] / 1000.0 : 0.6;
    }
}
