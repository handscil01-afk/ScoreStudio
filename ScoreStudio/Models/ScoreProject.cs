namespace ScoreStudio.Models;

/// <summary>
/// Music-domain data should remain independent from screen coordinates.
/// This first model is intentionally small and will grow with tested features.
/// </summary>
public sealed class ScoreProject
{
    public int SchemaVersion { get; set; } = 1;
    public string Title { get; set; } = "새 악보";
    public string Composer { get; set; } = "";
    public string PageSize { get; set; } = "A4";
    public string Orientation { get; set; } = "Portrait";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public string? ImportedFrom { get; set; }
    public List<ScorePart> Parts { get; set; } = new();

    public static ScoreProject CreateNew() => new()
    {
        Title = "새 악보",
        Parts = new List<ScorePart> { new() { Name = "파트 1" } }
    };
}

public sealed class ScorePart
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "파트 1";
    public string Instrument { get; set; } = "Unknown";
    public List<ScoreMeasure> Measures { get; set; } = new();
}

public sealed class ScoreMeasure
{
    public int Number { get; set; } = 1;
    public int Numerator { get; set; } = 4;
    public int Denominator { get; set; } = 4;
    public List<ScoreEvent> Events { get; set; } = new();
}

/// <summary>
/// Duration is stored as a rational number of quarter-note units where practical.
/// MIDI pitch is used as a neutral pitch representation; notation spelling is added separately.
/// </summary>
public sealed class ScoreEvent
{
    public string Kind { get; set; } = "Note";
    public int MidiPitch { get; set; } = 60;
    public int DurationNumerator { get; set; } = 1;
    public int DurationDenominator { get; set; } = 1;
    public int PositionNumerator { get; set; } = 0;
    public int PositionDenominator { get; set; } = 1;
    public string? NotationSpelling { get; set; }
    public string? Lyric { get; set; }
}
