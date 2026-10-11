namespace ScoreStudio.Layout;

/// <summary>Rough text widths without font metrics, so layout stays independent of the renderer.</summary>
public static class TextMetrics
{
    /// <summary>Hangul and CJK characters are about one em wide, other characters about half an em.</summary>
    public static double Width(string? text, double emSize) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Trim().Sum(c => c >= '\u1100' ? emSize : emSize * 0.55);
}
