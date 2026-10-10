namespace ScoreStudio.Models;

/// <summary>
/// Music-domain data should remain independent from screen coordinates.
/// This first model is intentionally small and will grow with tested features.
/// </summary>
public sealed class ScoreProject
{
    /// <summary>The project file schema version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
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
        Parts = new List<ScorePart>
        {
            new()
            {
                Name = "파트 1",
                Measures = Enumerable.Range(1, 4).Select(n => new ScoreMeasure { Number = n }).ToList()
            }
        }
    };
}

public sealed class ScorePart
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "파트 1";
    public string Instrument { get; set; } = "Unknown";

    /// <summary>Staves from top to bottom. A piano part has two; most parts have one.</summary>
    public List<ScoreStaff> Staves { get; set; } = new() { new() };

    public List<ScoreMeasure> Measures { get; set; } = new();
}

public sealed class ScoreStaff
{
    public ClefType Clef { get; set; } = ClefType.Treble;
}

/// <summary>Stored in project files by name, so the numeric values may change freely.</summary>
public enum ClefType
{
    Treble,
    Bass,
    Alto,
    Tenor,
    /// <summary>Treble clef sounding an octave lower, used for tenor voices and guitar.</summary>
    Treble8vb,
}

public sealed class ScoreMeasure
{
    public int Number { get; set; } = 1;
    public int Numerator { get; set; } = 4;
    public int Denominator { get; set; } = 4;

    /// <summary>Key signature as a position on the circle of fifths: −7 (seven flats) to 7 (seven sharps).</summary>
    public int KeyFifths { get; set; }

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

    /// <summary>Index into the part's staves, starting at 0.</summary>
    public int Staff { get; set; }

    /// <summary>Voice number within the part, starting at 1.</summary>
    public int Voice { get; set; } = 1;
}
