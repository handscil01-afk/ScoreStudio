using ScoreStudio.Models;

namespace ScoreStudio.Import;

/// <summary>Something in the source file that was not imported or was changed on the way in.</summary>
/// <param name="Message">User-facing Korean description of what was skipped or changed.</param>
/// <param name="Count">How many times it occurred in the file.</param>
/// <param name="FirstLocation">Where it first occurred, e.g. "피아노 3마디".</param>
public sealed record ImportWarning(string Message, int Count, string FirstLocation);

public sealed class ImportSummary
{
    public int Parts { get; init; }
    public int Measures { get; init; }
    public int Notes { get; init; }
    public int Rests { get; init; }
    /// <summary>Notes with at least one lyric syllable.</summary>
    public int Lyrics { get; init; }

    /// <summary>The highest lyric verse number.</summary>
    public int LyricVerses { get; init; }

    public int Ties { get; init; }
    public int ChordSymbols { get; init; }

    /// <summary>What a score holds, counted for the import report.</summary>
    public static ImportSummary Of(ScoreProject project)
    {
        var events = project.Parts.SelectMany(p => p.Measures).SelectMany(m => m.Events).ToList();
        return new ImportSummary
        {
            Parts = project.Parts.Count,
            Measures = project.Parts.Select(p => p.Measures.Count).DefaultIfEmpty(0).Max(),
            Notes = events.Count(e => e.Kind == "Note"),
            Rests = events.Count(e => e.Kind == "Rest"),
            Lyrics = events.Count(e => e.Lyrics.Count > 0),
            LyricVerses = events.SelectMany(e => e.Lyrics).Select(l => l.Verse).DefaultIfEmpty(0).Max(),
            Ties = events.Count(e => e.TieToNext),
            ChordSymbols = project.Parts.SelectMany(p => p.Measures).Sum(m => m.ChordSymbols.Count),
        };
    }
}

public sealed class ImportResult
{
    public required ScoreProject Project { get; init; }
    public required IReadOnlyList<ImportWarning> Warnings { get; init; }
    public required ImportSummary Summary { get; init; }
}

/// <summary>Collects warnings grouped by message, keeping a count and the first location.</summary>
internal sealed class ImportWarningCollector
{
    private readonly Dictionary<string, (int Count, string FirstLocation)> _warnings = new();
    private readonly List<string> _order = new();

    public void Add(string message, string location)
    {
        if (_warnings.TryGetValue(message, out var existing))
        {
            _warnings[message] = (existing.Count + 1, existing.FirstLocation);
            return;
        }

        _warnings[message] = (1, location);
        _order.Add(message);
    }

    public IReadOnlyList<ImportWarning> ToList() =>
        _order.Select(m => new ImportWarning(m, _warnings[m].Count, _warnings[m].FirstLocation)).ToList();
}
