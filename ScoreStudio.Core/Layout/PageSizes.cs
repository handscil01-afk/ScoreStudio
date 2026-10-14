namespace ScoreStudio.Layout;

public static class PageSizes
{
    /// <summary>The project's page in mm: its own size when it has one, else the named size.</summary>
    public static (double Width, double Height) Of(Models.ScoreProject project) =>
        project.Engraving is { PageWidth: { } width, PageHeight: { } height } ? (width, height) : Get(project.PageSize, project.Orientation);

    /// <summary>Page width and height in millimetres. Unknown sizes fall back to A4.</summary>
    public static (double Width, double Height) Get(string? pageSize, string? orientation)
    {
        var (shortSide, longSide) = pageSize?.Trim().ToUpperInvariant() switch
        {
            "LETTER" => (215.9, 279.4),
            _ => (210.0, 297.0),
        };

        return string.Equals(orientation?.Trim(), "Landscape", StringComparison.OrdinalIgnoreCase)
            ? (longSide, shortSide)
            : (shortSide, longSide);
    }
}
