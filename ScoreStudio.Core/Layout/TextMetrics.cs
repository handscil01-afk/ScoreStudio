namespace ScoreStudio.Layout;

/// <summary>Rough text widths without font metrics, so layout stays independent of the renderer.</summary>
public static class TextMetrics
{
    /// <summary>Hangul and CJK characters are about one em wide, capital letters two thirds, other characters about half.</summary>
    public static double Width(string? text, double emSize) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Trim().Sum(c => c switch
        {
            >= '\u1100' and not ('♭' or '♯') => emSize,
            >= 'A' and <= 'Z' => emSize * 0.67,
            _ => emSize * 0.55,
        });
}
