namespace ScoreStudio.Layout;

/// <summary>Rough text widths without font metrics, so layout stays independent of the renderer.</summary>
public static class TextMetrics
{
    /// <summary>
    /// Hangul and CJK characters are about one em wide; Latin letters and digits are measured roughly as in common
    /// sans-serif fonts (Segoe UI): wide m and w, narrow i, l and punctuation, capitals two thirds, the rest a little over half.
    /// </summary>
    public static double Width(string? text, double emSize) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Trim().Sum(c => emSize * c switch
        {
            >= '\u1100' and not ('♭' or '♯') => 1.0,
            'M' or 'W' or 'm' or 'w' => 0.88,
            'i' or 'l' or 'j' or 'I' or '.' or ',' or ':' or ';' or '\'' or '|' or '!' => 0.3,
            'f' or 't' or 'r' or '/' or '(' or ')' or '-' or ' ' or '1' => 0.4,
            >= 'A' and <= 'Z' => 0.67,
            _ => 0.58,
        });
}
